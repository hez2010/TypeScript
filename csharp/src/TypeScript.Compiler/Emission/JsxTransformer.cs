using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

/// <summary>Emits classic JSX factories and automatic runtime imports.</summary>
internal sealed partial class JsxTransformer(EmitContext context, CompilerOptions options, Checker checker, CancellationToken cancellation = default)
    : SyntaxRewriter(context, cancellation)
{
    private SourceFileNode? source;
    private Utf8String importSource;
    private VariableDeclarationNode? fileNameDeclaration;
    private readonly Dictionary<Utf8String, Dictionary<Utf8String, ImportSpecifierNode>> imports = [];
    private readonly HashSet<SyntaxNode> containingJsx = new(ReferenceEqualityComparer.Instance);
    private bool inChild;
    private NodeFactory F => Context.Factory;

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        if (node is SourceFileNode file)
        {
            if (file.IsDeclarationFile || options.Jsx is not (JsxEmit.React or JsxEmit.ReactJSX or JsxEmit.ReactJSXDev)) return file;
            IndexJsx(file);
            if (!containingJsx.Contains(file)) return file;
            return await SourceAsync(file);
        }
        if (!containingJsx.Contains(node)) return node;
        switch (node)
        {
            case JsxElementNode element:
                return await ElementAsync(element.OpeningElement!, element.Children, Location(element));
            case JsxSelfClosingElementNode element:
                return await ElementAsync(element, null, Location(element));
            case JsxFragmentNode fragment:
                return await FragmentAsync(fragment, Location(fragment));
            case JsxTextNode text:
                inChild = false;
                var fixedText = FixWhitespace(text.Text);
                return fixedText.Length == 0 ? null : String(fixedText);
            case JsxExpressionNode expression:
                inChild = false;
                var result = await VisitAsync(expression.Expression);
                return expression.DotDotDotToken is null ? result : F.NewSpreadElement(result);
            case JsxOpeningElementNode or JsxOpeningFragmentNode:
                throw new InvalidOperationException("JSX opening syntax must be visited with its children");
            default:
                inChild = false;
                return await VisitEachChildAsync(node);
        }
    }

    private void IndexJsx(SyntaxNode root)
    {
        containingJsx.Clear();
        Stack<(SyntaxNode Node, bool Visited)> stack = new([(root, false)]);
        while (stack.TryPop(out var entry))
        {
            Cancellation.ThrowIfCancellationRequested();
            if (!entry.Visited)
            {
                stack.Push((entry.Node, true));
                for (int i = entry.Node.ChildCount - 1; i >= 0; i--) stack.Push((entry.Node.GetChild(i), false));
            }
            else
            {
                bool contains = entry.Node is JsxElementNode or JsxSelfClosingElementNode or JsxFragmentNode or JsxTextNode or JsxExpressionNode;
                for (int i = 0; !contains && i < entry.Node.ChildCount; i++) contains = containingJsx.Contains(entry.Node.GetChild(i));
                if (contains) containingJsx.Add(entry.Node);
            }
        }
    }

    private async ValueTask<SyntaxNode> SourceAsync(SourceFileNode file)
    {
        source = file;
        inChild = false;
        importSource = ImplicitImportBase(file, options);
        fileNameDeclaration = null;
        imports.Clear();
        try
        {
            var visited = (SourceFileNode)(await VisitEachChildAsync(file))!;
            foreach (var helper in Context.ReadHelpers()) Context.AddHelper(visited, helper);
            List<SyntaxNode> statements = [.. visited.Statements ?? new([])];
            bool updated = false;
            if (fileNameDeclaration is not null)
            {
                Insert(F.NewVariableStatement(null, F.NewVariableDeclarationList(new([fileNameDeclaration]), NodeFlags.Const)));
                updated = true;
            }
            bool external = file.ExternalModuleIndicator is not null;
            bool commonJs = !external && await checker.IsCommonJsModuleForEmitAsync((SourceFileNode)Context.MostOriginal(file), Cancellation);
            if (external || commonJs)
                foreach (var (module, specifiers) in imports)
                {
                    var sorted = specifiers.OrderBy(pair => pair.Key, Utf8StringComparer.Ordinal).Select(pair => pair.Value).ToArray();
                    SyntaxNode statement = external
                        ? F.NewImportDeclaration(K.ImportDeclaration, null, F.NewImportClause(K.Unknown, null, F.NewNamedImports(new(sorted))), String(module), null)
                        : F.NewVariableStatement(null, F.NewVariableDeclarationList(new([F.NewVariableDeclaration(
                            F.NewBindingPattern(K.ObjectBindingPattern, new(sorted.Select(s => (SyntaxNode)F.NewBindingElement(null, s.PropertyName, s.Name, null)).ToArray())),
                            null, null, F.NewCallExpression(F.NewIdentifier("require"u8), null, null, new([String(module)]), NodeFlags.None))]), NodeFlags.Const));
                    SetSyntheticParents(statement);
                    Insert(statement);
                    updated = true;
                }
            if (!updated) return visited;
            var result = Context.Clone(file);
            result.Statements = new(statements.ToArray());
            return result;

            void Insert(SyntaxNode statement)
            {
                int index = 0;
                while (index < statements.Count && (statements[index] is ExpressionStatementNode { Expression: StringLiteralNode }
                    || (Context.GetFlags(statements[index]) & EmitFlags.CustomPrologue) != 0)) index++;
                statements.Insert(index, statement);
            }
        }
        finally { source = null; importSource = default; fileNameDeclaration = null; imports.Clear(); containingJsx.Clear(); inChild = false; }
    }

    internal static Utf8String ImplicitImportBase(SourceFileNode file, CompilerOptions options)
    {
        Utf8String? Pragma(Utf8String name) => file.Pragmas.LastOrDefault(p => p.Name == name).Arguments?.GetValueOrDefault("factory"u8).Value;
        var runtime = Pragma("jsxruntime"u8);
        if (runtime == "classic"u8) return default;
        var pragmaSource = Pragma("jsximportsource"u8);
        if (options.Jsx is not (JsxEmit.ReactJSX or JsxEmit.ReactJSXDev) && options.JsxImportSource is not { Length: > 0 }
            && pragmaSource is null && runtime != "automatic"u8) return default;
        return pragmaSource is { Length: > 0 } value ? value : options.JsxImportSource is { Length: > 0 } configured ? configured : "react"u8;
    }

    private async ValueTask<IdentifierNode> ImportAsync(Utf8String name)
    {
        var module = name == "createElement"u8 ? importSource : importSource + (options.Jsx == JsxEmit.ReactJSXDev ? "/jsx-dev-runtime"u8 : "/jsx-runtime"u8);
        if (!imports.TryGetValue(module, out var specifiers)) imports.Add(module, specifiers = []);
        if (specifiers.TryGetValue(name, out var existing)) return existing.Name!;
        var generated = Context.NewUniqueName("_"u8 + name, new(GeneratedIdentifierFlags.Optimistic | GeneratedIdentifierFlags.FileLevel | GeneratedIdentifierFlags.AllowNameSubstitution));
        var specifier = F.NewImportSpecifier(false, F.NewIdentifier(name), generated);
        await checker.SetReferencedImportForEmitAsync(generated, specifier, Cancellation);
        specifiers.Add(name, specifier);
        return generated;
    }

    private async ValueTask<SyntaxNode> FactoryAsync(SyntaxNode parent, bool fragment = false)
    {
        var entity = await checker.GetJsxFactoryForEmitAsync(Context.MostOriginal(source!), fragment, Cancellation);
        List<Utf8String> properties = [];
        while (entity is QualifiedNameNode qualified)
        {
            properties.Add(SyntaxNameText.Get(qualified.Right!));
            entity = qualified.Left;
        }
        if (entity is null) properties.Add(fragment ? "Fragment"u8 : "createElement"u8);
        var root = entity is not null ? SyntaxNameText.Get(entity) : options.ReactNamespace is { Length: > 0 } configured ? configured : "React"u8;
        var name = F.NewIdentifier(root);
        name.Flags &= ~NodeFlags.Synthesized;
        name.Parent = Context.ParseNode(parent);
        SyntaxNode result = name;
        if (await checker.GetReferencedExportContainerForEmitAsync(name, false, Cancellation) is ModuleDeclarationNode container)
            result = F.NewPropertyAccessExpression(Context.NewGeneratedNameForNode(container), null, name, NodeFlags.None);
        for (int i = properties.Count - 1; i >= 0; i--)
            result = F.NewPropertyAccessExpression(result, null, F.NewIdentifier(properties[i]), NodeFlags.None);
        return result;
    }

    private SyntaxNode TagName(SyntaxNode node)
    {
        var tag = node is JsxOpeningElementNode opening ? opening.TagName! : ((JsxSelfClosingElementNode)node).TagName!;
        if (tag is JsxNamespacedNameNode namespaced) return String(JsxName(namespaced));
        if (tag is IdentifierNode identifier && (identifier.Text.Length > 0 && identifier.Text[0] is >= (byte)'a' and <= (byte)'z'
            || identifier.Text.Span.Contains((byte)'-'))) return String(identifier.Text);
        return Context.CreateExpressionFromEntityName(tag);
    }

    private EmitRange Location(SyntaxNode node)
    {
        var scanner = new Scanner(source!.Source);
        scanner.ResetPosition(node.Pos);
        scanner.Scan();
        return new(scanner.TokenStart, node.End);
    }

    private SyntaxNode Call(SyntaxNode callee, List<SyntaxNode> arguments, EmitRange location)
    {
        var result = F.NewCallExpression(callee, null, null, new(arguments.ToArray()), NodeFlags.None);
        result.Pos = location.Pos;
        result.End = location.End;
        if (inChild) Context.AddFlags(result, EmitFlags.StartOnNewLine);
        return result;
    }

    private static void SetSyntheticParents(SyntaxNode root)
    {
        foreach (var node in root.DescendantsAndSelf())
            for (int i = 0; i < node.ChildCount; i++) node.GetChild(i).Parent = node;
    }

    private StringLiteralNode String(Utf8String text) => F.NewStringLiteral(text, TokenFlags.None);
    private static Utf8String JsxName(SyntaxNode node) => node is JsxNamespacedNameNode name
        ? Utf8String.Concat(SyntaxNameText.Get(name.Namespace!), ":"u8, SyntaxNameText.Get(name.Name!)) : SyntaxNameText.Get(node);
    private SyntaxNode Keyword(K kind) => F.NewKeywordExpression(kind);
    private PropertyAssignmentNode Property(Utf8String name, SyntaxNode value) => F.NewPropertyAssignment(null, F.NewIdentifier(name), null, null, value);
    private ObjectLiteralExpressionNode Object(IEnumerable<SyntaxNode> properties) => F.NewObjectLiteralExpression(new(properties.ToArray()), false);
}

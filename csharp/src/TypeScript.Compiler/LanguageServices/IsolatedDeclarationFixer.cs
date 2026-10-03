using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.LanguageServices;

internal enum AnnotationMode { Full, Relative, Widened }

internal sealed partial class IsolatedDeclarationFixer(DocumentProjection projection, Checker checker, UserPreferences preferences,
    AnnotationMode mode, CancellationToken cancellation)
{
    private SourceFileNode File => projection.File;
    private readonly SourceEditTracker tracker = new(projection, preferences.FormatCodeSettings, cancellation);
    private NodeFactory F => tracker.Factory;
    private EmitContext Context => tracker.Context;
    private readonly HashSet<SyntaxNode> fixedNodes = [];
    private readonly List<Symbol> imports = [];
    private bool mutatedTarget;

    internal async ValueTask<Utf8String> AnnotateAsync(int start)
    {
        var token = await SyntaxNavigation.GetTokenAtPositionAsync(File, start, cancellation);
        if (await ExpandoFunctionAsync(token) is { } expando)
            return expando is FunctionDeclarationNode function ? await ExpandoNamespaceAsync(function) : await FixAsync(expando);
        var node = Ancestor(token, node => node.Kind is K.GetAccessor or K.MethodDeclaration or K.PropertyDeclaration or K.FunctionDeclaration
            or K.FunctionExpression or K.ArrowFunction or K.VariableDeclaration or K.Parameter or K.ExportAssignment or K.ClassDeclaration
            || BindingPattern(node) && node.Parent is VariableDeclarationNode);
        return node is null ? default : await FixAsync(node);
    }

    private async ValueTask<Utf8String> FixAsync(SyntaxNode node)
    {
        if (!fixedNodes.Add(node)) return default;
        if (node.Kind is K.Parameter or K.PropertyDeclaration or K.VariableDeclaration)
        {
            if (await InferAsync(node) is not { } type) return default;
            if (node is ITypedNode { Type: { } old }) tracker.ReplaceNode(old, type);
            else
            {
                await tracker.InsertTypeAnnotationAsync(node, type);
                if (node is ParameterDeclarationNode { Parent: ArrowFunctionNode arrow }) await tracker.ParenthesizeArrowParametersAsync(arrow);
            }
            return Messages.Add_annotation_of_type_0.Format(preferences.Locale, Print(type));
        }
        if (node.Kind is K.ArrowFunction or K.FunctionExpression or K.FunctionDeclaration or K.MethodDeclaration or K.GetAccessor)
        {
            if (node is ITypedNode { Type: not null } || await InferAsync(node) is not { } type) return default;
            await tracker.InsertTypeAnnotationAsync(node, type);
            return Messages.Add_return_type_0.Format(preferences.Locale, Print(type));
        }
        if (node is ExportAssignmentNode export)
        {
            if (export.IsExportEquals || export.Expression is not { } expression || await InferAsync(expression) is not { } type) return default;
            var name = Context.NewUniqueName("_default"u8);
            var updated = Context.Clone(export); updated.Expression = name;
            tracker.Replace(SmartIndenter.Start(export, File), export.End, [Variable(name, Clone(expression), type), updated]);
            return Messages.Extract_default_export_to_variable.Format(preferences.Locale);
        }
        if (node is ClassDeclarationNode declaration)
        {
            var heritage = declaration.HeritageClauses?.OfType<HeritageClauseNode>().FirstOrDefault(clause => clause.Token == K.ExtendsKeyword)
                ?.Types?.FirstOrDefault() as ExpressionWithTypeArgumentsNode;
            if (heritage?.Expression is not { } expression || await InferAsync(expression) is not { } type) return default;
            var name = Unique(declaration.Name is IdentifierNode identifier ? identifier.Text + "Base"u8 : (Utf8String)"Anonymous"u8);
            tracker.InsertBefore(declaration, Variable(name, Clone(expression), type));
            tracker.ReplaceNode(heritage, F.NewExpressionWithTypeArguments(name, null));
            return Messages.Extract_base_class_to_variable.Format(preferences.Locale);
        }
        return BindingPattern(node) ? await DestructureAsync(node) : default;
    }

    internal async ValueTask<Utf8String> InlineAsync(int start, int end)
    {
        var token = await SyntaxNavigation.GetTokenAtPositionAsync(File, start, cancellation);
        if (await ExpandoFunctionAsync(token) is not null) return default;
        var target = BestFitting(token, end);
        if (target is null || ValueSignature(target) || ValueSignature(target.Parent)) return default;
        bool shorthand = target is ShorthandPropertyAssignmentNode, expression = Expression(target);
        if (!shorthand && NamedDeclaration(target) || Ancestor(target, node => BindingPattern(node) || node is EnumMemberNode) is not null
            || expression && Ancestor(target, node => node is HeritageClauseNode || SemanticSyntax.TypeNode(node)) is not null
            || target is SpreadElementNode || !expression && !shorthand) return default;
        Type? variableType = Ancestor(target, node => node is VariableDeclarationNode) is { } variable
            ? await checker.GetTypeAtLocationAsync(variable, cancellation) : null;
        if (variableType is not null && (variableType.Flags & TypeFlags.UniqueESSymbol) != 0) return default;
        if (await InferAsync(target, variableType) is not { } type || mutatedTarget) return default;
        if (target is ShorthandPropertyAssignmentNode property)
            tracker.Insert(target.End, As(Clone(property.Name!), type), new(Prefix: ": "u8));
        else
        {
            var cloned = Clone(target);
            if (NeedsParens(target)) cloned = F.NewParenthesizedExpression(cloned);
            tracker.ReplaceNode(target, F.NewAsExpression(F.NewSatisfiesExpression(cloned, Clone(type)), type));
        }
        return Messages.Add_satisfies_and_an_inline_type_assertion_with_0.Format(preferences.Locale, Print(type));
    }

    internal async ValueTask<Utf8String> ExtractAsync(int start, int end)
    {
        var target = BestFitting(await SyntaxNavigation.GetTokenAtPositionAsync(File, start, cancellation), end);
        if (target is null || ValueSignature(target) || ValueSignature(target.Parent) || !Expression(target)) return default;
        if (target is ArrayLiteralExpressionNode)
        {
            tracker.ReplaceNode(target, As(Clone(target), ConstType()));
            return Messages.Mark_array_literal_as_const.Format(preferences.Locale);
        }
        if (Ancestor(target, node => node is PropertyAssignmentNode) is not { } property || property == target.Parent && EntityName(target)) return default;
        var text = target is PropertyAccessExpressionNode { Name: IdentifierNode identifier } && TokenFacts.FromText(identifier.Text.Span) == K.Unknown
            ? identifier.Text : (Utf8String)"newLocal"u8;
        var name = Unique(text);
        SyntaxNode replacement = target, initializer = target;
        if (replacement is SpreadElementNode)
        {
            replacement = replacement.Parent!;
            while (replacement.Parent is ParenthesizedExpressionNode parenthesized) replacement = parenthesized;
            if (ConstAssertion(replacement.Parent)) replacement = initializer = replacement.Parent!;
            else initializer = As(Clone(replacement), ConstType());
        }
        if (EntityName(replacement) || Ancestor(target, TypeNodeFlow.Statement) is not { } statement) return default;
        tracker.InsertBefore(statement, Variable(name, Clone(initializer)));
        tracker.ReplaceNode(replacement, F.NewAsExpression(name, F.NewTypeQueryNode(name, null)));
        return Messages.Extract_to_variable_and_replace_with_0_as_typeof_0.Format(preferences.Locale, Print(name));
    }

    private async ValueTask<SyntaxNode?> InferAsync(SyntaxNode node, Type? variableType = null)
    {
        cancellation.ThrowIfCancellationRequested();
        mutatedTarget = false;
        if (mode == AnnotationMode.Relative) return await RelativeAsync(node);
        Dictionary<SyntaxNode, Symbol> symbols = [];
        var type = await checker.InferAnnotationAsync(node, mode == AnnotationMode.Widened, variableType, Context, symbols, cancellation);
        return Rewrite(type, symbols);
    }

    private SyntaxNode? Rewrite(SyntaxNode? type, Dictionary<SyntaxNode, Symbol> symbols)
    {
        if (type is null) return null;
        var rewritten = ImportTypeRewriter.Rewrite(type, symbols, F, cancellation);
        imports.AddRange(rewritten.Imports);
        return rewritten.Node;
    }

    private async ValueTask<SyntaxNode?> ExpandoFunctionAsync(SyntaxNode token)
    {
        var node = token;
        while (!TypeNodeFlow.Statement(node) && node.Kind is not (K.PropertyAccessExpression or K.ElementAccessExpression or K.BinaryExpression))
        {
            if (node.Parent is not { } parent) return null;
            node = parent;
        }
        var target = node is BinaryExpressionNode binary ? binary.Left : node;
        var expression = target switch { PropertyAccessExpressionNode property => property.Expression, ElementAccessExpressionNode element => element.Expression, _ => null };
        if (expression is null) return null;
        var type = await checker.GetTypeAtLocationAsync(expression, cancellation);
        if (!(await checker.PropertiesAsync(type, cancellation)).Any(symbol => symbol.ValueDeclaration == node || symbol.ValueDeclaration == node.Parent)) return null;
        return type.Symbol?.ValueDeclaration switch
        {
            FunctionExpressionNode { Parent: VariableDeclarationNode variable } => variable,
            ArrowFunctionNode { Parent: VariableDeclarationNode variable } => variable,
            FunctionDeclarationNode function => function,
            _ => null,
        };
    }

    private async ValueTask<Utf8String> ExpandoNamespaceAsync(FunctionDeclarationNode function)
    {
        if (function.Name is not IdentifierNode name) return default;
        List<SyntaxNode> properties = [];
        foreach (var symbol in await checker.PropertiesAsync(await checker.GetTypeAtLocationAsync(function, cancellation), cancellation))
        {
            var scanner = new Scanner(new(symbol.Name));
            var token = scanner.Scan();
            if (!(token == K.Identifier || token is >= K.FirstKeyword and <= K.LastKeyword) || scanner.Position != symbol.Name.Length
                || symbol.ValueDeclaration is VariableDeclarationNode) continue;
            Dictionary<SyntaxNode, Symbol> symbols = [];
            var type = await checker.MinimizedAnnotationAsync(await checker.GetTypeOfSymbolAtLocationAsync(symbol, null, cancellation), function,
                Checker.AnnotationFlags, Context, symbols, cancellation);
            if (Rewrite(type, symbols) is { } annotation) properties.Add(Variable(F.NewIdentifier(symbol.Name), null, annotation,
                new([F.NewToken(K.ExportKeyword)]), NodeFlags.None));
        }
        if (properties.Count == 0) return default;
        List<SyntaxNode> modifiers = [];
        if (SemanticSyntax.HasModifier(function, K.ExportKeyword)) modifiers.Add(F.NewToken(K.ExportKeyword));
        modifiers.Add(F.NewToken(K.DeclareKeyword));
        var space = F.NewModuleDeclaration(new([.. modifiers]), K.NamespaceKeyword, F.NewIdentifier(name.Text), null, F.NewModuleBlock(new([.. properties])));
        space.Flags = NodeFlags.Ambient | NodeFlags.ExportContext | NodeFlags.ContextFlags;
        tracker.InsertAfter(function, [space]);
        return Messages.Annotate_types_of_properties_expando_function_in_a_namespace.Format(preferences.Locale);
    }

    internal async ValueTask<DocumentTextEdit[]> ChangesAsync()
    {
        foreach (var symbol in imports)
        {
            if (symbol.Parent is not { } module) continue;
            foreach (var declaration in File.Statements!.OfType<ImportDeclarationNode>())
            {
                if (declaration.ImportClause is not ImportClauseNode clause || declaration.ModuleSpecifier is not { } specifier
                    || await checker.GetSymbolAtLocationAsync(specifier, cancellation) is not { } imported
                    || checker.Symbols.Merger.GetMergedSymbol(imported) != checker.Symbols.Merger.GetMergedSymbol(module)) continue;
                if (clause.NamedBindings is NamedImportsNode bindings)
                {
                    var updatedClause = Context.Clone(clause);
                    updatedClause.NamedBindings = F.NewNamedImports(new([.. bindings.Elements ?? new([]), F.NewImportSpecifier(false, null, F.NewIdentifier(symbol.Name))]));
                    var updated = Context.Clone(declaration); updated.ImportClause = updatedClause;
                    tracker.ReplaceNode(declaration, updated);
                }
                break;
            }
        }
        return await tracker.GetChangesAsync();
    }

    private Utf8String Print(SyntaxNode node)
    {
        var flags = Context.GetFlags(node);
        Context.SetFlags(node, flags | EmitFlags.SingleLine);
        var writer = new EmitTextWriter(inlineDisplay: true);
        try { new SyntaxPrinter(context: Context).Write(node, File, writer, cancellation: cancellation); }
        finally { Context.SetFlags(node, flags); }
        return writer.Text.Length > 160 ? writer.Text[..157] + "..."u8 : writer.Text;
    }

    private SyntaxNode Clone(SyntaxNode node)
    {
        var result = node.DeepClone<SyntaxNode>(F);
        foreach (var child in result.DescendantsAndSelf()) { cancellation.ThrowIfCancellationRequested(); child.Pos = -1; child.End = -1; }
        return result;
    }

    private IdentifierNode Unique(Utf8String text) => Context.NewUniqueName(text, new(GeneratedIdentifierFlags.Optimistic));
    private TypeReferenceNode ConstType() => F.NewTypeReferenceNode(F.NewIdentifier("const"u8), null);
    private TypeQueryNode TypeOf(SyntaxNode node) => F.NewTypeQueryNode(Clone(node), null);
    private SyntaxNode As(SyntaxNode expression, SyntaxNode type) => F.NewAsExpression(NeedsParens(expression) ? F.NewParenthesizedExpression(expression) : expression, type);
    private VariableStatementNode Variable(SyntaxNode name, SyntaxNode? initializer, SyntaxNode? type = null, NodeList? modifiers = null, NodeFlags flags = NodeFlags.Const)
        => F.NewVariableStatement(modifiers, F.NewVariableDeclarationList(new([F.NewVariableDeclaration(name, null, type, initializer)]), flags));
    private static bool NeedsParens(SyntaxNode node) => !EntityName(node) && node is not (CallExpressionNode or ObjectLiteralExpressionNode or ArrayLiteralExpressionNode);
    private static bool EntityName(SyntaxNode node)
    {
        while (node is PropertyAccessExpressionNode { Name: IdentifierNode, Expression: { } expression }) node = expression;
        return node is IdentifierNode;
    }
    private static bool BindingPattern(SyntaxNode node) => node.Kind is K.ObjectBindingPattern or K.ArrayBindingPattern;
    private static bool ConstAssertion(SyntaxNode? node) => node is AsExpressionNode or TypeAssertionNode
        && ((ITypedNode)node).Type is TypeReferenceNode { TypeName: IdentifierNode { Text: var text } } && text == "const"u8;
    private static bool ValueSignature(SyntaxNode? node) => node?.Kind is K.FunctionExpression or K.ArrowFunction or K.MethodDeclaration
        or K.GetAccessor or K.SetAccessor or K.FunctionDeclaration or K.Constructor;
    private static SyntaxNode? Ancestor(SyntaxNode? node, Func<SyntaxNode, bool> predicate)
    {
        for (; node is not null; node = node.Parent) if (predicate(node)) return node;
        return null;
    }

    private static SyntaxNode? BestFitting(SyntaxNode? node, int end)
    {
        while (node is not null && node.End < end) node = node.Parent;
        if (node is null) return null;
        while (node.Parent is { } parent && parent.Pos == node.Pos && parent.End == node.End) node = parent;
        if (node is IdentifierNode)
        {
            if (node.Parent is IInitializedNode { Initializer: { } initializer }) return initializer;
            if (node.Parent is ShorthandPropertyAssignmentNode) return node.Parent;
        }
        return node;
    }

    private static bool NamedDeclaration(SyntaxNode node) => node.Kind is K.ArrowFunction or K.BindingElement or K.ClassDeclaration or K.ClassExpression
        or K.ClassStaticBlockDeclaration or K.Constructor or K.EnumDeclaration or K.EnumMember or K.ExportSpecifier or K.FunctionDeclaration
        or K.FunctionExpression or K.GetAccessor or K.ImportClause or K.ImportEqualsDeclaration or K.ImportSpecifier or K.InterfaceDeclaration
        or K.JsxAttribute or K.MethodDeclaration or K.MethodSignature or K.ModuleDeclaration or K.NamespaceExportDeclaration or K.NamespaceImport
        or K.NamespaceExport or K.Parameter or K.PropertyAssignment or K.PropertyDeclaration or K.PropertySignature or K.SetAccessor
        or K.ShorthandPropertyAssignment or K.TypeAliasDeclaration or K.TypeParameter or K.VariableDeclaration or K.JSDocTypedefTag
        or K.JSDocCallbackTag or K.JSDocPropertyTag or K.NamedTupleMember;

    private static bool Expression(SyntaxNode node) => node.Kind is K.ConditionalExpression or K.YieldExpression or K.ArrowFunction or K.BinaryExpression
        or K.SpreadElement or K.AsExpression or K.OmittedExpression or K.PartiallyEmittedExpression or K.SatisfiesExpression or K.PrefixUnaryExpression
        or K.PostfixUnaryExpression or K.DeleteExpression or K.TypeOfExpression or K.VoidExpression or K.AwaitExpression or K.TypeAssertionExpression
        or K.PropertyAccessExpression or K.ElementAccessExpression or K.NewExpression or K.CallExpression or K.JsxElement or K.JsxSelfClosingElement
        or K.JsxFragment or K.TaggedTemplateExpression or K.ArrayLiteralExpression or K.ParenthesizedExpression or K.ObjectLiteralExpression
        or K.ClassExpression or K.FunctionExpression or K.Identifier or K.PrivateIdentifier or K.RegularExpressionLiteral or K.NumericLiteral
        or K.BigIntLiteral or K.StringLiteral or K.NoSubstitutionTemplateLiteral or K.TemplateExpression or K.FalseKeyword or K.NullKeyword
        or K.ThisKeyword or K.TrueKeyword or K.SuperKeyword or K.NonNullExpression or K.ExpressionWithTypeArguments or K.MetaProperty
        or K.ImportKeyword or K.MissingDeclaration;
}

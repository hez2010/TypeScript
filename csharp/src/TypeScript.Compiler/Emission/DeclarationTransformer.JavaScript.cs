using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class DeclarationTransformer
{
    private SyntaxNode? cjsAssignment;
    private IdentifierNode? cjsAssignmentName;
    private readonly List<SyntaxNode> cjsExports = [];
    private readonly HashSet<Utf8String> witnessedExports = [];

    private async ValueTask CollectAssignmentsAsync(SourceFileNode file)
    {
        bool commonJs = checker.Symbols.Binding(file)?.CommonJSModuleIndicator is not null;
        if (commonJs)
            foreach (var node in file.DescendantsAndSelf())
            {
                Cancellation.ThrowIfCancellationRequested();
                if (node is not BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken, Left: { } left, Right: { } right }
                    || !ModuleExports(left) || IsExports(right)) continue;
                var saved = SaveContext();
                try
                {
                    SetDiagnosticContext(node);
                    if (await ExportAssignmentAsync(node.Parent!, node, right, true) is { } result)
                    { cjsAssignment = result; hasScopeMarker = hasModuleIndicator = true; }
                }
                finally { RestoreContext(saved); }
            }
        foreach (var node in file.DescendantsAndSelf())
        {
            Cancellation.ThrowIfCancellationRequested();
            var saved = SaveContext();
            try
            {
                SetDiagnosticContext(node);
                if (node is BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken, Left: { } left })
                {
                    if (commonJs && ExportsBase(AccessBase(left)) && AccessName(left) is { } name)
                    {
                        if (await CommonJsExportAsync(node, PreferIdentifier(name)) is { } result) cjsExports.Add(result);
                    }
                    else if (!ModuleExports(left) && AccessBase(left)?.Kind != K.ThisKeyword) await ExpandoAssignmentAsync((BinaryExpressionNode)node);
                }
                else if (commonJs && node is CallExpressionNode { Expression: PropertyAccessExpressionNode { Expression: IdentifierNode { Text: var owner }, Name: IdentifierNode { Text: var method } }, Arguments: { Count: 3 } arguments }
                    && owner == "Object"u8 && method == "defineProperty"u8 && ExportsBase(arguments[0]) && LiteralName(arguments[1]))
                {
                    if (await CommonJsExportAsync(node, PreferIdentifier(arguments[1])) is { } result) cjsExports.Add(result);
                }
            }
            finally { RestoreContext(saved); }
        }
    }

    private static SyntaxNode? AccessBase(SyntaxNode? node) => node switch
    {
        PropertyAccessExpressionNode property => property.Expression,
        ElementAccessExpressionNode element => element.Expression,
        _ => null
    };
    private static SyntaxNode? AccessName(SyntaxNode node) => node switch
    {
        PropertyAccessExpressionNode property => property.Name,
        ElementAccessExpressionNode { ArgumentExpression: { } argument } => Unwrap(argument),
        _ => null
    };
    private static bool IsExports(SyntaxNode? node) => node is IdentifierNode { Text: var text } && text == "exports"u8;
    private static Utf8String PropertyText(SyntaxNode? node) => node switch
    {
        StringLiteralNode literal => literal.Text,
        NumericLiteralNode literal => literal.Text,
        NoSubstitutionTemplateLiteralNode literal => literal.Text,
        _ => SyntaxNameText.Get(node)
    };
    private static bool ExportsBase(SyntaxNode? node) => IsExports(node) || ModuleExports(node);
    private static bool ModuleExports(SyntaxNode? node) => AccessBase(node) is IdentifierNode { Text: var text } && text == "module"u8
        && PropertyText(AccessName(node!)!) == "exports"u8;
    private static bool IdentifierText(Utf8String text)
    {
        if (text.Length == 0) return false;
        for (int i = 0; i < text.Length;)
        {
            int point = Wtf8.Decode(text.Span[i..], out int width);
            if (i == 0 ? !TokenFacts.IsIdentifierStart(point) : !TokenFacts.IsIdentifierPart(point)) return false;
            i += width;
        }
        return true;
    }
    private SyntaxNode PreferIdentifier(SyntaxNode node)
    {
        if (node is NumericLiteralNode numeric) node = F.NewStringLiteral(numeric.Text, TokenFlags.None);
        if (node is StringLiteralNode or NoSubstitutionTemplateLiteralNode)
        {
            var name = PropertyText(node);
            if (IdentifierText(name) && TokenFacts.FromText(name.Span) is K.Unknown or K.DefaultKeyword)
            {
                var result = F.NewIdentifier(name);
                result.Parent = node.Parent;
                result.Flags &= ~NodeFlags.Synthesized;
                return result;
            }
        }
        return node;
    }
    private static bool RequireVariable(SyntaxNode node) => node is VariableDeclarationNode { Initializer: CallExpressionNode
        { Expression: IdentifierNode { Text: var text }, Arguments: { Count: 1 } arguments } } && text == "require"u8 && LiteralName(arguments[0]);
    private SyntaxNode? RequireImport(VariableDeclarationNode node)
    {
        var specifier = ((CallExpressionNode)node.Initializer!).Arguments![0];
        hasModuleIndicator = true;
        if (node.Name is IdentifierNode name) return F.NewImportEqualsDeclaration(null, false, name, F.NewExternalModuleReference(specifier));
        if (node.Name is not BindingPatternNode { Kind: K.ObjectBindingPattern } pattern) return null;
        var specifiers = (pattern.Elements ?? new([])).OfType<BindingElementNode>().Where(binding => binding.Name is IdentifierNode)
            .Select(binding => (SyntaxNode)F.NewImportSpecifier(false, binding.PropertyName, (IdentifierNode)binding.Name!)).ToArray();
        return F.NewImportDeclaration(K.ImportDeclaration, null, F.NewImportClause(K.Unknown, null, F.NewNamedImports(new(specifiers))), specifier, null);
    }

    private async ValueTask<SyntaxNode?> CommonJsExportAsync(SyntaxNode node, SyntaxNode name)
    {
        var result = await CommonJsExportWorkerAsync(node, name);
        if (result is null || cjsAssignmentName is null) return result;
        return F.NewModuleDeclaration(needsDeclare ? new([F.NewToken(K.DeclareKeyword)]) : null, K.NamespaceKeyword, cjsAssignmentName, null,
            F.NewModuleBlock(new(Parts(result).Select(StripDeclare).ToArray())));
    }

    private SyntaxNode StripDeclare(SyntaxNode node)
    {
        if (node is not IModifiedNode { Modifiers: { } modifiers } || !modifiers.Any(modifier => modifier.Kind == K.DeclareKeyword)) return node;
        var result = Context.Clone(node);
        ((IModifiedNode)result).Modifiers = new(modifiers.Where(modifier => modifier.Kind != K.DeclareKeyword).ToArray());
        return result;
    }

    private async ValueTask<SyntaxNode?> CommonJsExportWorkerAsync(SyntaxNode node, SyntaxNode name)
    {
        var text = name is IdentifierNode or StringLiteralNode ? PropertyText(name) : Utf8String.Empty;
        if (text.Length != 0 && !witnessedExports.Add(text)) return null;
        hasModuleIndicator = hasScopeMarker = true;
        if (node is BinaryExpressionNode { Right: IdentifierNode } binary && checker.Symbols.Declaration(node)?.Declarations.Length == 1
            && node.Parent is ExpressionStatementNode { Parent: SourceFileNode }) return await AliasExportAsync(binary, name);
        if (node is BinaryExpressionNode { Right: { } right } && Unwrap(right) is ClassExpressionNode type)
            return await CommonJsClassAsync(type, node, name);
        IdentifierNode local;
        bool inline = false;
        if (name is IdentifierNode identifier && identifier.Text != "default"u8)
        {
            var referenced = await checker.GetReferencedValueForEmitAsync(identifier, Cancellation);
            inline = referenced is null || referenced == node;
        }
        if (inline) local = (IdentifierNode)name;
        else local = Context.NewUniqueName(text == "default"u8 ? "_default"u8 : "_exported"u8, new(GeneratedIdentifierFlags.Optimistic));
        if (!inline) tracker.DiagnosticSelector = _ => new(node, Messages.Default_export_of_the_module_has_or_is_using_private_name_0);
        tracker.PushErrorFallbackNode(node);
        SyntaxNode? inferredType;
        try { inferredType = await EnsureTypeAsync(node); }
        finally { tracker.PopErrorFallbackNode(); }
        var modifiers = FromModifierFlags((needsDeclare ? ModifierFlags.Ambient : 0) | (inline ? ModifierFlags.Export : 0));
        var statement = F.NewVariableStatement(modifiers, F.NewVariableDeclarationList(new([F.NewVariableDeclaration(local, null, inferredType, null)]), inline ? NodeFlags.None : NodeFlags.Const));
        if (inline) return statement;
        Located(statement, node);
        var export = text == "default"u8 ? (SyntaxNode)F.NewExportAssignment(node.ModifierList, false, null, local) : NamedExport(local, name);
        Context.AddFlags(export, EmitFlags.NoComments);
        return F.NewSyntaxList([statement, export]);
    }

    private ExportDeclarationNode NamedExport(SyntaxNode local, SyntaxNode name) => F.NewExportDeclaration(null, false,
        F.NewNamedExports(new([F.NewExportSpecifier(false, local, name)])), null, null);

    private async ValueTask<SyntaxNode> AliasExportAsync(BinaryExpressionNode node, SyntaxNode name)
    {
        await CheckEntityAsync(node.Right!);
        return F.NewExportDeclaration(null, false, F.NewNamedExports(new([
            F.NewExportSpecifier(false, name is IdentifierNode && PropertyText(node.Right!) == PropertyText(name) ? null : node.Right, name)])), null, null);
    }

    private async ValueTask<SyntaxNode> CommonJsClassAsync(ClassExpressionNode node, SyntaxNode assignment, SyntaxNode exportName)
    {
        if (node.Name is { Text.Length: > 0 } originalName)
        {
            tracker.WatchedClassSymbol = checker.Symbols.Declaration(node);
            tracker.ClassSymbolTracked = false;
            try
            {
                var name = F.NewIdentifier(originalName.Text);
                var declaration = (ClassDeclarationNode)Located(await ClassExpressionAsync(node, name, new([F.NewToken(K.ExportKeyword)])), assignment);
                if (exportName is not IdentifierNode identifier || identifier.Text != originalName.Text || tracker.ClassSymbolTracked)
                {
                    var spaceName = Context.NewUniqueName("_ns"u8, new(GeneratedIdentifierFlags.Optimistic));
                    var space = F.NewModuleDeclaration(needsDeclare ? new([F.NewToken(K.DeclareKeyword)]) : null, K.NamespaceKeyword, spaceName, null,
                        F.NewModuleBlock(new([declaration])));
                    Utf8String baseName = exportName is IdentifierNode ? Utf8String.Concat("_"u8, PropertyText(exportName)) : "_exported"u8;
                    var alias = Context.NewUniqueName(baseName, new(GeneratedIdentifierFlags.Optimistic));
                    var import = F.NewImportEqualsDeclaration(null, false, alias, F.NewQualifiedName(spaceName, name));
                    var export = NamedExport(alias, exportName);
                    Context.AddFlags(export, EmitFlags.NoComments);
                    return F.NewSyntaxList([space, import, export]);
                }
                declaration.Modifiers = FromModifierFlags(ModifierFlags.Export | (needsDeclare ? ModifierFlags.Ambient : 0));
                return declaration;
            }
            finally { tracker.WatchedClassSymbol = null; tracker.ClassSymbolTracked = false; }
        }
        var className = exportName as IdentifierNode ?? Context.NewUniqueName("_class"u8, new(GeneratedIdentifierFlags.Optimistic));
        var result = Located(await ClassExpressionAsync(node, className, FromModifierFlags(ModifierFlags.Export | (needsDeclare ? ModifierFlags.Ambient : 0))), assignment);
        if (exportName is IdentifierNode) return result;
        var exported = NamedExport(className, exportName);
        Context.AddFlags(exported, EmitFlags.NoComments);
        return F.NewSyntaxList([result, exported]);
    }

    private async ValueTask<IReadOnlyList<SyntaxNode>> ThisPropertiesAsync(SyntaxNode type, NodeList? members)
    {
        var seen = new HashSet<(Utf8String Name, SyntaxNode? Node, bool Static, bool Private)>();
        foreach (var member in members ?? new([]))
            if (SemanticSyntax.Name(member) is { } name) seen.Add(PropertyKey(name, member, SemanticSyntax.HasModifier(member, K.StaticKeyword)));
        List<SyntaxNode> properties = [];
        foreach (var member in members ?? new([]))
            foreach (var node in member.DescendantsAndSelf())
            {
                Cancellation.ThrowIfCancellationRequested();
                if (node is not BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken, Left: { } left } || AccessBase(left)?.Kind != K.ThisKeyword) continue;
                var container = node.Parent;
                while (container is not null && container is not ClassStaticBlockDeclarationNode && (!SemanticSyntax.FunctionDeclarationLike(container) || container is ArrowFunctionNode)) container = container.Parent;
                if (container?.Parent != type || AccessName(left) is not { } name) continue;
                bool isStatic = container is ClassStaticBlockDeclarationNode || SemanticSyntax.HasModifier(container, K.StaticKeyword);
                if (await checker.GetReferencedMemberForEmitAsync(node, Cancellation) is null || !seen.Add(PropertyKey(name, node, isStatic))) continue;
                var heritage = type is ClassDeclarationNode declaration ? declaration.HeritageClauses : ((ClassExpressionNode)type).HeritageClauses;
                if (heritage is { Count: > 0 } && !heritage.OfType<HeritageClauseNode>().Any(clause => clause.Token == K.ExtendsKeyword
                    && clause.Types is { Count: 1 } && clause.Types[0] is ExpressionWithTypeArgumentsNode { Expression.Kind: K.NullKeyword }))
                {
                    tracker.ReportInferenceFallback(type);
                    if (await checker.IsThisPropertyAssignmentRedundantForEmitAsync(node, Cancellation)) continue;
                }
                if (left is ElementAccessExpressionNode && !LiteralName(name) && name is not PrefixUnaryExpressionNode { Operator: K.PlusToken or K.MinusToken, Operand: NumericLiteralNode })
                {
                    if (name is IdentifierNode || !Primitive(name) && name.Kind is not (>= K.FirstKeyword and <= K.LastKeyword)) continue;
                    await CheckNameAsync(node, name);
                    name = F.NewComputedPropertyName(name);
                }
                if (PropertyText(name) == "constructor"u8) continue;
                if (name is PrefixUnaryExpressionNode) name = F.NewComputedPropertyName(name);
                if (name is IdentifierNode identifier && !IdentifierText(identifier.Text)) name = F.NewStringLiteral(identifier.Text, TokenFlags.None);
                var property = F.NewPropertyDeclaration(isStatic ? new([F.NewToken(K.StaticKeyword)]) : null, name, null, await EnsureTypeAsync(node), null);
                if (node.Parent is ExpressionStatementNode statement) Located(property, statement);
                properties.Add(property);
            }
        return properties;
    }

    private static (Utf8String Name, SyntaxNode? Node, bool Static, bool Private) PropertyKey(SyntaxNode name, SyntaxNode node, bool isStatic)
    {
        var value = name is ComputedPropertyNameNode computed ? computed.Expression : name;
        bool known = name is IdentifierNode or PrivateIdentifierNode || value is StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode;
        if (value is PrefixUnaryExpressionNode { Operator: K.PlusToken or K.MinusToken, Operand: NumericLiteralNode operand } signed)
            return (signed.Operator == K.MinusToken && operand.Text != "0"u8 ? "-"u8 + operand.Text : operand.Text, null, isStatic, false);
        return known ? (PropertyText(value!), null, isStatic, name is PrivateIdentifierNode) : (Utf8String.Empty, node, isStatic, name is PrivateIdentifierNode);
    }
}

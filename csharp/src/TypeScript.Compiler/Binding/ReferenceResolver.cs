using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using static TypeScript.Compiler.Binding.SemanticSyntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.Binding;

public sealed class ReferenceResolverHooks
{
    public ResolveName? ResolveName { get; init; }
    public Func<SyntaxNode, Symbol?>? GetResolvedSymbol { get; init; }
    public Func<Symbol, Symbol?>? GetMergedSymbol { get; init; }
    public Func<Symbol, Symbol?>? GetParentOfSymbol { get; init; }
    public Func<SyntaxNode, Symbol?>? GetSymbolOfDeclaration { get; init; }
    public Func<Symbol, S, SyntaxNode?>? GetTypeOnlyAliasDeclaration { get; init; }
    public Func<Symbol, Symbol?>? GetExportSymbolOfValueSymbolIfExported { get; init; }
    public Func<ElementAccessExpressionNode, TextSlice?>? GetElementAccessExpressionName { get; init; }
}

/// <summary>Shared declaration/export queries for checking and emit.</summary>
public sealed class ReferenceResolver(CompilerOptions options, Func<SyntaxNode, BoundSourceFile?> getBinding, ReferenceResolverHooks hooks)
{
    private NameResolver? resolver;

    private Symbol? BoundSymbol(SyntaxNode? node) => node is null ? null : getBinding(node)?.Get(node)?.Symbol;

    private Symbol? ResolvedSymbol(SyntaxNode? node) => node is null ? null : hooks.GetResolvedSymbol?.Invoke(node);

    private Symbol? Merged(Symbol? symbol) => symbol is null ? null : hooks.GetMergedSymbol is { } get ? get(symbol) : symbol;

    private Symbol? ParentSymbol(Symbol? symbol) =>
        symbol is null ? null : hooks.GetParentOfSymbol is { } get ? get(symbol) : symbol.Parent;

    private Symbol? DeclarationSymbol(SyntaxNode node) => hooks.GetSymbolOfDeclaration is { } get ? get(node) : BoundSymbol(node);

    private Symbol? ValueSymbol(IdentifierNode reference, bool startInContainer)
    {
        if (ResolvedSymbol(reference) is { } resolved)
            return resolved;
        SyntaxNode? location = reference;
        if (startInContainer && reference.Parent is { } parent && Name(parent) == reference)
            location = DeclarationContainer(parent);
        const S meaning = S.ExportValue | S.Value | S.Alias;
        if (hooks.ResolveName is { } resolve)
            return resolve(location, reference.Text, meaning, null, false, false);
        resolver ??= new(options, getBinding);
        return resolver.Resolve(location, reference.Text, meaning);
    }

    private Symbol? Exported(Symbol? symbol)
    {
        if (symbol is null)
            return null;
        if (hooks.GetExportSymbolOfValueSymbolIfExported is { } get)
            return get(symbol);
        if ((symbol.Flags & S.ExportValue) != 0 && symbol.ExportSymbol is not null)
            symbol = symbol.ExportSymbol;
        return Merged(symbol);
    }

    private bool TypeOnlyAlias(Symbol symbol)
    {
        if (hooks.GetTypeOnlyAliasDeclaration is { } get)
            return get(symbol, S.Value) is not null;
        var node = AliasDeclaration(symbol);
        while (node is not null)
        {
            switch (node.Kind)
            {
                case K.ImportEqualsDeclaration:
                case K.ExportDeclaration:
                    return TypeOnly(node);
                case K.ImportClause:
                case K.ImportSpecifier:
                case K.ExportSpecifier:
                    if (TypeOnly(node))
                        return true;
                    node = node.Parent;
                    continue;
                case K.NamedImports:
                case K.NamedExports:
                    node = node.Parent;
                    continue;
            }
            break;
        }
        return false;
    }

    private static SyntaxNode? AliasDeclaration(Symbol symbol) => symbol.Declarations.LastOrDefault(IsAliasDeclaration);

    public SyntaxNode? GetReferencedExportContainer(IdentifierNode node, bool prefixLocals)
    {
        bool startInContainer = node.Parent?.Kind is K.ModuleDeclaration or K.EnumDeclaration && Name(node.Parent) == node;
        var symbol = ValueSymbol(node, startInContainer);
        if (symbol is null)
            return null;
        if ((symbol.Flags & S.ExportValue) != 0)
        {
            var exported = Merged(symbol.ExportSymbol) ?? throw new InvalidOperationException("Export value has no export symbol");
            if (!prefixLocals && (exported.Flags & S.ExportHasLocal) != 0 && (exported.Flags & S.Variable) == 0)
                return null;
            symbol = exported;
        }
        var parent = ParentSymbol(symbol);
        if (parent is null)
            return null;
        if ((parent.Flags & S.ValueModule) != 0 && parent.ValueDeclaration is SourceFileNode file)
            return file == Source(node) ? file : null;
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Kind is K.ModuleDeclaration or K.EnumDeclaration && DeclarationSymbol(ancestor) == parent)
                return ancestor;
        return null;
    }

    public SyntaxNode? GetReferencedImportDeclaration(IdentifierNode node)
    {
        var symbol = ValueSymbol(node, false);
        return symbol is not null && ((symbol.Flags & (S.Alias | S.Value)) == S.Alias
            || (symbol.Flags & (S.Alias | S.Assignment)) == (S.Alias | S.Assignment)) && !TypeOnlyAlias(symbol)
            ? AliasDeclaration(symbol) : null;
    }

    public SyntaxNode? GetReferencedValueDeclaration(IdentifierNode node) => Exported(ValueSymbol(node, false))?.ValueDeclaration;

    public IReadOnlyList<SyntaxNode> GetReferencedValueDeclarations(IdentifierNode node)
    {
        var symbol = Exported(ValueSymbol(node, false));
        return symbol?.Declarations.Where(n => n.Kind is K.VariableDeclaration or K.Parameter or K.BindingElement
            or K.PropertyDeclaration or K.PropertyAssignment or K.ShorthandPropertyAssignment or K.EnumMember or K.ObjectLiteralExpression
            or K.FunctionDeclaration or K.FunctionExpression or K.ArrowFunction or K.ClassDeclaration or K.ClassExpression or K.EnumDeclaration
            or K.MethodDeclaration or K.GetAccessor or K.SetAccessor or K.ModuleDeclaration).ToArray() ?? [];
    }

    public TextSlice GetElementAccessExpressionName(ElementAccessExpressionNode? node)
        => node is null ? "" : hooks.GetElementAccessExpressionName?.Invoke(node) ?? "";

    public SyntaxNode? GetReferencedMemberValueDeclaration(SyntaxNode node)
        => Exported(ResolvedSymbol(node) ?? Merged(BoundSymbol(node)))?.ValueDeclaration;

    internal static bool IsAliasDeclaration(SyntaxNode node)
    {
        switch (node.Kind)
        {
            case K.ImportEqualsDeclaration:
            case K.NamespaceExportDeclaration:
            case K.NamespaceImport:
            case K.NamespaceExport:
            case K.ImportSpecifier:
            case K.ExportSpecifier:
                return true;
            case K.ImportClause:
                return Name(node) is not null;
            case K.ExportAssignment:
                return AliasExpression(((ExportAssignmentNode)node).Expression);
            case K.BindingElement:
                node = node.Parent?.Parent ?? throw new InvalidOperationException("Binding element has no declaration");
                goto case K.VariableDeclaration;
            case K.VariableDeclaration:
                return (node.Flags & NodeFlags.JavaScriptFile) != 0
                    && node is VariableDeclarationNode { Type: null, Initializer: CallExpressionNode call }
                    && node.Parent?.Parent is { } statement && !HasModifier(statement, K.ExportKeyword)
                    && RequireCall(call) && call.Arguments![0].Kind is K.StringLiteral or K.NoSubstitutionTemplateLiteral;
            case K.BinaryExpression:
                var assignment = (BinaryExpressionNode)node;
                return assignment.OperatorToken?.Kind == K.EqualsToken && assignment.Left is { } left
                    && (left.Flags & NodeFlags.JavaScriptFile) != 0 && AliasExpression(assignment.Right)
                    && (ModuleExports(left) && assignment.Right is not IdentifierNode { Text.Span: "exports" }
                        || (ModuleExports(AccessBase(left)) || AccessBase(left) is IdentifierNode { Text.Span: "exports" })
                            && AccessName(left) is not null);
        }
        return false;
    }

    private static bool AliasExpression(SyntaxNode? node)
    {
        if (node is ClassExpressionNode)
            return true;
        while (node is PropertyAccessExpressionNode { Name: IdentifierNode } access)
            node = access.Expression;
        return node is IdentifierNode;
    }

    private static SyntaxNode? AccessBase(SyntaxNode? node) => node switch
    {
        PropertyAccessExpressionNode n => n.Expression,
        ElementAccessExpressionNode n => n.Expression,
        _ => null
    };

    private static TextSlice? AccessName(SyntaxNode? node) => node switch
    {
        PropertyAccessExpressionNode { Name: IdentifierNode name } => name.Text,
        ElementAccessExpressionNode { ArgumentExpression: StringLiteralNode name } => name.Text,
        ElementAccessExpressionNode { ArgumentExpression: NumericLiteralNode name } => name.Text,
        ElementAccessExpressionNode { ArgumentExpression: NoSubstitutionTemplateLiteralNode name } => name.Text,
        _ => (TextSlice?)null
    };

    private static bool ModuleExports(SyntaxNode? node) =>
        AccessBase(node) is IdentifierNode { Text.Span: "module" } && AccessName(node) == "exports";
}

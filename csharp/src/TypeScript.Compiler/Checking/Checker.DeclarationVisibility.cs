using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly Dictionary<SyntaxNode, bool> declarationVisibility = [];
    private readonly HashSet<SourceFileNode> visibilityFiles = [];
    private VisibilityChanges? visibilityChanges;
    internal Action<SyntaxNode>? BeforeVisibilityNode { get; set; }
    internal int DeclarationVisibilityCount => declarationVisibility.Count;
    internal int VisibilityFileCount => visibilityFiles.Count;

    private sealed class VisibilityChanges
    {
        internal Dictionary<SyntaxNode, bool?> Declarations { get; } = [];
        internal HashSet<SourceFileNode> Files { get; } = [];
    }

    private async ValueTask<T> VisibilityQueryAsync<T>(SyntaxNode? node, Func<ValueTask<T>> action, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(node, cancellation).ConfigureAwait(false);
        return await VisibilityOperationAsync(action, cancellation);
    }

    private async ValueTask<T> VisibilityOperationAsync<T>(Func<ValueTask<T>> action, CancellationToken cancellation)
    {
        if (visibilityChanges is not null)
            return await action();
        var changes = new VisibilityChanges();
        visibilityChanges = changes;
        try
        {
            var result = await action();
            cancellation.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            foreach (var (declaration, previous) in changes.Declarations)
                if (previous is { } value)
                    declarationVisibility[declaration] = value;
                else
                    declarationVisibility.Remove(declaration);
            foreach (var file in changes.Files)
                visibilityFiles.Remove(file);
            throw;
        }
        finally
        {
            visibilityChanges = null;
        }
    }

    private void SetDeclarationVisible(SyntaxNode node, bool visible)
    {
        bool found = declarationVisibility.TryGetValue(node, out bool previous);
        if (found && previous == visible)
            return;
        visibilityChanges?.Declarations.TryAdd(node, found ? previous : null);
        declarationVisibility[node] = visible;
    }

    internal ValueTask<bool> IsDeclarationVisibleAsync(SyntaxNode node, CancellationToken cancellation = default) =>
        VisibilityQueryAsync(node, () => ValueTask.FromResult(DeclarationVisible(node, cancellation)), cancellation);

    private bool DeclarationVisible(SyntaxNode? node, CancellationToken cancellation)
    {
        var pending = new List<SyntaxNode>();
        bool visible = false;
        while (node is not null && (node.Flags & NodeFlags.Synthesized) == 0)
        {
            cancellation.ThrowIfCancellationRequested();
            BeforeVisibilityNode?.Invoke(node);
            if (declarationVisibility.TryGetValue(node, out visible))
                break;
            pending.Add(node);
            switch (node.Kind)
            {
                case SyntaxKind.JSDocCallbackTag or SyntaxKind.JSDocTypedefTag:
                    visible = node.Parent?.Parent?.Parent is SourceFileNode;
                    break;
                case SyntaxKind.BindingElement:
                    node = node.Parent?.Parent;
                    continue;
                case SyntaxKind.VariableDeclaration or SyntaxKind.ModuleDeclaration or SyntaxKind.ClassDeclaration
                    or SyntaxKind.InterfaceDeclaration or SyntaxKind.TypeAliasDeclaration or SyntaxKind.JSTypeAliasDeclaration
                    or SyntaxKind.FunctionDeclaration or SyntaxKind.EnumDeclaration or SyntaxKind.ImportEqualsDeclaration:
                    if (node is VariableDeclarationNode { Name: BindingPatternNode { Elements.Count: 0 } })
                    {
                        visible = false;
                        break;
                    }
                    if (ExternalVisibilityAugmentation(node) || node.Parent is SourceFileNode
                        && program.Symbols.Binding(node.Parent)?.IsModule == true
                        && (node.Kind == SyntaxKind.JSTypeAliasDeclaration
                            || node is ModuleDeclarationNode && (node.Flags & NodeFlags.Reparsed) != 0))
                    {
                        visible = true;
                        break;
                    }
                    var parent = SemanticSyntax.DeclarationContainer(node);
                    if (!CombinedExport(node) && !(node is not ImportEqualsDeclarationNode && parent is not null and not SourceFileNode
                        && (parent.Flags & NodeFlags.Ambient) != 0))
                    {
                        visible = parent is SourceFileNode && program.Symbols.Binding(parent)?.IsModule != true;
                        break;
                    }
                    node = parent;
                    continue;
                case SyntaxKind.PropertyDeclaration or SyntaxKind.PropertySignature or SyntaxKind.GetAccessor or SyntaxKind.SetAccessor
                    or SyntaxKind.MethodDeclaration or SyntaxKind.MethodSignature:
                    if (Effective(node, SyntaxKind.PrivateKeyword) || Effective(node, SyntaxKind.ProtectedKeyword))
                    {
                        visible = false;
                        break;
                    }
                    node = node.Parent;
                    continue;
                case SyntaxKind.Constructor or SyntaxKind.ConstructSignature or SyntaxKind.CallSignature or SyntaxKind.IndexSignature
                    or SyntaxKind.Parameter or SyntaxKind.ModuleBlock or SyntaxKind.FunctionType or SyntaxKind.ConstructorType
                    or SyntaxKind.TypeLiteral or SyntaxKind.TypeReference or SyntaxKind.ArrayType or SyntaxKind.TupleType
                    or SyntaxKind.UnionType or SyntaxKind.IntersectionType or SyntaxKind.ParenthesizedType or SyntaxKind.NamedTupleMember:
                    node = node.Parent;
                    continue;
                case SyntaxKind.TypeParameter or SyntaxKind.SourceFile or SyntaxKind.NamespaceExportDeclaration:
                    visible = true;
                    break;
                case SyntaxKind.ExportSpecifier:
                    if (node.Parent?.Parent is ExportDeclarationNode { ModuleSpecifier: null } export)
                    {
                        node = export.Parent;
                        continue;
                    }
                    visible = false;
                    break;
                default:
                    visible = false;
                    break;
            }
            break;
        }
        cancellation.ThrowIfCancellationRequested();
        foreach (var declaration in pending)
            SetDeclarationVisible(declaration, visible);
        return visible;
    }

    private bool ExternalVisibilityAugmentation(SyntaxNode node) => AmbientModule(node) && node.Parent switch
    {
        SourceFileNode file => file.ExternalModuleIndicator is not null,
        ModuleBlockNode { Parent: ModuleDeclarationNode { Parent: SourceFileNode file } module } =>
            AmbientModule(module) && file.ExternalModuleIndicator is null,
        _ => false
    };

    private static bool CombinedExport(SyntaxNode node)
    {
        var root = SemanticSyntax.RootDeclaration(node);
        return SemanticSyntax.HasModifier(root, SyntaxKind.ExportKeyword)
            || root is VariableDeclarationNode && root.Parent?.Parent is VariableStatementNode statement
                && SemanticSyntax.HasModifier(statement, SyntaxKind.ExportKeyword);
    }

    internal ValueTask<IReadOnlyList<SyntaxNode>?> GetVisibleDeclarationsAsync(Symbol symbol, bool computeAliases,
        CancellationToken cancellation = default) => VisibilityQueryAsync(null,
            () => ValueTask.FromResult(VisibleDeclarations(symbol, computeAliases, cancellation)), cancellation);

    private IReadOnlyList<SyntaxNode>? VisibleDeclarations(Symbol symbol, bool computeAliases, CancellationToken cancellation)
    {
        Dictionary<SyntaxNode, SyntaxNode>? aliases = null;
        foreach (var declaration in symbol.Declarations)
        {
            cancellation.ThrowIfCancellationRequested();
            if (declaration is IdentifierNode || DeclarationVisible(declaration, cancellation))
                continue;
            var import = declaration switch
            {
                ImportEqualsDeclarationNode => declaration,
                ImportClauseNode => declaration.Parent,
                NamespaceImportNode => declaration.Parent?.Parent,
                ImportSpecifierNode => declaration.Parent?.Parent?.Parent,
                _ => null
            };
            if (import is not null && !SemanticSyntax.HasModifier(import, SyntaxKind.ExportKeyword)
                && DeclarationVisible(import.Parent, cancellation))
                Add(declaration, import);
            else if (declaration is VariableDeclarationNode && declaration.Parent?.Parent is VariableStatementNode variable
                && !SemanticSyntax.HasModifier(variable, SyntaxKind.ExportKeyword) && DeclarationVisible(variable.Parent, cancellation))
                Add(declaration, variable);
            else if (LateVisibilityStatement(declaration) && !SemanticSyntax.HasModifier(declaration, SyntaxKind.ExportKeyword)
                && DeclarationVisible(declaration.Parent, cancellation))
                Add(declaration, declaration);
            else if (declaration is BindingElementNode)
            {
                if ((symbol.Flags & SymbolFlags.Alias) != 0 && (declaration.Flags & NodeFlags.JavaScriptFile) != 0
                    && declaration.Parent?.Parent is VariableDeclarationNode { Parent.Parent: VariableStatementNode statement }
                    && !SemanticSyntax.HasModifier(
                        statement,
                        SyntaxKind.ExportKeyword) && DeclarationVisible(statement.Parent, cancellation))
                    Add(declaration, statement);
                else if ((symbol.Flags & SymbolFlags.BlockScopedVariable) != 0)
                {
                    var root = SemanticSyntax.RootDeclaration(declaration);
                    if (root is ParameterDeclarationNode || root.Parent?.Parent is not VariableStatementNode variableStatement)
                        return null;
                    if (SemanticSyntax.HasModifier(variableStatement, SyntaxKind.ExportKeyword))
                        continue;
                    if (!DeclarationVisible(variableStatement.Parent, cancellation))
                        return null;
                    Add(declaration, variableStatement);
                }
                else
                    return null;
            }
            else
                return null;
        }
        return aliases is null ? [] : aliases.Values.ToArray();

        void Add(SyntaxNode declaration, SyntaxNode statement)
        {
            if (!computeAliases)
                return;
            SetDeclarationVisible(declaration, true);
            (aliases ??= []).TryAdd(declaration, statement);
        }
    }

    private static bool LateVisibilityStatement(SyntaxNode node) => node is ImportDeclarationNode or ImportEqualsDeclarationNode
        or VariableStatementNode or ClassDeclarationNode or FunctionDeclarationNode or ModuleDeclarationNode or TypeAliasDeclarationNode
        or InterfaceDeclarationNode or EnumDeclarationNode;
}

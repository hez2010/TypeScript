using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal readonly record struct AssignmentMark(int LastPosition, bool Definite);

internal sealed class AssignmentMarks(CheckerLinks links, CheckerSymbols symbols, ReferenceSymbols references, EntityNames names)
{
    private readonly Dictionary<Symbol, AssignmentMark> marks = [];
    private readonly Dictionary<SyntaxNode, List<SyntaxNode>> activeMarks = [];

    internal async ValueTask<bool> AssignedAsync(Symbol symbol, CancellationToken cancellation = default)
    {
        await EnsureAsync(symbol, cancellation).ConfigureAwait(false);
        return marks.GetValueOrDefault(symbol).LastPosition != 0;
    }

    internal async ValueTask<bool> DefiniteAsync(Symbol symbol, CancellationToken cancellation = default)
    {
        await EnsureAsync(symbol, cancellation).ConfigureAwait(false);
        return marks.GetValueOrDefault(symbol).Definite;
    }

    internal async ValueTask<bool> PastLastAsync(Symbol symbol, SyntaxNode? location, CancellationToken cancellation = default)
    {
        await EnsureAsync(symbol, cancellation).ConfigureAwait(false);
        int position = marks.GetValueOrDefault(symbol).LastPosition;
        return position == 0 || location is not null && position < location.Pos;
    }

    internal async ValueTask<AssignmentMark> GetAsync(Symbol symbol, CancellationToken cancellation = default)
    {
        await EnsureAsync(symbol, cancellation).ConfigureAwait(false);
        return marks.GetValueOrDefault(symbol);
    }

    private async ValueTask EnsureAsync(Symbol symbol, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var root = DeclarationOrder.Ancestor(symbol.ValueDeclaration, FunctionOrSource);
        if (root is null)
            return;
        var data = links.Nodes.Get(root);
        if ((data.Flags & NodeCheckFlags.AssignmentsMarked) != 0)
            return;
        data.Flags |= NodeCheckFlags.AssignmentsMarked;
        var markedParent = DeclarationOrder.Ancestor(root.Parent,
            n => FunctionOrSource(n) && (links.Nodes.Get(n).Flags & NodeCheckFlags.AssignmentsMarked) != 0);
        if (markedParent is not null)
        {
            // A nested request borrows an in-progress ancestor scan. Its marker
            // must be rolled back with that scan if cancellation interrupts it.
            if (activeMarks.TryGetValue(markedParent, out var owner))
            {
                owner.Add(root);
                activeMarks.Add(root, owner);
            }
            return;
        }
        List<SyntaxNode> ownedMarks = [root];
        activeMarks.Add(root, ownedMarks);
        Dictionary<Symbol, AssignmentMark?> previous = [];
        try
        {
            var pending = new Stack<SyntaxNode>();
            pending.Push(root);
            while (pending.TryPop(out var node))
            {
                cancellation.ThrowIfCancellationRequested();
                if (node is IdentifierNode identifier)
                {
                    int kind = ReferenceSyntax.AssignmentKind(node);
                    if (kind == 0)
                        continue;
                    var target = references.Resolve(identifier, cancellation);
                    if (!ParameterOrMutableLocal(target))
                        continue;
                    Save(target);
                    var mark = marks.GetValueOrDefault(target);
                    int position = mark.LastPosition;
                    if (position != int.MaxValue)
                        position = DeclarationOrder.Ancestor(
                            node,
                            FunctionOrSource) == DeclarationOrder.Ancestor(target.ValueDeclaration, FunctionOrSource)
                            ? ExtendedPosition(node, target.ValueDeclaration!) : int.MaxValue;
                    marks[target] = new(position, mark.Definite || kind == 1);
                    continue;
                }
                if (node is ExportSpecifierNode export)
                {
                    var declaration = (ExportDeclarationNode)export.Parent!.Parent!;
                    var name = export.PropertyName ?? export.Name;
                    if (!export.IsTypeOnly
                        && !declaration.IsTypeOnly
                        && declaration.ModuleSpecifier is null
                        && name is not StringLiteralNode)
                    {
                        var target = await names.ResolveAsync(
                            name,
                            SymbolFlags.Value,
                            true,
                            true,
                            cancellation: cancellation).ConfigureAwait(false);
                        if (target is not null && ParameterOrMutableLocal(target))
                        {
                            Save(target);
                            marks[target] = marks.GetValueOrDefault(target) with { LastPosition = int.MaxValue };
                        }
                    }
                    continue;
                }
                if (node is InterfaceDeclarationNode or TypeAliasDeclarationNode or EnumDeclarationNode || SemanticSyntax.TypeNode(node))
                    continue;
                for (int i = node.ChildCount - 1; i >= 0; i--)
                    pending.Push(node.GetChild(i));
            }
            cancellation.ThrowIfCancellationRequested();
        }
        catch
        {
            foreach (var node in ownedMarks)
                links.Nodes.Get(node).Flags &= ~NodeCheckFlags.AssignmentsMarked;
            foreach (var (target, old) in previous)
                if (old is { } value)
                    marks[target] = value;
                else
                    marks.Remove(target);
            throw;
        }
        finally
        {
            foreach (var node in ownedMarks)
                activeMarks.Remove(node);
        }

        void Save(Symbol target)
        {
            if (!previous.ContainsKey(target))
                previous[target] = marks.TryGetValue(target, out var mark) ? mark : null;
        }
    }

    internal bool ParameterOrMutableLocal(Symbol symbol)
    {
        if (symbol.ValueDeclaration is not { } declaration)
            return false;
        var root = SemanticSyntax.RootDeclaration(declaration);
        return root is ParameterDeclarationNode
            || root is VariableDeclarationNode && (root.Parent is CatchClauseNode || MutableLocal(root));
    }

    internal bool MutableLocal(SyntaxNode declaration) => (declaration.Parent!.Flags & NodeFlags.Let) != 0
        && !(SemanticSyntax.HasModifier(declaration, SyntaxKind.ExportKeyword)
            || declaration.Parent.Parent is VariableStatementNode statement && (SemanticSyntax.HasModifier(
                statement,
                SyntaxKind.ExportKeyword)
                || statement.Parent is SourceFileNode file && symbols.Binding(file)?.IsModule == false));

    internal static bool Constant(Symbol symbol) => (symbol.Flags & SymbolFlags.Variable) != 0
        && symbol.ValueDeclaration is { } declaration && ConstLike(declaration);

    internal static bool ConstLike(SyntaxNode declaration) => SemanticSyntax.RootDeclaration(declaration).Parent is VariableDeclarationListNode list
        && (list.Flags & NodeFlags.Constant) != 0;

    private static bool FunctionOrSource(SyntaxNode node) => node is IFunctionSignature or SourceFileNode;

    internal static int ExtendedPosition(SyntaxNode node, SyntaxNode declaration)
    {
        int position = node.Pos;
        for (; node is not null && node.Pos > declaration.Pos; node = node.Parent!)
            if (node.Kind is SyntaxKind.VariableStatement or SyntaxKind.ExpressionStatement or SyntaxKind.IfStatement
                or SyntaxKind.DoStatement
                or SyntaxKind.WhileStatement or SyntaxKind.ForStatement or SyntaxKind.ForInStatement or SyntaxKind.ForOfStatement or SyntaxKind.WithStatement
                or SyntaxKind.SwitchStatement or SyntaxKind.TryStatement or SyntaxKind.ClassDeclaration)
                position = node.End;
        return position;
    }
}

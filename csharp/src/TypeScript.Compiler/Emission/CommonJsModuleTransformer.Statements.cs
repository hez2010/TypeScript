using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class CommonJsModuleTransformer
{
    private async ValueTask<SyntaxNode> NestedStatementAsync(SyntaxNode node)
    {
        switch (node)
        {
            case DoStatementNode statement:
                var updatedDo = Context.Clone(statement);
                updatedDo.Statement = await EmbeddedAsync(statement.Statement, true);
                updatedDo.Expression = await VisitAsync(statement.Expression);
                return updatedDo;
            case WhileStatementNode statement:
                var updatedWhile = Context.Clone(statement);
                updatedWhile.Expression = await VisitAsync(statement.Expression);
                updatedWhile.Statement = await EmbeddedAsync(statement.Statement, true);
                return updatedWhile;
            case LabeledStatementNode statement:
                var updatedLabel = Context.Clone(statement);
                updatedLabel.Statement = await EmbeddedAsync(statement.Statement, false);
                return updatedLabel;
            case WithStatementNode statement:
                var updatedWith = Context.Clone(statement);
                updatedWith.Expression = await VisitAsync(statement.Expression);
                updatedWith.Statement = await EmbeddedAsync(statement.Statement, false);
                return updatedWith;
            default: throw new InvalidOperationException();
        }
    }

    private async ValueTask<SyntaxNode> ForAsync(ForStatementNode node, bool topLevel)
    {
        List<SyntaxNode> statements = [];
        if (topLevel && node.Initializer is VariableDeclarationListNode list && (list.Flags & NodeFlags.BlockScoped) == 0)
            await AppendVariablesAsync(statements, list);
        var initializer = await InAsync(node.Initializer, discard: true);
        if (statements.Count > 0) statements.Insert(0, F.NewVariableStatement(null, (VariableDeclarationListNode?)initializer));
        var updated = Context.Clone(node);
        updated.Initializer = statements.Count > 0 ? null : initializer;
        updated.Condition = await VisitAsync(node.Condition);
        updated.Incrementor = await InAsync(node.Incrementor, discard: true);
        updated.Statement = await EmbeddedAsync(node.Statement, true);
        statements.Add(updated);
        return Many(statements);
    }

    private async ValueTask<SyntaxNode> ForInOrOfAsync(ForInOrOfStatementNode node, bool topLevel)
    {
        List<SyntaxNode> exports = [];
        if (topLevel && node.Initializer is VariableDeclarationListNode list && (list.Flags & NodeFlags.BlockScoped) == 0)
            await AppendVariablesAsync(exports, list, true);
        var updated = Context.Clone(node);
        updated.Initializer = node.Initializer is VariableDeclarationListNode ? await InAsync(node.Initializer, discard: true)
            : await new AssignmentPatternRewriter(this).VisitAsync(node.Initializer);
        updated.Expression = await VisitAsync(node.Expression);
        var body = (await EmbeddedAsync(node.Statement, true))!;
        if (exports.Count > 0)
        {
            if (body is BlockNode block)
            {
                var result = Context.Clone(block);
                result.Statements = new([.. exports, .. block.Statements ?? new([])], block.Statements?.Pos ?? -1, block.Statements?.End ?? -1);
                body = result;
            }
            else body = F.NewBlock(new([.. exports, body]), true);
        }
        updated.Statement = body;
        return updated;
    }
}

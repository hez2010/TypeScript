using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask CheckKnownConditionAsync(Type type, SyntaxNode expression, SyntaxNode? body, CancellationToken cancellation)
    {
        var pending = new Stack<SyntaxNode>();
        pending.Push(expression);
        while (pending.TryPop(out var condition))
        {
            cancellation.ThrowIfCancellationRequested();
            condition = MemberAccessRules.SkipParentheses(condition);
            var location = LogicalCondition(condition)
                ? MemberAccessRules.SkipParentheses(((BinaryExpressionNode)condition).Right!)
                : condition;
            if (condition is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.BarBarToken or SyntaxKind.QuestionQuestionToken } chain)
                pending.Push(chain.Left!);
            if (ModuleExportsAccess(location))
                continue;
            if (LogicalCondition(location))
            {
                pending.Push(location);
                continue;
            }
            var current = location == condition
                ? type
                : await Expressions.CheckAsync(location, cancellation: cancellation).ConfigureAwait(false);
            if ((current.Flags & TypeFlags.EnumLiteral) != 0 && location is PropertyAccessExpressionNode enumAccess
                && (links.SymbolNodes.TryGet(enumAccess.Expression!)?.ResolvedSymbol?.Flags & SymbolFlags.Enum) != 0
                && links.SymbolNodes.TryGet(enumAccess.Expression!)?.ResolvedSymbol is not null)
            {
                Error(location, 2845, current is LiteralType { Value: { } value } && ConstantEvaluator.IsTruthy(value) ? "true" : "false");
                continue;
            }
            bool asserted = location is PropertyAccessExpressionNode access
                && MemberAccessRules.SkipParentheses(access.Expression!) is AsExpressionNode or TypeAssertionNode;
            if (asserted || await Facts.GetAsync(current, TypeFacts.Truthy, cancellation).ConfigureAwait(false) == 0)
                continue;
            var calls = await SignaturesAsync(current, false, cancellation).ConfigureAwait(false);
            bool promise = await Awaited.OfPromiseAsync(current, cancellation: cancellation).ConfigureAwait(false) is not null;
            if (calls.Count == 0 && !promise)
                continue;
            SyntaxNode? tested = location is IdentifierNode ? location : (location as PropertyAccessExpressionNode)?.Name;
            var symbol = tested is null ? null : await ConditionSymbolAsync(tested, cancellation).ConfigureAwait(false);
            if (symbol is null && !promise)
                continue;
            bool used = false;
            if (symbol is not null)
            {
                for (var parent = condition.Parent; parent is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.AmpersandAmpersandToken } binary; parent = parent.Parent)
                    if (await ConditionUseAsync(binary.Right!, symbol, null, null, cancellation).ConfigureAwait(false))
                    {
                        used = true;
                        break;
                    }
                if (!used && body is not null)
                    used = await ConditionUseAsync(body, symbol, condition, tested, cancellation).ConfigureAwait(false);
            }
            if (!used)
            {
                Error(location, promise ? 2801 : 2774, promise ? [await TypeDisplay.GetAsync(current, cancellation)] : []);
                if (promise)
                    MissingAwaitHints.Add(location);
            }
        }
    }

    private static bool LogicalCondition(SyntaxNode node) => node is BinaryExpressionNode
    { OperatorToken.Kind: SyntaxKind.AmpersandAmpersandToken or SyntaxKind.BarBarToken or SyntaxKind.QuestionQuestionToken };

    private async ValueTask<bool> ConditionUseAsync(
        SyntaxNode body,
        Symbol testedSymbol,
        SyntaxNode? condition,
        SyntaxNode? testedNode,
        CancellationToken cancellation)
    {
        var pending = new Stack<SyntaxNode>();
        for (int i = body.ChildCount - 1; i >= 0; i--)
            pending.Push(body.GetChild(i));
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if (current is IdentifierNode && await ConditionSymbolAsync(current, cancellation).ConfigureAwait(false) == testedSymbol)
            {
                if (condition is null or IdentifierNode || testedNode is IdentifierNode { Parent: BinaryExpressionNode })
                    return true;
                if (await SameConditionReceiverAsync(testedNode?.Parent, current.Parent, cancellation).ConfigureAwait(false))
                    return true;
            }
            for (int i = current.ChildCount - 1; i >= 0; i--)
                pending.Push(current.GetChild(i));
        }
        return false;
    }

    private async ValueTask<bool> SameConditionReceiverAsync(SyntaxNode? left, SyntaxNode? right, CancellationToken cancellation)
    {
        while (left is not null && right is not null)
        {
            if (left is IdentifierNode && right is IdentifierNode
                || left.Kind == SyntaxKind.ThisKeyword && right.Kind == SyntaxKind.ThisKeyword)
                return await ConditionSymbolAsync(
                    left,
                    cancellation).ConfigureAwait(false) == await ConditionSymbolAsync(right, cancellation).ConfigureAwait(false);
            if (left is PropertyAccessExpressionNode l && right is PropertyAccessExpressionNode r)
            {
                if (await ConditionSymbolAsync(
                    l.Name!,
                    cancellation).ConfigureAwait(false) != await ConditionSymbolAsync(r.Name!, cancellation).ConfigureAwait(false))
                    return false;
                left = l.Expression;
                right = r.Expression;
            }
            else if (left is CallExpressionNode callLeft && right is CallExpressionNode callRight)
            {
                left = callLeft.Expression;
                right = callRight.Expression;
            }
            else
                return false;
        }
        return false;
    }

    private async ValueTask<Symbol?> ConditionSymbolAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (node.Parent is PropertyAccessExpressionNode access && access.Name == node)
        {
            await Expressions.CheckAsync(access, cancellation: cancellation).ConfigureAwait(false);
            return links.SymbolNodes.TryGet(access)?.ResolvedSymbol;
        }
        if (node is IdentifierNode identifier)
        {
            if (node.Parent is { } parent && SemanticSyntax.Name(parent) == node && program.Symbols.Declaration(parent) is { } declared)
                return declared;
            return program.ReferenceSymbols.Resolve(identifier, cancellation);
        }
        if (node.Kind == SyntaxKind.ThisKeyword)
            return (await ThisExpressions.ThisAsync(node, cancellation).ConfigureAwait(false)).Symbol;
        return program.Symbols.Declaration(node);
    }
}

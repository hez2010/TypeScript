using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using F = TypeScript.Compiler.Binding.FlowFlags;

namespace TypeScript.Compiler.Binding;

public sealed partial class Binder
{
    private static SyntaxNode? Expression(SyntaxNode? node) => node switch
    {
        ParenthesizedExpressionNode n => n.Expression,
        NonNullExpressionNode n => n.Expression,
        TypeOfExpressionNode n => n.Expression,
        PropertyAccessExpressionNode n => n.Expression,
        ElementAccessExpressionNode n => n.Expression,
        CallExpressionNode n => n.Expression,
        IfStatementNode n => n.Expression,
        WhileStatementNode n => n.Expression,
        DoStatementNode n => n.Expression,
        _ => null
    };

    private static SyntaxNode? SkipParentheses(SyntaxNode? node)
    {
        while (node is ParenthesizedExpressionNode parentheses)
            node = parentheses.Expression;
        return node;
    }

    private static bool Logical(SyntaxNode? node, bool includeAssignment = true)
    {
        while (true)
        {
            if (node is ParenthesizedExpressionNode parentheses)
                node = parentheses.Expression;
            else if (node is PrefixUnaryExpressionNode { Operator: K.ExclamationToken } unary)
            {
                node = unary.Operand;
                includeAssignment = false;
            }
            else
                break;
        }
        return node is BinaryExpressionNode { OperatorToken.Kind: K.AmpersandAmpersandToken or K.BarBarToken or K.QuestionQuestionToken }
            || includeAssignment
                && node is BinaryExpressionNode
                {
                    OperatorToken.Kind: K.AmpersandAmpersandEqualsToken or K.BarBarEqualsToken
                    or K.QuestionQuestionEqualsToken
                };
    }

    private static bool Optional(SyntaxNode? node) => node is not null && (node.Flags & NodeFlags.OptionalChain) != 0
            && node.Kind is K.PropertyAccessExpression or K.ElementAccessExpression or K.CallExpression or K.NonNullExpression;

    private static bool OptionalRoot(SyntaxNode node) => node switch
    {
        PropertyAccessExpressionNode n => n.QuestionDotToken is not null,
        ElementAccessExpressionNode n => n.QuestionDotToken is not null,
        CallExpressionNode n => n.QuestionDotToken is not null,
        _ => false
    };

    private static bool OutermostOptional(SyntaxNode node) =>
        !Optional(node.Parent) || OptionalRoot(node.Parent!) || Expression(node.Parent) != node;

    private static bool TopLogical(SyntaxNode node)
    {
        while (node.Parent is ParenthesizedExpressionNode or PrefixUnaryExpressionNode { Operator: K.ExclamationToken })
            node = node.Parent;
        var parent = node.Parent;
        bool condition = parent is IfStatementNode or WhileStatementNode or DoStatementNode && Expression(parent) == node
            || parent is ForStatementNode f && f.Condition == node || parent is ConditionalExpressionNode c && c.Condition == node;
        return !condition && !Logical(parent, false) && !(Optional(parent) && Expression(parent) == node);
    }

    private async ValueTask Binary(BinaryExpressionNode node)
    {
        K op = node.OperatorToken?.Kind ?? 0;
        if (Logical(node))
        {
            bool top = TopLogical(node);
            var savedFlow = currentFlow;
            bool savedEffects = hasFlowEffects;
            FlowNode? exit = top ? Label() : null;
            if (top)
                hasFlowEffects = false;
            var yes = exit ?? trueTarget!;
            var no = exit ?? falseTarget!;
            var right = Label();
            if (op is K.AmpersandAmpersandToken or K.AmpersandAmpersandEqualsToken)
                await Condition(node.Left, right, no).ConfigureAwait(false);
            else
                await Condition(node.Left, yes, right).ConfigureAwait(false);
            currentFlow = Finish(right);
            await Visit(node.OperatorToken).ConfigureAwait(false);
            if (Assignment(op))
            {
                var oldTrue = trueTarget;
                var oldFalse = falseTarget;
                trueTarget = yes;
                falseTarget = no;
                await Visit(node.Right).ConfigureAwait(false);
                trueTarget = oldTrue;
                falseTarget = oldFalse;
                AssignmentFlow(node.Left);
                Add(yes, ConditionFlow(F.TrueCondition, node));
                Add(no, ConditionFlow(F.FalseCondition, node));
            }
            else
                await Condition(node.Right, yes, no).ConfigureAwait(false);
            if (top)
            {
                currentFlow = hasFlowEffects ? Finish(exit!) : savedFlow;
                hasFlowEffects |= savedEffects;
            }
            return;
        }
        await Visit(node.Left).ConfigureAwait(false);
        await Visit(node.Type).ConfigureAwait(false);
        if (op == K.CommaToken)
            AssertionCall(node.Left);
        await Visit(node.OperatorToken).ConfigureAwait(false);
        await Visit(node.Right).ConfigureAwait(false);
        if (op == K.CommaToken)
            AssertionCall(node.Right);
        if (Assignment(op) && !AssignmentTarget(node))
        {
            AssignmentFlow(node.Left);
            if (op == K.EqualsToken && node.Left is ElementAccessExpressionNode access && NarrowableOperand(access.Expression))
                currentFlow = Mutation(F.ArrayMutation, node);
        }
    }

    private async ValueTask Conditional(ConditionalExpressionNode node)
    {
        var yes = Label();
        var no = Label();
        var exit = Label();
        var savedFlow = currentFlow;
        bool savedEffects = hasFlowEffects;
        hasFlowEffects = false;
        await Condition(node.Condition, yes, no).ConfigureAwait(false);
        currentFlow = Finish(yes);
        await Visit(node.QuestionToken).ConfigureAwait(false);
        await Visit(node.WhenTrue).ConfigureAwait(false);
        Add(exit, currentFlow);
        currentFlow = Finish(no);
        await Visit(node.ColonToken).ConfigureAwait(false);
        await Visit(node.WhenFalse).ConfigureAwait(false);
        Add(exit, currentFlow);
        currentFlow = hasFlowEffects ? Finish(exit) : savedFlow;
        hasFlowEffects |= savedEffects;
    }

    private async ValueTask OptionalFlow(SyntaxNode node)
    {
        bool top = TopLogical(node);
        var exit = top ? Label() : null;
        var savedFlow = currentFlow;
        bool savedEffects = hasFlowEffects;
        var yes = exit ?? trueTarget!;
        var no = exit ?? falseTarget!;
        var preChain = OptionalRoot(node) ? Label() : null;
        var expression = Expression(node);
        var oldTrue = trueTarget;
        var oldFalse = falseTarget;
        trueTarget = preChain ?? yes;
        falseTarget = no;
        await Visit(expression).ConfigureAwait(false);
        trueTarget = oldTrue;
        falseTarget = oldFalse;
        if (!Optional(expression) || OutermostOptional(expression!))
        {
            Add(preChain ?? yes, ConditionFlow(F.TrueCondition, expression));
            Add(no, ConditionFlow(F.FalseCondition, expression));
        }
        if (preChain is not null)
            currentFlow = Finish(preChain);
        oldTrue = trueTarget;
        oldFalse = falseTarget;
        trueTarget = yes;
        falseTarget = no;
        switch (node)
        {
            case PropertyAccessExpressionNode n:
                await Visit(n.QuestionDotToken).ConfigureAwait(false);
                await Visit(n.Name).ConfigureAwait(false);
                break;
            case ElementAccessExpressionNode n:
                await Visit(n.QuestionDotToken).ConfigureAwait(false);
                await Visit(n.ArgumentExpression).ConfigureAwait(false);
                break;
            case CallExpressionNode n:
                await Visit(n.QuestionDotToken).ConfigureAwait(false);
                await Each(n.TypeArguments).ConfigureAwait(false);
                await Each(n.Arguments).ConfigureAwait(false);
                break;
        }
        trueTarget = oldTrue;
        falseTarget = oldFalse;
        if (OutermostOptional(node))
        {
            Add(yes, ConditionFlow(F.TrueCondition, node));
            Add(no, ConditionFlow(F.FalseCondition, node));
        }
        if (top)
        {
            currentFlow = hasFlowEffects ? Finish(exit!) : savedFlow;
            hasFlowEffects |= savedEffects;
        }
    }

    private async ValueTask Call(CallExpressionNode node)
    {
        if (Optional(node))
            await OptionalFlow(node).ConfigureAwait(false);
        else if (SkipParentheses(node.Expression) is FunctionExpressionNode or ArrowFunctionNode)
        {
            await Each(node.TypeArguments).ConfigureAwait(false);
            await Each(node.Arguments).ConfigureAwait(false);
            await Visit(node.Expression).ConfigureAwait(false);
        }
        else
        {
            await EachChild(node).ConfigureAwait(false);
            if (node.Expression?.Kind == K.SuperKeyword)
                currentFlow = Mutation(F.Call, node);
        }
        if (node.Expression is PropertyAccessExpressionNode { Name: IdentifierNode { Text: "push" or "unshift" } } access
            && NarrowableOperand(access.Expression))
            currentFlow = Mutation(F.ArrayMutation, node);
    }

    private static bool EntityName(SyntaxNode? node)
    {
        while (node is PropertyAccessExpressionNode property)
            node = property.Expression;
        return node is IdentifierNode;
    }

    private static bool LiteralLike(SyntaxNode? node) =>
        node is StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode;

    private static bool AssignmentTarget(SyntaxNode node)
    {
        while (node.Parent is { } parent)
        {
            switch (parent)
            {
                case ParenthesizedExpressionNode or ArrayLiteralExpressionNode or ObjectLiteralExpressionNode:
                    node = parent;
                    break;
                case PropertyAssignmentNode p when p.Initializer == node:
                case SpreadElementNode:
                case SpreadAssignmentNode:
                    node = parent;
                    break;
                case BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken } assignment:
                    return assignment.Left == node;
                case ForInOrOfStatementNode statement:
                    return statement.Initializer == node;
                default:
                    return false;
            }
        }
        return false;
    }

    private static bool Narrowable(SyntaxNode? node)
    {
        while (true)
        {
            switch (node)
            {
                case IdentifierNode:
                    return true;
                case { Kind: K.ThisKeyword or K.SuperKeyword or K.MetaProperty }:
                    return true;
                case PropertyAccessExpressionNode or ParenthesizedExpressionNode or NonNullExpressionNode:
                    node = Expression(node);
                    break;
                case ElementAccessExpressionNode element:
                    if (LiteralLike(element.ArgumentExpression))
                        return true;
                    if (!EntityName(element.ArgumentExpression))
                        return false;
                    node = element.Expression;
                    break;
                case BinaryExpressionNode { OperatorToken.Kind: K.CommaToken } binary:
                    node = binary.Right;
                    break;
                case BinaryExpressionNode binary when Assignment(binary.OperatorToken?.Kind ?? 0):
                    return binary.Left?.Kind is K.Identifier or K.PropertyAccessExpression or K.ElementAccessExpression
                        or K.ParenthesizedExpression;
                default:
                    return false;
            }
        }
    }

    private static bool ContainsReference(SyntaxNode? node)
    {
        while (node is not null)
        {
            if (Narrowable(node))
                return true;
            if (!Optional(node))
                return false;
            node = Expression(node);
        }
        return false;
    }

    private static bool NarrowableOperand(SyntaxNode? node)
    {
        while (true)
        {
            node = SkipParentheses(node);
            if (node is BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken } assignment)
                node = assignment.Left;
            else if (node is BinaryExpressionNode { OperatorToken.Kind: K.CommaToken } comma)
                node = comma.Right;
            else
                return ContainsReference(node);
        }
    }

    private static bool Narrowing(SyntaxNode node)
    {
        var pending = new Stack<SyntaxNode>();
        pending.Push(node);
        while (pending.TryPop(out var expression))
        {
            switch (expression)
            {
                case IdentifierNode:
                    return true;
                case { Kind: K.ThisKeyword }:
                    return true;
                case PropertyAccessExpressionNode or ElementAccessExpressionNode:
                    if (ContainsReference(expression))
                        return true;
                    break;
                case ParenthesizedExpressionNode or NonNullExpressionNode or TypeOfExpressionNode:
                    if (Expression(expression) is { } inner)
                        pending.Push(inner);
                    break;
                case CallExpressionNode call:
                    if (call.Arguments?.Any(ContainsReference) == true
                        || call.Expression is PropertyAccessExpressionNode access && ContainsReference(access.Expression))
                        return true;
                    break;
                case PrefixUnaryExpressionNode { Operator: K.ExclamationToken, Operand: { } operand }:
                    pending.Push(operand);
                    break;
                case BinaryExpressionNode binary:
                    switch (binary.OperatorToken?.Kind)
                    {
                        case K.EqualsToken or K.BarBarEqualsToken or K.AmpersandAmpersandEqualsToken or K.QuestionQuestionEqualsToken:
                            if (ContainsReference(binary.Left))
                                return true;
                            break;
                        case K.EqualsEqualsToken or K.EqualsEqualsEqualsToken or K.ExclamationEqualsToken or K.ExclamationEqualsEqualsToken:
                            var left = SkipParentheses(binary.Left);
                            var right = SkipParentheses(binary.Right);
                            if (NarrowableOperand(left) || NarrowableOperand(right))
                                return true;
                            if (left is TypeOfExpressionNode lt
                                && NarrowableOperand(lt.Expression)
                                && right is StringLiteralNode or NoSubstitutionTemplateLiteralNode
                                || right is TypeOfExpressionNode rt
                                    && NarrowableOperand(rt.Expression)
                                    && left is StringLiteralNode or NoSubstitutionTemplateLiteralNode)
                                return true;
                            if (left?.Kind is K.TrueKeyword or K.FalseKeyword && right is not null)
                                pending.Push(right);
                            if (right?.Kind is K.TrueKeyword or K.FalseKeyword && left is not null)
                                pending.Push(left);
                            break;
                        case K.InstanceOfKeyword:
                            if (NarrowableOperand(binary.Left))
                                return true;
                            break;
                        case K.InKeyword or K.CommaToken:
                            if (binary.Right is { } value)
                                pending.Push(value);
                            break;
                    }
                    break;
            }
        }
        return false;
    }
}

using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed class OptionalChainTransformer(EmitContext context, CancellationToken cancellation = default) : SyntaxRewriter(context, cancellation)
{
    private NodeFactory F => Context.Factory;

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node) => node switch
    {
        CallExpressionNode call => await CallAsync(call, false),
        PropertyAccessExpressionNode or ElementAccessExpressionNode when Optional(node) => await OptionalAsync(node, false, false),
        DeleteExpressionNode deletion when Optional(TransformSyntax.SkipParentheses(deletion.Expression!)) => await NonOptionalAsync(deletion.Expression!, false, true),
        _ => await VisitEachChildAsync(node)
    };

    private async ValueTask<SyntaxNode> CallAsync(CallExpressionNode node, bool captureThis)
    {
        if (Optional(node)) return await OptionalAsync(node, captureThis, false);
        if (node.Expression is ParenthesizedExpressionNode parentheses && Optional(TransformSyntax.SkipParentheses(parentheses)))
        {
            var expression = await ParenthesesAsync(parentheses, true, false);
            var arguments = await VisitListAsync(node.Arguments);
            if (expression is SyntheticReferenceExpressionNode reference)
            {
                var call = EmitContext.CopyRange(Context.MethodCall(reference.Expression!, "call"u8, [reference.ThisArg!, .. arguments ?? new([])]), node);
                Context.SetOriginal(call, node);
                return call;
            }
            var updated = Context.Clone(node);
            updated.Expression = expression;
            updated.QuestionDotToken = null;
            updated.TypeArguments = null;
            updated.Arguments = arguments;
            return updated;
        }
        return (await VisitEachChildAsync(node))!;
    }

    private async ValueTask<SyntaxNode> ParenthesesAsync(ParenthesizedExpressionNode node, bool captureThis, bool isDelete)
    {
        var expression = await NonOptionalAsync(node.Expression!, captureThis, isDelete);
        var reference = expression as SyntheticReferenceExpressionNode;
        var inner = reference?.Expression ?? expression;
        SyntaxNode result = node;
        if (inner != node.Expression)
        {
            var updated = Context.Clone(node);
            updated.Expression = inner;
            result = updated;
        }
        if (reference is not null)
        {
            result = F.NewSyntheticReferenceExpression(result, reference.ThisArg);
            Context.SetOriginal(result, node);
        }
        return result;
    }

    private async ValueTask<SyntaxNode> AccessAsync(SyntaxNode node, bool captureThis, bool isDelete)
    {
        if (Optional(node)) return await OptionalAsync(node, captureThis, isDelete);
        var expression = (await VisitAsync(TransformSyntax.Expression(node)))!;
        SyntaxNode? receiver = null;
        if (captureThis)
        {
            receiver = expression;
            if (!TransformSyntax.SimpleCopiable(expression))
            {
                receiver = Temp();
                expression = Assignment(receiver, expression);
            }
        }
        var result = Context.Clone(node);
        if (result is PropertyAccessExpressionNode property)
        {
            property.Expression = expression;
            property.QuestionDotToken = null;
            property.Name = await VisitAsync(property.Name);
        }
        else
        {
            var element = (ElementAccessExpressionNode)result;
            element.Expression = expression;
            element.QuestionDotToken = null;
            element.ArgumentExpression = await VisitAsync(element.ArgumentExpression);
        }
        if (receiver is not null)
        {
            result = F.NewSyntheticReferenceExpression(result, receiver);
            Context.SetOriginal(result, node);
        }
        return result;
    }

    private async ValueTask<SyntaxNode> NonOptionalAsync(SyntaxNode node, bool captureThis, bool isDelete)
    {
        Cancellation.ThrowIfCancellationRequested();
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        return node switch
        {
            ParenthesizedExpressionNode parentheses => await ParenthesesAsync(parentheses, captureThis, isDelete),
            PropertyAccessExpressionNode or ElementAccessExpressionNode => await AccessAsync(node, captureThis, isDelete),
            CallExpressionNode call => await CallAsync(call, captureThis),
            _ => (await VisitAsync(node))!
        };
    }

    private async ValueTask<SyntaxNode> OptionalAsync(SyntaxNode node, bool captureThis, bool isDelete)
    {
        List<SyntaxNode> chain = [node];
        var link = node;
        while (link is not TaggedTemplateExpressionNode && QuestionDot(link) is null)
        {
            link = TransformSyntax.SkipPartials(TransformSyntax.Expression(link)!);
            chain.Add(link);
        }
        chain.Reverse();
        var expression = TransformSyntax.Expression(link)!;
        var left = await NonOptionalAsync(TransformSyntax.SkipPartials(expression), chain[0] is CallExpressionNode && Optional(chain[0]), false);
        SyntaxNode? leftReceiver = null;
        var capturedLeft = left;
        if (left is SyntheticReferenceExpressionNode reference)
        {
            leftReceiver = reference.ThisArg;
            capturedLeft = reference.Expression!;
        }
        var leftExpression = RestorePartials(expression, capturedLeft);
        if (!TransformSyntax.SimpleCopiable(capturedLeft))
        {
            capturedLeft = Temp();
            leftExpression = Assignment(capturedLeft, leftExpression);
        }
        var right = capturedLeft;
        SyntaxNode? receiver = null;
        for (int i = 0; i < chain.Count; i++)
        {
            var segment = chain[i];
            switch (segment)
            {
                case PropertyAccessExpressionNode or ElementAccessExpressionNode:
                    if (i == chain.Count - 1 && captureThis)
                    {
                        receiver = right;
                        if (!TransformSyntax.SimpleCopiable(right))
                        {
                            receiver = Temp();
                            right = Assignment(receiver, right);
                        }
                    }
                    right = segment is PropertyAccessExpressionNode property
                        ? F.NewPropertyAccessExpression(right, null, await VisitAsync(property.Name), NodeFlags.None)
                        : F.NewElementAccessExpression(right, null, await VisitAsync(((ElementAccessExpressionNode)segment).ArgumentExpression), NodeFlags.None);
                    break;
                case CallExpressionNode call:
                    if (i == 0 && leftReceiver is not null)
                    {
                        if (Context.GetAutoGenerateInfo(leftReceiver) is null)
                        {
                            leftReceiver = Context.Clone(leftReceiver);
                            Context.AddFlags(leftReceiver, EmitFlags.NoComments);
                        }
                        var callReceiver = leftReceiver.Kind == K.SuperKeyword ? F.NewKeywordExpression(K.ThisKeyword) : leftReceiver;
                        right = Context.MethodCall(right, "call"u8, [callReceiver, .. await VisitListAsync(call.Arguments) ?? new([])]);
                    }
                    else right = F.NewCallExpression(right, null, null, await VisitListAsync(call.Arguments), NodeFlags.None);
                    break;
            }
            Context.SetOriginal(right, segment);
        }
        SyntaxNode result = Context.Conditional(Context.NullCondition(leftExpression, capturedLeft, true),
            isDelete ? F.NewKeywordExpression(K.TrueKeyword) : Context.VoidZero(), isDelete ? F.NewDeleteExpression(right) : right);
        EmitContext.CopyRange(result, node);
        if (receiver is not null) result = F.NewSyntheticReferenceExpression(result, receiver);
        Context.SetOriginal(result, node);
        return result;
    }

    private SyntaxNode RestorePartials(SyntaxNode original, SyntaxNode expression)
    {
        var wrappers = new Stack<PartiallyEmittedExpressionNode>();
        while (original is PartiallyEmittedExpressionNode partial) { wrappers.Push(partial); original = partial.Expression!; }
        while (wrappers.TryPop(out var wrapper))
        {
            var updated = Context.Clone(wrapper);
            updated.Expression = expression;
            expression = updated;
        }
        return expression;
    }

    private IdentifierNode Temp()
    {
        var name = Context.NewTempVariable();
        Context.AddVariableDeclaration(name);
        return name;
    }
    private SyntaxNode Assignment(SyntaxNode left, SyntaxNode right) => Context.Binary(left, K.EqualsToken, right);
    private static bool Optional(SyntaxNode node) => (node.Flags & NodeFlags.OptionalChain) != 0;
    private static SyntaxNode? QuestionDot(SyntaxNode node) => node switch
    {
        CallExpressionNode n => n.QuestionDotToken, PropertyAccessExpressionNode n => n.QuestionDotToken,
        ElementAccessExpressionNode n => n.QuestionDotToken, _ => null
    };
}

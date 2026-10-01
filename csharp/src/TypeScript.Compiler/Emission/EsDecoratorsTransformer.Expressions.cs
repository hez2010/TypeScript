using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class EsDecoratorsTransformer
{
    private bool SuperActive => classThis is not null && classSuper is not null;
    private static bool SuperProperty(SyntaxNode? node) => node is PropertyAccessExpressionNode { Expression.Kind: K.SuperKeyword }
        or ElementAccessExpressionNode { Expression.Kind: K.SuperKeyword };
    private IdentifierNode Temp()
    {
        var name = Context.NewTempVariable();
        Context.AddVariableDeclaration(name);
        return name;
    }
    private T Original<T>(T result, SyntaxNode original) where T : SyntaxNode
    { Context.SetOriginal(result, original); return EmitContext.CopyRange(result, original); }
    private SyntaxNode ReflectGet(SyntaxNode key) => Context.MethodCall(F.NewIdentifier("Reflect"u8), "get"u8, [classSuper!, key, classThis!]);
    private SyntaxNode ReflectSet(SyntaxNode key, SyntaxNode value) => Context.MethodCall(F.NewIdentifier("Reflect"u8), "set"u8, [classSuper!, key, value, classThis!]);
    private ValueTask<SyntaxNode?> SuperKeyAsync(SyntaxNode node) => node is PropertyAccessExpressionNode { Name: IdentifierNode name }
        ? ValueTask.FromResult<SyntaxNode?>(Context.StringLiteralFromNode(name)) : VisitAsync(((ElementAccessExpressionNode)node).ArgumentExpression);
    private async ValueTask<SyntaxNode?> DiscardedAsync(SyntaxNode? node)
    {
        var saved = discarded; discarded = true;
        try { return await VisitAsync(node); }
        finally { discarded = saved; }
    }
    private async ValueTask<SyntaxNode?> AccessAsync(SyntaxNode node)
    {
        if (SuperActive && SuperProperty(node) && await SuperKeyAsync(node) is { } key)
            return Original(ReflectGet(key), node is PropertyAccessExpressionNode property ? property.Expression! : ((ElementAccessExpressionNode)node).Expression!);
        return await VisitEachChildAsync(node);
    }
    private async ValueTask<SyntaxNode?> CallAsync(CallExpressionNode node)
    {
        if (classThis is not null && SuperProperty(TransformSyntax.SkipParentheses(node.Expression!)))
            return Original(Context.MethodCall((await VisitAsync(node.Expression))!, "call"u8, [classThis, .. (await VisitListAsync(node.Arguments)) ?? new([])]), node);
        return await VisitEachChildAsync(node);
    }
    private async ValueTask<SyntaxNode?> TagAsync(TaggedTemplateExpressionNode node)
    {
        if (classThis is not null && SuperProperty(TransformSyntax.SkipParentheses(node.Tag!)))
        {
            var result = Context.Clone(node);
            result.Tag = Original(Context.MethodCall((await VisitAsync(node.Tag))!, "bind"u8, [classThis]), node);
            result.TypeArguments = null; result.QuestionDotToken = null;
            result.Template = await VisitAsync(node.Template);
            return result;
        }
        return await VisitEachChildAsync(node);
    }
    private async ValueTask<SyntaxNode?> BinaryAsync(BinaryExpressionNode node, bool discard)
    {
        if (node is { Left: ArrayLiteralExpressionNode or ObjectLiteralExpressionNode, OperatorToken.Kind: K.EqualsToken })
        {
            var result = Context.Clone(node);
            result.Left = await AssignmentTargetAsync(node.Left);
            result.Right = await VisitAsync(node.Right);
            return result;
        }
        if (AssignName(node) is BinaryExpressionNode assigned && assigned != node) return await VisitEachChildAsync(assigned);
        if (node.OperatorToken?.Kind is >= K.FirstAssignment and <= K.LastAssignment && SuperActive && SuperProperty(node.Left)
            && await SuperKeyAsync(node.Left!) is { } key)
        {
            if (node.OperatorToken.Kind is K.BarBarEqualsToken or K.AmpersandAmpersandEqualsToken or K.QuestionQuestionEqualsToken)
            {
                var captured = key;
                if (!Inlineable(key)) { key = Temp(); captured = Assign(key, captured); }
                var read = ReflectGet(captured);
                var right = (await VisitAsync(node.Right))!;
                var result = discard ? null : Temp();
                if (result is not null) right = Assign(result, right);
                SyntaxNode write = ReflectSet(key, right);
                if (result is not null) write = Context.Binary(write, K.CommaToken, result);
                return Original(Context.Binary(read, NonAssignment(node.OperatorToken.Kind), F.NewParenthesizedExpression(write)), node);
            }
            var value = (await VisitAsync(node.Right))!;
            if (node.OperatorToken.Kind != K.EqualsToken)
            {
                var readKey = key;
                if (!Inlineable(key)) { readKey = Temp(); key = Assign(readKey, key); }
                var read = Original(ReflectGet(readKey), node.Left!);
                value = EmitContext.CopyRange(Context.Binary(read, NonAssignment(node.OperatorToken.Kind), value), node);
            }
            var temporary = discard ? null : Temp();
            if (temporary is not null) value = EmitContext.CopyRange(Assign(temporary, value), node);
            var set = Original(ReflectSet(key, value), node);
            return temporary is null ? set : EmitContext.CopyRange(Context.Binary(set, K.CommaToken, temporary), node);
        }
        if (node.OperatorToken?.Kind == K.CommaToken)
        {
            var result = Context.Clone(node);
            result.Left = await DiscardedAsync(node.Left);
            result.Right = discard ? await DiscardedAsync(node.Right) : await VisitAsync(node.Right);
            return result;
        }
        return await VisitEachChildAsync(node);
    }
    private async ValueTask<SyntaxNode?> UpdateAsync(SyntaxNode node, bool discard)
    {
        var op = node is PrefixUnaryExpressionNode prefix ? prefix.Operator : ((PostfixUnaryExpressionNode)node).Operator;
        var operand = node is PrefixUnaryExpressionNode pre ? pre.Operand! : ((PostfixUnaryExpressionNode)node).Operand!;
        var target = TransformSyntax.SkipParentheses(operand);
        if (op is K.PlusPlusToken or K.MinusMinusToken && SuperActive && SuperProperty(target) && await SuperKeyAsync(target) is { } key)
        {
            var readKey = key;
            if (!Inlineable(key)) { readKey = Temp(); key = Assign(readKey, key); }
            var read = Original(ReflectGet(readKey), node);
            var result = discard ? null : Temp();
            var temp = Temp();
            var assignment = EmitContext.CopyRange(Assign(temp, read), operand);
            SyntaxNode operation = node is PrefixUnaryExpressionNode ? F.NewPrefixUnaryExpression(op, temp) : F.NewPostfixUnaryExpression(temp, op);
            EmitContext.CopyRange(operation, node);
            if (result is not null) operation = EmitContext.CopyRange(Assign(result, operation), node);
            SyntaxNode value = EmitContext.CopyRange(Context.Binary(assignment, K.CommaToken, operation), node);
            if (node is PostfixUnaryExpressionNode) value = EmitContext.CopyRange(Context.Binary(value, K.CommaToken, temp), node);
            var set = Original(ReflectSet(key, value), node);
            return result is null ? set : EmitContext.CopyRange(Context.Binary(set, K.CommaToken, result), node);
        }
        return await VisitEachChildAsync(node);
    }
    private async ValueTask<SyntaxNode> AssignmentTargetAsync(SyntaxNode node)
    {
        Cancellation.ThrowIfCancellationRequested();
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (SuperActive && SuperProperty(node) && await SuperKeyAsync(node) is { } key)
        {
            var parameter = Context.NewTempVariable();
            var setter = F.NewSetAccessorDeclaration(null, F.NewIdentifier("value"u8), null,
                new([F.NewParameterDeclaration(null, null, parameter, null, null, null)]), null, null,
                F.NewBlock(new([F.NewExpressionStatement(ReflectSet(key, parameter))]), false));
            return Original(F.NewPropertyAccessExpression(F.NewParenthesizedExpression(Object([setter])), null, F.NewIdentifier("value"u8), NodeFlags.None), node);
        }
        switch (node)
        {
            case ArrayLiteralExpressionNode array:
                var elements = new List<SyntaxNode>();
                foreach (var element in array.Elements ?? new([])) elements.Add(await AssignmentTargetAsync(element));
                var updatedArray = Context.Clone(array); updatedArray.Elements = new(elements.ToArray(), array.Elements?.Pos ?? -1, array.Elements?.End ?? -1, false, array.Elements?.HasTrailingComma ?? false); return updatedArray;
            case ObjectLiteralExpressionNode obj:
                var properties = new List<SyntaxNode>();
                foreach (var property in obj.Properties ?? new([])) properties.Add(await AssignmentTargetAsync(property));
                var updatedObject = Context.Clone(obj); updatedObject.Properties = new(properties.ToArray(), obj.Properties?.Pos ?? -1, obj.Properties?.End ?? -1, false, obj.Properties?.HasTrailingComma ?? false); return updatedObject;
            case PropertyAssignmentNode property:
                var updatedProperty = Context.Clone(property); updatedProperty.Name = await VisitAsync(property.Name); updatedProperty.Initializer = await AssignmentTargetAsync(property.Initializer!); return updatedProperty;
            case ShorthandPropertyAssignmentNode shorthand:
                return (await VisitEachChildAsync(AssignName(shorthand)))!;
            case SpreadElementNode spread:
                var updatedSpread = Context.Clone(spread); updatedSpread.Expression = await AssignmentTargetAsync(spread.Expression!); return updatedSpread;
            case SpreadAssignmentNode spread:
                var updatedRest = Context.Clone(spread); updatedRest.Expression = await AssignmentTargetAsync(spread.Expression!); return updatedRest;
            case BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken } binary:
                binary = (BinaryExpressionNode)AssignName(binary);
                var updatedBinary = Context.Clone(binary); updatedBinary.Left = await AssignmentTargetAsync(binary.Left!); updatedBinary.Right = await VisitAsync(binary.Right); return updatedBinary;
            default: return (await VisitEachChildAsync(node))!;
        }
    }
    private static K NonAssignment(K kind) => kind switch
    {
        K.PlusEqualsToken => K.PlusToken, K.MinusEqualsToken => K.MinusToken, K.AsteriskEqualsToken => K.AsteriskToken,
        K.AsteriskAsteriskEqualsToken => K.AsteriskAsteriskToken, K.SlashEqualsToken => K.SlashToken, K.PercentEqualsToken => K.PercentToken,
        K.LessThanLessThanEqualsToken => K.LessThanLessThanToken, K.GreaterThanGreaterThanEqualsToken => K.GreaterThanGreaterThanToken,
        K.GreaterThanGreaterThanGreaterThanEqualsToken => K.GreaterThanGreaterThanGreaterThanToken, K.AmpersandEqualsToken => K.AmpersandToken,
        K.BarEqualsToken => K.BarToken, K.CaretEqualsToken => K.CaretToken, K.BarBarEqualsToken => K.BarBarToken,
        K.AmpersandAmpersandEqualsToken => K.AmpersandAmpersandToken, K.QuestionQuestionEqualsToken => K.QuestionQuestionToken,
        _ => throw new InvalidOperationException("Expected a compound assignment")
    };
}

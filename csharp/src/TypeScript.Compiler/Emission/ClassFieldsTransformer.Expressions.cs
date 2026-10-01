using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class ClassFieldsTransformer
{
    private bool StaticSuper => LowerPrivate && StaticInitializer(classElement) && environment is not null;
    private static bool SuperProperty(SyntaxNode? node) => node is PropertyAccessExpressionNode { Expression.Kind: K.SuperKeyword }
        or ElementAccessExpressionNode { Expression.Kind: K.SuperKeyword };
    private IdentifierNode Temp()
    {
        var temp = Context.NewTempVariable();
        Context.AddVariableDeclaration(temp);
        return temp;
    }
    private T Original<T>(T result, SyntaxNode original) where T : SyntaxNode
    {
        Context.SetOriginal(result, original);
        return EmitContext.CopyRange(result, original);
    }
    private SyntaxNode ReflectGet(SyntaxNode key) => Context.MethodCall(F.NewIdentifier("Reflect"u8), "get"u8, [environment!.Super!, key, environment.Constructor!]);
    private SyntaxNode ReflectSet(SyntaxNode key, SyntaxNode value) => Context.MethodCall(F.NewIdentifier("Reflect"u8), "set"u8, [environment!.Super!, key, value, environment.Constructor!]);
    private ValueTask<SyntaxNode?> SuperKeyAsync(SyntaxNode node) => node is PropertyAccessExpressionNode { Name: IdentifierNode name }
        ? ValueTask.FromResult<SyntaxNode?>(F.NewStringLiteral(name.Text, TokenFlags.None)) : VisitAsync(((ElementAccessExpressionNode)node).ArgumentExpression);

    private async ValueTask<NodeList?> HeritageAsync(NodeList? clauses, bool capture)
    {
        if (!capture || clauses is null) return await VisitListAsync(clauses);
        List<SyntaxNode> result = [];
        foreach (var clause in clauses)
        {
            var updated = Context.Clone((HeritageClauseNode)clause);
            List<SyntaxNode> types = [];
            foreach (var type in updated.Types ?? new([]))
            {
                var expression = Context.Clone((ExpressionWithTypeArgumentsNode)type);
                var temp = Context.NewTempVariable(new(GeneratedIdentifierFlags.ReservedInNestedScopes));
                Context.AddVariableDeclaration(temp);
                environment!.Super = temp;
                expression.Expression = Assign(temp, (await VisitAsync(expression.Expression))!);
                expression.TypeArguments = null;
                types.Add(expression);
            }
            updated.Types = new(types.ToArray(), updated.Types?.Pos ?? -1, updated.Types?.End ?? -1);
            result.Add(updated);
        }
        return new(result.ToArray(), clauses.Pos, clauses.End);
    }

    private async ValueTask<SyntaxNode?> VisitDiscardedAsync(SyntaxNode? node)
    {
        bool saved = discarded;
        discarded = true;
        try { return await VisitAsync(node); }
        finally { discarded = saved; }
    }
    private async ValueTask<SyntaxNode> ForAsync(ForStatementNode node)
    {
        var result = Context.Clone(node);
        result.Initializer = await VisitDiscardedAsync(node.Initializer);
        result.Condition = await VisitAsync(node.Condition);
        result.Incrementor = await VisitDiscardedAsync(node.Incrementor);
        iteration = true;
        result.Statement = await VisitIterationBodyAsync(node.Statement);
        return result;
    }

    private async ValueTask<SyntaxNode?> AccessAsync(SyntaxNode node)
    {
        if (node is PropertyAccessExpressionNode { Name: PrivateIdentifierNode name } access && Private(name) is { } info)
            return Original(PrivateGet(info, (await VisitAsync(access.Expression))!), node);
        if (StaticSuper && SuperProperty(node))
        {
            if (environment!.Decorated) return await InvalidSuperAsync(node);
            if (environment is { Constructor: not null, Super: not null } && await SuperKeyAsync(node) is { } key)
                return Original(ReflectGet(key), node is PropertyAccessExpressionNode property ? property.Expression! : ((ElementAccessExpressionNode)node).Expression!);
        }
        return await VisitEachChildAsync(node);
    }
    private async ValueTask<SyntaxNode> InvalidSuperAsync(SyntaxNode node)
    {
        if (node is PropertyAccessExpressionNode property)
        {
            var result = Context.Clone(property);
            result.Expression = Context.VoidZero(); result.QuestionDotToken = null;
            return result;
        }
        var indexed = Context.Clone((ElementAccessExpressionNode)node);
        indexed.Expression = Context.VoidZero(); indexed.QuestionDotToken = null;
        indexed.ArgumentExpression = await VisitAsync(indexed.ArgumentExpression);
        return indexed;
    }
    private async ValueTask<SyntaxNode?> CallAsync(CallExpressionNode node)
    {
        var expression = TransformSyntax.SkipParentheses(node.Expression!);
        if (expression is PropertyAccessExpressionNode { Name: PrivateIdentifierNode name } access && Private(name) is not null)
        {
            var (thisArg, target) = PrivateCallBinding(access);
            var visited = (await VisitAsync(target))!;
            var privateReceiver = (await VisitAsync(thisArg))!;
            var args = await VisitListAsync(node.Arguments);
            var result = Context.Clone(node);
            result.Expression = F.NewPropertyAccessExpression(visited, (node.Flags & NodeFlags.OptionalChain) != 0 ? node.QuestionDotToken : null,
                F.NewIdentifier("call"u8), (node.Flags & NodeFlags.OptionalChain) != 0 ? NodeFlags.OptionalChain : NodeFlags.None);
            result.QuestionDotToken = null; result.TypeArguments = null; result.Arguments = new([privateReceiver, .. args ?? new([])]);
            return result;
        }
        if (StaticSuper && SuperProperty(expression) && environment?.Constructor is { } receiver)
            return Original(Context.MethodCall((await VisitAsync(node.Expression))!, "call"u8, [receiver, .. (await VisitListAsync(node.Arguments)) ?? new([])]), node);
        return await VisitEachChildAsync(node);
    }
    private async ValueTask<SyntaxNode?> TagAsync(TaggedTemplateExpressionNode node)
    {
        var tag = TransformSyntax.SkipParentheses(node.Tag!);
        if (tag is PropertyAccessExpressionNode { Name: PrivateIdentifierNode name } access && Private(name) is not null)
        {
            var (thisArg, target) = PrivateCallBinding(access);
            var result = Context.Clone(node);
            result.Tag = Context.MethodCall((await VisitAsync(target))!, "bind"u8, [(await VisitAsync(thisArg))!]);
            result.QuestionDotToken = null; result.TypeArguments = null; result.Template = await VisitAsync(node.Template);
            return result;
        }
        if (StaticSuper && SuperProperty(tag) && environment?.Constructor is { } receiver)
        {
            var result = Context.Clone(node);
            result.Tag = Original(Context.MethodCall((await VisitAsync(node.Tag))!, "bind"u8, [receiver]), node);
            result.QuestionDotToken = null; result.TypeArguments = null;
            result.Template = await VisitAsync(node.Template);
            return result;
        }
        return await VisitEachChildAsync(node);
    }

    private async ValueTask<SyntaxNode?> UpdateAsync(SyntaxNode node, bool discard)
    {
        var op = node is PrefixUnaryExpressionNode prefix ? prefix.Operator : ((PostfixUnaryExpressionNode)node).Operator;
        var operand = node is PrefixUnaryExpressionNode pre ? pre.Operand! : ((PostfixUnaryExpressionNode)node).Operand!;
        var target = TransformSyntax.SkipParentheses(operand);
        if (op is K.PlusPlusToken or K.MinusMinusToken && target is PropertyAccessExpressionNode { Name: PrivateIdentifierNode name } access && Private(name) is { } info)
        {
            var (read, initialize) = CopiableReceiver((await VisitAsync(access.Expression))!);
            var temp = node is PostfixUnaryExpressionNode && !discard ? Temp() : null;
            var value = ExpandUpdate(node, PrivateGet(info, read), temp);
            var set = Original(await PrivateSetAsync(info, initialize ?? read, value, K.EqualsToken), node);
            return temp is null ? set : EmitContext.CopyRange(Context.Binary(set, K.CommaToken, temp), node);
        }
        if (op is K.PlusPlusToken or K.MinusMinusToken && StaticSuper && SuperProperty(target))
        {
            if (environment!.Decorated)
            {
                var invalid = await InvalidSuperAsync(target);
                var result = Context.Clone(node);
                if (result is PrefixUnaryExpressionNode p) p.Operand = invalid;
                else ((PostfixUnaryExpressionNode)result).Operand = invalid;
                return result;
            }
            if (environment is { Constructor: not null, Super: not null } && await SuperKeyAsync(target) is { } key)
            {
                SyntaxNode readKey = key, writeKey = key;
                if (!Inlineable(key)) { readKey = Temp(); writeKey = Assign(readKey, key); }
                var read = EmitContext.CopyRange(ReflectGet(readKey), target);
                var temp = discard ? null : Temp();
                var value = ExpandUpdate(node, read, temp);
                var set = Original(ReflectSet(writeKey, value), node);
                return temp is null ? set : EmitContext.CopyRange(Context.Binary(set, K.CommaToken, temp), node);
            }
        }
        return await VisitEachChildAsync(node);
    }
    private SyntaxNode ExpandUpdate(SyntaxNode node, SyntaxNode value, IdentifierNode? result)
    {
        var operand = node is PrefixUnaryExpressionNode prefix ? prefix.Operand! : ((PostfixUnaryExpressionNode)node).Operand!;
        var temp = Temp();
        var assignment = EmitContext.CopyRange(Assign(temp, value), operand);
        SyntaxNode operation = node is PrefixUnaryExpressionNode pre ? F.NewPrefixUnaryExpression(pre.Operator, temp)
            : F.NewPostfixUnaryExpression(temp, ((PostfixUnaryExpressionNode)node).Operator);
        EmitContext.CopyRange(operation, node);
        if (result is not null) operation = EmitContext.CopyRange(Assign(result, operation), node);
        SyntaxNode sequence = EmitContext.CopyRange(Context.Binary(assignment, K.CommaToken, operation), node);
        if (node is PostfixUnaryExpressionNode) sequence = EmitContext.CopyRange(Context.Binary(sequence, K.CommaToken, temp), node);
        return sequence;
    }

    private async ValueTask<SyntaxNode?> BinaryAsync(BinaryExpressionNode node, bool discard)
    {
        if (named.Applies(node) && NamedInitializer(node) is { } initializer
            && NamedEvaluation.SkipOuter(initializer) is ClassExpressionNode anonymous && NeedsAssignedName(anonymous))
            node = (BinaryExpressionNode)named.Transform(node);
        if (node.OperatorToken?.Kind is K.AmpersandAmpersandEqualsToken or K.BarBarEqualsToken or K.QuestionQuestionEqualsToken
            && (StaticSuper && SuperProperty(node.Left) && environment is { Constructor: not null, Super: not null, Decorated: false }
                || NamedEvaluation.SkipOuter(node.Left!) is PropertyAccessExpressionNode { Name: PrivateIdentifierNode logicalName } && Private(logicalName) is not null))
            return await LogicalMemberAssignmentAsync(node, discard);
        if (node is { OperatorToken.Kind: K.EqualsToken, Left: ObjectLiteralExpressionNode or ArrayLiteralExpressionNode })
        {
            var saved = pendingTargets;
            pendingTargets = [];
            try
            {
                var assignment = Context.Clone(node);
                assignment.Left = await AssignmentTargetAsync(node.Left);
                assignment.Right = await VisitAsync(node.Right);
                return pendingTargets.Count == 0 ? assignment : Context.InlineExpressions([.. pendingTargets, assignment]);
            }
            finally { pendingTargets = saved; }
        }
        if (node.OperatorToken?.Kind is >= K.FirstAssignment and <= K.LastAssignment
            && NamedEvaluation.SkipOuter(node.Left!) is PropertyAccessExpressionNode { Name: PrivateIdentifierNode name } access && Private(name) is { } info)
            return Original(await PrivateSetAsync(info, access.Expression!, node.Right!, node.OperatorToken.Kind), node);
        if (node is { OperatorToken.Kind: K.InKeyword, Left: PrivateIdentifierNode privateName } && Private(privateName) is { } privateInfo)
        {
            var result = Context.HelperCall(EmitHelpers.ClassPrivateFieldIn, "__classPrivateFieldIn"u8, [privateInfo.Brand!, (await VisitAsync(node.Right))!]);
            Context.SetOriginal(result, node);
            return result;
        }
        if (node.OperatorToken?.Kind is >= K.FirstAssignment and <= K.LastAssignment && StaticSuper && SuperProperty(node.Left))
        {
            if (environment!.Decorated)
            {
                var invalid = Context.Clone(node);
                invalid.Left = await InvalidSuperAsync(node.Left!); invalid.Right = await VisitAsync(node.Right);
                return invalid;
            }
            if (environment is { Constructor: not null, Super: not null } && await SuperKeyAsync(node.Left!) is { } key)
            {
                var value = (await VisitAsync(node.Right))!;
                if (node.OperatorToken.Kind != K.EqualsToken)
                {
                    var readKey = key;
                    if (!Inlineable(key)) { readKey = Temp(); key = Assign(readKey, key); }
                    var read = Original(ReflectGet(readKey), node.Left!);
                    value = EmitContext.CopyRange(Context.Binary(read, NonAssignment(node.OperatorToken.Kind), value), node);
                }
                var temp = discard ? null : Temp();
                if (temp is not null) value = EmitContext.CopyRange(Assign(temp, value), node);
                var set = Original(ReflectSet(key, value), node);
                return temp is null ? set : EmitContext.CopyRange(Context.Binary(set, K.CommaToken, temp), node);
            }
        }
        return await VisitEachChildAsync(node);
    }

    private async ValueTask<SyntaxNode> LogicalMemberAssignmentAsync(BinaryExpressionNode node, bool discard)
    {
        var left = NamedEvaluation.SkipOuter(node.Left!);
        SyntaxNode read, write;
        if (left is PropertyAccessExpressionNode { Name: PrivateIdentifierNode name } access)
        {
            var info = Private(name)!;
            var (receiver, initialize) = CopiableReceiver((await VisitAsync(access.Expression))!);
            read = PrivateGet(info, initialize ?? receiver);
            write = await PrivateSetAsync(info, receiver, node.Right!, K.EqualsToken);
        }
        else
        {
            var key = (await SuperKeyAsync(left))!;
            var captured = key;
            if (!Inlineable(key)) { key = Temp(); captured = Assign(key, captured); }
            read = ReflectGet(captured);
            var value = (await VisitAsync(node.Right))!;
            var result = discard ? null : Temp();
            write = ReflectSet(key, result is null ? value : Assign(result, value));
            if (result is not null) write = Context.Binary(write, K.CommaToken, result);
        }
        // Keep the write inside the short-circuit branch. The setter must not run when the read decides the result.
        return Original(Context.Binary(read, NonAssignment(node.OperatorToken!.Kind), F.NewParenthesizedExpression(write)), node);
    }

    private async ValueTask<SyntaxNode?> AssignmentTargetAsync(SyntaxNode node)
    {
        Cancellation.ThrowIfCancellationRequested();
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (node is PropertyAccessExpressionNode { Name: PrivateIdentifierNode name } access && Private(name) is { } info)
        {
            var parameter = Context.NewGeneratedNameForNode(node);
            var receiver = access.Expression!;
            if (receiver.Kind is K.ThisKeyword or K.SuperKeyword || !TransformSyntax.SimpleCopiable(receiver))
            {
                var temp = Context.NewTempVariable(new(GeneratedIdentifierFlags.ReservedInNestedScopes));
                Context.AddVariableDeclaration(temp);
                pendingTargets.Add(Assign(temp, (await VisitAsync(receiver))!));
                receiver = temp;
            }
            return AssignmentWrapper(parameter, await PrivateSetAsync(info, receiver, parameter, K.EqualsToken));
        }
        if (StaticSuper && SuperProperty(node))
        {
            if (environment!.Decorated) return await InvalidSuperAsync(node);
            if (environment is { Constructor: not null, Super: not null } && await SuperKeyAsync(node) is { } key)
            {
                var temp = Context.NewTempVariable();
                return AssignmentWrapper(temp, ReflectSet(key, temp));
            }
        }
        switch (node)
        {
            case ArrayLiteralExpressionNode array:
                var updatedArray = Context.Clone(array);
                updatedArray.Elements = await AssignmentTargetsAsync(array.Elements);
                return updatedArray;
            case ObjectLiteralExpressionNode obj:
                var updatedObject = Context.Clone(obj);
                updatedObject.Properties = await AssignmentTargetsAsync(obj.Properties);
                return updatedObject;
            case PropertyAssignmentNode property:
                var updatedProperty = Context.Clone(property);
                updatedProperty.Name = await VisitAsync(property.Name);
                updatedProperty.Initializer = await AssignmentTargetAsync(property.Initializer!);
                return updatedProperty;
            case SpreadElementNode spread:
                var updatedSpread = Context.Clone(spread);
                updatedSpread.Expression = await AssignmentTargetAsync(spread.Expression!);
                return updatedSpread;
            case SpreadAssignmentNode spread:
                var updatedRest = Context.Clone(spread);
                updatedRest.Expression = await AssignmentTargetAsync(spread.Expression!);
                return updatedRest;
            case BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken } assignment:
                if (named.Applies(assignment) && NamedEvaluation.SkipOuter(assignment.Right!) is ClassExpressionNode anonymous && NeedsAssignedName(anonymous))
                    assignment = (BinaryExpressionNode)named.Transform(assignment);
                var updatedAssignment = Context.Clone(assignment);
                updatedAssignment.Left = await AssignmentTargetAsync(assignment.Left!);
                updatedAssignment.Right = await VisitAsync(assignment.Right);
                return updatedAssignment;
            default: return await VisitEachChildAsync(node);
        }
    }
    private async ValueTask<NodeList?> AssignmentTargetsAsync(NodeList? nodes)
    {
        if (nodes is null) return null;
        List<SyntaxNode> result = [];
        foreach (var node in nodes) result.Add((await AssignmentTargetAsync(node))!);
        return new(result.ToArray(), nodes.Pos, nodes.End, nodes.IsMissing, nodes.HasTrailingComma);
    }
    private SyntaxNode AssignmentWrapper(IdentifierNode parameter, SyntaxNode expression)
    {
        var setter = F.NewSetAccessorDeclaration(null, F.NewIdentifier("value"u8), null,
            new([F.NewParameterDeclaration(null, null, parameter, null, null, null)]), null, null,
            F.NewBlock(new([F.NewExpressionStatement(expression)]), false));
        return F.NewPropertyAccessExpression(F.NewParenthesizedExpression(F.NewObjectLiteralExpression(new([setter]), false)), null, F.NewIdentifier("value"u8), NodeFlags.None);
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

using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IFunctionBodyHost
{
    ValueTask<Type> CachedExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<bool> ConstantReferenceAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> PromiseResultAsync(SyntaxNode node, Type type, bool reportMissing, CancellationToken cancellation);

    ValueTask<Type?> UnwrapReturnAsync(SyntaxNode node, Type type, CancellationToken cancellation);

    ValueTask<(IReadOnlyList<Type> Yield, IReadOnlyList<Type> Next)> YieldTypesAsync(
        SyntaxNode node,
        CheckMode mode,
        CancellationToken cancellation);

    ValueTask<Type> GeneratorResultAsync(SyntaxNode node, Type yield, Type result, Type? next, CancellationToken cancellation);

    ValueTask<Type> WidenIterationAsync(SyntaxNode node, Type type, Type? contextual, int kind, CancellationToken cancellation);

    ValueTask ReportReturnWideningAsync(SyntaxNode node, Type type, WideningKind kind, CancellationToken cancellation);

    FlowNode? FlowOf(SyntaxNode node);
}

internal sealed class FunctionBodies(TypeContext context, CheckerSymbols symbols, TypeAlgebra algebra, TypeViews views,
    TypeWidening widening, ExpressionContexts expressions, FunctionContexts contexts, Signatures signatures,
    SymbolTypes values, AwaitedTypes awaited, FlowTypes flows, AssignmentMarks assignments, IFunctionBodyHost host)
{
    internal async ValueTask<Type> ReturnAsync(SyntaxNode node, CheckMode mode = 0, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var body = SemanticSyntax.Body(node);
        if (body is null)
            return context.ErrorType;
        bool async = SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword), generator = SemanticSyntax.Generator(node);
        Type? result = null, yield = null, next = null;
        Type fallback = context.VoidType;
        if (body is not BlockNode)
        {
            result = await host.CachedExpressionAsync(body, mode & ~CheckMode.SkipGenericFunctions, cancellation).ConfigureAwait(false);
            if (await expressions.ConstAsync(body, cancellation).ConfigureAwait(false))
                result = await algebra.RegularTypeAsync(result, cancellation).ConfigureAwait(false);
            if (async)
                result = await AwaitReturnAsync(result, node, cancellation).ConfigureAwait(false);
        }
        else
        {
            var (types, never) = await AggregateAsync(node, mode, cancellation).ConfigureAwait(false);
            if (generator)
            {
                if (never)
                    fallback = context.NeverType;
                else if (types.Count != 0)
                    result = await algebra.UnionAsync(types, UnionReduction.Subtype, cancellation: cancellation).ConfigureAwait(false);
                var yields = await host.YieldTypesAsync(node, mode, cancellation).ConfigureAwait(false);
                if (yields.Yield.Count != 0)
                    yield = await algebra.UnionAsync(
                        yields.Yield,
                        UnionReduction.Subtype,
                        cancellation: cancellation).ConfigureAwait(false);
                if (yields.Next.Count != 0)
                    next = await algebra.IntersectionAsync(yields.Next, cancellation: cancellation).ConfigureAwait(false);
            }
            else
            {
                if (never)
                    return async
                        ? await host.PromiseResultAsync(node, context.NeverType, true, cancellation).ConfigureAwait(false)
                        : context.NeverType;
                if (types.Count == 0)
                {
                    var contextual = await contexts.ReturnAsync(node, cancellation: cancellation).ConfigureAwait(false);
                    var unwrapped = contextual is not null
                        ? await host.UnwrapReturnAsync(node, contextual, cancellation).ConfigureAwait(false)
                        : null;
                    Type empty = unwrapped is not null
                        && (unwrapped is UnionType union ? union.Types : [unwrapped]).Any(t => (t.Flags & TypeFlags.Undefined) != 0)
                        ? context.UndefinedType : context.VoidType;
                    return async ? await host.PromiseResultAsync(node, empty, true, cancellation).ConfigureAwait(false) : empty;
                }
                result = await algebra.UnionAsync(types, UnionReduction.Subtype, cancellation: cancellation).ConfigureAwait(false);
            }
        }
        if (yield is not null)
            await host.ReportReturnWideningAsync(node, yield, WideningKind.GeneratorYield, cancellation).ConfigureAwait(false);
        if (result is not null)
            await host.ReportReturnWideningAsync(node, result, WideningKind.FunctionReturn, cancellation).ConfigureAwait(false);
        if (next is not null)
            await host.ReportReturnWideningAsync(node, next, WideningKind.GeneratorNext, cancellation).ConfigureAwait(false);
        if (Unit(result) || Unit(yield) || Unit(next))
        {
            var contextualSignature = await contexts.GetAsync(node, cancellation).ConfigureAwait(false);
            Type? contextual = null;
            if (contextualSignature is not null)
                contextual = contextualSignature == await signatures.FromDeclarationAsync(node, cancellation).ConfigureAwait(false)
                    ? generator ? null : result
                    : await expressions.InstantiateAsync(
                        await signatures.ReturnAsync(contextualSignature, cancellation).ConfigureAwait(false),
                        node,
                        0,
                        cancellation).ConfigureAwait(false);
            if (generator)
            {
                if (yield is not null)
                    yield = await host.WidenIterationAsync(node, yield, contextual, 0, cancellation).ConfigureAwait(false);
                if (result is not null)
                    result = await host.WidenIterationAsync(node, result, contextual, 1, cancellation).ConfigureAwait(false);
                if (next is not null)
                    next = await host.WidenIterationAsync(node, next, contextual, 2, cancellation).ConfigureAwait(false);
            }
            else if (result is not null && Unit(result))
                result = await expressions.WidenLiteralAsync(result, contextual is not null && async
                    ? (await awaited.PromisedAsync(contextual, cancellation: cancellation).ConfigureAwait(false)).Type : contextual,
                    cancellation).ConfigureAwait(false);
        }
        if (yield is not null)
            yield = await widening.GetAsync(yield, cancellation).ConfigureAwait(false);
        if (result is not null)
            result = await widening.GetAsync(result, cancellation).ConfigureAwait(false);
        if (next is not null)
            next = await widening.GetAsync(next, cancellation).ConfigureAwait(false);
        result ??= fallback;
        if (generator)
            return await host.GeneratorResultAsync(node, yield ?? context.NeverType, result, next, cancellation).ConfigureAwait(false);
        return async ? await host.PromiseResultAsync(node, result, false, cancellation).ConfigureAwait(false) : result;
    }

    private static bool Unit(Type? type) => type is not null && (type.Flags & TypeFlags.Unit) != 0;

    private async ValueTask<Type> AwaitReturnAsync(Type type, SyntaxNode node, CancellationToken cancellation) =>
            await awaited.UnwrapAsync(
                await awaited.GetAsync(type, false, node, 1058, cancellation).ConfigureAwait(false) ?? context.ErrorType,
                cancellation).ConfigureAwait(false);

    private async ValueTask<(IReadOnlyList<Type> Types, bool Never)> AggregateAsync(
        SyntaxNode node,
        CheckMode mode,
        CancellationToken cancellation)
    {
        var types = new List<Type>();
        bool empty = await ImplicitAsync(node, cancellation).ConfigureAwait(false), never = false;
        bool async = SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword);
        foreach (var statement in FunctionSyntax.Returns(SemanticSyntax.Body(node)))
        {
            cancellation.ThrowIfCancellationRequested();
            if (statement.Expression is null)
            {
                empty = true;
                continue;
            }
            var expression = Skip(statement.Expression);
            if (async && expression is AwaitExpressionNode awaitExpression)
                expression = Skip(awaitExpression.Expression!);
            if (expression is CallExpressionNode { Expression: IdentifierNode callee }
                && (await host.CachedExpressionAsync(callee, 0, cancellation).ConfigureAwait(false)).Symbol
                    == symbols.Merger.GetMergedSymbol(symbols.Binding(node)?.Get(node)?.Symbol)
                && (symbols.Binding(node)?.Get(node)?.Symbol?.ValueDeclaration is not (FunctionExpressionNode or ArrowFunctionNode)
                    || await host.ConstantReferenceAsync(callee, cancellation).ConfigureAwait(false)))
            {
                never = true;
                continue;
            }
            var type = await host.CachedExpressionAsync(
                expression,
                mode & ~CheckMode.SkipGenericFunctions,
                cancellation).ConfigureAwait(false);
            if (async)
                type = await AwaitReturnAsync(type, node, cancellation).ConfigureAwait(false);
            if ((type.Flags & TypeFlags.Never) != 0)
                never = true;
            if (await expressions.ConstAsync(expression, cancellation).ConfigureAwait(false))
                type = await algebra.RegularTypeAsync(type, cancellation).ConfigureAwait(false);
            if (!types.Contains(type))
                types.Add(type);
        }
        if (types.Count == 0 && !empty && (never || FunctionSyntax.Contextual(node)))
            return ([], true);
        if (context.StrictNullChecks && types.Count != 0 && empty && !types.Contains(context.UndefinedType))
            types.Add(context.UndefinedType);
        return (types, false);
    }

    internal async ValueTask<bool> ImplicitAsync(SyntaxNode node, CancellationToken cancellation = default) =>
        symbols.Binding(node)?.Get(node)?.EndFlow is { } end
            && await flows.Reachability.ReachableAsync(end, cancellation).ConfigureAwait(false);

    internal async ValueTask<TypePredicate?> PredicateAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        if (node is ConstructorDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode
            || SemanticSyntax.Generator(node) || SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword))
            return null;
        var body = SemanticSyntax.Body(node);
        SyntaxNode? expression = body is not null && body is not BlockNode ? body : null;
        if (expression is null)
        {
            foreach (var statement in FunctionSyntax.Returns(body))
            {
                if (expression is not null || statement.Expression is null)
                    return null;
                expression = statement.Expression;
            }
            if (expression is null || await ImplicitAsync(node, cancellation).ConfigureAwait(false))
                return null;
        }
        expression = Skip(expression);
        if (((await host.CachedExpressionAsync(expression, 0, cancellation).ConfigureAwait(false)).Flags & TypeFlags.Boolean) == 0)
            return null;
        var parameters = ((IFunctionSignature)node).Parameters!;
        for (int i = 0; i < parameters.Count; i++)
        {
            var parameter = (ParameterDeclarationNode)parameters[i];
            var symbol = symbols.Binding(parameter)!.Get(parameter)!.Symbol!;
            var initial = await values.GetAsync(symbol, cancellation).ConfigureAwait(false);
            if ((initial.Flags & TypeFlags.Boolean) != 0 || parameter.Name is not IdentifierNode name
                || await assignments.AssignedAsync(symbol, cancellation).ConfigureAwait(false) || parameter.DotDotDotToken is not null)
                continue;
            var antecedent = host.FlowOf(expression) ?? (expression.Parent is ReturnStatementNode ? host.FlowOf(expression.Parent) : null)
                ?? new FlowNode(FlowFlags.Start);
            var positive = new FlowNode(FlowFlags.TrueCondition, expression, antecedent);
            var trueType = await flows.GetAsync(name, initial, initial, node, positive, cancellation).ConfigureAwait(false);
            if (trueType == initial)
                continue;
            var negative = new FlowNode(FlowFlags.FalseCondition, expression, antecedent);
            var falseType = await views.ReducedAsync(
                await flows.GetAsync(name, initial, trueType, node, negative, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
            if ((falseType.Flags & TypeFlags.Never) != 0)
                return new(TypePredicateKind.Identifier, i, name.Text, trueType);
        }
        return null;
    }

    private static SyntaxNode Skip(SyntaxNode node)
    {
        while (node is ParenthesizedExpressionNode parentheses)
            node = parentheses.Expression!;
        return node;
    }
}

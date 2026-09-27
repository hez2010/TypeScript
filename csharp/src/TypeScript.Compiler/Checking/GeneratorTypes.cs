using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IGeneratorTypeHost
{
    ValueTask<Type> IterationGlobalAsync(TextSlice name, int arity, bool report, CancellationToken cancellation);

    ValueTask<Type> CheckGeneratorOperandAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<bool> ReportGeneratorReturnAsync(Type source, Type target, SyntaxNode node, CancellationToken cancellation);
}

internal sealed class GeneratorTypes(TypeContext context, TypeAlgebra algebra, IteratorProtocols protocols, IterationElements elements,
    AwaitedTypes awaited, ExpressionContexts expressions, FunctionContexts contexts, TypeRelations relations, IGeneratorTypeHost host)
{
    internal async ValueTask<(IReadOnlyList<Type> Yield, IReadOnlyList<Type> Next)> AggregateAsync(
        SyntaxNode function, CheckMode mode, CancellationToken cancellation = default)
    {
        var yields = new List<Type>();
        var nexts = new List<Type>();
        bool async = SemanticSyntax.HasModifier(function, SyntaxKind.AsyncKeyword);
        foreach (var node in FunctionSyntax.Yields(SemanticSyntax.Body(function)!))
        {
            cancellation.ThrowIfCancellationRequested();
            var type = node.Expression is { } operand
                ? await host.CheckGeneratorOperandAsync(operand, mode & ~CheckMode.SkipGenericFunctions, cancellation).ConfigureAwait(false)
                : context.UndefinedWideningType;
            if (node.Expression is { } expression && await expressions.ConstAsync(expression, cancellation).ConfigureAwait(false))
                type = await algebra.RegularTypeAsync(type, cancellation).ConfigureAwait(false);
            if (await YieldedAsync(node, type, context.AnyType, async, cancellation).ConfigureAwait(false) is { } yielded
                && !yields.Contains(yielded))
                yields.Add(yielded);
            var next = node.AsteriskToken is not null
                ? (await protocols.IterableAsync(
                    type,
                    async ? IterationUse.AsyncYieldStar : IterationUse.YieldStar,
                    node.Expression,
                    cancellation).ConfigureAwait(false)).Next
                : await expressions.GetAsync(node, cancellation: cancellation).ConfigureAwait(false);
            if (next is not null && !nexts.Contains(next))
                nexts.Add(next);
        }
        return (yields, nexts);
    }

    internal async ValueTask<Type?> YieldedAsync(
        YieldExpressionNode node,
        Type operand,
        Type sent,
        bool async,
        CancellationToken cancellation = default)
    {
        var location = node.Expression ?? node;
        var type = node.AsteriskToken is not null
            ? await elements.CheckAsync(
                async ? IterationUse.AsyncYieldStar : IterationUse.YieldStar,
                operand,
                sent,
                location,
                cancellation).ConfigureAwait(false)
            : operand;
        return async
            ? await awaited.GetAsync(
                type,
                true,
                location,
                node.AsteriskToken is null
                    ? DiagnosticCode.TypeOfYieldOperandInAnAsyncGeneratorMustEitherBeAValidPromiseOrMustNotContainACallableThenMember
                    : DiagnosticCode.TypeOfIteratedElementsOfAYieldAsteriskOperandMustEitherBeAValidPromiseOrMustNotContainACallableThenMember,
                cancellation).ConfigureAwait(false)
            : type;
    }

    internal async ValueTask<Type> CreateAsync(Type yield, Type result, Type? next, bool async, CancellationToken cancellation = default)
    {
        TextSlice prefix = async ? "Async" : "";
        var target = await host.IterationGlobalAsync(TextSlice.Concat(prefix, "Generator"), 3, false, cancellation).ConfigureAwait(false);
        if (async)
        {
            yield = await awaited.GetAsync(yield, cancellation: cancellation).ConfigureAwait(false) ?? context.UnknownType;
            result = await awaited.GetAsync(result, cancellation: cancellation).ConfigureAwait(false) ?? context.UnknownType;
        }
        if (target == context.EmptyGenericType)
        {
            target = await host.IterationGlobalAsync(TextSlice.Concat(prefix, "IterableIterator"), 3, false, cancellation).ConfigureAwait(false);
            if (target == context.EmptyGenericType)
            {
                await host.IterationGlobalAsync(TextSlice.Concat(prefix, "IterableIterator"), 3, true, cancellation).ConfigureAwait(false);
                return context.EmptyObjectType;
            }
        }
        return context.CreateTypeReference((InterfaceType)target, [yield, result, next ?? context.UnknownType]);
    }

    internal async ValueTask<bool> AssignableReturnAsync(Type type, bool async, CancellationToken cancellation = default,
        SyntaxNode? errorNode = null)
    {
        var iteration = await protocols.GeneratorAsync(type, async, cancellation).ConfigureAwait(false);
        var yield = iteration.Yield ?? context.AnyType;
        var generator = await CreateAsync(
            yield,
            iteration.Return ?? yield,
            iteration.Next ?? context.UnknownType,
            async,
            cancellation).ConfigureAwait(false);
        return errorNode is null
            ? await relations.RelatedAsync(generator, type, RelationKind.Assignable, cancellation).ConfigureAwait(false)
            : await host.ReportGeneratorReturnAsync(generator, type, errorNode, cancellation).ConfigureAwait(false);
    }

    internal ValueTask<Type> ContextReturnAsync(Type type, bool async, CancellationToken cancellation = default) =>
        algebra.FilterAsync(
            type,
            async part => (part.Flags & (TypeFlags.AnyOrUnknown | TypeFlags.Void | TypeFlags.InstantiableNonPrimitive)) != 0
            || await AssignableReturnAsync(part, async, cancellation).ConfigureAwait(false), cancellation);

    internal async ValueTask<Type?> ReturnExpressionAsync(Type type, bool async, CancellationToken cancellation = default)
    {
        if (type is UnionType)
            type = await algebra.FilterAsync(
                type,
                async part => (await protocols.GeneratorAsync(part, async, cancellation).ConfigureAwait(false)).Return is not null,
                cancellation).ConfigureAwait(false);
        return (await protocols.GeneratorAsync(type, async, cancellation).ConfigureAwait(false)).Return;
    }

    internal async ValueTask<Type?> ContextualAsync(SyntaxNode function, IterationTypeKind kind, CancellationToken cancellation = default) =>
        await contexts.ReturnAsync(function, cancellation: cancellation).ConfigureAwait(false) is { } type
            ? (await protocols.GeneratorAsync(
                type,
                SemanticSyntax.HasModifier(function, SyntaxKind.AsyncKeyword),
                cancellation).ConfigureAwait(false)).Get(kind) : null;

    internal async ValueTask<Type> WidenAsync(
        Type type,
        Type? contextual,
        IterationTypeKind kind,
        bool async,
        CancellationToken cancellation = default) =>
        (type.Flags & TypeFlags.Unit) == 0 ? type : await expressions.WidenLiteralAsync(type, contextual is null ? null
            : (await protocols.GeneratorAsync(contextual, async, cancellation).ConfigureAwait(false)).Get(kind),
            cancellation).ConfigureAwait(false);

    internal async ValueTask<Type?> OperandContextAsync(
        YieldExpressionNode node,
        ContextFlags flags,
        CancellationToken cancellation = default)
    {
        var function = DeclarationOrder.Ancestor(node.Parent, n => n is IFunctionSignature);
        if (function is null || await contexts.ReturnAsync(function, flags, cancellation).ConfigureAwait(false) is not { } type)
            return null;
        bool async = SemanticSyntax.HasModifier(function, SyntaxKind.AsyncKeyword);
        if (node.AsteriskToken is null && type is UnionType)
            type = await algebra.FilterAsync(
                type,
                async part => (await protocols.GeneratorAsync(part, async, cancellation).ConfigureAwait(false)).Return is not null,
                cancellation).ConfigureAwait(false);
        var iteration = await protocols.GeneratorAsync(type, async, cancellation).ConfigureAwait(false);
        if (node.AsteriskToken is null)
            return iteration.Yield;
        var yield = iteration.Yield ?? context.SilentNeverType;
        var result = await expressions.GetAsync(node, flags, cancellation).ConfigureAwait(false) ?? context.SilentNeverType;
        var next = iteration.Next ?? context.UnknownType;
        var generator = await CreateAsync(yield, result, next, false, cancellation).ConfigureAwait(false);
        return async ? await algebra.UnionAsync(
            [generator, await CreateAsync(yield, result, next, true, cancellation).ConfigureAwait(false)],
            cancellation: cancellation).ConfigureAwait(false) : generator;
    }
}

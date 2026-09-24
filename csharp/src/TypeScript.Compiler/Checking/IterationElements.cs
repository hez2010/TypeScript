using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IIterationElementHost
{
    bool NoUncheckedIndexedAccess { get; }

    ValueTask<Type> IterationGlobalAsync(string name, int arity, bool report, CancellationToken cancellation);

    ValueTask<bool> ArrayLikeAsync(Type type, CancellationToken cancellation);

    ValueTask<Type?> NumberIndexAsync(Type type, CancellationToken cancellation);

    void IterationError(SyntaxNode node, int code, bool missingAwait);
}

internal sealed class IterationElements(TypeContext context, TypeAlgebra algebra, IteratorProtocols protocols,
    TypeRelations relations, AwaitedTypes awaited, IIterationElementHost host)
{
    internal async ValueTask<Type> CheckAsync(
        IterationUse use,
        Type type,
        Type sent,
        SyntaxNode? node = null,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        context.RequireOwned(sent);
        return (type.Flags & TypeFlags.Any) != 0 ? type
            : await TryAsync(use, type, sent, node, true, cancellation).ConfigureAwait(false) ?? context.AnyType;
    }

    internal async ValueTask<Type?> TryAsync(
        IterationUse use,
        Type input,
        Type sent,
        SyntaxNode? node = null,
        bool checkAssignability = false,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(input);
        context.RequireOwned(sent);
        bool async = (use & IterationUse.AllowsAsyncIterablesFlag) != 0;
        if (input == context.NeverType)
        {
            if (node is not null)
                await NotIterableAsync(node, input, async, cancellation).ConfigureAwait(false);
            return null;
        }
        bool iterable = await host.IterationGlobalAsync(
            "Iterable",
            3,
            false,
            cancellation).ConfigureAwait(false) != context.EmptyGenericType;
        bool outOfBounds = host.NoUncheckedIndexedAccess && (use & IterationUse.PossiblyOutOfBounds) != 0;
        if (iterable || async)
        {
            var types = await protocols.IterableAsync(input, use, iterable ? node : null, cancellation).ConfigureAwait(false);
            if (checkAssignability && types.Next is { } next)
            {
                int code = (use & IterationUse.ForOfFlag) != 0 ? 2763 : (use & IterationUse.SpreadFlag) != 0 ? 2764
                    : (use & IterationUse.DestructuringFlag) != 0 ? 2765 : (use & IterationUse.YieldStarFlag) != 0 ? 2766 : 0;
                if (code != 0
                    && !await relations.RelatedAsync(sent, next, RelationKind.Assignable, cancellation).ConfigureAwait(false)
                    && node is not null)
                    host.IterationError(node, code, false);
            }
            if (types.Yield is not null || iterable)
                return types.Yield is not null && outOfBounds
                    ? await IncludeMissingAsync(types.Yield, cancellation).ConfigureAwait(false)
                    : types.Yield;
        }
        var array = input;
        bool hasString = false;
        if ((use & IterationUse.AllowsStringInputFlag) != 0)
        {
            if (array is UnionType union)
            {
                var filtered = union.Types.Where(t => (t.Flags & TypeFlags.StringLike) == 0).ToArray();
                if (filtered.Length != union.Types.Count)
                    array = await algebra.UnionAsync(filtered, UnionReduction.Subtype, cancellation: cancellation).ConfigureAwait(false);
            }
            else if ((array.Flags & TypeFlags.StringLike) != 0)
                array = context.NeverType;
            hasString = array != input;
            if (hasString && (array.Flags & TypeFlags.Never) != 0)
                return outOfBounds ? await IncludeMissingAsync(context.StringType, cancellation).ConfigureAwait(false) : context.StringType;
        }
        if (!await host.ArrayLikeAsync(array, cancellation).ConfigureAwait(false))
        {
            if (node is not null)
            {
                var types = await protocols.IterableAsync(input, use, cancellation: cancellation).ConfigureAwait(false);
                bool named = input.Symbol?.Name is "Float32Array" or "Float64Array" or "Int16Array" or "Int32Array" or "Int8Array"
                    or "NodeList" or "Uint16Array" or "Uint32Array" or "Uint8Array" or "Uint8ClampedArray";
                int code = types.Yield is not null || named
                    ? 2802
                    : (use & IterationUse.AllowsStringInputFlag) != 0 && !hasString ? 2495 : 2461;
                bool hint = types.Yield is null
                    && await awaited.OfPromiseAsync(array, cancellation: cancellation).ConfigureAwait(false) is not null;
                host.IterationError(node, code, hint);
            }
            return hasString
                ? outOfBounds ? await IncludeMissingAsync(context.StringType, cancellation).ConfigureAwait(false) : context.StringType
                : null;
        }
        var element = await host.NumberIndexAsync(array, cancellation).ConfigureAwait(false);
        if (hasString && element is not null)
        {
            if ((element.Flags & TypeFlags.StringLike) != 0 && !host.NoUncheckedIndexedAccess)
                return context.StringType;
            return await algebra.UnionAsync(
                outOfBounds ? [element, context.StringType, context.UndefinedType] : [element, context.StringType],
                UnionReduction.Subtype, cancellation: cancellation).ConfigureAwait(false);
        }
        return element is not null && (use & IterationUse.PossiblyOutOfBounds) != 0
            ? await IncludeMissingAsync(element, cancellation).ConfigureAwait(false) : element;
    }

    internal ValueTask<Type> IncludeMissingAsync(Type type, CancellationToken cancellation = default) => host.NoUncheckedIndexedAccess
        ? algebra.UnionAsync([type, context.MissingType], cancellation: cancellation) : ValueTask.FromResult(type);

    internal async ValueTask NotIterableAsync(SyntaxNode node, Type type, bool async, CancellationToken cancellation = default)
    {
        bool hint = await awaited.OfPromiseAsync(type, cancellation: cancellation).ConfigureAwait(false) is not null;
        if (!hint && !async && node.Parent is ForInOrOfStatementNode { Kind: SyntaxKind.ForOfStatement } loop && loop.Expression == node)
        {
            var target = await host.IterationGlobalAsync("AsyncIterable", 3, false, cancellation).ConfigureAwait(false);
            if (target != context.EmptyGenericType)
                hint = await relations.RelatedAsync(
                    type,
                    context.CreateTypeReference((InterfaceType)target, [context.AnyType, context.AnyType, context.AnyType]),
                    RelationKind.Assignable, cancellation).ConfigureAwait(false);
        }
        host.IterationError(node, async ? 2504 : 2488, hint);
    }
}

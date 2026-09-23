using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;

namespace TypeScript.Compiler.Checking;

internal interface IConditionalTypeHost
{
    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> SimplifyAsync(Type type, bool writing, CancellationToken cancellation);

    ValueTask<TypeMapper> InferConditionalAsync(IReadOnlyList<TypeParameter> parameters, Type check, Type extends,
            TypeMapper? mapper, bool deferred, CancellationToken cancellation);

    void ConditionalDepthExceeded();
}

internal sealed class ConditionalTypes(TypeContext context, TypeAlgebra algebra, TypeInstantiation instantiation,
    TypeConstraints constraints, MappedTypes mapped, TypeViews views, TypeRelations relations,
    Func<TypeReference, CancellationToken, ValueTask<IReadOnlyList<Type>>> arguments, IConditionalTypeHost host)
{
    internal async ValueTask<Type> EvaluateAsync(ConditionalRoot root, TypeMapper? mapper = null, bool forConstraint = false,
        TypeAlias? alias = null, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        Type result;
        List<Type>? extra = null;
        int tailCount = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            context.RequireOwned(root.CheckType);
            context.RequireOwned(root.ExtendsType);
            if (tailCount == 1000)
            {
                host.ConditionalDepthExceeded();
                return context.ErrorType;
            }
            var check = await InstantiateAsync(
                await mapped.ActualVariableAsync(root.CheckType, cancellation).ConfigureAwait(false),
                mapper,
                cancellation).ConfigureAwait(false);
            var extends = await InstantiateAsync(root.ExtendsType, mapper, cancellation).ConfigureAwait(false);
            if (check == context.ErrorType || extends == context.ErrorType)
                return context.ErrorType;
            if (check == context.WildcardType || extends == context.WildcardType)
                return context.WildcardType;
            var checkNode = SkipParentheses(root.Node.CheckType!);
            var extendsNode = SkipParentheses(root.Node.ExtendsType!);
            bool checkTuples = SimpleTuple(checkNode) && SimpleTuple(extendsNode)
                && ((TupleTypeNode)checkNode).Elements!.Count == ((TupleTypeNode)extendsNode).Elements!.Count;
            bool deferred = await DeferredAsync(check, checkTuples, cancellation).ConfigureAwait(false);
            var combined = root.InferTypeParameters is { Count: > 0 } parameters
                ? await host.InferConditionalAsync(parameters, check, extends, mapper, deferred, cancellation).ConfigureAwait(false) : null;
            var inferredExtends = combined is null
                ? extends
                : await InstantiateAsync(root.ExtendsType, combined, cancellation).ConfigureAwait(false);
            if (!deferred && !await DeferredAsync(inferredExtends, checkTuples, cancellation).ConfigureAwait(false))
            {
                if ((inferredExtends.Flags & TypeFlags.AnyOrUnknown) == 0
                    && ((check.Flags & TypeFlags.Any) != 0
                        || !await PermissivelyAssignableAsync(check, inferredExtends, cancellation).ConfigureAwait(false)))
                {
                    bool includeTrue = (check.Flags & TypeFlags.Any) != 0;
                    if (!includeTrue && forConstraint && (inferredExtends.Flags & TypeFlags.Never) == 0)
                    {
                        var permissive = await instantiation.PermissiveAsync(inferredExtends, cancellation).ConfigureAwait(false);
                        foreach (var part in permissive is UnionType union ? union.Types : (IReadOnlyList<Type>)[permissive])
                            if (await relations.RelatedAsync(
                                part,
                                await instantiation.PermissiveAsync(check, cancellation).ConfigureAwait(false),
                                RelationKind.Assignable,
                                cancellation).ConfigureAwait(false))
                            {
                                includeTrue = true;
                                break;
                            }
                    }
                    if (includeTrue)
                        (extra ??= []).Add(
                            await InstantiateAsync(
                                await host.TypeFromNodeAsync(root.Node.TrueType!, cancellation).ConfigureAwait(false),
                                combined ?? mapper,
                                cancellation).ConfigureAwait(false));
                    var whenFalse = await host.TypeFromNodeAsync(root.Node.FalseType!, cancellation).ConfigureAwait(false);
                    if (whenFalse is ConditionalType nested && nested.Root.Node.Parent == root.Node
                        && (!nested.Root.IsDistributive || nested.Root.CheckType == root.CheckType))
                    {
                        root = nested.Root;
                        continue;
                    }
                    if (await TailAsync(whenFalse, mapper, cancellation).ConfigureAwait(false) is { } tail)
                    {
                        (root, mapper) = tail;
                        alias = null;
                        if (root.Alias is not null)
                            tailCount++;
                        continue;
                    }
                    result = await InstantiateAsync(whenFalse, mapper, cancellation).ConfigureAwait(false);
                    break;
                }
                if ((inferredExtends.Flags & TypeFlags.AnyOrUnknown) != 0
                    || await RestrictivelyAssignableAsync(check, inferredExtends, cancellation).ConfigureAwait(false))
                {
                    var whenTrue = await host.TypeFromNodeAsync(root.Node.TrueType!, cancellation).ConfigureAwait(false);
                    var trueMapper = combined ?? mapper;
                    if (await TailAsync(whenTrue, trueMapper, cancellation).ConfigureAwait(false) is { } tail)
                    {
                        (root, mapper) = tail;
                        alias = null;
                        if (root.Alias is not null)
                            tailCount++;
                        continue;
                    }
                    result = await InstantiateAsync(whenTrue, trueMapper, cancellation).ConfigureAwait(false);
                    break;
                }
            }
            result = new ConditionalType(context, root,
                await InstantiateAsync(root.CheckType, mapper, cancellation).ConfigureAwait(false),
                await InstantiateAsync(root.ExtendsType, mapper, cancellation).ConfigureAwait(false))
            {
                Mapper = mapper,
                CombinedMapper = combined,
                Alias = alias ?? (mapper is null
                    ? root.Alias
                    : await instantiation.AliasAsync(root.Alias, mapper, cancellation).ConfigureAwait(false))
            };
            break;
        }
        if (extra is null)
            return result;
        extra.Add(result);
        return await algebra.UnionAsync(extra, cancellation: cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> InstantiateAsync(ConditionalType type, TypeMapper mapper, bool forConstraint = false,
        TypeAlias? alias = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        var root = type.Root;
        if (root.OuterTypeParameters is not { Count: > 0 } parameters)
            return type;
        var values = new Type[parameters.Count];
        for (int i = 0; i < values.Length; i++)
            values[i] = await mapper.MapAsync(parameters[i], cancellation).ConfigureAwait(false);
        var key = TypeCacheKey.Instantiation(values, alias, forConstraint);
        if (root.Instantiations?.TryGetValue(key, out var cached) == true)
            return cached;
        var effective = TypeMapper.Create(parameters.Cast<Type>().ToArray(), values);
        var distribution = root.IsDistributive
            ? await views.ReducedAsync(
                await effective.MapTypeAsync(root.CheckType, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false) : null;
        Type result;
        if (distribution is not null && distribution != root.CheckType && (distribution.Flags & (TypeFlags.Union | TypeFlags.Never)) != 0)
        {
            async ValueTask<Type?> MapAsync(Type part) => await EvaluateAsync(root,
                TypeMapper.Prepend(root.CheckType, part, effective), forConstraint, cancellation: cancellation).ConfigureAwait(false);
            if (distribution is UnionType union && alias is not null)
            {
                var mappedTypes = new List<Type>();
                foreach (var part in union.Types)
                    mappedTypes.Add((await MapAsync(part).ConfigureAwait(false))!);
                result = await algebra.UnionAsync(mappedTypes, alias: alias, cancellation: cancellation).ConfigureAwait(false);
            }
            else
                result = await algebra.MapAsync(
                    distribution,
                    MapAsync,
                    cancellation: cancellation).ConfigureAwait(false) ?? context.NeverType;
        }
        else
            result = await EvaluateAsync(root, effective, forConstraint, alias, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        (root.Instantiations ??= [])[key] = result;
        return result;
    }

    private async ValueTask<(ConditionalRoot Root, TypeMapper Mapper)?> TailAsync(
        Type type,
        TypeMapper? mapper,
        CancellationToken cancellation)
    {
        if (type is not ConditionalType conditional
            || mapper is null
            || conditional.Root.OuterTypeParameters is not { Count: > 0 } parameters)
            return null;
        var combined = TypeMapper.CombineAsync(conditional.Mapper, mapper, InstantiateRequiredAsync);
        var values = new Type[parameters.Count];
        for (int i = 0; i < values.Length; i++)
            values[i] = await combined.MapAsync(parameters[i], cancellation).ConfigureAwait(false);
        var effective = TypeMapper.Create(parameters.Cast<Type>().ToArray(), values);
        var check = conditional.Root.IsDistributive
            ? await effective.MapTypeAsync(conditional.Root.CheckType, cancellation).ConfigureAwait(false)
            : null;
        return check is null || check == conditional.Root.CheckType || (check.Flags & (TypeFlags.Union | TypeFlags.Never)) == 0
            ? (conditional.Root, effective) : null;
    }

    internal async ValueTask<Type> SimplifyAsync(ConditionalType type, bool writing, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        var whenTrue = await constraints.ConditionalTrueAsync(type, cancellation: cancellation).ConfigureAwait(false);
        var whenFalse = await constraints.ConditionalFalseAsync(type, cancellation).ConfigureAwait(false);
        if ((whenFalse.Flags & TypeFlags.Never) != 0
            && await mapped.ActualVariableAsync(
                whenTrue,
                cancellation).ConfigureAwait(false) == await mapped.ActualVariableAsync(type.CheckType, cancellation).ConfigureAwait(false))
        {
            if ((type.CheckType.Flags & TypeFlags.Any) != 0
                || await RestrictivelyAssignableAsync(type.CheckType, type.ExtendsType, cancellation).ConfigureAwait(false))
                return await host.SimplifyAsync(whenTrue, writing, cancellation).ConfigureAwait(false);
            if (await EmptyIntersectionAsync(type.CheckType, type.ExtendsType, cancellation).ConfigureAwait(false))
                return context.NeverType;
        }
        else if ((whenTrue.Flags & TypeFlags.Never) != 0
            && await mapped.ActualVariableAsync(
                whenFalse,
                cancellation).ConfigureAwait(false) == await mapped.ActualVariableAsync(type.CheckType, cancellation).ConfigureAwait(false))
        {
            if ((type.CheckType.Flags & TypeFlags.Any) == 0
                && await RestrictivelyAssignableAsync(type.CheckType, type.ExtendsType, cancellation).ConfigureAwait(false))
                return context.NeverType;
            if ((type.CheckType.Flags & TypeFlags.Any) != 0
                || await EmptyIntersectionAsync(type.CheckType, type.ExtendsType, cancellation).ConfigureAwait(false))
                return await host.SimplifyAsync(whenFalse, writing, cancellation).ConfigureAwait(false);
        }
        return type;
    }

    private async ValueTask<bool> EmptyIntersectionAsync(Type left, Type right, CancellationToken cancellation)
        =>
            ((await algebra.UnionAsync(
                [await algebra.IntersectionAsync([left, right], cancellation: cancellation).ConfigureAwait(false), context.NeverType],
                cancellation: cancellation).ConfigureAwait(false)).Flags & TypeFlags.Never) != 0;

    private async ValueTask<bool> PermissivelyAssignableAsync(Type source, Type target, CancellationToken cancellation)
        => await relations.RelatedAsync(await instantiation.PermissiveAsync(source, cancellation).ConfigureAwait(false),
            await instantiation.PermissiveAsync(target, cancellation).ConfigureAwait(false),
            RelationKind.Assignable,
            cancellation).ConfigureAwait(false);

    private async ValueTask<bool> RestrictivelyAssignableAsync(Type source, Type target, CancellationToken cancellation)
        => await relations.RelatedAsync(await instantiation.RestrictiveAsync(source, cancellation).ConfigureAwait(false),
            await instantiation.RestrictiveAsync(target, cancellation).ConfigureAwait(false),
            RelationKind.Assignable,
            cancellation).ConfigureAwait(false);

    private async ValueTask<bool> DeferredAsync(Type type, bool checkTuples, CancellationToken cancellation)
    {
        if (await mapped.GenericFlagsAsync(type, cancellation).ConfigureAwait(false) != 0)
            return true;
        if (checkTuples && type is TypeReference { Target: TupleType tupleTarget } tuple)
            foreach (var element in (await arguments(tuple, cancellation).ConfigureAwait(false)).Take(tupleTarget.ElementInfos.Count))
                if (await mapped.GenericFlagsAsync(element, cancellation).ConfigureAwait(false) != 0)
                    return true;
        return false;
    }

    private async ValueTask<Type> InstantiateAsync(Type type, TypeMapper? mapper, CancellationToken cancellation)
        => await instantiation.InstantiateAsync(type, mapper, cancellation: cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Conditional instantiation returned no type");

    private ValueTask<Type> InstantiateRequiredAsync(Type type, TypeMapper mapper, CancellationToken cancellation) =>
        InstantiateAsync(type, mapper, cancellation);

    private static SyntaxNode SkipParentheses(SyntaxNode node)
    {
        while (node is ParenthesizedTypeNode parentheses)
            node = parentheses.Type!;
        return node;
    }

    private static bool SimpleTuple(SyntaxNode node) => node is TupleTypeNode { Elements.Count: > 0 } tuple
            && !tuple.Elements.Any(
                e => e is OptionalTypeNode or RestTypeNode
                    || e is NamedTupleMemberNode named && (named.QuestionToken is not null || named.DotDotDotToken is not null));
}

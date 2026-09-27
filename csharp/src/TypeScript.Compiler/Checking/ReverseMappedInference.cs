using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface IReverseMappedInferenceHost
{
    bool IsArray(Type type);

    bool IsReadonlyArray(Type type);

    bool IsReadonly(Symbol symbol);

    ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Symbol>> PropertiesAsync(Type type, CancellationToken cancellation);

    ValueTask<Type> SymbolTypeAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> AssignableAsync(Type source, Type target, CancellationToken cancellation);
}

internal sealed class ReverseMappedInference(TypeContext context, TypeAlgebra algebra, TypeInstantiation instantiation,
    MappedTypes mapped, TypeKeys keys, IndexedTypes indexed, TupleTypes tuples, TypeRecursion recursion,
    TypeInference inference, TypeWidening widening, CheckerLinks links, IReverseMappedInferenceHost host)
{
    private readonly Dictionary<(Type, MappedType, IndexType), Type?> homomorphic = [], elements = [];
    private readonly List<Type> sources = [], targets = [];
    private ExpandingFlags expanding;

    internal async ValueTask<Type?> HomomorphicAsync(
        Type source,
        MappedType target,
        IndexType constraint,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        context.RequireOwned(constraint);
        var key = (source, target, constraint);
        if (homomorphic.TryGetValue(key, out var cached) && cached is not null)
            return cached;
        if (!((await host.IndexesAsync(source, cancellation).ConfigureAwait(false)).Any(i => i.KeyType == context.StringType)
            || (await host.PropertiesAsync(source, cancellation).ConfigureAwait(false)).Count != 0
                && await PartiallyInferableAsync(source, cancellation).ConfigureAwait(false)))
            return null;
        Type? result;
        if (host.IsArray(source))
        {
            var element = await ElementAsync(
                (await host.TypeArgumentsAsync((TypeReference)source, cancellation).ConfigureAwait(false))[0],
                target,
                constraint,
                cancellation).ConfigureAwait(false);
            result = element is null
                ? null
                : await tuples.ArrayAsync(element, host.IsReadonlyArray(source), cancellation).ConfigureAwait(false);
        }
        else if (source is TypeReference { Target: TupleType tuple } reference)
        {
            var args = await host.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false);
            var types = new Type[tuple.ElementInfos.Count];
            for (int i = 0; i < types.Length; i++)
            {
                var element = await ElementAsync(args[i], target, constraint, cancellation).ConfigureAwait(false);
                if (element is null)
                    return null;
                types[i] = element;
            }
            var infos = tuple.ElementInfos;
            if ((MappedTypes.Modifiers(target) & MappedTypeModifiers.IncludeOptional) != 0)
                infos = infos.Select(
                    i => (i.Flags & ElementFlags.Optional) != 0
                        ? new TupleElementInfo(ElementFlags.Required, i.LabeledDeclaration)
                        : i).ToArray();
            result = await tuples.CreateAsync(types, infos, tuple.IsReadonly, cancellation).ConfigureAwait(false);
        }
        else
        {
            var reverse = (ReverseMappedType)context.NewObjectType(ObjectFlags.ReverseMapped | ObjectFlags.Anonymous);
            reverse.Source = source;
            reverse.MappedType = target;
            reverse.ConstraintType = constraint;
            result = reverse;
        }
        cancellation.ThrowIfCancellationRequested();
        homomorphic[key] = result;
        return result;
    }

    internal async ValueTask<Type?> ElementAsync(
        Type source,
        MappedType target,
        IndexType constraint,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        context.RequireOwned(constraint);
        var key = (source, target, constraint);
        if (elements.TryGetValue(key, out var cached))
            return cached ?? context.UnknownType;
        sources.Add(source);
        targets.Add(target);
        var previous = expanding;
        Type? result = null;
        try
        {
            if (await recursion.IsDeeplyNestedAsync(source, sources, 2, cancellation).ConfigureAwait(false))
                expanding |= ExpandingFlags.Source;
            if (await recursion.IsDeeplyNestedAsync(target, targets, 2, cancellation).ConfigureAwait(false))
                expanding |= ExpandingFlags.Target;
            if (expanding != ExpandingFlags.Both)
            {
                var parameter = await indexed.GetAsync(
                    constraint.Target,
                    await mapped.ParameterAsync(target, cancellation).ConfigureAwait(false),
                    cancellation: cancellation).ConfigureAwait(false);
                var template = await mapped.TemplateAsync(target, cancellation).ConfigureAwait(false);
                var inferenceContext = inference.Create([parameter]);
                await inference.InferAsync(inferenceContext, source, template, cancellation: cancellation).ConfigureAwait(false);
                result = await widening.GetAsync(
                    await inference.FromCandidatesAsync(
                        inferenceContext.Inferences[0],
                        cancellation).ConfigureAwait(false) ?? context.UnknownType,
                    cancellation).ConfigureAwait(false);
            }
        }
        finally
        {
            sources.RemoveAt(sources.Count - 1);
            targets.RemoveAt(targets.Count - 1);
            expanding = previous;
        }
        cancellation.ThrowIfCancellationRequested();
        elements[key] = result;
        return result;
    }

    internal async ValueTask<bool> PartiallyInferableAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if ((type.ObjectFlags & ObjectFlags.NonInferrableType) == 0)
            return true;
        if ((type.ObjectFlags & ObjectFlags.ObjectLiteral) != 0)
        {
            foreach (var property in await host.PropertiesAsync(type, cancellation).ConfigureAwait(false))
                if (await PartiallyInferableAsync(
                    await host.SymbolTypeAsync(property, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false))
                    return true;
        }
        else if (type is TypeReference { Target: TupleType tuple } reference)
            foreach (var element in (await host.TypeArgumentsAsync(
                reference,
                cancellation).ConfigureAwait(false)).Take(tuple.ElementInfos.Count))
                if (await PartiallyInferableAsync(element, cancellation).ConfigureAwait(false))
                    return true;
        return false;
    }

    internal async ValueTask ResolveAsync(ReverseMappedType type, CancellationToken cancellation = default)
    {
        var source = type.Source!;
        var target = (MappedType)type.MappedType!;
        var constraint = (IndexType)type.ConstraintType!;
        var sourceIndex = (await host.IndexesAsync(
            source,
            cancellation).ConfigureAwait(false)).FirstOrDefault(i => i.KeyType == context.StringType);
        var modifiers = MappedTypes.Modifiers(target);
        bool readOnly = (modifiers & MappedTypeModifiers.IncludeReadonly) == 0;
        var optional = (modifiers & MappedTypeModifiers.IncludeOptional) != 0 ? 0 : SymbolFlags.Optional;
        IReadOnlyList<IndexInfo> indexes = sourceIndex is null ? [] : [context.NewIndexInfo(context.StringType,
            await ElementAsync(sourceIndex.ValueType, target, constraint, cancellation).ConfigureAwait(false) ?? context.UnknownType,
            readOnly && sourceIndex.IsReadonly)];
        var members = new Dictionary<TextSlice, Symbol>();
        var limit = await LimitedConstraintAsync(target, constraint, cancellation).ConfigureAwait(false);
        foreach (var property in await host.PropertiesAsync(source, cancellation).ConfigureAwait(false))
        {
            if (limit is not null
                && !await host.AssignableAsync(
                    await keys.PropertyAsync(property, TypeFlags.StringOrNumberLiteralOrUnique, false, cancellation).ConfigureAwait(false),
                    limit,
                    cancellation).ConfigureAwait(false))
                continue;
            var inferred = new Symbol(SymbolFlags.Property | SymbolFlags.Transient | property.Flags & optional, property.Name)
            { CheckFlags = CheckFlags.ReverseMapped | (readOnly && host.IsReadonly(property) ? CheckFlags.Readonly : 0) };
            inferred.DeclarationList.AddRange(property.Declarations);
            links.Values.Get(inferred).NameType = links.Values.Get(property).NameType;
            var data = links.ReverseMappedSymbols.Get(inferred);
            data.PropertyType = await host.SymbolTypeAsync(property, cancellation).ConfigureAwait(false);
            if (constraint.Target is IndexedAccessType { ObjectType: TypeParameter replacement, IndexType: TypeParameter } access)
            {
                var tuple = await tuples.CreateAsync(
                    [replacement],
                    [new(ElementFlags.Required)],
                    cancellation: cancellation).ConfigureAwait(false);
                data.MappedType = (MappedType)(await instantiation.InstantiateAsync(target,
                    TypeMapper.Create([access.IndexType, access.ObjectType], [context.GetNumberLiteralType(0), tuple]),
                    cancellation: cancellation).ConfigureAwait(false))!;
                data.ConstraintType = (IndexType)await keys.GetAsync(replacement, cancellation: cancellation).ConfigureAwait(false);
            }
            else
            {
                data.MappedType = target;
                data.ConstraintType = constraint;
            }
            members[property.Name] = inferred;
        }
        cancellation.ThrowIfCancellationRequested();
        type.Members = members.AsReadOnly();
        type.Properties = members.Values.Order(algebra.Order).ToArray();
        type.CallSignatures = [];
        type.ConstructSignatures = [];
        type.IndexInfos = indexes;
        type.ObjectFlags = type.ObjectFlags & ~ObjectFlags.UnresolvedMembers | ObjectFlags.MembersResolved;
    }

    internal async ValueTask<Type> SymbolAsync(Symbol symbol, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var values = links.Values.Get(symbol);
        if (values.ResolvedType is { } cached)
            return cached;
        var data = links.ReverseMappedSymbols.Get(symbol);
        var result = await ElementAsync(
            data.PropertyType!,
            data.MappedType!,
            data.ConstraintType!,
            cancellation).ConfigureAwait(false) ?? context.UnknownType;
        cancellation.ThrowIfCancellationRequested();
        return values.ResolvedType = result;
    }

    private async ValueTask<Type?> LimitedConstraintAsync(MappedType mappedType, IndexType constraintType, CancellationToken cancellation)
    {
        var constraint = await mapped.ConstraintAsync(mappedType, cancellation).ConfigureAwait(false);
        if (constraint is not UnionOrIntersectionType)
            return null;
        var origin = constraint is UnionType union ? union.Origin : constraint;
        if (origin is not IntersectionType intersection)
            return null;
        var limited = await algebra.IntersectionAsync(
            intersection.Types.Where(t => t != constraintType).ToArray(),
            cancellation: cancellation).ConfigureAwait(false);
        return limited != context.NeverType ? limited : null;
    }
}

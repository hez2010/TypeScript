using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface ITypeInferenceHost
{
    ValueTask<Type> CovariantInferenceAsync(InferenceInfo inference, Signature signature, CancellationToken cancellation);

    ValueTask<Type> WithThisAsync(Type type, Type argument, bool apparent, CancellationToken cancellation);

    ValueTask<Type?> IntraContextualTypeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> NonUndefinedAsync(Type type, CancellationToken cancellation);

    bool StrictFunctionTypes { get; }

    bool IsArray(Type type);

    bool IsReadonlyArray(Type type);

    bool SkipDirectInference(SyntaxNode node);

    bool HasInferencePattern(Type type);

    Type ArrayTarget(bool isReadonly);

    ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation);

    ValueTask<(IReadOnlyList<Type> Source, IReadOnlyList<Type> Target, IReadOnlyList<VarianceFlags> Variances)> AliasInferenceArgumentsAsync(
        TypeAlias source,
        TypeAlias target,
        CancellationToken cancellation);

    ValueTask<IReadOnlyList<VarianceFlags>> InferenceVariancesAsync(TypeReference target, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Symbol>> PropertiesAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Symbol>> ObjectPropertiesAsync(Type type, CancellationToken cancellation);

    ValueTask<Symbol?> PropertyAsync(Type type, TextSlice name, CancellationToken cancellation);

    ValueTask<Type> SymbolTypeAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);

    ValueTask<Type> SimplifyAsync(Type type, bool writing, CancellationToken cancellation);

    ValueTask<Type> EmptyInferenceObjectAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> DefinitelyUnrelatedAsync(Type source, Type target, CancellationToken cancellation);

    ValueTask<bool> InferableIndexAsync(Type type, CancellationToken cancellation);

    ValueTask<Type?> ReverseMappedInferenceAsync(Type source, MappedType target, IndexType constraint, CancellationToken cancellation);

    ValueTask<bool> ConstTypeVariableAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> MutableArrayLikeAsync(Type type, CancellationToken cancellation);

    IndexInfo EnumNumberIndex { get; }
}

internal sealed partial class TypeInference(TypeContext context, TypeAlgebra algebra, TypeConstraints constraints,
    TypeInstantiation instantiation, TypeRelations relations, TypeVariables variables, TypeRecursion recursion,
    TypeViews views, MappedTypes mapped, IndexedTypes indexed, TypeKeys keys,
    TupleTypes tuples, IndexSignatures indexes, SymbolTypes symbols, SignatureParameters parameters,
    Signatures signatures, SignatureAssignability signatureRelations, SignatureInstantiation signatureInstantiation,
    TemplateMatching templates, TypeWidening widening, TypeOrder order,
    ITypeInferenceHost host)
{
    internal InferenceContext Create(IReadOnlyList<Type> parameters, Signature? signature = null, InferenceFlags flags = 0,
        Func<Type, Type, CancellationToken, ValueTask<Ternary>>? compare = null)
    {
        foreach (var parameter in parameters)
            context.RequireOwned(parameter);
        if (signature is not null && signature.Context != context)
            throw new ArgumentException("Signature belongs to another checker", nameof(signature));
        return Create(parameters.Select(p => new InferenceInfo(p)).ToArray(), signature, flags, compare ?? AssignableAsync);
    }

    private InferenceContext Create(InferenceInfo[] inferences, Signature? signature, InferenceFlags flags,
        Func<Type, Type, CancellationToken, ValueTask<Ternary>> compare)
    {
        var result = new InferenceContext(inferences, signature, flags, compare);
        result.Mapper = TypeMapper.FunctionAsync((type, token) => MapAsync(result, type, true, token));
        result.NonFixingMapper = TypeMapper.FunctionAsync((type, token) => MapAsync(result, type, false, token));
        return result;
    }

    internal InferenceContext Clone(InferenceContext inference, InferenceFlags extraFlags = 0)
        =>
            Create(
                inference.Inferences.Select(i => i.Clone()).ToArray(),
                inference.Signature,
                inference.Flags | extraFlags,
                inference.CompareTypes);

    internal InferenceContext? CloneInferred(InferenceContext inference)
    {
        var infos = inference.Inferences.Where(i => i.HasCandidates).Select(i => i.Clone()).ToArray();
        return infos.Length == 0 ? null : Create(infos, inference.Signature, inference.Flags, inference.CompareTypes);
    }

    private ValueTask<Type> MapAsync(InferenceContext inference, Type type, bool fixing, CancellationToken cancellation)
        => inference.RunAsync(async () =>
        {
            for (int i = 0; i < inference.Inferences.Length; i++)
            {
                var info = inference.Inferences[i];
                if (info.Parameter != type)
                    continue;
                if (fixing && !info.IsFixed)
                {
                    foreach (var site in inference.IntraExpressionSites.ToArray())
                        if (await host.IntraContextualTypeAsync(site.Node, cancellation).ConfigureAwait(false) is { } target)
                            await InferAsync(inference, site.Type, target, cancellation: cancellation).ConfigureAwait(false);
                    inference.IntraExpressionSites = [];
                    inference.ClearCached();
                    info.IsFixed = true;
                }
                return await GetAsync(inference, i, cancellation).ConfigureAwait(false);
            }
            return type;
        }, cancellation);

    internal ValueTask<Type> GetAsync(InferenceContext inference, int index, CancellationToken cancellation = default)
        => inference.RunAsync(() => GetWorkerAsync(inference, index, cancellation), cancellation);

    private async ValueTask<Type> GetWorkerAsync(InferenceContext inference, int index, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var info = inference.Inferences[index];
        context.RequireOwned(info.Parameter);
        if (info.InferredType is { } cached)
            return cached;
        if (info.Parameter == context.ErrorType)
            return info.Parameter;
        Type? inferred = null, fallback = null;
        if (inference.Signature is { } signature)
        {
            var covariant = info.Candidates.Count == 0
                ? null
                : await host.CovariantInferenceAsync(info, signature, cancellation).ConfigureAwait(false);
            var contravariant = info.ContraCandidates.Count == 0 ? null
                : (info.Priority & InferencePriority.PriorityImpliesCombination) != 0
                    ? await algebra.IntersectionAsync(info.ContraCandidates, cancellation: cancellation).ConfigureAwait(false)
                    : await CommonSubtypeAsync(info.ContraCandidates, cancellation).ConfigureAwait(false);
            if (covariant is not null || contravariant is not null)
            {
                bool preferCovariant = covariant is not null && contravariant is null;
                if (!preferCovariant && covariant is not null && (covariant.Flags & (TypeFlags.Never | TypeFlags.Any)) == 0)
                {
                    foreach (var candidate in info.ContraCandidates)
                        if (await relations.RelatedAsync(covariant, candidate, RelationKind.Assignable, cancellation).ConfigureAwait(false))
                        {
                            preferCovariant = true;
                            break;
                        }
                    if (preferCovariant)
                        foreach (var other in inference.Inferences)
                        {
                            if (other != info && (other.Parameter is not TypeParameter otherParameter
                                || await constraints.ParameterConstraintAsync(
                                    otherParameter,
                                    cancellation).ConfigureAwait(false) != info.Parameter))
                                continue;
                            foreach (var candidate in other.Candidates)
                                if (!await relations.RelatedAsync(
                                    candidate,
                                    covariant,
                                    RelationKind.Assignable,
                                    cancellation).ConfigureAwait(false))
                                {
                                    preferCovariant = false;
                                    break;
                                }
                            if (!preferCovariant)
                                break;
                        }
                }
                inferred = preferCovariant ? covariant : contravariant;
                fallback = preferCovariant ? contravariant : covariant;
            }
            else if ((inference.Flags & InferenceFlags.NoDefault) != 0)
                inferred = context.SilentNeverType;
            else if (info.Parameter is TypeParameter defaultParameter
                && await constraints.DefaultAsync(defaultParameter, cancellation).ConfigureAwait(false) is { } defaultType)
            {
                var backreferences = TypeMapper.ToSingle(
                    inference.Inferences.Skip(index).Select(i => i.Parameter).ToArray(),
                    context.UnknownType);
                inferred = await instantiation.InstantiateAsync(
                    defaultType,
                    TypeMapper.Merge(backreferences, inference.NonFixingMapper),
                    cancellation: cancellation).ConfigureAwait(false);
            }
        }
        else
            inferred = await FromCandidatesAsync(info, cancellation).ConfigureAwait(false);
        // Publish the provisional result before resolving constraints. The mapper
        // may re-enter for this parameter through another parameter's constraint.
        info.InferredType = inferred ?? ((inference.Flags & InferenceFlags.AnyDefault) != 0 ? context.AnyType : context.UnknownType);
        if (info.Parameter is TypeParameter parameter
            && await constraints.ParameterConstraintAsync(parameter, cancellation).ConfigureAwait(false) is { } constraint)
        {
            var instantiated = await instantiation.InstantiateAsync(
                constraint,
                inference.NonFixingMapper,
                cancellation: cancellation).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Inference constraint instantiation returned no type");
            if (inferred is not null && (inference.Flags & InferenceFlags.NoConstraintChecks) == 0)
            {
                var withThis = await host.WithThisAsync(instantiated, inferred, false, cancellation).ConfigureAwait(false);
                if (await inference.CompareTypes(inferred, withThis, cancellation).ConfigureAwait(false) == Ternary.False)
                {
                    Type? filtered = null;
                    if (info.Priority == InferencePriority.ReturnType)
                        filtered = await algebra.MapAsync(inferred, async part =>
                            await inference.CompareTypes(part, withThis, cancellation).ConfigureAwait(false) != Ternary.False
                                ? part
                                : context.NeverType,
                            cancellation: cancellation).ConfigureAwait(false);
                    inferred = filtered is not null && (filtered.Flags & TypeFlags.Never) == 0 ? filtered : null;
                }
            }
            if (inferred is null)
                inferred = fallback is not null && await inference.CompareTypes(fallback,
                    await host.WithThisAsync(instantiated, fallback, false, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false) != Ternary.False
                    ? fallback : instantiated;
            info.InferredType = inferred;
        }
        cancellation.ThrowIfCancellationRequested();
        instantiation.ClearActiveCaches();
        return info.InferredType;
    }

    internal async ValueTask<IReadOnlyList<Type>> GetAllAsync(InferenceContext inference, CancellationToken cancellation = default)
        => await inference.RunAsync(async () =>
        {
            var result = new Type[inference.Inferences.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = await GetWorkerAsync(inference, i, cancellation).ConfigureAwait(false);
            return (IReadOnlyList<Type>)Array.AsReadOnly(result);
        }, cancellation).ConfigureAwait(false);

    internal async ValueTask<Type?> FromCandidatesAsync(InferenceInfo info, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(info.Parameter);
        return info.Candidates.Count != 0 ? await algebra.UnionAsync(
            info.Candidates,
            UnionReduction.Subtype,
            cancellation: cancellation).ConfigureAwait(false)
            : info.ContraCandidates.Count != 0
                ? await algebra.IntersectionAsync(info.ContraCandidates, cancellation: cancellation).ConfigureAwait(false)
                : null;
    }

    private async ValueTask<Type> CommonSubtypeAsync(IReadOnlyList<Type> types, CancellationToken cancellation)
    {
        var candidate = types[0];
        foreach (var type in types.Skip(1))
            if (await relations.RelatedAsync(type, candidate, RelationKind.Subtype, cancellation).ConfigureAwait(false))
                candidate = type;
        return candidate;
    }

    private async ValueTask<Ternary> AssignableAsync(Type source, Type target, CancellationToken cancellation)
        =>
            await relations.RelatedAsync(source, target, RelationKind.Assignable, cancellation).ConfigureAwait(false)
                ? Ternary.True
                : Ternary.False;
}

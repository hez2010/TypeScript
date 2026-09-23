using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface ICompositeMemberHost
{
    Type GlobalFunction { get; }

    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Signature>> UnionSignaturesAsync(
        IReadOnlyList<IReadOnlyList<Signature>> signatures,
        CancellationToken cancellation);

    ValueTask<IReadOnlyList<Signature>> ArrayMemberSignaturesAsync(UnionType type, CancellationToken cancellation);

    ValueTask<bool> IdenticalSignaturesAsync(Signature left, Signature right, CancellationToken cancellation);

    ValueTask<Type> ParameterTypeAsync(Symbol parameter, CancellationToken cancellation);

    ValueTask<Type?> ArrayElementAsync(Type type, CancellationToken cancellation);
}

internal sealed class CompositeMembers(TypeContext context, TypeAlgebra algebra, MappedMembers mapped, Signatures signatures,
    ICompositeMemberHost host)
{
    private readonly Signature unknownSignature = context.NewSignature(0, null, [], null, [], context.ErrorType, null, 0);

    internal async ValueTask UnionAsync(UnionType type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        var calls = new IReadOnlyList<Signature>[type.Types.Count];
        for (int i = 0; i < calls.Length; i++)
            calls[i] = type.Types[i] == host.GlobalFunction ? [unknownSignature]
            : await host.SignaturesAsync(type.Types[i], false, cancellation).ConfigureAwait(false);
        var callSignatures = await host.UnionSignaturesAsync(calls, cancellation).ConfigureAwait(false);
        if (callSignatures.Count == 0)
            callSignatures = await host.ArrayMemberSignaturesAsync(type, cancellation).ConfigureAwait(false);
        var constructors = new IReadOnlyList<Signature>[type.Types.Count];
        for (int i = 0; i < constructors.Length; i++)
            constructors[i] = await host.SignaturesAsync(type.Types[i], true, cancellation).ConfigureAwait(false);
        var constructSignatures = await host.UnionSignaturesAsync(constructors, cancellation).ConfigureAwait(false);
        var indexes = await UnionIndexesAsync(type.Types, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        Set(type, callSignatures, constructSignatures, indexes);
    }

    internal async ValueTask<IReadOnlyList<IndexInfo>> UnionIndexesAsync(
        IReadOnlyList<Type> types,
        CancellationToken cancellation = default)
    {
        var source = await host.IndexesAsync(types[0], cancellation).ConfigureAwait(false);
        var result = new List<IndexInfo>();
        foreach (var info in source)
        {
            bool present = true;
            foreach (var type in types)
            {
                if (!(await host.IndexesAsync(type, cancellation).ConfigureAwait(false)).Any(i => i.KeyType == info.KeyType))
                {
                    present = false;
                    break;
                }
            }
            if (!present)
                continue;
            var values = new List<Type>();
            foreach (var type in types)
                values.Add(
                    (await host.IndexesAsync(type, cancellation).ConfigureAwait(false)).First(i => i.KeyType == info.KeyType).ValueType);
            var value = await algebra.UnionAsync(values, cancellation: cancellation).ConfigureAwait(false);
            bool readOnly = false;
            foreach (var type in types)
                if ((await host.IndexesAsync(type, cancellation).ConfigureAwait(false)).First(i => i.KeyType == info.KeyType).IsReadonly)
                {
                    readOnly = true;
                    break;
                }
            result.Add(context.NewIndexInfo(info.KeyType, value, readOnly));
        }
        return result.AsReadOnly();
    }

    internal async ValueTask IntersectionAsync(IntersectionType type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        var mixins = new bool[type.Types.Count];
        int constructorCount = 0, mixinCount = 0, firstMixin = -1;
        for (int i = 0; i < mixins.Length; i++)
            mixins[i] = await MixinAsync(
                await host.SignaturesAsync(type.Types[i], true, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
        for (int i = 0; i < mixins.Length; i++)
        {
            var constructors = await host.SignaturesAsync(type.Types[i], true, cancellation).ConfigureAwait(false);
            if (constructors.Count != 0)
                constructorCount++;
            if (mixins[i])
            {
                if (firstMixin < 0)
                    firstMixin = i;
                mixinCount++;
            }
        }
        if (constructorCount != 0 && constructorCount == mixinCount)
        {
            mixins[firstMixin] = false;
            mixinCount--;
        }
        var calls = new List<Signature>();
        var constructs = new List<Signature>();
        var indexes = new List<IndexInfo>();
        for (int i = 0; i < type.Types.Count; i++)
        {
            var part = type.Types[i];
            if (!mixins[i])
            {
                var constructors = await host.SignaturesAsync(part, true, cancellation).ConfigureAwait(false);
                if (constructors.Count != 0 && mixinCount > 0)
                {
                    var mixed = new List<Signature>();
                    foreach (var signature in constructors)
                    {
                        var result = context.CloneSignature(signature);
                        var returnType = await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false);
                        var types = new List<Type>();
                        for (int j = 0; j < mixins.Length; j++)
                            if (j == i)
                                types.Add(returnType);
                            else if (mixins[j])
                                types.Add(
                                    await signatures.ReturnAsync(
                                        (await host.SignaturesAsync(type.Types[j], true, cancellation).ConfigureAwait(false))[0],
                                        cancellation).ConfigureAwait(false));
                        result.ResolvedReturnType = await algebra.IntersectionAsync(
                            types,
                            cancellation: cancellation).ConfigureAwait(false);
                        mixed.Add(result);
                    }
                    constructors = mixed.AsReadOnly();
                }
                await AppendAsync(constructs, constructors, cancellation).ConfigureAwait(false);
            }
            await AppendAsync(
                calls,
                await host.SignaturesAsync(part, false, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
            foreach (var index in await host.IndexesAsync(part, cancellation).ConfigureAwait(false))
                await mapped.AppendIndexAsync(indexes, index, false, cancellation).ConfigureAwait(false);
        }
        cancellation.ThrowIfCancellationRequested();
        Set(type, calls.AsReadOnly(), constructs.AsReadOnly(), indexes.AsReadOnly());
    }

    private async ValueTask<bool> MixinAsync(IReadOnlyList<Signature> signatures, CancellationToken cancellation)
    {
        if (signatures.Count != 1)
            return false;
        var signature = signatures[0];
        if (signature.TypeParameters.Count != 0 || signature.Parameters.Count != 1 || !signature.HasRestParameter)
            return false;
        var parameter = await host.ParameterTypeAsync(signature.Parameters[0], cancellation).ConfigureAwait(false);
        return (parameter.Flags & TypeFlags.Any) != 0
            || await host.ArrayElementAsync(parameter, cancellation).ConfigureAwait(false) == context.AnyType;
    }

    private async ValueTask AppendAsync(List<Signature> result, IReadOnlyList<Signature> source, CancellationToken cancellation)
    {
        foreach (var signature in source)
        {
            bool found = false;
            foreach (var current in result)
                if (await host.IdenticalSignaturesAsync(current, signature, cancellation).ConfigureAwait(false))
                {
                    found = true;
                    break;
                }
            if (!found)
                result.Add(signature);
        }
    }

    private static void Set(
        StructuredType type,
        IReadOnlyList<Signature> calls,
        IReadOnlyList<Signature> constructs,
        IReadOnlyList<IndexInfo> indexes)
    {
        type.Members = null;
        type.Properties = [];
        type.CallSignatures = calls;
        type.ConstructSignatures = constructs;
        type.IndexInfos = indexes;
        type.ObjectFlags |= ObjectFlags.MembersResolved;
    }
}

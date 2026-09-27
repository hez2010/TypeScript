using TypeScript.Compiler.Text;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed class SignatureInstantiation(TypeContext context, TypeInstantiation instantiation, TypeConstraints constraints,
    Signatures signatures, StructuredMembers members, CheckerSymbols symbols, TypeRelations relations)
{
    private readonly Dictionary<(Signature, TypeCacheKey), Signature> cache = [];

    internal async ValueTask<IReadOnlyList<Type>> ParametersAsync(Signature signature, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (signature.Context != context)
            throw new ArgumentException("Signature belongs to another checker", nameof(signature));
        var result = new Type[signature.TypeParameters.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var parameter = signature.TypeParameters[i];
            result[i] = await instantiation.InstantiateAsync(parameter, parameter.Mapper, cancellation: cancellation).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Signature parameter instantiation returned no type");
        }
        return result;
    }

    internal async ValueTask<Signature> GetAsync(Signature signature, IReadOnlyList<Type> arguments, bool javaScript = false,
        IReadOnlyList<TypeParameter>? inferredParameters = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (signature.Context != context)
            throw new ArgumentException("Signature belongs to another checker", nameof(signature));
        var types = await constraints.FillMissingArgumentsAsync(arguments, signature.TypeParameters, javaScript,
            (s, t, token) => relations.RelatedAsync(s, t, RelationKind.Identity, token), cancellation).ConfigureAwait(false);
        var result = await WithoutFillingAsync(signature, types, cancellation).ConfigureAwait(false);
        if (inferredParameters is { Count: > 0 }
            && await SingleAsync(
                await signatures.ReturnAsync(result, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false) is { } returned)
        {
            var newReturn = context.CloneSignature(returned);
            newReturn.TypeParameters = inferredParameters.ToArray();
            var returnType = FromSignature(newReturn);
            returnType.Mapper = result.Mapper;
            var newResult = context.CloneSignature(result);
            newResult.ResolvedReturnType = returnType;
            return newResult;
        }
        return result;
    }

    internal async ValueTask<Signature> WithoutFillingAsync(
        Signature signature,
        IReadOnlyList<Type> arguments,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (signature.Context != context)
            throw new ArgumentException("Signature belongs to another checker", nameof(signature));
        foreach (var argument in arguments)
            context.RequireOwned(argument);
        var key = (signature, new TypeCacheKey(arguments.ToArray()));
        if (cache.TryGetValue(key, out var result))
            return result;
        result = await CreateAsync(signature, arguments, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return cache[key] = result;
    }

    internal async ValueTask<Signature> CreateAsync(Signature signature, IReadOnlyList<Type> arguments,
        CancellationToken cancellation = default)
    {
        foreach (var argument in arguments)
            context.RequireOwned(argument);
        return await instantiation.SignatureAsync(signature,
            TypeMapper.Create((await ParametersAsync(signature, cancellation).ConfigureAwait(false)).ToArray(), arguments.ToArray()),
            true, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Signature?> SingleAsync(Type type, CancellationToken cancellation)
    {
        if (type is not ObjectType obj)
            return null;
        var resolved = await members.ResolveAsync(obj, cancellation).ConfigureAwait(false);
        if ((resolved.Properties?.Count ?? 0) != 0 || resolved.IndexInfos.Count != 0)
            return null;
        return resolved.CallSignatures.Count == 1 && resolved.ConstructSignatures.Count == 0 ? resolved.CallSignatures[0]
            : resolved.ConstructSignatures.Count == 1 && resolved.CallSignatures.Count == 0 ? resolved.ConstructSignatures[0] : null;
    }

    internal ObjectType FromSignature(Signature signature)
    {
        if (signature.Context != context)
            throw new ArgumentException("Signature belongs to another checker", nameof(signature));
        if (signature.IsolatedSignatureType is ObjectType cached)
            return cached;
        bool construct = signature.Declaration?.Kind is null or SyntaxKind.Constructor or SyntaxKind.ConstructSignature
            or SyntaxKind.ConstructorType;
        var result = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.SingleSignatureType,
            signature.Declaration is { } node ? symbols.Declaration(node) : null);
        result.Members = new Dictionary<TextSlice, Symbol>().AsReadOnly();
        result.Properties = [];
        result.CallSignatures = construct ? [] : [signature];
        result.ConstructSignatures = construct ? [signature] : [];
        result.IndexInfos = [];
        result.ObjectFlags |= ObjectFlags.MembersResolved;
        signature.IsolatedSignatureType = result;
        return result;
    }
}

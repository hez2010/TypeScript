using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

// Explicitly seeded declaration/instantiation dependencies for constraint tests.
// General AST checking, mapped types and conditional evaluation are not faked here.
internal sealed class ConstraintFixtureHost(TypeContext context, AlgebraFixtureHost properties) : ITypeConstraintHost
{
    internal Dictionary<SyntaxNode, Func<CancellationToken, ValueTask<Type>>> Nodes { get; } = [];
    internal List<int> Diagnostics { get; } = [];
    internal HashSet<ConditionalType> Restrictive { get; } = [];
    internal Dictionary<ConditionalType, Type> ConditionalInstantiations { get; } = [];
    internal Func<Type, bool, CancellationToken, ValueTask<Type>>? Simplifier { get; set; }
    internal Func<Type?, TypeMapper?, CancellationToken, ValueTask<Type?>>? Instantiator { get; set; }
    internal Func<SyntaxNode, CancellationToken, ValueTask<Type>>? NodeEvaluator { get; set; }
    internal IndexedTypes? Indexed { get; set; }
    internal MappedTypes? Mapped { get; set; }
    internal TypeKeys? Keys { get; set; }
    internal ConditionalTypes? Conditionals { get; set; }
    internal Func<Type, bool>? RestrictivePredicate { get; set; }
    private readonly Dictionary<(Type, Type, AccessFlags), Type> indexedAccesses = [];
    private readonly TypeVariables variables = new((type, _) => ValueTask.FromResult(type.ResolvedTypeArguments
        ?? throw new InvalidOperationException("Fixture requires deferred type arguments")));

    public ValueTask<Type> SimplifyAsync(Type type, bool writing, CancellationToken cancellation)
            => Simplifier?.Invoke(type, writing, cancellation) ?? ValueTask.FromResult(type);

    public ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation)
        => NodeEvaluator is { } evaluate ? evaluate(node, cancellation) : Nodes[node](cancellation);

    public ValueTask<Type?> InferredParameterConstraintAsync(TypeParameter parameter, CancellationToken cancellation)
    {
        if (parameter.Symbol?.Declarations.Any(d => d.Parent is InferTypeNode) == true)
            throw new InvalidOperationException("Fixture requires inference from an infer declaration");
        return ValueTask.FromResult<Type?>(null);
    }

    public async ValueTask<Type?> InstantiateAsync(Type? type, TypeMapper? mapper, CancellationToken cancellation)
    {
        if (Instantiator is { } instantiator)
            return await instantiator(type, mapper, cancellation).ConfigureAwait(false);
        if (type is null || mapper is null || !await variables.CouldContainAsync(type, cancellation).ConfigureAwait(false))
            return type;
        if (type is TypeParameter)
        {
            return await mapper.MapTypeAsync(type, cancellation).ConfigureAwait(false);
        }
        if (type is IntrinsicType or LiteralType || type == context.NoConstraintType || type == context.CircularConstraintType)
            return type;
        throw new InvalidOperationException("Fixture requires structural type instantiation");
    }

    public ValueTask<bool> IsGenericMappedAsync(MappedType type, CancellationToken cancellation)
            =>
                Mapped?.IsGenericAsync(
                    type,
                    cancellation) ?? throw new InvalidOperationException("Fixture requires mapped type generic analysis");

    public ValueTask<Type?> MappedNameTypeAsync(MappedType type, CancellationToken cancellation)
            => Mapped?.NameAsync(type, cancellation) ?? throw new InvalidOperationException("Fixture requires mapped name resolution");

    public bool HasKeyofConstraint(MappedType type) => MappedMembers.HasKeyofConstraint(type);

    public ValueTask<Type> MappedIndexTypeAsync(MappedType type, CancellationToken cancellation)
        =>
            Keys?.MappedAsync(
                type,
                cancellation: cancellation) ?? throw new InvalidOperationException("Fixture requires mapped index resolution");

    public ValueTask<bool> IsMappedGenericAccessAsync(IndexedAccessType type, CancellationToken cancellation)
        => Indexed is not null ? Indexed.IsMappedGenericAccessAsync(type, cancellation) : type.ObjectType is MappedType
            ? throw new InvalidOperationException("Fixture requires generic mapped access analysis") : ValueTask.FromResult(false);

    public ValueTask<Type> SubstituteMappedAccessAsync(Type objectType, Type indexType, CancellationToken cancellation)
            => Indexed?.SubstituteMappedAsync((MappedType)objectType, indexType, cancellation)
                ?? throw new InvalidOperationException("Fixture requires mapped access substitution");

    public ValueTask<Type?> IndexedAccessAsync(Type objectType, Type indexType, AccessFlags flags, CancellationToken cancellation)
    {
        if (Indexed is not null)
            return Indexed.TryGetAsync(objectType, indexType, flags, cancellation: cancellation);
        if (objectType is TypeParameter)
        {
            var key = (objectType, indexType, flags & AccessFlags.Persistent);
            if (!indexedAccesses.TryGetValue(key, out var type))
                indexedAccesses.Add(key, type = context.NewIndexedAccessType(key.objectType, key.indexType, key.Item3));
            return ValueTask.FromResult<Type?>(type);
        }
        return indexType is LiteralType { Value: string name } ? properties.GetPropertyTypeAsync(objectType, name, cancellation)
            : throw new InvalidOperationException("Fixture requires general indexed access");
    }

    public bool IsRestrictiveInstantiation(ConditionalType type) => RestrictivePredicate?.Invoke(type) ?? Restrictive.Contains(type);

    public ValueTask<Type> InstantiateConditionalAsync(
        ConditionalType type,
        TypeMapper mapper,
        bool forConstraint,
        CancellationToken cancellation)
            => Conditionals?.InstantiateAsync(type, mapper, forConstraint, cancellation: cancellation)
                ?? ValueTask.FromResult(ConditionalInstantiations.TryGetValue(type, out var result) ? result
                : throw new InvalidOperationException("Fixture requires conditional instantiation"));

    public ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation)
            =>
                ValueTask.FromResult(
                    type.ResolvedTypeArguments ?? throw new InvalidOperationException("Fixture requires deferred type arguments"));

    public bool IsArrayType(Type type) => type is TypeReference and not TypeReference { Target: TupleType }
            ? throw new InvalidOperationException("Fixture requires array target identity") : false;

    public ValueTask<Type> CreateTupleAsync(
        IReadOnlyList<Type> elements,
        IReadOnlyList<TupleElementInfo> infos,
        bool isReadonly,
        CancellationToken cancellation)
            => throw new InvalidOperationException("Fixture requires tuple normalization");

    public void CircularConstraint(TypeParameter parameter, SyntaxNode declaration) => Diagnostics.Add(2313);
}

using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

// One checker owns the mutually recursive instantiation, tuple, mapped-type and
// constraint services. All semantic dependencies resolve through that checker.
internal sealed class InstantiationServices : ITypeInstantiationHost, ITupleTypeHost, IObjectInstantiationHost,
    IMappedMemberHost, ITypeConstraintHost
{
    private readonly Checker checker;
    internal TupleTypes Tuples { get; }
    internal TypeInstantiation Engine { get; }
    internal ObjectInstantiation Objects { get; }
    internal MappedTypes Mapped { get; }
    internal MappedMembers Members { get; }
    internal TypeNodeFlow TypeNodeFlow { get; }
    internal TypeConstraints Constraints { get; }
    internal TypeResolutionStack Resolutions { get; }
    internal List<int> Diagnostics { get; } = [];
    internal List<int> ConstraintDiagnostics { get; } = [];

    internal InstantiationServices(TypeContext context, TypeAlgebra algebra, CheckerLinks links, Checker checker)
    {
        this.checker = checker;
        Tuples = new(context, algebra, links, this);
        Engine = new(context, algebra, links, this);
        Objects = new(context, links, Engine, new(TypeArgumentsAsync), this);
        Resolutions = new(links);
        Constraints = new(
            context,
            algebra,
            Resolutions,
            new(async (type, token) => await Members!.ModifiersTypeAsync(type, token).ConfigureAwait(false)),
            this);
        Mapped = new(context, algebra, Engine, Objects, Tuples, Resolutions, this);
        Members = new(context, algebra, Engine, Mapped, links, Resolutions, algebra.Order, this);
        TypeNodeFlow = new(context, algebra, Mapped, this);
    }

    public Type ArrayTarget(bool isReadonly) => checker.ArrayTarget(isReadonly);

    public bool IsArrayType(Type type) =>
        type is TypeReference reference && (reference.Target == ArrayTarget(false) || reference.Target == ArrayTarget(true));

    public bool IsReadonlyArrayType(Type type) => type is TypeReference reference && reference.Target == ArrayTarget(true);

    public ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation) =>
        checker.TypeArgumentsAsync(type, cancellation);

    public ValueTask<Type> NormalizedReferenceAsync(InterfaceType target, IReadOnlyList<Type> arguments, CancellationToken cancellation) =>
        Tuples.NormalizeReferenceAsync(target, arguments, cancellation: cancellation);

    public ValueTask<Type> ObjectInstantiationAsync(ObjectType type, TypeMapper mapper, TypeAlias? alias, CancellationToken cancellation) =>
        Objects.InstantiateAsync(type, mapper, alias, cancellation);

    public ValueTask<IReadOnlyList<Type>> OuterTypeParametersAsync(SyntaxNode declaration, CancellationToken cancellation) =>
        checker.OuterParametersAsync(declaration, cancellation);

    public ValueTask<Symbol?> TypeReferenceSymbolAsync(TypeReferenceNode reference, CancellationToken cancellation) =>
        checker.ReferenceSymbolAsync(reference, cancellation);

    public ValueTask<Symbol> ResolvedSymbolAsync(IdentifierNode identifier, CancellationToken cancellation) =>
        checker.ValueSymbolAsync(identifier, cancellation);

    public ValueTask<TypeAlias?> AliasForTypeNodeAsync(SyntaxNode node, CancellationToken cancellation) =>
        checker.AliasAsync(node, cancellation);

    public ValueTask<TypeParameter> MappedParameterAsync(MappedType type, CancellationToken cancellation) =>
        Mapped.ParameterAsync(type, cancellation);

    public ValueTask<Type> InstantiateMappedAsync(MappedType type, TypeMapper mapper, TypeAlias? alias, CancellationToken cancellation) =>
        Mapped.InstantiateAsync(type, mapper, alias, cancellation);

    public ValueTask<TypeParameter> DeclaredParameterAsync(TypeParameterDeclarationNode declaration, CancellationToken cancellation) =>
        checker.ParameterAsync(declaration, cancellation);

    public ValueTask<Type?> ParameterConstraintAsync(TypeParameter parameter, CancellationToken cancellation) =>
        Constraints.ParameterConstraintAsync(parameter, cancellation);

    public ValueTask<MappedType> DeclaredMappedTypeAsync(MappedTypeNode node, CancellationToken cancellation) =>
        checker.MappedNodeAsync(node, cancellation);

    public ValueTask<Type> ApparentTypeAsync(Type type, CancellationToken cancellation) => checker.ApparentAsync(type, cancellation);

    public ValueTask<IReadOnlyList<Symbol>> PropertiesAsync(Type type, CancellationToken cancellation) =>
        checker.PropertiesAsync(type, cancellation);

    public ValueTask<IReadOnlyList<IndexInfo>> IndexInfosAsync(Type type, CancellationToken cancellation) =>
        checker.IndexesAsync(type, cancellation);

    public ValueTask<Symbol?> PropertyAsync(Type type, string name, CancellationToken cancellation) =>
        checker.PropertyAsync(type, name, cancellation);

    public ValueTask<Type> PropertyNameTypeAsync(Symbol symbol, CancellationToken cancellation) =>
        checker.PropertyNameTypeAsync(symbol, cancellation);

    public bool IsReadonly(Symbol symbol) => checker.IsReadonly(symbol);

    public ValueTask<IndexInfo?> ApplicableIndexAsync(Type type, Type key, CancellationToken cancellation) =>
        checker.ApplicableIndexAsync(type, key, cancellation);

    public ValueTask<Type> ConditionalInstantiationAsync(ConditionalType type, TypeMapper mapper, CancellationToken cancellation) =>
        checker.ConditionalInstantiationAsync(type, mapper, null, cancellation);

    public ValueTask<Type> ConditionalInstantiationAsync(
        ConditionalType type,
        TypeMapper mapper,
        TypeAlias? alias,
        CancellationToken cancellation) => checker.ConditionalInstantiationAsync(type, mapper, alias, cancellation);

    public ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation) =>
        checker.TypeFromNodeAsync(node, cancellation);

    public ValueTask<Type> ReducedTypeAsync(Type type, CancellationToken cancellation) => checker.ReducedTypeAsync(type, cancellation);

    public ValueTask<Type> WithoutUndefinedFactsAsync(Type type, CancellationToken cancellation) =>
        checker.Facts.FilterAsync(type, TypeFacts.NEUndefined, cancellation);

    public ValueTask<Type?> InferReverseMappedAsync(Type source, MappedType mapped, IndexType constraint, CancellationToken cancellation) =>
        checker.ReverseMappedInferenceAsync(source, mapped, constraint, cancellation);

    public ValueTask<bool> IsGenericMappedAsync(Type type, CancellationToken cancellation) =>
        type is MappedType mapped ? Mapped.IsGenericAsync(mapped, cancellation) : ValueTask.FromResult(false);

    public async ValueTask<Type?> ArrayLikeElementAsync(Type type, CancellationToken cancellation) =>
        await checker.ArrayLikeAsync(type, cancellation).ConfigureAwait(false)
            ? await checker.NumberIndexAsync(type, cancellation).ConfigureAwait(false)
            : null;

    public ValueTask<Type> IndexTypeAsync(Type type, CancellationToken cancellation) => checker.IndexAsync(type, cancellation);

    public ValueTask<Type> IndexedAccessAsync(Type objectType, Type indexType, CancellationToken cancellation) =>
        checker.IndexedAccessAsync(objectType, indexType, 0, null, cancellation);

    public ValueTask<Type> IndexedAccessAsync(
        Type objectType,
        Type indexType,
        AccessFlags flags,
        TypeAlias? alias,
        CancellationToken cancellation) => checker.IndexedAccessAsync(objectType, indexType, flags, alias, cancellation);

    public async ValueTask<bool> IsGenericTypeAsync(Type type, CancellationToken cancellation) =>
        await Mapped.GenericFlagsAsync(type, cancellation).ConfigureAwait(false) != 0;

    public ValueTask<bool> IsAssignableAsync(Type source, Type target, CancellationToken cancellation) =>
        checker.AssignableAsync(source, target, cancellation);

    public ValueTask<bool> IsEmptyAnonymousAsync(Type type, CancellationToken cancellation) =>
        checker.Views.EmptyAnonymousAsync(type, cancellation);

    public ValueTask<Type> SimplifyAsync(Type type, bool writing, CancellationToken cancellation) =>
        checker.SimplifyAsync(type, writing, cancellation);

    public ValueTask<Type?> InferredParameterConstraintAsync(TypeParameter parameter, CancellationToken cancellation) =>
        checker.InferredConstraints.GetAsync(parameter, cancellation: cancellation);

    public ValueTask<Type?> InstantiateAsync(Type? type, TypeMapper? mapper, CancellationToken cancellation) =>
        Engine.InstantiateAsync(type, mapper, cancellation: cancellation);

    public ValueTask<bool> IsGenericMappedAsync(MappedType type, CancellationToken cancellation) =>
        Mapped.IsGenericAsync(type, cancellation);

    public ValueTask<Type?> MappedNameTypeAsync(MappedType type, CancellationToken cancellation) => Mapped.NameAsync(type, cancellation);

    public bool HasKeyofConstraint(MappedType type) => MappedMembers.HasKeyofConstraint(type);

    public ValueTask<Type> MappedIndexTypeAsync(MappedType type, CancellationToken cancellation) =>
        checker.Keys.MappedAsync(type, cancellation: cancellation);

    public ValueTask<bool> IsMappedGenericAccessAsync(IndexedAccessType type, CancellationToken cancellation) =>
        checker.Indexed.IsMappedGenericAccessAsync(type, cancellation);

    public ValueTask<Type> SubstituteMappedAccessAsync(Type objectType, Type indexType, CancellationToken cancellation) =>
        checker.Indexed.SubstituteMappedAsync((MappedType)objectType, indexType, cancellation);

    ValueTask<Type?> ITypeConstraintHost.IndexedAccessAsync(
        Type objectType,
        Type indexType,
        AccessFlags flags,
        CancellationToken cancellation) => checker.Indexed.TryGetAsync(objectType, indexType, flags, cancellation: cancellation);

    public bool IsRestrictiveInstantiation(ConditionalType type) => Engine.IsRestrictive(type);

    public ValueTask<Type> InstantiateConditionalAsync(
        ConditionalType type,
        TypeMapper mapper,
        bool forConstraint,
        CancellationToken cancellation) => checker.Conditionals.InstantiateAsync(type, mapper, forConstraint, cancellation: cancellation);

    public ValueTask<Type> CreateTupleAsync(
        IReadOnlyList<Type> elements,
        IReadOnlyList<TupleElementInfo> infos,
        bool isReadonly,
        CancellationToken cancellation) => Tuples.CreateAsync(elements, infos, isReadonly, cancellation);

    public void CircularConstraint(TypeParameter parameter, SyntaxNode declaration) => ConstraintDiagnostics.Add(2313);

    public void CircularProperty(Symbol symbol, MappedType type) => Diagnostics.Add(2615);

    public void InstantiationLimit(int depth, int count) => Diagnostics.Add(2589);

    public void TupleTooLarge() => Diagnostics.Add(2800);

    public void CrossProductTooLarge(long size) => Diagnostics.Add(2590);
}

using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : ITypeNormalizationHost, ITypeRelationHost, ITypeIdentityHost
{
    internal TypeNormalization Normalization { get; }
    internal RelationKeys RelationKeys { get; }
    internal TypeRelations Relations { get; }
    internal TypeIdentity Identity { get; }

    public ValueTask<Ternary> MappedRelationAsync(
        RelationOperation operation,
        MappedType source,
        MappedType target,
        CancellationToken cancellation)
            => Generics.MappedAsync(operation, source, target, cancellation);

    public ValueTask<Type> SimplifyAsync(Type type, bool writing, CancellationToken cancellation)
            => type is IndexedAccessType indexed ? Indexed.SimplifyAsync(indexed, writing, cancellation)
                : type is ConditionalType conditional ? Conditionals.SimplifyAsync(conditional, writing, cancellation)
                : ValueTask.FromResult(type);

    public ValueTask<Ternary> IdentityAsync(RelationOperation operation, Type source, Type target, CancellationToken cancellation)
            => Identity.CompareAsync(operation, source, target, cancellation);

    public ValueTask<Ternary> RelatedAsync(
        RelationOperation operation,
        Type source,
        Type target,
        RecursionFlags recursion,
        IntersectionState intersection,
        CancellationToken cancellation)
            => Structural.RelatedAsync(operation, source, target, recursion, intersection, cancellation);

    public ValueTask<bool> EnumRelatedAsync(Symbol source, Symbol target, CancellationToken cancellation)
            => source == target ? ValueTask.FromResult(true) : throw new InvalidOperationException("Checker requires enum relations");

    public void ComplexityOverflow(Type source, Type target) => Diagnostics.Add(2859);

    public ValueTask<Type> ConditionalBranchAsync(ConditionalType type, bool whenTrue, CancellationToken cancellation)
        => whenTrue ? Instantiation.Constraints.ConditionalTrueAsync(type, cancellation: cancellation)
            : Instantiation.Constraints.ConditionalFalseAsync(type, cancellation);

    public ValueTask<Ternary?> VarianceAsync(RelationOperation operation, Type source, Type target, CancellationToken cancellation)
        => Variances.RelateAsync(operation, source, target, cancellation: cancellation);
}

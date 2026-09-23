using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal sealed partial class ProgramTypeHost : ITypeNormalizationHost, ITypeRelationHost, ITypeIdentityHost
{
    internal TypeNormalization Normalization { get; }
    internal RelationKeys RelationKeys { get; }
    internal TypeRelations Relations { get; }
    internal TypeIdentity Identity { get; }

    public ValueTask<Type> SimplifyAsync(Type type, bool writing, CancellationToken cancellation)
            => type is IndexedAccessType indexed ? Indexed.SimplifyAsync(indexed, writing, cancellation)
                : type is ConditionalType ? throw new InvalidOperationException("Probe requires conditional simplification")
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
            => source == target ? ValueTask.FromResult(true) : throw new InvalidOperationException("Probe requires enum relations");

    public void ComplexityOverflow(Type source, Type target) => Diagnostics.Add(2859);

    public ValueTask<Type> ConditionalBranchAsync(ConditionalType type, bool whenTrue, CancellationToken cancellation)
            => throw new InvalidOperationException("Probe requires conditional branches");

    public ValueTask<Ternary?> VarianceAsync(RelationOperation operation, Type source, Type target, CancellationToken cancellation)
        => Variances.RelateAsync(operation, source, target, cancellation: cancellation);
}

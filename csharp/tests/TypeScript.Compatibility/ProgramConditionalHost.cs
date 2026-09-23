using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal sealed partial class ProgramTypeHost : IConditionalTypeHost, IConditionalRelationHost
{
    internal ConditionalTypes Conditionals { get; }
    internal ConditionalRelations ConditionalRelations { get; }

    public ValueTask<Type> ConditionalInstantiationAsync(
        ConditionalType type,
        TypeMapper mapper,
        TypeAlias? alias,
        CancellationToken cancellation)
            => Conditionals.InstantiateAsync(type, mapper, alias: alias, cancellation: cancellation);

    public ValueTask<TypeMapper> InferConditionalAsync(IReadOnlyList<TypeParameter> parameters, Type check, Type extends,
            TypeMapper? mapper, bool deferred, CancellationToken cancellation)
            => throw new InvalidOperationException("Probe requires conditional inference");

    public void ConditionalDepthExceeded() => Diagnostics.Add(2589);

    public ValueTask<TypeMapper> InferConditionalRelationAsync(IReadOnlyList<TypeParameter> parameters, Type source, Type target,
            RelationOperation operation, CancellationToken cancellation)
            => throw new InvalidOperationException("Probe requires conditional relation inference");
}

using TypeScript.Compiler.Ast;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IConditionalTypeHost, IConditionalRelationHost
{
    internal ConditionalTypes Conditionals { get; }
    internal ConditionalRelations ConditionalRelations { get; }

    public ValueTask<Type> ConditionalInstantiationAsync(
        ConditionalType type,
        TypeMapper mapper,
        TypeAlias? alias,
        CancellationToken cancellation)
            => Conditionals.InstantiateAsync(type, mapper, alias: alias, cancellation: cancellation);

    public async ValueTask<TypeMapper> InferConditionalAsync(IReadOnlyList<TypeParameter> parameters, Type check, Type extends,
            TypeMapper? mapper, bool deferred, CancellationToken cancellation)
    {
        var inference = Inference.Create(parameters);
        if (mapper is not null)
            inference.NonFixingMapper = TypeMapper.CombineAsync(inference.NonFixingMapper, mapper, InstantiateInferenceAsync);
        if (!deferred)
            await Inference.InferAsync(
                inference,
                check,
                extends,
                InferencePriority.NoConstraints | InferencePriority.AlwaysStrict,
                cancellation: cancellation);
        return mapper is null ? inference.Mapper : TypeMapper.CombineAsync(inference.Mapper, mapper, InstantiateInferenceAsync);
    }

    public void ConditionalDepthExceeded() => Diagnostics.Add(2589);

    public async ValueTask<TypeMapper> InferConditionalRelationAsync(IReadOnlyList<TypeParameter> parameters, Type source, Type target,
            RelationOperation operation, CancellationToken cancellation)
    {
        var inference = Inference.Create(parameters, compare: (s, t, token) => operation.CompareAsync(s, t, cancellation: token));
        await Inference.InferAsync(
            inference,
            source,
            target,
            InferencePriority.NoConstraints | InferencePriority.AlwaysStrict,
            cancellation: cancellation);
        return inference.Mapper;
    }

    private async ValueTask<Type> InstantiateInferenceAsync(Type type, TypeMapper mapper, CancellationToken cancellation)
        => await Instantiation.Engine.InstantiateAsync(type, mapper, cancellation: cancellation)
            ?? throw new InvalidOperationException("Inference mapper instantiation returned no type");
}

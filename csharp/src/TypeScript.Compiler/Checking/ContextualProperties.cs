using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Semantics;

namespace TypeScript.Compiler.Checking;

internal sealed class ContextualProperties(TypeContext context, CheckerLinks links, TypeAlgebra algebra,
    TypeProperties properties, SymbolTypes values, StructuredMembers members, MappedTypes mapped,
    MappedMembers mappedMembers, IndexedTypes indexed, IndexSignatures indexes, TupleTypes tuples,
    TypeConstraints constraints, TypeViews views, TypeRelations relations, TypeResolutionStack resolutions)
{
    internal async ValueTask<Type?> GetAsync(Type type, Utf8String name, Type? nameType = null, CancellationToken cancellation = default)
    {
        Diagnostics.CompilationCapture.ProbeMark propertiesMark = Diagnostics.CompilationCapture.Mark();
        try
        {
            return await GetCoreAsync(type, name, nameType, cancellation).ConfigureAwait(false);
        }
        finally
        {
            Diagnostics.CompilationCapture.Report(Diagnostics.AllocationProbes.CtxProperties, propertiesMark);
        }
    }

    private async ValueTask<Type?> GetCoreAsync(Type type, Utf8String name, Type? nameType, CancellationToken cancellation)
    {
        // The common case is a single object type, and going through MapAsync would allocate a closure
        // and an async state machine for it on every contextual property query. Only the union case
        // needs the mapping machinery.
        if (type is not UnionType)
        {
            if ((type.Flags & TypeFlags.Never) != 0)
                return type;
            var single = await ConstituentAsync(type, name, nameType, cancellation).ConfigureAwait(false);
            if (single is not null)
                context.RequireOwned(single);
            return single;
        }
        return await algebra.MapAsync(type, part => ConstituentAsync(part, name, nameType, cancellation), true, cancellation)
            .ConfigureAwait(false);
    }

    private async ValueTask<Type?> ConstituentAsync(Type part, Utf8String name, Type? nameType, CancellationToken cancellation)
    {
        {
            if (part is IntersectionType intersection)
            {
                // Both lists are bounded by the intersection's constituent count, and the candidates
                // list is cleared and refilled as properties are found, so sizing them up front avoids
                // the doubling chain on every contextual query over an intersection.
                var types = new List<Type>(intersection.Types.Count);
                var candidates = new List<Type>(intersection.Types.Count);
                bool ignoreIndexes = false;
                foreach (var constituent in intersection.Types)
                {
                    if ((constituent.Flags & TypeFlags.Object) == 0)
                        continue;
                    if (await GenericMappedAsync(constituent, cancellation).ConfigureAwait(false))
                    {
                        Add(await MappedAsync((MappedType)constituent, name, nameType, cancellation).ConfigureAwait(false));
                        continue;
                    }
                    var property = await ConcreteAsync(constituent, name, cancellation).ConfigureAwait(false);
                    if (property is null)
                    {
                        if (!ignoreIndexes)
                            candidates.Add(constituent);
                        continue;
                    }
                    ignoreIndexes = true;
                    candidates.Clear();
                    Add(property);
                }
                foreach (var candidate in candidates)
                    Add(await IndexAsync(candidate, name, nameType, cancellation).ConfigureAwait(false));
                return types.Count == 0 ? null : types.Count == 1 ? types[0]
                    : await algebra.IntersectionAsync(types, cancellation: cancellation).ConfigureAwait(false);
                void Add(Type? value)
                {
                    if (value is not null)
                        types.Add((value.Flags & TypeFlags.Any) != 0 ? context.UnknownType : value);
                }
            }
            if ((part.Flags & TypeFlags.Object) == 0)
                return null;
            if (await GenericMappedAsync(part, cancellation).ConfigureAwait(false))
                return await MappedAsync((MappedType)part, name, nameType, cancellation).ConfigureAwait(false);
            return await ConcreteAsync(part, name, cancellation).ConfigureAwait(false)
                ?? await IndexAsync(part, name, nameType, cancellation).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> GenericMappedAsync(Type type, CancellationToken cancellation) => type is MappedType mapping
        && await mapped.IsGenericAsync(mapping, cancellation).ConfigureAwait(false)
        && await mappedMembers.NameKindAsync(mapping, cancellation).ConfigureAwait(false) != MappedTypeNameTypeKind.Remapping;

    private async ValueTask<Type?> ConcreteAsync(Type type, Utf8String name, CancellationToken cancellation)
    {
        var property = await properties.PropertyAsync(type, name, cancellation: cancellation).ConfigureAwait(false);
        if (property is null || (property.CheckFlags & CheckFlags.Mapped) != 0 && links.Values.Get(property).ResolvedType is null
            && resolutions.FindCycleStart(property, TypeSystemPropertyName.Type) >= 0)
            return null;
        return values.NonMissing(
            await values.GetAsync(property, cancellation).ConfigureAwait(false),
            (property.Flags & SymbolFlags.Optional) != 0);
    }

    private async ValueTask<Type?> IndexAsync(Type type, Utf8String name, Type? nameType, CancellationToken cancellation)
    {
        if (type is TypeReference { Target: TupleType tuple } reference
            && IndexSignatures.NumericName(name)
            && JsNumber.FromString(name) >= 0
            && await tuples.SliceElementAsync(
                reference,
                tuple.FixedLength,
                noReductions: true,
                cancellation: cancellation).ConfigureAwait(false) is { } rest)
            return rest;
        return (await indexes.ApplicableAsync(
            (await members.ResolveAsync((StructuredType)type, cancellation).ConfigureAwait(false)).IndexInfos,
            nameType ?? context.GetStringLiteralType(name), cancellation).ConfigureAwait(false))?.ValueType;
    }

    private async ValueTask<Type?> MappedAsync(MappedType type, Utf8String name, Type? nameType, CancellationToken cancellation)
    {
        var key = nameType ?? context.GetStringLiteralType(name);
        var constraint = await mapped.ConstraintAsync(type, cancellation).ConfigureAwait(false);
        if (await mapped.NameAsync(type, cancellation).ConfigureAwait(false) is { } mapping
                && await ExcludedAsync(mapping, key, cancellation).ConfigureAwait(false)
            || await ExcludedAsync(constraint, key, cancellation).ConfigureAwait(false))
            return null;
        return await relations.RelatedAsync(
            key,
            await constraints.BaseConstraintOrTypeAsync(constraint, cancellation).ConfigureAwait(false),
            RelationKind.Assignable, cancellation).ConfigureAwait(false)
            ? await indexed.SubstituteMappedAsync(type, key, cancellation).ConfigureAwait(false) : null;
    }

    private async ValueTask<bool> ExcludedAsync(Type type, Type key, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        if (type is ConditionalType conditional)
            return (await views.ReducedAsync(
                await constraints.ConditionalTrueAsync(conditional, cancellation: cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false)).Flags.HasFlag(TypeFlags.Never)
                && await mapped.ActualVariableAsync(
                    await constraints.ConditionalFalseAsync(conditional, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false)
                    == await mapped.ActualVariableAsync(conditional.CheckType, cancellation).ConfigureAwait(false)
                && await relations.RelatedAsync(key, conditional.ExtendsType, RelationKind.Assignable, cancellation).ConfigureAwait(false);
        if (type is IntersectionType intersection)
            foreach (var part in intersection.Types)
                if (await ExcludedAsync(part, key, cancellation).ConfigureAwait(false))
                    return true;
        return false;
    }
}

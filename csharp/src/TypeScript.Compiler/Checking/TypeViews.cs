using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface ITypeViewHost
{
    ValueTask<Type> GlobalAsync(Utf8String name, CancellationToken cancellation);

    ValueTask<Type> WithThisAsync(Type type, Type argument, bool apparent, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Symbol>> CompositePropertiesAsync(UnionOrIntersectionType type, CancellationToken cancellation);

    ValueTask<Type> SymbolTypeAsync(Symbol symbol, CancellationToken cancellation);

    bool IsArray(Type type);

    ValueTask<StructuredType> ResolveAsync(StructuredType type, CancellationToken cancellation);

    ValueTask<IReadOnlyDictionary<Utf8String, Symbol>> MembersAsync(Symbol symbol, CancellationToken cancellation);
}

// Apparent types expose primitive wrappers and constraints; reduction separately
// removes impossible discriminant/private-member intersections.
internal sealed class TypeViews(TypeContext context, TypeAlgebra algebra, TypeConstraints constraints, TypeInstantiation instantiation,
    MappedTypes mapped, MappedMembers members, ITypeViewHost host)
{
    private readonly Dictionary<Type, Type> apparentIntersections = [];

    internal async ValueTask<Type> ApparentAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        var original = type;
        if ((type.Flags & TypeFlags.Instantiable) != 0)
            type = await constraints.BaseConstraintAsync(type, cancellation).ConfigureAwait(false) ?? context.UnknownType;
        if (type is MappedType mappedType)
            return await MappedAsync(mappedType, cancellation).ConfigureAwait(false);
        if (type is TypeReference && (type.ObjectFlags & ObjectFlags.Reference) != 0 && type != original)
            return await host.WithThisAsync(type, original, false, cancellation).ConfigureAwait(false);
        if (type is IntersectionType intersection)
        {
            if (type == original)
            {
                if (intersection.ResolvedApparentType is { } cached)
                    return cached;
                var result = await host.WithThisAsync(type, original, true, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                return intersection.ResolvedApparentType = result;
            }
            if (apparentIntersections.TryGetValue(original, out var previous))
                return previous;
            var apparent = await host.WithThisAsync(type, original, true, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            apparentIntersections[original] = apparent;
            return apparent;
        }
        Utf8String? name = (type.Flags & TypeFlags.StringLike) != 0 ? Utf8String.Copy("String"u8) : (type.Flags & TypeFlags.NumberLike) != 0 ? Utf8String.Copy("Number"u8)
            : (type.Flags & TypeFlags.BigIntLike) != 0 ? Utf8String.Copy("BigInt"u8) : (type.Flags & TypeFlags.BooleanLike) != 0 ? Utf8String.Copy("Boolean"u8)
            : (type.Flags & TypeFlags.ESSymbolLike) != 0 ? Utf8String.Copy("Symbol"u8) : null;
        if (name is not null)
            return await host.GlobalAsync(name.Value, cancellation).ConfigureAwait(false);
        if ((type.Flags & TypeFlags.NonPrimitive) != 0 || (type.Flags & TypeFlags.Unknown) != 0 && !context.StrictNullChecks)
            return context.EmptyObjectType;
        return (type.Flags & TypeFlags.Index) != 0 ? context.StringNumberSymbolType : type;
    }

    private async ValueTask<Type> MappedAsync(MappedType type, CancellationToken cancellation)
    {
        if (type.ResolvedApparentType is { } cached)
            return cached;
        var target = (MappedType?)type.Target ?? type;
        var variable = await mapped.HomomorphicVariableAsync(target, cancellation).ConfigureAwait(false);
        Type result = type;
        if (variable is not null && target.Declaration!.NameType is null)
        {
            var modifiers = await members.ModifiersTypeAsync(type, cancellation).ConfigureAwait(false);
            var constraint = modifiers is MappedType nested && await mapped.IsGenericAsync(nested, cancellation).ConfigureAwait(false)
                ? await ApparentAsync(nested, cancellation).ConfigureAwait(false)
                : await constraints.BaseConstraintAsync(modifiers, cancellation).ConfigureAwait(false);
            if (constraint is not null
                && (constraint is UnionType union ? union.Types.All(ArrayOrTupleOrIntersection) : ArrayOrTupleOrIntersection(constraint)))
                result = await instantiation.InstantiateAsync(
                    target,
                    TypeMapper.Prepend(variable, constraint, type.Mapper),
                    cancellation: cancellation).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Apparent mapped instantiation returned no type");
        }
        cancellation.ThrowIfCancellationRequested();
        return type.ResolvedApparentType = result;
    }

    private bool ArrayOrTupleOrIntersection(Type type) =>
        ArrayOrTuple(type) || type is IntersectionType intersection && intersection.Types.All(ArrayOrTuple);

    private bool ArrayOrTuple(Type type) => host.IsArray(type) || type is TypeReference { Target: TupleType };

    internal async ValueTask<Type> ReducedAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type is UnionType union && (type.ObjectFlags & ObjectFlags.ContainsIntersections) != 0)
        {
            if (union.ResolvedReducedType is { } cached)
                return cached;
            var parts = new Type[union.Types.Count];
            bool changed = false;
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = await ReducedAsync(union.Types[i], cancellation).ConfigureAwait(false);
                changed |= parts[i] != union.Types[i];
            }
            var result = changed ? await algebra.UnionAsync(parts, cancellation: cancellation).ConfigureAwait(false) : union;
            cancellation.ThrowIfCancellationRequested();
            if (result is UnionType reduced)
                reduced.ResolvedReducedType = reduced;
            return union.ResolvedReducedType = result;
        }
        if (type is IntersectionType intersection)
        {
            if ((type.ObjectFlags & ObjectFlags.IsNeverIntersectionComputed) == 0)
            {
                var previous = type.ObjectFlags & (ObjectFlags.IsNeverIntersectionComputed | ObjectFlags.IsNeverIntersection);
                type.ObjectFlags |= ObjectFlags.IsNeverIntersectionComputed;
                try
                {
                    foreach (var property in await host.CompositePropertiesAsync(intersection, cancellation).ConfigureAwait(false))
                        if (await NeverPropertyAsync(property, cancellation).ConfigureAwait(false))
                        {
                            type.ObjectFlags |= ObjectFlags.IsNeverIntersection;
                            break;
                        }
                    cancellation.ThrowIfCancellationRequested();
                }
                catch
                {
                    type.ObjectFlags = type.ObjectFlags & ~(ObjectFlags.IsNeverIntersectionComputed | ObjectFlags.IsNeverIntersection) | previous;
                    throw;
                }
            }
            if ((type.ObjectFlags & ObjectFlags.IsNeverIntersection) != 0)
                return context.NeverType;
        }
        return type;
    }

    internal async ValueTask<bool> NeverPropertyAsync(Symbol property, CancellationToken cancellation = default)
        => (property.Flags & SymbolFlags.Optional) == 0
            && (property.CheckFlags & (CheckFlags.NonUniformAndLiteral | CheckFlags.HasNeverType)) == CheckFlags.NonUniformAndLiteral
            && ((await host.SymbolTypeAsync(property, cancellation).ConfigureAwait(false)).Flags & TypeFlags.Never) != 0
            || property.ValueDeclaration is null && (property.CheckFlags & CheckFlags.ContainsPrivate) != 0;

    internal async ValueTask<Type> ReducedApparentAsync(Type type, CancellationToken cancellation = default)
        =>
            await ReducedAsync(
                await ApparentAsync(await ReducedAsync(type, cancellation).ConfigureAwait(false), cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);

    internal async ValueTask<bool> EmptyAnonymousAsync(Type type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        return (type.ObjectFlags & ObjectFlags.Anonymous) != 0
            && ((type.ObjectFlags & ObjectFlags.MembersResolved) != 0 && EmptyResolved((StructuredType)type)
                || type.Symbol is { } symbol && (symbol.Flags & SymbolFlags.TypeLiteral) != 0
                    && (await host.MembersAsync(symbol, cancellation).ConfigureAwait(false)).Count == 0);
    }

    internal async ValueTask<bool> EmptyObjectAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type is ObjectType obj)
            return !(type is MappedType mappedType && await mapped.IsGenericAsync(mappedType, cancellation).ConfigureAwait(false))
                && EmptyResolved(await host.ResolveAsync(obj, cancellation).ConfigureAwait(false));
        if ((type.Flags & TypeFlags.NonPrimitive) != 0)
            return true;
        if (type is UnionOrIntersectionType composite)
        {
            foreach (var part in composite.Types)
            {
                bool empty = await EmptyObjectAsync(part, cancellation).ConfigureAwait(false);
                if (type is UnionType && empty)
                    return true;
                if (type is IntersectionType && !empty)
                    return false;
            }
            return type is IntersectionType;
        }
        return false;
    }

    private bool EmptyResolved(StructuredType type) => type != context.AnyFunctionType
        && type.Properties is null or { Count: 0 } && type.CallSignatures.Count == 0 && type.ConstructSignatures.Count == 0 && type.IndexInfos.Count == 0;

    internal async ValueTask<bool> UnknownLikeUnionAsync(Type type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (!context.StrictNullChecks || type is not UnionType union)
            return false;
        if ((type.ObjectFlags & ObjectFlags.IsUnknownLikeUnionComputed) == 0)
        {
            var previous = type.ObjectFlags & (ObjectFlags.IsUnknownLikeUnionComputed | ObjectFlags.IsUnknownLikeUnion);
            type.ObjectFlags |= ObjectFlags.IsUnknownLikeUnionComputed;
            try
            {
                if (union.Types.Count >= 3
                    && (union.Types[0].Flags & TypeFlags.Undefined) != 0
                    && (union.Types[1].Flags & TypeFlags.Null) != 0)
                    foreach (var part in union.Types)
                        if (await EmptyAnonymousAsync(part, cancellation).ConfigureAwait(false))
                        {
                            type.ObjectFlags |= ObjectFlags.IsUnknownLikeUnion;
                            break;
                        }
                cancellation.ThrowIfCancellationRequested();
            }
            catch
            {
                type.ObjectFlags = type.ObjectFlags & ~(ObjectFlags.IsUnknownLikeUnionComputed | ObjectFlags.IsUnknownLikeUnion) | previous;
                throw;
            }
        }
        return (type.ObjectFlags & ObjectFlags.IsUnknownLikeUnion) != 0;
    }
}

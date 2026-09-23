using System.Globalization;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using F = TypeScript.Compiler.Checking.TypeFlags;
using M = TypeScript.Compiler.Checking.MappedTypeModifiers;

namespace TypeScript.Compiler.Checking;

internal interface IMappedTypeHost
{
    ValueTask<TypeParameter> DeclaredParameterAsync(TypeParameterDeclarationNode declaration, CancellationToken cancellation);

    ValueTask<Type?> ParameterConstraintAsync(TypeParameter parameter, CancellationToken cancellation);

    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> ReducedTypeAsync(Type type, CancellationToken cancellation);

    bool IsArrayType(Type type);

    bool IsReadonlyArrayType(Type type);

    ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation);

    ValueTask<Type> IndexedAccessAsync(Type objectType, Type indexType, CancellationToken cancellation);

    ValueTask<Type> WithoutUndefinedFactsAsync(Type type, CancellationToken cancellation);
}

internal sealed class MappedTypes(TypeContext context, TypeAlgebra algebra, TypeInstantiation instantiation,
    ObjectInstantiation objects, TupleTypes tuples, TypeResolutionStack resolutions, IMappedTypeHost host)
{
    internal async ValueTask<TypeParameter> ParameterAsync(MappedType type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type.TypeParameter is { } cached)
            return cached;
        var result = await host.DeclaredParameterAsync(type.Declaration!.TypeParameter!, cancellation).ConfigureAwait(false);
        context.RequireOwned(result);
        return type.TypeParameter = result;
    }

    internal async ValueTask<Type> ConstraintAsync(MappedType type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type.ConstraintType is { } cached)
            return cached;
        var parameter = await ParameterAsync(type, cancellation).ConfigureAwait(false);
        var result = await host.ParameterConstraintAsync(parameter, cancellation).ConfigureAwait(false) ?? context.ErrorType;
        context.RequireOwned(result);
        return type.ConstraintType = result;
    }

    internal async ValueTask<Type?> NameAsync(MappedType type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type.Declaration!.NameType is not { } node)
            return null;
        if (type.NameType is { } cached)
            return cached;
        var result = await host.TypeFromNodeAsync(node, cancellation).ConfigureAwait(false);
        return type.NameType = await RequiredAsync(result, type.Mapper, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> TemplateAsync(MappedType type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type.TemplateType is { } cached)
            return cached;
        Type result = context.ErrorType;
        if (type.Declaration!.Type is { } node)
        {
            result = await host.TypeFromNodeAsync(node, cancellation).ConfigureAwait(false);
            context.RequireOwned(result);
            if (context.StrictNullChecks && (Modifiers(type) & M.IncludeOptional) != 0)
                result = await OptionalAsync(result, cancellation).ConfigureAwait(false);
            result = await RequiredAsync(result, type.Mapper, cancellation).ConfigureAwait(false);
        }
        return type.TemplateType = result;
    }

    internal async ValueTask<Type?> HomomorphicVariableAsync(MappedType type, CancellationToken cancellation = default)
    {
        var constraint = await ConstraintAsync(type, cancellation).ConfigureAwait(false);
        if (constraint is IndexType index)
        {
            var variable = await ActualVariableAsync(index.Target, cancellation).ConfigureAwait(false);
            if (variable is TypeParameter)
                return variable;
        }
        return null;
    }

    internal async ValueTask<bool> IsGenericAsync(MappedType type, CancellationToken cancellation = default)
    {
        var constraint = await ConstraintAsync(type, cancellation).ConfigureAwait(false);
        if ((await GenericFlagsAsync(constraint, cancellation).ConfigureAwait(false) & ObjectFlags.IsGenericIndexType) != 0)
            return true;
        var name = await NameAsync(type, cancellation).ConfigureAwait(false);
        if (name is null)
            return false;
        var parameter = await ParameterAsync(type, cancellation).ConfigureAwait(false);
        var mapped = await RequiredAsync(name, TypeMapper.Create([parameter], [constraint]), cancellation).ConfigureAwait(false);
        return (await GenericFlagsAsync(mapped, cancellation).ConfigureAwait(false) & ObjectFlags.IsGenericIndexType) != 0;
    }

    internal async ValueTask<ObjectFlags> GenericFlagsAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        ObjectFlags flags = 0;
        if (type is UnionOrIntersectionType or SubstitutionType)
        {
            if ((type.ObjectFlags & ObjectFlags.IsGenericTypeComputed) == 0)
            {
                if (type is UnionOrIntersectionType composite)
                    foreach (var part in composite.Types)
                        flags |= await GenericFlagsAsync(part, cancellation).ConfigureAwait(false);
                else
                {
                    var substitution = (SubstitutionType)type;
                    flags = await GenericFlagsAsync(substitution.BaseType, cancellation).ConfigureAwait(false)
                        | await GenericFlagsAsync(substitution.Constraint, cancellation).ConfigureAwait(false);
                }
                type.ObjectFlags |= ObjectFlags.IsGenericTypeComputed | flags;
            }
            return type.ObjectFlags & ObjectFlags.IsGenericType;
        }
        if ((type.Flags & F.InstantiableNonPrimitive) != 0
            || type is MappedType mapped && await IsGenericAsync(mapped, cancellation).ConfigureAwait(false)
            || TypeConstraints.IsGenericTuple(type))
            flags |= ObjectFlags.IsGenericObjectType;
        if ((type.Flags & (F.InstantiableNonPrimitive | F.Index)) != 0
            || (type.Flags & (F.TemplateLiteral | F.StringMapping)) != 0 && !TypeAlgebra.IsPatternLiteral(type))
            flags |= ObjectFlags.IsGenericIndexType;
        return flags;
    }

    internal async ValueTask<Type> ActualVariableAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        while (type is SubstitutionType substitution)
        {
            cancellation.ThrowIfCancellationRequested();
            type = substitution.BaseType;
        }
        if (type is IndexedAccessType indexed)
        {
            var target = await ActualVariableAsync(indexed.ObjectType, cancellation).ConfigureAwait(false);
            var index = await ActualVariableAsync(indexed.IndexType, cancellation).ConfigureAwait(false);
            if (target != indexed.ObjectType || index != indexed.IndexType)
            {
                var result = await host.IndexedAccessAsync(target, index, cancellation).ConfigureAwait(false);
                context.RequireOwned(result);
                return result;
            }
        }
        return type is TypeParameter parameter ? parameter.NonDistributed : type;
    }

    internal async ValueTask<Type> InstantiateAsync(MappedType type, TypeMapper mapper, TypeAlias? alias = null,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (alias is not null)
            foreach (var argument in alias.TypeArguments)
                context.RequireOwned(argument);
        var variable = await HomomorphicVariableAsync(type, cancellation).ConfigureAwait(false);
        if (variable is not null)
        {
            var mapped = await RequiredAsync(variable, mapper, cancellation).ConfigureAwait(false);
            if (variable != mapped)
            {
                var reduced = await host.ReducedTypeAsync(mapped, cancellation).ConfigureAwait(false);
                context.RequireOwned(reduced);
                if (reduced is UnionType union && alias is not null)
                {
                    var results = new Type[union.Types.Count];
                    for (int i = 0; i < results.Length; i++)
                        results[i] = await ConstituentAsync(union.Types[i]).ConfigureAwait(false);
                    return await algebra.UnionAsync(results, alias: alias, cancellation: cancellation).ConfigureAwait(false);
                }
                return await algebra.MapAsync(reduced, async constituent => await ConstituentAsync(constituent).ConfigureAwait(false),
                    cancellation: cancellation).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Homomorphic mapping removed all constituents");
            }
        }
        var constraint = await ConstraintAsync(type, cancellation).ConfigureAwait(false);
        if (await RequiredAsync(constraint, mapper, cancellation).ConfigureAwait(false) == context.WildcardType)
            return context.WildcardType;
        return await objects.AnonymousAsync(type, mapper, alias, cancellation).ConfigureAwait(false);

        async ValueTask<Type> ConstituentAsync(Type source)
        {
            if ((source.Flags & (F.AnyOrUnknown | F.InstantiableNonPrimitive | F.Object | F.Intersection)) == 0
                || source == context.WildcardType || IsError(source))
                return source;
            if (type.Declaration!.NameType is null)
            {
                if (host.IsArrayType(source) || (source.Flags & F.Any) != 0
                    && resolutions.FindCycleStart(variable!, TypeSystemPropertyName.ResolvedBaseConstraint) < 0
                    && await HasArrayConstraintAsync((TypeParameter)variable!, cancellation).ConfigureAwait(false))
                    return await ArrayAsync(
                        source,
                        type,
                        TypeMapper.Prepend(variable!, source, mapper),
                        cancellation).ConfigureAwait(false);
                if (source is TypeReference { Target: TupleType } tuple)
                    return await TupleAsync(tuple, type, variable!, mapper, cancellation).ConfigureAwait(false);
                if (source is IntersectionType intersection && intersection.Types.All(IsArrayOrTuple))
                {
                    var parts = new Type[intersection.Types.Count];
                    for (int i = 0; i < parts.Length; i++)
                        parts[i] = await ConstituentAsync(intersection.Types[i]).ConfigureAwait(false);
                    return await algebra.IntersectionAsync(parts, cancellation: cancellation).ConfigureAwait(false);
                }
            }
            return await objects.AnonymousAsync(
                type,
                TypeMapper.Prepend(variable!, source, mapper),
                cancellation: cancellation).ConfigureAwait(false);
        }
    }

    private bool IsArrayOrTuple(Type type) => host.IsArrayType(type) || type is TypeReference { Target: TupleType };

    private async ValueTask<bool> HasArrayConstraintAsync(TypeParameter parameter, CancellationToken cancellation)
    {
        var constraint = await host.ParameterConstraintAsync(parameter, cancellation).ConfigureAwait(false);
        return constraint is UnionType union ? union.Types.All(IsArrayOrTuple) : constraint is not null && IsArrayOrTuple(constraint);
    }

    private async ValueTask<Type> ArrayAsync(Type source, MappedType type, TypeMapper mapper, CancellationToken cancellation)
    {
        var element = await InstantiateTemplateAsync(type, context.NumberType, true, mapper, cancellation).ConfigureAwait(false);
        return IsError(element) ? context.ErrorType
            : await tuples.ArrayAsync(
                element,
                ModifiedReadonly(host.IsReadonlyArrayType(source), Modifiers(type)),
                cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> TupleAsync(TypeReference source, MappedType type, Type variable, TypeMapper mapper,
        CancellationToken cancellation)
    {
        var target = (TupleType)source.ReferencedType;
        var fixedMapper = target.FixedLength == 0 ? mapper : TypeMapper.Prepend(variable, source, mapper);
        var modifiers = Modifiers(type);
        var elements = await tuples.ElementsAsync(source, cancellation).ConfigureAwait(false);
        var results = new Type[elements.Count];
        var infos = target.ElementInfos.ToArray();
        for (int i = 0; i < results.Length; i++)
        {
            var flags = infos[i].Flags;
            if (i < target.FixedLength)
                results[i] = await InstantiateTemplateAsync(type, context.GetStringLiteralType(i.ToString(CultureInfo.InvariantCulture)),
                    (flags & ElementFlags.Optional) != 0, fixedMapper, cancellation).ConfigureAwait(false);
            else if ((flags & ElementFlags.Variadic) != 0)
                results[i] = await RequiredAsync(
                    type,
                    TypeMapper.Prepend(variable, elements[i], mapper),
                    cancellation).ConfigureAwait(false);
            else
            {
                var array = await tuples.ArrayAsync(elements[i], cancellation: cancellation).ConfigureAwait(false);
                var mapped = await RequiredAsync(type, TypeMapper.Prepend(variable, array, mapper), cancellation).ConfigureAwait(false);
                results[i] = host.IsArrayType(mapped)
                    ? (await host.TypeArgumentsAsync((TypeReference)mapped, cancellation).ConfigureAwait(false))[0] : context.UnknownType;
            }
            if ((modifiers & M.IncludeOptional) != 0)
            {
                if ((flags & ElementFlags.Required) != 0)
                    infos[i] = infos[i] with { Flags = ElementFlags.Optional };
            }
            else if ((modifiers & M.ExcludeOptional) != 0 && (flags & ElementFlags.Optional) != 0)
                infos[i] = infos[i] with { Flags = ElementFlags.Required };
        }
        return results.Contains(context.ErrorType) ? context.ErrorType
            : await tuples.CreateAsync(results, infos, ModifiedReadonly(target.IsReadonly, modifiers), cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> InstantiateTemplateAsync(MappedType type, Type key, bool isOptional, TypeMapper mapper,
        CancellationToken cancellation = default)
    {
        context.RequireOwned(key);
        var parameter = await ParameterAsync(type, cancellation).ConfigureAwait(false);
        var templateMapper = TypeMapper.Append(mapper, parameter, key);
        var template = await TemplateAsync((MappedType?)type.Target ?? type, cancellation).ConfigureAwait(false);
        var result = await RequiredAsync(template, templateMapper, cancellation).ConfigureAwait(false);
        var modifiers = Modifiers(type);
        if (context.StrictNullChecks && (modifiers & M.IncludeOptional) != 0 && !MaybeKind(result, F.Undefined | F.Void, cancellation))
            return await OptionalAsync(result, cancellation).ConfigureAwait(false);
        if (context.StrictNullChecks && (modifiers & M.ExcludeOptional) != 0 && isOptional)
        {
            result = context.ExactOptionalPropertyTypes ? algebra.Filter(result, t => t != context.MissingType)
                : await host.WithoutUndefinedFactsAsync(result, cancellation).ConfigureAwait(false);
            context.RequireOwned(result);
        }
        return result;
    }

    private ValueTask<Type> OptionalAsync(Type type, CancellationToken cancellation)
        => type == context.UndefinedOrMissingType || type is UnionType union && union.Types[0] == context.UndefinedOrMissingType
            ? ValueTask.FromResult(type) : algebra.UnionAsync([type, context.UndefinedOrMissingType], cancellation: cancellation);

    private async ValueTask<Type> RequiredAsync(Type type, TypeMapper? mapper, CancellationToken cancellation)
        => await instantiation.InstantiateAsync(type, mapper, cancellation: cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Instantiation of a present type returned no type");

    internal static bool MaybeKind(Type type, F kind, CancellationToken cancellation = default)
    {
        var pending = new Stack<Type>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if ((current.Flags & kind) != 0)
                return true;
            if (current is UnionOrIntersectionType composite)
                foreach (var part in composite.Types)
                    pending.Push(part);
        }
        return false;
    }

    internal static M Modifiers(MappedType type)
    {
        var declaration = type.Declaration!;
        M result = 0;
        if (declaration.ReadonlyToken is { } readonlyToken)
            result |= readonlyToken.Kind == K.MinusToken ? M.ExcludeReadonly : M.IncludeReadonly;
        if (declaration.QuestionToken is { } question)
            result |= question.Kind == K.MinusToken ? M.ExcludeOptional : M.IncludeOptional;
        return result;
    }

    internal static bool ModifiedReadonly(bool state, M modifiers)
        => (modifiers & M.IncludeReadonly) != 0 || (modifiers & M.ExcludeReadonly) == 0 && state;

    private bool IsError(Type type) => type == context.ErrorType || (type.Flags & F.Any) != 0 && type.Alias is not null;
}

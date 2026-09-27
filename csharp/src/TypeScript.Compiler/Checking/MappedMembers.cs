using System.Globalization;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using F = TypeScript.Compiler.Checking.TypeFlags;
using O = TypeScript.Compiler.Checking.ObjectFlags;
using M = TypeScript.Compiler.Checking.MappedTypeModifiers;

namespace TypeScript.Compiler.Checking;

internal interface IMappedMemberHost : IMappedTypeHost
{
    ValueTask<MappedType> DeclaredMappedTypeAsync(MappedTypeNode node, CancellationToken cancellation);

    ValueTask<Type> ApparentTypeAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Symbol>> PropertiesAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexInfosAsync(Type type, CancellationToken cancellation);

    ValueTask<Symbol?> PropertyAsync(Type type, string name, CancellationToken cancellation);

    ValueTask<Type> PropertyNameTypeAsync(Symbol symbol, CancellationToken cancellation);

    bool IsReadonly(Symbol symbol);

    ValueTask<IndexInfo?> ApplicableIndexAsync(Type type, Type key, CancellationToken cancellation);

    ValueTask<bool> IsAssignableAsync(Type source, Type target, CancellationToken cancellation);

    ValueTask<Type> IndexTypeAsync(Type type, CancellationToken cancellation);

    Type ArrayTarget(bool isReadonly);

    ValueTask<Type> ConditionalInstantiationAsync(ConditionalType type, TypeMapper mapper, CancellationToken cancellation);

    ValueTask CircularPropertyAsync(Symbol symbol, MappedType type, CancellationToken cancellation);
}

internal sealed class MappedMembers(TypeContext context, TypeAlgebra algebra, TypeInstantiation instantiation,
    MappedTypes mapped, CheckerLinks links, TypeResolutionStack resolutions, TypeOrder order, IMappedMemberHost host)
{
    internal static bool HasKeyofConstraint(MappedType type)
        => type.Declaration!.TypeParameter!.Constraint is TypeOperatorNode { Operator: SyntaxKind.KeyOfKeyword };

    internal async ValueTask<Type> ModifiersTypeAsync(MappedType type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type.ModifiersType is { } cached)
            return cached;
        Type result;
        if (HasKeyofConstraint(type))
        {
            var node = (TypeOperatorNode)type.Declaration!.TypeParameter!.Constraint!;
            result = await RequiredAsync(await host.TypeFromNodeAsync(node.Type!, cancellation).ConfigureAwait(false),
                type.Mapper, cancellation).ConfigureAwait(false);
        }
        else
        {
            var declared = await host.DeclaredMappedTypeAsync(type.Declaration!, cancellation).ConfigureAwait(false);
            var constraint = await mapped.ConstraintAsync(declared, cancellation).ConfigureAwait(false);
            var extended = constraint is TypeParameter parameter
                ? await host.ParameterConstraintAsync(
                    (TypeParameter)parameter.NonDistributed,
                    cancellation).ConfigureAwait(false) : constraint;
            result = extended is IndexType index
                ? await RequiredAsync(index.Target, type.Mapper, cancellation).ConfigureAwait(false) : context.UnknownType;
        }
        context.RequireOwned(result);
        return type.ModifiersType = result;
    }

    internal async ValueTask<MappedTypeNameTypeKind> NameKindAsync(MappedType type, CancellationToken cancellation = default)
    {
        var name = await mapped.NameAsync(type, cancellation).ConfigureAwait(false);
        if (name is null)
            return MappedTypeNameTypeKind.None;
        var parameter = await mapped.ParameterAsync(type, cancellation).ConfigureAwait(false);
        return await host.IsAssignableAsync(name, parameter, cancellation).ConfigureAwait(false)
            ? MappedTypeNameTypeKind.Filtering : MappedTypeNameTypeKind.Remapping;
    }

    internal static int Optionality(MappedType type)
    {
        var modifiers = MappedTypes.Modifiers(type);
        return (modifiers & M.ExcludeOptional) != 0 ? -1 : (modifiers & M.IncludeOptional) != 0 ? 1 : 0;
    }

    internal async ValueTask<int> CombinedOptionalityAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        while (type is MappedType mappedType)
        {
            int optionality = Optionality(mappedType);
            if (optionality != 0)
                return optionality;
            type = await ModifiersTypeAsync(mappedType, cancellation).ConfigureAwait(false);
        }
        if (type is not IntersectionType intersection)
            return 0;
        int result = await CombinedOptionalityAsync(intersection.Types[0], cancellation).ConfigureAwait(false);
        foreach (var part in intersection.Types.Skip(1))
            if (await CombinedOptionalityAsync(part, cancellation).ConfigureAwait(false) != result)
                return 0;
        return result;
    }

    internal async ValueTask<Type> LowerBoundAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        switch (type)
        {
            case IndexType index:
                var apparent = await host.ApparentTypeAsync(index.Target, cancellation).ConfigureAwait(false);
                context.RequireOwned(apparent);
                if (TypeConstraints.IsGenericTuple(apparent))
                {
                    var tuple = (TupleType)((TypeReference)apparent).ReferencedType;
                    var keys = new Type[tuple.FixedLength + 1];
                    for (int i = 0; i < tuple.FixedLength; i++)
                        keys[i] = context.GetStringLiteralType(i.ToString(CultureInfo.InvariantCulture));
                    keys[^1] = await host.IndexTypeAsync(host.ArrayTarget(tuple.IsReadonly), cancellation).ConfigureAwait(false);
                    return await algebra.UnionAsync(keys, cancellation: cancellation).ConfigureAwait(false);
                }
                var indexed = await host.IndexTypeAsync(apparent, cancellation).ConfigureAwait(false);
                context.RequireOwned(indexed);
                return indexed;
            case ConditionalType conditional when conditional.Root.IsDistributive:
                var check = await LowerBoundAsync(conditional.CheckType, cancellation).ConfigureAwait(false);
                if (check == conditional.CheckType)
                    return type;
                var result = await host.ConditionalInstantiationAsync(conditional,
                    TypeMapper.Prepend(conditional.Root.CheckType, check, conditional.Mapper), cancellation).ConfigureAwait(false);
                context.RequireOwned(result);
                return result;
            case UnionType:
                return await algebra.MapAsync(type, async part => await LowerBoundAsync(part, cancellation).ConfigureAwait(false),
                    noReductions: true, cancellation).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Key lower-bound mapping removed all constituents");
            case IntersectionType intersection:
                if (intersection.Types.Count == 2 && (intersection.Types[0].Flags & (F.String | F.Number | F.BigInt)) != 0
                    && intersection.Types[1] == context.EmptyTypeLiteralType)
                    return type;
                var parts = new Type[intersection.Types.Count];
                for (int i = 0; i < parts.Length; i++)
                    parts[i] = await LowerBoundAsync(intersection.Types[i], cancellation).ConfigureAwait(false);
                return await algebra.IntersectionAsync(parts, cancellation: cancellation).ConfigureAwait(false);
            default:
                return type;
        }
    }

    internal async ValueTask ResolveAsync(MappedType type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.ObjectFlags & O.MembersResolved) != 0)
            return;
        var oldMembers = type.Members;
        var oldProperties = type.Properties;
        var oldCalls = type.CallSignatures;
        var oldConstructs = type.ConstructSignatures;
        var oldIndexes = type.IndexInfos;
        var members = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        var indexes = new List<IndexInfo>();
        type.Members = null;
        type.Properties = [];
        type.CallSignatures = [];
        type.ConstructSignatures = [];
        type.IndexInfos = [];
        type.ObjectFlags |= O.MembersResolved;
        try
        {
            var parameter = await mapped.ParameterAsync(type, cancellation).ConfigureAwait(false);
            var constraint = await mapped.ConstraintAsync(type, cancellation).ConfigureAwait(false);
            var target = (MappedType?)type.Target ?? type;
            var nameType = await mapped.NameAsync(target, cancellation).ConfigureAwait(false);
            bool linkDeclarations = await NameKindAsync(target, cancellation).ConfigureAwait(false) != MappedTypeNameTypeKind.Remapping;
            var template = await mapped.TemplateAsync(target, cancellation).ConfigureAwait(false);
            var modifiersType = await host.ApparentTypeAsync(
                await ModifiersTypeAsync(type, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
            context.RequireOwned(modifiersType);
            var modifiers = MappedTypes.Modifiers(type);
            if (HasKeyofConstraint(type))
                await ForEachKeyAsync(modifiersType, AddKeyAsync, cancellation).ConfigureAwait(false);
            else
                foreach (var key in Parts(await LowerBoundAsync(constraint, cancellation).ConfigureAwait(false)))
                    await AddKeyAsync(key).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            type.Members = members.AsReadOnly();
            var properties = members.Where(p => Named(p.Key)).Select(p => p.Value).ToArray();
            Array.Sort(properties, order.CompareSymbols);
            type.Properties = Array.AsReadOnly(properties);
            type.IndexInfos = Array.AsReadOnly(indexes.ToArray());

            async ValueTask AddKeyAsync(Type key)
            {
                cancellation.ThrowIfCancellationRequested();
                context.RequireOwned(key);
                var names = nameType is null ? key : await RequiredAsync(nameType,
                    TypeMapper.Append(type.Mapper, parameter, key), cancellation).ConfigureAwait(false);
                foreach (var name in Parts(names))
                {
                    if (UsableName(name))
                    {
                        string propertyName = PropertyName(name);
                        if (members.TryGetValue(propertyName, out var existing))
                        {
                            var values = links.Values.Get(existing);
                            values.NameType = await algebra.UnionAsync(
                                [values.NameType!, name],
                                cancellation: cancellation).ConfigureAwait(false);
                            var mappedLinks = links.MappedSymbols.Get(existing);
                            mappedLinks.KeyType = await algebra.UnionAsync(
                                [mappedLinks.KeyType!, key],
                                cancellation: cancellation).ConfigureAwait(false);
                            continue;
                        }
                        var original = UsableName(key)
                            ? await host.PropertyAsync(modifiersType, PropertyName(key), cancellation).ConfigureAwait(false)
                            : null;
                        bool optional = (modifiers & M.IncludeOptional) != 0 || (modifiers & M.ExcludeOptional) == 0
                            && original is not null && (original.Flags & SymbolFlags.Optional) != 0;
                        bool readOnly = (modifiers & M.IncludeReadonly) != 0 || (modifiers & M.ExcludeReadonly) == 0
                            && original is not null && host.IsReadonly(original);
                        bool stripOptional = context.StrictNullChecks
                            && !optional
                            && original is not null
                            && (original.Flags & SymbolFlags.Optional) != 0;
                        var property = new Symbol(
                            SymbolFlags.Property | SymbolFlags.Transient | (optional ? SymbolFlags.Optional : 0),
                            propertyName)
                        {
                            CheckFlags = (original?.CheckFlags & CheckFlags.Late ?? 0) | CheckFlags.Mapped
                                | (readOnly ? CheckFlags.Readonly : 0) | (stripOptional ? CheckFlags.StripOptional : 0)
                        };
                        members.Add(propertyName, property);
                        var propertyLinks = links.Values.Get(property);
                        propertyLinks.ContainingType = type;
                        propertyLinks.NameType = name;
                        var mapping = links.MappedSymbols.Get(property);
                        mapping.KeyType = key;
                        if (original is not null)
                        {
                            mapping.SyntheticOrigin = original;
                            if (linkDeclarations)
                                property.DeclarationList.AddRange(original.Declarations);
                        }
                    }
                    else if (await ValidIndexKeyAsync(name, cancellation).ConfigureAwait(false) || (name.Flags & (F.Any | F.Enum)) != 0)
                    {
                        var indexKey = (name.Flags & (F.Any | F.String)) != 0 ? context.StringType
                            : (name.Flags & (F.Number | F.Enum)) != 0 ? context.NumberType : name;
                        var value = await RequiredAsync(
                            template,
                            TypeMapper.Append(type.Mapper, parameter, key),
                            cancellation).ConfigureAwait(false);
                        var original = await host.ApplicableIndexAsync(modifiersType, name, cancellation).ConfigureAwait(false);
                        bool readOnly = (modifiers & M.IncludeReadonly) != 0
                            || (modifiers & M.ExcludeReadonly) == 0 && original is { IsReadonly: true };
                        await AppendIndexAsync(
                            indexes,
                            context.NewIndexInfo(indexKey, value, readOnly),
                            true,
                            cancellation).ConfigureAwait(false);
                    }
                }
            }
        }
        catch
        {
            type.ObjectFlags &= ~O.MembersResolved;
            type.Members = oldMembers;
            type.Properties = oldProperties;
            type.CallSignatures = oldCalls;
            type.ConstructSignatures = oldConstructs;
            type.IndexInfos = oldIndexes;
            foreach (var property in members.Values)
            {
                links.Values.Remove(property);
                links.MappedSymbols.Remove(property);
            }
            throw;
        }
    }

    internal async ValueTask<Type> SymbolTypeAsync(Symbol symbol, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var values = links.Values.Get(symbol);
        if (values.ResolvedType is { } cached)
            return cached;
        var type = values.ContainingType as MappedType ?? throw new InvalidOperationException("Mapped property has no containing mapped type");
        context.RequireOwned(type);
        if (!resolutions.Push(symbol, TypeSystemPropertyName.Type))
        {
            type.ContainsError = true;
            return context.ErrorType;
        }
        bool active = true;
        Type? assigned = null;
        try
        {
            var template = await mapped.TemplateAsync((MappedType?)type.Target ?? type, cancellation).ConfigureAwait(false);
            var parameter = await mapped.ParameterAsync(type, cancellation).ConfigureAwait(false);
            var key = links.MappedSymbols.Get(symbol).KeyType ?? throw new InvalidOperationException("Mapped property has no key type");
            var result = await RequiredAsync(template, TypeMapper.Append(type.Mapper, parameter, key), cancellation).ConfigureAwait(false);
            if (context.StrictNullChecks && (symbol.Flags & SymbolFlags.Optional) != 0
                && !MappedTypes.MaybeKind(result, F.Undefined | F.Void, cancellation))
                result = await mapped.OptionalAsync(result, cancellation).ConfigureAwait(false);
            else if ((symbol.CheckFlags & CheckFlags.StripOptional) != 0)
                result = await mapped.RemoveOptionalAsync(result, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            bool resolved = resolutions.Pop();
            active = false;
            if (values.ResolvedType is null)
                values.ResolvedType = assigned = resolved ? result : context.ErrorType;
            if (!resolved)
                await host.CircularPropertyAsync(symbol, type, cancellation).ConfigureAwait(false);
            return values.ResolvedType;
        }
        catch
        {
            if (assigned is not null && values.ResolvedType == assigned)
                values.ResolvedType = null;
            throw;
        }
        finally
        {
            if (active)
                resolutions.Pop();
        }
    }

    internal async ValueTask<Type> ApparentKeysAsync(Type nameType, MappedType target, CancellationToken cancellation = default)
    {
        context.RequireOwned(nameType);
        var modifiers = await host.ApparentTypeAsync(
            await ModifiersTypeAsync(target, cancellation).ConfigureAwait(false),
            cancellation).ConfigureAwait(false);
        var keys = new List<Type>();
        var parameter = await mapped.ParameterAsync(target, cancellation).ConfigureAwait(false);
        await ForEachKeyAsync(modifiers, async key => keys.Add(await RequiredAsync(nameType,
            TypeMapper.Append(target.Mapper, parameter, key), cancellation).ConfigureAwait(false)), cancellation).ConfigureAwait(false);
        return await algebra.UnionAsync(keys, cancellation: cancellation).ConfigureAwait(false);
    }

    private async ValueTask ForEachKeyAsync(Type type, Func<Type, ValueTask> action, CancellationToken cancellation)
    {
        context.RequireOwned(type);
        foreach (var property in await host.PropertiesAsync(type, cancellation).ConfigureAwait(false))
            await action(await host.PropertyNameTypeAsync(property, cancellation).ConfigureAwait(false)).ConfigureAwait(false);
        if ((type.Flags & F.Any) != 0)
            await action(context.StringType).ConfigureAwait(false);
        else
            foreach (var index in await host.IndexInfosAsync(type, cancellation).ConfigureAwait(false))
                await action(index.KeyType).ConfigureAwait(false);
    }

    internal async ValueTask AppendIndexAsync(List<IndexInfo> indexes, IndexInfo next, bool union, CancellationToken cancellation = default)
    {
        context.RequireOwned(next.KeyType);
        context.RequireOwned(next.ValueType);
        for (int i = 0; i < indexes.Count; i++)
        {
            var current = indexes[i];
            if (current.KeyType != next.KeyType)
                continue;
            var value = union ? await algebra.UnionAsync(
                [current.ValueType, next.ValueType],
                cancellation: cancellation).ConfigureAwait(false)
                : await algebra.IntersectionAsync([current.ValueType, next.ValueType], cancellation: cancellation).ConfigureAwait(false);
            indexes[i] = context.NewIndexInfo(current.KeyType, value,
                union ? current.IsReadonly || next.IsReadonly : current.IsReadonly && next.IsReadonly);
            return;
        }
        indexes.Add(next);
    }

    internal async ValueTask<bool> ValidIndexKeyAsync(Type type, CancellationToken cancellation = default)
    {
        context.RequireOwned(type);
        var pending = new Stack<Type>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if ((current.Flags & (F.String | F.Number | F.ESSymbol)) != 0 || TypeAlgebra.IsPatternLiteral(current))
                return true;
            if (current is IntersectionType intersection
                && await mapped.GenericFlagsAsync(current, cancellation).ConfigureAwait(false) == 0)
                foreach (var part in intersection.Types)
                    pending.Push(part);
        }
        return false;
    }

    private async ValueTask<Type> RequiredAsync(Type type, TypeMapper? mapper, CancellationToken cancellation)
        => await instantiation.InstantiateAsync(type, mapper, cancellation: cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Instantiation of a present type returned no type");

    private static IReadOnlyList<Type> Parts(Type type) => type is UnionType union ? union.Types : [type];

    private static bool UsableName(Type type) => (type.Flags & F.StringOrNumberLiteralOrUnique) != 0;

    private static bool Named(string name) => !name.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal)
            || name.StartsWith(Symbol.InternalPrefix + Symbol.InternalPrefix, StringComparison.Ordinal)
            || name.Length < 2 || name[1] is '@' or '#';

    internal static string PropertyName(Type type) => type switch
    {
        LiteralType { Value: string name } => name.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal)
            ? Symbol.InternalPrefix + name
            : name,
        LiteralType { Value: double number } => TokenFacts.NumberText(number),
        UniqueSymbolType unique => unique.Name,
        _ => throw new ArgumentException("Type is not usable as a property name", nameof(type))
    };
}

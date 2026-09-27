using System.Runtime.CompilerServices;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed class ObjectSpreads(TypeContext context, CheckerLinks links, TypeAlgebra algebra, TypeFactQueries facts,
    TypeViews views, TypeProperties properties, SymbolTypes values, MappedTypes mapped, BindingTypes bindings,
    StructuredMembers structuredMembers, IBindingTypeHost host)
{
    internal async ValueTask<Type> GetAsync(Type left, Type right, Symbol? symbol, ObjectFlags flags, bool readOnly,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(left);
        context.RequireOwned(right);
        if (((left.Flags | right.Flags) & TypeFlags.Any) != 0)
            return context.AnyType;
        if (((left.Flags | right.Flags) & TypeFlags.Unknown) != 0)
            return context.UnknownType;
        if ((left.Flags & TypeFlags.Never) != 0)
            return right;
        if ((right.Flags & TypeFlags.Never) != 0)
            return left;
        left = await MergeEmptyAsync(left, readOnly, cancellation).ConfigureAwait(false);
        if (left is UnionType)
            return algebra.CheckCrossProduct([left, right])
                ? (await algebra.MapAsync(
                    left,
                    async part => await GetAsync(part, right, symbol, flags, readOnly, cancellation).ConfigureAwait(false),
                    cancellation: cancellation).ConfigureAwait(false))! : context.ErrorType;
        right = await MergeEmptyAsync(right, readOnly, cancellation).ConfigureAwait(false);
        if (right is UnionType)
            return algebra.CheckCrossProduct([left, right])
                ? (await algebra.MapAsync(
                    right,
                    async part => await GetAsync(left, part, symbol, flags, readOnly, cancellation).ConfigureAwait(false),
                    cancellation: cancellation).ConfigureAwait(false))! : context.ErrorType;
        if ((right.Flags & (TypeFlags.BooleanLike | TypeFlags.NumberLike | TypeFlags.BigIntLike | TypeFlags.StringLike
                | TypeFlags.EnumLike | TypeFlags.NonPrimitive | TypeFlags.Index)) != 0)
            return left;
        if (await GenericAsync(left, cancellation).ConfigureAwait(false) || await GenericAsync(right, cancellation).ConfigureAwait(false))
        {
            if (await views.EmptyObjectAsync(left, cancellation).ConfigureAwait(false))
                return right;
            if (left is IntersectionType intersection)
            {
                var last = intersection.Types[^1];
                if (await NonGenericObjectAsync(last, cancellation).ConfigureAwait(false)
                    && await NonGenericObjectAsync(right, cancellation).ConfigureAwait(false))
                {
                    var types = intersection.Types.ToArray();
                    types[^1] = await GetAsync(last, right, symbol, flags, readOnly, cancellation).ConfigureAwait(false);
                    return await algebra.IntersectionAsync(types, cancellation: cancellation).ConfigureAwait(false);
                }
            }
            return await algebra.IntersectionAsync([left, right], cancellation: cancellation).ConfigureAwait(false);
        }
        var members = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        var indexInfos = left == context.EmptyObjectType ? await host.IndexesAsync(right, cancellation).ConfigureAwait(false)
            : await UnionIndexesAsync(left, right, cancellation).ConfigureAwait(false);
        foreach (var property in await properties.GetAsync(right, cancellation).ConfigureAwait(false))
            if (NonPublic(property))
                skipped.Add(property.Name);
            else if (BindingTypes.Spreadable(property))
                members[property.Name] = await bindings.SpreadSymbolAsync(property, readOnly, cancellation).ConfigureAwait(false);
        foreach (var property in await properties.GetAsync(left, cancellation).ConfigureAwait(false))
        {
            if (skipped.Contains(property.Name) || !BindingTypes.Spreadable(property))
                continue;
            if (members.TryGetValue(property.Name, out var previous))
            {
                var rightType = await values.GetAsync(previous, cancellation).ConfigureAwait(false);
                if ((previous.Flags & SymbolFlags.Optional) != 0)
                {
                    var result = new Symbol(
                        SymbolFlags.Property | SymbolFlags.Transient | (property.Flags & SymbolFlags.Optional),
                        property.Name);
                    var leftType = await values.GetAsync(property, cancellation).ConfigureAwait(false);
                    var leftRequired = await RemoveMissingAsync(leftType, cancellation).ConfigureAwait(false);
                    var rightRequired = await RemoveMissingAsync(rightType, cancellation).ConfigureAwait(false);
                    links.Values.Get(result).ResolvedType = leftRequired == rightRequired ? leftType
                        : await algebra.UnionAsync(
                            [leftType, rightRequired],
                            UnionReduction.Subtype,
                            cancellation: cancellation).ConfigureAwait(false);
                    links.Spreads.Get(result).Left = property;
                    links.Spreads.Get(result).Right = previous;
                    result.DeclarationList.AddRange(property.Declarations);
                    result.DeclarationList.AddRange(previous.Declarations);
                    links.Values.Get(result).NameType = links.Values.Get(property).NameType;
                    members[property.Name] = result;
                }
            }
            else
                members[property.Name] = await bindings.SpreadSymbolAsync(property, readOnly, cancellation).ConfigureAwait(false);
        }
        var indexes = indexInfos.Select(info => info.IsReadonly == readOnly ? info
            : context.NewIndexInfo(info.KeyType, info.ValueType, readOnly, info.Declaration, info.Components.ToArray())).ToArray();
        return await ObjectAsync(
            symbol,
            members,
            indexes,
            flags | ObjectFlags.ObjectLiteral | ObjectFlags.ContainsObjectOrArrayLiteral | ObjectFlags.ContainsSpread,
            cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> MergeEmptyAsync(Type type, bool readOnly, CancellationToken cancellation = default)
    {
        if (type is not UnionType union)
            return type;
        var nonEmpty = new List<Type>();
        foreach (var part in union.Types)
            if (!await EmptySpreadAsync(part, cancellation).ConfigureAwait(false))
                nonEmpty.Add(part);
        if (nonEmpty.Count == 0)
        {
            foreach (var part in union.Types)
                if (await views.EmptyObjectAsync(part, cancellation).ConfigureAwait(false))
                    return part;
            return context.EmptyObjectType;
        }
        var first = nonEmpty[0];
        if (nonEmpty.Any(t => t != first))
            return type;
        var members = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        foreach (var property in await properties.GetAsync(first, cancellation).ConfigureAwait(false))
        {
            if (NonPublic(property) || !BindingTypes.Spreadable(property))
                continue;
            var symbol = new Symbol(SymbolFlags.Property | SymbolFlags.Optional | SymbolFlags.Transient, property.Name)
            { CheckFlags = (property.CheckFlags & CheckFlags.Late) | (readOnly ? CheckFlags.Readonly : 0) };
            var value = (property.Flags & SymbolFlags.SetAccessor) != 0 && (property.Flags & SymbolFlags.GetAccessor) == 0
                ? context.UndefinedType : await values.GetAsync(property, cancellation).ConfigureAwait(false);
            if (!((property.Flags & SymbolFlags.SetAccessor) != 0 && (property.Flags & SymbolFlags.GetAccessor) == 0)
                && context.StrictNullChecks)
                value = await algebra.UnionAsync([value, context.UndefinedOrMissingType], cancellation: cancellation).ConfigureAwait(false);
            links.Values.Get(symbol).ResolvedType = value;
            symbol.DeclarationList.AddRange(property.Declarations);
            links.Values.Get(symbol).NameType = links.Values.Get(property).NameType;
            links.MappedSymbols.Get(symbol).SyntheticOrigin = property;
            members[symbol.Name] = symbol;
        }
        return await ObjectAsync(first.Symbol, members, await host.IndexesAsync(first, cancellation).ConfigureAwait(false),
            ObjectFlags.ObjectLiteral | ObjectFlags.ContainsObjectOrArrayLiteral, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<ObjectType> ObjectAsync(Symbol? symbol, Dictionary<string, Symbol> members,
        IReadOnlyList<IndexInfo> indexes, ObjectFlags flags = 0, CancellationToken cancellation = default)
    {
        var result = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved | flags, symbol);
        result.Members = members.AsReadOnly();
        var named = new List<Symbol>();
        foreach (var (name, member) in members)
            if (await structuredMembers.NamedAsync(name, member, cancellation).ConfigureAwait(false))
                named.Add(member);
        result.Properties = named.Order(algebra.Order).ToArray();
        result.CallSignatures = result.ConstructSignatures = [];
        result.IndexInfos = indexes;
        return result;
    }

    private async ValueTask<IReadOnlyList<IndexInfo>> UnionIndexesAsync(Type left, Type right, CancellationToken cancellation)
    {
        var result = new List<IndexInfo>();
        var leftIndexes = await host.IndexesAsync(left, cancellation).ConfigureAwait(false);
        foreach (var info in leftIndexes)
        {
            var other = (await host.IndexesAsync(right, cancellation).ConfigureAwait(false)).FirstOrDefault(i => i.KeyType == info.KeyType);
            if (other is not null)
                result.Add(
                    context.NewIndexInfo(
                    info.KeyType,
                    await algebra.UnionAsync([info.ValueType, other.ValueType], cancellation: cancellation).ConfigureAwait(false),
                    info.IsReadonly || other.IsReadonly));
        }
        return result;
    }

    private bool NonPublic(Symbol symbol) =>
        (host.AccessFlags(symbol, false) & (CheckFlags.ContainsPrivate | CheckFlags.ContainsProtected)) != 0;

    private async ValueTask<bool> GenericAsync(Type type, CancellationToken cancellation) =>
            (await mapped.GenericFlagsAsync(type, cancellation).ConfigureAwait(false) & ObjectFlags.IsGenericObjectType) != 0;

    private async ValueTask<bool> NonGenericObjectAsync(Type type, CancellationToken cancellation) => (type.Flags & TypeFlags.Object) != 0
            && !(type is MappedType mapping && await mapped.IsGenericAsync(mapping, cancellation).ConfigureAwait(false));

    private async ValueTask<bool> EmptySpreadAsync(Type type, CancellationToken cancellation) => await views.EmptyObjectAsync(
        type,
        cancellation).ConfigureAwait(false)
            || (type.Flags & (TypeFlags.Nullable | TypeFlags.BooleanLike | TypeFlags.NumberLike | TypeFlags.BigIntLike | TypeFlags.StringLike
                | TypeFlags.EnumLike | TypeFlags.NonPrimitive | TypeFlags.Index)) != 0;

    private ValueTask<Type> RemoveMissingAsync(Type type, CancellationToken cancellation) => context.ExactOptionalPropertyTypes
            ? ValueTask.FromResult(
                algebra.Filter(type, part => part != context.MissingType)) : facts.FilterAsync(type, TypeFacts.NEUndefined, cancellation);
}

using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IIndexedTypeHost
{
    bool NoUncheckedIndexedAccess { get; }
    bool NoImplicitAny { get; }

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    ValueTask<Type?> ContextualPropertyAsync(Type type, TextSlice name, CancellationToken cancellation);

    ValueTask<Type?> ElementPropertyAsync(Symbol property, Type objectType, ElementAccessExpressionNode node,
        AccessFlags flags, CancellationToken cancellation);

    ValueTask ReadonlyIndexAsync(IndexInfo? index, Type objectType, ElementAccessExpressionNode? node, CancellationToken cancellation);

    ValueTask<Type?> MissingElementAsync(Type original, Type objectType, Type index, Type fullIndex,
        ElementAccessExpressionNode node, TextSlice? propertyName, AccessFlags flags, CancellationToken cancellation);

    ValueTask DeprecatedPropertyAsync(Symbol property, SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> SimplifyAsync(Type type, bool writing, CancellationToken cancellation);

    ValueTask InvalidIndexAsync(
        SyntaxNode node,
        Type objectType,
        Type indexType,
        DiagnosticCode code,
        CancellationToken cancellation,
        Type? fullIndex = null,
        TextSlice? suggestion = null);
}

// Type-level indexed access. Expression access additionally needs reference
// tracking, assignment checks and control-flow analysis supplied by the host.
internal sealed class IndexedTypes(TypeContext context, TypeAlgebra algebra, TypeViews views, TypeKeys keys,
    TypeProperties properties, SymbolTypes symbols, IndexSignatures indexes, TypeRelations relations,
    TypeInstantiation instantiation, TypeConstraints constraints, TupleTypes tuples, MappedTypes mapped,
    MappedMembers mappedMembers, IIndexedTypeHost host)
{
    private readonly Dictionary<(IndexedAccessType, bool), Type> simplified = [];

    internal async ValueTask<Type> GetAsync(Type objectType, Type indexType, AccessFlags flags = 0,
        SyntaxNode? node = null, TypeAlias? alias = null, CancellationToken cancellation = default)
        => await TryGetAsync(objectType, indexType, flags, node, alias, cancellation).ConfigureAwait(false)
            ?? (node is null ? context.UnknownType : context.ErrorType);

    internal async ValueTask<Type?> TryGetAsync(Type objectType, Type indexType, AccessFlags flags = 0,
        SyntaxNode? node = null, TypeAlias? alias = null, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(objectType);
        context.RequireOwned(indexType);
        if (objectType == context.WildcardType || indexType == context.WildcardType)
            return context.WildcardType;
        objectType = await views.ReducedAsync(objectType, cancellation).ConfigureAwait(false);
        if (await StringIndexOnlyAsync(objectType, cancellation).ConfigureAwait(false)
            && (indexType.Flags & TypeFlags.Nullable) == 0
            && await KeyKindAsync(indexType, false, cancellation).ConfigureAwait(false))
            indexType = context.StringType;
        if (host.NoUncheckedIndexedAccess && (flags & AccessFlags.ExpressionPosition) != 0)
            flags |= AccessFlags.IncludeUndefined;
        if (await ShouldDeferAsync(objectType, indexType, node, cancellation).ConfigureAwait(false))
            return (objectType.Flags & TypeFlags.AnyOrUnknown) != 0
                ? objectType
                : context.GetGenericIndexedAccess(objectType, indexType, flags, alias);
        var apparent = await views.ReducedApparentAsync(objectType, cancellation).ConfigureAwait(false);
        if (indexType is UnionType union && (indexType.Flags & TypeFlags.Boolean) == 0)
        {
            var values = new List<Type>();
            bool missing = false;
            foreach (var part in union.Types)
            {
                var value = await PropertyAsync(objectType, apparent, part, indexType, node,
                    flags | (missing ? AccessFlags.SuppressNoImplicitAnyError : 0), cancellation).ConfigureAwait(false);
                if (value is not null)
                    values.Add(value);
                else if (node is null)
                    return null;
                else
                    missing = true;
            }
            if (missing)
                return null;
            return (flags & AccessFlags.Writing) != 0 ? await algebra.IntersectionAsync(
                values,
                alias: alias,
                cancellation: cancellation).ConfigureAwait(false)
                : await algebra.UnionAsync(values, alias: alias, cancellation: cancellation).ConfigureAwait(false);
        }
        return await PropertyAsync(objectType, apparent, indexType, indexType, node,
            flags | AccessFlags.CacheSymbol | AccessFlags.ReportDeprecated, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<bool> ShouldDeferAsync(Type objectType, Type indexType, SyntaxNode? node = null,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(objectType);
        context.RequireOwned(indexType);
        if ((await mapped.GenericFlagsAsync(indexType, cancellation).ConfigureAwait(false) & ObjectFlags.IsGenericIndexType) != 0)
            return true;
        if (node is not null and not IndexedAccessTypeNode)
            return TypeConstraints.IsGenericTuple(objectType)
                && !IndexLessThan(indexType, TotalFixed((TupleType)((TypeReference)objectType).Target!));
        return (await mapped.GenericFlagsAsync(objectType, cancellation).ConfigureAwait(false) & ObjectFlags.IsGenericObjectType) != 0
                && !(objectType is TypeReference { Target: TupleType tuple } && IndexLessThan(indexType, TotalFixed(tuple)))
            || await keys.GenericReducibleAsync(objectType, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<bool> StringIndexOnlyAsync(Type type, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (type is UnionOrIntersectionType composite)
        {
            foreach (var part in composite.Types)
                if (!await StringIndexOnlyAsync(part, cancellation).ConfigureAwait(false))
                    return false;
            return true;
        }
        return (type.Flags & TypeFlags.Object) != 0
            && !(type is MappedType mapping && await mapped.IsGenericAsync(mapping, cancellation).ConfigureAwait(false))
            && (await properties.GetAsync(type, cancellation).ConfigureAwait(false)).Count == 0
            && await host.IndexesAsync(type, cancellation).ConfigureAwait(false) is [var index] && index.KeyType == context.StringType;
    }

    private async ValueTask<Type?> PropertyAsync(Type original, Type objectType, Type indexType, Type fullIndexType,
        SyntaxNode? node, AccessFlags flags, CancellationToken cancellation)
    {
        var element = node as ElementAccessExpressionNode;
        TextSlice? name = node is PrivateIdentifierNode ? null : PropertyName(indexType, node);
        if (name is not null)
        {
            if ((flags & AccessFlags.Contextual) != 0)
                return await host.ContextualPropertyAsync(objectType, (name).Value, cancellation).ConfigureAwait(false) ?? context.AnyType;
            var property = await properties.PropertyAsync(objectType, (name).Value, cancellation: cancellation).ConfigureAwait(false);
            if (property is not null)
            {
                if ((flags & AccessFlags.ReportDeprecated) != 0 && node is not null && property.Declarations.Count != 0)
                    await host.DeprecatedPropertyAsync(property, node, cancellation).ConfigureAwait(false);
                if (element is not null)
                    return await host.ElementPropertyAsync(property, objectType, element, flags, cancellation).ConfigureAwait(false);
                var value = (flags & AccessFlags.Writing) != 0 ? await symbols.WriteAsync(property, cancellation).ConfigureAwait(false)
                    : await symbols.GetAsync(property, cancellation).ConfigureAwait(false);
                return node is IndexedAccessTypeNode && ContainsMissing(value)
                    ? await algebra.UnionAsync([value, context.UndefinedType], cancellation: cancellation).ConfigureAwait(false) : value;
            }
            if (Parts(objectType).All(t => t is TypeReference { Target: TupleType }) && IndexSignatures.NumericName((name).Value))
            {
                double position = JsNumber.FromString((name).Value);
                if (node is not null && (flags & AccessFlags.AllowMissing) == 0
                    && Parts(objectType).All(t => (((TupleType)((TypeReference)t).Target!).CombinedFlags & ElementFlags.Variable) == 0))
                {
                    if (objectType is TypeReference { Target: TupleType })
                    {
                        if (position < 0)
                        {
                            await host.InvalidIndexAsync(
                                IndexNode(node),
                                objectType,
                                indexType,
                                DiagnosticCode.ATupleTypeCannotBeIndexedWithANegativeValue,
                                cancellation).ConfigureAwait(false);
                            return context.UndefinedType;
                        }
                        await host.InvalidIndexAsync(
                            IndexNode(node),
                            objectType,
                            indexType,
                            DiagnosticCode.TupleType0OfLength1HasNoElementAtIndex2,
                            cancellation).ConfigureAwait(false);
                    }
                    else
                        await host.InvalidIndexAsync(
                            IndexNode(node),
                            objectType,
                            indexType,
                            DiagnosticCode.Property0DoesNotExistOnType1,
                            cancellation).ConfigureAwait(false);
                }
                if (position >= 0)
                {
                    await host.ReadonlyIndexAsync(
                        (await host.IndexesAsync(
                            objectType,
                            cancellation).ConfigureAwait(false)).FirstOrDefault(i => i.KeyType == context.NumberType),
                        objectType,
                        element, cancellation).ConfigureAwait(false);
                    return await TupleRestAsync(
                        objectType,
                        position,
                        (flags & AccessFlags.IncludeUndefined) != 0,
                        cancellation).ConfigureAwait(false);
                }
            }
        }
        if ((indexType.Flags & TypeFlags.Nullable) == 0 && await KeyKindAsync(indexType, true, cancellation).ConfigureAwait(false))
        {
            if ((objectType.Flags & (TypeFlags.Any | TypeFlags.Never)) != 0)
                return objectType;
            var infos = await host.IndexesAsync(objectType, cancellation).ConfigureAwait(false);
            var index = await indexes.ApplicableAsync(infos, indexType, cancellation).ConfigureAwait(false)
                ?? infos.FirstOrDefault(i => i.KeyType == context.StringType);
            if (index is not null)
            {
                if ((flags & AccessFlags.NoIndexSignatures) != 0 && index.KeyType != context.NumberType)
                {
                    if (element is not null)
                        await host.InvalidIndexAsync(
                            element,
                            original,
                            indexType,
                            (flags & AccessFlags.Writing) != 0
                                ? DiagnosticCode.Type0IsGenericAndCanOnlyBeIndexedForReading
                                : DiagnosticCode.Type0CannotBeUsedToIndexType1,
                            cancellation).ConfigureAwait(false);
                    return null;
                }
                if (node is not null
                    && index.KeyType == context.StringType
                    && !await KeyKindAsync(indexType, false, cancellation).ConfigureAwait(false))
                {
                    await host.InvalidIndexAsync(
                        IndexNode(node),
                        objectType,
                        indexType,
                        DiagnosticCode.Type0CannotBeUsedAsAnIndexType,
                        cancellation).ConfigureAwait(false);
                    return await IncludeMissingAsync(index.ValueType, flags, cancellation).ConfigureAwait(false);
                }
                await host.ReadonlyIndexAsync(index, objectType, element, cancellation).ConfigureAwait(false);
                bool enumMember = objectType.Symbol is { } symbol && (symbol.Flags & SymbolFlags.Enum) != 0
                    && (indexType.Flags & TypeFlags.EnumLiteral) != 0 && indexType.Symbol?.Parent == symbol;
                return await IncludeMissingAsync(index.ValueType, enumMember ? 0 : flags, cancellation).ConfigureAwait(false);
            }
            if ((indexType.Flags & TypeFlags.Never) != 0)
                return context.NeverType;
            if (await JsLiteralAsync(objectType, cancellation).ConfigureAwait(false))
                return context.AnyType;
            if (element is not null && !AccessExpressions.ConstEnum(objectType))
                return await host.MissingElementAsync(
                    original,
                    objectType,
                    indexType,
                    fullIndexType,
                    element,
                    name,
                    flags,
                    cancellation).ConfigureAwait(false);
        }
        if ((flags & AccessFlags.AllowMissing) != 0 && (objectType.ObjectFlags & ObjectFlags.ObjectLiteral) != 0)
            return context.UndefinedType;
        if (await JsLiteralAsync(objectType, cancellation).ConfigureAwait(false))
            return context.AnyType;
        if (node is not null)
        {
            var indexNode = IndexNode(node);
            DiagnosticCode code = indexNode is not BigIntLiteralNode
                && (indexType.Flags & TypeFlags.StringOrNumberLiteral) != 0 ? DiagnosticCode.Property0DoesNotExistOnType1
                : (indexType.Flags & (TypeFlags.String | TypeFlags.Number)) != 0
                    ? DiagnosticCode.Type0HasNoMatchingIndexSignatureForType1
                    : DiagnosticCode.Type0CannotBeUsedAsAnIndexType;
            await host.InvalidIndexAsync(indexNode, objectType, indexType, code, cancellation).ConfigureAwait(false);
        }
        return (indexType.Flags & TypeFlags.Any) != 0 ? indexType : null;
    }

    private ValueTask<Type> IncludeMissingAsync(Type type, AccessFlags flags, CancellationToken cancellation)
        =>
            (flags & AccessFlags.IncludeUndefined) != 0
                ? algebra.UnionAsync([type, context.MissingType], cancellation: cancellation)
                : ValueTask.FromResult(type);

    private async ValueTask<Type?> TupleRestAsync(Type type, double position, bool includeMissing, CancellationToken cancellation)
        => await algebra.MapAsync(type, async part =>
        {
            var tuple = (TupleType)((TypeReference)part).Target!;
            var rest = await tuples.SliceElementAsync(
                (TypeReference)part,
                tuple.FixedLength,
                cancellation: cancellation).ConfigureAwait(false);
            return rest is null ? context.UndefinedType : includeMissing && position >= TotalFixed(tuple)
                ? await algebra.UnionAsync([rest, context.MissingType], cancellation: cancellation).ConfigureAwait(false) : rest;
        }, cancellation: cancellation).ConfigureAwait(false);

    private async ValueTask<bool> KeyKindAsync(Type type, bool includeSymbol, CancellationToken cancellation)
    {
        var mask = includeSymbol
            ? TypeFlags.StringLike | TypeFlags.NumberLike | TypeFlags.ESSymbolLike
            : TypeFlags.String | TypeFlags.Number;
        if ((type.Flags & mask) != 0)
            return true;
        return await relations.RelatedAsync(type, context.NumberType, RelationKind.Assignable, cancellation).ConfigureAwait(false)
            || await relations.RelatedAsync(type, context.StringType, RelationKind.Assignable, cancellation).ConfigureAwait(false)
            || includeSymbol
                && await relations.RelatedAsync(type, context.ESSymbolType, RelationKind.Assignable, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> SimplifyAsync(IndexedAccessType type, bool writing, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        var key = (type, writing);
        if (simplified.TryGetValue(key, out var cached))
            return cached == context.CircularConstraintType ? type : cached;
        simplified[key] = type;
        try
        {
            var result = await SimplifyWorkerAsync(type, writing, cancellation).ConfigureAwait(false);
            if (result != type)
                result = algebra.Filter(result, t => t != type);
            cancellation.ThrowIfCancellationRequested();
            simplified[key] = result;
            return result;
        }
        catch
        {
            if (simplified.TryGetValue(key, out var current) && current == type)
                simplified.Remove(key);
            throw;
        }
    }

    private async ValueTask<Type> SimplifyWorkerAsync(IndexedAccessType type, bool writing, CancellationToken cancellation)
    {
        var objectType = await host.SimplifyAsync(type.ObjectType, writing, cancellation).ConfigureAwait(false);
        var indexType = await host.SimplifyAsync(type.IndexType, writing, cancellation).ConfigureAwait(false);
        if (indexType is UnionType union)
        {
            var values = new List<Type>();
            foreach (var index in union.Types)
                values.Add(
                    await host.SimplifyAsync(
                        await GetAsync(objectType, index, cancellation: cancellation).ConfigureAwait(false),
                        writing,
                        cancellation).ConfigureAwait(false));
            return writing ? await algebra.IntersectionAsync(values, cancellation: cancellation).ConfigureAwait(false)
                : await algebra.UnionAsync(values, cancellation: cancellation).ConfigureAwait(false);
        }
        if ((indexType.Flags & TypeFlags.Instantiable) == 0
            && await DistributeIndexAsync(objectType, indexType, writing, cancellation).ConfigureAwait(false) is { } distributed)
            return distributed;
        if (TypeConstraints.IsGenericTuple(objectType) && (indexType.Flags & TypeFlags.NumberLike) != 0)
        {
            int start = (indexType.Flags & TypeFlags.Number) != 0 ? 0 : ((TupleType)((TypeReference)objectType).Target!).FixedLength;
            var element = await tuples.SliceElementAsync(
                (TypeReference)objectType,
                start,
                writing: writing,
                cancellation: cancellation).ConfigureAwait(false);
            if (element is not null)
                return element;
        }
        if (objectType is MappedType mapping && await mapped.IsGenericAsync(mapping, cancellation).ConfigureAwait(false)
            && await mappedMembers.NameKindAsync(mapping, cancellation).ConfigureAwait(false) != MappedTypeNameTypeKind.Remapping)
            return await algebra.MapAsync(await SubstituteMappedAsync(mapping, type.IndexType, cancellation).ConfigureAwait(false),
                async part => await host.SimplifyAsync(part, writing, cancellation).ConfigureAwait(false),
                cancellation: cancellation).ConfigureAwait(false)
                ?? context.NeverType;
        return type;
    }

    internal async ValueTask<Type?> DistributeIndexAsync(
        Type objectType,
        Type indexType,
        bool writing,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(objectType);
        context.RequireOwned(indexType);
        if (objectType is not UnionOrIntersectionType composite
            || objectType is IntersectionType && await keys.ShouldDeferAsync(objectType, cancellation: cancellation).ConfigureAwait(false))
            return null;
        var values = new List<Type>();
        foreach (var part in composite.Types)
            values.Add(await host.SimplifyAsync(await GetAsync(part, indexType, cancellation: cancellation).ConfigureAwait(false),
                writing, cancellation).ConfigureAwait(false));
        return writing
            || objectType is IntersectionType ? await algebra.IntersectionAsync(values, cancellation: cancellation).ConfigureAwait(false)
            : await algebra.UnionAsync(values, cancellation: cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> SubstituteMappedAsync(MappedType type, Type index, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        context.RequireOwned(index);
        var parameter = await mapped.ParameterAsync(type, cancellation).ConfigureAwait(false);
        var mapper = TypeMapper.CombineAsync(type.Mapper, TypeMapper.Create([parameter], [index]),
            async (t, m, token) => await instantiation.InstantiateAsync(t, m, cancellation: token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Mapped template instantiation returned no type"));
        var template = await mapped.TemplateAsync((MappedType?)type.Target ?? type, cancellation).ConfigureAwait(false);
        var result = await instantiation.InstantiateAsync(template, mapper, cancellation: cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Mapped template instantiation returned no type");
        bool optional = MappedMembers.Optionality(type) > 0;
        if (!optional)
        {
            if (await mapped.GenericFlagsAsync(type, cancellation).ConfigureAwait(false) != 0)
                optional = await mappedMembers.CombinedOptionalityAsync(
                    await mappedMembers.ModifiersTypeAsync(type, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false) > 0;
            else if (await constraints.BaseConstraintAsync(index, cancellation).ConfigureAwait(false) is { } constraint)
                foreach (var property in await properties.GetAsync(type, cancellation).ConfigureAwait(false))
                    if ((property.Flags & SymbolFlags.Optional) != 0
                        && await relations.RelatedAsync(
                            await keys.PropertyAsync(
                                property,
                                TypeFlags.StringOrNumberLiteralOrUnique,
                                false,
                                cancellation).ConfigureAwait(false),
                            constraint,
                            RelationKind.Assignable,
                            cancellation).ConfigureAwait(false))
                    {
                        optional = true;
                        break;
                    }
        }
        return context.StrictNullChecks && optional
            ? await algebra.UnionAsync([result, context.UndefinedOrMissingType], cancellation: cancellation).ConfigureAwait(false) : result;
    }

    internal async ValueTask<bool> IsMappedGenericAccessAsync(IndexedAccessType type, CancellationToken cancellation = default)
        => type.ObjectType is MappedType mapping && !await mapped.IsGenericAsync(mapping, cancellation).ConfigureAwait(false)
            && (await mapped.GenericFlagsAsync(type.IndexType, cancellation).ConfigureAwait(false) & ObjectFlags.IsGenericIndexType) != 0
            && (MappedTypes.Modifiers(mapping) & MappedTypeModifiers.ExcludeOptional) == 0 && mapping.Declaration!.NameType is null;

    private bool ContainsMissing(Type type) =>
        type == context.MissingType || type is UnionType union && union.Types.Contains(context.MissingType);

    private async ValueTask<bool> JsLiteralAsync(Type type, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (host.NoImplicitAny)
            return false;
        if ((type.ObjectFlags & ObjectFlags.JSLiteral) != 0)
            return true;
        if (type is UnionOrIntersectionType composite)
        {
            bool union = type is UnionType;
            foreach (var part in composite.Types)
                if (await JsLiteralAsync(part, cancellation).ConfigureAwait(false) != union)
                    return !union;
            return union;
        }
        if ((type.Flags & TypeFlags.Instantiable) != 0)
        {
            var constraint = await constraints.ResolvedBaseConstraintAsync(type, cancellation).ConfigureAwait(false);
            return constraint != type && await JsLiteralAsync(constraint, cancellation).ConfigureAwait(false);
        }
        return false;
    }

    private static IReadOnlyList<Type> Parts(Type type) => type is UnionType union ? union.Types : [type];

    internal static int TotalFixed(TupleType tuple) =>
        tuple.FixedLength + tuple.ElementInfos.Reverse().TakeWhile(e => (e.Flags & ElementFlags.Fixed) != 0).Count();

    private static bool IndexLessThan(Type type, int limit) => Parts(type).All(t =>
            (t.Flags & TypeFlags.StringOrNumberLiteral) != 0 && IndexSignatures.NumericName(MappedMembers.PropertyName(t))
            && JsNumber.FromString(MappedMembers.PropertyName(t)) is var number && number >= 0 && number < limit);

    private static TextSlice? PropertyName(Type type, SyntaxNode? node) => (type.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0
            ? MappedMembers.PropertyName(type) : node switch
            {
                IdentifierNode identifier => identifier.Text,
                StringLiteralNode text => text.Text,
                NumericLiteralNode numeric => numeric.Text,
                _ => (TextSlice?)null
            };

    private static SyntaxNode IndexNode(SyntaxNode node) => node switch
    {
        IndexedAccessTypeNode indexed => indexed.IndexType!,
        ElementAccessExpressionNode element => element.ArgumentExpression!,
        ComputedPropertyNameNode computed => computed.Expression!,
        SyntheticExpressionNode synthetic => synthetic.Parent!,
        _ => node
    };
}

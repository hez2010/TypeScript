using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using F = TypeScript.Compiler.Checking.TypeFlags;
using O = TypeScript.Compiler.Checking.ObjectFlags;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal interface IInstantiationFixtureSource
{
    ValueTask<Type> IndexAsync(Type type, CancellationToken cancellation);

    ValueTask<Type> ConditionalInstantiationAsync(ConditionalType type, TypeMapper mapper, TypeAlias? alias, CancellationToken cancellation);

    ValueTask<Type?> ReverseMappedInferenceAsync(Type source, MappedType target, IndexType constraint, CancellationToken cancellation);

    ValueTask<Type> ReducedTypeAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> AssignableAsync(Type source, Type target, CancellationToken cancellation);

    ValueTask<Type> IndexedAccessAsync(Type objectType, Type indexType, AccessFlags flags, TypeAlias? alias, CancellationToken cancellation);

    Type ArrayTarget(bool isReadonly);

    ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation);

    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Type>> OuterParametersAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Symbol?> ReferenceSymbolAsync(TypeReferenceNode node, CancellationToken cancellation);

    ValueTask<Symbol> ValueSymbolAsync(IdentifierNode node, CancellationToken cancellation);

    ValueTask<TypeAlias?> AliasAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<TypeParameter> ParameterAsync(TypeParameterDeclarationNode node, CancellationToken cancellation);

    ValueTask<MappedType> MappedNodeAsync(MappedTypeNode node, CancellationToken cancellation);

    ValueTask<Type> ApparentAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Symbol>> PropertiesAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    ValueTask<Symbol?> PropertyAsync(Type type, string name, CancellationToken cancellation);

    ValueTask<Type> PropertyNameTypeAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<IndexInfo?> ApplicableIndexAsync(Type type, Type key, CancellationToken cancellation);

    bool IsReadonly(Symbol symbol);

    ValueTask<Type> SymbolTypeAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<bool> UnknownLikeUnionAsync(Type type, CancellationToken cancellation);
}

// Fixed, resolved fixture dependencies. No AST/member/inference fallback is
// supplied: an unsupported query fails the differential test.
internal sealed class InstantiationFixtureHost : ITypeInstantiationHost, ITupleTypeHost, IObjectInstantiationHost, IMappedMemberHost
{
    private readonly TypeContext context;
    private readonly TypeAlgebra algebra;
    private readonly AlgebraFixtureHost relations;
    private readonly CheckerLinks links;
    private readonly Type array, readonlyArray;
    private readonly IInstantiationFixtureSource? source;
    internal TupleTypes Tuples { get; }
    internal TypeInstantiation Engine { get; }
    internal ObjectInstantiation Objects { get; }
    internal MappedTypes Mapped { get; }
    internal MappedMembers Members { get; }
    internal TypeNodeFlow TypeNodeFlow { get; }
    internal Dictionary<MappedTypeNode, MappedType> MappedNodes { get; } = [];
    internal TypeConstraints Constraints { get; }
    internal TypeResolutionStack Resolutions { get; }
    internal ConstraintFixtureHost ConstraintDependencies { get; }
    internal Dictionary<TypeParameterDeclarationNode, TypeParameter> Parameters { get; } = [];
    internal Dictionary<SyntaxNode, IReadOnlyList<Type>> OuterParameters { get; } = [];
    internal Dictionary<TypeReferenceNode, Symbol?> ReferenceSymbols { get; } = [];
    internal Dictionary<IdentifierNode, Symbol> ValueSymbols { get; } = [];
    internal Dictionary<SyntaxNode, TypeAlias?> NodeAliases { get; } = [];
    internal List<DiagnosticCode> Diagnostics { get; } = [];
    internal Action<Type>? OnIndex { get; set; }
    internal Action<TypeReference>? OnTypeArguments { get; set; }
    internal Action<string>? BeforeProperty { get; set; }

    internal InstantiationFixtureHost(TypeContext context, TypeAlgebra algebra, CheckerLinks links, AlgebraFixtureHost relations,
        IInstantiationFixtureSource? source = null)
    {
        this.context = context;
        this.algebra = algebra;
        this.relations = relations;
        this.links = links;
        this.source = source;
        array = source?.ArrayTarget(false) ?? ArrayTargetType("Array");
        readonlyArray = source?.ArrayTarget(true) ?? ArrayTargetType("ReadonlyArray");
        Tuples = new(context, algebra, links, this);
        Engine = new(context, algebra, links, this);
        Objects = new(context, links, Engine, new(TypeArgumentsAsync), this);
        Resolutions = new(links);
        ConstraintDependencies = new(context, relations)
        {
            Instantiator = (type, mapper, cancellation) => Engine.InstantiateAsync(type, mapper, cancellation: cancellation),
            NodeEvaluator = source is null ? null : source.TypeFromNodeAsync
        };
        Constraints = new(
            context,
            algebra,
            Resolutions,
            new((_, _) => throw new InvalidOperationException("Fixture requires mapped modifiers resolution")),
            ConstraintDependencies);
        Mapped = new(context, algebra, Engine, Objects, Tuples, Resolutions, this);
        Members = new(context, algebra, Engine, Mapped, links, Resolutions, new([]), this);
        TypeNodeFlow = new(context, algebra, Mapped, this);
    }

    private InterfaceType ArrayTargetType(string name)
    {
        var symbol = new Symbol(SymbolFlags.Interface | SymbolFlags.Transient, name);
        var target = (InterfaceType)context.NewObjectType(O.Interface | O.Reference, symbol);
        var parameter = context.NewTypeParameter(new(SymbolFlags.TypeParameter | SymbolFlags.Transient, "T"));
        var thisType = context.NewTypeParameter();
        thisType.IsThisType = true;
        thisType.Constraint = target;
        target.ThisType = thisType;
        target.Target = target;
        target.AllTypeParameters = Array.AsReadOnly<Type>([parameter, thisType]);
        target.ResolvedTypeArguments = Array.AsReadOnly<Type>([parameter]);
        target.Instantiations = new() { [new TypeCacheKey([parameter])] = target };
        target.DeclaredMembersResolved = true;
        target.BaseTypesResolved = true;
        var length = new Symbol(SymbolFlags.Property | SymbolFlags.Transient, "length");
        links.Values.Get(length).ResolvedType = context.NumberType;
        target.DeclaredMembers = new Dictionary<string, Symbol> { ["length"] = length }.AsReadOnly();
        return target;
    }

    public Type ArrayTarget(bool isReadonly) => isReadonly ? readonlyArray : array;

    public ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation)
    {
        OnTypeArguments?.Invoke(type);
        if (source is not null)
            return source.TypeArgumentsAsync(type, cancellation);
        return ValueTask.FromResult(
            type.ResolvedTypeArguments ?? throw new InvalidOperationException("Fixture requires deferred type arguments"));
    }

    public ValueTask<Type> NormalizedReferenceAsync(InterfaceType target, IReadOnlyList<Type> arguments, CancellationToken cancellation)
            => Tuples.NormalizeReferenceAsync(target, arguments, cancellation: cancellation);

    public ValueTask<Type> ObjectInstantiationAsync(ObjectType type, TypeMapper mapper, TypeAlias? alias, CancellationToken cancellation)
            => Objects.InstantiateAsync(type, mapper, alias, cancellation);

    public ValueTask<IReadOnlyList<Type>> OuterTypeParametersAsync(SyntaxNode declaration, CancellationToken cancellation)
        => source is null ? ValueTask.FromResult(OuterParameters[declaration]) : source.OuterParametersAsync(declaration, cancellation);

    public ValueTask<Symbol?> TypeReferenceSymbolAsync(TypeReferenceNode reference, CancellationToken cancellation)
        => source is null ? ValueTask.FromResult(ReferenceSymbols[reference]) : source.ReferenceSymbolAsync(reference, cancellation);

    public ValueTask<Symbol> ResolvedSymbolAsync(IdentifierNode identifier, CancellationToken cancellation)
        => source is null ? ValueTask.FromResult(ValueSymbols[identifier]) : source.ValueSymbolAsync(identifier, cancellation);

    public ValueTask<TypeAlias?> AliasForTypeNodeAsync(SyntaxNode node, CancellationToken cancellation)
        => source is null ? ValueTask.FromResult(NodeAliases[node]) : source.AliasAsync(node, cancellation);

    public ValueTask<TypeParameter> MappedParameterAsync(MappedType type, CancellationToken cancellation)
        => Mapped.ParameterAsync(type, cancellation);

    public ValueTask<Type> InstantiateMappedAsync(MappedType type, TypeMapper mapper, TypeAlias? alias, CancellationToken cancellation)
        => Mapped.InstantiateAsync(type, mapper, alias, cancellation);

    public ValueTask<TypeParameter> DeclaredParameterAsync(TypeParameterDeclarationNode declaration, CancellationToken cancellation)
        => source is null ? ValueTask.FromResult(Parameters[declaration]) : source.ParameterAsync(declaration, cancellation);

    public ValueTask<Type?> ParameterConstraintAsync(TypeParameter parameter, CancellationToken cancellation)
        => Constraints.ParameterConstraintAsync(parameter, cancellation);

    public ValueTask<MappedType> DeclaredMappedTypeAsync(MappedTypeNode node, CancellationToken cancellation)
        => source is null ? ValueTask.FromResult(MappedNodes[node]) : source.MappedNodeAsync(node, cancellation);

    public async ValueTask<Type> ApparentTypeAsync(Type type, CancellationToken cancellation)
    {
        if (source is not null)
            return await source.ApparentAsync(type, cancellation).ConfigureAwait(false);
        if ((type.Flags & F.Instantiable) != 0)
            type = await Constraints.BaseConstraintAsync(type, cancellation).ConfigureAwait(false) ?? context.UnknownType;
        if (type is MappedType or IntersectionType)
            throw new InvalidOperationException("Fixture requires apparent mapped/intersection types");
        if ((type.Flags & (F.StringLike | F.NumberLike | F.BigIntLike | F.BooleanLike | F.ESSymbolLike | F.NonPrimitive)) != 0
            || (type.Flags & F.Unknown) != 0 && !context.StrictNullChecks)
            return context.EmptyObjectType; // No primitive library members in this fixture program.
        return (type.Flags & F.Index) != 0 ? context.StringNumberSymbolType : type;
    }

    public async ValueTask<IReadOnlyList<Symbol>> PropertiesAsync(Type type, CancellationToken cancellation)
    {
        if (source is not null)
            return await source.PropertiesAsync(type, cancellation).ConfigureAwait(false);
        if (type is MappedType mapped)
            await Members.ResolveAsync(mapped, cancellation).ConfigureAwait(false);
        if (IsArrayType(type))
            await ArrayElement((TypeReference)type, cancellation).ConfigureAwait(false);
        if (type is ObjectType { ObjectFlags: var flags } structure && (flags & O.MembersResolved) != 0)
            return structure.Properties ?? [];
        if ((type.Flags & (F.Primitive | F.AnyOrUnknown | F.Never)) != 0)
            return [];
        throw new InvalidOperationException("Fixture requires general property resolution");
    }

    public async ValueTask<IReadOnlyList<IndexInfo>> IndexInfosAsync(Type type, CancellationToken cancellation)
    {
        if (source is not null)
            return await source.IndexesAsync(type, cancellation).ConfigureAwait(false);
        await PropertiesAsync(type, cancellation).ConfigureAwait(false);
        return type is StructuredType structured ? structured.IndexInfos : [];
    }

    public async ValueTask<Symbol?> PropertyAsync(Type type, string name, CancellationToken cancellation)
    {
        BeforeProperty?.Invoke(name);
        if (source is not null)
            return await source.PropertyAsync(type, name, cancellation).ConfigureAwait(false);
        await PropertiesAsync(type, cancellation).ConfigureAwait(false);
        return type is StructuredType structured ? structured.Members?.GetValueOrDefault(name) : null;
    }

    public ValueTask<Type> PropertyNameTypeAsync(Symbol symbol, CancellationToken cancellation)
    {
        if (source is not null)
            return source.PropertyNameTypeAsync(symbol, cancellation);
        var name = links.Values.Get(symbol).NameType;
        if (name is not null)
            return ValueTask.FromResult((name.Flags & F.StringOrNumberLiteralOrUnique) != 0 ? name : context.NeverType);
        if (symbol.Name.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Fixture requires private/computed property names");
        return ValueTask.FromResult<Type>(context.GetStringLiteralType(symbol.Name));
    }

    public bool IsReadonly(Symbol symbol) => source?.IsReadonly(symbol) ?? (symbol.CheckFlags & CheckFlags.Readonly) != 0;

    public async ValueTask<IndexInfo?> ApplicableIndexAsync(Type type, Type key, CancellationToken cancellation)
    {
        if (source is not null)
            return await source.ApplicableIndexAsync(type, key, cancellation).ConfigureAwait(false);
        IndexInfo? exact = null, fallback = null;
        foreach (var info in await IndexInfosAsync(type, cancellation).ConfigureAwait(false))
        {
            if (info.KeyType == context.StringType)
            {
                if ((key.Flags & (F.StringLike | F.NumberLike)) != 0)
                    fallback = info;
            }
            else if (info.KeyType == key || relations.Related(key, info.KeyType, false))
            {
                if (exact is not null)
                    throw new InvalidOperationException("Fixture requires multiple applicable index signatures");
                exact = info;
            }
        }
        return exact ?? fallback;
    }

    public ValueTask<Type> ConditionalInstantiationAsync(ConditionalType type, TypeMapper mapper, CancellationToken cancellation)
        => ConditionalInstantiationAsync(type, mapper, null, cancellation);

    public ValueTask CircularPropertyAsync(Symbol symbol, MappedType type, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Diagnostics.Add(DiagnosticCode.TypeOfProperty0CircularlyReferencesItselfInMappedType1);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (source is not null)
            return await source.TypeFromNodeAsync(node, cancellation).ConfigureAwait(false);
        var raw = node is MappedTypeNode mapped ? MappedNodes[mapped]
            : await ConstraintDependencies.TypeFromNodeAsync(node, cancellation).ConfigureAwait(false);
        return await TypeNodeFlow.ApplyAsync(raw, node, cancellation).ConfigureAwait(false);
    }

    public ValueTask<Type> ReducedTypeAsync(Type type, CancellationToken cancellation)
    {
        if (source is not null)
            return source.ReducedTypeAsync(type, cancellation);
        var pending = new Stack<Type>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            if (current is IntersectionType && (current.ObjectFlags & O.IsNeverIntersectionComputed) == 0)
                throw new InvalidOperationException("Fixture requires intersection reduction");
            if (current is UnionOrIntersectionType composite)
                foreach (var part in composite.Types)
                    pending.Push(part);
        }
        return ValueTask.FromResult(type);
    }

    public bool IsArrayType(Type type) =>
        type is TypeReference reference && (reference.Target == array || reference.Target == readonlyArray);

    public bool IsReadonlyArrayType(Type type) => type is TypeReference reference && reference.Target == readonlyArray;

    public ValueTask<Type> WithoutUndefinedFactsAsync(Type type, CancellationToken cancellation)
        => ValueTask.FromResult(algebra.Filter(type, part => part is UnionOrIntersectionType or ObjectType or TypeParameter
            ? throw new InvalidOperationException("Fixture requires structural type facts") : (part.Flags & (F.Undefined | F.Void)) == 0));

    public ValueTask<Type?> InferReverseMappedAsync(Type source, MappedType mapped, IndexType constraint, CancellationToken cancellation)
        => this.source?.ReverseMappedInferenceAsync(source, mapped, constraint, cancellation)
            ?? throw new InvalidOperationException("Fixture requires reverse mapped inference");

    public ValueTask<bool> IsGenericMappedAsync(Type type, CancellationToken cancellation)
        => type is MappedType mapped ? Mapped.IsGenericAsync(mapped, cancellation) : ValueTask.FromResult(false);

    public async ValueTask<Type?> ArrayLikeElementAsync(Type type, CancellationToken cancellation)
    {
        if ((type.Flags & F.Any) != 0)
            return type;
        if (type is TypeReference reference && (reference.Target == array || reference.Target == readonlyArray))
            return await ArrayElement(reference, cancellation).ConfigureAwait(false);
        if ((type.Flags & F.Primitive) != 0)
            return null;
        throw new InvalidOperationException("Fixture requires array-like structural analysis");
    }

    private async ValueTask<Type> ArrayElement(TypeReference reference, CancellationToken cancellation)
    {
        if (source is not null)
            return (await source.IndexesAsync(
                reference,
                cancellation).ConfigureAwait(false)).First(i => i.KeyType == context.NumberType).ValueType;
        if ((reference.ObjectFlags & O.MembersResolved) == 0)
        {
            var target = (InterfaceType)reference.ReferencedType;
            var parameter = target.ResolvedTypeArguments![0];
            var mapped = await Engine.InstantiateAsync(
                parameter,
                TypeMapper.Create([parameter], [reference.ResolvedTypeArguments![0]]),
                cancellation: cancellation).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Array element instantiation failed");
            reference.IndexInfos = [context.NewIndexInfo(context.NumberType, mapped)];
            reference.Members = target.DeclaredMembers;
            reference.Properties = target.DeclaredMembers!.Values.ToArray();
            reference.ObjectFlags |= O.MembersResolved;
        }
        return reference.IndexInfos[0].ValueType;
    }

    public async ValueTask<Type> IndexTypeAsync(Type target, CancellationToken cancellation)
    {
        OnIndex?.Invoke(target);
        if (source is not null)
            return await source.IndexAsync(target, cancellation).ConfigureAwait(false);
        if ((target.Flags & F.InstantiableNonPrimitive) != 0 || TypeConstraints.IsGenericTuple(target))
            return context.GetIndexTypeForGenericType(target);
        if (target is IntersectionType intersection && intersection.Types.All(IsArrayType))
        {
            var parts = new Type[intersection.Types.Count];
            for (int i = 0; i < parts.Length; i++)
                parts[i] = await IndexTypeAsync(intersection.Types[i], cancellation).ConfigureAwait(false);
            return await algebra.UnionAsync(parts, cancellation: cancellation).ConfigureAwait(false);
        }
        if (IsArrayType(target))
        {
            await ArrayElement((TypeReference)target, cancellation).ConfigureAwait(false);
            return await algebra.UnionAsync(
                [context.NumberType, context.GetStringLiteralType("length")],
                cancellation: cancellation).ConfigureAwait(false);
        }
        if (target == context.WildcardType)
            return target;
        if ((target.Flags & F.Unknown) != 0)
            return context.NeverType;
        if ((target.Flags & (F.Any | F.Never)) != 0)
            return context.StringNumberSymbolType;
        if (target is ObjectType { Members: { } members })
        {
            var keys = new List<Type>();
            foreach (var property in members.Values)
                keys.Add(await PropertyNameTypeAsync(property, cancellation).ConfigureAwait(false));
            foreach (var index in ((ObjectType)target).IndexInfos)
            {
                keys.Add(index.KeyType);
                if (index.KeyType == context.StringType)
                    keys.Add(context.NumberType);
            }
            return await algebra.UnionAsync(keys, cancellation: cancellation).ConfigureAwait(false);
        }
        if ((target.Flags & F.Primitive) != 0)
            return context.NeverType; // No standard primitive libraries in this fixture program.
        throw new InvalidOperationException("Fixture requires general keyof resolution");
    }

    public ValueTask<Type> IndexedAccessAsync(Type objectType, Type indexType, CancellationToken cancellation)
            => IndexedAccessAsync(objectType, indexType, 0, null, cancellation);

    public async ValueTask<Type> IndexedAccessAsync(
        Type objectType,
        Type indexType,
        AccessFlags flags,
        TypeAlias? alias,
        CancellationToken cancellation)
    {
        if (source is not null)
            return await source.IndexedAccessAsync(objectType, indexType, flags, alias, cancellation).ConfigureAwait(false);
        if ((objectType.Flags & F.Any) != 0)
            return objectType;
        if ((objectType.Flags & F.Never) != 0)
            return context.NeverType;
        bool genericIndex = (await Mapped.GenericFlagsAsync(indexType, cancellation).ConfigureAwait(false) & O.IsGenericIndexType) != 0;
        if (genericIndex || objectType is TypeParameter)
        {
            return context.GetGenericIndexedAccess(objectType, indexType, flags, alias);
        }
        if (indexType is UnionType union && objectType is ObjectType { ObjectFlags: var objectFlags }
            && (objectFlags & O.MembersResolved) != 0)
        {
            if (alias is not null)
                throw new InvalidOperationException("Fixture requires indexed union alias handling");
            var values = new Type[union.Types.Count];
            for (int i = 0; i < values.Length; i++)
                values[i] = await IndexedAccessAsync(objectType, union.Types[i], flags, null, cancellation).ConfigureAwait(false);
            return (flags & AccessFlags.Writing) != 0
                ? await algebra.IntersectionAsync(values, cancellation: cancellation).ConfigureAwait(false)
                : await algebra.UnionAsync(values, cancellation: cancellation).ConfigureAwait(false);
        }
        if ((indexType.Flags & F.Number) != 0
            && objectType is TypeReference reference
            && (reference.Target == array || reference.Target == readonlyArray))
            return await ArrayElement(reference, cancellation).ConfigureAwait(false);
        if ((indexType.Flags & F.Number) != 0 && objectType is TypeReference { Target: TupleType } tuple)
        {
            if ((tuple.ObjectFlags & O.MembersResolved) == 0)
            {
                var target = (TupleType)tuple.ReferencedType;
                var parameters = target.ResolvedTypeArguments!;
                var mapper = TypeMapper.Create(parameters.ToArray(), tuple.ResolvedTypeArguments!.Take(parameters.Count).ToArray());
                var formal = await algebra.UnionAsync(parameters, cancellation: cancellation).ConfigureAwait(false);
                var element = await Engine.InstantiateAsync(formal, mapper, cancellation: cancellation).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Tuple index instantiation failed");
                var arrayTarget = (InterfaceType)(target.IsReadonly ? readonlyArray : array);
                var arrayParameter = arrayTarget.ResolvedTypeArguments![0];
                var indexValue = await Engine.InstantiateAsync(
                    arrayParameter,
                    TypeMapper.Create([arrayParameter], [element]),
                    cancellation: cancellation).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Tuple base index instantiation failed");
                tuple.IndexInfos = [context.NewIndexInfo(context.NumberType, indexValue)];
                tuple.ObjectFlags |= O.MembersResolved;
            }
            return tuple.IndexInfos[0].ValueType;
        }
        if ((indexType.Flags & F.Number) != 0 && (objectType.Flags & F.Primitive) != 0)
            return context.UnknownType;
        if (indexType is LiteralType { Value: string name })
        {
            if (objectType is TypeReference { Target: TupleType target } fixedTuple
                && int.TryParse(
                    name,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int position)
                && position < target.FixedLength && name == position.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                await IndexedAccessAsync(objectType, context.NumberType, cancellation).ConfigureAwait(false);
                return fixedTuple.ResolvedTypeArguments![position];
            }
            return await relations.GetPropertyTypeAsync(objectType, name, cancellation).ConfigureAwait(false) ?? context.UnknownType;
        }
        throw new InvalidOperationException("Fixture requires general indexed access");
    }

    public ValueTask<Type> ConditionalInstantiationAsync(
        ConditionalType type,
        TypeMapper mapper,
        TypeAlias? alias,
        CancellationToken cancellation)
        => source?.ConditionalInstantiationAsync(type, mapper, alias, cancellation)
            ?? throw new InvalidOperationException("Fixture requires conditional evaluation");

    public async ValueTask<bool> IsGenericTypeAsync(Type type, CancellationToken cancellation)
        => await Mapped.GenericFlagsAsync(type, cancellation).ConfigureAwait(false) != 0;

    public async ValueTask<bool> IsAssignableAsync(Type source, Type target, CancellationToken cancellation)
        => this.source is not null ? await this.source.AssignableAsync(source, target, cancellation).ConfigureAwait(false)
            : source == target || (source.Flags & (F.Any | F.Never)) != 0
            || (target is TypeParameter && (source.Flags & (F.Primitive | F.Unknown)) != 0
                ? false : relations.Related(source, target, false));

    public ValueTask<bool> IsEmptyAnonymousAsync(Type type, CancellationToken cancellation)
            => relations.IsEmptyAnonymousObjectAsync(type, cancellation);

    public void InstantiationLimit(int depth, int count) =>
        Diagnostics.Add(DiagnosticCode.TypeInstantiationIsExcessivelyDeepAndPossiblyInfinite);

    public void TupleTooLarge() => Diagnostics.Add(DiagnosticCode.ExpressionProducesATupleTypeThatIsTooLargeToRepresent);

    public void CrossProductTooLarge(long size) => Diagnostics.Add(DiagnosticCode.ExpressionProducesAUnionTypeThatIsTooComplexToRepresent);
}

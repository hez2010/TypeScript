using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using Type = TypeScript.Compiler.Checking.Type;
using F = TypeScript.Compiler.Checking.TypeFlags;
using O = TypeScript.Compiler.Checking.ObjectFlags;

namespace TypeScript.Compatibility;

// Fixed, resolved fixture dependencies. No AST/member/inference fallback is
// supplied: an unsupported query fails the differential test.
internal sealed class InstantiationFixtureHost : ITypeInstantiationHost, ITupleTypeHost, IObjectInstantiationHost, IMappedTypeHost
{
    private readonly TypeContext context;
    private readonly TypeAlgebra algebra;
    private readonly AlgebraFixtureHost relations;
    private readonly Dictionary<(Type, Type, AccessFlags), Type> accesses = [];
    private readonly InterfaceType array, readonlyArray;
    internal TupleTypes Tuples { get; }
    internal TypeInstantiation Engine { get; }
    internal ObjectInstantiation Objects { get; }
    internal MappedTypes Mapped { get; }
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
    internal List<int> Diagnostics { get; } = [];
    internal Action<Type>? OnIndex { get; set; }
    internal Action<TypeReference>? OnTypeArguments { get; set; }

    internal InstantiationFixtureHost(TypeContext context, TypeAlgebra algebra, CheckerLinks links, AlgebraFixtureHost relations)
    {
        this.context = context;
        this.algebra = algebra;
        this.relations = relations;
        array = ArrayTargetType("Array");
        readonlyArray = ArrayTargetType("ReadonlyArray");
        Tuples = new(context, algebra, links, this);
        Engine = new(context, algebra, links, this);
        Objects = new(context, links, Engine, new(TypeArgumentsAsync), this);
        Resolutions = new(links);
        ConstraintDependencies = new(context, relations)
        {
            Instantiator = (type, mapper, cancellation) => Engine.InstantiateAsync(type, mapper, cancellation: cancellation)
        };
        Constraints = new(
            context,
            algebra,
            Resolutions,
            new((_, _) => throw new InvalidOperationException("Fixture requires mapped modifiers resolution")),
            ConstraintDependencies);
        Mapped = new(context, algebra, Engine, Objects, Tuples, Resolutions, this);
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
        return target;
    }

    public Type ArrayTarget(bool isReadonly) => isReadonly ? readonlyArray : array;

    public ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation)
    {
        OnTypeArguments?.Invoke(type);
        return ValueTask.FromResult(
            type.ResolvedTypeArguments ?? throw new InvalidOperationException("Fixture requires deferred type arguments"));
    }

    public ValueTask<Type> NormalizedReferenceAsync(InterfaceType target, IReadOnlyList<Type> arguments, CancellationToken cancellation)
            => Tuples.NormalizeReferenceAsync(target, arguments, cancellation: cancellation);

    public ValueTask<Type> ObjectInstantiationAsync(ObjectType type, TypeMapper mapper, TypeAlias? alias, CancellationToken cancellation)
            => Objects.InstantiateAsync(type, mapper, alias, cancellation);

    public ValueTask<IReadOnlyList<Type>> OuterTypeParametersAsync(SyntaxNode declaration, CancellationToken cancellation)
        => ValueTask.FromResult(OuterParameters[declaration]);

    public ValueTask<Symbol?> TypeReferenceSymbolAsync(TypeReferenceNode reference, CancellationToken cancellation)
        => ValueTask.FromResult(ReferenceSymbols[reference]);

    public ValueTask<Symbol> ResolvedSymbolAsync(IdentifierNode identifier, CancellationToken cancellation)
        => ValueTask.FromResult(ValueSymbols[identifier]);

    public ValueTask<TypeAlias?> AliasForTypeNodeAsync(SyntaxNode node, CancellationToken cancellation)
        => ValueTask.FromResult(NodeAliases[node]);

    public ValueTask<TypeParameter> MappedParameterAsync(MappedType type, CancellationToken cancellation)
        => Mapped.ParameterAsync(type, cancellation);

    public ValueTask<Type> InstantiateMappedAsync(MappedType type, TypeMapper mapper, TypeAlias? alias, CancellationToken cancellation)
        => Mapped.InstantiateAsync(type, mapper, alias, cancellation);

    public ValueTask<TypeParameter> DeclaredParameterAsync(TypeParameterDeclarationNode declaration, CancellationToken cancellation)
        => ValueTask.FromResult(Parameters[declaration]);

    public ValueTask<Type?> ParameterConstraintAsync(TypeParameter parameter, CancellationToken cancellation)
        => Constraints.ParameterConstraintAsync(parameter, cancellation);

    public async ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var raw = node is MappedTypeNode mapped ? MappedNodes[mapped]
            : await ConstraintDependencies.TypeFromNodeAsync(node, cancellation).ConfigureAwait(false);
        return await TypeNodeFlow.ApplyAsync(raw, node, cancellation).ConfigureAwait(false);
    }

    public ValueTask<Type> ReducedTypeAsync(Type type, CancellationToken cancellation)
    {
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
            => throw new InvalidOperationException("Fixture requires reverse mapped inference");

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
            reference.ObjectFlags |= O.MembersResolved;
        }
        return reference.IndexInfos[0].ValueType;
    }

    public async ValueTask<Type> IndexTypeAsync(Type target, CancellationToken cancellation)
    {
        OnIndex?.Invoke(target);
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
            return await algebra.UnionAsync(
                members.Keys.Select(context.GetStringLiteralType).ToArray(),
                cancellation: cancellation).ConfigureAwait(false);
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
        if ((objectType.Flags & F.Any) != 0)
            return objectType;
        if ((objectType.Flags & F.Never) != 0)
            return context.NeverType;
        if (objectType is TypeParameter || (indexType.Flags & F.InstantiableNonPrimitive) != 0)
        {
            if (alias is not null)
                throw new InvalidOperationException("Fixture requires indexed alias caching");
            var key = (objectType, indexType, flags & AccessFlags.Persistent);
            if (!accesses.TryGetValue(key, out var cached))
                accesses.Add(key, cached = context.NewIndexedAccessType(key.objectType, key.indexType, key.Item3));
            return cached;
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
                var arrayTarget = target.IsReadonly ? readonlyArray : array;
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
            => throw new InvalidOperationException("Fixture requires conditional evaluation");

    public async ValueTask<bool> IsGenericTypeAsync(Type type, CancellationToken cancellation)
        => await Mapped.GenericFlagsAsync(type, cancellation).ConfigureAwait(false) != 0;

    public ValueTask<bool> IsAssignableAsync(Type source, Type target, CancellationToken cancellation)
            => ValueTask.FromResult((source.Flags & F.Any) != 0 || relations.Related(source, target, false));

    public ValueTask<bool> IsEmptyAnonymousAsync(Type type, CancellationToken cancellation)
            => relations.IsEmptyAnonymousObjectAsync(type, cancellation);

    public void InstantiationLimit(int depth, int count) => Diagnostics.Add(2589);

    public void TupleTooLarge() => Diagnostics.Add(2800);

    public void CrossProductTooLarge(long size) => Diagnostics.Add(2590);
}

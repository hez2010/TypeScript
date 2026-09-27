using TypeScript.Compiler.Text;
using System.Numerics;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

/// <summary>Exclusive semantic ownership, canonical types and instantiation caches for one checker.</summary>
public sealed class TypeContext
{
    private uint typeCount, signatureCount;
    private readonly Dictionary<TextSlice, LiteralType> strings = new();
    private readonly Dictionary<double, LiteralType> numbers = [];
    private readonly Dictionary<BigInteger, LiteralType> bigints = [];
    private readonly Dictionary<(Symbol Enum, object Value), LiteralType> enumLiterals = [];
    private readonly Dictionary<TypeCacheKey, UnionType> unions = [];
    private readonly Dictionary<(TypeCacheKey Types, AccessFlags Flags), IndexedAccessType> indexedAccesses = [];
    private readonly Dictionary<(Type, bool), IndexType> indexes = [];
    private readonly Dictionary<(Type, Type), SubstitutionType> substitutions = [];
    private readonly Dictionary<Symbol, UniqueSymbolType> uniqueSymbols = [];
    internal IEnumerable<UniqueSymbolType> UniqueSymbols => uniqueSymbols.Values;

    public bool StrictNullChecks { get; }
    public bool ExactOptionalPropertyTypes { get; }
    public uint TypeCount => typeCount;
    public uint SignatureCount => signatureCount;
    public IntrinsicType AnyType { get; }
    public IntrinsicType AutoType { get; }
    public IntrinsicType WildcardType { get; }
    public IntrinsicType BlockedStringType { get; }
    public IntrinsicType ErrorType { get; }
    public IntrinsicType UnresolvedType { get; }
    public IntrinsicType NonInferrableAnyType { get; }
    public IntrinsicType IntrinsicMarkerType { get; }
    public IntrinsicType UnknownType { get; }
    public IntrinsicType UndefinedType { get; }
    public IntrinsicType UndefinedWideningType { get; }
    public IntrinsicType MissingType { get; }
    public IntrinsicType UndefinedOrMissingType => ExactOptionalPropertyTypes ? MissingType : UndefinedType;
    public IntrinsicType OptionalType { get; }
    public IntrinsicType NullType { get; }
    public IntrinsicType NullWideningType { get; }
    public IntrinsicType StringType { get; }
    public IntrinsicType NumberType { get; }
    public IntrinsicType BigIntType { get; }
    public LiteralType RegularFalseType { get; }
    public LiteralType FalseType { get; }
    public LiteralType RegularTrueType { get; }
    public LiteralType TrueType { get; }
    public Type BooleanType { get; }
    public IntrinsicType ESSymbolType { get; }
    public IntrinsicType VoidType { get; }
    public IntrinsicType NeverType { get; }
    public IntrinsicType SilentNeverType { get; }
    public IntrinsicType ImplicitNeverType { get; }
    public IntrinsicType UnreachableNeverType { get; }
    public IntrinsicType NonPrimitiveType { get; }
    public IntrinsicType UniqueLiteralType { get; }
    public Type StringOrNumberType { get; }
    public Type StringNumberSymbolType { get; }
    public Type NumberOrBigIntType { get; }
    public TemplateLiteralType NumericStringType { get; }
    public Type TemplateConstraintType { get; }
    public ObjectType EmptyObjectType { get; }
    public ObjectType EmptyTypeLiteralType { get; }
    public ObjectType UnknownEmptyObjectType { get; }
    public ObjectType AnyFunctionType { get; }
    public Type UnknownUnionType { get; }
    public ObjectType NoConstraintType { get; }
    public ObjectType CircularConstraintType { get; }
    public ObjectType ResolvingDefaultType { get; }
    public ObjectType EmptyGenericType { get; }

    public TypeContext(bool strictNullChecks = false, bool exactOptionalPropertyTypes = false)
    {
        StrictNullChecks = strictNullChecks;
        ExactOptionalPropertyTypes = exactOptionalPropertyTypes;
        AnyType = new(this, TypeFlags.Any, "any");
        AutoType = new(this, TypeFlags.Any, "any", ObjectFlags.NonInferrableType);
        WildcardType = new(this, TypeFlags.Any, "any");
        BlockedStringType = new(this, TypeFlags.Any, "any");
        ErrorType = new(this, TypeFlags.Any, "error");
        UnresolvedType = new(this, TypeFlags.Any, "unresolved");
        NonInferrableAnyType = new(this, TypeFlags.Any, "any", ObjectFlags.ContainsWideningType);
        IntrinsicMarkerType = new(this, TypeFlags.Any, "intrinsic");
        UnknownType = new(this, TypeFlags.Unknown, "unknown");
        UndefinedType = new(this, TypeFlags.Undefined, "undefined");
        UndefinedWideningType = Widening(UndefinedType);
        MissingType = new(this, TypeFlags.Undefined, "undefined");
        OptionalType = new(this, TypeFlags.Undefined, "undefined");
        NullType = new(this, TypeFlags.Null, "null");
        NullWideningType = Widening(NullType);
        StringType = new(this, TypeFlags.String, "string");
        NumberType = new(this, TypeFlags.Number, "number");
        BigIntType = new(this, TypeFlags.BigInt, "bigint");
        RegularFalseType = new(this, TypeFlags.BooleanLiteral, false);
        FalseType = GetFreshLiteralType(RegularFalseType);
        RegularTrueType = new(this, TypeFlags.BooleanLiteral, true);
        TrueType = GetFreshLiteralType(RegularTrueType);
        BooleanType = GetUnionFromSortedTypes([RegularFalseType, RegularTrueType], ObjectFlags.PrimitiveUnion);
        ESSymbolType = new(this, TypeFlags.ESSymbol, "symbol");
        VoidType = new(this, TypeFlags.Void, "void");
        NeverType = new(this, TypeFlags.Never, "never");
        SilentNeverType = new(this, TypeFlags.Never, "never", ObjectFlags.NonInferrableType);
        ImplicitNeverType = new(this, TypeFlags.Never, "never");
        UnreachableNeverType = new(this, TypeFlags.Never, "never");
        NonPrimitiveType = new(this, TypeFlags.NonPrimitive, "object");
        StringOrNumberType = GetUnionFromSortedTypes([StringType, NumberType], ObjectFlags.PrimitiveUnion);
        StringNumberSymbolType = GetUnionFromSortedTypes([StringType, NumberType, ESSymbolType], ObjectFlags.PrimitiveUnion);
        NumberOrBigIntType = GetUnionFromSortedTypes([NumberType, BigIntType], ObjectFlags.PrimitiveUnion);
        NumericStringType = NewTemplateLiteralType(["", ""], [NumberType]);
        TemplateConstraintType = GetUnionFromSortedTypes(strictNullChecks
            ? [UndefinedType, NullType, StringType, NumberType, BigIntType, RegularFalseType, RegularTrueType]
            : [StringType, NumberType, BigIntType, RegularFalseType, RegularTrueType], ObjectFlags.PrimitiveUnion);
        UniqueLiteralType = new(this, TypeFlags.Never, "never");
        EmptyObjectType = NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved);
        EmptyTypeLiteralType = NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved,
            new Symbol(SymbolFlags.TypeLiteral | SymbolFlags.Transient, Symbol.InternalPrefix + "type"));
        UnknownEmptyObjectType = NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved);
        UnknownUnionType = strictNullChecks ? GetUnionFromSortedTypes([UndefinedType, NullType, UnknownEmptyObjectType], 0) : UnknownType;
        EmptyGenericType = NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved);
        EmptyGenericType.Instantiations = [];
        AnyFunctionType = NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved | ObjectFlags.NonInferrableType);
        NoConstraintType = NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved);
        CircularConstraintType = NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved);
        ResolvingDefaultType = NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved);
        MarkerSuper = NewTypeParameter();
        MarkerSub = NewTypeParameter();
        MarkerSub.Constraint = MarkerSuper;
        VarianceCheckSuper = NewTypeParameter();
        VarianceCheckSub = NewTypeParameter();
        VarianceCheckSub.Constraint = VarianceCheckSuper;
        MarkerOther = NewTypeParameter();
    }

    internal TypeParameter MarkerSuper { get; }
    internal TypeParameter MarkerSub { get; }
    internal TypeParameter VarianceCheckSuper { get; }
    internal TypeParameter VarianceCheckSub { get; }
    internal TypeParameter MarkerOther { get; }

    internal uint NextTypeId() => typeCount = checked(typeCount + 1);

    internal void RequireOwned(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!ReferenceEquals(type.Context, this))
            throw new ArgumentException("Types from different checker contexts cannot be combined", nameof(type));
    }

    internal void RequireOwned(ReadOnlySpan<Type> types)
    {
        foreach (var type in types)
            RequireOwned(type);
    }

    private IntrinsicType Widening(IntrinsicType type) => StrictNullChecks ? type
        : new(this, type.Flags, type.IntrinsicName, ObjectFlags.ContainsWideningType);

    public LiteralType GetStringLiteralType(TextSlice value)
    {
        if (!strings.TryGetValue(value, out var type))
            strings.Add(value, type = new(this, TypeFlags.StringLiteral, value));
        return type;
    }

    public LiteralType GetNumberLiteralType(double value)
    {
        // Dictionary<double, ...> uses Double.Equals, which canonicalizes NaNs
        // and treats both signed zeros as equal, as required by these caches.
        if (!numbers.TryGetValue(value, out var type))
            numbers.Add(value, type = new(this, TypeFlags.NumberLiteral, value));
        return type;
    }

    public LiteralType GetBigIntLiteralType(BigInteger value)
    {
        if (!bigints.TryGetValue(value, out var type))
            bigints.Add(value, type = new(this, TypeFlags.BigIntLiteral, value));
        return type;
    }

    public LiteralType GetEnumLiteralType(TextSlice value, Symbol enumSymbol, Symbol member)
        => EnumLiteral(value, TypeFlags.StringLiteral, enumSymbol, member);

    public LiteralType GetEnumLiteralType(double value, Symbol enumSymbol, Symbol member)
        => EnumLiteral(value, TypeFlags.NumberLiteral, enumSymbol, member);

    private LiteralType EnumLiteral(object value, TypeFlags flags, Symbol enumSymbol, Symbol member)
    {
        ArgumentNullException.ThrowIfNull(enumSymbol);
        ArgumentNullException.ThrowIfNull(member);
        if (!enumLiterals.TryGetValue((enumSymbol, value), out var type))
            enumLiterals.Add((enumSymbol, value), type = new(this, flags | TypeFlags.EnumLiteral, value) { Symbol = member });
        return type;
    }

    public LiteralType GetFreshLiteralType(LiteralType type)
    {
        RequireOwned(type);
        if (type.FreshType is null)
        {
            var fresh = new LiteralType(this, type.Flags, type.Value, type) { Symbol = type.Symbol };
            fresh.FreshType = fresh;
            type.FreshType = fresh;
        }
        return type.FreshType;
    }

    internal LiteralType NewComputedEnumType(Symbol symbol) => new(this, TypeFlags.Enum, null) { Symbol = symbol };

    internal UniqueSymbolType GetUniqueSymbolType(Symbol symbol)
    {
        if (!uniqueSymbols.TryGetValue(symbol, out var type))
            uniqueSymbols.Add(symbol, type = new(this, symbol, TextSlice.ConcatMany(Symbol.InternalPrefix + "@", symbol.Name, "@", TextSlice.Format(symbol.Id))));
        return type;
    }

    internal ObjectType NewObjectType(ObjectFlags flags, Symbol? symbol = null) => flags switch
    {
        _ when (flags & ObjectFlags.ClassOrInterface) != 0 => new InterfaceType(this, flags, symbol),
        _ when (flags & ObjectFlags.Tuple) != 0 => new TupleType(this, flags) { Symbol = symbol },
        _ when (flags & ObjectFlags.Reference) != 0 => new TypeReference(this, flags, symbol),
        _ when (flags & ObjectFlags.Mapped) != 0 => new MappedType(this, flags, symbol),
        _ when (flags & ObjectFlags.ReverseMapped) != 0 => new ReverseMappedType(this, flags, symbol),
        _ when (flags & ObjectFlags.EvolvingArray) != 0 => new EvolvingArrayType(this, flags, symbol),
        _ when (flags & ObjectFlags.InstantiationExpressionType) != 0 => new InstantiationExpressionType(this, flags, symbol),
        _ when (flags & ObjectFlags.Anonymous) != 0 => new ObjectType(this, flags, symbol),
        _ => throw new ArgumentOutOfRangeException(nameof(flags), "Unknown object type kind")
    };

    internal TypeParameter NewTypeParameter(Symbol? symbol = null) => new(this, symbol);

    internal TypeReference CreateTypeReference(InterfaceType target, ReadOnlySpan<Type> arguments, ObjectFlags flags = 0)
    {
        RequireOwned(target);
        RequireOwned(arguments);
        var key = new TypeCacheKey(arguments);
        var cache = target.Instantiations ??= [];
        if (cache.TryGetValue(key, out var existing))
            return (TypeReference)existing;
        var result = new TypeReference(this, ObjectFlags.Reference | flags | PropagatingFlags(arguments), target.Symbol)
        {
            Target = target,
            ResolvedTypeArguments = Array.AsReadOnly(arguments.ToArray())
        };
        cache.Add(key, result);
        return result;
    }

    internal TypeReference CloneTypeReference(TypeReference source)
    {
        RequireOwned(source);
        return new(this, source.ObjectFlags & ~ObjectFlags.MembersResolved, source.Symbol)
        {
            Target = source.Target,
            ResolvedTypeArguments = source.ResolvedTypeArguments
        };
    }

    internal TypeAlias CreateAlias(Symbol symbol, ReadOnlySpan<Type> arguments)
    {
        RequireOwned(arguments);
        return new(symbol, arguments);
    }

    // This is the canonicalization step after union normalization. It deliberately
    // does not perform subtype, literal, template, or constrained-variable reduction.
    internal Type GetUnionFromSortedTypes(ReadOnlySpan<Type> types, ObjectFlags flags, TypeAlias? alias = null, Type? origin = null)
    {
        RequireOwned(types);
        if (origin is not null)
            RequireOwned(origin);
        if (alias is not null)
            foreach (var argument in alias.TypeArguments)
                RequireOwned(argument);
        if (types.Length == 0)
            return NeverType;
        if (types.Length == 1)
            return types[0];
        var key = TypeCacheKey.Union(types, origin, alias);
        if (unions.TryGetValue(key, out var existing))
            return existing;
        var result = new UnionType(this, flags | PropagatingFlags(types, TypeFlags.Nullable), types) { Alias = alias, Origin = origin };
        if (types.Length == 2 && (types[0].Flags & TypeFlags.BooleanLiteral) != 0 && (types[1].Flags & TypeFlags.BooleanLiteral) != 0)
            result.Flags |= TypeFlags.Boolean;
        unions.Add(key, result);
        return result;
    }

    internal UnionType NewUnionType(ReadOnlySpan<Type> types, ObjectFlags flags = 0)
    {
        RequireOwned(types);
        return new(this, flags, types);
    }

    internal IntersectionType NewIntersectionType(ReadOnlySpan<Type> types, ObjectFlags flags = 0)
    {
        RequireOwned(types);
        return new(this, flags, types);
    }

    internal IndexType GetIndexTypeForGenericType(Type type, IndexFlags flags = 0)
    {
        RequireOwned(type);
        bool stringsOnly = (flags & IndexFlags.StringsOnly) != 0;
        if (!indexes.TryGetValue((type, stringsOnly), out var result))
            indexes.Add((type, stringsOnly), result = new(this, type, flags & IndexFlags.StringsOnly));
        return result;
    }

    internal IndexedAccessType NewIndexedAccessType(Type objectType, Type indexType, AccessFlags flags)
    {
        RequireOwned(objectType);
        RequireOwned(indexType);
        return new(this, objectType, indexType, flags);
    }

    internal IndexedAccessType GetGenericIndexedAccess(Type objectType, Type indexType, AccessFlags flags, TypeAlias? alias = null)
    {
        RequireOwned(objectType);
        RequireOwned(indexType);
        if (alias is not null)
            foreach (var argument in alias.TypeArguments)
                RequireOwned(argument);
        var key = (TypeCacheKey.Instantiation([objectType, indexType], alias, false), flags);
        if (!indexedAccesses.TryGetValue(key, out var result))
            indexedAccesses.Add(key, result = new(this, objectType, indexType, flags & AccessFlags.Persistent) { Alias = alias });
        return result;
    }

    internal TemplateLiteralType NewTemplateLiteralType(ReadOnlySpan<TextSlice> texts, ReadOnlySpan<Type> types)
    {
        RequireOwned(types);
        if (types.Length == 0 || texts.Length != types.Length + 1)
            throw new ArgumentException("Template texts must surround at least one type");
        return new(this, texts, types);
    }

    internal StringMappingType NewStringMappingType(Symbol symbol, Type target)
    {
        RequireOwned(target);
        return new(this, symbol, target);
    }

    internal Type GetSubstitutionType(Type baseType, Type constraint)
    {
        RequireOwned(baseType);
        RequireOwned(constraint);
        if ((constraint.Flags & TypeFlags.AnyOrUnknown) != 0 || constraint == baseType || (baseType.Flags & TypeFlags.Any) != 0)
            return baseType;
        return GetOrCreateSubstitutionType(baseType, constraint);
    }

    internal SubstitutionType GetOrCreateSubstitutionType(Type baseType, Type constraint)
    {
        RequireOwned(baseType);
        RequireOwned(constraint);
        if (!substitutions.TryGetValue((baseType, constraint), out var result))
            substitutions.Add((baseType, constraint), result = new(this, baseType, constraint));
        return result;
    }

    internal Signature NewSignature(SignatureFlags flags, SyntaxNode? declaration, ReadOnlySpan<TypeParameter> typeParameters,
        Symbol? thisParameter, ReadOnlySpan<Symbol> parameters, Type? returnType, TypePredicate? predicate, int minimumArgumentCount)
    {
        foreach (var parameter in typeParameters)
            RequireOwned(parameter);
        if (returnType is not null)
            RequireOwned(returnType);
        if (predicate?.Type is { } predicateType)
            RequireOwned(predicateType);
        return new(this, signatureCount = checked(signatureCount + 1))
        {
            Flags = flags,
            Declaration = declaration,
            TypeParameters = Array.AsReadOnly(typeParameters.ToArray()),
            ThisParameter = thisParameter,
            Parameters = Array.AsReadOnly(parameters.ToArray()),
            ResolvedReturnType = returnType,
            ResolvedTypePredicate = predicate,
            MinArgumentCount = minimumArgumentCount
        };
    }

    internal IndexInfo NewIndexInfo(Type keyType, Type valueType, bool isReadonly = false, SyntaxNode? declaration = null,
        ReadOnlySpan<SyntaxNode> components = default)
    {
        RequireOwned(keyType);
        RequireOwned(valueType);
        return new(keyType, valueType, isReadonly, declaration, components);
    }

    internal Signature CloneSignature(Signature source)
    {
        if (source.Context != this)
            throw new ArgumentException("Signature belongs to a different checker context", nameof(source));
        var result = NewSignature(source.Flags & SignatureFlags.PropagatingFlags, source.Declaration,
            source.TypeParameters.ToArray(), source.ThisParameter, source.Parameters.ToArray(), null, null, source.MinArgumentCount);
        result.Target = source.Target;
        result.Mapper = source.Mapper;
        result.Composite = source.Composite;
        return result;
    }

    internal static ObjectFlags PropagatingFlags(ReadOnlySpan<Type> types, TypeFlags excludeKinds = 0)
    {
        var flags = ObjectFlags.None;
        foreach (var type in types)
            if ((type.Flags & excludeKinds) == 0)
                flags |= type.ObjectFlags;
        return flags & ObjectFlags.PropagatingFlags;
    }
}

// Keys retain the complete identity sequence: hash collisions cannot alias types.
// Owning context is checked before constructing a key, so local IDs are sufficient.
internal sealed class TypeCacheKey : IEquatable<TypeCacheKey>
{
    private readonly long[] values;
    private readonly int hash;

    internal TypeCacheKey(ReadOnlySpan<Type> types)
    {
        values = new long[types.Length];
        for (int i = 0; i < types.Length; i++)
            values[i] = types[i].Id;
        hash = Hash(values);
    }

    private TypeCacheKey(long[] values)
    {
        this.values = values;
        hash = Hash(values);
    }

    internal static TypeCacheKey Union(ReadOnlySpan<Type> types, Type? origin, TypeAlias? alias)
        => new(UnionValues(types, origin, alias));

    private static long[] UnionValues(ReadOnlySpan<Type> types, Type? origin, TypeAlias? alias, int suffixLength = 0)
    {
        int typeCount = origin is UnionOrIntersectionType compositeOrigin ? compositeOrigin.Types.Count : types.Length;
        int prefixLength = origin is IndexType ? 2 : 1;
        var values = new long[prefixLength + typeCount + 1 + (alias is null ? 0 : alias.TypeArguments.Count + 1) + suffixLength];
        int offset = 0;
        switch (origin)
        {
            case null:
                values[offset++] = 0;
                foreach (var type in types)
                    values[offset++] = type.Id;
                break;
            case UnionOrIntersectionType composite:
                values[offset++] = composite is UnionType ? 1 : 2;
                foreach (var type in composite.Types)
                    values[offset++] = type.Id;
                break;
            case IndexType index:
                values[offset++] = 3;
                values[offset++] = index.Id;
                foreach (var type in types)
                    values[offset++] = type.Id;
                break;
            default:
                throw new ArgumentException("Invalid union origin", nameof(origin));
        }
        values[offset++] = -1;
        if (alias is not null)
        {
            values[offset++] = alias.Symbol.Id;
            foreach (var type in alias.TypeArguments)
                values[offset++] = type.Id;
        }
        return values;
    }

    internal static TypeCacheKey Instantiation(ReadOnlySpan<Type> types, TypeAlias? alias, bool singleSignature)
    {
        var values = UnionValues(types, null, alias, suffixLength: 2);
        values[^2] = -2;
        values[^1] = singleSignature ? 1 : 0;
        return new(values);
    }

    private static int Hash(long[] values)
    {
        var hash = new HashCode();
        foreach (long value in values)
            hash.Add(value);
        return hash.ToHashCode();
    }

    public bool Equals(TypeCacheKey? other) => other is not null && hash == other.hash && values.AsSpan().SequenceEqual(other.values);

    public override bool Equals(object? obj) => obj is TypeCacheKey other && Equals(other);

    public override int GetHashCode() => hash;
}

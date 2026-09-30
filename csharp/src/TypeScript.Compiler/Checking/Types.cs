using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

// Type identity and all lazy semantic state belong to one checker context. Parsed
// syntax and binding may be shared by programs; these objects must not be shared.
public abstract class Type
{
    public TypeContext Context { get; }
    public uint Id { get; }
    public TypeFlags Flags { get; internal set; }
    public ObjectFlags ObjectFlags { get; internal set; }
    public Symbol? Symbol { get; internal set; }
    public TypeAlias? Alias { get; internal set; }

    private protected Type(TypeContext context, TypeFlags flags, ObjectFlags objectFlags = 0)
    {
        Context = context;
        Id = context.NextTypeId();
        Flags = flags;
        ObjectFlags = objectFlags;
    }

    public sealed override int GetHashCode() => unchecked((int)Id);

    public bool IsFreshLiteral => this is LiteralType literal && literal.FreshType == this;
    public bool IsUnit => (Flags & TypeFlags.Unit) != 0;
    public bool IsLiteral => (Flags & TypeFlags.Boolean) != 0 || (this is UnionType union
        ? (Flags & TypeFlags.EnumLiteral) != 0 || union.Types.All(t => t.IsUnit) : IsUnit);
}

public sealed class TypeAlias
{
    public Symbol Symbol { get; }
    public IReadOnlyList<Type> TypeArguments { get; }

    internal TypeAlias(Symbol symbol, ReadOnlySpan<Type> arguments)
    {
        Symbol = symbol;
        TypeArguments = Array.AsReadOnly(arguments.ToArray());
    }
}

public sealed class IntrinsicType : Type
{
    public Utf8String IntrinsicName { get; }

    internal IntrinsicType(TypeContext context, TypeFlags flags, Utf8String name, ObjectFlags objectFlags = 0)
            : base(context, flags, objectFlags) => IntrinsicName = name;
}

public sealed class LiteralType : Type
{
    public object? Value { get; }
    public LiteralType? FreshType { get; internal set; }
    public LiteralType RegularType { get; }

    internal LiteralType(TypeContext context, TypeFlags flags, object? value, LiteralType? regular = null)
        : base(context, flags)
    {
        Value = value;
        RegularType = regular ?? this;
    }
}

public sealed class UniqueSymbolType : Type
{
    public Utf8String Name { get; }

    internal UniqueSymbolType(TypeContext context, Symbol symbol, Utf8String name) : base(context, TypeFlags.UniqueESSymbol)
    {
        Symbol = symbol;
        Name = name;
    }
}

public abstract class ConstrainedType : Type
{
    internal Type? ResolvedBaseConstraint { get; set; }

    private protected ConstrainedType(TypeContext context, TypeFlags flags, ObjectFlags objectFlags = 0)
            : base(context, flags, objectFlags) { }
}

public abstract class StructuredType : ConstrainedType
{
    public IReadOnlyDictionary<Utf8String, Symbol>? Members { get; internal set; }
    public IReadOnlyList<Symbol>? Properties { get; internal set; }
    public IReadOnlyList<Signature> CallSignatures { get; internal set; } = [];
    public IReadOnlyList<Signature> ConstructSignatures { get; internal set; } = [];
    public IReadOnlyList<IndexInfo> IndexInfos { get; internal set; } = [];
    internal Type? WithoutAbstractConstructSignatures { get; set; }

    private protected StructuredType(TypeContext context, TypeFlags flags, ObjectFlags objectFlags)
        : base(context, flags, objectFlags) { }
}

public class ObjectType : StructuredType
{
    internal Type? Target { get; set; }
    internal TypeMapper? Mapper { get; set; }
    internal Dictionary<TypeCacheKey, Type>? Instantiations { get; set; }

    internal ObjectType(TypeContext context, ObjectFlags flags, Symbol? symbol = null)
        : base(context, TypeFlags.Object, flags) => Symbol = symbol;
}

public class TypeReference : ObjectType
{
    internal SyntaxNode? Node { get; set; }
    // null means unresolved; an empty list means resolved with no arguments.
    public IReadOnlyList<Type>? ResolvedTypeArguments { get; internal set; }
    public Type ReferencedType => Target ?? throw new InvalidOperationException("Type reference has no target");

    internal TypeReference(TypeContext context, ObjectFlags flags, Symbol? symbol = null) : base(context, flags, symbol) { }
}

public class InterfaceType : TypeReference
{
    internal IReadOnlyList<Type> AllTypeParameters { get; set; } = [];
    internal int OuterTypeParameterCount { get; set; }
    public TypeParameter? ThisType { get; internal set; }
    internal bool BaseTypesResolved { get; set; }
    internal bool DeclaredMembersResolved { get; set; }
    internal Type? ResolvedBaseConstructorType { get; set; }
    internal IReadOnlyList<Type>? ResolvedBaseTypes { get; set; }
    internal IReadOnlyDictionary<Utf8String, Symbol>? DeclaredMembers { get; set; }
    internal IReadOnlyList<Signature>? DeclaredCallSignatures { get; set; }
    internal IReadOnlyList<Signature>? DeclaredConstructSignatures { get; set; }
    internal IReadOnlyList<IndexInfo>? DeclaredIndexInfos { get; set; }

    internal InterfaceType(TypeContext context, ObjectFlags flags, Symbol? symbol = null) : base(context, flags, symbol) { }
}

public readonly record struct TupleElementInfo(ElementFlags Flags, SyntaxNode? LabeledDeclaration = null);

public sealed class TupleType : InterfaceType
{
    public IReadOnlyList<TupleElementInfo> ElementInfos { get; internal set; } = [];
    public int MinLength { get; internal set; }
    public int FixedLength { get; internal set; }
    public ElementFlags CombinedFlags { get; internal set; }
    public bool IsReadonly { get; internal set; }

    internal TupleType(TypeContext context, ObjectFlags flags) : base(context, flags) { }
}

public sealed class InstantiationExpressionType : ObjectType
{
    internal SyntaxNode? Node { get; set; }

    internal InstantiationExpressionType(TypeContext context, ObjectFlags flags, Symbol? symbol) : base(context, flags, symbol) { }
}

public sealed class MappedType : ObjectType
{
    internal MappedTypeNode? Declaration { get; set; }
    internal TypeParameter? TypeParameter { get; set; }
    internal Type? ConstraintType { get; set; }
    internal Type? NameType { get; set; }
    internal Type? TemplateType { get; set; }
    internal Type? ModifiersType { get; set; }
    internal Type? ResolvedApparentType { get; set; }
    internal bool ContainsError { get; set; }

    internal MappedType(TypeContext context, ObjectFlags flags, Symbol? symbol) : base(context, flags, symbol) { }
}

public sealed class ReverseMappedType : ObjectType
{
    internal Type? Source { get; set; }
    internal Type? MappedType { get; set; }
    internal Type? ConstraintType { get; set; }

    internal ReverseMappedType(TypeContext context, ObjectFlags flags, Symbol? symbol) : base(context, flags, symbol) { }
}

public sealed class EvolvingArrayType : ObjectType
{
    internal Type? ElementType { get; set; }
    internal Type? FinalArrayType { get; set; }

    internal EvolvingArrayType(TypeContext context, ObjectFlags flags, Symbol? symbol) : base(context, flags, symbol) { }
}

public abstract class UnionOrIntersectionType : StructuredType
{
    private readonly Type[] types;
    public IReadOnlyList<Type> Types { get; }
    internal ReadOnlySpan<Type> TypesSpan => types;
    internal Dictionary<Utf8String, Symbol>? PropertyCache { get; set; }
    internal Dictionary<Utf8String, Symbol>? PropertyCacheWithoutFunctionAugment { get; set; }
    internal IReadOnlyList<Symbol>? ResolvedProperties { get; set; }

    private protected UnionOrIntersectionType(TypeContext context, TypeFlags flags, ObjectFlags objectFlags, ReadOnlySpan<Type> types)
        : base(context, flags, objectFlags)
    {
        this.types = types.ToArray();
        Types = Array.AsReadOnly(this.types);
    }
}

public sealed class UnionType : UnionOrIntersectionType
{
    internal Type? ResolvedReducedType { get; set; }
    internal Type? RegularType { get; set; }
    public Type? Origin { get; internal set; }
    internal Utf8String? KeyPropertyName { get; set; }
    internal Dictionary<Type, Type>? ConstituentMap { get; set; }

    internal UnionType(TypeContext context, ObjectFlags flags, ReadOnlySpan<Type> types)
            : base(context, TypeFlags.Union, flags, types) { }
}

public sealed class IntersectionType : UnionOrIntersectionType
{
    internal Type? ResolvedApparentType { get; set; }
    internal Type? UniqueLiteralFilledInstantiation { get; set; }

    internal IntersectionType(TypeContext context, ObjectFlags flags, ReadOnlySpan<Type> types)
            : base(context, TypeFlags.Intersection, flags, types) { }
}

public sealed class TypeParameter : ConstrainedType
{
    internal Type? Constraint { get; set; }
    internal TypeParameter? Target { get; set; }
    internal TypeMapper? Mapper { get; set; }
    public bool IsThisType { get; internal set; }
    internal bool IsDistributed { get; set; }
    internal Type? ResolvedDefaultType { get; set; }
    internal TypeParameter? DistributedType { get; set; }

    internal TypeParameter(TypeContext context, Symbol? symbol) : base(context, TypeFlags.TypeParameter) => Symbol = symbol;

    internal Type NonDistributed => IsDistributed
            ? Constraint ?? throw new InvalidOperationException("Distributed parameter has no constraint") : this;
}

public sealed class IndexType : ConstrainedType
{
    public Type Target { get; }
    public IndexFlags IndexFlags { get; }

    internal IndexType(TypeContext context, Type target, IndexFlags flags) : base(context, TypeFlags.Index)
    {
        Target = target;
        IndexFlags = flags;
    }
}

public sealed class IndexedAccessType : ConstrainedType
{
    public Type ObjectType { get; }
    public Type IndexType { get; }
    public AccessFlags AccessFlags { get; }

    internal IndexedAccessType(TypeContext context, Type objectType, Type indexType, AccessFlags flags)
            : base(context, TypeFlags.IndexedAccess)
    {
        ObjectType = objectType;
        IndexType = indexType;
        AccessFlags = flags;
    }
}

public sealed class TemplateLiteralType : ConstrainedType
{
    public IReadOnlyList<Utf8String> Texts { get; }
    public IReadOnlyList<Type> Types { get; }

    internal TemplateLiteralType(TypeContext context, ReadOnlySpan<Utf8String> texts, ReadOnlySpan<Type> types)
            : base(context, TypeFlags.TemplateLiteral)
    {
        Texts = Array.AsReadOnly(texts.ToArray());
        Types = Array.AsReadOnly(types.ToArray());
    }
}

public sealed class StringMappingType : ConstrainedType
{
    public Type Target { get; }

    internal StringMappingType(TypeContext context, Symbol symbol, Type target) : base(context, TypeFlags.StringMapping)
    {
        Symbol = symbol;
        Target = target;
    }
}

public sealed class SubstitutionType : ConstrainedType
{
    public Type BaseType { get; }
    public Type Constraint { get; }

    internal SubstitutionType(TypeContext context, Type baseType, Type constraint) : base(context, TypeFlags.Substitution)
    {
        BaseType = baseType;
        Constraint = constraint;
    }
}

public sealed class ConditionalRoot
{
    public ConditionalTypeNode Node { get; }
    public Type CheckType { get; }
    public Type ExtendsType { get; }
    public bool IsDistributive { get; }
    internal IReadOnlyList<TypeParameter>? InferTypeParameters { get; set; }
    internal IReadOnlyList<TypeParameter>? OuterTypeParameters { get; set; }
    internal Dictionary<TypeCacheKey, Type>? Instantiations { get; set; }
    internal TypeAlias? Alias { get; set; }

    internal ConditionalRoot(ConditionalTypeNode node, Type checkType, Type extendsType, bool distributive)
    {
        Node = node;
        CheckType = checkType;
        ExtendsType = extendsType;
        IsDistributive = distributive;
    }
}

public sealed class ConditionalType : ConstrainedType
{
    public ConditionalRoot Root { get; }
    public Type CheckType { get; }
    public Type ExtendsType { get; }
    internal Type? ResolvedTrueType { get; set; }
    internal Type? ResolvedFalseType { get; set; }
    internal Type? ResolvedInferredTrueType { get; set; }
    internal Type? ResolvedDefaultConstraint { get; set; }
    internal Type? ResolvedConstraintOfDistributive { get; set; }
    internal TypeMapper? Mapper { get; set; }
    internal TypeMapper? CombinedMapper { get; set; }

    internal ConditionalType(TypeContext context, ConditionalRoot root, Type checkType, Type extendsType)
        : base(context, TypeFlags.Conditional)
    {
        Root = root;
        CheckType = checkType;
        ExtendsType = extendsType;
    }
}

public sealed class Signature
{
    public uint Id { get; }
    public TypeContext Context { get; }
    public SignatureFlags Flags { get; internal set; }
    public int MinArgumentCount { get; internal set; }
    internal int ResolvedMinArgumentCount { get; set; } = -1;
    public SyntaxNode? Declaration { get; internal set; }
    public IReadOnlyList<TypeParameter> TypeParameters { get; internal set; } = [];
    public IReadOnlyList<Symbol> Parameters { get; internal set; } = [];
    public Symbol? ThisParameter { get; internal set; }
    internal Type? ResolvedReturnType { get; set; }
    internal TypePredicate? ResolvedTypePredicate { get; set; }
    public Signature? Target { get; internal set; }
    internal TypeMapper? Mapper { get; set; }
    internal Type? IsolatedSignatureType { get; set; }
    internal CompositeSignature? Composite { get; set; }
    public bool HasRestParameter => (Flags & SignatureFlags.HasRestParameter) != 0;

    internal Signature(TypeContext context, uint id)
    {
        Context = context;
        Id = id;
    }
}

internal sealed class CompositeSignature(bool isUnion, IReadOnlyList<Signature> signatures)
{
    internal bool IsUnion { get; } = isUnion;
    internal IReadOnlyList<Signature> Signatures { get; } = signatures;
}

public sealed class TypePredicate(TypePredicateKind kind, int parameterIndex, Utf8String parameterName, Type? type)
{
    public TypePredicateKind Kind { get; } = kind;
    public int ParameterIndex { get; } = parameterIndex;
    public Utf8String ParameterName { get; } = parameterName;
    public Type? Type { get; } = type;
}

public sealed class IndexInfo
{
    public Type KeyType { get; }
    public Type ValueType { get; }
    public bool IsReadonly { get; }
    public SyntaxNode? Declaration { get; }
    internal Symbol? IndexSymbol { get; set; }
    public IReadOnlyList<SyntaxNode> Components { get; }

    internal IndexInfo(Type keyType, Type valueType, bool isReadonly, SyntaxNode? declaration, ReadOnlySpan<SyntaxNode> components)
    {
        KeyType = keyType;
        ValueType = valueType;
        IsReadonly = isReadonly;
        Declaration = declaration;
        Components = Array.AsReadOnly(components.ToArray());
    }
}

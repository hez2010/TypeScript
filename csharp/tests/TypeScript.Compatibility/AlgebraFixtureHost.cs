using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using Type = TypeScript.Compiler.Checking.Type;
using F = TypeScript.Compiler.Checking.TypeFlags;
using O = TypeScript.Compiler.Checking.ObjectFlags;

namespace TypeScript.Compatibility;

// The production algebra requires the complete checker's services. This isolated
// fixture host supplies declared primitive and plain-property object fixtures; queries
// outside that domain fail the probe rather than pretending to resolve a type.
internal sealed class AlgebraFixtureHost(TypeContext context) : ITypeAlgebraHost
{
    private readonly Dictionary<Symbol, Type> propertyTypes = [];
    internal List<int> Diagnostics { get; } = [];
    internal Action? BeforeGenericIndex { get; set; }
    internal Func<Type, CancellationToken, ValueTask<Type?>>? ResolveBaseConstraint { get; set; }

    internal Type Shape(string[] names, Type[] types, Symbol? symbol)
    {
        var result = context.NewObjectType(O.Anonymous | O.MembersResolved, symbol);
        var members = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            var member = new Symbol(SymbolFlags.Property | SymbolFlags.Transient, names[i]);
            members.Add(names[i], member);
            propertyTypes.Add(member, types[i]);
        }
        result.Members = members.AsReadOnly();
        result.Properties = members.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray();
        return result;
    }

    public void ReportComplexity(string operation, long size) => Diagnostics.Add(2590);

    public ValueTask<Type?> GetBaseConstraintAsync(Type type, CancellationToken cancellation)
        => ResolveBaseConstraint is { } resolve ? resolve(
            type,
            cancellation) : ValueTask.FromResult(
            type is TypeParameter parameter ? parameter.Constraint
                : throw new InvalidOperationException("Fixture requires general constraint resolution"));

    public ValueTask<bool> IsSubtypeAsync(Type source, Type target, bool strict, CancellationToken cancellation)
            => ValueTask.FromResult(Related(source, target, strict));

    public ValueTask<bool> IsDerivedFromAsync(Type source, Type target, CancellationToken cancellation)
            => throw new InvalidOperationException("Fixture requires class derivation");

    public ValueTask<IReadOnlyList<Symbol>> GetPropertiesAsync(Type type, CancellationToken cancellation)
    {
        if (type is TypeParameter { Constraint: { } constraint })
            type = constraint;
        if ((type.Flags & F.Primitive) != 0)
            return ValueTask.FromResult<IReadOnlyList<Symbol>>([]);
        return type is ObjectType { ObjectFlags: var flags } structure && (flags & O.MembersResolved) != 0
            ? ValueTask.FromResult<IReadOnlyList<Symbol>>(structure.Properties ?? [])
            : throw new InvalidOperationException("Fixture requires member resolution");
    }

    public ValueTask<Type> GetTypeOfSymbolAsync(Symbol symbol, CancellationToken cancellation)
            => ValueTask.FromResult(propertyTypes[symbol]);

    public ValueTask<Type?> GetPropertyTypeAsync(Type type, string name, CancellationToken cancellation)
            => type is ObjectType structure && (type.ObjectFlags & O.MembersResolved) != 0
                ? ValueTask.FromResult(structure.Members?.GetValueOrDefault(name) is { } symbol ? propertyTypes[symbol] : null)
                : throw new InvalidOperationException("Fixture requires property type resolution");

    public ValueTask<bool> IsEmptyAnonymousObjectAsync(Type type, CancellationToken cancellation)
            => ValueTask.FromResult(Empty(type));

    public ValueTask<bool> IsEmptyObjectAsync(Type type, CancellationToken cancellation) => ValueTask.FromResult(Empty(type));

    public ValueTask<bool> IsGenericIndexAsync(Type type, CancellationToken cancellation)
    {
        BeforeGenericIndex?.Invoke();
        return ValueTask.FromResult((GenericFlags(type) & O.IsGenericIndexType) != 0);
    }

    public ValueTask<bool> MatchesPatternAsync(Type literal, Type pattern, CancellationToken cancellation)
            => ValueTask.FromResult(Matches(literal, pattern));

    internal static O GenericFlags(Type type)
    {
        if (type is UnionOrIntersectionType or SubstitutionType)
        {
            if ((type.ObjectFlags & O.IsGenericTypeComputed) == 0)
            {
                var flags = type is UnionOrIntersectionType composite ? composite.Types.Aggregate(O.None, (f, t) => f | GenericFlags(t))
                    : GenericFlags(((SubstitutionType)type).BaseType) | GenericFlags(((SubstitutionType)type).Constraint);
                type.ObjectFlags |= O.IsGenericTypeComputed | flags;
            }
            return type.ObjectFlags & O.IsGenericType;
        }
        if (type is MappedType or TypeReference)
            throw new InvalidOperationException("Fixture requires mapped/tuple generic analysis");
        O result = (type.Flags & F.InstantiableNonPrimitive) != 0 ? O.IsGenericObjectType : 0;
        if ((type.Flags & (F.InstantiableNonPrimitive | F.Index)) != 0
            || (type.Flags & (F.TemplateLiteral | F.StringMapping)) != 0 && !TypeAlgebra.IsPatternLiteral(type))
            result |= O.IsGenericIndexType;
        return result;
    }

    private bool Empty(Type type)
    {
        if (type is not ObjectType)
            return false;
        if ((type.ObjectFlags & O.MembersResolved) == 0)
            throw new InvalidOperationException("Fixture object has unresolved members");
        return type != context.AnyFunctionType
            && type is StructuredType { Properties: null or { Count: 0 }, CallSignatures.Count: 0, ConstructSignatures.Count: 0, IndexInfos.Count: 0 };
    }

    internal bool Related(Type source, Type target, bool strict)
    {
        if (source is LiteralType sourceLiteral)
            source = sourceLiteral.RegularType;
        if (target is LiteralType targetLiteral)
            target = targetLiteral.RegularType;
        if (source == target || (target.Flags & F.Any) != 0 || (source.Flags & F.Never) != 0 || source == context.WildcardType)
            return true;
        if ((target.Flags & F.Unknown) != 0 && !(strict && (source.Flags & F.Any) != 0))
            return true;
        if ((target.Flags & F.Never) != 0)
            return false;
        if (source is TypeParameter { Constraint: { } constraint })
            return Related(constraint, target, strict);
        if (source is UnionType union)
            return union.Types.All(t => Related(t, target, strict));
        if (target is UnionType targetUnion)
            return targetUnion.Types.Any(t => Related(source, t, strict));
        if ((source.Flags & F.Undefined) != 0 && (!context.StrictNullChecks || (target.Flags & F.VoidLike) != 0))
            return true;
        if ((source.Flags & F.Null) != 0 && (!context.StrictNullChecks || (target.Flags & F.Null) != 0))
            return true;
        if ((source.Flags & F.StringLike) != 0 && (target.Flags & F.String) != 0
            || (source.Flags & F.NumberLike) != 0 && (target.Flags & F.Number) != 0
            || (source.Flags & F.BigIntLike) != 0 && (target.Flags & F.BigInt) != 0
            || (source.Flags & F.BooleanLike) != 0 && (target.Flags & F.Boolean) != 0
            || (source.Flags & F.ESSymbolLike) != 0 && (target.Flags & F.ESSymbol) != 0)
            return true;
        if (source is LiteralType { Value: string } && target is TemplateLiteralType or StringMappingType)
            return Matches(source, target);
        if (target is ObjectType && Empty(target))
            return (source.Flags & F.DefinitelyNonNullable) != 0 || !context.StrictNullChecks && (source.Flags & F.Unknown) != 0;
        if (source is ObjectType { Members: { } sourceMembers } && target is ObjectType { Properties: { } targetProperties })
            return targetProperties.All(
                p => sourceMembers.TryGetValue(p.Name, out var member) && Related(propertyTypes[member], propertyTypes[p], strict));
        if (source is ObjectType && Empty(source))
            return (target.Flags & F.NonPrimitive) != 0 && (!strict || (source.ObjectFlags & O.FreshLiteral) != 0);
        if ((source.Flags & (F.StructuredOrInstantiable | F.EnumLike)) != 0
            || (target.Flags & (F.StructuredOrInstantiable | F.EnumLike)) != 0)
            throw new InvalidOperationException("Fixture requires a non-primitive relation");
        return false;
    }

    private static bool Matches(Type literal, Type pattern)
    {
        if (literal is not LiteralType { Value: string value })
            throw new InvalidOperationException("Pattern source is not a string fixture");
        if (pattern is StringMappingType mapping && (mapping.Target.Flags & (F.Any | F.String)) != 0)
            return TypeAlgebra.ApplyStringMapping(mapping.Symbol!.Name, value) == value;
        if (pattern is TemplateLiteralType template && template.Types.All(t => (t.Flags & (F.Any | F.String)) != 0))
        {
            if (!value.StartsWith(template.Texts[0], StringComparison.Ordinal)
                || !value.EndsWith(template.Texts[^1], StringComparison.Ordinal))
                return false;
            int position = template.Texts[0].Length, end = value.Length - template.Texts[^1].Length;
            if (position > end)
                return false;
            for (int i = 1; i < template.Texts.Count - 1; i++)
            {
                int found = value.IndexOf(template.Texts[i], position, end - position, StringComparison.Ordinal);
                if (found < 0)
                    return false;
                position = found + template.Texts[i].Length;
            }
            return true;
        }
        throw new InvalidOperationException("Fixture requires general template/string-mapping relations");
    }
}

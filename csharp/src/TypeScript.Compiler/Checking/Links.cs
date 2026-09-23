using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

// Sparse BCL tables are per checker, never attached to shared ASTs or symbols.
// TryGet/Has do not allocate links or force assignment of a global node ID.
internal sealed class LinkStore<TKey, TValue> where TKey : class where TValue : class, new()
{
    private readonly Dictionary<TKey, TValue> values = new(ReferenceEqualityComparer.Instance);
    internal int Count => values.Count;

    internal TValue? TryGet(TKey key) => values.GetValueOrDefault(key);

    internal bool Has(TKey key) => values.ContainsKey(key);

    internal bool Remove(TKey key) => values.Remove(key);

    internal TValue Get(TKey key)
    {
        if (!values.TryGetValue(key, out var value))
            values.Add(key, value = new());
        return value;
    }
}

internal sealed class ValueSymbolLinks
{
    internal Type? ResolvedType { get; set; }
    internal Type? WriteType { get; set; }
    internal Symbol? Target { get; set; }
    internal TypeMapper? Mapper { get; set; }
    internal Type? NameType { get; set; }
    internal Type? ContainingType { get; set; }
    internal bool FunctionOrConstructorChecked { get; set; }
}

internal sealed class AliasSymbolLinks
{
    internal Symbol? ImmediateTarget { get; set; }
    internal Symbol? AliasTarget { get; set; }
    internal bool Referenced { get; set; }
    internal SyntaxNode? TypeOnlyDeclaration { get; set; }
}

internal sealed class MappedSymbolLinks
{
    internal Type? KeyType { get; set; }
    internal Symbol? SyntheticOrigin { get; set; }
}

internal sealed class TypeAliasLinks
{
    internal Type? DeclaredType { get; set; }
    internal IReadOnlyList<TypeParameter>? TypeParameters { get; set; }
    internal Dictionary<TypeCacheKey, Type>? Instantiations { get; set; }
    internal bool IsConstructorDeclaredProperty { get; set; }
}

internal sealed class DeclaredTypeLinks
{
    internal Type? DeclaredType { get; set; }
}

internal sealed class NodeLinks
{
    internal NodeCheckFlags Flags { get; set; }
    internal bool? DeclarationRequiresScopeChange { get; set; }
    internal bool HasReportedStatementInAmbientContext { get; set; }
}

internal sealed class TypeNodeLinks
{
    internal Type? ResolvedType { get; set; }
    internal IReadOnlyList<Type>? OuterTypeParameters { get; set; }
}

internal sealed class CheckerLinks
{
    internal LinkStore<Symbol, ValueSymbolLinks> Values { get; } = new();
    internal LinkStore<Symbol, AliasSymbolLinks> Aliases { get; } = new();
    internal LinkStore<Symbol, MappedSymbolLinks> MappedSymbols { get; } = new();
    internal LinkStore<Symbol, TypeAliasLinks> TypeAliases { get; } = new();
    internal LinkStore<Symbol, DeclaredTypeLinks> DeclaredTypes { get; } = new();
    internal LinkStore<SyntaxNode, NodeLinks> Nodes { get; } = new();
    internal LinkStore<SyntaxNode, TypeNodeLinks> TypeNodes { get; } = new();

    internal bool HasResolvedProperty(object target, TypeSystemPropertyName property) => property switch
    {
        TypeSystemPropertyName.Type => Values.Get((Symbol)target).ResolvedType is not null,
        TypeSystemPropertyName.DeclaredType => TypeAliases.Get((Symbol)target).DeclaredType is not null,
        TypeSystemPropertyName.ResolvedTypeArguments => ((TypeReference)target).ResolvedTypeArguments is not null,
        TypeSystemPropertyName.ResolvedBaseTypes => ((InterfaceType)target).BaseTypesResolved,
        TypeSystemPropertyName.ResolvedBaseConstructorType => ((InterfaceType)target).ResolvedBaseConstructorType is not null,
        TypeSystemPropertyName.ResolvedReturnType => ((Signature)target).ResolvedReturnType is not null,
        TypeSystemPropertyName.ResolvedBaseConstraint => ((ConstrainedType)target).ResolvedBaseConstraint is not null,
        TypeSystemPropertyName.InitializerIsUndefined => (Nodes.Get((SyntaxNode)target).Flags & NodeCheckFlags.InitializerIsUndefinedComputed) != 0,
        TypeSystemPropertyName.WriteType => Values.Get((Symbol)target).WriteType is not null,
        TypeSystemPropertyName.AliasTarget => Aliases.Get((Symbol)target).AliasTarget is not null,
        _ => throw new ArgumentOutOfRangeException(nameof(property))
    };
}

// A failed push poisons the participating resolutions, but does not push a frame.
// An already resolved property terminates the search for cycles. ResolutionStart
// isolates nested computations that must not participate in an outer resolution.
internal sealed class TypeResolutionStack(CheckerLinks links)
{
    private readonly List<Entry> entries = [];
    private int resolutionStart;
    internal int Count => entries.Count;

    internal int ResolutionStart
    {
        get => resolutionStart;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, entries.Count);
            resolutionStart = value;
        }
    }

    private readonly record struct Entry(object Target, TypeSystemPropertyName Property, bool Result);

    internal bool Push(object target, TypeSystemPropertyName property)
    {
        int cycle = FindCycleStart(target, property);
        if (cycle >= 0)
        {
            for (int i = cycle; i < entries.Count; i++)
                entries[i] = entries[i] with { Result = false };
            return false;
        }
        entries.Add(new(target, property, true));
        return true;
    }

    internal bool Pop()
    {
        if (entries.Count <= resolutionStart)
            throw new InvalidOperationException("Cannot pop past the current resolution boundary");
        var entry = entries[^1];
        entries.RemoveAt(entries.Count - 1);
        return entry.Result;
    }

    internal int FindCycleStart(object target, TypeSystemPropertyName property)
    {
        for (int i = entries.Count - 1; i >= resolutionStart; i--)
        {
            var entry = entries[i];
            if (links.HasResolvedProperty(entry.Target, entry.Property))
                return -1;
            if (ReferenceEquals(entry.Target, target) && entry.Property == property)
                return i;
        }
        return -1;
    }
}

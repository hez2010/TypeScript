using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal sealed partial class ProgramTypeHost : ISignatureParameterHost, ISignatureComparisonHost, ISignatureCompositionHost
{
    internal SignatureParameters Parameters { get; }
    internal SignatureComparison SignatureComparison { get; }
    internal SignatureComposition SignatureComposition { get; }
    internal Func<Type, Type, bool, CancellationToken, ValueTask<Ternary>>? CompareTypes { get; set; }
    internal Action<Type, Type>? BeforeSignatureIndex { get; set; }
    public Type AnyArray => program.Globals.AnyArrayType!;

    ValueTask<Type> ISignatureParameterHost.IndexedAccessAsync(Type objectType, Type indexType, CancellationToken cancellation)
    {
        BeforeSignatureIndex?.Invoke(objectType, indexType);
        return IndexedAccessAsync(objectType, indexType, cancellation);
    }

    public async ValueTask<Symbol?> ResolveSymbolAsync(Symbol symbol, CancellationToken cancellation)
        =>
            program.Symbols.Merger.GetMergedSymbol(
                await program.Aliases.SymbolAsync(program.Symbols.Merger.GetMergedSymbol(symbol), cancellation: cancellation));

    public ValueTask<Ternary> CompareTypesAsync(Type source, Type target, bool subtype, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (CompareTypes is not null)
            return CompareTypes(source, target, subtype, cancellation);
        if (source is LiteralType s)
            source = s.RegularType;
        if (target is LiteralType t)
            target = t.RegularType;
        if (source == target)
            return ValueTask.FromResult(Ternary.True);
        if (subtype)
            return ValueTask.FromResult(relations.Related(source, target, false) ? Ternary.True : Ternary.False);
        if (((source.Flags | target.Flags) & (TypeFlags.UnionOrIntersection | TypeFlags.IndexedAccess | TypeFlags.Conditional | TypeFlags.Substitution)) == 0)
        {
            if (source.Flags != target.Flags)
                return ValueTask.FromResult(Ternary.False);
            if ((source.Flags & TypeFlags.Singleton) != 0)
                return ValueTask.FromResult(Ternary.True);
        }
        IReadOnlyList<Type> sourceParts = source is UnionType sourceUnion ? sourceUnion.Types : [source];
        IReadOnlyList<Type> targetParts = target is UnionType targetUnion ? targetUnion.Types : [target];
        if (sourceParts.All(PrimitiveAtom) && targetParts.All(PrimitiveAtom))
            return ValueTask.FromResult(sourceParts.Count == targetParts.Count
                && sourceParts.All(
                    s => targetParts.Any(
                        t => s == t || s.Flags == t.Flags && (s.Flags & TypeFlags.Singleton) != 0)) ? Ternary.True : Ternary.False);
        if (((source.Flags | target.Flags) & TypeFlags.StructuredOrInstantiable) != 0)
            throw new InvalidOperationException("Probe requires structural type identity");
        return ValueTask.FromResult(Ternary.False);
    }

    private static bool PrimitiveAtom(Type type) => (type.Flags & (TypeFlags.Intrinsic | TypeFlags.Literal | TypeFlags.UniqueESSymbol)) != 0
        && (type.Flags & (TypeFlags.EnumLike | TypeFlags.UnionOrIntersection)) == 0;
}

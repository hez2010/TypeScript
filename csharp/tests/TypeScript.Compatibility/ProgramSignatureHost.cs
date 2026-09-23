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

    public async ValueTask<Ternary> CompareTypesAsync(Type source, Type target, bool subtype, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (CompareTypes is not null)
            return await CompareTypes(source, target, subtype, cancellation);
        if (subtype)
            return relations.Related(source, target, false) ? Ternary.True : Ternary.False;
        return await Relations.RelatedAsync(source, target, RelationKind.Identity, cancellation) ? Ternary.True : Ternary.False;
    }
}

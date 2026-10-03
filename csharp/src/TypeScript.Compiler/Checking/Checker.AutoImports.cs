using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal Utf8String JsxNamespaceName(SyntaxNode location) => JsxFactoryRoot(JsxFactoryName(location));

    internal async ValueTask<(Symbol Symbol, SymbolFlags Flags, SyntaxNode? TypeOnly)> GetAutoImportTargetAsync(Symbol symbol, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(null, cancellation);
        var resolved = program.Symbols.Merger.GetMergedSymbol((symbol.Flags & SymbolFlags.Alias) != 0
            ? await program.Aliases.ResolveAsync(symbol, cancellation) : symbol)!;
        return (resolved, await program.Aliases.FlagsAsync(resolved, cancellation: cancellation),
            await program.Aliases.TypeOnlyAsync(symbol, cancellation: cancellation));
    }
}

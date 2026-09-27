using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly Dictionary<SourceFileNode, Symbol> externalHelperModules = [];
    private readonly HashSet<(SourceFileNode File, TextSlice Name)> checkedExternalHelpers = [];
    private readonly HashSet<(SyntaxNode Node, DiagnosticCode Code, TextSlice Name)> externalHelperErrors = [];

    private async ValueTask ExternalHelpersAsync(SyntaxNode node, IReadOnlyList<TextSlice> names, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (program.Symbols.Program.Configuration.Options.Boolean("importHelpers") != true || (node.Flags & NodeFlags.Ambient) != 0)
            return;
        var file = SemanticSyntax.Source(node)!;
        if (file.ExternalModuleIndicator is null
            && !(program.Symbols.Binding(file)?.CommonJSModuleIndicator is not null
                && (ModuleKind == 1 || ModuleKind is >= 100 and <= 199)))
            return;
        if (!externalHelperModules.TryGetValue(file, out var module))
        {
            var reference = program.Symbols.Program.GetFile(file.FileName)!.Resolutions.FirstOrDefault(r => r.Node is null
                && r.Specifier == "tslib");
            var resolved = reference?.Resolution.IsResolved == true
                ? program.Symbols.Program.GetFile(reference.Resolution.FileName)?.Binding.Symbol : null;
            resolved ??= program.Symbols.PatternAugmentations.GetValueOrDefault("tslib") ?? program.Symbols.Globals.GetValueOrDefault("\"tslib\"");
            module = program.Symbols.Merger.GetMergedSymbol(resolved) ?? UnknownSymbol;
            if (module == UnknownSymbol)
                Error(
                    node,
                    reference?.Resolution.IsResolved == true
                        ? DiagnosticCode.File0IsNotAModule
                        : DiagnosticCode.ThisSyntaxRequiresAnImportedHelperButModule0CannotBeFound,
                    reference?.Resolution.IsResolved == true ? reference.Resolution.FileName : "tslib");
            externalHelperModules.Add(file, module);
        }
        if (module == UnknownSymbol)
            return;
        var exports = await program.ModuleExports.ResolveAsync(module, cancellation);
        foreach (var name in names)
        {
            if (checkedExternalHelpers.Contains((file, name)))
                continue;
            DiagnosticCode code = DiagnosticCode.None;
            var symbol = await program.Aliases.SymbolAsync(
                program.Symbols.Lookup(exports, name, SymbolFlags.Value),
                cancellation: cancellation);
            if (symbol is null)
                code = DiagnosticCode.ThisSyntaxRequiresAnImportedHelperNamed1WhichDoesNotExistIn0ConsiderUpgradingYourVersionOf0;
            else if (name.Span is "__classPrivateFieldGet" or "__classPrivateFieldSet")
            {
                int minimum = name == "__classPrivateFieldGet" ? 4 : 5;
                bool compatible = false;
                foreach (var signature in await SignaturesAsync(await Values.GetAsync(symbol, cancellation), false, cancellation))
                    if (await Parameters.CountAsync(signature, cancellation) >= minimum)
                    {
                        compatible = true;
                        break;
                    }
                if (!compatible)
                    code = DiagnosticCode.ThisSyntaxRequiresAnImportedHelperNamed1With2ParametersWhichIsNotCompatibleWithTheOneIn0ConsiderUpgradingYourVersionOf0;
            }
            cancellation.ThrowIfCancellationRequested();
            if (code != DiagnosticCode.None && externalHelperErrors.Add((node, code, name)))
            {
                // Helpers with different names carry different diagnostic arguments, even at the same syntax location.
                Diagnostics.Add(code);
                TrackDiagnostic(
                    node,
                    code,
                    code == DiagnosticCode.ThisSyntaxRequiresAnImportedHelperNamed1With2ParametersWhichIsNotCompatibleWithTheOneIn0ConsiderUpgradingYourVersionOf0
                    ? ["tslib", name, name == "__classPrivateFieldGet" ? "4" : "5"] : ["tslib", name]);
            }
            checkedExternalHelpers.Add((file, name));
        }
    }
}

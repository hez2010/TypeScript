using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class CompletionQuery
    {
        private bool forItemResolve;
        private IReadOnlyList<(ImportFix Fix, AutoImportExport Export)> autoImports = [];

        private async ValueTask CollectAutoImportsAsync()
        {
            if (forItemResolve || File.FileName.StartsWith("^/"u8) || importStatement is null && preferences.IncludeCompletionsForModuleExports == false) return;
            if (!autoImportsAvailable) throw new AutoImportsRequiredException();
            var mapped = projection.ToRange(position, position);
            if (mapped.Fidelity != MappingFidelity.Exact) return;
            Utf8String prefix = default;
            if (previous is IdentifierNode identifier)
            {
                mapped = projection.ToRange(await StartAsync(identifier), await StartAsync(identifier));
                if (mapped.Fidelity != MappingFidelity.Exact) return;
                if (previous != contextToken || importStatement is null) prefix = identifier.Text;
            }
            autoImportView ??= new(program, checker, projection, preferences, cancellation, cache: autoImportCache);
            autoImports = await autoImportView.CompletionsAsync(prefix, mapped.Range.Start, jsxOpen, typeOnly);
        }

        private async ValueTask AutoImportItemsAsync(List<CompletionItem> items, Dictionary<Utf8String, bool> names)
        {
            foreach (var (fix, export) in autoImports)
            {
                cancellation.ThrowIfCancellationRequested();
                var data = fix.Data;
                var name = data.Name;
                if (names.GetValueOrDefault(name) || TokenFacts.FromText(name.Span) is >= K.FirstKeyword and < K.FirstContextualKeyword) continue;
                if (!export.UnresolvedAlias && (typeOnly ? (export.Flags & (SymbolFlags.Type | SymbolFlags.Module)) == 0
                    : importStatement is null && (export.Flags & SymbolFlags.Value) == 0)) continue;
                Utf8String insert = default, filter = default;
                CompletionTextEdit? edit = null;
                bool snippet = false;
                if (importStatement is { } statement)
                {
                    snippet = capabilities.Snippets;
                    Utf8String tab = snippet ? "$1"u8 : default;
                    var quoted = EscapeSnippet(Quote(data.ModuleSpecifier));
                    var escapedName = EscapeSnippet(name);
                    Utf8String prefix = statement.TopLevelTypeOnly ? "import type "u8 : "import "u8;
                    Utf8String suffix = await SourceFormatter.ProbablyUsesSemicolonsAsync(File, cancellation) ? ";"u8 : default;
                    insert = autoImportView!.Kind(export, forceImport: true) switch
                    {
                        ImportKind.CommonJS => prefix + escapedName + tab + " = require("u8 + quoted + ")"u8 + suffix,
                        ImportKind.Default => prefix + escapedName + tab + " from "u8 + quoted + suffix,
                        ImportKind.Namespace => prefix + "* as "u8 + escapedName + " from "u8 + quoted + suffix,
                        _ => prefix + "{ "u8 + (statement.TypeOnlySpecifier ? (Utf8String)"type "u8 : default) + escapedName + tab + " } from "u8 + quoted + suffix,
                    };
                    filter = name;
                    edit = new(insert, statement.Replacement!.Value);
                }
                var item = new CompletionItem(name, SymbolClassification.CompletionKind(export.Kind), SortText: importStatement is null ? "16"u8 : "11"u8,
                    InsertText: insert.IsEmpty ? null : insert, FilterText: filter.IsEmpty ? null : filter, TextEdit: edit, InsertTextFormat: snippet ? 2 : null,
                    Data: new(projection.OriginalFileName, position, name, supplementalIndex, data.ModuleSpecifier, data, importStatement is not null),
                    LabelDetails: new(Description: data.ModuleSpecifier), Tags: export.Deprecated ? [1] : null);
                if (projection.IsMapped)
                {
                    var changes = await fix.EditsAsync(projection, options, preferences, cancellation);
                    if (!changes.Safe) continue;
                    item = item with { Detail = changes.Description, AdditionalTextEdits = changes.Edits };
                }
                names[name] = false;
                items.Add(item);
            }
        }
    }
}

using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.LanguageServices;

public sealed record VisualStudioReference(int Id, int? DefinitionId, DocumentLocation Location, int Kind,
    Utf8String ProjectName, IReadOnlyList<ClassifiedTextRun>? DefinitionText);

public sealed partial class LanguageServiceDocument
{
    public async ValueTask<VisualStudioReference[]> GetVisualStudioReferencesAsync(ProjectSnapshot project, DocumentPosition position,
        bool classified = true, CancellationToken cancellation = default)
    {
        using var request = new ProjectRequest(cancellation);
        using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request);
        var data = await ReferenceDataAsync(project, position, new(ReferenceUse.References), lease.Checker, cancellation);
        return await VisualStudioReferencesAsync(project, data, lease.Checker, classified, 0, cancellation);
    }

    internal async ValueTask<VisualStudioReference[]> VisualStudioReferencesAsync(ProjectSnapshot project, IReadOnlyList<ReferenceData> data,
        Checker checker, bool classified, int nextId, CancellationToken cancellation)
    {
        var maps = new DeclarationMaps(project.Program!, cancellation);
        List<VisualStudioReference> items = [];
        foreach (var query in data)
            foreach (var group in query.Groups)
            {
                cancellation.ThrowIfCancellationRequested();
                var node = group.Kind == ReferenceDefinitionKind.Symbol
                    ? group.Symbol?.Declarations.FirstOrDefault() is { } declaration ? declaration.DeclarationName ?? declaration : query.Node : group.Node;
                if (node is null) continue;
                IReadOnlyList<ClassifiedTextRun> runs;
                if (group.Kind is ReferenceDefinitionKind.Symbol or ReferenceDefinitionKind.This)
                {
                    if (group.Symbol is not { } symbol) continue;
                    var location = group.Kind == ReferenceDefinitionKind.This ? node : query.Node;
                    int meaning = await ReferenceNavigation.IntersectingMeaningAsync(location, symbol, cancellation);
                    var flags = ((meaning & ReferenceNavigation.Value) != 0 ? SymbolFlags.Value | SymbolFlags.Signature : 0)
                        | ((meaning & ReferenceNavigation.Type) != 0 ? SymbolFlags.Type : 0)
                        | ((meaning & ReferenceNavigation.Namespace) != 0 ? SymbolFlags.Namespace : 0);
                    var display = await SymbolDisplay.QuickInfoAsync(checker, symbol, location, new(-1, 0), classified, cancellation, flags);
                    runs = classified ? display.Runs : [new("text"u8, display.Text)];
                }
                else runs = [new(group.Kind == ReferenceDefinitionKind.Keyword ? "keyword"u8
                    : group.Kind == ReferenceDefinitionKind.String ? "string"u8 : "text"u8,
                    group.Kind == ReferenceDefinitionKind.Keyword ? TokenFacts.Text(node.Kind) : ReferenceNavigation.Text(node))];
                if (await LocationAsync(new(ReferenceEntryKind.Node, node, null)) is not { } definition) continue;
                int definitionId = nextId++;
                items.Add(new(definitionId, null, definition, 17, project.Id, runs));
                foreach (var entry in group.Entries)
                {
                    if (DeclarationOfSymbol(entry.Node, group.Symbol) || await LocationAsync(entry) is not { } reference) continue;
                    int kind = entry.Kind != ReferenceEntryKind.Range && entry.Node is { } name && ReferenceNavigation.IsWriteAccess(name) ? 4 : 3;
                    items.Add(new(nextId++, definitionId, reference, kind, project.Id, null));
                }
            }
        return items.ToArray();

        async ValueTask<DocumentLocation?> LocationAsync(ReferenceEntry entry)
        {
            var (file, start, end) = await ReferenceRangeAsync(entry, cancellation);
            var mapped = ReferenceMap(project.Program!, maps, file, start, end, MappingFeature.References);
            return mapped.Fidelity is MappingFidelity.Exact or MappingFidelity.Atom ? new(mapped.Uri, mapped.Range) : null;
        }
    }
}

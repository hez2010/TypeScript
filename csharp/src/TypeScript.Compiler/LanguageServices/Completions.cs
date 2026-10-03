using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Projects;

namespace TypeScript.Compiler.LanguageServices;

public sealed record CompletionCapabilities(bool Snippets = false, bool CommitCharacters = false, bool InsertReplace = false,
    bool DefaultCommitCharacters = false, bool DefaultEditRange = false, bool Markdown = false, bool LabelDetails = false);
public sealed record CompletionItemData(Utf8String FileName, int Position, Utf8String Name, int? SupplementalFileIndex = null, Utf8String Source = default,
    AutoImportFix? AutoImport = null, bool IsImportStatementCompletion = false);
public sealed record CompletionTextEdit(Utf8String NewText, DocumentRange Insert, DocumentRange? Replace = null);
public sealed record CompletionLabelDetails(Utf8String? Detail = null, Utf8String? Description = null);
public sealed record CompletionDocumentation(Utf8String Value, Utf8String? Kind = null);
public sealed record CompletionItem(Utf8String Label, int? Kind = null, Utf8String? Detail = null, Utf8String? SortText = null,
    int? InsertTextFormat = null, CompletionTextEdit? TextEdit = null, Utf8String[]? CommitCharacters = null, CompletionItemData? Data = null,
    Utf8String? InsertText = null, Utf8String? FilterText = null, bool? Preselect = null, int[]? Tags = null,
    CompletionLabelDetails? LabelDetails = null, CompletionDocumentation? Documentation = null, DocumentTextEdit[]? AdditionalTextEdits = null)
{
    internal Binding.Symbol? Symbol { get; init; }
}
public sealed record CompletionItemDefaults(Utf8String[]? CommitCharacters = null, DocumentRange? Insert = null, DocumentRange? Replace = null);
public sealed record CompletionList(CompletionItem[] Items, bool IsIncomplete = false, CompletionItemDefaults? ItemDefaults = null);
internal sealed class AutoImportsRequiredException() : Exception("completion list needs auto imports");

public sealed partial class LanguageServiceDocument
{
    public ValueTask<CompletionList?> GetCompletionsAsync(ProjectSnapshot project, DocumentPosition position,
        CompletionCapabilities? capabilities = null, UserPreferences? preferences = null, Utf8String? triggerCharacter = null,
        CancellationToken cancellation = default)
    {
        if (project.Program is null) throw new ArgumentException("Project has no program", nameof(project));
        return CompletionsAsync(project, position, capabilities, preferences, triggerCharacter, cancellation);
    }

    internal ValueTask<CompletionList?> GetJSDocTemplateCompletionsAsync(DocumentPosition position, CompletionCapabilities? capabilities = null,
        UserPreferences? preferences = null, Utf8String? triggerCharacter = null, CancellationToken cancellation = default)
        => CompletionsAsync(null, position, capabilities, preferences, triggerCharacter, cancellation);

    private async ValueTask<CompletionList?> CompletionsAsync(ProjectSnapshot? project, DocumentPosition position,
        CompletionCapabilities? capabilities, UserPreferences? preferences, Utf8String? triggerCharacter, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var candidates = FromPosition(position, MappingFeature.Completion);
        if (candidates.Count == 0 || candidates[0].Position.Fidelity != MappingFidelity.Exact) return null;
        var (projection, mapped) = candidates[0];
        return WithData(await CompletionsAtPositionAsync(project, projection, mapped.Position, capabilities ?? new(),
            preferences ?? new(), triggerCharacter, false, true, cancellation));

        CompletionList? WithData(CompletionList? list)
        {
            if (list is null) return null;
            int index = Array.IndexOf(projections, projection) - 1;
            return list with { Items = list.Items.Select(item => item.Data is not null ? item : item with
                { Data = new(projection.OriginalFileName, mapped.Position, item.Label, index >= 0 ? index : null) }).ToArray() };
        }
    }

    internal ValueTask<CompletionList?> GetCompletionsAtPositionAsync(ProjectSnapshot project, int position,
        UserPreferences preferences, Utf8String? triggerCharacter, bool includeSymbols, CancellationToken cancellation)
        => CompletionsAtPositionAsync(project, projections[0], position, new(), preferences, triggerCharacter, includeSymbols,
            project.Kind != ProjectKind.Synthetic, cancellation);

    private async ValueTask<CompletionList?> CompletionsAtPositionAsync(ProjectSnapshot? project, DocumentProjection projection, int position,
        CompletionCapabilities capabilities, UserPreferences preferences, Utf8String? triggerCharacter, bool includeSymbols, bool autoImportsAvailable, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (triggerCharacter is not null && !await CompletionQuery.ValidTriggerAsync(projection.File, position, triggerCharacter.Value, cancellation)) return null;
        if (triggerCharacter == " "u8) return preferences.IncludeCompletionsForImportStatements == true ? new([], IsIncomplete: true) : null;
        if (await JSDocCompletion.CreateAsync(projection, position, capabilities, preferences, cancellation) is { } item) return new([item]);
        if (project is null) return null;
        using var request = new ProjectRequest(cancellation);
        using var lease = await project.Resource!.Checkers.AcquireAsync(includeSymbols ? ProjectCheckerLifetime.Api : ProjectCheckerLifetime.Query, request, projection.File).ConfigureAwait(false);
        int index = Array.IndexOf(projections, projection) - 1;
        var query = new CompletionQuery(lease.Checker, projection, position, capabilities, preferences, project.Program!, index >= 0 ? index : null,
            file => ProjectionForFile(project.Program!, file), cancellation, project.AutoImports, includeSymbols, autoImportsAvailable);
        return await query.GetAsync();
    }

    public async ValueTask<CompletionItem> ResolveCompletionItemAsync(ProjectSnapshot project, CompletionItem item,
        CompletionCapabilities? capabilities = null, UserPreferences? preferences = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (project.Program is null) throw new ArgumentException("Project has no program", nameof(project));
        var data = item.Data ?? throw new ArgumentException("completion item data is nil", nameof(item));
        int index = data.SupplementalFileIndex is { } supplemental ? checked(supplemental + 1) : 0;
        if ((uint)index >= (uint)projections.Length || data.SupplementalFileIndex < 0)
            throw new ArgumentException($"supplemental source file index not found: {data.SupplementalFileIndex}", nameof(item));
        var projection = projections[index];
        if (projection.OriginalFileName != data.FileName) throw new ArgumentException($"file not found: {data.FileName}", nameof(item));
        if ((uint)data.Position > (uint)projection.File.Source.Length) return item;
        using var request = new ProjectRequest(cancellation);
        using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request, projection.File).ConfigureAwait(false);
        var query = new CompletionQuery(lease.Checker, projection, data.Position, capabilities ?? new(), preferences ?? new(),
            project.Program, data.SupplementalFileIndex, file => ProjectionForFile(project.Program, file), cancellation, project.AutoImports);
        return await query.ResolveAsync(item);
    }
}

using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Projects;

namespace TypeScript.Compiler.LanguageServices;

public sealed record DiagnosticClientOptions(bool RelatedInformation = false, IReadOnlyList<int>? Tags = null, bool VisualStudio = false,
    Utf8String? Locale = null);
public sealed record DocumentDiagnosticRelatedInformation(DocumentLocation Location, Utf8String Message);
public sealed record DocumentDiagnostic(DocumentRange Range, int Code, int Severity, Utf8String Message, Utf8String Source,
    IReadOnlyList<DocumentDiagnosticRelatedInformation> RelatedInformation, IReadOnlyList<int> Tags);

public sealed partial class LanguageServiceDocument
{
    public async ValueTask<IReadOnlyList<DocumentDiagnostic>> GetDiagnosticsAsync(ProjectSnapshot project, UserPreferences? preferences = null,
        DiagnosticClientOptions? options = null, CancellationToken cancellation = default)
    {
        using var request = new ProjectRequest(cancellation);
        return await GetDiagnosticsCoreAsync(project, preferences ?? new(), options ?? new(), request, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<(IReadOnlyList<DocumentDiagnostic> Before, IReadOnlyList<DocumentDiagnostic> After)> GetDiagnosticsAroundEmitAsync(
        ProjectSnapshot project, UserPreferences preferences, DiagnosticClientOptions options, CancellationToken cancellation)
    {
        using var request = new ProjectRequest(cancellation);
        using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Diagnostics, request).ConfigureAwait(false);
        var before = await GetDiagnosticsCoreAsync(project, preferences, options, request, cancellation).ConfigureAwait(false);
        await project.Program!.EmitWithCheckerAsync(new() { WriteFile = static (_, _, _, _) => ValueTask.CompletedTask }, lease.Checker, cancellation).ConfigureAwait(false);
        var after = await GetDiagnosticsCoreAsync(project, preferences, options, request, cancellation).ConfigureAwait(false);
        return (before, after);
    }

    private async ValueTask<IReadOnlyList<DocumentDiagnostic>> GetDiagnosticsCoreAsync(ProjectSnapshot project, UserPreferences preferences,
        DiagnosticClientOptions options, ProjectRequest request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (preferences.EnableValidation == false) return [];
        var program = project.Program ?? throw new ArgumentException("Project has no program", nameof(project));
        List<Diagnostic> diagnostics = [];
        foreach (var projection in projections)
        {
            var source = projection.File;
            diagnostics.AddRange(program.SyntacticDiagnostics(source, cancellation));
            using (var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Diagnostics, request, source).ConfigureAwait(false))
            {
                var checker = lease.Checker;
                if (!checker.SkipProgramFile(source)) await checker.CheckSourceFileAsync(source, cancellation);
                var grouped = checker.GroupDiagnosticsByFile();
                diagnostics.AddRange(checker.DetailedDiagnosticsForProgramFile(source, grouped[source]));
                diagnostics.AddRange(checker.SuggestionsForFile(source));
            }
            if (program.Configuration.Options.Declaration == true || program.Configuration.Options.Composite == true)
                diagnostics.AddRange(await program.GetDeclarationDiagnosticsAsync(source, cancellation));
        }
        List<DocumentDiagnostic> result = [];
        Dictionary<DocumentProjection, List<Diagnostic>> synthesized = [];
        foreach (var diagnostic in diagnostics)
        {
            cancellation.ThrowIfCancellationRequested();
            var projection = FindProjection(diagnostic.FileName);
            if (!diagnostic.IsMapperFailure && diagnostic.Source is not { IsEmpty: false } && projection?.Map is { } map
                && map.VirtualToOriginalSpan(diagnostic.Start, diagnostic.Start + diagnostic.Length).Fidelity == MappingFidelity.None)
            {
                // Unnecessary spans in generated code are filtered by the program diagnostic contract.
                if (diagnostic.Message.ReportsUnnecessary) continue;
                if (!synthesized.TryGetValue(projection, out var entries)) synthesized.Add(projection, entries = []);
                entries.Add(diagnostic); continue;
            }
            result.Add(DiagnosticPresentation.Convert(diagnostic, FindProjection, options, preferences.ReportStyleChecksAsWarnings == true));
        }
        foreach (var (projection, entries) in synthesized)
        {
            var category = entries[0].Message.Category;
            foreach (var diagnostic in entries)
            {
                if (diagnostic.Message.Category == DiagnosticCategory.Error) { category = DiagnosticCategory.Error; break; }
                if (diagnostic.Message.Category == DiagnosticCategory.Warning) category = DiagnosticCategory.Warning;
            }
            var aggregate = new Diagnostic(Messages.Virtual_code_produced_by_the_content_mapper_0_has_problems_with_no_corresponding_location_in_this_file
                with { Category = category }, 0, 0, [projection.MapperIdentity])
            { FileName = projection.File.FileName, RelatedInformation = entries };
            result.Add(DiagnosticPresentation.Convert(aggregate, FindProjection, options, preferences.ReportStyleChecksAsWarnings == true));
        }
        return result;

        DocumentProjection? FindProjection(Utf8String? name) => name is { } fileName && program.GetFile(fileName) is { } file
            ? ProjectionForFile(program, file.Syntax) : null;
    }
}

internal static class DiagnosticPresentation
{
    internal static DocumentDiagnostic Convert(Diagnostic diagnostic, Func<Utf8String?, DocumentProjection?> project,
        DiagnosticClientOptions options, bool reportStyleChecksAsWarnings = false)
    {
        int severity = diagnostic.Message.Category switch
        { DiagnosticCategory.Warning => 2, DiagnosticCategory.Suggestion => 4, DiagnosticCategory.Message => 3, _ => 1 };
        int code = (int)diagnostic.Code;
        if (reportStyleChecksAsWarnings && severity == 1 && code is 6196 or 6133 or 6138 or 6192 or 7027 or 7028 or 7029 or 7030) severity = 2;
        List<DocumentDiagnosticRelatedInformation> related = [];
        if (options.RelatedInformation)
            foreach (var item in diagnostic.RelatedInformation)
                related.Add(new(new(DocumentUris.FromFileName(project(item.FileName)?.OriginalFileName ?? item.FileName ?? default), Range(item)),
                    Localize(item)));
        List<int> tags = [];
        if (diagnostic.Message.ReportsUnnecessary && options.Tags?.Contains(1) == true) tags.Add(1);
        if (diagnostic.Message.ReportsDeprecated && options.Tags?.Contains(2) == true) tags.Add(2);
        return new(Range(diagnostic), code, severity, Message(diagnostic), diagnostic.Source is { IsEmpty: false } source ? source : "ts"u8, related, tags);

        Utf8String Localize(Diagnostic item)
        {
            var arguments = item.Arguments;
            var projection = project(item.FileName);
            if (item.Source is not { IsEmpty: false } && projection?.Map?.AliasForVirtualSpan(item.Start, item.Start + item.Length) is { } alias)
            {
                var virtualName = projection.File.Source.Text[alias.VirtualStart..alias.VirtualEnd];
                var originalName = projection.OriginalText[alias.OriginalStart..alias.OriginalEnd];
                arguments = arguments.Select(argument => argument == virtualName ? originalName : argument).ToArray();
            }
            return item.Message.Format(options.Locale, arguments);
        }

        Utf8String Message(Diagnostic item)
        {
            var text = new Utf8StringBuilder(Localize(item));
            Stack<(Diagnostic Diagnostic, int Depth)> pending = new();
            for (int i = item.MessageChain.Count - 1; i >= 0; i--) pending.Push((item.MessageChain[i], 1));
            while (pending.TryPop(out var entry))
            {
                text.Append((byte)'\n').Append((byte)' ', entry.Depth * 2).Append(Localize(entry.Diagnostic));
                for (int i = entry.Diagnostic.MessageChain.Count - 1; i >= 0; i--) pending.Push((entry.Diagnostic.MessageChain[i], entry.Depth + 1));
            }
            return text.ToUtf8String();
        }

        DocumentRange Range(Diagnostic item)
        {
            var projection = project(item.FileName);
            if (projection is null) return default;
            if (item.Source is { IsEmpty: false }) return projection.ToOriginalRange(item.Start, item.Start + item.Length);
            var mapped = projection.ToRange(item.Start, item.Start + item.Length);
            return mapped.Fidelity == MappingFidelity.None ? default : mapped.Range;
        }
    }
}

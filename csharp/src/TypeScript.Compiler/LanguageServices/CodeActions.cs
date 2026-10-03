using System.Text.Json;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Projects;

namespace TypeScript.Compiler.LanguageServices;

public sealed record CodeActionDiagnostic(DocumentRange Range, int? Code, Utf8String? Source, Utf8String Message, JsonElement Data);
public sealed record CodeActionContext(IReadOnlyList<CodeActionDiagnostic>? Diagnostics = null, IReadOnlyList<Utf8String>? Only = null);
public sealed record CodeAction(Utf8String Title, Utf8String Kind, IReadOnlyDictionary<Utf8String, DocumentTextEdit[]> Changes,
    CodeActionDiagnostic? Diagnostic = null);

public sealed partial class LanguageServiceDocument
{
    public async ValueTask<IReadOnlyList<CodeAction>?> GetCodeActionsAsync(ProjectSnapshot project, CodeActionContext? context,
        UserPreferences? preferences = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var program = project.Program ?? throw new ArgumentException("Project has no program", nameof(project));
        preferences ??= new();
        preferences = preferences with { FormatCodeSettings = preferences.FormatCodeSettings with {
            NewLineCharacter = program.Configuration.Options.NewLine == Configuration.NewLineKind.CRLF ? "\r\n"u8 : "\n"u8 } };
        List<CodeAction> actions = [];
        List<CodeFix> seen = [];
        HashSet<CodeFixProvider> matched = [];
        var uri = DocumentUris.FromFileName(projections[0].OriginalFileName);
        using var request = new ProjectRequest(cancellation);
        if (context?.Only is { } only)
            foreach (var kind in only)
            {
                foreach (var (candidate, title) in new (Utf8String, DiagnosticMessage)[] {
                    ((Utf8String)"source.organizeImports.ts"u8, Messages.Organize_Imports),
                    ((Utf8String)"source.removeUnusedImports.ts"u8, Messages.Remove_Unused_Imports),
                    ((Utf8String)"source.sortImports.ts"u8, Messages.Sort_Imports) })
                {
                    if (!Contains(kind, candidate)) continue;
                    var projection = projections[0];
                    using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request, projection.File);
                    var changes = await new ImportOrganizer(projection, lease.Checker, program.Configuration.Options, preferences, cancellation).OrganizeAsync(candidate);
                    actions.Add(new(title.Format(preferences.Locale), candidate, changes.Length == 0 ? new Dictionary<Utf8String, DocumentTextEdit[]>()
                        : new Dictionary<Utf8String, DocumentTextEdit[]> { [uri] = changes }));
                }
                if (Contains(kind, "source.fixAll.ts"u8))
                {
                    List<DocumentTextEdit> changes = [];
                    foreach (var provider in CodeFixQuery.Providers)
                        if (await AllAsync(provider) is { } fix) changes.AddRange(fix.Changes);
                    if (changes.Count != 0) actions.Add(new(Messages.Fix_All.Format(preferences.Locale), "source.fixAll.ts"u8,
                        new Dictionary<Utf8String, DocumentTextEdit[]> { [uri] = [.. changes] }));
                }
            }
        if (context?.Diagnostics is { } diagnostics && (context.Only is not { Count: > 0 } || context.Only.Any(kind => Contains(kind, "quickfix"u8))))
        {
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Code is not { } code || diagnostic.Source is { } source && source != "ts"u8) continue;
                foreach (var provider in CodeFixQuery.Providers)
                {
                    if (!provider.Codes.Contains(code)) continue;
                    foreach (var projection in projections)
                        foreach (var span in projection.FromRange(diagnostic.Range, MappingFeature.CodeActions))
                        {
                            using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request, projection.File);
                            var query = new CodeFixQuery(program, lease.Checker, projection, preferences, project.AutoImports, cancellation);
                            foreach (var fix in await provider.Single(query, code, span.Start, span.End, diagnostic.Message))
                            {
                                if (seen.Any(previous => previous.Description == fix.Description && previous.Changes.SequenceEqual(fix.Changes))) continue;
                                seen.Add(fix);
                                actions.Add(new(fix.Description, "quickfix"u8, new Dictionary<Utf8String, DocumentTextEdit[]> { [uri] = fix.Changes }, diagnostic));
                                matched.Add(provider);
                            }
                        }
                }
            }
            foreach (var provider in matched)
            {
                var projection = projections[0];
                using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request, projection.File);
                var query = new CodeFixQuery(program, lease.Checker, projection, preferences, project.AutoImports, cancellation);
                if ((await query.DiagnosticsAsync()).Count(diagnostic => CodeFixQuery.Matches(diagnostic, provider.Codes)) < 2) continue;
                if (await provider.All(query) is { Changes.Length: > 0 } fix)
                    actions.Add(new(fix.Description, "quickfix"u8, new Dictionary<Utf8String, DocumentTextEdit[]> { [uri] = fix.Changes }));
            }
        }
        return actions;

        async ValueTask<CodeFix?> AllAsync(CodeFixProvider provider)
        {
            var projection = projections[0];
            using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request, projection.File);
            return await provider.All(new(program, lease.Checker, projection, preferences, project.AutoImports, cancellation));
        }
    }

    private static bool Contains(Utf8String kind, Utf8String candidate) => kind.IsEmpty || kind == candidate || candidate.StartsWith(kind + "."u8);
}

internal sealed record CodeFix(Utf8String Description, DocumentTextEdit[] Changes);
internal sealed record CodeFixProvider(int[] Codes,
    Func<CodeFixQuery, int, int, int, Utf8String, ValueTask<IReadOnlyList<CodeFix>>> Single,
    Func<CodeFixQuery, ValueTask<CodeFix?>> All);

internal sealed partial class CodeFixQuery(CompilerProgram program, Checker checker, DocumentProjection projection,
    UserPreferences preferences, AutoImportCache? cache, CancellationToken cancellation)
{
    internal static readonly CodeFixProvider[] Providers;
    static CodeFixQuery() => Providers = [
        new(ImportErrorCodes, static (query, code, position, end, message) => query.ImportsAsync(code, position, message), static query => query.AllImportsAsync()),
        new(IsolatedErrorCodes, static (query, code, position, end, message) => query.IsolatedAsync(position, end), static query => query.AllIsolatedAsync()),
        new(InterfaceErrorCodes, static (query, code, position, end, message) => query.InterfacesAsync(position), static query => query.AllInterfacesAsync()),
    ];
    private Ast.SourceFileNode File => projection.File;
    private Configuration.CompilerOptions Options => program.Configuration.Options;
    private AutoImportView? importView;
    private AutoImportView Imports => importView ??= new(program, checker, projection, preferences, cancellation, cache: cache);
    private IReadOnlyList<Diagnostic>? semanticDiagnostics, allDiagnostics;

    internal async ValueTask<IReadOnlyList<Diagnostic>> SemanticDiagnosticsAsync()
    {
        if (semanticDiagnostics is not null) return semanticDiagnostics;
        if (!checker.SkipProgramFile(File)) await checker.CheckSourceFileAsync(File, cancellation);
        return semanticDiagnostics = checker.DetailedDiagnosticsForProgramFile(File, checker.GroupDiagnosticsByFile()[File]);
    }

    internal async ValueTask<IReadOnlyList<Diagnostic>> DiagnosticsAsync()
    {
        if (allDiagnostics is not null) return allDiagnostics;
        List<Diagnostic> result = [];
        foreach (var source in new[] { File }.Concat((program.GetFile(File.FileName)?.SupplementalSourceFiles ?? [])
            .Select(name => program.GetFile(name)!.Syntax)))
        {
            result.AddRange(program.SyntacticDiagnostics(source, cancellation));
            if (source == File) result.AddRange(await SemanticDiagnosticsAsync());
            else
            {
                if (!checker.SkipProgramFile(source)) await checker.CheckSourceFileAsync(source, cancellation);
                result.AddRange(checker.DetailedDiagnosticsForProgramFile(source, checker.GroupDiagnosticsByFile()[source]));
            }
            result.AddRange(checker.SuggestionsForFile(source));
            if (Options.Declaration == true || Options.Composite == true) result.AddRange(await program.GetDeclarationDiagnosticsAsync(source, cancellation));
        }
        return allDiagnostics = result;
    }

    internal static bool Matches(Diagnostic diagnostic, int[] codes) => diagnostic.Source is not { IsEmpty: false } && codes.Contains((int)diagnostic.Code);
}

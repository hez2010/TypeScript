using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compiler.Api;

public sealed partial class ApiSession
{
    private static readonly HashSet<Utf8String> diagnosticMethods =
    [
        "getSyntacticDiagnostics"u8, "getBindDiagnostics"u8, "getSemanticDiagnostics"u8, "getSuggestionDiagnostics"u8,
        "getDeclarationDiagnostics"u8, "getConfigFileParsingDiagnostics"u8, "getProgramDiagnostics"u8, "getGlobalDiagnostics"u8,
    ];

    private async ValueTask<RpcResponse?> DiagnosticRequestAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation)
    {
        if (!diagnosticMethods.Contains(method)) return null;
        await using var pin = Pin(ApiJson.UInt64(parameters, "snapshot"u8));
        var data = pin.Data;
        var projectId = ApiJson.String(parameters, "project"u8);
        var program = data.Program(projectId);
        var pool = data.Project(projectId).Resource!.Checkers;
        IReadOnlyList<Diagnostic> diagnostics;
        if (method == "getConfigFileParsingDiagnostics"u8) diagnostics = program.Configuration.Diagnostics;
        else if (method == "getProgramDiagnostics"u8) diagnostics = program.ProgramDiagnostics;
        else if (method == "getGlobalDiagnostics"u8)
        {
            using (var request = new ProjectRequest(cancellation))
            using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Diagnostics, request))
                await lease.Checker.CheckProgramAsync(cancellation);
            diagnostics = DiagnosticCollection.SortAndDeduplicate(program.Diagnostics.Concat(pool.GlobalDiagnostics)
                .Where(diagnostic => diagnostic.FileName is null));
        }
        else
        {
            var files = ApiJson.Get(parameters, "files"u8);
            List<Diagnostic> collected = [];
            if (files.ValueKind == JsonValueKind.Array)
                foreach (var file in files.EnumerateArray())
                {
                    var source = program.GetFile(Document(file, program))?.Syntax ?? throw new ApiException(
                        $"source file not found: {(file.ValueKind == JsonValueKind.Object ? ApiJson.String(file, "uri"u8) : ApiJson.String(file))}");
                    collected.AddRange(await CollectAsync([source]));
                }
            else collected.AddRange(await CollectAsync(program.SourceFiles.Select(file => file.Syntax).ToArray()));
            diagnostics = collected;
        }
        return RpcResponse.Json(writer =>
        {
            writer.WriteStartArray();
            foreach (var diagnostic in diagnostics) WriteDiagnostic(writer, diagnostic, name =>
                program.GetFile(name)?.Syntax.Source ?? program.ProjectReferences.Projects.Values
                    .FirstOrDefault(config => config.SourceFile?.FileName == name)?.SourceFile?.Source
                ?? data.Snapshot.FileSystem.GetDocument(name)?.Source, program);
            writer.WriteEndArray();
        });

        async ValueTask<IReadOnlyList<Diagnostic>> CollectAsync(IReadOnlyList<SourceFileNode> sources)
        {
            if (sources.Count == 0) return [];
            List<Diagnostic> result = [];
            if (method == "getSyntacticDiagnostics"u8)
                foreach (var source in sources) result.AddRange(program.SyntacticDiagnostics(source, cancellation));
            else if (method == "getBindDiagnostics"u8)
                foreach (var source in sources) result.AddRange(program.GetFile(source.FileName)!.Binding.Diagnostics
                    .Select(diagnostic => diagnostic with { FileName = source.FileName }));
            else if (method == "getDeclarationDiagnostics"u8)
                foreach (var source in sources) result.AddRange(await program.GetDeclarationDiagnosticsAsync(source, cancellation));
            else
            {
                using var request = new ProjectRequest(cancellation);
                using var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Diagnostics, request);
                var checker = lease.Checker;
                foreach (var source in sources)
                    if (!checker.SkipProgramFile(source)) await checker.CheckSourceFileAsync(source, cancellation);
                var grouped = checker.GroupDiagnosticsByFile();
                foreach (var source in sources) result.AddRange(method == "getSuggestionDiagnostics"u8
                    ? checker.SuggestionsForFile(source) : checker.DetailedDiagnosticsForProgramFile(source, grouped[source]));
            }
            return DiagnosticCollection.SortAndDeduplicate(result.Where(diagnostic => !diagnostic.Message.ReportsUnnecessary
                || diagnostic.Source is not null || diagnostic.FileName is not { } name || program.GetFile(name)?.Mapping is not { } mapping
                || mapping.Map.VirtualToOriginalSpan(diagnostic.Start, diagnostic.Start + diagnostic.Length).Fidelity != MappingFidelity.None));
        }
    }
}

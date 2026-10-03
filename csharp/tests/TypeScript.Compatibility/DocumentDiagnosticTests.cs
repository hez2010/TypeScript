using System.Runtime.CompilerServices;
using System.Text.Json;
using TypeScript.Compiler;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Syntax;
using Presentation = TypeScript.Compiler.LanguageServices.DiagnosticPresentation;

namespace TypeScript.Compatibility;

internal static partial class DocumentDiagnosticTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        const int depth = 2048;
        var text = Utf8String.FromString("export {}; function 方法(値:number) {" + new string('{', depth)
            + "const 名:number='😀';" + new string('}', depth) + "}");
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = text.Span.ToArray() }, true);
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8); options.SetRaw("noUnusedLocals"u8, "true"u8);
        await using var session = new ProjectSession(fs, new() { MaxCheckers = 2 });
        session.SetInferredOptions(options); session.Notify(new(FileChangeKind.Open, "/a.ts"u8, 1, text));
        int refreshes = 0; session.DiagnosticsRefreshRequested += () => refreshes++;
        session.Configure(new() { ReportStyleChecksAsWarnings = false }); session.Configure(new() { ReportStyleChecksAsWarnings = false });
        Check(refreshes == 1, "Only changed diagnostic preferences request refresh");
        await using var snapshot = await session.GetSnapshotAsync(["/a.ts"u8]);
        session.Configure(new() { ReportStyleChecksAsWarnings = false });
        Check(refreshes == 1, "Committed diagnostic preferences suppress redundant refreshes");
        var project = snapshot.Snapshot.GetDefaultProject("/a.ts"u8)!;
        var file = project.Program!.GetFile("/a.ts"u8)!;
        var original = file.Syntax.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        foreach (var encoding in new[] { PositionEncoding.Utf8, PositionEncoding.Utf16 })
        {
            var service = new LanguageServiceDocument(project.Program, file, encoding);
            Check(await service.GetDiagnosticsAsync(project, new() { EnableValidation = false }, cancellation: deadline.Token) is [], "Disabled validation returns an empty array");
            var diagnostics = await service.GetDiagnosticsAsync(project, options: new(Tags: [1, 2]), cancellation: deadline.Token);
            Check(diagnostics.Any(diagnostic => diagnostic.Code == 2322) && diagnostics.All(diagnostic => diagnostic.Code != 2318), "Deep file checking excludes program global diagnostics");
            var assignment = diagnostics.Single(diagnostic => diagnostic.Code == 2322);
            Check(assignment.Range.End.Character - assignment.Range.Start.Character == (encoding == PositionEncoding.Utf8 ? 3 : 1), "Diagnostic names use the negotiated encoding");
            Check(diagnostics.Any(diagnostic => diagnostic.Code == 6133 && diagnostic.Severity == 2 && diagnostic.Tags.SequenceEqual([1])), "Style errors become tagged warnings");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.GetDiagnosticsAsync(project, options: new(Tags: [1, 2]), cancellation: deadline.Token).AsTask()));
            Check(concurrent.All(items => LspJson.DocumentDiagnostics(items).Data.Span.SequenceEqual(LspJson.DocumentDiagnostics(diagnostics).Data.Span)), "Concurrent diagnostic requests remain deterministic");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await service.GetDiagnosticsAsync(project, cancellation: canceled.Token); Check(false, "Canceled diagnostics must throw"); }
            catch (OperationCanceledException) { checks++; }
            using var heldRequest = new ProjectRequest(deadline.Token);
            using var held = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Diagnostics, heldRequest, file.Syntax);
            using var queuedCancellation = new CancellationTokenSource();
            var queued = service.GetDiagnosticsAsync(project, cancellation: queuedCancellation.Token).AsTask();
            Check(!queued.IsCompleted, "The held diagnostic checker queues the next request");
            queuedCancellation.Cancel();
            try { await queued.WaitAsync(deadline.Token); Check(false, "Queued diagnostic cancellation must throw"); }
            catch (OperationCanceledException) { checks++; }
        }
        Check(original.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags), "Diagnostics leave shared syntax immutable");
        using var config = JsonDocument.Parse("{\"unstable\":{\"validateEnabled\":false,\"reportStyleChecksAsWarnings\":false},\"validate\":{\"enabled\":true},\"reportStyleChecksAsWarnings\":true}");
        var preferences = new UserPreferences().WithConfig(config.RootElement);
        Check(preferences.EnableValidation == true && preferences.ReportStyleChecksAsWarnings == true, "Stable diagnostic settings override raw settings");
        var mappedFile = await Parser.ParseSourceFileAsync(new("/mapped.ts"u8), new("virtual"u8));
        var projection = new DocumentProjection(mappedFile, new(mappedFile, new("日本語"u8),
            new([new(0, 7, 0, 9, MappingKind.Alias, MappingFeature.None)]), "/mapped.ts"u8, "mapper"u8, "test"u8, []), PositionEncoding.Utf16);
        Diagnostic mapped = new(Messages.Cannot_find_name_0, 0, 7, ["virtual"u8]) { FileName = mappedFile.FileName };
        var converted = Presentation.Convert(mapped with { RelatedInformation = [mapped], MessageChain = [mapped] }, _ => projection, new(true));
        Check(converted.Message == "Cannot find name '日本語'.\n  Cannot find name '日本語'."u8 && converted.Range == new DocumentRange(new(0, 0), new(0, 3)), "Alias substitution and mapping apply to message chains independently of service feature flags");
        Check(converted.RelatedInformation.Single().Message == "Cannot find name '日本語'."u8, "Related diagnostic aliases use original names");
        var external = Presentation.Convert(mapped with { Source = "external"u8, Start = 3, Length = 3 }, _ => projection, new());
        Check(external.Range == new DocumentRange(new(0, 1), new(0, 2)) && external.Message == "Cannot find name 'virtual'."u8, "Mapper diagnostics already use original coordinates and messages");
        var localized = Presentation.Convert(mapped, _ => projection, new(Locale: "ja"u8));
        Check(localized.Message == Messages.Cannot_find_name_0.Format("ja"u8, "日本語"u8) && localized.Message != converted.RelatedInformation[0].Message, "Locale selection applies after alias substitution");
        var (released, retained) = await ReleasedAsync();
        for (int i = 0; i < 3 && released.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!released.IsAlive, "Diagnostic results do not retain a disposed program"); GC.KeepAlive(retained);
        return checks;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference, IReadOnlyList<DocumentDiagnostic>)> ReleasedAsync()
    {
        Utf8String text = "const x:number='text';"u8;
        await using var session = new ProjectSession(new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = text.Span.ToArray() }, true));
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8); session.SetInferredOptions(options);
        session.Notify(new(FileChangeKind.Open, "/a.ts"u8, 1, text));
        await using var snapshot = await session.GetSnapshotAsync(["/a.ts"u8]);
        var project = snapshot.Snapshot.GetDefaultProject("/a.ts"u8)!;
        var service = new LanguageServiceDocument(project.Program!, project.Program!.GetFile("/a.ts"u8)!);
        var diagnostics = await service.GetDiagnosticsAsync(project);
        if (diagnostics.Count != 1) throw new InvalidOperationException("Retention fixture must produce a diagnostic");
        return (new(project.Program), diagnostics);
    }
}

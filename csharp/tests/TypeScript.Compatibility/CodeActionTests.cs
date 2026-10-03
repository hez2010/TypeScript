using System.Runtime.CompilerServices;
using System.Text.Json;
using TypeScript.Compiler;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compatibility;

internal static partial class CodeActionTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Utf8String text = "const 字='😀';\r\ndeclare function make():number;\r\nexport let 値=make();\r\nexport const other=make();\r\nimport {used,unused} from './dep';const shorthand={used};"u8;
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        { ["/a.ts"u8] = text.Span.ToArray(), ["/dep.ts"u8] = "export const used=1;export const unused=2;"u8.ToArray() }, true);
        await using var session = new ProjectSession(fs, new() { MaxCheckers = 1 });
        session.SetInferredOptions(Options()); session.Notify(new(FileChangeKind.Open, "/a.ts"u8, 1, text));
        await using var snapshot = await session.GetSnapshotAsync(["/a.ts"u8], deadline.Token);
        var project = snapshot.Snapshot.GetDefaultProject("/a.ts"u8)!;
        var file = project.Program!.GetFile("/a.ts"u8)!;
        var original = file.Syntax.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        foreach (var encoding in new[] { PositionEncoding.Utf8, PositionEncoding.Utf16 })
        {
            var service = new LanguageServiceDocument(project.Program, file, encoding);
            var diagnostics = await ContextAsync(service, project, deadline.Token);
            var actions = await service.GetCodeActionsAsync(project, diagnostics, cancellation: deadline.Token);
            Check(actions is { Count: > 0 } && actions.Any(action => action.Diagnostic is null), "Two missing annotations produce a fix-all action");
            var annotation = actions!.First(action => action.Title == "Add annotation of type 'number'"u8);
            var edit = annotation.Changes["file:///a.ts"u8].Single();
            Check(edit.Range.Start == new DocumentPosition(2, encoding == PositionEncoding.Utf8 ? 14u : 12u) && edit.NewText == ": number"u8,
                "Annotation insertions retain UTF-8 source offsets and negotiated editor coordinates");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.GetCodeActionsAsync(project, diagnostics, cancellation: deadline.Token).AsTask()));
            var expected = LspJson.CodeActions(actions).Data.ToArray();
            Check(concurrent.All(result => LspJson.CodeActions(result).Data.Span.SequenceEqual(expected)), "Concurrent annotation requests return identical diagnostics and edits through one checker");
            Check((await service.GetCodeActionsAsync(project, diagnostics with { Only = ["refactor"u8] }, cancellation: deadline.Token)) is [], "Unregistered action kinds return no fixes");
            Check((await service.GetCodeActionsAsync(project, new(), cancellation: deadline.Token)) is [], "Missing client diagnostics do not synthesize quick fixes");
            Check((await service.GetCodeActionsAsync(project, diagnostics with { Diagnostics = diagnostics.Diagnostics!.Select(diagnostic => diagnostic with { Source = "custom"u8 }).ToArray() }, cancellation: deadline.Token)) is [],
                "External diagnostic codes cannot select TypeScript fixes");
            var duplicate = await service.GetCodeActionsAsync(project, diagnostics with { Diagnostics = [.. diagnostics.Diagnostics!, .. diagnostics.Diagnostics!] }, cancellation: deadline.Token);
            Check(LspJson.CodeActions(duplicate).Data.Span.SequenceEqual(expected), "Repeated client diagnostics preserve action deduplication and order");
            var all = await service.GetCodeActionsAsync(project, new(Only: ["source.fixAll.ts"u8]), cancellation: deadline.Token);
            Check(all is [var fix] && fix.Changes["file:///a.ts"u8].Length == 2, "Source fix-all computes current declaration diagnostics without client diagnostics");
            var sorted = await service.GetCodeActionsAsync(project, new(Only: ["source.organizeImports"u8]), cancellation: deadline.Token);
            Check(sorted is [var organized] && organized.Kind == "source.organizeImports.ts"u8 && organized.Changes["file:///a.ts"u8]
                .Any(edit => edit.NewText.Contains("used"u8) && !edit.NewText.Contains("unused"u8)), "Organize imports keeps shorthand references and removes unused imports");
            var sourceActions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.GetCodeActionsAsync(project, new(Only: ["source"u8]), cancellation: deadline.Token).AsTask()));
            Check(sourceActions.All(result => result is { Count: 4 }), "Concurrent parent source actions complete import-use queries without reacquiring the checker gate");

            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await service.GetCodeActionsAsync(project, diagnostics, cancellation: canceled.Token); Check(false, "Canceled actions must throw"); }
            catch (OperationCanceledException) { checks++; }
            using (var heldRequest = new ProjectRequest(deadline.Token))
            using (var held = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, heldRequest, file.Syntax))
            using (var queuedCancellation = new CancellationTokenSource())
            {
                var queued = service.GetCodeActionsAsync(project, diagnostics, cancellation: queuedCancellation.Token).AsTask();
                Check(!queued.IsCompleted, "Holding the only checker queues action requests");
                queuedCancellation.Cancel();
                try { await queued.WaitAsync(deadline.Token); Check(false, "Queued action cancellation must throw"); }
                catch (OperationCanceledException) { checks++; }
            }
            Check(LspJson.CodeActions(await service.GetCodeActionsAsync(project, diagnostics, cancellation: deadline.Token)).Data.Span.SequenceEqual(expected),
                "Canceling a queued action releases its request without stranding the checker");

            Utf8String prefix = "<template>😀</template>\n"u8;
            foreach (var kind in new[] { MappingKind.Verbatim, MappingKind.Atom, MappingKind.Alias })
            {
                var mapping = new MappedSourceFile(file.Syntax, new(prefix + text), new([new(0, text.Length, prefix.Length, prefix.Length + text.Length, kind)]), file.Syntax.FileName, "test"u8, "test"u8, []);
                var projection = new DocumentProjection(file.Syntax, mapping, encoding);
                var mapped = new LanguageServiceDocument([projection]);
                var at = text.IndexOf("値=make"u8);
                var diagnostic = diagnostics.Diagnostics!.First(diagnostic => diagnostic.Code == 9010) with { Range = projection.ToOriginalRange(prefix.Length + at, prefix.Length + at + "値"u8.Length) };
                var result = await mapped.GetCodeActionsAsync(project, new([diagnostic]), cancellation: deadline.Token);
                Check((result is { Count: > 0 }) == (kind == MappingKind.Verbatim), "Annotation changes require exact mapping of the complete edit");
                if (kind == MappingKind.Verbatim)
                    Check(result![0].Changes["file:///a.ts"u8][0].Range.Start.Line == 3, "Mapped annotation edits retain the original component line");
            }
            var disabled = new MappedSourceFile(file.Syntax, new(text), new([new(0, text.Length, 0, text.Length, MappingKind.Verbatim, MappingFeature.All & ~MappingFeature.CodeActions)]), file.Syntax.FileName, "test"u8, "test"u8, []);
            Check((await new LanguageServiceDocument([new(file.Syntax, disabled, encoding)]).GetCodeActionsAsync(project, diagnostics, cancellation: deadline.Token)) is [], "Disabled code-action mappings suppress quick fixes");
        }
        Check(original.Select(item => item.Node).SequenceEqual(file.Syntax.DescendantsAndSelf())
            && original.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags),
            "Annotation generation and printing preserve the shared source tree");
        var (released, retained) = await ReleasedAsync();
        for (int i = 0; i < 4 && released.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!released.IsAlive, "Returned code actions do not retain the disposed program"); GC.KeepAlive(retained);
        return checks + await DeepSafetyAsync();
    }

    private static CompilerOptions Options()
    {
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8); options.SetRaw("declaration"u8, "true"u8); options.SetRaw("isolatedDeclarations"u8, "true"u8);
        return options;
    }

    private static async Task<int> DeepSafetyAsync()
    {
        const int depth = 2048;
        var sources = new[] {
            Utf8String.FromString("declare const condition:boolean,value:number;export const result=" + string.Concat(Enumerable.Repeat("condition?value:", depth)) + "value;"),
            Utf8String.FromString("declare const source:any;export const " + string.Concat(Enumerable.Repeat("{x:", depth)) + "leaf" + new string('}', depth) + "=source;"),
        };
        int checks = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        foreach (var source in sources)
        {
            var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = source.Span.ToArray() }, true);
            await using var session = new ProjectSession(fs);
            session.SetInferredOptions(Options()); session.Notify(new(FileChangeKind.Open, "/a.ts"u8, 1, source));
            await using var snapshot = await session.GetSnapshotAsync(["/a.ts"u8], deadline.Token);
            var project = snapshot.Snapshot.GetDefaultProject("/a.ts"u8)!;
            var file = project.Program!.GetFile("/a.ts"u8)!.Syntax;
            using var request = new ProjectRequest(deadline.Token);
            using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request, file);
            var fixer = new IsolatedDeclarationFixer(new(file, null, PositionEncoding.Utf8), lease.Checker, new(), AnnotationMode.Relative, deadline.Token);
            var description = await fixer.AnnotateAsync(source.IndexOf("export const "u8) + "export const "u8.Length);
            var changes = await fixer.ChangesAsync();
            if (description.IsEmpty || changes.Length == 0) throw new InvalidOperationException("Deep conditional and destructuring annotations must complete without stack exhaustion");
            checks++;
        }
        return checks;
    }

    private static async Task<CodeActionContext> ContextAsync(LanguageServiceDocument service, ProjectSnapshot project, CancellationToken cancellation = default)
    {
        var diagnostics = await service.GetDiagnosticsAsync(project, cancellation: cancellation);
        using var serialized = JsonDocument.Parse(LspJson.DocumentDiagnostics(diagnostics).Data);
        var items = serialized.RootElement.GetProperty("items");
        return new(items.EnumerateArray().Select(diagnostic => new CodeActionDiagnostic(LspJson.Range(diagnostic.GetProperty("range")),
            diagnostic.GetProperty("code").GetInt32(), "ts"u8, JsonStrings.GetString(diagnostic.GetProperty("message")), diagnostic.Clone())).ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference, IReadOnlyList<CodeAction>)> ReleasedAsync()
    {
        Utf8String text = "declare function make():number;export let value=make();"u8;
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = text.Span.ToArray() }, true);
        await using var session = new ProjectSession(fs);
        session.SetInferredOptions(Options()); session.Notify(new(FileChangeKind.Open, "/a.ts"u8, 1, text));
        await using var snapshot = await session.GetSnapshotAsync(["/a.ts"u8]);
        var project = snapshot.Snapshot.GetDefaultProject("/a.ts"u8)!;
        var service = new LanguageServiceDocument(project.Program!, project.Program!.GetFile("/a.ts"u8)!);
        var actions = await service.GetCodeActionsAsync(project, await ContextAsync(service, project));
        return (new(project.Program), actions is { Count: > 0 } ? actions : throw new InvalidOperationException("Retention fixture must produce actions"));
    }
}

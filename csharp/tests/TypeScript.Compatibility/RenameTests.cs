using System.Runtime.CompilerServices;
using TypeScript.Compiler;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;

namespace TypeScript.Compatibility;

internal static class RenameTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        const int depth = 2048;
        var text = Utf8String.FromString("export const 名前 = '😀';\r\n" + string.Concat(Enumerable.Repeat("function f(){", depth)) + "return 名前;" + new string('}', depth));
        Utf8String use = "import { 名前 } from './kkkk/x'; 名前;"u8;
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a/kkkk/x.ts"u8] = text.Span.ToArray(), ["/a/use.ts"u8] = use.Span.ToArray() }, false);
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8);
        await using var host = new ProjectSnapshotHost(fs);
        await using var snapshot = await host.CreateAsync(new() { CreatePrograms = [new(["/a/use.ts"u8], options)] });
        var project = snapshot.CreatedPrograms[0];
        var file = project.Program!.GetFile("/a/kkkk/x.ts"u8)!;
        var original = file.Syntax.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        var service = new LanguageServiceDocument(project.Program, file);
        var position = new DocumentLineMap(text).ToPosition(text.LastIndexOf("名前"u8), PositionEncoding.Utf8);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var importService = new LanguageServiceDocument(project.Program, project.Program.GetFile("/a/use.ts"u8)!);
        var importInfo = await importService.GetRenameInfoAsync(project, new DocumentLineMap(use).ToPosition(use.IndexOf("./kkkk/x"u8), PositionEncoding.Utf8),
            capabilities: new(true, true), cancellation: deadline.Token);
        Check(importInfo.CanRename && importInfo.FileToRename == "/a/kkkk/x.ts"u8, "Default preferences allow import-path renames when the client supports resource operations");
        var info = await service.GetRenameInfoAsync(project, position, cancellation: deadline.Token);
        Check(info.CanRename && info.DisplayName == "名前"u8 && info.TriggerSpan.End.Character - info.TriggerSpan.Start.Character == 6, "Deep rename preserves UTF-8 token bounds");
        var renamed = await service.GetRenameEditsAsync(project, position, "新しい名前"u8, cancellation: deadline.Token);
        Check(renamed?.Changes?.Values.Sum(edits => edits.Count) == 4 && renamed.Changes.Values.SelectMany(edits => edits).All(edit => edit.NewText == "新しい名前"u8), "Deep exported rename reaches consumers");
        var utf16 = new LanguageServiceDocument(project.Program, file, PositionEncoding.Utf16);
        Check((await utf16.GetRenameInfoAsync(project, new(0, 13), cancellation: deadline.Token)).TriggerSpan == new DocumentRange(new(0, 13), new(0, 15)), "UTF-16 prepare rename uses code-unit bounds");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.GetRenameEditsAsync(project, position, "新しい名前"u8, cancellation: deadline.Token).AsTask()));
        Check(concurrent.All(result => result?.Changes?.Values.Sum(edits => edits.Count) == 4), "Concurrent rename queries share no mutable output state");
        Check(original.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags), "Rename leaves the shared syntax tree immutable");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.GetRenameInfoAsync(project, position, cancellation: cancelled.Token); Check(false, "Canceled prepare rename must throw"); }
        catch (OperationCanceledException) { checks++; }
        try { await service.GetRenameEditsAsync(project, position, "x"u8, cancellation: cancelled.Token); Check(false, "Canceled rename must throw"); }
        catch (OperationCanceledException) { checks++; }
        try { await service.GetEditsForFileRenameAsync(project, "/a/kkkk"u8, "/a/new"u8, cancellation: cancelled.Token); Check(false, "Canceled file rename must throw"); }
        catch (OperationCanceledException) { checks++; }
        var edits = await service.GetEditsForFileRenameAsync(project, "/a/KKKK"u8, "/a/new"u8, cancellation: deadline.Token);
        Check(edits is [TextDocumentChange { Uri.Span: var uri, Edits: [var edit] }] && uri.SequenceEqual("file:///a/use.ts"u8) && edit.NewText == "./new/x"u8,
            "Case folding that shrinks UTF-8 directory names preserves the suffix and updates imports");
        Check((await service.GetEditsForFileRenameAsync(project, "/a/kk"u8, "/a/new"u8, cancellation: deadline.Token)).Count == 0, "Directory renames require a component boundary");
        var released = await ReleasedAsync();
        for (int i = 0; i < 3 && released.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!released.IsAlive, "Rename and file-rename requests release their project after session disposal");
        return checks;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ReleasedAsync()
    {
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = "export const a = 1; a;"u8.ToArray(), ["/b.ts"u8] = "import { a } from './a'; a;"u8.ToArray() }, true);
        await using var session = new ProjectSession(fs);
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8); session.SetInferredOptions(options);
        session.Notify(new(FileChangeKind.Open, "/b.ts"u8, 1, "import { a } from './a'; a;"u8));
        await using var snapshot = await session.GetSnapshotAsync(["/b.ts"u8]);
        await CrossProjectReferences.RenameAsync(session, snapshot.Snapshot, "/b.ts"u8, new(0, 24), "b"u8, new(true, true, true), PositionEncoding.Utf8, default);
        await CrossProjectReferences.RenameFilesAsync(session, [new("file:///a.ts"u8, "file:///c.ts"u8)], new(true, true, true), PositionEncoding.Utf8, false, default);
        return new(snapshot.Snapshot.GetDefaultProject("/b.ts"u8)!.Program);
    }
}

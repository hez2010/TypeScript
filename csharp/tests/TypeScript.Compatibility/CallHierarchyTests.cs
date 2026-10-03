using System.Runtime.CompilerServices;
using TypeScript.Compiler;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;

namespace TypeScript.Compatibility;

internal static class CallHierarchyTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        const int depth = 2048;
        var text = Utf8String.FromString("export function 葉() {}\r\nexport function 方法() {" + new string('{', depth) + "葉();" + new string('}', depth) + "}\r\n方法();");
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = text.Span.ToArray() }, true);
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8);
        await using var host = new ProjectSnapshotHost(fs);
        await using var snapshot = await host.CreateAsync(new() { CreatePrograms = [new(["/a.ts"u8], options)] });
        var project = snapshot.CreatedPrograms[0];
        var file = project.Program!.GetFile("/a.ts"u8)!;
        var original = file.Syntax.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        foreach (var encoding in new[] { PositionEncoding.Utf8, PositionEncoding.Utf16 })
        {
            var service = new LanguageServiceDocument(project.Program, file, encoding);
            var lines = new DocumentLineMap(text);
            var leaf = lines.ToPosition(text.IndexOf("葉"u8), encoding);
            var outer = lines.ToPosition(text.IndexOf("方法"u8), encoding);
            var prepared = await service.PrepareCallHierarchyAsync(project, outer, deadline.Token);
            Check(prepared is [{ Name.Span: var name, SelectionRange: var selection }] && name.SequenceEqual("方法"u8)
                && selection.End.Character - selection.Start.Character == (encoding == PositionEncoding.Utf8 ? 6 : 2), "Prepare call hierarchy preserves negotiated name bounds");
            var outgoing = await service.GetOutgoingCallsAsync(project, outer, deadline.Token);
            Check(outgoing is [{ Item.Name.Span: var to, FromRanges.Count: 1 }] && to.SequenceEqual("葉"u8), "Deep outgoing traversal reaches the nested call");
            var incoming = await service.GetIncomingCallsAsync(project, leaf, deadline.Token);
            Check(incoming is [{ Item.Name.Span: var from, FromRanges.Count: 1 }] && from.SequenceEqual("方法"u8), "Deep incoming traversal retains the named containing function");
            Check(await service.PrepareCallHierarchyAsync(project, new(0, 0), deadline.Token) is null, "Prepare at source-file position zero has no item");
            Check(await service.GetOutgoingCallsAsync(project, new(0, 0), deadline.Token) is [{ Item.Name.Span: var top }] && top.SequenceEqual("方法"u8), "A source-file item includes top-level calls");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.GetOutgoingCallsAsync(project, outer, deadline.Token).AsTask()));
            Check(concurrent.All(result => result is [{ FromRanges.Count: 1 }]), "Concurrent call-hierarchy requests have independent collectors");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await service.PrepareCallHierarchyAsync(project, outer, canceled.Token); Check(false, "Canceled prepare must throw"); }
            catch (OperationCanceledException) { checks++; }
            try { await service.GetOutgoingCallsAsync(project, outer, canceled.Token); Check(false, "Canceled outgoing query must throw"); }
            catch (OperationCanceledException) { checks++; }
            try { await service.GetIncomingCallsAsync(project, leaf, canceled.Token); Check(false, "Canceled incoming query must throw"); }
            catch (OperationCanceledException) { checks++; }
        }
        Check(original.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags), "Call hierarchy leaves shared syntax immutable");
        var (released, results) = await ReleasedAsync();
        for (int i = 0; i < 3 && released.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!released.IsAlive, "Returned call-hierarchy items do not retain disposed projects");
        GC.KeepAlive(results);
        return checks;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference, CallHierarchyCall[]?)> ReleasedAsync()
    {
        Utf8String source = "export function a() {}"u8, use = "import { a } from './a'; a();"u8;
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = source.Span.ToArray(), ["/b.ts"u8] = use.Span.ToArray() }, true);
        await using var session = new ProjectSession(fs);
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8); session.SetInferredOptions(options);
        session.Notify(new(FileChangeKind.Open, "/b.ts"u8, 1, use));
        await using var snapshot = await session.GetSnapshotAsync(["/a.ts"u8]);
        var calls = await CrossProjectReferences.IncomingCallsAsync(session, snapshot.Snapshot, "/a.ts"u8, new(0, 16), PositionEncoding.Utf8, default);
        if (calls is not [{ FromRanges.Count: 1 }]) throw new InvalidOperationException("Retention fixture must exercise incoming-call conversion");
        return (new(snapshot.Snapshot.GetDefaultProject("/a.ts"u8)!.Program), calls);
    }
}

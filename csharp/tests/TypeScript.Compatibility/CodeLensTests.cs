using System.Runtime.CompilerServices;
using System.Text.Json;
using TypeScript.Compiler;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compatibility;

internal static class CodeLensTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        const int depth = 2048;
        var text = Utf8String.FromString("export function 方法() {" + new string('{', depth) + "方法();" + new string('}', depth) + "}\r\n方法();");
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = text.Span.ToArray() }, true);
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8);
        await using var session = new ProjectSession(fs);
        session.SetInferredOptions(options); session.Notify(new(FileChangeKind.Open, "/a.ts"u8, 1, text));
        int refreshes = 0; session.CodeLensRefreshRequested += () => refreshes++;
        var preferences = new UserPreferences { CodeLens = new(ReferencesEnabled: true, ShowOnAllFunctions: true) };
        session.Configure(preferences); session.Configure(preferences with { MaximumHoverLength = 100 });
        Check(refreshes == 1, "Only a changed CodeLens preference requests a refresh");
        await using var snapshot = await session.GetSnapshotAsync(["/a.ts"u8]);
        session.Configure(preferences);
        Check(refreshes == 1, "Committed snapshot preferences also suppress unchanged refreshes");
        var project = snapshot.Snapshot.GetDefaultProject("/a.ts"u8)!;
        var file = project.Program!.GetFile("/a.ts"u8)!;
        var original = file.Syntax.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        foreach (var encoding in new[] { PositionEncoding.Utf8, PositionEncoding.Utf16 })
        {
            var service = new LanguageServiceDocument(project.Program, file, encoding);
            Check(await service.GetCodeLensesAsync(project, cancellation: deadline.Token) is null, "CodeLens is disabled by default");
            Check(await service.GetCodeLensesAsync(project, new(ImplementationsEnabled: true), deadline.Token) is [], "Enabled CodeLens with no candidates returns an array");
            var lenses = await service.GetCodeLensesAsync(project, preferences.CodeLens, deadline.Token);
            Check(lenses is [{ Data.Position: 16, Range.Start.Line: 0, Range.Start.Character: 16 }], "Deep lens traversal returns the raw source offset and document position");
            var resolved = await service.ResolveCodeLensAsync(project, lenses![0], "editor.showLocations"u8, deadline.Token);
            Check(resolved.Command is { Title.Span: var title, Command.Span: var command, Locations.Count: 2 }
                && title.SequenceEqual("2 references"u8) && command.SequenceEqual("editor.showLocations"u8), "Resolution excludes the declaration and retains both uses");
            Check(resolved.Command!.Locations!.All(location => location.Range.End.Character - location.Range.Start.Character == (encoding == PositionEncoding.Utf8 ? 6 : 2)), "Resolved ranges use the negotiated encoding");
            var noCommand = await service.ResolveCodeLensAsync(project, lenses[0], cancellation: deadline.Token);
            Check(noCommand.Command is { Title.Span: var count, Command.IsEmpty: true, Locations: null } && count.SequenceEqual("2 references"u8), "An absent command keeps the count and omits arguments");
            var emptyCommand = await service.ResolveCodeLensAsync(project, lenses[0], (Utf8String)""u8, deadline.Token);
            Check(emptyCommand.Command is { Command.IsEmpty: true, Locations.Count: 2 }, "An explicitly empty command still includes arguments");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.ResolveCodeLensAsync(project, lenses[0], "show"u8, deadline.Token).AsTask()));
            Check(concurrent.All(result => result.Command?.Locations?.Count == 2), "Concurrent resolution has independent output and checker leases");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await service.GetCodeLensesAsync(project, preferences.CodeLens, canceled.Token); Check(false, "Canceled enumeration must throw"); }
            catch (OperationCanceledException) { checks++; }
            try { await service.ResolveCodeLensAsync(project, lenses[0], cancellation: canceled.Token); Check(false, "Canceled resolution must throw"); }
            catch (OperationCanceledException) { checks++; }
            try { await service.ResolveCodeLensAsync(project, lenses[0] with { Data = lenses[0].Data with { SupplementalFileIndex = 0 } }, cancellation: deadline.Token); Check(false, "Unknown supplemental identity must fail"); }
            catch (ArgumentException error) { Check(error.Message == "supplemental source file index not found: 0", "Invalid supplemental identity preserves the diagnostic"); }
        }
        Check(original.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags), "CodeLens leaves shared syntax immutable");
        var (released, result) = await ReleasedAsync();
        for (int i = 0; i < 3 && released.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!released.IsAlive, "Resolved lenses do not retain a disposed project"); GC.KeepAlive(result);
        return checks;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference, CodeLens)> ReleasedAsync()
    {
        Utf8String text = "export function a() {} a();"u8;
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = text.Span.ToArray() }, true);
        await using var session = new ProjectSession(fs);
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8); session.SetInferredOptions(options);
        session.Notify(new(FileChangeKind.Open, "/a.ts"u8, 1, text));
        await using var snapshot = await session.GetSnapshotAsync(["/a.ts"u8]);
        var project = snapshot.Snapshot.GetDefaultProject("/a.ts"u8)!;
        var service = new LanguageServiceDocument(project.Program!, project.Program!.GetFile("/a.ts"u8)!);
        var lenses = await service.GetCodeLensesAsync(project, new(ReferencesEnabled: true));
        var resolved = await CrossProjectReferences.ResolveCodeLensAsync(session, snapshot.Snapshot, lenses![0], "show"u8, PositionEncoding.Utf8, default);
        if (resolved.Command?.Locations?.Count != 1) throw new InvalidOperationException("Retention fixture must resolve references");
        return (new(project.Program), resolved);
    }

    internal static async Task<int> LspSafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (bool refreshSupport in new[] { false, true })
        {
            var (serverStream, clientStream) = RpcTests.DuplexStream.Pair();
            var running = LanguageServer.RunAsync(new MemoryFileSystem([], true), serverStream, serverStream, new() { CurrentDirectory = "/"u8 }, deadline.Token);
            var callbacks = new Client();
            await using var peer = new RpcConnection(clientStream, clientStream, callbacks);
            var clientRun = peer.RunAsync(deadline.Token);
            async Task<JsonElement> Request(Utf8String method, string parameters)
            {
                using var json = JsonDocument.Parse(await peer.CallAsync(method, System.Text.Encoding.UTF8.GetBytes(parameters), deadline.Token));
                return json.RootElement.Clone();
            }
            var initialized = await Request("initialize"u8, "{\"processId\":null,\"rootUri\":\"file:///\",\"capabilities\":{\"workspace\":{\"codeLens\":{\"refreshSupport\":" + (refreshSupport ? "true" : "false") + "}}},\"initializationOptions\":{\"codeLensShowLocationsCommandName\":\"show\"}}");
            Check(initialized.GetProperty("capabilities").GetProperty("codeLensProvider").GetProperty("resolveProvider").GetBoolean(), "Server advertises resolvable lenses");
            await peer.NotifyAsync("initialized"u8, "{}"u8.ToArray(), deadline.Token);
            await peer.NotifyAsync("textDocument/didOpen"u8, "{\"textDocument\":{\"uri\":\"file:///a.ts\",\"languageId\":\"typescript\",\"version\":1,\"text\":\"export function a() {} a();\"}}"u8.ToArray(), deadline.Token);
            Check((await Request("textDocument/codeLens"u8, "{\"textDocument\":{\"uri\":\"file:///a.ts\"}}")).ValueKind == JsonValueKind.Null, "Initial disabled preference returns null");
            var settings = "{\"settings\":{\"js/ts\":{\"referencesCodeLens\":{\"enabled\":true}}}}"u8.ToArray();
            await peer.NotifyAsync("workspace/didChangeConfiguration"u8, settings, deadline.Token);
            var lenses = await Request("textDocument/codeLens"u8, "{\"textDocument\":{\"uri\":\"file:///a.ts\"}}");
            Check(lenses.GetArrayLength() == 1, "Configuration updates affect subsequent requests");
            if (refreshSupport) await callbacks.RefreshSeen.Task.WaitAsync(deadline.Token);
            Check(callbacks.Refreshes == (refreshSupport ? 1 : 0), "Refresh is sent only when the client supports it");
            var resolved = await Request("codeLens/resolve"u8, lenses[0].GetRawText());
            Check(resolved.GetProperty("command").GetProperty("title").GetString() == "1 reference", "Resolution proceeds while the refresh callback is still pending");
            await peer.NotifyAsync("workspace/didChangeConfiguration"u8, settings, deadline.Token);
            await Request("textDocument/codeLens"u8, "{\"textDocument\":{\"uri\":\"file:///a.ts\"}}");
            Check(callbacks.Refreshes == (refreshSupport ? 1 : 0), "Unchanged configuration does not send another refresh");
            try { await Request("codeLens/resolve"u8, "{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":0}},\"data\":{\"kind\":\"references\",\"uri\":\"file:///gone.ts\",\"position\":0}}"); Check(false, "Missing file must fail resolution"); }
            catch (IOException error) { Check(error.InnerException is RpcException { Code: -32801 }, "Removed CodeLens files report ContentModified"); }
            await Request("shutdown"u8, "");
            await peer.NotifyAsync("exit"u8, ReadOnlyMemory<byte>.Empty, deadline.Token);
            await running.WaitAsync(deadline.Token); peer.Stop();
            try { await clientRun.WaitAsync(deadline.Token); }
            // The client may try to reply to its canceled callback after the server has closed.
            // A write error then belongs to this test client, not the completed server session.
            catch (IOException error) when (refreshSupport && callbacks.RefreshCanceled && error.Message == "Peer closed the connection") { }
            Check(!refreshSupport || callbacks.RefreshCanceled, "Shutdown cancels pending refresh work and disposes the session");
        }
        return checks;
    }

    private sealed class Client : IRpcHandler
    {
        internal int Refreshes;
        internal bool RefreshCanceled;
        internal readonly TaskCompletionSource RefreshSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<RpcResponse> HandleRequestAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation)
        {
            if (method == "workspace/codeLens/refresh"u8)
            {
                Interlocked.Increment(ref Refreshes); RefreshSeen.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellation); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { RefreshCanceled = true; throw; }
            }
            return RpcResponse.Null;
        }
        public ValueTask HandleNotificationAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation) => ValueTask.CompletedTask;
    }
}

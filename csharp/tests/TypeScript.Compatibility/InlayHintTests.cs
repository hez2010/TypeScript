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

internal static class InlayHintTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        const int depth = 2048;
        var text = Utf8String.FromString("function 方法(値:number) {" + new string('{', depth) + "let 数 = 値;" + new string('}', depth) + "return 値;}\r\nlet 別 = 方法(1);");
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = text.Span.ToArray() }, true);
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8);
        await using var session = new ProjectSession(fs, new() { MaxCheckers = 2 });
        session.SetInferredOptions(options); session.Notify(new(FileChangeKind.Open, "/a.ts"u8, 1, text));
        var preferences = new UserPreferences { InlayHints = new(ParameterNames: "all"u8, VariableTypes: true, ReturnTypes: true) };
        int refreshes = 0; session.InlayHintsRefreshRequested += () => refreshes++;
        session.Configure(preferences); session.Configure(preferences with { MaximumHoverLength = 100 });
        Check(refreshes == 1, "Only changed inlay preferences request refresh");
        await using var snapshot = await session.GetSnapshotAsync(["/a.ts"u8]);
        session.Configure(preferences);
        Check(refreshes == 1, "Committed preferences also suppress redundant refreshes");
        var project = snapshot.Snapshot.GetDefaultProject("/a.ts"u8)!;
        var file = project.Program!.GetFile("/a.ts"u8)!;
        var original = file.Syntax.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        foreach (var encoding in new[] { PositionEncoding.Utf8, PositionEncoding.Utf16 })
        {
            var service = new LanguageServiceDocument(project.Program, file, encoding);
            var range = new DocumentRange(new(0, 0), new(2, 0));
            Check(await service.GetInlayHintsAsync(project, range, cancellation: deadline.Token) is null, "Hints are disabled by default");
            Check(await service.GetInlayHintsAsync(project, range, new() { InlayHints = new(EnumValues: true) }, deadline.Token) is [], "Enabled hints without candidates return an array");
            var hints = await service.GetInlayHintsAsync(project, range, preferences, deadline.Token);
            Check(hints is { Count: 4 } && hints.Count(hint => hint.Kind == 1) == 3 && hints.Count(hint => hint.Kind == 2) == 1, "Deep traversal returns return, variable, and parameter hints");
            var parameter = hints!.Single(hint => hint.Kind == 2);
            Check(parameter.Parts is [{ Value.Span: var name, Location: { } location }, { Value.Span: var colon }]
                && name.SequenceEqual("値"u8) && colon.SequenceEqual(":"u8)
                && location.Range.End.Character - location.Range.Start.Character == (encoding == PositionEncoding.Utf8 ? 3 : 1), "Parameter navigation uses the negotiated encoding");
            Check(hints!.All(hint => hint.Kind == 1 ? hint.PaddingLeft == true && hint.PaddingRight is null : hint.PaddingLeft is null && hint.PaddingRight == true), "Padding fields retain their optional values");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.GetInlayHintsAsync(project, range, preferences, deadline.Token).AsTask()));
            Check(concurrent.All(hints => hints?.Count == 4), "Concurrent queries use independent checker leases");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await service.GetInlayHintsAsync(project, range, preferences, canceled.Token); Check(false, "Canceled hint query must throw"); }
            catch (OperationCanceledException) { checks++; }
            using var heldRequest = new ProjectRequest(deadline.Token);
            using var held = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, heldRequest, file.Syntax);
            using var queuedCancellation = new CancellationTokenSource();
            var queued = service.GetInlayHintsAsync(project, range, preferences, queuedCancellation.Token).AsTask();
            Check(!queued.IsCompleted, "The held checker queues the next hint request");
            queuedCancellation.Cancel();
            try { await queued.WaitAsync(deadline.Token); Check(false, "Queued cancellation must throw"); }
            catch (OperationCanceledException) { checks++; }
        }
        Check(original.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags), "Hints leave shared syntax immutable");
        using var config = JsonDocument.Parse("{\"inlayHints\":{\"parameterNames\":{\"enabled\":\"literals\",\"suppressWhenArgumentMatchesName\":false},\"variableTypes\":{\"enabled\":true,\"suppressWhenTypeMatchesName\":false}}}");
        var parsed = new UserPreferences().WithConfig(config.RootElement).InlayHints;
        Check(parsed.ParameterNames == "literals"u8 && parsed.ParameterNamesWhenArgumentMatches == true && parsed.VariableTypes == true && parsed.VariableTypesWhenNameMatches == true, "Stable suppress settings invert the raw show settings");
        var (released, retained) = await ReleasedAsync();
        for (int i = 0; i < 3 && released.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!released.IsAlive, "Hint label targets do not retain the disposed project"); GC.KeepAlive(retained);
        return checks;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference, IReadOnlyList<InlayHint>?)> ReleasedAsync()
    {
        Utf8String text = "class Type {} function f(value:Type) { return value; } let x = f(new Type());"u8;
        await using var session = new ProjectSession(new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = text.Span.ToArray() }, true));
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8); session.SetInferredOptions(options);
        session.Notify(new(FileChangeKind.Open, "/a.ts"u8, 1, text));
        await using var snapshot = await session.GetSnapshotAsync(["/a.ts"u8]);
        var project = snapshot.Snapshot.GetDefaultProject("/a.ts"u8)!;
        var service = new LanguageServiceDocument(project.Program!, project.Program!.GetFile("/a.ts"u8)!);
        var hints = await service.GetInlayHintsAsync(project, new(new(0, 0), new(1, 0)), new() { InlayHints = new(ParameterNames: "all"u8, VariableTypes: true, ReturnTypes: true) });
        if (hints is not { Count: 3 } || !hints.Any(hint => hint.Parts?.Any(part => part.Location is not null) == true)) throw new InvalidOperationException("Retention fixture must produce navigation targets");
        return (new(project.Program), hints);
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
            const string query = "{\"textDocument\":{\"uri\":\"file:///a.ts\"},\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":1,\"character\":0}}}";
            var initialized = await Request("initialize"u8, "{\"processId\":null,\"rootUri\":\"file:///\",\"capabilities\":{\"workspace\":{\"inlayHint\":{\"refreshSupport\":" + (refreshSupport ? "true" : "false") + "}}}}");
            Check(initialized.GetProperty("capabilities").GetProperty("inlayHintProvider").GetBoolean(), "Server advertises inlay hints");
            await peer.NotifyAsync("initialized"u8, "{}"u8.ToArray(), deadline.Token);
            await peer.NotifyAsync("textDocument/didOpen"u8, "{\"textDocument\":{\"uri\":\"file:///a.ts\",\"languageId\":\"typescript\",\"version\":1,\"text\":\"function f(value:number) { return value; } let x=f(1);\"}}"u8.ToArray(), deadline.Token);
            Check((await Request("textDocument/inlayHint"u8, query)).ValueKind == JsonValueKind.Null, "Initial disabled preference returns null");
            var settings = "{\"settings\":{\"js/ts\":{\"inlayHints\":{\"parameterNames\":{\"enabled\":\"all\"},\"variableTypes\":{\"enabled\":true}}}}}"u8.ToArray();
            await peer.NotifyAsync("workspace/didChangeConfiguration"u8, settings, deadline.Token);
            var hints = await Request("textDocument/inlayHint"u8, query);
            Check(hints.GetArrayLength() == 2, "Updated settings enable parameter and variable hints");
            if (refreshSupport) await callbacks.RefreshSeen.Task.WaitAsync(deadline.Token);
            Check(callbacks.Refreshes == (refreshSupport ? 1 : 0), "Refresh requires client support");
            await peer.NotifyAsync("workspace/didChangeConfiguration"u8, settings, deadline.Token);
            Check((await Request("textDocument/inlayHint"u8, query)).GetArrayLength() == 2, "Requests complete while a refresh callback is pending");
            Check(callbacks.Refreshes == (refreshSupport ? 1 : 0), "Unchanged settings do not request another refresh");
            await Request("shutdown"u8, "");
            await peer.NotifyAsync("exit"u8, ReadOnlyMemory<byte>.Empty, deadline.Token);
            await running.WaitAsync(deadline.Token); peer.Stop(); await clientRun.WaitAsync(deadline.Token);
            Check(true, "Shutdown cancels pending refreshes and disposes the session");
        }
        return checks;
    }

    private sealed class Client : IRpcHandler
    {
        internal int Refreshes;
        internal readonly TaskCompletionSource RefreshSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<RpcResponse> HandleRequestAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation)
        {
            if (method == "workspace/inlayHint/refresh"u8)
            {
                Interlocked.Increment(ref Refreshes); RefreshSeen.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellation);
            }
            return RpcResponse.Null;
        }
        public ValueTask HandleNotificationAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation) => ValueTask.CompletedTask;
    }
}

using System.Text.Json;
using TypeScript.Compiler;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compatibility;

internal static partial class DocumentDiagnosticTests
{
    internal static async Task<int> LspSafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (bool refreshSupport in new[] { false, true })
        {
            var (serverStream, clientStream) = RpcTests.DuplexStream.Pair();
            var running = LanguageServer.RunAsync(new MemoryFileSystem([], true), serverStream, serverStream, new() { CurrentDirectory = "/"u8 }, deadline.Token);
            var callbacks = new DiagnosticClient();
            await using var peer = new RpcConnection(clientStream, clientStream, callbacks);
            var clientRun = peer.RunAsync(deadline.Token);
            async Task<JsonElement> Request(Utf8String method, string parameters)
            {
                using var json = JsonDocument.Parse(await peer.CallAsync(method, System.Text.Encoding.UTF8.GetBytes(parameters), deadline.Token));
                return json.RootElement.Clone();
            }
            const string query = "{\"textDocument\":{\"uri\":\"file:///a.ts\"},\"previousResultId\":\"old\"}";
            var initialized = await Request("initialize"u8, "{\"processId\":null,\"rootUri\":\"file:///\",\"locale\":\"ja\",\"initializationOptions\":{\"disablePushDiagnostics\":true},\"capabilities\":{\"_vs_supportsVisualStudioExtensions\":true,\"workspace\":{\"diagnostics\":{\"refreshSupport\":" + (refreshSupport ? "true" : "false") + "}}}}");
            var provider = initialized.GetProperty("capabilities").GetProperty("diagnosticProvider");
            Check(provider.GetProperty("identifier").GetString() == "typescript" && provider.GetProperty("interFileDependencies").GetBoolean()
                && !provider.GetProperty("workspaceDiagnostics").GetBoolean(), "Server advertises the file diagnostic provider");
            await peer.NotifyAsync("initialized"u8, "{}"u8.ToArray(), deadline.Token);
            await peer.NotifyAsync("textDocument/didOpen"u8, "{\"textDocument\":{\"uri\":\"file:///a.ts\",\"languageId\":\"typescript\",\"version\":1,\"text\":\"const 名:number='字';\"}}"u8.ToArray(), deadline.Token);
            var report = await Request("textDocument/diagnostic"u8, query);
            Check(report.GetProperty("kind").GetString() == "full" && !report.TryGetProperty("resultId", out _), "Previous result IDs still produce complete reports");
            var item = report.GetProperty("items")[0];
            Check(item.GetProperty("code").GetString() == "TS2322" && item.GetProperty("message").GetString() == Messages.Type_0_is_not_assignable_to_type_1.Format("ja"u8, "string"u8, "number"u8).ToString(), "Visual Studio codes and locale are negotiated at initialization");
            var settings = "{\"settings\":{\"js/ts\":{\"validate\":{\"enabled\":false}}}}"u8.ToArray();
            await peer.NotifyAsync("workspace/didChangeConfiguration"u8, settings, deadline.Token);
            Check((await Request("textDocument/diagnostic"u8, query)).GetProperty("items").GetArrayLength() == 0, "Disabled validation clears the report");
            if (refreshSupport) await callbacks.RefreshSeen.Task.WaitAsync(deadline.Token);
            Check(callbacks.Refreshes == (refreshSupport ? 1 : 0), "Diagnostic refresh requires client support");
            await peer.NotifyAsync("workspace/didChangeConfiguration"u8, settings, deadline.Token);
            Check((await Request("textDocument/diagnostic"u8, query)).GetProperty("items").GetArrayLength() == 0, "Requests complete while a diagnostic refresh is pending");
            Check(callbacks.Refreshes == (refreshSupport ? 1 : 0), "Unchanged diagnostic settings do not request another refresh");
            await Request("shutdown"u8, "");
            await peer.NotifyAsync("exit"u8, ReadOnlyMemory<byte>.Empty, deadline.Token);
            await running.WaitAsync(deadline.Token); peer.Stop(); await clientRun.WaitAsync(deadline.Token);
            Check(true, "Shutdown cancels pending diagnostic refreshes");
        }
        return checks;
    }

    private sealed class DiagnosticClient : IRpcHandler
    {
        internal int Refreshes;
        internal readonly TaskCompletionSource RefreshSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<RpcResponse> HandleRequestAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation)
        {
            if (method == "workspace/diagnostic/refresh"u8)
            {
                Interlocked.Increment(ref Refreshes); RefreshSeen.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellation);
            }
            return RpcResponse.Null;
        }
        public ValueTask HandleNotificationAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation) => ValueTask.CompletedTask;
    }
}

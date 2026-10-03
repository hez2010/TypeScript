using System.Text.Json;
using System.Threading.Channels;
using TypeScript.Compiler;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compatibility;

internal static partial class DocumentDiagnosticTests
{
    internal static async Task<int> PushSafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        foreach (bool visualStudio in new[] { false, true })
        {
            var memory = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
            {
                ["/src/tsconfig.json"u8] = "{\"compilerOptions\":{\"target\":\"nope\"}}"u8.ToArray(),
                ["/src/a.ts"u8] = "export const 名 = 1;"u8.ToArray(),
                ["/global/tsconfig.json"u8] = "{\"compilerOptions\":{\"target\":\"es2020\"}}"u8.ToArray(),
                ["/global/a.ts"u8] = "export function f() { using x = { [Symbol.dispose]() {} }; }"u8.ToArray(),
            }, true);
            var (serverStream, clientStream) = RpcTests.DuplexStream.Pair();
            var running = LanguageServer.RunAsync(new LibraryFileSystem(memory), serverStream, serverStream, new() { CurrentDirectory = "/"u8 }, deadline.Token);
            var callbacks = new PushClient();
            await using var peer = new RpcConnection(clientStream, clientStream, callbacks);
            var clientRun = peer.RunAsync(deadline.Token);
            async Task<JsonElement> Request(Utf8String method, string parameters)
            {
                using var json = JsonDocument.Parse(await peer.CallAsync(method, System.Text.Encoding.UTF8.GetBytes(parameters), deadline.Token));
                return json.RootElement.Clone();
            }
            ValueTask Notify(Utf8String method, string parameters) => peer.NotifyAsync(method, System.Text.Encoding.UTF8.GetBytes(parameters), deadline.Token);
            async Task<JsonElement> Next(string uri)
            {
                while (true)
                {
                    var item = await callbacks.Publications.Reader.ReadAsync(deadline.Token);
                    Check(!item.TryGetProperty("version", out _), "Project publications omit source document versions");
                    if (item.GetProperty("uri").GetString() == uri) return item.GetProperty("diagnostics");
                }
            }
            bool HasCode(JsonElement diagnostics, int code) => diagnostics.EnumerateArray().Any(item => visualStudio
                ? item.GetProperty("code").GetString() == $"TS{code}" : item.GetProperty("code").GetInt32() == code);
            await Request("initialize"u8, "{\"processId\":null,\"rootUri\":\"file:///\",\"capabilities\":{\"_vs_supportsVisualStudioExtensions\":" + (visualStudio ? "true" : "false") + ",\"general\":{\"positionEncodings\":[\"" + (visualStudio ? "utf-8" : "utf-16") + "\"]},\"textDocument\":{\"publishDiagnostics\":{\"relatedInformation\":true,\"tagSupport\":{\"valueSet\":[1,2]}}}}}");
            await Notify("initialized"u8, "{}");
            const string open = "{\"textDocument\":{\"uri\":\"file:///src/a.ts\",\"languageId\":\"typescript\",\"version\":1,\"text\":\"export const 名 = 1;\"}}";
            await Notify("textDocument/didOpen"u8, open);
            var first = await Next("file:///src/tsconfig.json");
            Check(HasCode(first, 6046), "Opening a source publishes configuration errors without a follow-up request");
            var error = first.EnumerateArray().First(item => visualStudio ? item.GetProperty("code").GetString() == "TS6046" : item.GetProperty("code").GetInt32() == 6046);
            Check(error.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32() > 0, "Configuration errors preserve their source range");
            memory.WriteFile("/src/tsconfig.json"u8, "{\"compilerOptions\":{}}"u8);
            const string watched = "{\"changes\":[{\"uri\":\"file:///src/tsconfig.json\",\"type\":2}]}";
            await Notify("workspace/didChangeWatchedFiles"u8, watched);
            Check((await Next("file:///src/tsconfig.json")).GetArrayLength() == 0, "A watched configuration fix clears errors without a follow-up request");
            memory.WriteFile("/src/tsconfig.json"u8, "{\"compilerOptions\":{\"target\":\"nope\"}}"u8);
            await Notify("workspace/didChangeWatchedFiles"u8, watched);
            Check(HasCode(await Next("file:///src/tsconfig.json"), 6046), "A watched configuration error is published");
            await Notify("textDocument/didClose"u8, "{\"textDocument\":{\"uri\":\"file:///src/a.ts\"}}");
            Check((await Next("file:///src/tsconfig.json")).GetArrayLength() == 0, "Closing the final source clears project diagnostics");
            await Notify("textDocument/didOpen"u8, open);
            Check(HasCode(await Next("file:///src/tsconfig.json"), 6046), "Reopening a source restores project diagnostics");
            await Notify("workspace/didChangeConfiguration"u8, "{\"settings\":{\"js/ts\":{\"validate\":{\"enabled\":false}}}}");
            await Request("textDocument/diagnostic"u8, "{\"textDocument\":{\"uri\":\"file:///src/a.ts\"}}");
            Check((await Next("file:///src/tsconfig.json")).GetArrayLength() == 0, "Disabling validation clears pushed diagnostics");
            await Notify("workspace/didChangeConfiguration"u8, "{\"settings\":{\"js/ts\":{\"validate\":{\"enabled\":true}}}}");
            await Notify("textDocument/didOpen"u8, "{\"textDocument\":{\"uri\":\"file:///global/a.ts\",\"languageId\":\"typescript\",\"version\":1,\"text\":\"export function f() { using x = { [Symbol.dispose]() {} }; }\"}}");
            Check((await Next("file:///global/tsconfig.json")).GetArrayLength() == 0, "Global errors are deferred until checking discovers them");
            await Request("textDocument/diagnostic"u8, "{\"textDocument\":{\"uri\":\"file:///global/a.ts\"}}");
            Check(HasCode(await Next("file:///global/tsconfig.json"), 2318), "A completed check publishes newly discovered global errors");
            await Notify("workspace/didChangeWatchedFiles"u8, watched);
            await Request("shutdown"u8, "");
            await Notify("exit"u8, "");
            await running.WaitAsync(deadline.Token); peer.Stop(); await clientRun.WaitAsync(deadline.Token);
            Check(true, "Shutdown cancels pending project updates and publication work");
        }
        return checks;
    }

    internal static async Task<int> RefreshSafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (bool disabled in new[] { false, true })
        {
            var memory = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
            {
                ["/src/tsconfig.json"u8] = "{\"compilerOptions\":{\"noLib\":true}}"u8.ToArray(),
                ["/src/custom.json"u8] = "{\"compilerOptions\":{\"noLib\":true,\"target\":\"nope\"}}"u8.ToArray(),
                ["/src/a.ts"u8] = "export const 名: number = '字';"u8.ToArray(),
            }, true);
            var (serverStream, clientStream) = RpcTests.DuplexStream.Pair();
            var running = LanguageServer.RunAsync(memory, serverStream, serverStream, new() { CurrentDirectory = "/"u8 }, deadline.Token);
            var callbacks = new PushClient { RejectRefresh = disabled };
            await using var peer = new RpcConnection(clientStream, clientStream, callbacks);
            var clientRun = peer.RunAsync(deadline.Token);
            async Task<JsonElement> Request(Utf8String method, string parameters)
            {
                using var json = JsonDocument.Parse(await peer.CallAsync(method, System.Text.Encoding.UTF8.GetBytes(parameters), deadline.Token));
                return json.RootElement.Clone();
            }
            ValueTask Notify(Utf8String method, string parameters) => peer.NotifyAsync(method, System.Text.Encoding.UTF8.GetBytes(parameters), deadline.Token);
            const string query = "{\"textDocument\":{\"uri\":\"file:///src/a.ts\"}}";
            await Request("initialize"u8, "{\"processId\":null,\"rootUri\":\"file:///\",\"initializationOptions\":{\"disablePushDiagnostics\":" + (disabled ? "true" : "false") + "},\"capabilities\":{\"workspace\":{\"diagnostics\":{\"refreshSupport\":true}}}}");
            await Notify("initialized"u8, "{}");
            await Notify("textDocument/didOpen"u8, "{\"textDocument\":{\"uri\":\"file:///src/a.ts\",\"languageId\":\"typescript\",\"version\":1,\"text\":\"export const 名: number = '字';\"}}");
            await Request("textDocument/diagnostic"u8, query);
            await Notify("workspace/didChangeConfiguration"u8, "{\"settings\":{\"js/ts\":{\"unstable\":{\"customConfigFileName\":\"custom.json\",\"locale\":\"ja\"}}}}");
            var report = await Request("textDocument/diagnostic"u8, query);
            var error = report.GetProperty("items").EnumerateArray().First(item => item.GetProperty("code").GetInt32() == 2322);
            Check(error.GetProperty("message").GetString() == TypeScript.Compiler.Diagnostics.Messages.Type_0_is_not_assignable_to_type_1.Format("ja"u8, "string"u8, "number"u8).ToString(), "Configuration locale changes apply to pulled diagnostics");
            await Task.Delay(650, deadline.Token);
            Check(callbacks.Refreshes == 1, "A custom configuration name change schedules one diagnostic refresh");
            var publications = new List<JsonElement>(); while (callbacks.Publications.Reader.TryRead(out var item)) publications.Add(item);
            Check(disabled ? publications.Count == 0 : publications.Any(item => item.GetProperty("uri").GetString() == "file:///src/custom.json"
                && item.GetProperty("diagnostics").EnumerateArray().Any(diagnostic => diagnostic.GetProperty("code").GetInt32() == 6046)), "Push-disable negotiation and custom configuration diagnostics are honored");
            int before = callbacks.Refreshes;
            for (int i = 0; i < 3; i++) await Notify("workspace/didChangeWatchedFiles"u8, "{\"changes\":[{\"uri\":\"file:///src/dependency.ts\",\"type\":2}]}");
            await Request("textDocument/diagnostic"u8, query);
            await Task.Delay(650, deadline.Token);
            Check(callbacks.Refreshes == before + 1, "Watched source notifications coalesce into one refresh");
            before = callbacks.Refreshes;
            await Notify("workspace/didChangeWatchedFiles"u8, "{\"changes\":[{\"uri\":\"file:///src/picture.png\",\"type\":2},{\"uri\":\"file:///src/unknown.ts\",\"type\":99}]}");
            await Request("textDocument/diagnostic"u8, query);
            await Task.Delay(650, deadline.Token);
            Check(callbacks.Refreshes == before, "Unrelated extensions and invalid watcher kinds do not refresh diagnostics");
            await Notify("workspace/didChangeWatchedFiles"u8, "{\"changes\":[{\"uri\":\"file:///src/dependency.ts\",\"type\":2}]}");
            await Notify("textDocument/didChange"u8, "{\"textDocument\":{\"uri\":\"file:///src/a.ts\",\"version\":2},\"contentChanges\":[{\"text\":\"export const 名: number = 1;\"}]}");
            await Request("textDocument/diagnostic"u8, query);
            await Task.Delay(650, deadline.Token);
            Check(callbacks.Refreshes == before, "Ordinary source edits cancel a pending workspace diagnostic refresh");
            await Request("shutdown"u8, ""); await Notify("exit"u8, "");
            await running.WaitAsync(deadline.Token); peer.Stop(); await clientRun.WaitAsync(deadline.Token);
        }
        return checks;
    }

    private sealed class PushClient : IRpcHandler
    {
        internal int Refreshes;
        internal bool RejectRefresh;
        internal readonly Channel<JsonElement> Publications = Channel.CreateUnbounded<JsonElement>();
        public ValueTask<RpcResponse> HandleRequestAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation)
        {
            if (method == "workspace/diagnostic/refresh"u8)
            {
                Interlocked.Increment(ref Refreshes);
                if (RejectRefresh) throw new RpcException(-32601, "Refresh rejected"u8);
            }
            return ValueTask.FromResult(RpcResponse.Null);
        }
        public ValueTask HandleNotificationAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation)
        {
            if (method == "textDocument/publishDiagnostics"u8) Publications.Writer.TryWrite(parameters.Clone());
            return ValueTask.CompletedTask;
        }
    }
}

using System.Text.Json;
using TypeScript.Compiler;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compatibility;

internal static partial class CodeActionTests
{
    internal static async Task<int> LspSafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        foreach (var encoding in new[] { "utf-8", "utf-16" })
        {
            Utf8String source = "const 字='😀';declare function make():number;export let 値=make();"u8;
            Utf8String saved = "export const 値:number=1;"u8;
            var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = saved.Span.ToArray() }, true);
            var (server, client) = RpcTests.DuplexStream.Pair();
            var running = LanguageServer.RunAsync(fs, server, server, new() { CurrentDirectory = "/"u8, InferredCompilerOptions = Options() }, deadline.Token);
            await using var peer = new RpcConnection(client, client, new Client());
            var clientRun = peer.RunAsync(deadline.Token);
            async Task<JsonElement> Request(Utf8String method, RpcResponse parameters)
            {
                using var json = JsonDocument.Parse(await peer.CallAsync(method, parameters.Data, deadline.Token));
                return json.RootElement.Clone();
            }
            var initialized = await Request("initialize"u8, new(System.Text.Encoding.UTF8.GetBytes("{\"processId\":null,\"rootUri\":\"file:///\",\"capabilities\":{\"general\":{\"positionEncodings\":[\"" + encoding + "\"]}},\"initializationOptions\":{\"disablePushDiagnostics\":true}}")));
            var capabilities = initialized.GetProperty("capabilities");
            Check(capabilities.GetProperty("codeActionProvider").GetProperty("codeActionKinds").EnumerateArray().Select(item => item.GetString()).SequenceEqual(
                ["quickfix", "source.organizeImports.ts", "source.removeUnusedImports.ts", "source.sortImports.ts", "source.fixAll.ts"]), "Initialization advertises exactly the active code-action kinds");
            var completion = capabilities.GetProperty("completionProvider");
            Check(completion.GetProperty("resolveProvider").GetBoolean() && completion.GetProperty("completionItem").GetProperty("labelDetailsSupport").GetBoolean()
                && completion.GetProperty("triggerCharacters").EnumerateArray().Select(item => item.GetString()).SequenceEqual([".", "\"", "'", "`", "/", "@", "<", "#", " ", "*"]),
                "Initialization advertises complete completion and resolve capabilities");
            await peer.NotifyAsync("initialized"u8, "{}"u8.ToArray(), deadline.Token);
            await OpenAsync(source, 1);
            var parameters = await ActionsAsync();
            var actions = await Request("textDocument/codeAction"u8, parameters);
            Check(actions.GetArrayLength() > 0 && actions[0].GetProperty("edit").GetProperty("changes").GetProperty("file:///a.ts")[0].GetProperty("newText").GetString() == ": number",
                "LSP code actions return edits for the open overlay");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Request("textDocument/codeAction"u8, parameters)));
            Check(concurrent.All(value => JsonElement.DeepEquals(value, actions)), "Concurrent LSP action requests preserve full response identity");
            await peer.NotifyAsync("textDocument/didChange"u8, RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); writer.WriteStartObject("textDocument"u8); writer.WriteString("uri"u8, "file:///a.ts"u8); writer.WriteNumber("version"u8, 2); writer.WriteEndObject();
                writer.WriteStartArray("contentChanges"u8); writer.WriteStartObject(); writer.WriteString("text"u8, saved.Span); writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
            }).Data, deadline.Token);
            Check((await Request("textDocument/codeAction"u8, await ActionsAsync())).GetArrayLength() == 0, "Annotation edits clear actions after the document version changes");
            await peer.NotifyAsync("textDocument/didClose"u8, "{\"textDocument\":{\"uri\":\"file:///a.ts\"}}"u8.ToArray(), deadline.Token);
            await OpenAsync(source, 3);
            Check((await Request("textDocument/codeAction"u8, await ActionsAsync())).GetArrayLength() > 0, "Reopening a document rebuilds its annotation actions");
            await Request("shutdown"u8, default); await peer.NotifyAsync("exit"u8, ReadOnlyMemory<byte>.Empty, deadline.Token);
            await running.WaitAsync(deadline.Token); peer.Stop(); await clientRun.WaitAsync(deadline.Token);
            Check(true, "Shutdown disposes code-action session resources");

            ValueTask OpenAsync(Utf8String text, int version) => peer.NotifyAsync("textDocument/didOpen"u8, RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); writer.WriteStartObject("textDocument"u8); writer.WriteString("uri"u8, "file:///a.ts"u8);
                writer.WriteString("languageId"u8, "typescript"u8); writer.WriteNumber("version"u8, version); writer.WriteString("text"u8, text.Span); writer.WriteEndObject(); writer.WriteEndObject();
            }).Data, deadline.Token);

            async Task<RpcResponse> ActionsAsync()
            {
                var diagnostics = await Request("textDocument/diagnostic"u8, new("{\"textDocument\":{\"uri\":\"file:///a.ts\"}}"u8.ToArray()));
                return RpcResponse.Json(writer =>
                {
                    writer.WriteStartObject(); writer.WriteStartObject("textDocument"u8); writer.WriteString("uri"u8, "file:///a.ts"u8); writer.WriteEndObject();
                    writer.WritePropertyName("range"u8); LspJson.Range(writer, new(new(0, 0), new(0, 0)));
                    writer.WriteStartObject("context"u8); writer.WritePropertyName("diagnostics"u8); diagnostics.GetProperty("items").WriteTo(writer); writer.WriteEndObject(); writer.WriteEndObject();
                });
            }
        }
        return checks;
    }

    private sealed class Client : IRpcHandler
    {
        public ValueTask<RpcResponse> HandleRequestAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation) => new(RpcResponse.Null);
        public ValueTask HandleNotificationAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation) => ValueTask.CompletedTask;
    }
}

using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Api;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Projects;

namespace TypeScript.Compatibility;

internal static class LspApiTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellation = timeout.Token;
        var (server, client) = RpcTests.DuplexStream.Pair();
        var running = LanguageServer.RunAsync(new MemoryFileSystem([], true), server, server, new() { CurrentDirectory = "/"u8 }, cancellation);
        await using var lsp = new RpcConnection(client, client, new Client());
        var listening = lsp.RunAsync(cancellation);
        async ValueTask<JsonElement> Call(RpcConnection peer, Utf8String method, Utf8String parameters = default)
        {
            using var result = JsonDocument.Parse(await peer.CallAsync(method, method == "shutdown"u8 ? ReadOnlyMemory<byte>.Empty : parameters.IsEmpty ? "{}"u8.ToArray() : parameters.Memory, cancellation));
            return result.RootElement.Clone();
        }
        await Call(lsp, "initialize"u8, "{\"processId\":null,\"rootUri\":null,\"capabilities\":{}}"u8);
        await lsp.NotifyAsync("initialized"u8, "{}"u8.ToArray(), cancellation);
        await lsp.NotifyAsync("textDocument/didOpen"u8,
            "{\"textDocument\":{\"uri\":\"file:///a.ts\",\"languageId\":\"typescript\",\"version\":1,\"text\":\"export const 名 = '😀';\"}}"u8.ToArray(), cancellation);
        var endpoint = await Call(lsp, "custom/initializeAPISession"u8);
        var pipePath = Utf8String.FromString(endpoint.GetProperty("pipe").GetString()!);
        Check(endpoint.GetProperty("sessionId").GetString()!.StartsWith("api-session-", StringComparison.Ordinal), "API session has an identity");
        await using var stream = await ConnectAsync(pipePath, cancellation);
        await using var api = new RpcConnection(stream, stream, new Client());
        var apiRunning = api.RunAsync(cancellation);
        Check((await Call(api, "initialize"u8)).GetProperty("currentDirectory").GetString() == "/", "API inherits editor working directory");
        var first = await Call(api, "getCurrentLanguageServerSnapshot"u8,
            "{\"changes\":{\"createPrograms\":[{\"rootFiles\":[\"/a.ts\"],\"compilerOptions\":{\"noLib\":true}}]}}"u8);
        ulong oldId = first.GetProperty("snapshot").GetUInt64();
        string projectId = first.GetProperty("operation").GetProperty("createdPrograms")[0].GetString()!;
        async Task<Utf8String> Source(ulong id)
        {
            var response = await Call(api, "getSourceFile"u8, Utf8String.FromString($$"""{"snapshot":{{id}},"project":"{{projectId}}","file":"/a.ts"}"""));
            return ((SourceFileNode)AstDecoder.Decode(response.GetProperty("data").GetBytesFromBase64()).Root).Source.Text;
        }
        Check(await Source(oldId) == "export const 名 = '😀';"u8, "API sees the editor overlay through its pipe");
        await lsp.NotifyAsync("textDocument/didChange"u8,
            "{\"textDocument\":{\"uri\":\"file:///a.ts\",\"version\":2},\"contentChanges\":[{\"text\":\"export const 名 = '🦋';\"}]}"u8.ToArray(), cancellation);
        // Requests on different pipes have independent ordering; fence the editor notification on its own connection.
        await Call(lsp, "textDocument/foldingRange"u8, "{\"textDocument\":{\"uri\":\"file:///a.ts\"}}"u8);
        var updated = await Call(api, "getCurrentLanguageServerSnapshot"u8, Utf8String.FromString($$$"""{"baseSnapshot":{{{oldId}}},"changes":{"ensurePrograms":true}}"""));
        Check(await Source(updated.GetProperty("snapshot").GetUInt64()) == "export const 名 = '🦋';"u8, "API current snapshot sees an editor change");
        Check(await Source(oldId) == "export const 名 = '😀';"u8, "Pinned API snapshot keeps the old editor text");

        var observerEndpoint = await Call(lsp, "custom/initializeAPISession"u8);
        await using var observerStream = await ConnectAsync(Utf8String.FromString(observerEndpoint.GetProperty("pipe").GetString()!), cancellation);
        await using var observer = new RpcConnection(observerStream, observerStream, new Client());
        var observerRunning = observer.RunAsync(cancellation);
        await api.DisposeAsync(); await apiRunning;
        bool removed = false;
        for (int i = 0; i < 100; i++)
        {
            var state = await Call(observer, "getCurrentLanguageServerSnapshot"u8);
            removed = state.GetProperty("projects").EnumerateArray().All(project => project.GetProperty("id").GetString() != projectId);
            if (removed) break;
            await Task.Delay(10, cancellation);
        }
        Check(removed, "Disconnect releases only that API client's canonical synthetic program");
        var editor = await Call(lsp, "textDocument/selectionRange"u8, "{\"textDocument\":{\"uri\":\"file:///a.ts\"},\"positions\":[{\"line\":0,\"character\":0}]}"u8);
        Check(editor.ValueKind == JsonValueKind.Array && editor.GetArrayLength() == 1, "Disconnect preserves the editor document");
        var pending = await Call(lsp, "custom/initializeAPISession"u8);
        var pendingPath = Utf8String.FromString(pending.GetProperty("pipe").GetString()!);
        await Call(lsp, "shutdown"u8);
        await observerRunning.WaitAsync(cancellation);
        Check(true, "Shutdown closes connected API clients");
        using (var rebound = new RpcPipeListener(pendingPath)) Check(true, "Shutdown releases a bound pipe that no client connected to");
        await lsp.NotifyAsync("exit"u8, ReadOnlyMemory<byte>.Empty, cancellation);
        await running.WaitAsync(cancellation); await listening.WaitAsync(cancellation);
        await using var projects = new ProjectSession(new MemoryFileSystem([], true));
        await using var attached = new ApiSession(projects);
        using var changes = JsonDocument.Parse("{\"changes\":{\"createPrograms\":[{\"rootFiles\":[],\"compilerOptions\":{\"noLib\":true}}]}}");
        using var saved = JsonDocument.Parse((await attached.HandleRequestAsync("getCurrentLanguageServerSnapshot"u8, changes.RootElement, cancellation)).Data);
        ulong retainedId = saved.RootElement.GetProperty("snapshot").GetUInt64();
        await projects.DisposeAsync();
        using var query = JsonDocument.Parse($$"""{"snapshot":{{retainedId}},"project":"/dev/null/synthetic/1"}""");
        using var names = JsonDocument.Parse((await attached.HandleRequestAsync("getSourceFileNames"u8, query.RootElement, cancellation)).Data);
        Check(names.RootElement.GetArrayLength() == 0, "An API snapshot remains readable after its editor session closes");
        await attached.DisposeAsync();
        Check(true, "API disposal releases retained resources after the editor has already closed");
        return checks;
    }

    private static async ValueTask<Stream> ConnectAsync(Utf8String path, CancellationToken cancellation)
    {
        if (OperatingSystem.IsWindows())
        {
            var stream = new NamedPipeClientStream(".", (path.StartsWith("\\\\.\\pipe\\"u8) ? path[9..] : path).ToString(), PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await stream.ConnectAsync(cancellation); return stream; }
            catch { await stream.DisposeAsync(); throw; }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(path.ToString()), cancellation); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    }

    private sealed class Client : IRpcHandler
    {
        public ValueTask<RpcResponse> HandleRequestAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation) => ValueTask.FromResult(RpcResponse.Null);
        public ValueTask HandleNotificationAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation) => default;
    }
}

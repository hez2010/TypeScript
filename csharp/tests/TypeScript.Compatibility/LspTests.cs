using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Api;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compatibility;

internal static class LspTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellation = timeout.Token;
        foreach (var encoding in new[] { PositionEncoding.Utf8, PositionEncoding.Utf16 })
        {
            var (server, client) = RpcTests.DuplexStream.Pair(); await using var clientOwner = client;
            var running = LanguageServer.RunAsync(new MemoryFileSystem([], true), server, server, new() { CurrentDirectory = "/"u8 }, cancellation);
            var peer = new RpcWire(client, client, false, 10_000_000);
            int nextId = 0;
            async ValueTask<RpcMessage> Response()
            {
                var message = await peer.ReadAsync(cancellation);
                if (message is null) throw new EndOfStreamException();
                return message;
            }
            async ValueTask<RpcMessage> Request(Utf8String method, RpcResponse parameters)
            {
                var id = new RpcId(default, ++nextId);
                await peer.WriteAsync(id, method, parameters, null, cancellation);
                var message = await Response(); Check(message.Id == id, "Response preserves its request identity"); return message;
            }
            ValueTask Notify(Utf8String method, RpcResponse parameters) => peer.WriteAsync(null, method, parameters, null, cancellation);
            static JsonElement Json(ReadOnlyMemory<byte> data) { using var document = JsonDocument.Parse(data, new() { MaxDepth = int.MaxValue }); return document.RootElement.Clone(); }
            var before = await Request("textDocument/foldingRange"u8, RpcResponse.Null);
            Check(before.Error?.Code == -32002, "Requests before initialize fail without blocking");
            Check((await Request("initialize"u8, new("{\"capabilities\":{}}"u8.ToArray()))).Error?.Code == -32602,
                "Incomplete initialization is rejected without changing session state");
            var initialization = RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); writer.WriteNull("processId"); writer.WriteString("rootUri", "file:///"); writer.WriteStartObject("capabilities");
                writer.WriteStartObject("general"); writer.WriteStartArray("positionEncodings"); writer.WriteStringValue(encoding == PositionEncoding.Utf8 ? "utf-8" : "utf-16");
                writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteStartObject("workspace"); writer.WriteBoolean("configuration", true);
                writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
            });
            var initialized = await Request("initialize"u8, initialization);
            Check(Json(initialized.Data).GetProperty("capabilities").GetProperty("positionEncoding").GetString()
                == (encoding == PositionEncoding.Utf8 ? "utf-8" : "utf-16"), "Encoding is negotiated");
            Check((await Request("initialize"u8, initialization)).Error?.Code == -32600, "Duplicate initialization rejected");
            await Notify("initialized"u8, RpcResponse.Json(writer => { writer.WriteStartObject(); writer.WriteEndObject(); }));
            var configure = await Response(); Check(configure.Method == "workspace/configuration"u8, "Initialization requests client configuration");
            var cancelledId = new RpcId("queued"u8);
            await peer.WriteAsync(cancelledId, "textDocument/foldingRange"u8, RpcResponse.Null, null, cancellation);
            await Notify("$/cancelRequest"u8, RpcResponse.Json(writer => { writer.WriteStartObject(); writer.WriteString("id", "queued"); writer.WriteEndObject(); }));
            // A following response proves the read loop has handled the cancel before the ordered request can run.
            await peer.WriteAsync(configure.Id, default, new("[{},{},{},{}]"u8.ToArray()), null, cancellation);
            var registration = await Response(); Check(registration.Method == "client/registerCapability"u8, "Registration progresses while the handler awaits callbacks");
            await peer.WriteAsync(registration.Id, default, RpcResponse.Null, null, cancellation);
            var cancelled = await Response(); Check(cancelled.Id == cancelledId && cancelled.Error?.Code == -32800, "Queued requests can be cancelled out of band");

            var emptyId = new RpcId(default) { IsString = true };
            await peer.WriteAsync(emptyId, "unknown"u8, RpcResponse.Null, null, cancellation);
            var unknown = await Response(); Check(unknown.Id == emptyId && unknown.Error?.Code == -32601, "Empty string identity remains distinct from numeric zero");

            Utf8String initial = "const 名 = <組.件>😀</組.件>;\r\n"u8;
            await Notify("textDocument/didOpen"u8, RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); writer.WriteStartObject("textDocument"); writer.WriteString("uri", "file:///file.tsx");
                writer.WriteString("languageId", "typescriptreact"); writer.WriteNumber("version", 1); writer.WriteString("text", initial); writer.WriteEndObject(); writer.WriteEndObject();
            }));
            var queryIds = new List<(RpcId Id, RpcResponse Expected)>();
            Utf8String current = initial;
            for (int i = 0; i < 20; i++)
            {
                Utf8String next = i % 2 == 0 ? "const 名 = <別.組>😀</組.件>;\r\n"u8 : initial;
                var oldLines = new DocumentLineMap(current);
                var editRange = oldLines.ToRange(0, current.Length, encoding);
                await Notify("textDocument/didChange"u8, RpcResponse.Json(writer =>
                {
                    writer.WriteStartObject(); writer.WriteStartObject("textDocument"); writer.WriteString("uri", "file:///file.tsx"); writer.WriteNumber("version", i + 2);
                    writer.WriteEndObject(); writer.WriteStartArray("contentChanges"); writer.WriteStartObject(); writer.WritePropertyName("range"); LspJson.Range(writer, editRange);
                    writer.WriteString("text", next); writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
                }));
                var map = new DocumentLineMap(next);
                int offset = next.Span.IndexOf("<"u8) + 1;
                var position = map.ToPosition(offset, encoding);
                var query = RpcResponse.Json(writer =>
                {
                    writer.WriteStartObject(); writer.WriteStartObject("textDocument"); writer.WriteString("uri", "file:///file.tsx"); writer.WriteEndObject();
                    writer.WritePropertyName("position"); LspJson.Position(writer, position); writer.WriteEndObject();
                });
                var parsed = await Parser.ParseSourceFileAsync(new("/file.tsx"u8, ScriptKind.TSX), new(next), cancellation);
                var expected = LspJson.LinkedEditingRanges(await SyntaxLanguageService.GetLinkedEditingRangesAsync(parsed, offset, encoding, cancellation));
                var id = new RpcId(default, ++nextId); queryIds.Add((id, expected));
                await peer.WriteAsync(id, "textDocument/linkedEditingRange"u8, query, null, cancellation);
                current = next;
            }
            foreach (var (id, expected) in queryIds)
            {
                var response = await Response();
                Check(response.Id == id && response.Error is null && JsonElement.DeepEquals(Json(response.Data), Json(expected.Data)),
                    "Each queued query observes preceding edits and excludes following edits");
            }
            Check((await Request("shutdown"u8, RpcResponse.Null)).Error?.Code == -32602, "Shutdown rejects explicit null without closing the session");
            Check((await Request("shutdown"u8, default)).Error is null, "Shutdown completes after queued requests");
            Check((await Request("unknown"u8, RpcResponse.Null)).Error?.Code == -32600, "Requests after shutdown fail without hanging");
            await Notify("exit"u8, default);
            await running.WaitAsync(cancellation);
            Check(client.CanWrite, "Exit finishes with editor input still open");
        }
        foreach (var (bytes, expected) in new (byte[], string)[] { ([0xE2, 0x80], "��"), ([0xF0, 0x9F, 0x98], "���"), ([0xC0, 0xAF], "��") })
        {
            var result = RpcResponse.String(new(bytes));
            using var value = JsonDocument.Parse(result.Data);
            Check(value.RootElement.GetString() == expected, "JSON replaces each invalid UTF-8 byte independently");
        }
        return checks;
    }

    internal static async Task ServerAsync(Utf8String file)
    {
        using var input = JsonDocument.Parse(await File.ReadAllBytesAsync(file.ToString()));
        var files = input.RootElement.GetProperty("files").EnumerateObject()
            .ToDictionary(JsonStrings.GetName, property => JsonStrings.GetString(property.Value).Span.ToArray());
        var fs = new LibraryFileSystem(CreateFileSystem(input.RootElement, files));
        await LanguageServer.RunAsync(fs, Console.OpenStandardInput(), Console.OpenStandardOutput(), new()
        {
            CurrentDirectory = input.RootElement.TryGetProperty("cwd", out var cwd) ? JsonStrings.GetString(cwd) : "/"u8,
            InferredCompilerOptions = input.RootElement.TryGetProperty("inferredOptions", out var inferred) ? ApiJson.CompilerOptions(inferred) : null,
        });
    }

    internal static MemoryFileSystem CreateFileSystem(JsonElement input, IEnumerable<KeyValuePair<Utf8String, byte[]>>? files = null)
    {
        var fs = new MemoryFileSystem([], caseSensitive: !input.TryGetProperty("caseSensitive", out var sensitive) || sensitive.GetBoolean(),
            symbolicLinks: input.TryGetProperty("symlinks", out var links) && links.ValueKind == JsonValueKind.Object
                ? links.EnumerateObject().ToDictionary(JsonStrings.GetName, property => JsonStrings.GetString(property.Value)) : null);
        foreach (var (name, text) in files ?? input.GetProperty("files").EnumerateObject().Select(property => KeyValuePair.Create(JsonStrings.GetName(property), JsonStrings.GetString(property.Value).Span.ToArray())))
            fs.WriteFile(name, text);
        return fs;
    }
}

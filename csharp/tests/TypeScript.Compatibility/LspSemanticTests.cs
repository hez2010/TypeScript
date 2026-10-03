using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compatibility;

internal static class LspSemanticTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellation = timeout.Token;
        // Port the two server_semantictokens_test.go regressions through framed RPC.
        foreach (bool mixedCaseLibrary in new[] { false, true })
        {
            Utf8String disk = mixedCaseLibrary ? "console.log(\"hi\");"u8 : "var x\nvar x\nvar x\nvar x\nvar x\nvar x\nconst a = 1\n"u8;
            Dictionary<Utf8String, byte[]> files = new()
            {
                ["/home/projects/test.ts"u8] = disk.Span.ToArray(), ["/home/projects/other.ts"u8] = "export {}"u8.ToArray(),
                ["/home/projects/tsconfig.json"u8] = mixedCaseLibrary ? "{\"compilerOptions\":{\"lib\":[\"es5\"]}}"u8.ToArray() : "{}"u8.ToArray(),
            };
            if (mixedCaseLibrary) files["/TSLib/lib.es5.d.ts"u8] = """
                /// <reference no-default-lib="true"/>
                interface Boolean {}
                interface Function {}
                interface CallableFunction {}
                interface NewableFunction {}
                interface IArguments {}
                interface Number { toExponential: any; }
                interface Object {}
                interface RegExp {}
                interface String { charAt: any; }
                interface Array<T> { length: number; [n: number]: T; }
                interface ReadonlyArray<T> {}
                declare const console: { log(msg: any): void; };
                """u8.ToArray();
            IFileSystem fs = new MemoryFileSystem(files, caseSensitive: false);
            if (!mixedCaseLibrary) fs = new LibraryFileSystem(fs);
            var (server, client) = RpcTests.DuplexStream.Pair();
            var running = LanguageServer.RunAsync(fs, server, server, new()
            { CurrentDirectory = "/home/projects"u8, DefaultLibraryDirectory = mixedCaseLibrary ? "/TSLib"u8 : ((LibraryFileSystem)fs).LibraryDirectory }, cancellation);
            await using var peer = new RpcConnection(client, client, new Client());
            var receiving = peer.RunAsync(cancellation);
            async Task<JsonElement> Call(Utf8String method, RpcResponse parameters)
            {
                using var result = JsonDocument.Parse(await peer.CallAsync(method, parameters.Data, cancellation));
                return result.RootElement.Clone();
            }
            await Call("initialize"u8, RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); writer.WriteNull("processId"); writer.WriteNull("rootUri"); writer.WriteStartObject("capabilities"); writer.WriteStartObject("textDocument"); writer.WriteStartObject("semanticTokens");
                writer.WriteStartArray("formats"); writer.WriteStringValue("relative"); writer.WriteEndArray();
                writer.WriteStartObject("requests"); writer.WriteBoolean("full", true); writer.WriteEndObject();
                writer.WriteStartArray("tokenTypes"); foreach (var type in SemanticTokens.SupportedLegend.TokenTypes) JsonStrings.WriteString(writer, type); writer.WriteEndArray();
                writer.WriteStartArray("tokenModifiers"); foreach (var modifier in SemanticTokens.SupportedLegend.TokenModifiers) JsonStrings.WriteString(writer, modifier); writer.WriteEndArray();
                writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
            }));
            await peer.NotifyAsync("initialized"u8, "{}"u8.ToArray(), cancellation);
            async Task Open(Utf8String name, Utf8String text)
            {
                await peer.NotifyAsync("textDocument/didOpen"u8, RpcResponse.Json(writer =>
                {
                    writer.WriteStartObject(); writer.WriteStartObject("textDocument"); writer.WriteString("uri", DocumentUris.FromFileName(name));
                    writer.WriteString("languageId", "typescript"); writer.WriteNumber("version", 1); writer.WriteString("text", text); writer.WriteEndObject(); writer.WriteEndObject();
                }).Data, cancellation);
            }
            Task<JsonElement> Tokens(Utf8String name) => Call("textDocument/semanticTokens/full"u8, RpcResponse.Json(writer =>
            { writer.WriteStartObject(); writer.WriteStartObject("textDocument"); writer.WriteString("uri", DocumentUris.FromFileName(name)); writer.WriteEndObject(); writer.WriteEndObject(); }));
            if (!mixedCaseLibrary)
            {
                await Open("/home/projects/other.ts"u8, "export {}"u8);
                await Tokens("/home/projects/other.ts"u8);
            }
            await Open("/home/projects/test.ts"u8, mixedCaseLibrary ? disk : Utf8String.FromString(disk.ToString().Replace("\n", "\r\n")));
            var result = await Tokens("/home/projects/test.ts"u8);
            Check(result.ValueKind == JsonValueKind.Object, "Semantic tokens survive the server document transition");
            var data = result.GetProperty("data").EnumerateArray().Select(value => value.GetUInt32()).ToArray();
            Check(data.Length >= 5 && data.Length % 5 == 0, "Server emits complete semantic token tuples");
            if (mixedCaseLibrary) Check(Enumerable.Range(0, data.Length / 5).Any(index => (data[index * 5 + 4] & 512) != 0),
                "Mixed-case library names retain defaultLibrary on a case-insensitive host");
            else
            {
                Check(data.Length == 35, "CRLF overlay retains every LF-parsed declaration");
                Check(data.Where((_, index) => index % 5 == 0).SequenceEqual(new uint[] { 0, 1, 1, 1, 1, 1, 1 }), "Token lines use the active CRLF source");
            }
            await Call("shutdown"u8, default); await peer.NotifyAsync("exit"u8, ReadOnlyMemory<byte>.Empty, cancellation);
            await running.WaitAsync(cancellation); await receiving.WaitAsync(cancellation);
        }
        return checks;
    }
    private sealed class Client : IRpcHandler
    {
        public ValueTask<RpcResponse> HandleRequestAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation) => ValueTask.FromResult(RpcResponse.Null);
        public ValueTask HandleNotificationAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation) => default;
    }
}

using System.Text.Json;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Api;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compatibility;

internal static class LspFormattingTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellation = timeout.Token;
        foreach (bool configuration in new[] { false, true })
        {
            var (server, client) = RpcTests.DuplexStream.Pair();
            var running = LanguageServer.RunAsync(new MemoryFileSystem([], true), server, server, new() { CurrentDirectory = "/"u8 }, cancellation);
            await using var peer = new RpcConnection(client, client, new Client());
            var receiving = peer.RunAsync(cancellation);
            async Task<JsonElement> Call(Utf8String method, ReadOnlyMemory<byte> parameters)
            {
                using var result = JsonDocument.Parse(await peer.CallAsync(method, parameters, cancellation)); return result.RootElement.Clone();
            }
            var initialization = configuration
                ? """{"processId":null,"rootUri":null,"capabilities":{"workspace":{"configuration":true}}}"""u8.ToArray()
                : """{"processId":null,"rootUri":null,"capabilities":{},"initializationOptions":{"userPreferences":{"format":{"insertSpaceBeforeAndAfterBinaryOperators":false}}}}"""u8.ToArray();
            var initialized = await Call("initialize"u8, initialization);
            Check(initialized.GetProperty("capabilities").GetProperty("documentFormattingProvider").GetBoolean(), "Formatting capability");
            await peer.NotifyAsync("initialized"u8, "{}"u8.ToArray(), cancellation);
            await peer.NotifyAsync("textDocument/didOpen"u8, """{"textDocument":{"uri":"file:///file.ts","languageId":"typescript","version":1,"text":"const 名=1;"}}"""u8.ToArray(), cancellation);
            Task<JsonElement> Format() => Call("textDocument/formatting"u8, """{"textDocument":{"uri":"file:///file.ts"},"options":{"tabSize":4,"insertSpaces":true}}"""u8.ToArray());
            Check((await Format()).GetArrayLength() == 0, "Initialization or workspace precedence supplies formatting preferences");
            await peer.NotifyAsync("workspace/didChangeConfiguration"u8, """{"settings":{"js/ts":{"format":{"enabled":false}}}}"""u8.ToArray(), cancellation);
            Check((await Format()).ValueKind == JsonValueKind.Null, "Configuration disables formatting");
            await peer.NotifyAsync("workspace/didChangeConfiguration"u8, """{"settings":{}}"""u8.ToArray(), cancellation);
            Check((await Format()).GetArrayLength() == 2, "Removing configuration restores defaults");
            await peer.NotifyAsync("workspace/didChangeConfiguration"u8,
                """{"settings":{"javascript":{"format":{"insertSpaceBeforeAndAfterBinaryOperators":false}},"typescript":{"unstable":{"insertSpaceBeforeAndAfterBinaryOperators":false},"format":{"insertSpaceBeforeAndAfterBinaryOperators":true}}}}"""u8.ToArray(), cancellation);
            Check((await Format()).GetArrayLength() == 2, "Stable options override unstable and TypeScript overrides JavaScript");
            await peer.NotifyAsync("workspace/didChangeConfiguration"u8,
                """{"settings":{"js/ts":{"unstable":{"insertSpaceBeforeAndAfterBinaryOperators":false},"format":{"insertSpaceBeforeAndAfterBinaryOperators":null}}}}"""u8.ToArray(), cancellation);
            Check((await Format()).GetArrayLength() == 0, "Null preferences retain the preceding setting");
            await Call("shutdown"u8, ReadOnlyMemory<byte>.Empty); await peer.NotifyAsync("exit"u8, ReadOnlyMemory<byte>.Empty, cancellation);
            await running.WaitAsync(cancellation); await receiving.WaitAsync(cancellation);
        }
        await using (var session = new ProjectSession(new MemoryFileSystem([], true)))
        {
            session.Notify(new(FileChangeKind.Open, "/file.ts"u8, 1, "const value=1;"u8));
            session.Configure(new() { EnableFormatting = false, FormatCodeSettings = new() { IndentSize = 2 } });
            await using var before = await session.GetSnapshotAsync(["/file.ts"u8], cancellation);
            session.Configure(new() { FormatCodeSettings = new() { IndentSize = 8 } });
            await using var after = await session.GetSnapshotAsync(["/file.ts"u8], cancellation);
            Check(before.Snapshot.UserPreferences.EnableFormatting == false && before.Snapshot.UserPreferences.FormatCodeSettings.IndentSize == 2, "Pinned snapshots retain old preferences");
            Check(after.Snapshot.UserPreferences.EnableFormatting == true && after.Snapshot.UserPreferences.FormatCodeSettings.IndentSize == 8, "Preference-only changes publish a new snapshot");
            Check(ReferenceEquals(before.Snapshot.GetDefaultProject("/file.ts"u8)!.Program, after.Snapshot.GetDefaultProject("/file.ts"u8)!.Program), "Formatting changes reuse the program");
        }
        await using (var session = new ProjectSession(new MemoryFileSystem([], true)))
        {
            Utf8String target = "// 名😀\nfunction f() {\n}"u8;
            session.Notify(new(FileChangeKind.Open, "/file.ts"u8, 1, target));
            session.Configure(new() { EnableFormatting = false, FormatCodeSettings = new() { IndentSize = 2, InsertSpaceBeforeAndAfterBinaryOperators = false } });
            await using var api = new ApiSession(session);
            async Task<JsonElement> Call(Utf8String method, string parameters)
            {
                using var input = JsonDocument.Parse(parameters);
                using var result = JsonDocument.Parse((await api.HandleRequestAsync(method, input.RootElement, cancellation)).Data);
                return result.RootElement.Clone();
            }
            var before = await Call("getCurrentLanguageServerSnapshot"u8, "{}");
            ulong first = before.GetProperty("snapshot").GetUInt64();
            string project = before.GetProperty("projects")[0].GetProperty("id").GetString()!;
            var source = await Parser.ParseSourceFileAsync(new("/node.ts"u8), new("const 名 = 1;"u8));
            var packet = await AstEncoder.EncodeAsync(source.Statements![0], source, cancellation: cancellation);
            string data = Convert.ToBase64String(packet.Bytes);
            async Task<string?> Insert(ulong snapshot) => (await Call("formatNodeForInsertion"u8,
                $$"""{"snapshot":{{snapshot}},"project":"{{project}}","file":"/file.ts","position":{{target.Length - 1}},"data":"{{data}}"}""")).GetString();
            Check(await Insert(first) == "  const 名=1;", "Insertion uses the pinned API formatting options and UTF-8 position");
            session.Configure(new() { FormatCodeSettings = new() { IndentSize = 8 } });
            var after = await Call("getCurrentLanguageServerSnapshot"u8, "{}");
            Check(await Insert(after.GetProperty("snapshot").GetUInt64()) == "        const 名 = 1;", "Insertion observes new preferences in a new snapshot");
            Check(await Insert(first) == "  const 名=1;", "A later configuration cannot change insertion in an older snapshot");
        }
        return checks;
    }

    private sealed class Client : IRpcHandler
    {
        public ValueTask<RpcResponse> HandleRequestAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation) => ValueTask.FromResult(
            method == "workspace/configuration"u8
                ? new RpcResponse("""[{"format":{"insertSpaceBeforeAndAfterBinaryOperators":false}},{"format":{"insertSpaceBeforeAndAfterBinaryOperators":true}},{"format":{"insertSpaceBeforeAndAfterBinaryOperators":false}},{"tabSize":2,"insertSpaces":false}]"""u8.ToArray()) : RpcResponse.Null);
        public ValueTask HandleNotificationAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation) => default;
    }
}

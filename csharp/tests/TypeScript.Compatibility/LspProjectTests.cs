using System.Diagnostics;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compatibility;

internal static class LspProjectTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        static ContentMapper[] Parse(string text)
        { using var input = JsonDocument.Parse(text); return ContentMapperContributions.Parse(input.RootElement); }
        var mappers = Parse("""
            [{"contributorId":"publisher.extension","extensions":[".vue"],"inferredProjectContribution":{
                "options":{"mode":"embedded"},"manifest":{"name":"Vue mapper","version":"2.3.4","exec":["node","mapper.js"],"cwd":"/workspace/mapper","compilerOptions":["strict"]}}},
             {"contributorId":"publisher.extension","extensions":[".svelte"]}]
            """);
        Check(mappers.Length == 1 && mappers[0].Extensions.SequenceEqual(new Utf8String[] { ".vue"u8 }), "Only inline contributions claim extensions");
        Check(ContentMapperHost.Identity(mappers[0]) == "publisher.extension[0] (Vue mapper@2.3.4)"u8, "Contribution identity includes its index, name and version");
        Check(mappers[0].PackageDirectory == "/workspace/mapper"u8 && mappers[0].Options!.Value.GetProperty("mode").GetString() == "embedded", "Manifest cwd and options survive the input document");
        Check(mappers[0].Exec.SequenceEqual(new Utf8String[] { "node"u8, "mapper.js"u8 }) && mappers[0].CompilerOptions!.Value[0].GetString() == "strict", "Manifest execution and compiler options are retained");
        var minimal = Parse("""[{"contributorId":"publisher.extension","extensions":[".vue"],"inferredProjectContribution":{"manifest":{"name":"mapper","exec":["mapper"]}}}]""");
        var defaultOptions = minimal[0].Options!.Value;
        Check(defaultOptions.ValueKind == JsonValueKind.Object && !defaultOptions.EnumerateObject().Any(), "Missing options default to an owned empty object");
        foreach (var extensions in new[] { new[] { ".vue", ".vue" }, new[] { ".vue", ".VUE" } })
        {
            var inputs = RpcResponse.Json(writer =>
            {
                writer.WriteStartArray();
                foreach (var extension in extensions)
                {
                    writer.WriteStartObject(); writer.WriteString("contributorId", "extension"); writer.WriteStartArray("extensions");
                    writer.WriteStringValue(extension); writer.WriteEndArray(); writer.WriteStartObject("inferredProjectContribution"); writer.WriteStartObject("manifest");
                    writer.WriteString("name", "mapper"); writer.WriteStartArray("exec"); writer.WriteStringValue("mapper"); writer.WriteEndArray();
                    writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
                }
                writer.WriteEndArray();
            });
            using var input = JsonDocument.Parse(inputs.Data);
            try { ContentMapperContributions.Parse(input.RootElement); throw new InvalidOperationException("Accepted duplicate contribution"); }
            catch (RpcException error) { Check(error.Message.Contains("both claim extension", StringComparison.Ordinal), "Inline extension claims are case insensitive"); }
        }
        try { Parse("""[{"contributorId":"built-in","extensions":[".TS"]}]"""); throw new InvalidOperationException("Accepted built-in extension"); }
        catch (RpcException error) { Check(error.Message.Contains("invalid extension \".TS\"", StringComparison.Ordinal), "Native extensions cannot be contributed"); }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellation = timeout.Token;
        var (server, client) = RpcTests.DuplexStream.Pair(); await using var clientOwner = client;
        var running = LanguageServer.RunAsync(new MemoryFileSystem([], true), server, server, new() { CurrentDirectory = "/"u8 }, cancellation);
        var wire = new RpcWire(client, client, false, 1_000_000);
        var initialize = new RpcId(default, 1);
        await wire.WriteAsync(initialize, "initialize"u8, new("""
            {"processId":null,"rootUri":null,"capabilities":{"textDocument":{"synchronization":{"dynamicRegistration":true}}},"initializationOptions":{"runExternalCode":true,"disablePushDiagnostics":true}}
            """u8.ToArray()), null, cancellation);
        Check((await wire.ReadAsync(cancellation))?.Id == initialize, "Mapper cancellation client initializes");
        await wire.WriteAsync(null, "initialized"u8, new("{}"u8.ToArray()), null, cancellation);
        var config = await wire.ReadAsync(cancellation);
        Check(config?.Method == "client/registerCapability"u8, "Configuration registration is acknowledged");
        await wire.WriteAsync(config!.Id, default, RpcResponse.Null, null, cancellation);
        var contribution = new RpcId(default, 2);
        await wire.WriteAsync(contribution, "custom/setContentMapperContributions"u8, new("""
            {"openDocuments":[],"contributions":[{"contributorId":"extension","extensions":[".vue"],"inferredProjectContribution":{"manifest":{"name":"mapper","exec":["mapper"]}}}]}
            """u8.ToArray()), null, cancellation);
        var registration = await wire.ReadAsync(cancellation);
        Check(registration?.Method == "client/registerCapability"u8, "Mapper registration can remain pending independently of the request");
        await wire.WriteAsync(null, "$/cancelRequest"u8, new("{\"id\":2}"u8.ToArray()), null, cancellation);
        var cancelled = await wire.ReadAsync(cancellation);
        Check(cancelled?.Id == contribution && cancelled.Error?.Code == -32800, "A contribution request cancels while its client callback is pending");
        var shutdown = new RpcId(default, 3);
        await wire.WriteAsync(shutdown, "shutdown"u8, default, null, cancellation);
        var stopped = await wire.ReadAsync(cancellation);
        Check(stopped?.Id == shutdown && stopped.Error is null, "Shutdown joins the pending mapper registration without a client reply");
        await wire.WriteAsync(null, "exit"u8, default, null, cancellation);
        await running.WaitAsync(cancellation);
        return checks;
    }

    internal static async Task ServerAsync(Utf8String file)
    {
        using var input = JsonDocument.Parse(await File.ReadAllBytesAsync(file.ToString()));
        var memory = LspTests.CreateFileSystem(input.RootElement);
        await using var session = new LanguageServerSession(new LibraryFileSystem(memory), new()
        {
            CurrentDirectory = input.RootElement.TryGetProperty("cwd", out var cwd) ? JsonStrings.GetString(cwd) : "/"u8,
            ProgressDelay = input.RootElement.TryGetProperty("progressDelayMilliseconds", out var delay) ? TimeSpan.FromMilliseconds(delay.GetInt32()) : TimeSpan.Zero,
            StartMapperProcess = start =>
            {
                string fixture = Environment.GetEnvironmentVariable("CSHARP_CONTENT_MAPPER_FIXTURE")
                    ?? throw new InvalidOperationException("The pinned mapper fixture was not supplied.");
                start.ArgumentList.Insert(0, start.FileName); start.FileName = fixture;
                start.WorkingDirectory = Path.GetDirectoryName(fixture)!;
                return Process.Start(start)!;
            },
        });
        await using var connection = new RpcConnection(Console.OpenStandardInput(), Console.OpenStandardOutput(),
            new Handler(session, memory), new() { PreserveMessageOrder = true });
        session.Connect(connection);
        await connection.RunAsync();
    }

    private sealed class Handler(LanguageServerSession session, MemoryFileSystem memory) : IRpcHandler
    {
        public ValueTask<RpcResponse> HandleRequestAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation)
        {
            if (method != "$test/writeFiles"u8) return session.HandleRequestAsync(method, parameters, cancellation);
            using var input = JsonDocument.Parse(parameters);
            foreach (var file in input.RootElement.EnumerateObject())
                memory.WriteFile(JsonStrings.GetName(file), JsonStrings.GetString(file.Value).Span.ToArray());
            return ValueTask.FromResult(RpcResponse.Null);
        }
        public ValueTask HandleNotificationAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation)
            => session.HandleNotificationAsync(method, parameters, cancellation);
    }
}

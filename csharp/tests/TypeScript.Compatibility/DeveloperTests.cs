using System.IO.Compression;
using System.Text.Json;
using TypeScript.Compiler.Api;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compatibility;

internal static class DeveloperTests
{
    internal static async Task<int> SafetyAsync(Utf8String directory)
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var errors = new StringWriter();
        bool initialized = false;
        var messages = new List<(int Type, Utf8String Message)>();
        var logger = new ServerLogger(errors, () => initialized, (type, message) => messages.Add((type, message)));
        logger.MapperMessage("hidden"u8); Check(errors.ToString().Length == 0, "Mapper output requires trace verbosity");
        logger.SetVerbosity(1); logger.MapperMessage("visible"u8);
        Check(errors.ToString().Contains("visible", StringComparison.Ordinal), "Pre-initialization mapper trace uses stderr");
        initialized = true;
        for (int level = 0; level <= 5; level++)
        {
            logger.SetVerbosity(level);
            for (int type = 1; type <= 5; type++)
            {
                messages.Clear(); logger.Log(type, "message"u8);
                int threshold = type switch { 1 => 5, 2 => 4, 5 => 2, _ => 3 };
                Check(messages.Count == (level > 0 && level <= threshold ? 1 : 0), $"Log filtering at verbosity {level}, message type {type}");
            }
            messages.Clear(); logger.MapperMessage("mapper"u8);
            Check(messages.Count == (level == 1 ? 1 : 0), "Mapper messages are emitted only at trace verbosity");
        }

        var fs = new MemoryFileSystem([]);
        await using var api = new ApiSession(fs);
        async Task<JsonElement> Call(Utf8String method, Utf8String? target = null)
        {
            var parameters = RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); if (target is { } value) writer.WriteString("dir"u8, value.Span); writer.WriteEndObject();
            });
            var response = await ((IRpcHandler)api).HandleRequestAsync(method, parameters.Data, default);
            using var parsed = JsonDocument.Parse(response.Data); return parsed.RootElement.Clone();
        }
        async Task Error(Utf8String method, Utf8String? target, string expected)
        {
            try { await Call(method, target); throw new InvalidOperationException("Expected profile error"); }
            catch (ApiException error) { Check(error.Message == "api: client error: " + expected, error.Message); }
        }
        await Error("startCPUProfile"u8, null, "dir is required");
        await Error("saveHeapProfile"u8, ""u8, "dir is required");
        await Error("stopCPUProfile"u8, null, "failed to stop CPU profile: CPU profiling not in progress");
        Utf8String cpuDirectory = CompilerPath.Combine(directory, "api-cpu"u8);
        Check((await Call("startCPUProfile"u8, cpuDirectory)).ValueKind == JsonValueKind.Null, "CPU start returns null");
        Check(Directory.EnumerateFiles(cpuDirectory.ToString(), "*-cpuprofile.pb.gz").Count() == 1, "CPU start creates its result file immediately");
        await Error("startCPUProfile"u8, cpuDirectory, "failed to start CPU profile: CPU profiling already in progress");
        JsonElement stopped;
        using (NativeProfile.Enter("TypeScript.ProfileContract"u8))
            stopped = Call("stopCPUProfile"u8).GetAwaiter().GetResult();
        CheckProfile(stopped, "cpuprofile");
        await Error("stopCPUProfile"u8, null, "failed to stop CPU profile: CPU profiling not in progress");
        CheckProfile(await Call("saveHeapProfile"u8, CompilerPath.Combine(directory, "api-heap"u8)), "heapprofile");
        Utf8String disposedDirectory = CompilerPath.Combine(directory, "disposed-cpu"u8);
        await Call("startCPUProfile"u8, disposedDirectory); await api.DisposeAsync();
        using (var next = new OnDemandProfiler())
        {
            next.Start(CompilerPath.Combine(directory, "next-session"u8));
            var file = next.Stop(); Check(File.Exists(file.ToString()), "Disposal releases global CPU profiling ownership");
        }
        Check(Directory.EnumerateFiles(disposedDirectory.ToString()).All(file => new FileInfo(file).Length > 0), "Session disposal completes its owned CPU file");
        return checks;

        void CheckProfile(JsonElement response, string kind)
        {
            string file = response.GetProperty("file").GetString()!;
            Check(response.EnumerateObject().Count() == 1 && Path.GetFileName(file).EndsWith($"-{kind}.pb.gz", StringComparison.Ordinal), "Profile result contains the requested file path");
            using var bytes = File.OpenRead(file); using var gzip = new GZipStream(bytes, CompressionMode.Decompress);
            using var decoded = new MemoryStream(); gzip.CopyTo(decoded);
            Check(decoded.Length > 0, "The reported profile contains gzip-compressed protobuf bytes");
        }
    }
}

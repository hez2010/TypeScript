using System.Text.Json;
using System.Threading.Channels;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Protocol;
using static TypeScript.Compiler.LanguageServer.LspJson;

namespace TypeScript.Compiler.LanguageServer;

internal sealed partial class LanguageServerSession
{
    private readonly OnDemandProfiler profiler = new();
    private readonly Channel<(int Type, Utf8String Message)> logMessages = Channel.CreateUnbounded<(int, Utf8String)>(new() { SingleReader = true });
    private ServerLogger? serverLogger;
    private ServerLogger logger => serverLogger ??= new(options.ErrorWriter, () => state != 0, (type, message) => logMessages.Writer.TryWrite((type, message)));
    private Task logPublisher = Task.CompletedTask;

    private async Task PublishLogsAsync()
    {
        try
        {
            await foreach (var item in logMessages.Reader.ReadAllAsync(apiLifetime.Token).ConfigureAwait(false))
            {
                var parameters = RpcResponse.Json(writer =>
                {
                    writer.WriteStartObject(); writer.WriteNumber("type"u8, item.Type); String(writer, "message"u8, item.Message); writer.WriteEndObject();
                }).Data;
                await connection!.NotifyAsync("window/logMessage"u8, parameters, apiLifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (apiLifetime.IsCancellationRequested) { }
        catch (IOException) { }
    }

    private RpcResponse? DeveloperRequest(Utf8String method, JsonElement input)
    {
        if (method == "custom/runGC"u8)
        { GC.Collect(); logger.Log(3, "GC triggered"u8); return RpcResponse.Null; }
        if (method != "custom/startCPUProfile"u8 && method != "custom/stopCPUProfile"u8
            && method != "custom/saveHeapProfile"u8 && method != "custom/saveAllocProfile"u8) return null;
        Utf8String directory = String(input, "dir"u8);
        try
        {
            if (method == "custom/startCPUProfile"u8)
            {
                profiler.Start(directory); logger.Log(3, "CPU profiling started, will save to: "u8 + directory); return RpcResponse.Null;
            }
            Utf8String path = method == "custom/stopCPUProfile"u8 ? profiler.Stop()
                : OnDemandProfiler.Save(directory, method == "custom/saveAllocProfile"u8);
            Utf8String label = method == "custom/stopCPUProfile"u8 ? "CPU profile saved to: "u8
                : method == "custom/saveHeapProfile"u8 ? "Heap profile saved to: "u8 : "Allocation profile saved to: "u8;
            logger.Log(3, label + path);
            return RpcResponse.Json(writer => { writer.WriteStartObject(); String(writer, "file"u8, path); writer.WriteEndObject(); });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        { throw new RpcException(-32603, Utf8String.FromString(error.Message), error); }
    }
}

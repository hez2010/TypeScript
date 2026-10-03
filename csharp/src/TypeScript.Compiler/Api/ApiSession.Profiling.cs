using System.Text.Json;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compiler.Api;

public sealed partial class ApiSession
{
    private readonly OnDemandProfiler profiler = new();

    private RpcResponse? ProfileRequest(Utf8String method, JsonElement parameters)
    {
        if (method != "startCPUProfile"u8 && method != "stopCPUProfile"u8 && method != "saveHeapProfile"u8) return null;
        Utf8String directory = method == "stopCPUProfile"u8 ? default : ApiJson.String(parameters, "dir"u8);
        if (method != "stopCPUProfile"u8 && directory.IsEmpty) throw new ApiException("dir is required");
        try
        {
            if (method == "startCPUProfile"u8) { profiler.Start(directory); return RpcResponse.Null; }
            var path = method == "stopCPUProfile"u8 ? profiler.Stop() : OnDemandProfiler.Save(directory);
            return RpcResponse.Json(writer => { writer.WriteStartObject(); ApiJson.String(writer, "file"u8, path); writer.WriteEndObject(); });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            string operation = method == "startCPUProfile"u8 ? "start CPU" : method == "stopCPUProfile"u8 ? "stop CPU" : "save heap";
            throw new ApiException($"failed to {operation} profile: {error.Message}", inner: error);
        }
    }
}

using System.Text.Json;
using TypeScript.Compiler.Configuration;

namespace TypeScript.Compiler.Protocol;

/// <summary>An owned JSON response or a raw payload for the binary transport.</summary>
public readonly record struct RpcResponse(ReadOnlyMemory<byte> Data, bool IsBinary = false)
{
    public static RpcResponse Null => new("null"u8.ToArray());
    public static RpcResponse Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { MaxDepth = int.MaxValue })) write(writer);
        return new(stream.ToArray());
    }
    public static RpcResponse String(Utf8String value) => Json(writer => JsonStrings.WriteString(writer, value.Span));
    public static RpcResponse Boolean(bool value) => new(value ? "true"u8.ToArray() : "false"u8.ToArray());
}

public interface IRpcHandler
{
    ValueTask<RpcResponse> HandleRequestAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation);
    ValueTask HandleNotificationAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation);
}

public class RpcException(int code, Utf8String message, Exception? inner = null) : Exception(message.ToString(), inner)
{
    public int Code { get; } = code;
}

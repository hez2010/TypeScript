using System.Buffers;
using System.Globalization;
using System.Text.Json;
using TypeScript.Compiler.Configuration;

namespace TypeScript.Compiler.Protocol;

internal readonly record struct RpcId(Utf8String Text, int Number = 0)
{
    internal bool IsString { get; init; } = !Text.IsEmpty;
    internal static RpcId? Read(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        JsonValueKind.String => new(JsonStrings.GetString(value)) { IsString = true },
        JsonValueKind.Number when value.TryGetInt32(out int number) => new(default, number),
        _ => throw new InvalidDataException("Invalid JSON-RPC request identity"),
    };
    internal void Write(Utf8JsonWriter writer)
    { if (IsString) JsonStrings.WriteString(writer, Text.Span); else writer.WriteNumberValue(Number); }
    public override string ToString() => IsString ? Text.ToString() : Number.ToString(CultureInfo.InvariantCulture);
}

internal sealed record RpcMessage(RpcId? Id, Utf8String Method, ReadOnlyMemory<byte> Data, RpcException? Error = null)
{
    internal bool IsResponse => Id is not null && Method.IsEmpty;
}

/// <summary>Reads one stream sequentially. The connection serializes writes, including entire frames.</summary>
internal sealed class RpcWire(Stream input, Stream output, bool binary, int maximumPayloadLength)
{
    private readonly byte[] buffer = new byte[4096];
    private int position, available;
    private async ValueTask<int> ByteAsync(CancellationToken cancellation)
    {
        if (position == available)
        {
            available = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false); position = 0;
            if (available == 0) return -1;
        }
        return buffer[position++];
    }
    private async ValueTask<int> RequiredByteAsync(CancellationToken cancellation) =>
        await ByteAsync(cancellation).ConfigureAwait(false) is var value && value >= 0 ? value : throw new EndOfStreamException("Truncated RPC frame");
    private async ValueTask<byte[]> BytesAsync(long length, CancellationToken cancellation)
    {
        if (length < 0 || length > maximumPayloadLength || length > Array.MaxLength)
            throw new InvalidDataException("RPC payload length exceeds the configured limit");
        byte[] result = GC.AllocateUninitializedArray<byte>((int)length);
        int count = Math.Min(result.Length, available - position);
        buffer.AsSpan(position, count).CopyTo(result); position += count;
        await input.ReadExactlyAsync(result.AsMemory(count), cancellation).ConfigureAwait(false);
        return result;
    }
    internal async ValueTask<RpcMessage?> ReadAsync(CancellationToken cancellation)
    {
        if (binary)
        {
            int marker = await ByteAsync(cancellation).ConfigureAwait(false);
            if (marker < 0) return null;
            if (marker != 0x93) throw new InvalidDataException("Expected MessagePack fixed 3-element array");
            int type = await RequiredByteAsync(cancellation).ConfigureAwait(false);
            if (type == 0xCC) type = await RequiredByteAsync(cancellation).ConfigureAwait(false);
            if (type is < 1 or > 6) throw new InvalidDataException("Unknown MessagePack message type");
            Utf8String method = new(await BinaryAsync(cancellation).ConfigureAwait(false));
            byte[] payload = await BinaryAsync(cancellation).ConfigureAwait(false);
            return type switch
            {
                1 => new(new(method), method, payload),
                2 => new(new(method), default, payload),
                3 => new(new(method), default, default, new(-32603, new(payload))),
                _ => throw new InvalidDataException($"Unexpected MessagePack message type: {type}"),
            };
        }
        long length = 0;
        bool first = true;
        while (true)
        {
            var line = new ArrayBufferWriter<byte>();
            while (true)
            {
                int value = await ByteAsync(cancellation).ConfigureAwait(false);
                if (value < 0)
                {
                    if (first && line.WrittenCount == 0) return null;
                    throw new EndOfStreamException("Truncated RPC header");
                }
                first = false;
                line.Write([(byte)value]);
                if (value == '\n') break;
                if (line.WrittenCount > maximumPayloadLength) throw new InvalidDataException("RPC header exceeds the configured limit");
            }
            if (line.WrittenSpan.SequenceEqual("\r\n"u8)) break;
            int colon = line.WrittenSpan.IndexOf((byte)':');
            if (colon < 0) throw new InvalidDataException("Invalid JSON-RPC header");
            if (line.WrittenSpan[..colon].SequenceEqual("Content-Length"u8)
                && (!long.TryParse(line.WrittenSpan[(colon + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out length) || length < 0))
                throw new InvalidDataException("Invalid JSON-RPC content length");
        }
        if (length <= 0) throw new InvalidDataException("Missing JSON-RPC content length");
        var bytes = await BytesAsync(length, cancellation).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = int.MaxValue });
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null) return new(null, default, default);
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid JSON-RPC message");
        if (root.TryGetProperty("jsonrpc"u8, out var version) && !version.ValueEquals("2.0"u8))
            throw new InvalidDataException("Invalid JSON-RPC version");
        var id = root.TryGetProperty("id"u8, out var rawId) ? RpcId.Read(rawId) : null;
        Utf8String methodName = root.TryGetProperty("method"u8, out var rawMethod) && rawMethod.ValueKind != JsonValueKind.Null
            ? JsonStrings.GetString(rawMethod) : default;
        var dataName = id is not null && methodName.IsEmpty ? "result"u8 : "params"u8;
        ReadOnlyMemory<byte> data = root.TryGetProperty(dataName, out var dataValue) ? JsonStrings.Raw(dataValue).Memory : default;
        RpcException? error = null;
        if (root.TryGetProperty("error"u8, out var rawError) && rawError.ValueKind != JsonValueKind.Null)
        {
            int code = rawError.GetProperty("code"u8).GetInt32();
            var message = JsonStrings.GetString(rawError.GetProperty("message"u8));
            error = new(code, message);
        }
        return new(id, methodName, data, error);
    }
    private async ValueTask<byte[]> BinaryAsync(CancellationToken cancellation)
    {
        int marker = await RequiredByteAsync(cancellation).ConfigureAwait(false);
        int width = marker switch { 0xC4 => 1, 0xC5 => 2, 0xC6 => 4, _ => throw new InvalidDataException("Expected MessagePack binary data") };
        uint length = 0;
        for (int i = 0; i < width; i++) length = length << 8 | (uint)await RequiredByteAsync(cancellation).ConfigureAwait(false);
        return await BytesAsync(length, cancellation).ConfigureAwait(false);
    }
    internal async ValueTask WriteAsync(RpcId? id, Utf8String method, RpcResponse response, RpcException? error, CancellationToken cancellation)
    {
        if (binary)
        {
            var frame = new ArrayBufferWriter<byte>();
            MessagePackWriter.Array(frame, 3);
            MessagePackWriter.UInt(frame, !method.IsEmpty ? 6u : error is null ? 4u : 5u);
            MessagePackWriter.Binary(frame, !method.IsEmpty ? method.Span : id is { } identity
                ? !identity.Text.IsEmpty ? identity.Text.Span : Utf8String.Format(identity.Number).Span : []);
            ReadOnlyMemory<byte> payload = error is null ? response.Data : Utf8String.FromString(error.Message).Memory;
            MessagePackWriter.BinaryHeader(frame, payload.Length);
            await output.WriteAsync(frame.WrittenMemory, cancellation).ConfigureAwait(false);
            await output.WriteAsync(payload, cancellation).ConfigureAwait(false);
        }
        else
        {
            var json = RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); writer.WriteString("jsonrpc"u8, "2.0"u8);
                if (id is { } identity) { writer.WritePropertyName("id"u8); identity.Write(writer); }
                if (!method.IsEmpty)
                {
                    writer.WritePropertyName("method"u8); JsonStrings.WriteString(writer, method.Span);
                    if (!response.Data.IsEmpty) { writer.WritePropertyName("params"u8); WriteData(writer, response); }
                }
                else if (error is not null)
                {
                    writer.WriteStartObject("error"u8); writer.WriteNumber("code"u8, error.Code);
                    writer.WritePropertyName("message"u8); JsonStrings.WriteString(writer, Utf8String.FromString(error.Message).Span); writer.WriteEndObject();
                }
                else { writer.WritePropertyName("result"u8); WriteData(writer, response); }
                writer.WriteEndObject();
            });
            Utf8String header = Utf8String.Concat("Content-Length: "u8, Utf8String.Format(json.Data.Length), "\r\n\r\n"u8);
            await output.WriteAsync(header.Memory, cancellation).ConfigureAwait(false);
            await output.WriteAsync(json.Data, cancellation).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellation).ConfigureAwait(false);
    }
    private static void WriteData(Utf8JsonWriter writer, RpcResponse response)
    {
        if (response.IsBinary) throw new InvalidOperationException("JSON-RPC requires JSON responses");
        if (response.Data.IsEmpty) writer.WriteNullValue();
        else writer.WriteRawValue(response.Data.Span);
    }
}

using TypeScript.Compiler.Protocol;
using System.Text.Json;
using TypeScript.Compiler.Configuration;

namespace TypeScript.Compiler.Api;

public sealed class ApiException(string message, bool invalidRequest = false, Exception? inner = null)
    : RpcException(-32603, Utf8String.FromString((invalidRequest ? "api: invalid request: " : "api: client error: ") + message), inner)
{
    public bool InvalidRequest { get; } = invalidRequest;
}

internal static class ApiJson
{
    internal static JsonElement Get(JsonElement value, ReadOnlySpan<byte> name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) ? property : default;
    internal static Utf8String String(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        ? default : value.ValueKind == JsonValueKind.String ? JsonStrings.GetString(value) : throw new ApiException("Expected a string", true);
    internal static Utf8String String(JsonElement value, ReadOnlySpan<byte> name) => String(Get(value, name));
    internal static bool Boolean(JsonElement value, ReadOnlySpan<byte> name) => Get(value, name).ValueKind switch
    { JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false, JsonValueKind.True => true, _ => throw new ApiException("Expected a boolean", true) };
    internal static ulong UInt64(JsonElement value, ReadOnlySpan<byte> name) => UInt64(Get(value, name));
    internal static ulong UInt64(JsonElement number)
    {
        return number.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? 0 : number.ValueKind == JsonValueKind.Number && number.TryGetUInt64(out var result)
            ? result : throw new ApiException("Expected an unsigned integer", true);
    }
    internal static uint UInt32(JsonElement value, ReadOnlySpan<byte> name) => UInt32(Get(value, name));
    internal static uint UInt32(JsonElement value)
    {
        ulong result = UInt64(value);
        return result <= uint.MaxValue ? (uint)result : throw new ApiException("Expected an unsigned 32-bit integer", true);
    }
    internal static int Int32(JsonElement value, ReadOnlySpan<byte> name)
    {
        var number = Get(value, name);
        return number.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? 0 : number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var result)
            ? result : throw new ApiException("Expected an integer", true);
    }
    internal static JsonElement[] Array(JsonElement value) => value.ValueKind switch
    { JsonValueKind.Null or JsonValueKind.Undefined => [], JsonValueKind.Array => value.EnumerateArray().ToArray(), _ => throw new ApiException("Expected an array", true) };
    internal static JsonElement[] Array(JsonElement value, ReadOnlySpan<byte> name) => Array(Get(value, name));
    internal static Utf8String[] Strings(JsonElement value, ReadOnlySpan<byte> name) => Array(value, name).Select(String).ToArray();
    internal static void String(Utf8JsonWriter writer, ReadOnlySpan<byte> name, Utf8String value)
    { writer.WritePropertyName(name); JsonStrings.WriteString(writer, value.Span); }
    internal static void Strings(Utf8JsonWriter writer, IEnumerable<Utf8String> values)
    { writer.WriteStartArray(); foreach (var value in values) JsonStrings.WriteString(writer, value.Span); writer.WriteEndArray(); }
    internal static void Strings(Utf8JsonWriter writer, ReadOnlySpan<byte> name, IEnumerable<Utf8String> values)
    { writer.WritePropertyName(name); Strings(writer, values); }
    internal static void Write(Utf8JsonWriter writer, JsonElement value)
    { if (value.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue(); else JsonStrings.WriteValue(writer, value); }
    internal static CompilerOptions CompilerOptions(JsonElement value)
    {
        var result = new CompilerOptions();
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return result;
        if (value.ValueKind != JsonValueKind.Object) throw new ApiException("Expected compiler options", true);
        foreach (var property in value.EnumerateObject()) result.Set(JsonStrings.GetName(property), property.Value);
        return result;
    }
}

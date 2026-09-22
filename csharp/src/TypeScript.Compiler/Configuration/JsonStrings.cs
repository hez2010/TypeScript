using System.Globalization;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Configuration;

/// <summary>Preserves UTF-16 path code units at the System.Text.Json Unicode-scalar boundary.</summary>
public static class JsonStrings
{
    public static string GetString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException("Expected a JSON string");
        try { return value.GetString()!; }
        catch (InvalidOperationException) { return DecodeLiteral(value.GetRawText()); }
    }
    public static string GetName(JsonProperty property)
    {
        try { return property.Name; }
        catch (InvalidOperationException) { return DecodeLiteral(property.ToString()); }
    }
    private static string DecodeLiteral(string json)
    {
        var scanner = new Scanner(new SourceText(json));
        if (scanner.Scan() != SyntaxKind.StringLiteral) throw new InvalidOperationException("Expected an encoded JSON string");
        return scanner.Value;
    }
    private static int Unpaired(string value, int start)
    {
        while (start < value.Length)
        {
            int offset = value.AsSpan(start).IndexOfAnyInRange('\uD800', '\uDFFF');
            if (offset < 0) return -1;
            start += offset;
            if (!char.IsHighSurrogate(value[start]) || start + 1 == value.Length || !char.IsLowSurrogate(value[start + 1])) return start;
            start += 2;
        }
        return -1;
    }
    private static byte[] Encode(string value)
    {
        using var stream = new MemoryStream(); stream.WriteByte((byte)'"');
        int start = 0;
        for (int unpaired = Unpaired(value, start); unpaired >= 0; unpaired = Unpaired(value, start))
        {
            stream.Write(JsonEncodedText.Encode(value.AsSpan(start, unpaired - start)).EncodedUtf8Bytes);
            stream.Write(Encoding.ASCII.GetBytes("\\u" + ((int)value[unpaired]).ToString("X4", CultureInfo.InvariantCulture)));
            start = unpaired + 1;
        }
        stream.Write(JsonEncodedText.Encode(value.AsSpan(start)).EncodedUtf8Bytes);
        stream.WriteByte((byte)'"'); return stream.ToArray();
    }
    internal static void WriteString(Utf8JsonWriter writer, string value)
    {
        if (Unpaired(value, 0) < 0) writer.WriteStringValue(value);
        else writer.WriteRawValue(Encode(value), skipInputValidation: true);
    }
    internal static void WriteName(Utf8JsonWriter writer, string name, List<(int Start, int End, string Name)> patches)
    {
        // Both callers use compact writers. Let the BCL write the object structure,
        // then replace only the quoted key spelling that it cannot encode losslessly.
        int start = checked((int)(writer.BytesCommitted + writer.BytesPending));
        writer.WritePropertyName(name);
        if (Unpaired(name, 0) >= 0) patches.Add((start, checked((int)(writer.BytesCommitted + writer.BytesPending)) - 1, name));
    }
    internal static JsonElement Parse(MemoryStream stream, List<(int Start, int End, string Name)>? namePatches = null)
    {
        ReadOnlyMemory<byte> bytes = stream.GetBuffer().AsMemory(0, checked((int)stream.Length));
        using var patched = namePatches is { Count: > 0 } ? new MemoryStream() : null;
        if (patched is not null)
        {
            int previous = 0;
            foreach (var (start, end, name) in namePatches!)
            {
                int nameStart = bytes.Span[start] == ',' ? start + 1 : start;
                patched.Write(bytes.Span[previous..nameStart]); patched.Write(Encode(name)); previous = end;
            }
            patched.Write(bytes.Span[previous..]); bytes = patched.GetBuffer().AsMemory(0, checked((int)patched.Length));
        }
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = int.MaxValue });
        return document.RootElement.Clone();
    }
    internal static void WriteValue(Utf8JsonWriter writer, JsonElement value) => writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
}

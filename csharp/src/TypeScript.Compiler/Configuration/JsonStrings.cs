using System.Globalization;
using System.Buffers;
using System.Text.Json;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Configuration;

/// <summary>Reads and writes UTF-8 JSON text, preserving WTF-8 string values.</summary>
public static class JsonStrings
{
    // JsonElement exposes raw text only as UTF-16 publicly. These .NET 11
    // accessors retain the BCL parser and its original UTF-8 spelling, including lone-surrogate escapes.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetRawValue")]
    private static extern ReadOnlyMemory<byte> RawValue(ref JsonElement value);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetPropertyNameRaw")]
    private static extern ReadOnlySpan<byte> RawName(ref JsonElement value);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "WritePropertyNameHelper")]
    internal static extern void WriteEncodedName(Utf8JsonWriter writer, ReadOnlySpan<byte> escapedName);

    public static Utf8String GetString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Expected a JSON string");
        var reader = new Utf8JsonReader(RawValue(ref value).Span);
        reader.Read();
        return ReadString(ref reader);
    }

    public static Utf8String GetName(JsonProperty property)
    {
        var value = property.Value;
        ReadOnlySpan<byte> name = RawName(ref value);
        if (!name.Contains((byte)'\\'))
            return Utf8String.Copy(name);
        Utf8String json = Utf8String.Concat("\""u8, name, "\""u8);
        var reader = new Utf8JsonReader(json.Span);
        reader.Read();
        return ReadString(ref reader);
    }

    // JsonDocument may own pooled buffers. Returned text must outlive its document.
    public static Utf8String Raw(JsonElement value) => Utf8String.Copy(RawValue(ref value).Span);

    internal static Utf8String ReadString(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is not JsonTokenType.String and not JsonTokenType.PropertyName)
            throw new JsonException("Expected a JSON string");
        if (!reader.ValueIsEscaped)
            return reader.HasValueSequence ? new(reader.ValueSequence.ToArray()) : Utf8String.Copy(reader.ValueSpan);
        byte[] bytes = new byte[reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length];
        try
        { return new(bytes.AsMemory(0, reader.CopyString(bytes))); }
        catch (InvalidOperationException)
        {
            ReadOnlySpan<byte> raw = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
            return DecodeLiteral(Utf8String.Concat("\""u8, raw, "\""u8));
        }
    }

    private static Utf8String DecodeLiteral(Utf8String json)
    {
        var scanner = new Scanner(new SourceText(json));
        if (scanner.Scan() != SyntaxKind.StringLiteral)
            throw new InvalidOperationException("Expected an encoded JSON string");
        return scanner.Value;
    }

    private static int NonUnicode(ReadOnlySpan<byte> value, int start)
    {
        while (start < value.Length)
        {
            int point = Wtf8.Decode(value[start..], out int width);
            if (point is >= 0xD800 and <= 0xDFFF || point == 0xFFFD && width == 1)
                return start;
            start += width;
        }
        return -1;
    }

    private static byte[] Encode(ReadOnlySpan<byte> value)
    {
        using var stream = new MemoryStream();
        stream.WriteByte((byte)'"');
        int start = 0;
        Span<byte> escape = stackalloc byte[6];
        "\\u"u8.CopyTo(escape);
        for (int unpaired = NonUnicode(value, start); unpaired >= 0; unpaired = NonUnicode(value, start))
        {
            stream.Write(JsonEncodedText.Encode(value.Slice(start, unpaired - start)).EncodedUtf8Bytes);
            int point = Wtf8.Decode(value[unpaired..], out int width);
            point.TryFormat(escape[2..], out _, "X4", CultureInfo.InvariantCulture);
            stream.Write(escape);
            start = unpaired + width;
        }
        stream.Write(JsonEncodedText.Encode(value[start..]).EncodedUtf8Bytes);
        stream.WriteByte((byte)'"');
        return stream.ToArray();
    }

    internal static void WriteString(Utf8JsonWriter writer, ReadOnlySpan<byte> value)
    {
        if (System.Text.Unicode.Utf8.IsValid(value))
            writer.WriteStringValue(value);
        else
            writer.WriteRawValue(Encode(value), skipInputValidation: true);
    }

    internal static void WriteName(Utf8JsonWriter writer, Utf8String name)
    {
        if (System.Text.Unicode.Utf8.IsValid(name))
            writer.WritePropertyName(name.Span);
        else
        {
            byte[] encoded = Encode(name);
            WriteEncodedName(writer, encoded.AsSpan(1, encoded.Length - 2));
        }
    }

    internal static JsonElement Parse(MemoryStream stream)
    {
        ReadOnlyMemory<byte> bytes = stream.GetBuffer().AsMemory(0, checked((int)stream.Length));
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = int.MaxValue });
        return document.RootElement.Clone();
    }

    internal static void WriteValue(Utf8JsonWriter writer, JsonElement value) =>
        writer.WriteRawValue(RawValue(ref value).Span, skipInputValidation: true);
}

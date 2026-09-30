using System.Text.Json;
using System.Text.Json.Serialization;
using TypeScript.Compiler.Configuration;

namespace TypeScript.Compiler.Text;

internal sealed class Utf8StringJsonConverter : JsonConverter<Utf8String>
{
    public override Utf8String Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => JsonStrings.ReadString(ref reader);

    public override void Write(Utf8JsonWriter writer, Utf8String value, JsonSerializerOptions options) =>
        JsonStrings.WriteString(writer, value.Span);

    public override Utf8String ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => JsonStrings.ReadString(ref reader);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Utf8String value, JsonSerializerOptions options) =>
        JsonStrings.WriteName(writer, value);
}

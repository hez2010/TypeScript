using System.Text.Json;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compiler.LanguageServer;

internal static partial class LspJson
{
    internal static DiagnosticClientOptions DiagnosticOptions(JsonElement capabilities, Utf8String? locale = null, bool push = false)
    {
        var options = Get(Get(capabilities, "textDocument"u8), push ? "publishDiagnostics"u8 : "diagnostic"u8);
        return new(Boolean(options, "relatedInformation"u8), Array(Get(Get(options, "tagSupport"u8), "valueSet"u8)).Select(item => item.GetInt32()).ToArray(),
            Boolean(capabilities, "_vs_supportsVisualStudioExtensions"u8), locale);
    }

    internal static RpcResponse DocumentDiagnostics(IReadOnlyList<DocumentDiagnostic> diagnostics, bool visualStudio = false) => RpcResponse.Json(writer =>
    {
        writer.WriteStartObject(); writer.WriteString("kind"u8, "full"u8); writer.WritePropertyName("items"u8);
        WriteDiagnostics(writer, diagnostics, visualStudio); writer.WriteEndObject();
    });

    internal static void WriteDiagnostics(Utf8JsonWriter writer, IReadOnlyList<DocumentDiagnostic> diagnostics, bool visualStudio)
    {
        writer.WriteStartArray();
        foreach (var diagnostic in diagnostics)
        {
            writer.WriteStartObject(); writer.WritePropertyName("range"u8); Range(writer, diagnostic.Range);
            if (visualStudio) writer.WriteString("code"u8, $"TS{diagnostic.Code}"); else writer.WriteNumber("code"u8, diagnostic.Code);
            writer.WriteNumber("severity"u8, diagnostic.Severity); String(writer, "message"u8, diagnostic.Message); String(writer, "source"u8, diagnostic.Source);
            if (diagnostic.RelatedInformation.Count != 0)
            {
                writer.WriteStartArray("relatedInformation"u8);
                foreach (var related in diagnostic.RelatedInformation)
                {
                    writer.WriteStartObject(); writer.WriteStartObject("location"u8); String(writer, "uri"u8, related.Location.Uri);
                    writer.WritePropertyName("range"u8); Range(writer, related.Location.Range); writer.WriteEndObject();
                    String(writer, "message"u8, related.Message); writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (diagnostic.Tags.Count != 0)
            { writer.WriteStartArray("tags"u8); foreach (int tag in diagnostic.Tags) writer.WriteNumberValue(tag); writer.WriteEndArray(); }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

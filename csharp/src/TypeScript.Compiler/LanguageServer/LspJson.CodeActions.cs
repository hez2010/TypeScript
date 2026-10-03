using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compiler.LanguageServer;

internal static partial class LspJson
{
    internal static CodeActionContext? CodeActionContext(JsonElement value) => value.ValueKind != JsonValueKind.Object ? null : new(
        Get(value, "diagnostics"u8).ValueKind != JsonValueKind.Array ? null : Array(Get(value, "diagnostics"u8)).Select(diagnostic => new CodeActionDiagnostic(
            Range(Get(diagnostic, "range"u8)), Get(diagnostic, "code"u8) is { ValueKind: JsonValueKind.Number } code && code.TryGetInt32(out int number) ? number : null,
            Get(diagnostic, "source"u8) is { ValueKind: JsonValueKind.String } source ? JsonStrings.GetString(source) : null,
            String(diagnostic, "message"u8), diagnostic.Clone())).ToArray(),
        Get(value, "only"u8).ValueKind != JsonValueKind.Array ? null : Array(Get(value, "only"u8)).Select(JsonStrings.GetString).ToArray());

    internal static RpcResponse CodeActions(IReadOnlyList<CodeAction>? actions) => RpcResponse.Json(writer =>
    {
        if (actions is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var action in actions)
        {
            writer.WriteStartObject(); String(writer, "title"u8, action.Title); String(writer, "kind"u8, action.Kind);
            if (action.Diagnostic is { } diagnostic)
            { writer.WriteStartArray("diagnostics"u8); diagnostic.Data.WriteTo(writer); writer.WriteEndArray(); }
            writer.WriteStartObject("edit"u8); writer.WriteStartObject("changes"u8);
            foreach (var (uri, edits) in action.Changes)
            {
                writer.WritePropertyName(uri.Span); writer.WriteStartArray();
                foreach (var edit in edits)
                {
                    writer.WriteStartObject(); writer.WritePropertyName("range"u8); Range(writer, edit.Range);
                    String(writer, "newText"u8, edit.NewText); writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });
}

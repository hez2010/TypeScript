using System.Text.Json;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compiler.LanguageServer;

internal static partial class LspJson
{
    internal static CompletionCapabilities CompletionOptions(JsonElement capabilities)
    {
        var item = Get(Get(Get(capabilities, "textDocument"u8), "completion"u8), "completionItem"u8);
        var defaults = Array(Get(Get(Get(Get(capabilities, "textDocument"u8), "completion"u8), "completionList"u8), "itemDefaults"u8));
        return new(Boolean(item, "snippetSupport"u8), Boolean(item, "commitCharactersSupport"u8), Boolean(item, "insertReplaceSupport"u8),
            defaults.Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "commitCharacters"),
            defaults.Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "editRange"),
            String(Array(Get(item, "documentationFormat"u8)).FirstOrDefault()) == "markdown"u8,
            Boolean(item, "labelDetailsSupport"u8));
    }
    internal static RpcResponse Completions(CompletionList? list) => RpcResponse.Json(writer =>
    {
        if (list is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject(); writer.WriteBoolean("isIncomplete"u8, list.IsIncomplete);
        if (!list.IsIncomplete || list.Items.Length != 0)
        {
            writer.WriteStartArray("items"u8);
            foreach (var item in list.Items) WriteCompletion(writer, item);
            writer.WriteEndArray();
        }
        if (list.ItemDefaults is { } defaults)
        {
            writer.WriteStartObject("itemDefaults"u8);
            if (defaults.CommitCharacters is { } characters)
            { writer.WriteStartArray("commitCharacters"u8); foreach (var character in characters) Configuration.JsonStrings.WriteString(writer, character); writer.WriteEndArray(); }
            if (defaults.Insert is { } insert && defaults.Replace is { } replace)
            {
                writer.WriteStartObject("editRange"u8); writer.WritePropertyName("insert"u8); Range(writer, insert);
                writer.WritePropertyName("replace"u8); Range(writer, replace); writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    });

    internal static RpcResponse Completion(CompletionItem item) => RpcResponse.Json(writer => WriteCompletion(writer, item));

    private static void WriteCompletion(Utf8JsonWriter writer, CompletionItem item)
    {
        writer.WriteStartObject(); String(writer, "label"u8, item.Label);
        if (item.Kind is { } kind) writer.WriteNumber("kind"u8, kind);
        if (item.Detail is { } detail) String(writer, "detail"u8, detail);
        if (item.SortText is { } sortText) String(writer, "sortText"u8, sortText);
        if (item.InsertText is { } insertText) String(writer, "insertText"u8, insertText);
        if (item.FilterText is { } filterText) String(writer, "filterText"u8, filterText);
        if (item.Preselect is { } preselect) writer.WriteBoolean("preselect"u8, preselect);
        if (item.Tags is { } tags)
        { writer.WriteStartArray("tags"u8); foreach (int tag in tags) writer.WriteNumberValue(tag); writer.WriteEndArray(); }
        if (item.LabelDetails is { } labelDetails)
        {
            writer.WriteStartObject("labelDetails"u8);
            if (labelDetails.Detail is { } labelDetail) String(writer, "detail"u8, labelDetail);
            if (labelDetails.Description is { } description) String(writer, "description"u8, description);
            writer.WriteEndObject();
        }
        if (item.Documentation is { } documentation)
        {
            writer.WritePropertyName("documentation"u8);
            if (documentation.Kind is null) Configuration.JsonStrings.WriteString(writer, documentation.Value);
            else { writer.WriteStartObject(); String(writer, "kind"u8, documentation.Kind.Value); String(writer, "value"u8, documentation.Value); writer.WriteEndObject(); }
        }
        if (item.AdditionalTextEdits is { } edits)
        {
            writer.WriteStartArray("additionalTextEdits"u8);
            foreach (var change in edits)
            { writer.WriteStartObject(); writer.WritePropertyName("range"u8); Range(writer, change.Range); String(writer, "newText"u8, change.NewText); writer.WriteEndObject(); }
            writer.WriteEndArray();
        }
        if (item.InsertTextFormat is { } format) writer.WriteNumber("insertTextFormat"u8, format);
        if (item.TextEdit is { } edit)
        {
            writer.WriteStartObject("textEdit"u8); String(writer, "newText"u8, edit.NewText);
            writer.WritePropertyName(edit.Replace is null ? "range"u8 : "insert"u8); Range(writer, edit.Insert);
            if (edit.Replace is { } replace) { writer.WritePropertyName("replace"u8); Range(writer, replace); }
            writer.WriteEndObject();
        }
        if (item.CommitCharacters is { } characters)
        { writer.WriteStartArray("commitCharacters"u8); foreach (var character in characters) Configuration.JsonStrings.WriteString(writer, character); writer.WriteEndArray(); }
        if (item.Data is { } data)
        {
            writer.WriteStartObject("data"u8);
            if (!data.FileName.IsEmpty) String(writer, "fileName"u8, data.FileName);
            if (data.Position != 0) writer.WriteNumber("position"u8, data.Position);
            if (!data.Name.IsEmpty) String(writer, "name"u8, data.Name);
            if (!data.Source.IsEmpty) String(writer, "source"u8, data.Source);
            if (data.SupplementalFileIndex is { } index) writer.WriteNumber("supplementalFileIndex"u8, index);
            if (data.IsImportStatementCompletion) writer.WriteBoolean("isImportStatementCompletion"u8, true);
            if (data.AutoImport is { } fix) WriteAutoImport(writer, fix);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    internal static CompletionItem ReadCompletion(JsonElement value)
    {
        var data = Get(value, "data"u8);
        if (data.ValueKind != JsonValueKind.Object) throw new RpcException(-32603, "completion item data is nil"u8);
        var edit = Get(value, "textEdit"u8);
        var replace = Get(edit, "replace"u8);
        var characters = Get(value, "commitCharacters"u8);
        var labelDetails = Get(value, "labelDetails"u8);
        var documentation = Get(value, "documentation"u8);
        var edits = Get(value, "additionalTextEdits"u8);
        var tags = Get(value, "tags"u8);
        return new(String(value, "label"u8), Number("kind"u8), Text("detail"u8), Text("sortText"u8), Number("insertTextFormat"u8),
            edit.ValueKind != JsonValueKind.Object ? null : new(String(edit, "newText"u8),
                Range(Get(edit, replace.ValueKind == JsonValueKind.Object ? "insert"u8 : "range"u8)),
                replace.ValueKind == JsonValueKind.Object ? Range(replace) : null),
            characters.ValueKind == JsonValueKind.Array ? Array(characters).Select(String).ToArray() : null,
            new(String(data, "fileName"u8), Integer(data, "position"u8), String(data, "name"u8),
                Get(data, "supplementalFileIndex"u8) is { ValueKind: JsonValueKind.Number } index ? index.GetInt32() : null, String(data, "source"u8),
                ReadAutoImport(Get(data, "autoImport"u8)), Boolean(data, "isImportStatementCompletion"u8)),
            Text("insertText"u8), Text("filterText"u8), Get(value, "preselect"u8) is { ValueKind: JsonValueKind.True or JsonValueKind.False } preselect ? preselect.GetBoolean() : null,
            tags.ValueKind == JsonValueKind.Array ? Array(tags).Select(tag => tag.GetInt32()).ToArray() : null,
            labelDetails.ValueKind == JsonValueKind.Object ? new(OptionalText(labelDetails, "detail"u8), OptionalText(labelDetails, "description"u8)) : null,
            documentation.ValueKind == JsonValueKind.String ? new(String(documentation)) : documentation.ValueKind == JsonValueKind.Object ? new(String(documentation, "value"u8), String(documentation, "kind"u8)) : null,
            edits.ValueKind == JsonValueKind.Array ? Array(edits).Select(edit => new DocumentTextEdit(Range(Get(edit, "range"u8)), String(edit, "newText"u8))).ToArray() : null);

        int? Number(ReadOnlySpan<byte> name) => Get(value, name) is { ValueKind: JsonValueKind.Number } number ? number.GetInt32() : null;
        Utf8String? Text(ReadOnlySpan<byte> name) => Get(value, name) is { ValueKind: JsonValueKind.String } text ? String(text) : null;
        static Utf8String? OptionalText(JsonElement obj, ReadOnlySpan<byte> name) => Get(obj, name) is { ValueKind: JsonValueKind.String } text ? String(text) : null;
    }

    private static void WriteAutoImport(Utf8JsonWriter writer, AutoImportFix fix)
    {
        writer.WriteStartObject("autoImport"u8);
        if (fix.Kind != 0) writer.WriteNumber("kind"u8, (int)fix.Kind);
        if (!fix.Name.IsEmpty) String(writer, "name"u8, fix.Name);
        writer.WriteNumber("importKind"u8, (int)fix.ImportKind);
        if (fix.UseRequire) writer.WriteBoolean("useRequire"u8, true);
        writer.WriteNumber("addAsTypeOnly"u8, (int)fix.AddAsTypeOnly);
        if (!fix.ModuleSpecifier.IsEmpty) String(writer, "moduleSpecifier"u8, fix.ModuleSpecifier);
        writer.WriteNumber("importIndex"u8, fix.ImportIndex);
        if (fix.UsagePosition is { } position) { writer.WritePropertyName("usagePosition"u8); Position(writer, position); }
        if (!fix.NamespacePrefix.IsEmpty) String(writer, "namespacePrefix"u8, fix.NamespacePrefix);
        writer.WriteEndObject();
    }

    private static AutoImportFix? ReadAutoImport(JsonElement value) => value.ValueKind != JsonValueKind.Object ? null
        : new((AutoImportFixKind)Integer(value, "kind"u8), String(value, "name"u8), (ImportKind)Integer(value, "importKind"u8),
            Boolean(value, "useRequire"u8), (AddAsTypeOnly)Integer(value, "addAsTypeOnly"u8), String(value, "moduleSpecifier"u8),
            Integer(value, "importIndex"u8), Get(value, "usagePosition"u8) is { ValueKind: JsonValueKind.Object } position ? Position(position) : null,
            String(value, "namespacePrefix"u8));
}

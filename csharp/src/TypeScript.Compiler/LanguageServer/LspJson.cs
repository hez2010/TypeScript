using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compiler.LanguageServer;

internal static partial class LspJson
{
    internal static RpcResponse AutoInsertion(DocumentTextEdit? edit) => RpcResponse.Json(writer =>
    {
        if (edit is not { } item) { writer.WriteNullValue(); return; }
        writer.WriteStartObject(); writer.WriteNumber("_vs_textEditFormat"u8, 2);
        writer.WriteStartObject("_vs_textEdit"u8); writer.WritePropertyName("range"u8); Range(writer, item.Range);
        String(writer, "newText"u8, item.NewText); writer.WriteEndObject(); writer.WriteEndObject();
    });
    internal static RpcResponse InlayHints(IReadOnlyList<InlayHint>? hints) => RpcResponse.Json(writer =>
    {
        if (hints is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var hint in hints)
        {
            writer.WriteStartObject(); writer.WritePropertyName("position"u8); Position(writer, hint.Position);
            if (hint.Parts is { } parts)
            {
                writer.WriteStartArray("label"u8);
                foreach (var part in parts)
                {
                    writer.WriteStartObject(); String(writer, "value"u8, part.Value);
                    if (part.Location is { } location)
                    {
                        writer.WriteStartObject("location"u8); String(writer, "uri"u8, location.Uri);
                        writer.WritePropertyName("range"u8); Range(writer, location.Range); writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            else String(writer, "label"u8, hint.Label);
            if (hint.Kind is { } kind) writer.WriteNumber("kind"u8, kind);
            if (hint.PaddingLeft is { } left) writer.WriteBoolean("paddingLeft"u8, left);
            if (hint.PaddingRight is { } right) writer.WriteBoolean("paddingRight"u8, right);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });
    internal static CodeLens ReadCodeLens(JsonElement value)
    {
        var data = Get(value, "data"u8);
        return new(Range(Get(value, "range"u8)), new(String(data, "kind"u8), String(data, "uri"u8), Integer(data, "position"u8),
            Get(data, "supplementalFileIndex"u8) is { ValueKind: JsonValueKind.Number } index ? index.GetInt32() : null));
    }
    internal static RpcResponse CodeLenses(IReadOnlyList<CodeLens>? lenses) => RpcResponse.Json(writer =>
    {
        if (lenses is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray(); foreach (var lens in lenses) WriteCodeLens(writer, lens); writer.WriteEndArray();
    });
    internal static RpcResponse CodeLens(CodeLens lens) => RpcResponse.Json(writer => WriteCodeLens(writer, lens));
    private static void WriteCodeLens(Utf8JsonWriter writer, CodeLens lens)
    {
        writer.WriteStartObject(); writer.WritePropertyName("range"u8); Range(writer, lens.Range);
        writer.WriteStartObject("data"u8); String(writer, "kind"u8, lens.Data.Kind); String(writer, "uri"u8, lens.Data.Uri);
        writer.WriteNumber("position"u8, lens.Data.Position);
        if (lens.Data.SupplementalFileIndex is { } index) writer.WriteNumber("supplementalFileIndex"u8, index);
        writer.WriteEndObject();
        if (lens.Command is { } command)
        {
            writer.WriteStartObject("command"u8); String(writer, "title"u8, command.Title); String(writer, "command"u8, command.Command);
            if (command.Locations is { } locations)
            {
                writer.WriteStartArray("arguments"u8); JsonStrings.WriteString(writer, lens.Data.Uri); Position(writer, lens.Range.Start);
                writer.WriteStartArray();
                foreach (var location in locations)
                { writer.WriteStartObject(); String(writer, "uri"u8, location.Uri); writer.WritePropertyName("range"u8); Range(writer, location.Range); writer.WriteEndObject(); }
                writer.WriteEndArray(); writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
    internal static RpcResponse CallHierarchyItems(IReadOnlyList<CallHierarchyItem>? items) => RpcResponse.Json(writer =>
    {
        if (items is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray(); foreach (var item in items) CallHierarchyItem(writer, item); writer.WriteEndArray();
    });
    internal static RpcResponse CallHierarchyCalls(IReadOnlyList<CallHierarchyCall>? calls, bool incoming) => RpcResponse.Json(writer =>
    {
        if (calls is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var call in calls)
        {
            writer.WriteStartObject(); writer.WritePropertyName(incoming ? "from"u8 : "to"u8); CallHierarchyItem(writer, call.Item);
            writer.WriteStartArray("fromRanges"u8); foreach (var range in call.FromRanges) Range(writer, range); writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });
    private static void CallHierarchyItem(Utf8JsonWriter writer, CallHierarchyItem item)
    {
        writer.WriteStartObject(); String(writer, "name"u8, item.Name); writer.WriteNumber("kind"u8, (int)item.Kind); String(writer, "uri"u8, item.Uri);
        writer.WritePropertyName("range"u8); Range(writer, item.Range); writer.WritePropertyName("selectionRange"u8); Range(writer, item.SelectionRange);
        if (!item.Detail.IsEmpty) String(writer, "detail"u8, item.Detail); writer.WriteEndObject();
    }
    internal static RenameCapabilities RenameOptions(JsonElement capabilities)
    {
        var workspace = Get(capabilities, "workspace"u8);
        var edit = Get(workspace, "workspaceEdit"u8);
        return new(Boolean(edit, "documentChanges"u8), Array(Get(edit, "resourceOperations"u8)).Any(item => String(item) == "rename"u8),
            Boolean(Get(workspace, "fileOperations"u8), "willRename"u8));
    }
    internal static RpcResponse PrepareRename(RenameInfo info)
    {
        if (!info.CanRename) throw new RpcException(-32803, info.ErrorMessage);
        return RpcResponse.Json(writer =>
        {
            writer.WriteStartObject(); writer.WritePropertyName("range"u8); Range(writer, info.TriggerSpan);
            String(writer, "placeholder"u8, info.DisplayName); writer.WriteEndObject();
        });
    }
    internal static RpcResponse WorkspaceEdit(WorkspaceEdit? edit) => RpcResponse.Json(writer =>
    {
        if (edit is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        if (edit.Changes is { } changes)
        {
            writer.WriteStartObject("changes"u8);
            foreach (var (uri, edits) in changes) { writer.WritePropertyName(uri.Span); Edits(edits); }
            writer.WriteEndObject();
        }
        if (edit.DocumentChanges is { } documents)
        {
            writer.WriteStartArray("documentChanges"u8);
            foreach (var document in documents)
            {
                writer.WriteStartObject();
                if (document is RenameFileChange rename)
                { writer.WriteString("kind"u8, "rename"u8); String(writer, "oldUri"u8, rename.OldUri); String(writer, "newUri"u8, rename.NewUri); }
                else if (document is TextDocumentChange text)
                {
                    writer.WriteStartObject("textDocument"u8); String(writer, "uri"u8, text.Uri); writer.WriteNull("version"u8); writer.WriteEndObject();
                    writer.WritePropertyName("edits"u8); Edits(text.Edits);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        void Edits(IReadOnlyList<DocumentTextEdit> edits)
        {
            writer.WriteStartArray();
            foreach (var item in edits)
            { writer.WriteStartObject(); writer.WritePropertyName("range"u8); Range(writer, item.Range); String(writer, "newText"u8, item.NewText); writer.WriteEndObject(); }
            writer.WriteEndArray();
        }
    });
    internal static RpcResponse DocumentHighlights(IReadOnlyList<MultiDocumentHighlight> documents, Utf8String? current = null) => RpcResponse.Json(writer =>
    {
        writer.WriteStartArray();
        foreach (var document in documents)
        {
            if (current is { } uri && document.Uri != uri) continue;
            if (current is null) { writer.WriteStartObject(); String(writer, "uri"u8, document.Uri); writer.WriteStartArray("highlights"u8); }
            foreach (var highlight in document.Highlights)
            { writer.WriteStartObject(); writer.WritePropertyName("range"u8); Range(writer, highlight.Range); writer.WriteNumber("kind"u8, highlight.Kind); writer.WriteEndObject(); }
            if (current is null) { writer.WriteEndArray(); writer.WriteEndObject(); }
        }
        writer.WriteEndArray();
    });
    internal static RpcResponse VisualStudioReferences(IReadOnlyList<VisualStudioReference> items) => RpcResponse.Json(writer =>
    {
        writer.WriteStartArray();
        foreach (var item in items)
        {
            writer.WriteStartObject(); writer.WriteNumber("_vs_id"u8, item.Id);
            if (item.DefinitionId is { } definitionId) writer.WriteNumber("_vs_definitionId"u8, definitionId);
            writer.WriteStartObject("_vs_location"u8); String(writer, "uri"u8, item.Location.Uri);
            writer.WritePropertyName("range"u8); Range(writer, item.Location.Range); writer.WriteEndObject();
            if (item.DefinitionText is { } runs)
            { writer.WritePropertyName("_vs_definitionText"u8); Runs(writer, runs); writer.WriteString("_vs_containingType"u8, ""u8); }
            writer.WriteStartArray("_vs_kind"u8); writer.WriteNumberValue(item.Kind); writer.WriteEndArray();
            String(writer, "_vs_projectName"u8, item.ProjectName); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });
    internal static RpcResponse References(IReadOnlyList<DocumentLocation> locations) => RpcResponse.Json(writer =>
    {
        writer.WriteStartArray();
        foreach (var location in locations)
        {
            writer.WriteStartObject(); String(writer, "uri"u8, location.Uri);
            writer.WritePropertyName("range"u8); Range(writer, location.Range); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });
    internal static RpcResponse Definitions(IReadOnlyList<DefinitionLocation> locations, bool links) => RpcResponse.Json(writer =>
    {
        writer.WriteStartArray();
        foreach (var location in locations)
        {
            writer.WriteStartObject();
            String(writer, links ? "targetUri"u8 : "uri"u8, location.Uri);
            writer.WritePropertyName(links ? "targetRange"u8 : "range"u8); Range(writer, links ? location.Range : location.SelectionRange);
            if (links)
            {
                writer.WritePropertyName("targetSelectionRange"u8); Range(writer, location.SelectionRange);
                if (location.OriginRange is { } origin) { writer.WritePropertyName("originSelectionRange"u8); Range(writer, origin); }
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });
    internal static SignatureHelpOptions SignatureHelpOptions(JsonElement parameters, JsonElement capabilities)
    {
        var info = Get(Get(Get(capabilities, "textDocument"u8), "signatureHelp"u8), "signatureInformation"u8);
        var context = Get(parameters, "context"u8);
        return new()
        {
            Markdown = String(Array(Get(info, "documentationFormat"u8)).FirstOrDefault()) == "markdown"u8,
            SupportsVisualStudio = Boolean(capabilities, "_vs_supportsVisualStudioExtensions"u8),
            ActiveParameterSupport = Boolean(info, "activeParameterSupport"u8), NoActiveParameterSupport = Boolean(info, "noActiveParameterSupport"u8),
            TriggerKind = context.ValueKind == JsonValueKind.Object ? Integer(context, "triggerKind"u8) : null,
            TriggerCharacter = String(context, "triggerCharacter"u8), IsRetrigger = Boolean(context, "isRetrigger"u8),
        };
    }
    internal static RpcResponse SignatureHelp(SignatureHelp? help) => help is null ? RpcResponse.Null : RpcResponse.Json(writer =>
    {
        writer.WriteStartObject(); writer.WriteStartArray("signatures"u8);
        foreach (var signature in help.Signatures)
        {
            writer.WriteStartObject(); String(writer, "label"u8, signature.Label); Documentation(signature.Documentation);
            writer.WriteStartArray("parameters"u8);
            foreach (var parameter in signature.Parameters)
            { writer.WriteStartObject(); String(writer, "label"u8, parameter.Label); Documentation(parameter.Documentation); writer.WriteEndObject(); }
            writer.WriteEndArray(); ActiveParameter(signature.ActiveParameter, signature.HasActiveParameter);
            if (signature.Runs is { Count: > 0 } runs) { writer.WritePropertyName("_vs_colorizedLabel"u8); Runs(writer, runs); }
            writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteNumber("activeSignature"u8, help.ActiveSignature);
        ActiveParameter(help.ActiveParameter, help.HasActiveParameter); writer.WriteEndObject();
        void ActiveParameter(int? parameter, bool present)
        { if (present) { if (parameter is { } index) writer.WriteNumber("activeParameter"u8, index); else writer.WriteNull("activeParameter"u8); } }
        void Documentation(Utf8String text)
        {
            if (text.IsEmpty) return;
            writer.WriteStartObject("documentation"u8); String(writer, "kind"u8, help.Markdown ? "markdown"u8 : "plaintext"u8);
            String(writer, "value"u8, text); writer.WriteEndObject();
        }
    });

    internal static HoverOptions HoverOptions(JsonElement parameters, JsonElement capabilities, int maximumLength) => new()
    {
        Markdown = String(Array(Get(Get(Get(capabilities, "textDocument"u8), "hover"u8), "contentFormat"u8)).FirstOrDefault()) == "markdown"u8,
        SupportsVerbosity = Boolean(Get(capabilities, "experimental"u8), "hoverVerbosityLevel"u8),
        SupportsVisualStudio = Boolean(capabilities, "_vs_supportsVisualStudioExtensions"u8),
        VerbosityLevel = Integer(parameters, "verbosityLevel"u8), MaximumLength = maximumLength > 0 ? maximumLength : 500,
    };
    internal static RpcResponse Hover(Hover? hover) => hover is null ? RpcResponse.Null : RpcResponse.Json(writer =>
    {
        writer.WriteStartObject(); writer.WriteStartObject("contents"u8);
        String(writer, "kind"u8, hover.Kind); String(writer, "value"u8, hover.Value); writer.WriteEndObject();
        if (hover.Range is { } range) { writer.WritePropertyName("range"u8); Range(writer, range); }
        if (hover.CanIncreaseVerbosity) writer.WriteBoolean("canIncreaseVerbosity"u8, true);
        if (hover.Displays is { Count: > 0 } displays)
        {
            writer.WritePropertyName("_vs_rawContent"u8);
            if (displays.Count > 1) { writer.WriteStartObject(); writer.WriteNumber("Style"u8, 1); writer.WriteStartArray("Elements"u8); }
            foreach (var display in displays) Display(writer, display);
            if (displays.Count > 1) { writer.WriteEndArray(); writer.WriteString("_vs_type"u8, "ContainerElement"u8); writer.WriteEndObject(); }
        }
        writer.WriteEndObject();
    });
    private static void Display(Utf8JsonWriter writer, HoverDisplay display)
    {
        if (!display.Documentation.IsEmpty) { writer.WriteStartObject(); writer.WriteNumber("Style"u8, 1); writer.WriteStartArray("Elements"u8); }
        writer.WriteStartObject(); writer.WriteNumber("Style"u8, 0); writer.WriteStartArray("Elements"u8);
        writer.WriteStartObject(); writer.WriteStartObject("ImageId"u8); writer.WriteString("Guid"u8, "ae27a6b0-e345-4288-96df-5eaf394ee369"u8);
        writer.WriteNumber("Id"u8, display.ImageId); writer.WriteString("_vs_type"u8, "ImageId"u8); writer.WriteEndObject();
        writer.WriteString("_vs_type"u8, "ImageElement"u8); writer.WriteEndObject();
        Runs(writer, display.Runs); writer.WriteEndArray(); writer.WriteString("_vs_type"u8, "ContainerElement"u8); writer.WriteEndObject();
        if (!display.Documentation.IsEmpty)
        {
            Runs(writer, [new("text"u8, display.Documentation)]); writer.WriteEndArray();
            writer.WriteString("_vs_type"u8, "ContainerElement"u8); writer.WriteEndObject();
        }
    }
    private static void Runs(Utf8JsonWriter writer, IReadOnlyList<ClassifiedTextRun> runs)
    {
        writer.WriteStartObject(); writer.WriteStartArray("Runs"u8);
        foreach (var run in runs)
        {
            writer.WriteStartObject(); String(writer, "ClassificationTypeName"u8, run.Classification); String(writer, "Text"u8, run.Text);
            writer.WriteString("_vs_type"u8, "ClassifiedTextRun"u8); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteString("_vs_type"u8, "ClassifiedTextElement"u8); writer.WriteEndObject();
    }
    internal static JsonElement Get(JsonElement value, ReadOnlySpan<byte> name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) ? property : default;
    internal static Utf8String String(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        ? default : JsonStrings.GetString(value);
    internal static Utf8String String(JsonElement value, ReadOnlySpan<byte> name) => String(Get(value, name));
    internal static bool Boolean(JsonElement value, ReadOnlySpan<byte> name) => Get(value, name).ValueKind == JsonValueKind.True;
    internal static int Integer(JsonElement value, ReadOnlySpan<byte> name) => Get(value, name) is { ValueKind: JsonValueKind.Number } number ? number.GetInt32() : 0;
    internal static IEnumerable<JsonElement> Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    internal static DocumentPosition Position(JsonElement value) => new(
        Get(value, "line"u8) is { ValueKind: JsonValueKind.Number } line ? line.GetUInt32() : 0,
        Get(value, "character"u8) is { ValueKind: JsonValueKind.Number } character ? character.GetUInt32() : 0);
    internal static DocumentRange Range(JsonElement value) => new(Position(Get(value, "start"u8)), Position(Get(value, "end"u8)));
    internal static void String(Utf8JsonWriter writer, ReadOnlySpan<byte> name, Utf8String value)
    { writer.WritePropertyName(name); JsonStrings.WriteString(writer, value); }
    internal static void Position(Utf8JsonWriter writer, DocumentPosition position)
    { writer.WriteStartObject(); writer.WriteNumber("line"u8, position.Line); writer.WriteNumber("character"u8, position.Character); writer.WriteEndObject(); }
    internal static void Range(Utf8JsonWriter writer, DocumentRange range)
    {
        writer.WriteStartObject(); writer.WritePropertyName("start"u8); Position(writer, range.Start);
        writer.WritePropertyName("end"u8); Position(writer, range.End); writer.WriteEndObject();
    }
    internal static RpcResponse SelectionRanges(SelectionRange[]? ranges) => RpcResponse.Json(writer =>
    {
        if (ranges is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var range in ranges)
        {
            int depth = 0;
            for (SelectionRange? current = range; current is not null; current = current.Parent)
            {
                writer.WriteStartObject(); depth++;
                writer.WritePropertyName("range"u8); Range(writer, current.Range);
                if (current.Parent is not null) writer.WritePropertyName("parent"u8);
            }
            while (depth-- != 0) writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });

    internal static RpcResponse SemanticTokenResult(uint[]? data) => RpcResponse.Json(writer =>
    {
        if (data is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject(); writer.WriteStartArray("data"u8);
        foreach (uint value in data) writer.WriteNumberValue(value);
        writer.WriteEndArray(); writer.WriteEndObject();
    });
    internal static RpcResponse TextEdits(DocumentTextEdit[]? edits) => RpcResponse.Json(writer =>
    {
        if (edits is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var edit in edits)
        {
            writer.WriteStartObject(); writer.WritePropertyName("range"u8); Range(writer, edit.Range);
            String(writer, "newText"u8, edit.NewText); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });
    internal static RpcResponse WorkspaceSymbols(WorkspaceSymbol[] symbols) => RpcResponse.Json(writer =>
    {
        writer.WriteStartArray();
        foreach (var symbol in symbols)
        {
            writer.WriteStartObject(); String(writer, "name"u8, symbol.Name); writer.WriteNumber("kind"u8, (int)symbol.Kind);
            writer.WriteStartObject("location"u8); String(writer, "uri"u8, symbol.Uri);
            writer.WritePropertyName("range"u8); Range(writer, symbol.Range); writer.WriteEndObject();
            if (symbol.ContainerName is { IsEmpty: false } container) String(writer, "containerName"u8, container);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });
    internal static RpcResponse DocumentSymbols(DocumentSymbol[] symbols, bool hierarchical, Utf8String uri) => RpcResponse.Json(writer =>
    {
        writer.WriteStartArray();
        Stack<(DocumentSymbol? Symbol, Utf8String? Container)> pending = [];
        for (int i = symbols.Length - 1; i >= 0; i--) pending.Push((symbols[i], null));
        while (pending.TryPop(out var item))
        {
            if (item.Symbol is not { } symbol) { writer.WriteEndArray(); writer.WriteEndObject(); continue; }
            writer.WriteStartObject(); String(writer, "name"u8, symbol.Name); writer.WriteNumber("kind"u8, (int)symbol.Kind);
            if (hierarchical)
            {
                writer.WritePropertyName("range"u8); Range(writer, symbol.Range);
                writer.WritePropertyName("selectionRange"u8); Range(writer, symbol.SelectionRange);
                writer.WriteStartArray("children"u8); pending.Push((null, null));
            }
            else
            {
                writer.WriteStartObject("location"u8); String(writer, "uri"u8, uri);
                writer.WritePropertyName("range"u8); Range(writer, symbol.Range); writer.WriteEndObject();
                if (item.Container is { } container) String(writer, "containerName"u8, container);
                writer.WriteEndObject();
            }
            for (int i = symbol.Children.Length - 1; i >= 0; i--) pending.Push((symbol.Children[i], symbol.Name));
        }
        writer.WriteEndArray();
    });
    internal static RpcResponse LinkedEditingRanges(LinkedEditingRanges? result) => RpcResponse.Json(writer =>
    {
        if (result is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject(); writer.WriteStartArray("ranges"u8);
        foreach (var range in result.Ranges) Range(writer, range);
        writer.WriteEndArray(); String(writer, "wordPattern"u8, result.WordPattern); writer.WriteEndObject();
    });
    internal static RpcResponse FoldingRanges(FoldingRange[] ranges) => RpcResponse.Json(writer =>
    {
        writer.WriteStartArray();
        foreach (var range in ranges)
        {
            writer.WriteStartObject(); writer.WriteNumber("startLine"u8, range.StartLine); writer.WriteNumber("startCharacter"u8, range.StartCharacter);
            writer.WriteNumber("endLine"u8, range.EndLine); writer.WriteNumber("endCharacter"u8, range.EndCharacter);
            if (range.Kind is { } kind) String(writer, "kind"u8, kind);
            if (range.CollapsedText is { } banner) String(writer, "collapsedText"u8, banner);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });
}

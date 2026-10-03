using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class FormattingTests
{
    internal static async Task InsertionLinesAsync()
    {
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            using var input = JsonDocument.Parse(line);
            var root = input.RootElement;
            var file = await Parser.ParseSourceFileAsync(new(JsonStrings.GetString(root.GetProperty("file")), (ScriptKind)root.GetProperty("kind").GetInt32()), new(JsonStrings.GetString(root.GetProperty("text"))));
            var target = await Parser.ParseSourceFileAsync(new("/target.ts"u8, file.ScriptKind), new(JsonStrings.GetString(root.GetProperty("target"))));
            var options = FormatCodeSettings.Read(root.GetProperty("options"));
            using var output = new MemoryStream();
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartArray();
            foreach (var node in new SyntaxNode[] { file }.Concat(file.Statements!))
            {
                foreach (var child in node.DescendantsAndSelf()) child.Parent = null;
                var (text, positioned) = SyntaxPrinter.PrintAndPositionNode(node, newLine: options.NewLineCharacter, indentSize: options.IndentSize);
                writer.WriteStartObject(); writer.WritePropertyName("text"); JsonStrings.WriteString(writer, text);
                writer.WritePropertyName("ranges"); writer.WriteStartArray();
                foreach (var n in positioned.DescendantsAndSelf())
                {
                    List<SyntaxChild> groups = []; n.GetChildGroups(groups);
                    writer.WriteStartArray(); writer.WriteNumberValue((int)n.Kind); writer.WriteNumberValue(n.Pos);
                    writer.WriteNumberValue(n.End); writer.WriteNumberValue((int)n.Flags); writer.WriteStartArray();
                    foreach (var group in groups)
                        if (group.List is { } list)
                        {
                            writer.WriteStartArray(); writer.WriteNumberValue(list.Pos); writer.WriteNumberValue(list.End);
                            writer.WriteNumberValue(list.Count); writer.WriteNumberValue(list.HasTrailingComma ? 1 : 0); writer.WriteEndArray();
                        }
                    writer.WriteEndArray(); writer.WriteEndArray();
                }
                writer.WriteEndArray(); writer.WritePropertyName("formatted"); writer.WriteStartArray();
                if (node is not SourceFileNode)
                    foreach (var pos in root.GetProperty("positions").EnumerateArray())
                        JsonStrings.WriteString(writer, await SourceFormatter.FormatNodeForInsertionAsync(node, target, pos.GetInt32(), options));
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.Flush();
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        }
    }

    internal static async Task<int> SafetyAsync()
    {
        int assertions = 0;
        void Check(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
        const int depth = 4096;
        var text = Utf8String.FromString("const value=" + new string('(', depth) + "0" + new string(')', depth) + ";");
        var file = await Parser.ParseSourceFileAsync(new("/deep.ts"u8), new(text));
        var before = file.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        var changes = await SourceFormatter.FormatDocumentAsync(file);
        Check(changes.Length == 2 && changes[0] == new SourceTextChange(11, 11, " "u8) && changes[1] == new SourceTextChange(12, 12, " "u8), "Deep formatting changes");
        Check(before.All(entry => entry.Node.Parent == entry.Parent && entry.Node.Pos == entry.Pos && entry.Node.End == entry.End && entry.Node.Flags == entry.Flags), "Formatting modified the syntax tree");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await SourceFormatter.FormatDocumentAsync(file, cancellation: cancellation.Token); throw new InvalidOperationException("Formatting ignored cancellation"); }
        catch (OperationCanceledException) { assertions++; }
        Check((await SourceFormatter.FormatDocumentAsync(await Parser.ParseSourceFileAsync(new("/empty.ts"u8), new(Utf8String.Empty)))).Length == 0, "Empty source formatting");
        var comment = await Parser.ParseSourceFileAsync(new("/comment.ts"u8), new("/* unclosed\n  text; {\n"u8));
        Check((await SourceFormatter.FormatOnTypeAsync(comment, comment.End, "\n"u8)).Length == 0, "On-type formatting within a comment");
        Check(SourceFormatter.IndentationText(7, new() { TabSize = 3, ConvertTabsToSpaces = false }) == "\t\t "u8, "Tab indentation");
        Check(SourceFormatter.IndentationText(7, new() { TabSize = 0, ConvertTabsToSpaces = false }).IsEmpty, "Zero tab size");
        var positioned = SyntaxPrinter.PrintAndPositionNode(file.Statements![0]);
        var second = SyntaxPrinter.PrintAndPositionNode(file.Statements[0]);
        Check(positioned.Text == second.Text && positioned.Node != file.Statements[0] && positioned.Node != second.Node, "Positioned printing owns its result and is repeatable");
        Check(before.All(entry => entry.Node.Parent == entry.Parent && entry.Node.Pos == entry.Pos && entry.Node.End == entry.End && entry.Node.Flags == entry.Flags), "Positioned printing preserves the caller's tree");
        var conditional = await Parser.ParseSourceFileAsync(new("/if.ts"u8), new("if (a) {}"u8));
        Check(SyntaxPrinter.PrintAndPositionNode(conditional.Statements![0]).Node is IfStatementNode { ElseStatement: null }, "Positioning preserves an absent else branch");
        try { SyntaxPrinter.PrintAndPositionNode(file, cancellation: cancellation.Token); throw new InvalidOperationException("Positioned printing ignored cancellation"); }
        catch (OperationCanceledException) { assertions++; }
        return assertions;
    }

    internal static async Task FormattingLinesAsync()
    {
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            using var input = JsonDocument.Parse(line);
            var root = input.RootElement;
            var file = await Parser.ParseSourceFileAsync(new(JsonStrings.GetString(root.GetProperty("file"))), new(JsonStrings.GetString(root.GetProperty("text"))));
            var options = FormatCodeSettings.Read(root.GetProperty("options"));
            var parameters = root.GetProperty("params");
            var encoding = root.GetProperty("encoding").GetString() == "utf-16" ? PositionEncoding.Utf16 : PositionEncoding.Utf8;
            LanguageServiceDocument service = new(file, encoding);
            if (root.TryGetProperty("projections", out var projections))
            {
                List<DocumentProjection> mapped = [];
                foreach (var projection in projections.EnumerateArray())
                {
                    var name = JsonStrings.GetString(projection.GetProperty("fileName"));
                    var syntax = await Parser.ParseSourceFileAsync(new(name, (ScriptKind)projection.GetProperty("scriptKind").GetInt32()), new(JsonStrings.GetString(projection.GetProperty("text"))));
                    var map = new SpanMap(projection.GetProperty("segments").EnumerateArray().Select(segment =>
                        new MappingSegment(segment[0].GetInt32(), segment[1].GetInt32(), segment[2].GetInt32(), segment[3].GetInt32(), (MappingKind)segment[4].GetInt32(), (MappingFeature)segment[5].GetInt32())));
                    mapped.Add(new(syntax, new(syntax, new(JsonStrings.GetString(projection.GetProperty("original"))), map, name, "test"u8, "test"u8, []), encoding));
                }
                service = new(mapped.ToArray());
            }
            DocumentTextEdit[]? edits = root.GetProperty("method").GetString() switch
            {
                "textDocument/formatting" => await service.GetFormattingEditsAsync(options),
                "textDocument/rangeFormatting" => await service.GetFormattingEditsAsync(options, LspJson.Range(parameters.GetProperty("range"))),
                "textDocument/onTypeFormatting" => await service.GetFormattingEditsAfterKeystrokeAsync(LspJson.Position(parameters.GetProperty("position")), JsonStrings.GetString(parameters.GetProperty("ch")), options),
                _ => throw new InvalidOperationException("Unknown formatting method"),
            };
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                if (edits is null) writer.WriteNullValue();
                else
                {
                    writer.WriteStartArray();
                    foreach (var edit in edits)
                    {
                        writer.WriteStartObject(); writer.WritePropertyName("range"); LspJson.Range(writer, edit.Range);
                        writer.WritePropertyName("newText"); JsonStrings.WriteString(writer, edit.NewText); writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
            }
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        }
    }

    internal static async Task IndentationLinesAsync()
    {
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            using var input = JsonDocument.Parse(line);
            var root = input.RootElement;
            var file = await Parser.ParseSourceFileAsync(new(JsonStrings.GetString(root.GetProperty("file")), (ScriptKind)root.GetProperty("kind").GetInt32()),
                new(JsonStrings.GetString(root.GetProperty("text"))));
            var options = new FormatCodeSettings();
            if (root.TryGetProperty("options", out var settings)) options = FormatCodeSettings.Read(settings, options);
            var positions = root.TryGetProperty("positions", out var supplied) ? supplied.EnumerateArray().Select(p => p.GetInt32()).ToArray()
                : Enumerable.Range(0, file.Source.Length + 2).ToArray();
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                async ValueTask Number(Func<ValueTask<int>> query)
                {
                    try { writer.WriteNumberValue(await query()); }
                    catch (Exception error) when (error is InvalidOperationException or ArgumentOutOfRangeException or IndexOutOfRangeException)
                    { writer.WriteStartObject(); writer.WriteString("error", error.Message); writer.WriteEndObject(); }
                }
                writer.WriteStartObject(); writer.WriteStartArray("positions");
                foreach (int position in positions)
                {
                    writer.WriteStartArray();
                    await Number(() => SmartIndenter.GetIndentationAsync(position, file, options));
                    await Number(() => SmartIndenter.GetIndentationAsync(position, file, options, true));
                    writer.WriteEndArray();
                }
                writer.WriteEndArray(); writer.WriteStartArray("nodes");
                var worker = new SmartIndenter.Indentation(file, options, default);
                var stack = new Stack<SyntaxNode>(); stack.Push(file);
                while (stack.TryPop(out var node))
                {
                    if ((node.Flags & NodeFlags.Reparsed) != 0) continue;
                    writer.WriteStartArray(); writer.WriteNumberValue((uint)node.Kind); writer.WriteNumberValue(node.Pos); writer.WriteNumberValue(node.End);
                    writer.WriteBooleanValue(await SyntaxNavigation.IsCompletedNodeAsync(node, file));
                    await Number(() => SmartIndenter.GetIndentationForNodeAsync(node, file, options));
                    await Number(() => SmartIndenter.GetIndentationForNodeAsync(node, file, options, (0, file.Source.Length)));
                    var list = await worker.ContainingListAsync(node);
                    if (list is null) writer.WriteNullValue();
                    else { writer.WriteStartArray(); writer.WriteNumberValue(list.Pos); writer.WriteNumberValue(list.End); writer.WriteNumberValue(list.Count); writer.WriteEndArray(); }
                    writer.WriteEndArray();
                    for (int i = node.ChildCount - 1; i >= 0; i--) stack.Push(node.GetChild(i));
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        }
    }

}

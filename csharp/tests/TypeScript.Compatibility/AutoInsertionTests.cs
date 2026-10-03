using System.Text.Json;
using TypeScript.Compiler;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compatibility;

internal static class AutoInsertionTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        const int depth = 2048;
        var text = Utf8String.FromString("const x=" + string.Concat(Enumerable.Repeat("<A>", depth)) + string.Concat(Enumerable.Repeat("</A>", depth - 1)));
        var file = await Parser.ParseSourceFileAsync(new("/deep.tsx"u8, ScriptKind.TSX), new(text));
        var original = file.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        var service = new LanguageServiceDocument(file);
        var position = new DocumentPosition(0, 8 + 3 * depth);
        Check(await service.GetAutoInsertionAsync(position, ">"u8) is { NewText.Span: var closing } && closing.SequenceEqual("$0</A>"u8), "Deep matching tags find their unclosed ancestor without recursion");
        Check(original.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags), "Auto insertion preserves shared syntax");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await service.GetAutoInsertionAsync(position, ">"u8, cancellation: canceled.Token); Check(false, "Canceled insertion must throw"); }
        catch (OperationCanceledException) { checks++; }
        Utf8String source = "const 字='😀'; const x=<$Tag.件>"u8;
        file = await Parser.ParseSourceFileAsync(new("/source.tsx"u8, ScriptKind.TSX), new(source));
        foreach (var encoding in new[] { PositionEncoding.Utf8, PositionEncoding.Utf16 })
        {
            var lines = new DocumentLineMap(source);
            position = lines.ToPosition(source.Length, encoding);
            service = new LanguageServiceDocument(file, encoding);
            var edit = await service.GetAutoInsertionAsync(position, ">"u8);
            Check(edit is { NewText.Span: var snippet } && snippet.SequenceEqual("$0</\\$Tag.件>"u8) && edit.Value.Range == new DocumentRange(position, position), "Snippet escaping and edit positions preserve Unicode");
            Check(await service.GetAutoInsertionAsync(position, "/"u8) is null, "Only the closing-angle trigger inserts a tag");
            Check(await service.GetAutoInsertionAsync(position, ">"u8, new() { EnableAutoClosingTags = false }) is null, "The disabled preference suppresses insertion");
            foreach (var kind in new[] { MappingKind.Verbatim, MappingKind.Atom, MappingKind.Alias })
            {
                var mapping = new MappedSourceFile(file, new(source), new([new(0, source.Length, 0, source.Length, kind)]), file.FileName, "test"u8, "test"u8, []);
                service = new LanguageServiceDocument([new(file, mapping, encoding)]);
                Check((await service.GetAutoInsertionAsync(position, ">"u8) is not null) == (kind == MappingKind.Verbatim), "Insertion requires an exact projection");
            }
            var disabled = new MappedSourceFile(file, new(source), new([new(0, source.Length, 0, source.Length, MappingKind.Verbatim, MappingFeature.All & ~MappingFeature.AutoInsert)]), file.FileName, "test"u8, "test"u8, []);
            Check(await new LanguageServiceDocument([new(file, disabled, encoding)]).GetAutoInsertionAsync(position, ">"u8) is null, "Feature-disabled mappings suppress insertion");
            Check(await new LanguageServiceDocument([new(file, null, encoding), new(file, null, encoding)]).GetAutoInsertionAsync(position, ">"u8) is null, "Ambiguous duplicate projections suppress insertion");
            var unmapped = new MappedSourceFile(file, new(source), new([]), file.FileName, "test"u8, "test"u8, []);
            Check(await new LanguageServiceDocument([new(file, unmapped, encoding)]).GetAutoInsertionAsync(position, ">"u8) is null, "Synthesized positions suppress insertion");
        }
        using var fallback = JsonDocument.Parse("{\"autoClosingTags\":false}");
        using var stable = JsonDocument.Parse("{\"unstable\":{\"autoClosingTags\":false},\"autoClosingTags\":{\"enabled\":true}}");
        Check(new UserPreferences().WithConfig(fallback.RootElement).EnableAutoClosingTags == false, "Legacy auto-closing configuration remains supported");
        Check(new UserPreferences().WithConfig(stable.RootElement).EnableAutoClosingTags == true, "Stable auto-closing configuration overrides unstable values");
        return checks;
    }
}

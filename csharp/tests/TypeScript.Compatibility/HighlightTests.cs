using TypeScript.Compiler;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;

namespace TypeScript.Compatibility;

internal static class HighlightTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        const int depth = 2048;
        var text = Utf8String.FromString("let 名前 = '😀';\r\nfunction 方法(){while(true){" + new string('{', depth)
            + "名前 = 'é'; break;" + new string('}', depth) + "}}\r\nconst 字 = 名前;");
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/deep.ts"u8] = text.Span.ToArray(), ["/other.ts"u8] = "名前;"u8.ToArray() }, true);
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8);
        await using var host = new ProjectSnapshotHost(fs);
        await using var snapshot = await host.CreateAsync(new() { CreatePrograms = [new(["/deep.ts"u8, "/other.ts"u8], options)] });
        var project = snapshot.CreatedPrograms[0];
        var file = project.Program!.GetFile("/deep.ts"u8)!;
        var original = file.Syntax.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        var service = new LanguageServiceDocument(project.Program, file);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var lines = new DocumentLineMap(text);
        var position = lines.ToPosition(text.LastIndexOf("名前"u8), PositionEncoding.Utf8);
        var highlights = await service.GetDocumentHighlightsAsync(project, position, cancellation: deadline.Token);
        Check(highlights is [var document] && document.Highlights.Select(h => h.Kind).SequenceEqual([3, 3, 2]), "Deep highlights distinguish declarations, assignments and reads");
        var loop = await service.GetDocumentHighlightsAsync(project, lines.ToPosition(text.IndexOf("break"u8), PositionEncoding.Utf8), cancellation: deadline.Token);
        Check(loop is [var flow] && flow.Highlights.Count == 2 && flow.Highlights[0].Range.Start == lines.ToPosition(text.IndexOf("while"u8), PositionEncoding.Utf8), "Deep break ownership highlights the loop and its break");
        var other = await service.GetDocumentHighlightsAsync(project, position, ["/other.ts"u8, "/other.ts"u8, "/missing.ts"u8], deadline.Token);
        Check(other is [var separate] && separate.Uri == "file:///other.ts"u8 && separate.Highlights is [var read] && read.Kind == 2, "Multi-file highlights honor selected files and remove duplicates");
        var fallback = await service.GetDocumentHighlightsAsync(project, position, ["/missing.ts"u8], deadline.Token);
        Check(LspJson.DocumentHighlights(fallback).Data.Span.SequenceEqual(LspJson.DocumentHighlights(highlights).Data.Span), "Unresolved search files fall back to the current document");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.GetDocumentHighlightsAsync(project, position, cancellation: deadline.Token).AsTask()));
        Check(concurrent.All(value => LspJson.DocumentHighlights(value).Data.Span.SequenceEqual(LspJson.DocumentHighlights(highlights).Data.Span)), "Concurrent highlights preserve ordering and ownership");
        var utf16 = new LanguageServiceDocument(project.Program, file, PositionEncoding.Utf16);
        var converted = await utf16.GetDocumentHighlightsAsync(project, lines.ToPosition(text.LastIndexOf("名前"u8), PositionEncoding.Utf16), cancellation: deadline.Token);
        Check(converted is [var convertedDocument] && convertedDocument.Highlights[0].Range == new DocumentRange(new(0, 4), new(0, 6)), "Highlight ranges use UTF-16 coordinates");
        Check(original.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags), "Highlight traversal preserves source nodes");
        Check(await service.GetDocumentHighlightsAsync(project, new(uint.MaxValue, 0), cancellation: deadline.Token) is [], "Highlights beyond EOF are empty");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.GetDocumentHighlightsAsync(project, position, cancellation: cancelled.Token); Check(false, "Canceled highlights must throw"); }
        catch (OperationCanceledException) { checks++; }
        return checks;
    }
}

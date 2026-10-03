using System.Text.Json;
using TypeScript.Compiler;
using TypeScript.Compiler.Api;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;

namespace TypeScript.Compatibility;

internal static class HoverTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        const int depth = 2048;
        var deep = Utf8String.FromString(string.Concat(Enumerable.Repeat("function f(){", depth)) + "const 名=1;" + new string('}', depth));
        Utf8String aliases = "namespace N { const value = 1; export { value as alias }; }\nN;"u8;
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        { ["/deep.ts"u8] = deep.Span.ToArray(), ["/aliases.ts"u8] = aliases.Span.ToArray() }, true);
        using var raw = JsonDocument.Parse("{\"noLib\":true}");
        await using var host = new ProjectSnapshotHost(fs, new() { CurrentDirectory = "/"u8 });
        await using var snapshot = await host.CreateAsync(new() { CreatePrograms = [new(["/deep.ts"u8, "/aliases.ts"u8], ApiJson.CompilerOptions(raw.RootElement))] });
        var project = snapshot.CreatedPrograms[0];
        var file = project.Program!.GetFile("/deep.ts"u8)!;
        var original = file.Syntax.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        var service = new LanguageServiceDocument(project.Program, file);
        var position = new DocumentLineMap(deep).ToPosition(deep.IndexOf("名"u8), PositionEncoding.Utf8);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var hover = await service.GetHoverAsync(project, position, new() { Markdown = true }, timeout.Token);
        Check(hover?.Value == "```typescript\nconst 名: 1\n```\n"u8, "Deep hover retains the innermost symbol");
        Check(hover!.Range == new DocumentRange(position, position with { Character = position.Character + 3 }), "Hover ranges retain UTF-8 coordinates");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.GetHoverAsync(project, position, new() { Markdown = true }, timeout.Token).AsTask()));
        Check(concurrent.All(result => result == hover), "Concurrent hover requests share an immutable snapshot safely");
        Check(original.All(entry => entry.Node.Parent == entry.Parent && entry.Node.Pos == entry.Pos && entry.Node.End == entry.End && entry.Node.Flags == entry.Flags), "Hover preserves source positions and parents");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.GetHoverAsync(project, position, cancellation: cancelled.Token); throw new InvalidOperationException("Expected hover cancellation"); }
        catch (OperationCanceledException) { checks++; }
        var aliasFile = project.Program.GetFile("/aliases.ts"u8)!;
        var expanded = await new LanguageServiceDocument(project.Program, aliasFile).GetHoverAsync(project, new(1, 0),
            new() { Markdown = true, VerbosityLevel = 1, SupportsVerbosity = true }, timeout.Token);
        Check(expanded?.Value.Contains("export { value as alias };"u8) == true, "Namespace alias expansion completes while holding the checker query lease");
        return checks;
    }
}

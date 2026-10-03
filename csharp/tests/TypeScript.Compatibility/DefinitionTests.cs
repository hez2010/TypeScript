using System.Text.Json;
using TypeScript.Compiler;
using TypeScript.Compiler.Api;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;

namespace TypeScript.Compatibility;

internal static class DefinitionTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        const int depth = 2048;
        var text = Utf8String.FromString("interface 表 { 名: string; }\r\nfunction 関数(値: 表): 表 { return 値; }\r\n"
            + string.Concat(Enumerable.Repeat("function f(){", depth)) + "const 字 = '😀'; 関数({名: 字});" + new string('}', depth));
        var entryText = "import { 関数 } from './lib'; 関数();"u8;
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/deep.ts"u8] = text.Span.ToArray(), ["/entry.ts"u8] = entryText.ToArray(),
            ["/lib.d.ts"u8] = "export declare function 関数(): void;"u8.ToArray(), ["/lib.js"u8] = "export function 関数() {}"u8.ToArray(),
        }, true);
        using var raw = JsonDocument.Parse("{\"noLib\":true,\"strict\":true}");
        await using var host = new ProjectSnapshotHost(fs, new() { CurrentDirectory = "/"u8 });
        await using var snapshot = await host.CreateAsync(new() { CreatePrograms = [new(["/deep.ts"u8], ApiJson.CompilerOptions(raw.RootElement)), new(["/entry.ts"u8], ApiJson.CompilerOptions(raw.RootElement))] });
        var project = snapshot.CreatedPrograms[0];
        var file = project.Program!.GetFile("/deep.ts"u8)!;
        var original = file.Syntax.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        var service = new LanguageServiceDocument(project.Program, file);
        int offset = text.LastIndexOf("関数"u8);
        var position = new DocumentLineMap(text).ToPosition(offset, PositionEncoding.Utf8);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await service.GetDefinitionsAsync(project, position, cancellation: deadline.Token);
        Check(result is [var location] && location.SelectionRange == new DocumentRange(new(1, 9), new(1, 15)), "Deep definitions retain the declaration name span");
        var type = await service.GetDefinitionsAsync(project, position, true, deadline.Token);
        Check(type is [var definition] && definition.SelectionRange == new DocumentRange(new(0, 10), new(0, 13)), "Type definitions follow a function return type");
        var expected = LspJson.Definitions(result, true).Data;
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.GetDefinitionsAsync(project, position, cancellation: deadline.Token).AsTask()));
        Check(concurrent.All(value => LspJson.Definitions(value, true).Data.Span.SequenceEqual(expected.Span)), "Concurrent definition requests return identical targets");
        Check(original.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags), "Definition queries preserve the source tree");
        var utf16 = new LanguageServiceDocument(project.Program, file, PositionEncoding.Utf16);
        var converted = await utf16.GetDefinitionsAsync(project, new DocumentLineMap(text).ToPosition(offset, PositionEncoding.Utf16), cancellation: deadline.Token);
        Check(converted is [var utf16Location] && utf16Location.SelectionRange == new DocumentRange(new(1, 9), new(1, 11)), "Definition targets use the negotiated encoding");
        Check(await service.GetDefinitionsAsync(project, new(uint.MaxValue, 0), cancellation: deadline.Token) is [], "Definitions beyond the document are empty");
        Check(LspJson.Definitions(await service.GetSourceDefinitionsAsync(project, position, deadline.Token), true).Data.Span.SequenceEqual(expected.Span), "Source definitions retain deep implementation declarations");
        var external = snapshot.CreatedPrograms[1];
        var entry = external.Program!.GetFile("/entry.ts"u8)!;
        var externalService = new LanguageServiceDocument(external.Program, entry);
        var externalPosition = new DocumentLineMap(entry.Syntax.Source.Text).ToPosition(entry.Syntax.Source.Text.LastIndexOf("関数"u8), PositionEncoding.Utf8);
        Check(await externalService.GetDefinitionsAsync(external, externalPosition, cancellation: deadline.Token) is [var typed] && typed.Uri == "file:///lib.d.ts"u8, "Ordinary definitions retain the declaration file");
        var sourceDefinition = await externalService.GetSourceDefinitionsAsync(external, externalPosition, deadline.Token);
        Check(sourceDefinition is [var implementation] && implementation.Uri == "file:///lib.js"u8 && implementation.SelectionRange == new DocumentRange(new(0, 16), new(0, 22)), "Source definitions parse and bind an implementation outside the program");
        Check(external.Program.GetFile("/lib.js"u8) is null, "Source lookup preserves the published program graph");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.GetDefinitionsAsync(project, position, cancellation: cancelled.Token); throw new InvalidOperationException("Expected definition cancellation"); }
        catch (OperationCanceledException) { checks++; }
        return checks;
    }
}

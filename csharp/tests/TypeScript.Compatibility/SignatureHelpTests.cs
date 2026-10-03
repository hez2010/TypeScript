using System.Text.Json;
using TypeScript.Compiler;
using TypeScript.Compiler.Api;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;

namespace TypeScript.Compatibility;

internal static class SignatureHelpTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        const int depth = 2048;
        var text = Utf8String.FromString("function 関数(値: string, 数?: number): boolean { return true; }\n"
            + string.Concat(Enumerable.Repeat("function f(){", depth)) + "関数('😀', );" + new string('}', depth));
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/deep.ts"u8] = text.Span.ToArray() }, true);
        using var raw = JsonDocument.Parse("{\"noLib\":true,\"strict\":true}");
        await using var host = new ProjectSnapshotHost(fs, new() { CurrentDirectory = "/"u8 });
        await using var snapshot = await host.CreateAsync(new() { CreatePrograms = [new(["/deep.ts"u8], ApiJson.CompilerOptions(raw.RootElement))] });
        var project = snapshot.CreatedPrograms[0];
        var file = project.Program!.GetFile("/deep.ts"u8)!;
        var original = file.Syntax.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        var service = new LanguageServiceDocument(project.Program, file);
        int offset = text.IndexOf(", );"u8) + 1;
        var position = new DocumentLineMap(text).ToPosition(offset, PositionEncoding.Utf8);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var help = await service.GetSignatureHelpAsync(project, position, cancellation: deadline.Token);
        Check(help is { Signatures.Count: 1, ActiveSignature: 0, ActiveParameter: 1, HasActiveParameter: true }, "Deep calls retain the active parameter");
        Check(help!.Signatures[0].Label == "関数(値: string, 数?: number): boolean"u8, $"Signature labels retain Unicode names and optional types: {help.Signatures[0].Label}");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.GetSignatureHelpAsync(project, position, cancellation: deadline.Token).AsTask()));
        var expected = LspJson.SignatureHelp(help).Data;
        Check(concurrent.All(result => LspJson.SignatureHelp(result).Data.Span.SequenceEqual(expected.Span)), "Concurrent signature queries retain identical responses");
        Check(original.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags),
            "Signature queries preserve source positions, parents and flags");
        var utf16 = new LanguageServiceDocument(project.Program, file, PositionEncoding.Utf16);
        Check(LspJson.SignatureHelp(await utf16.GetSignatureHelpAsync(project, new DocumentLineMap(text).ToPosition(offset, PositionEncoding.Utf16),
            cancellation: deadline.Token)).Data.Span.SequenceEqual(expected.Span), "Signature help preserves the negotiated coordinate boundary");
        Check(await service.GetSignatureHelpAsync(project, new(0, 0), cancellation: deadline.Token) is null, "Signature help at the start of the file is empty");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.GetSignatureHelpAsync(project, position, cancellation: cancelled.Token); throw new InvalidOperationException("Expected signature-help cancellation"); }
        catch (OperationCanceledException) { checks++; }
        foreach (var (closing, opening) in new[] { (')', '('), (']', '['), ('}', '{') })
        {
            var recovery = Utf8String.FromString($"function f<T>(x:T){{}}; f< {closing} {opening}");
            var recoveryFs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/recovery.ts"u8] = recovery.Span.ToArray() }, true);
            await using var recoveryHost = new ProjectSnapshotHost(recoveryFs);
            await using var recoverySnapshot = await recoveryHost.CreateAsync(new() { CreatePrograms = [new(["/recovery.ts"u8], ApiJson.CompilerOptions(raw.RootElement))] });
            var recoveryProject = recoverySnapshot.CreatedPrograms[0];
            var recoveryService = new LanguageServiceDocument(recoveryProject.Program!, recoveryProject.Program!.GetFile("/recovery.ts"u8)!);
            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Check(await recoveryService.GetSignatureHelpAsync(recoveryProject, new(0, 26), cancellation: bounded.Token) is null,
                "Malformed delimiters must finish backward navigation instead of revisiting a later opening token");
        }
        return checks;
    }
}

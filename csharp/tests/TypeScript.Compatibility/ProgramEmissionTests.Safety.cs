using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static partial class ProgramEmissionTests
{
    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool value)
        {
            if (!value) throw new InvalidOperationException($"Program emit assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8); options.SetRaw("declaration"u8, "true"u8); options.SetRaw("sourceMap"u8, "true"u8);
        options.SetRaw("declarationMap"u8, "true"u8); options.SetString("target"u8, "es2015"u8); options.SetString("outDir"u8, "/out"u8);
        options.SetString("module"u8, "commonjs"u8); options.SetString("moduleResolution"u8, "bundler"u8);
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/source/input.ts"u8] = "export class C { #x = 1; value(): number { return this.#x; } }"u8.ToArray(),
            ["/source/other.ts"u8] = "export const value = 1;"u8.ToArray()
        });
        var config = new ParsedConfig("/source/tsconfig.json"u8, options, ["/source/input.ts"u8, "/source/other.ts"u8], [], [], []);
        var program = await CompilerProgram.CreateAsync(fs, "/source"u8, config);
        var source = program.GetFile("/source/input.ts"u8)!.Syntax;
        var snapshot = program.SourceFiles.SelectMany(file => file.Syntax.DescendantsAndSelf()).Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags, node.BindingId)).ToArray();
        bool Unchanged() => snapshot.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags && item.Node.BindingId == item.BindingId);
        var writes = new Dictionary<Utf8String, Utf8String>();
        var capture = new EmitOptions { WriteFile = (path, text, _, _) => { writes.Add(path, text); return ValueTask.CompletedTask; } };
        var first = await program.EmitAsync(capture);
        Check(!first.EmitSkipped && first.Diagnostics.Count == 0 && first.EmittedFiles.Count == 8 && first.SourceMaps.Count == 4);
        Check(Unchanged() && !fs.FileExists("/out/input.js"u8));
        var saved = writes.ToDictionary(); writes.Clear();
        var again = await program.EmitAsync(capture);
        Check(again.EmittedFiles.SequenceEqual(first.EmittedFiles) && writes.All(pair => saved[pair.Key] == pair.Value) && Unchanged());
        using var stop = new CancellationTokenSource(); stop.Cancel();
        try { await program.EmitAsync(capture, stop.Token); throw new InvalidOperationException("Missed cancellation"); }
        catch (OperationCanceledException) { checks++; }
        using var midway = new CancellationTokenSource();
        int callbacks = 0;
        try
        {
            await program.EmitAsync(new() { WriteFile = (_, _, _, _) => { callbacks++; midway.Cancel(); return ValueTask.CompletedTask; } }, midway.Token);
            throw new InvalidOperationException("Missed write cancellation");
        }
        catch (OperationCanceledException) { checks++; }
        Check(callbacks == 1 && Unchanged());
        writes.Clear(); await program.EmitAsync(capture);
        Check(writes.All(pair => saved[pair.Key] == pair.Value) && Unchanged());
        var written = await program.EmitAsync();
        Check(written.EmittedFiles.Count == 8 && saved.All(pair => fs.ReadFile(pair.Key)!.AsSpan().SequenceEqual(pair.Value.Span)));
        var foreign = await CompilerProgram.CreateAsync(fs, "/source"u8, config);
        try { await program.EmitAsync(capture with { SourceFiles = [foreign.GetFile(source.FileName)!.Syntax] }); throw new InvalidOperationException("Accepted foreign syntax"); }
        catch (ArgumentException) { checks++; }
        writes.Clear(); var signature = await program.EmitAsync(capture with { Only = EmitOnly.BuilderSignature, SourceFiles = [source] });
        Check(signature.EmittedFiles.Count == 1 && signature.SourceMaps.Count == 0 && !writes.Values.Single().Contains("sourceMappingURL"u8));
        var signatureText = writes.Values.Single();
        fs.WriteFile(source.FileName, "export class C { #x = 20; value(): number { return this.#x + 1; } }"u8);
        var rebuilt = await CompilerProgram.CreateAsync(fs, "/source"u8, config, program);
        Check(ReferenceEquals(program.GetFile("/source/other.ts"u8)!.Syntax, rebuilt.GetFile("/source/other.ts"u8)!.Syntax));
        writes.Clear(); await rebuilt.EmitAsync(capture with { Only = EmitOnly.BuilderSignature, SourceFiles = [rebuilt.GetFile(source.FileName)!.Syntax] });
        Check(writes.Values.Single() == signatureText && Unchanged());
        fs.WriteFile(source.FileName, "export class C { #x = 20; value(): string { return ''; } }"u8);
        var changed = await CompilerProgram.CreateAsync(fs, "/source"u8, config, rebuilt);
        writes.Clear(); await changed.EmitAsync(capture with { Only = EmitOnly.BuilderSignature, SourceFiles = [changed.GetFile(source.FileName)!.Syntax] });
        Check(writes.Values.Single() != signatureText && Unchanged());

        options.SetRaw("runExternalCode"u8, "true"u8);
        using var mapperOptions = JsonDocument.Parse("{\"echo\":true,\"supplemental\":true}");
        using var declared = JsonDocument.Parse("[]");
        var mapper = new ContentMapper("fixture"u8, [".view"u8], mapperOptions.RootElement.Clone(), "/source"u8, "fixture"u8, "1"u8,
            ["node"u8, Utf8String.FromString(Path.GetFullPath("csharp/tests/fixtures/mappers/mapper.mjs")), "utf-8"u8, "fixture"u8], declared.RootElement.Clone(), false);
        var mappedConfig = new ParsedConfig("/source/tsconfig.json"u8, options, ["/source/main.view"u8], [], [], []) { ContentMappers = [mapper] };
        fs.WriteFile("/source/main.view"u8, "export const value: number = 1;"u8);
        var mapped = await CompilerProgram.CreateAsync(fs, "/source"u8, mappedConfig);
        Check(mapped.SourceFiles.Count == 2 && mapped.SourceFiles.All(file => file.Mapping is not null));
        writes.Clear(); var mappedResult = await mapped.EmitAsync(capture);
        Check(mappedResult.EmittedFiles.Count == 4 && mappedResult.EmittedFiles.All(path => path.EndsWith(".ts"u8) || path.EndsWith(".map"u8)));
        var canonical = writes.Single(pair => pair.Key == "/out/main.d.view.ts"u8).Value;
        Check(canonical.Contains("/// <reference path=\"./main.view.0.d.ts\" />"u8));
        Check(mappedResult.SourceMaps.Count == 2);
        Check(mappedResult.SourceMaps.Single(map => map.GeneratedFile == "/out/main.d.view.ts"u8).Map.Sources.SequenceEqual(["../source/main.view"u8]));
        return checks;
    }
}

using System.Buffers;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class TranspileTests
{
    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition) throw new InvalidOperationException($"Transpile assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noEmit"u8, "true"u8);
        options.SetRaw("declaration"u8, "false"u8);
        options.SetRaw("sourceMap"u8, "true"u8);
        options.SetRaw("declarationMap"u8, "true"u8);
        options.SetString("module"u8, "nodenext"u8);
        var before = options.Values.Select(entry => (entry.Key, entry.Value.GetRawText())).ToArray();
        var settings = new TranspileOptions { CompilerOptions = options, FileName = "src/value.mts"u8, ReportDiagnostics = true };
        Utf8String input = "import {Type} from './unresolved'; export const value: Type = 1;"u8;
        var script = await Transpiler.TranspileModuleAsync(input, settings);
        var declaration = await Transpiler.TranspileDeclarationAsync(input, settings);
        Check(script.OutputText.Contains("export const value = 1"u8) && script.Diagnostics.Count == 0);
        Check(declaration.OutputText.Contains("import { Type }"u8) && declaration.Diagnostics.Count == 0);
        Check(script.SourceMapText.Contains("value.mjs"u8) && declaration.SourceMapText.Contains("value.d.mts"u8));
        Check(before.SequenceEqual(options.Values.Select(entry => (entry.Key, entry.Value.GetRawText()))));
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => await Transpiler.TranspileModuleAsync(input, settings)));
        Check(concurrent.All(result => result.OutputText == script.OutputText && result.SourceMapText == script.SourceMapText));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        foreach (bool declarations in new[] { false, true })
        {
            try
            {
                await (declarations ? Transpiler.TranspileDeclarationAsync(input, settings, stop.Token)
                    : Transpiler.TranspileModuleAsync(input, settings, stop.Token));
                throw new InvalidOperationException("Transpile missed cancellation");
            }
            catch (OperationCanceledException) { checks++; }
        }
        Check((await Transpiler.TranspileModuleAsync(input, settings)).OutputText == script.OutputText);
        var fallback = await Transpiler.TranspileDeclarationAsync("export const x = unknown();"u8);
        Check(fallback.Diagnostics.Any(diagnostic => (int)diagnostic.Code == 9010) && fallback.OutputText.Length != 0);
        var syntax = await Transpiler.TranspileModuleAsync("export const x: =;"u8, new() { ReportDiagnostics = true });
        Check(syntax.Diagnostics.Select(diagnostic => (int)diagnostic.Code).SequenceEqual([1110, 1109]));
        Check((await Transpiler.TranspileModuleAsync("export const x: =;"u8)).Diagnostics.Count == 0);
        try
        {
            await Transpiler.TranspileModuleAsync("declare const x: number;"u8, new() { FileName = "input.d.ts"u8 });
            throw new InvalidOperationException("A declaration input unexpectedly emitted JavaScript");
        }
        catch (InvalidOperationException error) when (error.Message == "Output generation failed") { checks++; }
        return checks;
    }

    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                try
                {
                    var options = new CompilerOptions();
                    if (request.TryGetProperty("options", out var configured))
                        foreach (var property in configured.EnumerateObject()) options.Set(JsonStrings.GetName(property), property.Value);
                    var settings = new TranspileOptions
                    {
                        CompilerOptions = options,
                        FileName = request.TryGetProperty("file", out var name) ? JsonStrings.GetString(name) : default,
                        ReportDiagnostics = request.TryGetProperty("report", out var report) && report.GetBoolean()
                    };
                    var text = JsonStrings.GetString(request.GetProperty("text"));
                    var result = (request.TryGetProperty("declaration", out var declaration) && declaration.GetBoolean()
                        ? Transpiler.TranspileDeclarationAsync(text, settings) : Transpiler.TranspileModuleAsync(text, settings)).GetAwaiter().GetResult();
                    writer.WriteBase64String("textBase64"u8, result.OutputText.Span);
                    writer.WriteBase64String("mapBase64"u8, result.SourceMapText.Span);
                    writer.WritePropertyName("diagnostics"u8); DeclarationEmissionTests.WriteDiagnostics(writer, result.Diagnostics);
                }
                catch (Exception error)
                {
                    writer.WriteString("error"u8, error.Message); writer.WriteString("stack"u8, error.StackTrace);
                }
                writer.WriteEndObject();
            }
            Console.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
        }
    }
}

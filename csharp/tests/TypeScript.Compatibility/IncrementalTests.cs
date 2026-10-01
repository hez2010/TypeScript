using System.Buffers;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Incremental;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class IncrementalTests
{
    internal static async Task<int> SafetyAsync()
    {
        int count = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException($"Incremental safety assertion {count + 1}"); count++; }
        async Task Canceled(Func<Task> action)
        {
            try { await action(); throw new InvalidOperationException("Expected cancellation"); }
            catch (OperationCanceledException) { count++; }
        }
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/source/tsconfig.json"u8] = "{\"compilerOptions\":{\"composite\":true,\"lib\":[\"es5\"],\"outDir\":\"/out\"},\"include\":[\"*.ts\"]}"u8.ToArray(),
            ["/source/a.ts"u8] = "export function value(): number { return 1; }"u8.ToArray(),
            ["/source/b.ts"u8] = "import { value } from './a'; export const result = value();"u8.ToArray(),
            ["/source/c.ts"u8] = "import { result } from './b'; export const copy = result;"u8.ToArray(),
            ["/source/unrelated.ts"u8] = "export const other = 1;"u8.ToArray()
        };
        IFileSystem fs = new LibraryFileSystem(new MemoryFileSystem(files, currentDirectory: "/source"u8));
        async Task<IncrementalProgram> Create(IncrementalProgram? previous = null)
        {
            var config = new ConfigParser(fs, "/source"u8).Parse("/source/tsconfig.json"u8);
            return await IncrementalProgram.CreateAsync(await CompilerProgram.CreateAsync(fs, "/source"u8, config, previous?.Program), fs, previous);
        }
        var initial = await Create();
        Check((await initial.GetDiagnosticsAsync()).Count == 0);
        var firstEmit = await initial.EmitAsync();
        Check(firstEmit.EmittedFiles.Count == 9 && initial.HasChangedDeclaration);
        var initialState = (await initial.GetBuildInfoAsync()).ToJson();
        var unchanged = await Create(initial);
        Check((await unchanged.GetDiagnosticsAsync()).Count == 0);
        Check(unchanged.CheckedFiles.Count == 0 && unchanged.Program.ReusedSourceFiles == initial.Program.SourceFiles.Count);
        Check((await unchanged.EmitAsync()).EmittedFiles.Count == 0);
        var restarted = await Create();
        Check((await restarted.GetDiagnosticsAsync()).Count == 0 && restarted.CheckedFiles.Count == 0);
        Check((await restarted.EmitAsync()).EmittedFiles.Count == 0);
        fs.WriteFile("/source/a.ts"u8, "export function value(): number { return 2; }"u8);
        var implementation = await Create(unchanged);
        await implementation.GetDiagnosticsAsync();
        Check(implementation.CheckedFiles.Count == 1 && implementation.CheckedFiles.Contains("/source/a.ts"u8));
        var implementationEmit = await implementation.EmitAsync();
        Check(implementationEmit.EmittedFiles.SequenceEqual(new Utf8String[] { "/out/a.js"u8, "/out/tsconfig.tsbuildinfo"u8 }));
        Check(!implementation.HasChangedDeclaration);
        Check((await initial.GetBuildInfoAsync()).ToJson().AsSpan().SequenceEqual(initialState));
        fs.WriteFile("/source/a.ts"u8, "export function value(): string { return 'text'; }"u8);
        var publicChange = await Create(implementation);
        await publicChange.GetDiagnosticsAsync();
        Check(publicChange.CheckedFiles.Contains("/source/a.ts"u8) && publicChange.CheckedFiles.Contains("/source/b.ts"u8)
            && publicChange.CheckedFiles.Contains("/source/c.ts"u8) && !publicChange.CheckedFiles.Contains("/source/unrelated.ts"u8));
        var savedInfo = fs.ReadFile("/out/tsconfig.tsbuildinfo"u8)!;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Canceled(async () => await publicChange.GetDiagnosticsAsync(canceled.Token));
        await Canceled(async () => await publicChange.EmitAsync(new() { WriteFile = (_, _, _, _) => throw new OperationCanceledException() }));
        Check(fs.ReadFile("/out/tsconfig.tsbuildinfo"u8)!.AsSpan().SequenceEqual(savedInfo));
        var retry = await publicChange.EmitAsync();
        Check(retry.Diagnostics.Count == 0 && retry.EmittedFiles.Contains("/out/a.d.ts"u8));
        var finalState = (await publicChange.GetBuildInfoAsync()).ToJson();
        var ownedInfo = await publicChange.GetBuildInfoAsync();
        ownedInfo.FileNames![0] = "corrupt"u8;
        Check((await publicChange.GetBuildInfoAsync()).ToJson().AsSpan().SequenceEqual(finalState));
        var reloaded = await Create();
        Check((await reloaded.GetDiagnosticsAsync()).Count == 0 && reloaded.CheckedFiles.Count == 0);
        Check((await reloaded.EmitAsync()).EmittedFiles.Count == 0);
        fs.WriteFile("/out/tsconfig.tsbuildinfo"u8,
            "{\"version\":\"7.1.0-dev\",\"fileNames\":[\"../source/a.ts\",\"../source/a.ts\"],\"fileInfos\":[\"x\",\"x\"]}"u8);
        var corrupt = await Create();
        Check((await corrupt.GetDiagnosticsAsync()).Count == 0 && corrupt.CheckedFiles.Count != 0);
        return count;
    }

    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            try { Console.WriteLine(ExecuteAsync(document.RootElement).GetAwaiter().GetResult()); }
            catch (Exception error)
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject(); writer.WriteString("error", error.Message); writer.WriteString("stack", error.StackTrace); writer.WriteEndObject();
                }
                Console.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
            }
        }
    }

    private static async Task<string> ExecuteAsync(JsonElement input)
    {
        Utf8String Text(JsonElement value, string name, Utf8String fallback = default) => value.TryGetProperty(name, out var text) ? JsonStrings.GetString(text) : fallback;
        bool Flag(string name) => input.TryGetProperty(name, out var value) && value.GetBoolean();
        var cwd = Text(input, "cwd", "/source"u8);
        var configPath = Text(input, "config", CompilerPath.Combine(cwd, "tsconfig.json"u8));
        var files = input.GetProperty("files").EnumerateObject().ToDictionary(JsonStrings.GetName, property => JsonStrings.GetString(property.Value).Span.ToArray());
        IFileSystem fs = new LibraryFileSystem(new MemoryFileSystem(files, !Flag("caseInsensitive"), cwd));
        IncrementalProgram? previous = null;
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray();
            foreach (var step in input.GetProperty("steps").EnumerateArray())
            {
                if (step.TryGetProperty("edits", out var edits))
                    foreach (var edit in edits.EnumerateObject())
                    {
                        if (edit.Value.ValueKind == JsonValueKind.Null) fs.Remove(JsonStrings.GetName(edit));
                        else fs.WriteFile(JsonStrings.GetName(edit), JsonStrings.GetString(edit.Value));
                    }
                if (step.TryGetProperty("fresh", out var fresh) && fresh.GetBoolean()) previous = null;
                var config = new ConfigParser(fs, cwd).Parse(configPath);
                if (Flag("singleThreaded")) config.Options.SetRaw("singleThreaded"u8, "true"u8);
                var program = await CompilerProgram.CreateAsync(fs, cwd, config, previous?.Program);
                var current = await IncrementalProgram.CreateAsync(program, fs, previous, hashWithText: Flag("hashWithText"));
                var diagnostics = await current.GetDiagnosticsAsync();
                List<(Utf8String Path, Utf8String Text, BuildInfo? Info)> writes = [];
                void Write(Utf8String path, Utf8String text, BuildInfo? info = null)
                {
                    if (path == Text(step, "failWrite")) throw new IOException("test write failure");
                    fs.WriteFile(path, text);
                    writes.Add((path, text, info));
                }
                var emitted = await current.EmitAsync(new()
                {
                    WriteFile = (path, text, _, _) => { Write(path, text); return ValueTask.CompletedTask; }
                }, (path, info, _) => { Write(path, new(info.ToJson()), info); return ValueTask.CompletedTask; });
                writer.WriteStartObject();
                writer.WritePropertyName("diagnostics"); DeclarationEmissionTests.WriteDiagnostics(writer, DiagnosticCollection.SortAndDeduplicate(diagnostics.Concat(emitted.Diagnostics)));
                writer.WriteStartArray("emitted"); foreach (var path in emitted.EmittedFiles) JsonStrings.WriteString(writer, path); writer.WriteEndArray();
                writer.WriteStartArray("sources"); foreach (var file in program.SourceFiles) JsonStrings.WriteString(writer, file.Syntax.FileName); writer.WriteEndArray();
                writer.WriteBoolean("skipped", emitted.EmitSkipped); writer.WriteBoolean("changedDeclaration", current.HasChangedDeclaration);
                writer.WriteStartArray("writes");
                foreach (var write in writes.OrderBy(write => write.Path, Utf8StringComparer.Ordinal))
                {
                    writer.WriteStartObject(); writer.WritePropertyName("path"); JsonStrings.WriteString(writer, write.Path);
                    if (write.Info is not null) { writer.WritePropertyName("buildInfo"); writer.WriteRawValue(write.Info.ToJson()); }
                    else writer.WriteBase64String("textBase64", write.Text.Span);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                if (fs.ReadFile(current.BuildInfoFileName) is { } buildInfo)
                {
                    writer.WritePropertyName("buildInfo"); writer.WriteRawValue(buildInfo);
                }
                writer.WriteEndObject();
                previous = current;
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }
}

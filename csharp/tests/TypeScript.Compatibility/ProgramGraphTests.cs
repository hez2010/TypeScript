using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class ProgramGraphTests
{
    internal static async Task Safety()
    {
        int assertions = 0;
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition)
                throw new InvalidDataException(message);
        }
        static JsonElement Json(string text)
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        var options = new CompilerOptions();
        options.Set("noLib", Json("true"));
        options.Set("noEmit", Json("true"));
        var fs = new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/a.ts"] = Wtf8.Encode("import './b'; export const a = 1;"),
            ["/project/b.ts"] = Wtf8.Encode("export const b = 1;")
        });
        var config = new ParsedConfig("/project/tsconfig.json", options, ["/project/a.ts"], [], [], []);
        var first = await CompilerProgram.CreateAsync(fs, "/project", config, concurrency: 1);
        var second = await CompilerProgram.CreateAsync(fs, "/project", config, first, concurrency: 4);
        Check(
            second.ReusedSourceFiles == 2
                && ReferenceEquals(first.GetFile("/project/a.ts")!.Syntax, second.GetFile("/project/a.ts")!.Syntax),
            "Unchanged syntax is retained by reference");
        Check(
            ReferenceEquals(first.GetFile("/project/a.ts")!.Binding, second.GetFile("/project/a.ts")!.Binding),
            "Unchanged syntax retains symbol identity");
        fs.WriteFile("/project/b.ts", Wtf8.Encode("export const replacement = 2;"));
        var third = await CompilerProgram.CreateAsync(fs, "/project", config, second);
        Check(
            third.ReusedSourceFiles == 1 && third.GetFile("/project/b.ts")!.Binding.Symbol!.Exports.ContainsKey("replacement"),
            "Changed files rebuild binding");
        Check(
            first.GetFile("/project/b.ts")!.Binding.Symbol!.Exports.ContainsKey("b")
                && !first.GetFile("/project/b.ts")!.Binding.Symbol!.Exports.ContainsKey("replacement"),
            "Older snapshots preserve their symbols");
        fs.WriteFile("/project/a.ts", Wtf8.Encode("import './missing';"));
        var missing = await CompilerProgram.CreateAsync(fs, "/project", config, third);
        Check(!missing.GetFile("/project/a.ts")!.Resolutions[0].Resolution.IsResolved, "Negative module resolution is recorded");
        fs.WriteFile("/project/missing.ts", Wtf8.Encode("export const found=1;"));
        var found = await CompilerProgram.CreateAsync(fs, "/project", config, missing);
        Check(
            found.GetFile("/project/missing.ts") is not null && found.GetFile("/project/b.ts") is null,
            "Filesystem changes invalidate negative resolutions and remove unreachable files");
        Check(
            ReferenceEquals(found.GetFile("/project/a.ts")!.Syntax, missing.GetFile("/project/a.ts")!.Syntax),
            "Graph invalidation need not discard unchanged syntax");
        var resolver = new ModuleResolver(fs, options, "/project");
        var negative = resolver.Resolve("./new", "/project/a.ts");
        fs.WriteFile("/project/new.ts", Wtf8.Encode("export {}"));
        resolver.Invalidate();
        Check(
            !negative.IsResolved && resolver.Resolve("./new", "/project/a.ts").IsResolved,
            "Explicit resolver invalidation refreshes negative caches");
        fs.WriteFile("/project/node_modules/p/package.json", Wtf8.Encode("{\"types\":\"one.d.ts\"}"));
        fs.WriteFile("/project/node_modules/p/one.d.ts", Wtf8.Encode("export {}"));
        fs.WriteFile("/project/node_modules/p/two.d.ts", Wtf8.Encode("export {}"));
        Check(resolver.Resolve("p", "/project/a.ts").FileName.EndsWith("/one.d.ts", StringComparison.Ordinal), "Package path is resolved");
        fs.WriteFile("/project/node_modules/p/package.json", Wtf8.Encode("{\"types\":\"two.d.ts\"}"));
        resolver.Invalidate();
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => resolver.ResolveAsync("p", "/project/a.ts").AsTask()));
        Check(
            concurrent.All(r => r.FileName.EndsWith("/two.d.ts", StringComparison.Ordinal)),
            "Package invalidation is consistent across concurrent readers");
        options.Set("moduleDetection", Json("\"force\""));
        Check(first.Configuration.Options.String("moduleDetection") is null, "Programs own their compiler option snapshot");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await CompilerProgram.CreateAsync(fs, "/project", config, cancellation: cancelled.Token);
            throw new InvalidDataException("Expected program cancellation");
        }
        catch (OperationCanceledException)
        {
            assertions++;
        }
        const int depth = 12000;
        var deep = Parser.ParseSourceFile(
            new("/project/deep.ts"),
            new SourceText("function f(){ return " + new string('(', depth) + "value" + new string(')', depth) + "; }"));
        var previousContext = SynchronizationContext.Current;
        ValueTask<BoundSourceFile> operation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new RejectContext());
            operation = Binder.BindAsync(deep);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        var bound = await operation.ConfigureAwait(false);
        Check(
            bound.Diagnostics.Count == 0 && bound.Locals.ContainsKey("f"),
            "Deep binding completes without a native recursion limit or synchronization context dependency");
        Check(ReferenceEquals(bound, await Binder.BindAsync(deep)), "Completed binding publication is idempotent");
        const int chainLength = 3000;
        var chain = new Dictionary<string, byte[]>();
        for (int i = 0; i < chainLength; i++)
            chain["/chain/" + i + ".ts"] = Wtf8.Encode(i + 1 < chainLength ? "import './" + (i + 1) + "';" : "export {};");
        var chainOptions = new CompilerOptions();
        chainOptions.Set("noLib", Json("true"));
        var chainConfig = new ParsedConfig("/chain/tsconfig.json", chainOptions, ["/chain/0.ts"], [], [], []);
        var graph = await CompilerProgram.CreateAsync(new MemoryFileSystem(chain), "/chain", chainConfig);
        Check(graph.SourceFiles.Count == chainLength && graph.SourceFiles[0].Syntax.FileName == "/chain/2999.ts"
            && graph.SourceFiles[^1].Syntax.FileName == "/chain/0.ts", "Deep import graph uses deterministic iterative postorder");
        var jsOptions = new CompilerOptions();
        jsOptions.Set("noLib", Json("true"));
        jsOptions.Set("allowJs", Json("true"));
        jsOptions.Set("module", Json("\"commonjs\""));
        var jsRoots = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.js"] = Wtf8.Encode("import { a } from 'b'; import { c } from 'c';"),
            ["/project/node_modules/b.ts"] = Wtf8.Encode("var a = 10;"),
            ["/project/node_modules/c.js"] = Wtf8.Encode("exports.a = 10;")
        }), "/project", new("/project/tsconfig.json", jsOptions,
            ["/project/main.js", "/project/node_modules/b.ts", "/project/node_modules/c.js"], [], [], []));
        Check(jsRoots.SourceFiles.Select(f => f.Syntax.FileName).SequenceEqual(
            ["/project/node_modules/b.ts", "/project/node_modules/c.js", "/project/main.js"]),
            "JavaScript depth elision retains dependency ordering when the target is also a root");
        foreach (bool allow in new[] { false, true })
        {
            var arbitraryOptions = new CompilerOptions();
            arbitraryOptions.Set("noLib", Json("true"));
            arbitraryOptions.Set("module", Json("\"nodenext\""));
            arbitraryOptions.Set("allowArbitraryExtensions", Json(allow ? "true" : "false"));
            var arbitrary = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
            {
                ["/project/main.ts"] = Wtf8.Encode("import mod = require('./native.node'); mod.value;"),
                ["/project/native.d.node.ts"] = Wtf8.Encode("export const value: number;")
            }), "/project", new("/project/tsconfig.json", arbitraryOptions, ["/project/main.ts"], [], [], []));
            Check(arbitrary.SourceFiles.Count == (allow ? 2 : 1), "Arbitrary-extension imports respect the graph inclusion option");
            var checker = await arbitrary.CreateCheckerAsync();
            await checker.CheckProgramAsync();
            Check(checker.DiagnosticCodesForProgramFile(arbitrary.GetFile("/project/main.ts")!.Syntax)
                .SequenceEqual(allow ? [] : new[] { DiagnosticCode.Module0WasResolvedTo1ButAllowArbitraryExtensionsIsNotSet }),
                "Disallowed arbitrary-extension imports report the option diagnostic");
        }
        Console.WriteLine(
            $"Program reuse, invalidation and stack safety: {assertions} assertions; {depth} syntax levels; {chainLength} files");
    }

    private sealed class RejectContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            throw new InvalidOperationException("Captured hostile synchronization context");
    }

    internal static async Task Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var input = document.RootElement;
            string directory = input.GetProperty("directory").GetString()!;
            var files = new Dictionary<string, byte[]>();
            foreach (var file in input.GetProperty("files").EnumerateObject())
                files[file.Name] = Wtf8.Encode(JsonStrings.GetString(file.Value));
            var links = new Dictionary<string, string>();
            if (input.TryGetProperty("symlinks", out var symlinks))
                foreach (var link in symlinks.EnumerateObject())
                    links[link.Name] = JsonStrings.GetString(link.Value);
            string config;
            if (input.TryGetProperty("config", out var configName))
                config = configName.GetString()!;
            else
            {
                config = directory + "/tsconfig.json";
                using var configStream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(configStream))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("compilerOptions");
                    input.GetProperty("options").WriteTo(writer);
                    writer.WritePropertyName("files");
                    input.GetProperty("roots").WriteTo(writer);
                    writer.WriteEndObject();
                }
                files[config] = configStream.ToArray();
            }
            var fs = new LibraryFileSystem(new MemoryFileSystem(files, input.GetProperty("sensitive").GetBoolean(), directory, links));
            var parsed = new ConfigParser(fs, directory).Parse(config);
            int concurrency = input.TryGetProperty("concurrency", out var count) ? count.GetInt32() : 4;
            var program = await CompilerProgram.CreateAsync(fs, directory, parsed,
                useProjectReferenceSources: input.TryGetProperty("useSources", out var source) && source.GetBoolean(),
                concurrency: concurrency).ConfigureAwait(false);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                writer.WriteStartArray();
                foreach (var file in program.SourceFiles)
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(file.Syntax.FileName);
                    writer.WriteBooleanValue(file.Binding.IsModule);
                    writer.WriteNumberValue((int)file.ImpliedFormat);
                    writer.WriteStringValue(file.PackageType);
                    writer.WriteStartArray();
                    foreach (var import in file.Resolutions.Where(r => !r.TypeReference && !r.Augmentation && r.Node is not null))
                    {
                        writer.WriteStartArray();
                        writer.WriteStringValue(import.Specifier);
                        writer.WriteStringValue(import.Resolution.FileName);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteStartArray();
                foreach (var error in program.Diagnostics)
                    writer.WriteNumberValue((int)error.Code);
                writer.WriteEndArray();
                writer.WriteEndArray();
            }
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }
}

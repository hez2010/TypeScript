using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class ModuleSpecifierProgramTests
{
    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Program module naming assertion {checks + 1}");
            checks++;
        }
        var fs = new ObservedFileSystem(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.ts"] = Wtf8.Encode("export const main=1;"),
            ["/project/package.json"] = Wtf8.Encode("{\"dependencies\":{\"pkg\":\"*\"}}"),
            ["/store/pkg/package.json"] = Wtf8.Encode("{\"name\":\"pkg\",\"version\":\"1\",\"types\":\"item.ts\"}"),
            ["/store/pkg/item.ts"] = Wtf8.Encode("export const item=1;")
        }, symbolicLinks: new Dictionary<string, string> { ["/project/node_modules/pkg"] = "/store/pkg" }));
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        var program = await CompilerProgram.CreateAsync(
            fs,
            "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts", "/store/pkg/item.ts"], [], [], []));
        var source = program.GetFile("/project/main.ts")!.Syntax;
        Check(program.CommonSourceDirectory == "/project/");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            program.GetModuleSpecifiers(source, "/store/pkg/item.ts", cancellation: stop.Token);
            throw new InvalidOperationException("Pre-cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        using var duringRead = new CancellationTokenSource();
        fs.BeforeRead = _ => duringRead.Cancel();
        try
        {
            program.GetModuleSpecifiers(source, "/store/pkg/item.ts", cancellation: duringRead.Token);
            throw new InvalidOperationException("Host cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        fs.BeforeRead = _ => throw new IOException("host read failure");
        try
        {
            program.GetModuleSpecifiers(source, "/store/pkg/item.ts");
            throw new InvalidOperationException("Host failure ignored");
        }
        catch (IOException error) when (error.Message == "host read failure")
        {
            checks++;
        }
        fs.BeforeRead = null;
        var result = program.GetModuleSpecifiers(source, "/store/pkg/item.ts");
        Check(result.Kind == ModuleSpecifierKind.NodeModules && result.Specifiers.SequenceEqual(["pkg"]));
        int reads = fs.Reads;
        Check(program.GetModuleSpecifiers(source, "/store/pkg/item.ts").Specifiers.SequenceEqual(["pkg"]));
        Check(fs.Reads == reads);
        try
        {
            ((IList<string>)result.Specifiers)[0] = "bad";
            throw new InvalidOperationException("Mutable naming result");
        }
        catch (NotSupportedException)
        {
            checks++;
        }
        var paths = program.GetModuleSpecifierPaths(source, "/store/pkg/item.ts");
        Check(paths.Any(p => p.FileName == "/project/node_modules/pkg/item.ts"));
        try
        {
            program.GetModuleSpecifiers(Parser.ParseSourceFile(new("/project/main.ts"), new SourceText("")), "/store/pkg/item.ts");
            throw new InvalidOperationException("Foreign source accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        var results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(() => program.GetModuleSpecifiers(source, "/store/pkg/item.ts"))));
        Check(results.All(r => r.Specifiers.SequenceEqual(["pkg"])) && fs.Reads == reads);
        Check(program.SourceFileMayBeEmitted(source));
        return checks;
    }

    private sealed class ObservedFileSystem(IFileSystem inner) : IFileSystem
    {
        internal Action<string>? BeforeRead { get; set; }
        internal int Reads { get; private set; }
        public bool CaseSensitive => inner.CaseSensitive;

        public byte[]? ReadFile(string path)
        {
            Reads++;
            BeforeRead?.Invoke(path);
            return inner.ReadFile(path);
        }

        public bool FileExists(string path) => inner.FileExists(path);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public string RealPath(string path) => inner.RealPath(path);

        public DirectoryEntries GetAccessibleEntries(string path) => inner.GetAccessibleEntries(path);

        public FileEntry? Stat(string path) => inner.Stat(path);

        public void WriteFile(string path, ReadOnlySpan<byte> contents) => inner.WriteFile(path, contents);

        public void AppendFile(string path, ReadOnlySpan<byte> contents) => inner.AppendFile(path, contents);

        public void Remove(string path) => inner.Remove(path);

        public void SetTimes(string path, DateTime accessTimeUtc, DateTime writeTimeUtc) =>
            inner.SetTimes(path, accessTimeUtc, writeTimeUtc);
    }

    internal static async ValueTask<int> WriteAsync(JsonElement input, Utf8JsonWriter writer)
    {
        string Text(string key, string fallback = "") => input.TryGetProperty(key, out var value) ? JsonStrings.GetString(value) : fallback;
        bool Flag(string key) => input.TryGetProperty(key, out var value) && value.GetBoolean();
        var files = input.GetProperty("files").EnumerateObject().ToDictionary(
            p => p.Name,
            p => Wtf8.Encode(JsonStrings.GetString(p.Value)));
        var links = input.TryGetProperty("fileLinks", out var fileLinks)
            ? fileLinks.EnumerateObject().ToDictionary(p => p.Name, p => JsonStrings.GetString(p.Value)) : null;
        string cwd = Text("directory"), configPath = Text("config");
        if (configPath.Length == 0)
        {
            configPath = CompilerPath.Combine(cwd, "tsconfig.json");
            using var stream = new MemoryStream();
            using (var configWriter = new Utf8JsonWriter(stream))
            {
                configWriter.WriteStartObject();
                configWriter.WritePropertyName("compilerOptions");
                input.GetProperty("options").WriteTo(configWriter);
                configWriter.WritePropertyName("files");
                input.GetProperty("roots").WriteTo(configWriter);
                configWriter.WriteEndObject();
            }
            files[configPath] = stream.ToArray();
        }
        var fs = new MemoryFileSystem(files, Flag("sensitive"), cwd, links);
        var config = new ConfigParser(fs, cwd).Parse(configPath);
        var program = await CompilerProgram.CreateAsync(fs, cwd, config, useProjectReferenceSources: Flag("useSources"),
            concurrency: input.TryGetProperty("concurrency", out var concurrency) ? concurrency.GetInt32() : 4,
            globalTypingsCache: Text("globalTypingsCache"));
        writer.WriteStartArray();
        writer.WriteStringValue(program.CommonSourceDirectory);
        writer.WriteStartArray();
        foreach (var file in program.SourceFiles)
        {
            var source = file.Syntax;
            writer.WriteStartArray();
            writer.WriteStringValue(source.FileName);
            writer.WriteStringValue(
                program.ProjectReferences.Outputs.TryGetValue(source.FileName, out var output) ? output.Source : source.FileName);
            writer.WriteStringValue(
                program.ProjectReferences.Sources.TryGetValue(source.FileName, out var reference) ? reference.Output : "");
            writer.WriteNumberValue((int)program.ResolutionModeForUsage(source, null));
            writer.WriteBooleanValue(program.SourceFileMayBeEmitted(source));
            writer.WriteStartArray();
            foreach (var import in source.Imports)
            {
                string text = import is TypeScript.Compiler.Ast.StringLiteralNode literal
                    ? literal.Text
                    : ((TypeScript.Compiler.Ast.NoSubstitutionTemplateLiteralNode)import).Text;
                var mode = program.ResolutionModeForUsage(source, import);
                var resolved = file.Resolutions.FirstOrDefault(r => !r.TypeReference && r.Specifier == text && r.Mode == mode)?.Resolution;
                writer.WriteStartArray();
                writer.WriteStringValue(text);
                writer.WriteStringValue(resolved?.FileName ?? "");
                writer.WriteNumberValue((int)mode);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteStartArray();
        foreach (var source in program.SourceFiles)
            foreach (var target in program.SourceFiles)
                foreach (string preference in new[] { "shortest", "project-relative", "non-relative" })
                    foreach (var mode in new[]
                    {
                        ReferenceResolutionMode.Unspecified,
                        ReferenceResolutionMode.Require,
                        ReferenceResolutionMode.Import
                    })
                    {
                        var result = program.GetModuleSpecifiers(source.Syntax, target.Syntax.FileName, new(preference), mode);
                        writer.WriteStartArray();
                        writer.WriteStringValue(source.Syntax.FileName);
                        writer.WriteStringValue(target.Syntax.FileName);
                        writer.WriteStringValue(preference);
                        writer.WriteNumberValue((int)mode);
                        writer.WriteNumberValue((int)result.Kind);
                        writer.WriteStartArray();
                        foreach (string name in result.Specifiers)
                            writer.WriteStringValue(name);
                        writer.WriteEndArray();
                        writer.WriteStartArray();
                        foreach (var path in program.GetModuleSpecifierPaths(source.Syntax, target.Syntax.FileName))
                        {
                            writer.WriteStartObject();
                            writer.WriteString("FileName", path.FileName);
                            writer.WriteBoolean("IsInNodeModules", path.IsInNodeModules);
                            writer.WriteBoolean("IsRedirect", path.IsRedirect);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                        writer.WriteEndArray();
                    }
        writer.WriteEndArray();
        writer.WriteStartArray();
        foreach (var diagnostic in program.Diagnostics.OrderBy(d => d.Code))
            writer.WriteNumberValue(diagnostic.Code);
        writer.WriteEndArray();
        CheckerCorpusTests.WriteDiagnostics(writer, program.Diagnostics);
        writer.WriteEndArray();
        return 0;
    }
}

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
        var fs = new ObservedFileSystem(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode("export const main=1;"),
            ["/project/package.json"u8] = Wtf8.Encode("{\"dependencies\":{\"pkg\":\"*\"}}"),
            ["/store/pkg/package.json"u8] = Wtf8.Encode("{\"name\":\"pkg\",\"version\":\"1\",\"types\":\"item.ts\"}"),
            ["/store/pkg/item.ts"u8] = Wtf8.Encode("export const item=1;")
        }, symbolicLinks: new Dictionary<Utf8String, Utf8String> { ["/project/node_modules/pkg"u8] = "/store/pkg"u8 }));
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(
            fs,
            "/project"u8,
            new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8, "/store/pkg/item.ts"u8], [], [], []));
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        Check(program.CommonSourceDirectory == "/project/"u8);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            program.GetModuleSpecifiers(source, "/store/pkg/item.ts"u8, cancellation: stop.Token);
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
            program.GetModuleSpecifiers(source, "/store/pkg/item.ts"u8, cancellation: duringRead.Token);
            throw new InvalidOperationException("Host cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        fs.BeforeRead = _ => throw new IOException("host read failure");
        try
        {
            program.GetModuleSpecifiers(source, "/store/pkg/item.ts"u8);
            throw new InvalidOperationException("Host failure ignored");
        }
        catch (IOException error) when (error.Message == "host read failure")
        {
            checks++;
        }
        fs.BeforeRead = null;
        var result = program.GetModuleSpecifiers(source, "/store/pkg/item.ts"u8);
        Check(result.Kind == ModuleSpecifierKind.NodeModules && result.Specifiers.SequenceEqual([Utf8String.Copy("pkg"u8)]));
        int reads = fs.Reads;
        Check(program.GetModuleSpecifiers(source, "/store/pkg/item.ts"u8).Specifiers.SequenceEqual([Utf8String.Copy("pkg"u8)]));
        Check(fs.Reads == reads);
        try
        {
            ((IList<Utf8String>)result.Specifiers)[0] = "bad"u8;
            throw new InvalidOperationException("Mutable naming result");
        }
        catch (NotSupportedException)
        {
            checks++;
        }
        var paths = program.GetModuleSpecifierPaths(source, "/store/pkg/item.ts"u8);
        Check(paths.Any(p => p.FileName == "/project/node_modules/pkg/item.ts"u8));
        try
        {
            program.GetModuleSpecifiers(Parser.ParseSourceFile(new("/project/main.ts"u8), new SourceText(""u8)), "/store/pkg/item.ts"u8);
            throw new InvalidOperationException("Foreign source accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        var results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(() => program.GetModuleSpecifiers(source, "/store/pkg/item.ts"u8))));
        Check(results.All(r => r.Specifiers.SequenceEqual([Utf8String.Copy("pkg"u8)])) && fs.Reads == reads);
        Check(program.SourceFileMayBeEmitted(source));
        return checks;
    }

    private sealed class ObservedFileSystem(IFileSystem inner) : IFileSystem
    {
        internal Action<Utf8String>? BeforeRead { get; set; }
        internal int Reads { get; private set; }
        public bool CaseSensitive => inner.CaseSensitive;

        public byte[]? ReadFile(Utf8String path)
        {
            Reads++;
            BeforeRead?.Invoke(path);
            return inner.ReadFile(path);
        }

        public bool FileExists(Utf8String path) => inner.FileExists(path);

        public bool DirectoryExists(Utf8String path) => inner.DirectoryExists(path);

        public Utf8String RealPath(Utf8String path) => inner.RealPath(path);

        public DirectoryEntries GetAccessibleEntries(Utf8String path) => inner.GetAccessibleEntries(path);

        public FileEntry? Stat(Utf8String path) => inner.Stat(path);

        public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents) => inner.WriteFile(path, contents);

        public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents) => inner.AppendFile(path, contents);

        public void Remove(Utf8String path) => inner.Remove(path);

        public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc) =>
            inner.SetTimes(path, accessTimeUtc, writeTimeUtc);
    }

    internal static async ValueTask<int> WriteAsync(JsonElement input, Utf8JsonWriter writer)
    {
        Utf8String Text(Utf8String key, Utf8String fallback = default) => input.TryGetProperty(key, out var value) ? JsonStrings.GetString(value) : fallback;
        bool Flag(Utf8String key) => input.TryGetProperty(key, out var value) && value.GetBoolean();
        var files = input.GetProperty("files"u8).EnumerateObject().ToDictionary(
            p => JsonStrings.GetName(p),
            p => JsonStrings.GetString(p.Value).Span.ToArray());
        var links = input.TryGetProperty("fileLinks"u8, out var fileLinks)
            ? fileLinks.EnumerateObject().ToDictionary(p => JsonStrings.GetName(p), p => JsonStrings.GetString(p.Value)) : null;
        Utf8String cwd = Text("directory"u8), configPath = Text("config"u8);
        if (configPath.Length == 0)
        {
            configPath = CompilerPath.Combine(cwd, "tsconfig.json"u8);
            using var stream = new MemoryStream();
            using (var configWriter = new Utf8JsonWriter(stream))
            {
                configWriter.WriteStartObject();
                configWriter.WritePropertyName("compilerOptions"u8);
                input.GetProperty("options"u8).WriteTo(configWriter);
                configWriter.WritePropertyName("files"u8);
                input.GetProperty("roots"u8).WriteTo(configWriter);
                configWriter.WriteEndObject();
            }
            files[configPath] = stream.ToArray();
        }
        var fs = new MemoryFileSystem(files, Flag("sensitive"u8), cwd, links);
        var config = new ConfigParser(fs, cwd).Parse(configPath);
        var program = await CompilerProgram.CreateAsync(fs, cwd, config, useProjectReferenceSources: Flag("useSources"u8),
            concurrency: input.TryGetProperty("concurrency"u8, out var concurrency) ? concurrency.GetInt32() : 4,
            globalTypingsCache: Text("globalTypingsCache"u8));
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
                program.ProjectReferences.Sources.TryGetValue(source.FileName, out var reference) ? reference.Output : Utf8String.Empty);
            writer.WriteNumberValue((int)program.ResolutionModeForUsage(source, null));
            writer.WriteBooleanValue(program.SourceFileMayBeEmitted(source));
            writer.WriteStartArray();
            foreach (var import in source.Imports)
            {
                Utf8String text = import is TypeScript.Compiler.Ast.StringLiteralNode literal
                    ? literal.Text
                    : ((TypeScript.Compiler.Ast.NoSubstitutionTemplateLiteralNode)import).Text;
                var mode = program.ResolutionModeForUsage(source, import);
                var resolved = file.Resolutions.FirstOrDefault(r => !r.TypeReference && r.Specifier == text && r.Mode == mode)?.Resolution;
                writer.WriteStartArray();
                writer.WriteStringValue(text.Span);
                writer.WriteStringValue(resolved?.FileName ?? ""u8);
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
                foreach (Utf8String preference in new Utf8String[] { "shortest"u8, "project-relative"u8, "non-relative"u8 })
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
                        foreach (Utf8String name in result.Specifiers)
                            writer.WriteStringValue(name);
                        writer.WriteEndArray();
                        writer.WriteStartArray();
                        foreach (var path in program.GetModuleSpecifierPaths(source.Syntax, target.Syntax.FileName))
                        {
                            writer.WriteStartObject();
                            writer.WriteString("FileName"u8, path.FileName);
                            writer.WriteBoolean("IsInNodeModules"u8, path.IsInNodeModules);
                            writer.WriteBoolean("IsRedirect"u8, path.IsRedirect);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                        writer.WriteEndArray();
                    }
        writer.WriteEndArray();
        writer.WriteStartArray();
        foreach (var diagnostic in program.Diagnostics.OrderBy(d => d.Code))
            writer.WriteNumberValue((int)diagnostic.Code);
        writer.WriteEndArray();
        CheckerCorpusTests.WriteDiagnostics(writer, program.Diagnostics);
        writer.WriteEndArray();
        return 0;
    }
}

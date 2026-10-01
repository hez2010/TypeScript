using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static partial class ProgramEmissionTests
{
    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            try { Console.WriteLine(EmitAsync(document.RootElement).GetAwaiter().GetResult()); }
            catch (Exception error)
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject(); writer.WriteString("error"u8, error.Message); writer.WriteString("stack"u8, error.StackTrace); writer.WriteEndObject();
                }
                Console.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
            }
        }
    }

    private static async Task<string> EmitAsync(JsonElement request)
    {
        Utf8String Text(string name, Utf8String fallback = default) => request.TryGetProperty(name, out var value) ? JsonStrings.GetString(value) : fallback;
        bool Flag(string name) => request.TryGetProperty(name, out var value) && value.GetBoolean();
        var cwd = Text("cwd", "/source"u8);
        var fileName = Text("file", CompilerPath.Combine(cwd, "input.ts"u8));
        var files = new Dictionary<Utf8String, byte[]>();
        if (!Flag("corpus")) files[fileName] = Text("text").Span.ToArray();
        if (request.TryGetProperty("files", out var extras))
            foreach (var property in extras.EnumerateObject()) files[JsonStrings.GetName(property)] = JsonStrings.GetString(property.Value).Span.ToArray();
        var configPath = CompilerPath.Combine(cwd, "tsconfig.json"u8);
        var configBytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(configBytes))
        {
            writer.WriteStartObject(); writer.WriteStartObject("compilerOptions"u8);
            if (!Flag("corpus"))
            {
                writer.WriteBoolean("noLib"u8, true); writer.WriteString("module"u8, "esnext"u8); writer.WriteString("moduleResolution"u8, "bundler"u8);
                writer.WriteString("outDir"u8, "/out"u8);
            }
            if (request.TryGetProperty("options", out var options)) foreach (var property in options.EnumerateObject()) property.WriteTo(writer);
            writer.WriteEndObject(); writer.WritePropertyName("files"u8);
            if (request.TryGetProperty("roots", out var roots)) roots.WriteTo(writer);
            else { writer.WriteStartArray(); writer.WriteStringValue(fileName.Span); writer.WriteEndArray(); }
            writer.WriteEndObject();
        }
        if (!Flag("corpus")) files[configPath] = request.TryGetProperty("config", out var configText)
            ? JsonStrings.GetString(configText).Span.ToArray() : configBytes.WrittenSpan.ToArray();
        var links = request.TryGetProperty("symlinks", out var symlinks)
            ? symlinks.EnumerateObject().ToDictionary(JsonStrings.GetName, entry => JsonStrings.GetString(entry.Value)) : null;
        IFileSystem fs = new MemoryFileSystem(files, !Flag("caseInsensitive"), cwd, links);
        ParsedConfig config;
        if (Flag("corpus"))
        {
            fs = new LibraryFileSystem(fs);
            var options = new CompilerOptions();
            if (request.TryGetProperty("options", out var configured))
                foreach (var property in configured.EnumerateObject()) options.Set(JsonStrings.GetName(property), property.Value);
            var roots = request.GetProperty("roots").EnumerateArray().Select(JsonStrings.GetString).ToArray();
            config = new(Text("configFileName"), options, roots, [], [], []);
            if (request.TryGetProperty("contentMappers", out var mapperCount) && mapperCount.GetInt32() != 0)
            {
                var fixture = Environment.GetEnvironmentVariable("CSHARP_CONTENT_MAPPER_FIXTURE")
                    ?? throw new InvalidOperationException("Missing content-mapper fixture executable");
                if (!Path.IsPathFullyQualified(fixture) || !File.Exists(fixture))
                    throw new InvalidOperationException("Invalid content-mapper fixture executable");
                var configuredMappers = new ConfigParser(fs, cwd).Parse(config.FileName, options);
                if (configuredMappers.ContentMappers.Length != mapperCount.GetInt32())
                    throw new InvalidDataException("Content-mapper configuration count differs from the reference input");
                config = configuredMappers with
                {
                    Options = options,
                    FileNames = roots,
                    ContentMappers = configuredMappers.ContentMappers.Select(mapper => mapper with
                    {
                        PackageDirectory = Utf8String.FromString(Path.GetDirectoryName(fixture)!),
                        Exec = [Utf8String.FromString(fixture), .. mapper.Exec]
                    }).ToArray()
                };
            }
        }
        else config = new ConfigParser(fs, cwd).Parse(configPath);
        var program = await CompilerProgram.CreateAsync(fs, cwd, config);
        if (Text("checkOnly") is { Length: > 0 } checkFile)
        {
            var source = program.GetFile(checkFile)!.Syntax;
            var checker = await program.CreateCheckerAsync();
            await checker.CheckSourceFileAsync(source);
            var checkOutput = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(checkOutput))
            {
                writer.WriteStartObject(); writer.WritePropertyName("diagnostics"u8);
                DeclarationEmissionTests.WriteDiagnostics(writer, [.. source.ParseDiagnostics, .. checker.DetailedDiagnosticsForProgramFile(source)]);
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(checkOutput.WrittenSpan);
        }
        List<(Utf8String Path, Utf8String Text, EmitWriteFileData Data)> writes = [];
        var result = await program.EmitAsync(new()
        {
            Only = request.TryGetProperty("only", out var only) ? (EmitOnly)only.GetInt32() : EmitOnly.All,
            Force = Flag("force"),
            SourceFiles = request.TryGetProperty("targets", out var targets) ? targets.EnumerateArray().Select(value => program.GetFile(JsonStrings.GetString(value))!.Syntax).ToArray() : null,
            WriteFile = (path, text, data, _) =>
            {
                if (path == Text("failWrite")) throw new IOException("test write failure");
                if (path == Text("skipWrite")) data.SkippedDeclarationWrite = true;
                writes.Add((path, text, data));
                return ValueTask.CompletedTask;
            }
        });
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteBoolean("skipped"u8, result.EmitSkipped);
            writer.WritePropertyName("diagnostics"u8); DeclarationEmissionTests.WriteDiagnostics(writer, result.Diagnostics);
            writer.WriteStartArray("emitted"u8); foreach (var path in result.EmittedFiles) writer.WriteStringValue(path.Span); writer.WriteEndArray();
            writer.WriteStartArray("writes"u8);
            foreach (var write in writes.OrderBy(write => write.Path, Utf8StringComparer.Ordinal))
            {
                writer.WriteStartObject(); writer.WriteString("path"u8, write.Path.Span); writer.WriteBase64String("textBase64"u8, write.Text.Span);
                writer.WriteString("source"u8, write.Data.SourceFile.FileName.Span); writer.WriteNumber("mapPosition"u8, write.Data.SourceMapUrlPosition);
                writer.WritePropertyName("diagnostics"u8); DeclarationEmissionTests.WriteDiagnostics(writer, write.Data.Diagnostics ?? []);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteStartArray("maps"u8);
            foreach (var map in result.SourceMaps)
            {
                writer.WriteStartObject(); writer.WriteString("file"u8, map.GeneratedFile.Span);
                writer.WriteStartArray("inputs"u8); foreach (var path in map.InputSourceFileNames) writer.WriteStringValue(path.Span); writer.WriteEndArray();
                writer.WritePropertyName("map"u8); writer.WriteRawValue(map.Map.ToJson().Span); writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (Flag("corpus"))
            {
                writer.WriteStartArray("sources"u8);
                foreach (var file in program.SourceFiles)
                {
                    writer.WriteStartArray(); writer.WriteStringValue(file.Syntax.FileName.Span);
                    writer.WriteStringValue(Convert.ToHexStringLower(SHA256.HashData(file.Syntax.Source.Text.Span))); writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }
}

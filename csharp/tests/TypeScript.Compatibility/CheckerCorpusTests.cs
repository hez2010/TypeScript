using TypeScript.Compiler.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;

namespace TypeScript.Compatibility;

internal static class CheckerCorpusTests
{
    internal static void Lines(Utf8String blobDirectory, bool reuseSyntax = false)
    {
        CompilerProgram? previous = null;
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                previous = Process(input.RootElement, blobDirectory, writer, reuseSyntax ? previous : null).GetAwaiter().GetResult();
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    private static async Task<CompilerProgram?> Process(
        JsonElement input,
        Utf8String blobDirectory,
        Utf8JsonWriter writer,
        CompilerProgram? previous)
    {
        CompilerProgram? program = null;
        List<(Utf8String File, IReadOnlyList<DiagnosticCode> Codes)> diagnostics = [];
        IReadOnlyList<DiagnosticCode> globals = [];
        bool includeDetails = input.TryGetProperty("includeDiagnosticDetails"u8, out var details) && details.GetBoolean();
        var semanticDetails = new List<Diagnostic>();
        IReadOnlyList<Diagnostic> globalDetails = [];
        Exception? failure = null;
        Utf8String stage = "read-input"u8;
        try
        {
            var files = new Dictionary<Utf8String, byte[]>();
            foreach (var file in input.GetProperty("files"u8).EnumerateObject())
            {
                Utf8String hash = TypeScript.Compiler.Configuration.JsonStrings.GetString(file.Value)!;
                if (hash.Length != 64 || hash.Span.ContainsAnyExcept("0123456789abcdefABCDEF"u8))
                    throw new InvalidDataException("Invalid corpus blob hash");
                var bytes = File.ReadAllBytes(Path.Combine(blobDirectory.ToString(), hash.ToString()));
                if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(hash.ToString(), StringComparison.Ordinal))
                    throw new InvalidDataException("Corpus blob hash mismatch");
                files.Add(JsonStrings.GetName(file), bytes);
            }
            Utf8String cwd = TypeScript.Compiler.Configuration.JsonStrings.GetString(input.GetProperty("currentDirectory"u8))!;
            var symlinks = input.GetProperty("symlinks"u8).EnumerateObject().ToDictionary(p => JsonStrings.GetName(p), p => JsonStrings.GetString(p.Value)!);
            var fs = new LibraryFileSystem(new MemoryFileSystem(files, input.GetProperty("caseSensitive"u8).GetBoolean(), cwd, symlinks));
            var options = new CompilerOptions();
            foreach (var option in input.GetProperty("options"u8).EnumerateObject())
                options.Set(JsonStrings.GetName(option), option.Value);
            var roots = input.GetProperty("roots"u8) is { ValueKind: JsonValueKind.Array } rootNames
                ? rootNames.EnumerateArray().Select(p => JsonStrings.GetString(p)!).ToArray() : [];
            var config = new ParsedConfig(JsonStrings.GetString(input.GetProperty("configFileName"u8))!, options, roots, [], [], []);
            stage = "create-program"u8;
            if (input.GetProperty("contentMappers"u8).GetInt32() != 0)
            {
                Utf8String fixture = Utf8String.FromString(Environment.GetEnvironmentVariable("CSHARP_CONTENT_MAPPER_FIXTURE")
                    ?? throw new InvalidOperationException("Missing content-mapper fixture executable"));
                if (!Path.IsPathFullyQualified(fixture.ToString()) || !File.Exists(fixture.ToString()))
                    throw new InvalidOperationException("Invalid content-mapper fixture executable");
                var configured = new ConfigParser(fs, cwd).Parse(config.FileName, options);
                if (configured.ContentMappers.Length != input.GetProperty("contentMappers"u8).GetInt32())
                    throw new InvalidDataException("Content-mapper configuration count differs from the reference input");
                config = configured with
                {
                    Options = options,
                    FileNames = roots,
                    ContentMappers = configured.ContentMappers.Select(mapper => mapper with
                    {
                        // Test package directories exist in the virtual filesystem only.
                        PackageDirectory = Utf8String.FromString(Path.GetDirectoryName(fixture.ToString())!),
                        Exec = [fixture, .. mapper.Exec]
                    }).ToArray()
                };
            }
            program = await CompilerProgram.CreateAsync(fs, cwd, config, previous,
                concurrency: input.GetProperty("singleThreaded"u8).GetBoolean() ? 1 : Environment.ProcessorCount,
                defaultLibraryDirectory: JsonStrings.GetString(input.GetProperty("libraryDirectory"u8)));
            stage = "create-checker"u8;
            if (program.SourceFiles.Count != 0)
            {
                var pool = await program.CreateCheckerPoolAsync(input.GetProperty("singleThreaded"u8).GetBoolean());
                stage = "check-program"u8;
                var checkedDiagnostics = await pool.GetDiagnosticsAsync();
                stage = "diagnostics"u8;
                foreach (var file in program.SourceFiles)
                {
                    diagnostics.Add((file.Syntax.FileName, checkedDiagnostics.Semantic.Where(d => d.FileName == file.Syntax.FileName)
                        .Select(d => d.Code).Order().ToArray()));
                    if (includeDetails)
                        semanticDetails.AddRange(checkedDiagnostics.Semantic.Where(d => d.FileName == file.Syntax.FileName));
                }
                globals = checkedDiagnostics.Global.Select(d => d.Code).Order().ToArray();
                if (includeDetails)
                    globalDetails = checkedDiagnostics.Global;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        writer.WriteStartObject();
        writer.WriteString("name"u8, JsonStrings.GetString(input.GetProperty("name"u8)));
        writer.WriteString("status"u8, failure is null ? Utf8String.Copy("checked"u8) : Utf8String.Copy("failed"u8));
        writer.WriteString("stage"u8, stage);
        if (failure is not null)
        {
            writer.WriteString("errorType"u8, failure.GetType().FullName);
            writer.WriteString("error"u8, failure.Message);
            writer.WriteString("stack"u8, failure.StackTrace);
        }
        writer.WriteStartArray("sources"u8);
        if (program is not null)
            foreach (var file in program.SourceFiles)
            {
                writer.WriteStartObject();
                writer.WriteString("file"u8, file.Syntax.FileName);
                writer.WriteString("sha256"u8, Convert.ToHexStringLower(SHA256.HashData(file.Syntax.Source.Bytes.Span)));
                writer.WriteBoolean("library"u8, file.Library);
                writer.WriteEndObject();
            }
        writer.WriteEndArray();
        writer.WriteStartArray("semanticCodes"u8);
        foreach (var (file, codes) in diagnostics.OrderBy(d => d.File, Utf8StringComparer.Ordinal))
            foreach (int code in codes)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(file);
                writer.WriteNumberValue(code);
                writer.WriteEndArray();
            }
        writer.WriteEndArray();
        writer.WriteStartArray("globalCodes"u8);
        foreach (int code in globals)
            writer.WriteNumberValue(code);
        writer.WriteEndArray();
        if (includeDetails)
        {
            writer.WritePropertyName("semanticDiagnostics"u8);
            WriteDiagnostics(writer, semanticDetails);
            writer.WritePropertyName("globalDiagnostics"u8);
            WriteDiagnostics(writer, globalDetails);
        }
        writer.WriteEndObject();
        return program;
    }

    internal static void WriteDiagnostics(Utf8JsonWriter writer, IEnumerable<Diagnostic> diagnostics)
    {
        writer.WriteStartArray();
        foreach (var diagnostic in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("file"u8, diagnostic.FileName ?? ""u8);
            writer.WriteNumber("start"u8, diagnostic.Start);
            writer.WriteNumber("length"u8, diagnostic.Length);
            writer.WriteNumber("code"u8, (int)diagnostic.Code);
            writer.WriteNumber("category"u8, (int)diagnostic.Message.Category);
            writer.WriteString("key"u8, diagnostic.Message.Key);
            writer.WriteStartArray("arguments"u8);
            foreach (Utf8String argument in diagnostic.Arguments)
                writer.WriteStringValue(argument.Span);
            writer.WriteEndArray();
            writer.WritePropertyName("chain"u8);
            WriteDiagnostics(writer, diagnostic.MessageChain);
            writer.WritePropertyName("related"u8);
            WriteDiagnostics(writer, diagnostic.RelatedInformation);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

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
    internal static void Lines(string blobDirectory, bool reuseSyntax = false)
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
        string blobDirectory,
        Utf8JsonWriter writer,
        CompilerProgram? previous)
    {
        CompilerProgram? program = null;
        List<(string File, IReadOnlyList<int> Codes)> diagnostics = [];
        IReadOnlyList<int> globals = [];
        bool includeDetails = input.TryGetProperty("includeDiagnosticDetails", out var details) && details.GetBoolean();
        var semanticDetails = new List<Diagnostic>();
        IReadOnlyList<Diagnostic> globalDetails = [];
        Exception? failure = null;
        string stage = "read-input";
        try
        {
            var files = new Dictionary<string, byte[]>();
            foreach (var file in input.GetProperty("files").EnumerateObject())
            {
                string hash = file.Value.GetString()!;
                if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)))
                    throw new InvalidDataException("Invalid corpus blob hash");
                var bytes = File.ReadAllBytes(Path.Combine(blobDirectory, hash));
                if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(hash, StringComparison.Ordinal))
                    throw new InvalidDataException("Corpus blob hash mismatch");
                files.Add(file.Name, bytes);
            }
            string cwd = input.GetProperty("currentDirectory").GetString()!;
            var symlinks = input.GetProperty("symlinks").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            var fs = new LibraryFileSystem(new MemoryFileSystem(files, input.GetProperty("caseSensitive").GetBoolean(), cwd, symlinks));
            var options = new CompilerOptions();
            foreach (var option in input.GetProperty("options").EnumerateObject())
                options.Set(option.Name, option.Value);
            var roots = input.GetProperty("roots") is { ValueKind: JsonValueKind.Array } rootNames
                ? rootNames.EnumerateArray().Select(p => p.GetString()!).ToArray() : [];
            var config = new ParsedConfig(input.GetProperty("configFileName").GetString()!, options, roots, [], [], []);
            stage = "create-program";
            if (input.GetProperty("contentMappers").GetInt32() != 0)
            {
                string fixture = Environment.GetEnvironmentVariable("CSHARP_CONTENT_MAPPER_FIXTURE")
                    ?? throw new InvalidOperationException("Missing content-mapper fixture executable");
                if (!Path.IsPathFullyQualified(fixture) || !File.Exists(fixture))
                    throw new InvalidOperationException("Invalid content-mapper fixture executable");
                var configured = new ConfigParser(fs, cwd).Parse(config.FileName, options);
                if (configured.ContentMappers.Length != input.GetProperty("contentMappers").GetInt32())
                    throw new InvalidDataException("Content-mapper configuration count differs from the reference input");
                config = configured with
                {
                    Options = options,
                    FileNames = roots,
                    ContentMappers = configured.ContentMappers.Select(mapper => mapper with
                    {
                        // Test package directories exist in the virtual filesystem only.
                        PackageDirectory = Path.GetDirectoryName(fixture)!,
                        Exec = [fixture, .. mapper.Exec]
                    }).ToArray()
                };
            }
            program = await CompilerProgram.CreateAsync(fs, cwd, config, previous,
                concurrency: input.GetProperty("singleThreaded").GetBoolean() ? 1 : Environment.ProcessorCount,
                defaultLibraryDirectory: input.GetProperty("libraryDirectory").GetString());
            stage = "create-checker";
            if (program.SourceFiles.Count != 0)
            {
                var checker = await program.CreateCheckerAsync();
                stage = "check-program";
                await checker.CheckProgramAsync();
                stage = "diagnostics";
                foreach (var file in program.SourceFiles)
                {
                    diagnostics.Add((file.Syntax.FileName, checker.DiagnosticCodesForProgramFile(file.Syntax)));
                    if (includeDetails)
                        semanticDetails.AddRange(checker.DetailedDiagnosticsForProgramFile(file.Syntax));
                }
                globals = checker.DiagnosticCodesForFile(null);
                if (includeDetails)
                    globalDetails = checker.DetailedDiagnosticsForFile(null);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        writer.WriteStartObject();
        writer.WriteString("name", input.GetProperty("name").GetString());
        writer.WriteString("status", failure is null ? "checked" : "failed");
        writer.WriteString("stage", stage);
        if (failure is not null)
        {
            writer.WriteString("errorType", failure.GetType().FullName);
            writer.WriteString("error", failure.Message);
            writer.WriteString("stack", failure.StackTrace);
        }
        writer.WriteStartArray("sources");
        if (program is not null)
            foreach (var file in program.SourceFiles)
            {
                writer.WriteStartObject();
                writer.WriteString("file", file.Syntax.FileName);
                writer.WriteString("sha256", Convert.ToHexStringLower(SHA256.HashData(file.Syntax.Source.Bytes.Span)));
                writer.WriteBoolean("library", file.Library);
                writer.WriteEndObject();
            }
        writer.WriteEndArray();
        writer.WriteStartArray("semanticCodes");
        foreach (var (file, codes) in diagnostics.OrderBy(d => d.File, StringComparer.Ordinal))
            foreach (int code in codes)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(file);
                writer.WriteNumberValue(code);
                writer.WriteEndArray();
            }
        writer.WriteEndArray();
        writer.WriteStartArray("globalCodes");
        foreach (int code in globals)
            writer.WriteNumberValue(code);
        writer.WriteEndArray();
        if (includeDetails)
        {
            writer.WritePropertyName("semanticDiagnostics");
            WriteDiagnostics(writer, semanticDetails);
            writer.WritePropertyName("globalDiagnostics");
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
            writer.WriteString("file", diagnostic.FileName ?? "");
            writer.WriteNumber("start", diagnostic.Start);
            writer.WriteNumber("length", diagnostic.Length);
            writer.WriteNumber("code", diagnostic.Code);
            writer.WriteNumber("category", (int)diagnostic.Message.Category);
            writer.WriteString("key", diagnostic.Message.Key);
            writer.WriteStartArray("arguments");
            foreach (string argument in diagnostic.Arguments)
                writer.WriteStringValue(argument);
            writer.WriteEndArray();
            writer.WritePropertyName("chain");
            WriteDiagnostics(writer, diagnostic.MessageChain);
            writer.WritePropertyName("related");
            WriteDiagnostics(writer, diagnostic.RelatedInformation);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

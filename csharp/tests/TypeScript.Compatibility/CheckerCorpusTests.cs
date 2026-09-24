using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
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
                throw new NotSupportedException("Corpus adapter requires the reference test content-mapper host");
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
                    diagnostics.Add((file.Syntax.FileName, checker.DiagnosticCodesForProgramFile(file.Syntax)));
                globals = checker.DiagnosticCodesForFile(null);
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
        writer.WriteEndObject();
        return program;
    }
}

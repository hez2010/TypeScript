using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;

namespace TypeScript.Compatibility;

internal static class CheckerWorkloadTests
{
    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            RunAsync(input.RootElement).GetAwaiter().GetResult();
        }
    }

    private static async Task RunAsync(JsonElement input)
    {
        var files = input.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name,
            p => File.ReadAllBytes(Path.Combine(input.GetProperty("blobDirectory").GetString()!, p.Value.GetString()!)));
        var links = input.GetProperty("symlinks").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var options = new CompilerOptions();
        foreach (var option in input.GetProperty("options").EnumerateObject())
            options.Set(option.Name, option.Value);
        var config = new ParsedConfig(input.GetProperty("configFileName").GetString()!, options,
            input.GetProperty("roots").EnumerateArray().Select(p => p.GetString()!).ToArray(), [], [], []);
        string cwd = input.GetProperty("currentDirectory").GetString()!;
        var fs = new LibraryFileSystem(new MemoryFileSystem(files, input.GetProperty("caseSensitive").GetBoolean(), cwd, links));
        bool single = input.GetProperty("singleThreaded").GetBoolean();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var result = await MeasureAsync(fs, cwd, config, input.GetProperty("libraryDirectory").GetString(), single);
        long releasedBytes = GC.GetTotalMemory(true);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("elapsedMs", result.Elapsed);
            writer.WriteNumber("cpuMs", result.Cpu);
            writer.WriteNumber("programMs", result.Program);
            writer.WriteNumber("allocatedBytes", result.Allocated);
            writer.WriteNumber("liveBytes", result.Live);
            writer.WriteNumber("releasedBytes", releasedBytes);
            writer.WriteNumber("peakRssBytes", Process.GetCurrentProcess().PeakWorkingSet64);
            writer.WriteNumber("sourceFiles", result.SourceFiles);
            writer.WriteNumber("checkerCount", result.Checkers);
            writer.WriteNumber("diagnosticCount", result.Diagnostics);
            writer.WriteString("graphSha256", result.Graph);
            writer.WriteEndObject();
        }
        Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        GC.KeepAlive(fs);
    }

    private static async Task<Measurement> MeasureAsync(
        LibraryFileSystem fs,
        string cwd,
        ParsedConfig config,
        string? libraries,
        bool single)
    {
        long allocated = GC.GetTotalAllocatedBytes(true);
        var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var timer = Stopwatch.StartNew();
        var program = await CompilerProgram.CreateAsync(fs, cwd, config, concurrency: single ? 1 : Environment.ProcessorCount,
            defaultLibraryDirectory: libraries);
        double programMs = timer.Elapsed.TotalMilliseconds;
        var pool = await program.CreateCheckerPoolAsync(single);
        var diagnostics = await pool.GetDiagnosticsAsync();
        timer.Stop();
        double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        var graph = new StringBuilder();
        foreach (var file in program.SourceFiles)
            graph.Append(file.Syntax.FileName).Append('\0')
                .Append(Convert.ToHexStringLower(SHA256.HashData(file.Syntax.Source.Bytes.Span))).Append('\n');
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(graph.ToString())));
        long live = GC.GetTotalMemory(true);
        var result = new Measurement(timer.Elapsed.TotalMilliseconds, cpuMs, programMs, bytes, live, program.SourceFiles.Count,
            pool.Count, diagnostics.Semantic.Count + diagnostics.Global.Count, hash);
        GC.KeepAlive(pool);
        GC.KeepAlive(program);
        return result;
    }

    private readonly record struct Measurement(double Elapsed, double Cpu, double Program, long Allocated, long Live,
        int SourceFiles, int Checkers, int Diagnostics, string Graph);
}

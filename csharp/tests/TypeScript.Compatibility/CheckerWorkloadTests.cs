using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
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
        var files = input.GetProperty("files"u8).EnumerateObject().ToDictionary(p => JsonStrings.GetName(p),
            p => File.ReadAllBytes(Path.Combine((JsonStrings.GetString(input.GetProperty("blobDirectory"u8))!).ToString(), (JsonStrings.GetString(p.Value)!).ToString())));
        var links = input.GetProperty("symlinks"u8).EnumerateObject().ToDictionary(p => JsonStrings.GetName(p), p => JsonStrings.GetString(p.Value)!);
        var options = new CompilerOptions();
        foreach (var option in input.GetProperty("options"u8).EnumerateObject())
            options.Set(JsonStrings.GetName(option), option.Value);
        var config = new ParsedConfig(JsonStrings.GetString(input.GetProperty("configFileName"u8))!, options,
            input.GetProperty("roots"u8).EnumerateArray().Select(p => JsonStrings.GetString(p)!).ToArray(), [], [], []);
        Utf8String cwd = TypeScript.Compiler.Configuration.JsonStrings.GetString(input.GetProperty("currentDirectory"u8))!;
        var fs = new LibraryFileSystem(new MemoryFileSystem(files, input.GetProperty("caseSensitive"u8).GetBoolean(), cwd, links));
        bool single = input.GetProperty("singleThreaded"u8).GetBoolean();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var result = await MeasureAsync(fs, cwd, config, JsonStrings.GetString(input.GetProperty("libraryDirectory"u8)), single);
        long releasedBytes = GC.GetTotalMemory(true);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("runtime"u8, RuntimeInformation.FrameworkDescription);
            writer.WriteBoolean("serverGC"u8, GCSettings.IsServerGC);
            writer.WriteNumber("elapsedMs"u8, result.Elapsed);
            writer.WriteNumber("startedTimestamp"u8, result.Started);
            writer.WriteNumber("stoppedTimestamp"u8, result.Stopped);
            writer.WriteNumber("cpuMs"u8, result.Cpu);
            writer.WriteNumber("programMs"u8, result.Program);
            writer.WriteNumber("poolMs"u8, result.Pool);
            writer.WriteNumber("programGcPauseMs"u8, result.ProgramPause);
            writer.WriteNumber("gcPauseMs"u8, result.Pause);
            writer.WriteNumber("gen0Collections"u8, result.Gen0);
            writer.WriteNumber("gen1Collections"u8, result.Gen1);
            writer.WriteNumber("gen2Collections"u8, result.Gen2);
            writer.WriteNumber("allocatedBytes"u8, result.Allocated);
            writer.WriteNumber("liveBytes"u8, result.Live);
            writer.WriteNumber("releasedBytes"u8, releasedBytes);
            writer.WriteNumber("peakRssBytes"u8, Process.GetCurrentProcess().PeakWorkingSet64);
            writer.WriteNumber("sourceFiles"u8, result.SourceFiles);
            writer.WriteNumber("checkerCount"u8, result.Checkers);
            writer.WriteNumber("diagnosticCount"u8, result.Diagnostics);
            writer.WriteString("graphSha256"u8, result.Graph);
            writer.WriteEndObject();
        }
        Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        GC.KeepAlive(fs);
    }

    private static async Task<Measurement> MeasureAsync(
        LibraryFileSystem fs,
        Utf8String cwd,
        ParsedConfig config,
        Utf8String? libraries,
        bool single)
    {
        long allocated = GC.GetTotalAllocatedBytes(true);
        var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        double pause = GC.GetTotalPauseDuration().TotalMilliseconds;
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        long started = Stopwatch.GetTimestamp();
        var timer = Stopwatch.StartNew();
        var program = await CompilerProgram.CreateAsync(fs, cwd, config, concurrency: single ? 1 : Environment.ProcessorCount,
            defaultLibraryDirectory: libraries);
        double programMs = timer.Elapsed.TotalMilliseconds;
        double programPause = GC.GetTotalPauseDuration().TotalMilliseconds - pause;
        var pool = await program.CreateCheckerPoolAsync(single);
        double poolMs = timer.Elapsed.TotalMilliseconds - programMs;
        var diagnostics = await pool.GetDiagnosticsAsync();
        timer.Stop();
        long stopped = Stopwatch.GetTimestamp();
        double pauseMs = GC.GetTotalPauseDuration().TotalMilliseconds - pause;
        gen0 = GC.CollectionCount(0) - gen0;
        gen1 = GC.CollectionCount(1) - gen1;
        gen2 = GC.CollectionCount(2) - gen2;
        double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        var graph = new Utf8StringBuilder();
        foreach (var file in program.SourceFiles)
            graph.Append(file.Syntax.FileName).Append((byte)'\0')
                .Append(Utf8String.FromString(Convert.ToHexStringLower(SHA256.HashData(file.Syntax.Source.Bytes.Span)))).Append((byte)'\n');
        Utf8String hash = Utf8String.FromString(Convert.ToHexStringLower(SHA256.HashData(graph.ToUtf8String().Span.ToArray())));
        long live = GC.GetTotalMemory(true);
        var result = new Measurement(timer.Elapsed.TotalMilliseconds, cpuMs, programMs, bytes, live, program.SourceFiles.Count,
            pool.Count, diagnostics.Semantic.Count + diagnostics.Global.Count, hash, poolMs, programPause, pauseMs, gen0, gen1, gen2, started, stopped);
        GC.KeepAlive(pool);
        GC.KeepAlive(program);
        return result;
    }

    private readonly record struct Measurement(double Elapsed, double Cpu, double Program, long Allocated, long Live,
        int SourceFiles, int Checkers, int Diagnostics, Utf8String Graph, double Pool, double ProgramPause, double Pause, int Gen0, int Gen1, int Gen2,
        long Started, long Stopped);
}

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Storage;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compatibility;

internal static class ProfileExperiments
{
    public static void Run(Utf8String inputPath, Utf8String directory)
    {
        Directory.CreateDirectory(directory.ToString());
        using JsonDocument input = JsonDocument.Parse(File.ReadAllBytes(inputPath.ToString()));
        var fixture = input.RootElement.GetProperty("cases"u8).EnumerateArray().Single(
            item => JsonStrings.GetString(item.GetProperty("name"u8)) == "large-graph"u8);
        var files = fixture.GetProperty("files").EnumerateArray().Select(
            file => new PipelineExperiments.InputFile(
                JsonStrings.GetString(file.GetProperty("name"))!,
                file.GetProperty("text").GetBytesFromBase64())).ToArray();
        using var profile = NativeProfile.Start();
        try
        {
            using var duplicate = NativeProfile.Start();
            throw new InvalidDataException("Concurrent profile accepted");
        }
        catch (InvalidOperationException) { }
        var timer = Stopwatch.StartNew();
        int iterations = 0;
        while (timer.Elapsed.TotalSeconds < 2)
        {
            using var operation = NativeProfile.Enter("TypeScript.Compile"u8);
            var project = PipelineExperiments.Compile(files, static () => new ArenaNodeStore(), static bytes => new Utf8Source(bytes));
            foreach (var file in project.Files.Values)
            {
                _ = Compiler.Protocol.SliceEncoder.Encode(file);
                _ = SlicePrinter.Print(file);
            }
            iterations++;
        }
        // Multiple compiler workers contribute real thread counters to one session.
        Parallel.For(
            0,
            8,
            _ => PipelineExperiments.Compile(files, static () => new ArenaNodeStore(), static bytes => new Utf8Source(bytes)));
        using (NativeProfile.Enter("scope-lifecycle-check"u8))
        {
            try
            {
                profile.Stop(directory);
                throw new InvalidDataException("Stopped an active scope");
            }
            catch (InvalidOperationException) { }
        }
        WeakReference owner = RetainThenRelease(profile, directory);
        profile.Stop(directory);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        if (owner.IsAlive)
            throw new InvalidDataException("Profile metadata retained a compiler owner");
        profile.SaveHeap(TypeScript.Compiler.Hosts.CompilerPath.Combine(directory, "heap-released.pb.gz"u8));
        try
        {
            profile.Stop(directory);
            throw new InvalidDataException("Double stop accepted");
        }
        catch (InvalidOperationException) { }
        using var stream = File.Create(Path.Combine(directory.ToString(), "run.json"));
        using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("iterations"u8, iterations);
        writer.WriteBoolean("releasedOwnerCollected"u8, !owner.IsAlive);
        writer.WriteString("cpu"u8, "instrumented phase thread CPU nanoseconds"u8);
        writer.WriteString("allocation"u8, "managed bytes allocated on instrumented threads"u8);
        writer.WriteString(
            "heap"u8,
            "whole-process live managed bytes, plus separately labeled retained-source bytes and syntax-node counts"u8);
        writer.WriteEndObject();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RetainThenRelease(NativeProfile profile, Utf8String directory)
    {
        byte[] owner = new byte[4 * 1024 * 1024];
        NativeProfile.TrackOwner(owner, "known-retained-source"u8, owner.Length, 123);
        profile.SaveHeap(TypeScript.Compiler.Hosts.CompilerPath.Combine(directory, "heap-retained.pb.gz"u8));
        GC.KeepAlive(owner);
        return new(owner);
    }
}

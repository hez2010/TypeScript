using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Storage;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compatibility;

internal static class ProfileExperiments
{
    public static void Run(string inputPath, string directory)
    {
        Directory.CreateDirectory(directory);
        using JsonDocument input = JsonDocument.Parse(File.ReadAllBytes(inputPath));
        var fixture = input.RootElement.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "large-graph");
        var files = fixture.GetProperty("files").EnumerateArray().Select(file => new PipelineExperiments.InputFile(file.GetProperty("name").GetString()!, file.GetProperty("text").GetBytesFromBase64())).ToArray();
        using var profile = NativeProfile.Start();
        try { using var duplicate = NativeProfile.Start(); throw new InvalidDataException("Concurrent profile accepted"); }
        catch (InvalidOperationException) { }
        var timer = Stopwatch.StartNew();
        int iterations = 0;
        while (timer.Elapsed.TotalSeconds < 2)
        {
            using var operation = NativeProfile.Enter("TypeScript.Compile");
            var project = PipelineExperiments.Compile(files, static () => new ArenaNodeStore(), static bytes => new Utf8Source(bytes));
            foreach (var file in project.Files.Values)
            {
                _ = Compiler.Protocol.SliceEncoder.Encode(file);
                _ = SlicePrinter.Print(file);
            }
            iterations++;
        }
        // Multiple compiler workers contribute real thread counters to one session.
        Parallel.For(0, 8, _ => PipelineExperiments.Compile(files, static () => new ArenaNodeStore(), static bytes => new Utf8Source(bytes)));
        using (NativeProfile.Enter("scope-lifecycle-check"))
        {
            try { profile.Stop(directory); throw new InvalidDataException("Stopped an active scope"); }
            catch (InvalidOperationException) { }
        }
        WeakReference owner = RetainThenRelease(profile, directory);
        profile.Stop(directory);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        if (owner.IsAlive) throw new InvalidDataException("Profile metadata retained a compiler owner");
        profile.SaveHeap(Path.Combine(directory, "heap-released.pb.gz"));
        try { profile.Stop(directory); throw new InvalidDataException("Double stop accepted"); }
        catch (InvalidOperationException) { }
        using var stream = File.Create(Path.Combine(directory, "run.json"));
        using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
        writer.WriteStartObject(); writer.WriteNumber("iterations", iterations); writer.WriteBoolean("releasedOwnerCollected", !owner.IsAlive); writer.WriteString("cpu", "instrumented phase thread CPU nanoseconds"); writer.WriteString("allocation", "managed bytes allocated on instrumented threads"); writer.WriteString("heap", "whole-process live managed bytes, plus separately labeled retained-source bytes and syntax-node counts"); writer.WriteEndObject();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RetainThenRelease(NativeProfile profile, string directory)
    {
        byte[] owner = new byte[4 * 1024 * 1024];
        NativeProfile.TrackOwner(owner, "known-retained-source", owner.Length, 123);
        profile.SaveHeap(Path.Combine(directory, "heap-retained.pb.gz"));
        GC.KeepAlive(owner);
        return new(owner);
    }
}

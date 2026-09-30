using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeScript.Compiler.Experiments;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Storage;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static partial class Experiments
{
    private sealed class ReferenceNode(NodeRecord value)
    {
        public readonly NodeRecord Value = value;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentProcessId();

    public static bool CheckNativeInterop() => !OperatingSystem.IsWindows() || GetCurrentProcessId() == Environment.ProcessId;

    public static bool CheckFileSystem()
    {
        Utf8String directory = Utf8String.FromString(Path.Combine(Path.GetTempPath(), "typescript-csharp-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory.ToString());
        try
        {
            Utf8String name = OperatingSystem.IsWindows() ? Utf8String.Copy([0xED, 0xA0, 0x80, 0x2D, 0xE6, 0x97, 0xA5, 0xE6, 0x9C, 0xAC, 0xE8, 0xAA, 0x9E, 0x2E, 0x74, 0x73]) : "日本語.ts"u8;
            byte[] content = Wtf8.Encode("const x = '\uD800';\n");
            Utf8String file = Utf8String.FromString(Path.Combine(directory.ToString(), name.ToString()));
            File.WriteAllBytes(file.ToString(), content);
            return Utf8String.FromString(Path.GetFileName(Directory.GetFiles(directory.ToString()).Single())) == name
                && File.ReadAllBytes(file.ToString()).AsSpan().SequenceEqual(content);
        }
        finally
        {
            Directory.Delete(directory.ToString(), true);
        }
    }

    public static bool CheckOwnershipAndStack()
    {
        var arena = new Arena<NodeRecord>(31);
        Handle<NodeRecord> first = arena.Add(new(SyntaxKind.Identifier, -1, 0, 0, 0, 0, 0));
        ref NodeRecord stable = ref arena[first];
        for (int i = 1; i < 250_000; i++)
            arena.Add(new(SyntaxKind.ParenthesizedType, i, i + 1, 0, (uint)(i - 1), 0, 0));
        stable = stable with { End = 42 };
        GC.Collect();
        if (arena[first].End != 42)
            return false;
        int visits = 0;
        for (int index = arena.Count - 1; index != 0; index = (int)arena[new(arena, index)].Parent)
            visits++;
        if (visits != arena.Count - 1)
            return false;
        try
        {
            _ = new Arena<NodeRecord>()[first];
            return false;
        }
        catch (ArgumentException) { }
        Utf8String deep = Utf8String.Concat(new Utf8String('(', 100_000), "string"u8, new Utf8String(')', 100_000));
        return TypeRelations.Parse(deep, []).AsSpan().SequenceEqual([new TypeAtom(AtomKind.String)]);
    }

    private static long sink;

    public static void Benchmark(JsonElement input, JsonElement expected)
    {
        var packets = expected.GetProperty("files"u8).EnumerateArray().Select(
            file => new AstPacket(file.GetProperty("wire"u8).GetBytesFromBase64())).ToArray();
        var texts = input.GetProperty("files"u8).EnumerateArray().Select(file => file.GetProperty("text"u8).GetBytesFromBase64()).ToArray();
        var types = new List<TypeAtom[]>();
        foreach (JsonElement type in input.GetProperty("types"u8).EnumerateArray())
            types.Add(TypeRelations.Parse(JsonStrings.GetString(type), types));
        var workloads = new (Utf8String Name, Func<long> Run)[]
        {
            (Utf8String.Copy("record-classes"u8), () =>
            {
                long sum = 0;
                foreach (AstPacket packet in packets)
                {
                    var nodes = new ReferenceNode[packet.NodeCount];
                    for (int i = 0; i < nodes.Length; i++) nodes[i] = new(packet.GetNode(i));
                    foreach (ReferenceNode node in nodes) sum += node.Value.Pos;
                    GC.KeepAlive(nodes);
                }
                return sum;
            }),
            (Utf8String.Copy("record-arena-256"u8), () =>
            {
                long sum = 0;
                foreach (AstPacket packet in packets)
                {
                    var arena = new Arena<NodeRecord>();
                    for (int i = 0; i < packet.NodeCount; i++) arena.Add(packet.GetNode(i));
                    for (int i = 0; i < arena.Count; i++) sum += arena[new(arena, i)].Pos;
                    GC.KeepAlive(arena);
                }
                return sum;
            }),
            (Utf8String.Copy("utf8-search"u8), () =>
            {
                long sum = 0;
                foreach (byte[] text in texts) sum += text.AsSpan().Count((byte)'\n');
                return sum;
            }),
            (Utf8String.Copy("relation-generic"u8), () =>
            {
                long sum = 0;
                foreach (TypeAtom[] source in types) foreach (TypeAtom[] target in types) if (TypeRelations.Assignable<StrictAssignment>(
                    source,
                    target)) sum++;
                return sum;
            }),
            (Utf8String.Copy("relation-concrete"u8), () =>
            {
                long sum = 0;
                foreach (TypeAtom[] source in types) foreach (TypeAtom[] target in types) if (TypeRelations.AssignableConcrete(
                    source,
                    target)) sum++;
                return sum;
            }),
        };
        const int samples = 15, iterations = 20;
        long[] checksums = workloads.Select(workload => workload.Run()).ToArray();
        if (checksums[0] != checksums[1] || checksums[3] != checksums[4])
            throw new InvalidDataException("Benchmark implementations produced different outputs");
        var times = workloads.Select(_ => new double[samples]).ToArray();
        var allocated = workloads.Select(_ => new long[samples]).ToArray();
        for (int sample = 0; sample < samples; sample++)
        {
            for (int order = 0; order < workloads.Length; order++)
            {
                int index = (order + sample) % workloads.Length;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                long before = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                for (int iteration = 0; iteration < iterations; iteration++)
                    sink = workloads[index].Run();
                times[index][sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
                allocated[index][sample] = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
            }
        }
        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new() { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("scope"u8, "AST packet hydration and primitive operations; not end-to-end compiler performance"u8);
        writer.WriteNumber("iterationsPerSample"u8, iterations);
        writer.WriteStartArray("workloads"u8);
        for (int i = 0; i < workloads.Length; i++)
        {
            writer.WriteStartObject();
            writer.WriteString("name"u8, workloads[i].Name);
            writer.WriteNumber("checksum"u8, checksums[i]);
            writer.WriteStartArray("milliseconds"u8);
            foreach (double value in times[i])
                writer.WriteNumberValue(value);
            writer.WriteEndArray();
            writer.WriteStartArray("allocatedBytes"u8);
            foreach (long value in allocated[i])
                writer.WriteNumberValue(value);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static void ProfileWorkload(int seconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seconds, 1);
        var timer = Stopwatch.StartNew();
        using var events = new ProbeEvents();
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            byte[] buffer = new byte[64 * 1024];
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (byte)(i ^ sink);
            sink += buffer.AsSpan().Count((byte)42);
            events.Batch(sink, GC.GetTotalAllocatedBytes());
        }
        Console.WriteLine(
            $"pid={Environment.ProcessId}; checksum={sink}; allocated={GC.GetTotalAllocatedBytes()}; retained={GC.GetTotalMemory(true)}");
    }
}

[EventSource(Name = "TypeScript-Rewrite-Probe")]
internal sealed class ProbeEvents : EventSource
{
    [Event(1)]
    public void Batch(long checksum, long allocated) => WriteEvent(1, checksum, allocated);
}

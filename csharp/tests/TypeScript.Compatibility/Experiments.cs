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
        string directory = Path.Combine(Path.GetTempPath(), "typescript-csharp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string name = OperatingSystem.IsWindows() ? "\uD800-日本語.ts" : "日本語.ts";
            byte[] content = Wtf8.Encode("const x = '\uD800';\n");
            string file = Path.Combine(directory, name);
            File.WriteAllBytes(file, content);
            return Path.GetFileName(Directory.GetFiles(directory).Single()) == name && File.ReadAllBytes(file).AsSpan().SequenceEqual(content);
        }
        finally { Directory.Delete(directory, true); }
    }

    public static bool CheckOwnershipAndStack()
    {
        var arena = new Arena<NodeRecord>(31);
        Handle<NodeRecord> first = arena.Add(new(SyntaxKind.Identifier, -1, 0, 0, 0, 0, 0));
        ref NodeRecord stable = ref arena[first];
        for (int i = 1; i < 250_000; i++) arena.Add(new(SyntaxKind.ParenthesizedType, i, i + 1, 0, (uint)(i - 1), 0, 0));
        stable = stable with { End = 42 };
        GC.Collect();
        if (arena[first].End != 42) return false;
        int visits = 0;
        for (int index = arena.Count - 1; index != 0; index = (int)arena[new(arena, index)].Parent) visits++;
        if (visits != arena.Count - 1) return false;
        try { _ = new Arena<NodeRecord>()[first]; return false; }
        catch (ArgumentException) { }
        string deep = new string('(', 100_000) + "string" + new string(')', 100_000);
        return TypeRelations.Parse(deep, []).AsSpan().SequenceEqual([new TypeAtom(AtomKind.String)]);
    }

    private static long sink;
    public static void Benchmark(JsonElement input, JsonElement expected)
    {
        var packets = expected.GetProperty("files").EnumerateArray().Select(file => new AstPacket(file.GetProperty("wire").GetBytesFromBase64())).ToArray();
        var texts = input.GetProperty("files").EnumerateArray().Select(file => file.GetProperty("text").GetBytesFromBase64()).ToArray();
        var utf16 = texts.Select(text => Wtf8.DecodeString(text)).ToArray();
        var types = new List<TypeAtom[]>();
        foreach (JsonElement type in input.GetProperty("types").EnumerateArray()) types.Add(TypeRelations.Parse(type.GetString(), types));
        var workloads = new (string Name, Func<long> Run)[]
        {
            ("record-classes", () =>
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
            ("record-arena-256", () =>
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
            ("utf8-search", () => { long sum = 0; foreach (byte[] text in texts) sum += text.AsSpan().Count((byte)'\n'); return sum; }),
            ("utf16-search", () => { long sum = 0; foreach (string text in utf16) sum += text.AsSpan().Count('\n'); return sum; }),
            ("utf8-to-utf16", () => { long sum = 0; foreach (byte[] text in texts) sum += Wtf8.DecodeString(text).Length; return sum; }),
            ("utf16-to-utf8", () => { long sum = 0; foreach (string text in utf16) sum += Wtf8.Encode(text).Length; return sum; }),
            ("relation-generic", () => { long sum = 0; foreach (TypeAtom[] source in types) foreach (TypeAtom[] target in types) if (TypeRelations.Assignable<StrictAssignment>(source, target)) sum++; return sum; }),
            ("relation-concrete", () => { long sum = 0; foreach (TypeAtom[] source in types) foreach (TypeAtom[] target in types) if (TypeRelations.AssignableConcrete(source, target)) sum++; return sum; }),
            ("position-maps", () => { long sum = 0; foreach (byte[] text in texts) sum += new PositionMap(text).Utf8ToUtf16(text.Length); return sum; }),
        };
        const int samples = 15, iterations = 20;
        long[] checksums = workloads.Select(workload => workload.Run()).ToArray();
        if (checksums[0] != checksums[1] || checksums[2] != checksums[3] || checksums[6] != checksums[7])
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
                for (int iteration = 0; iteration < iterations; iteration++) sink = workloads[index].Run();
                times[index][sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
                allocated[index][sample] = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
            }
        }
        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new() { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("scope", "AST packet hydration and primitive operations; not end-to-end compiler performance");
        writer.WriteNumber("iterationsPerSample", iterations);
        writer.WriteStartArray("workloads");
        for (int i = 0; i < workloads.Length; i++)
        {
            writer.WriteStartObject();
            writer.WriteString("name", workloads[i].Name);
            writer.WriteNumber("checksum", checksums[i]);
            writer.WriteStartArray("milliseconds"); foreach (double value in times[i]) writer.WriteNumberValue(value); writer.WriteEndArray();
            writer.WriteStartArray("allocatedBytes"); foreach (long value in allocated[i]) writer.WriteNumberValue(value); writer.WriteEndArray();
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
            for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)(i ^ sink);
            sink += buffer.AsSpan().Count((byte)42);
            events.Batch(sink, GC.GetTotalAllocatedBytes());
        }
        Console.WriteLine($"pid={Environment.ProcessId}; checksum={sink}; allocated={GC.GetTotalAllocatedBytes()}; retained={GC.GetTotalMemory(true)}");
    }
}

[EventSource(Name = "TypeScript-Rewrite-Probe")]
internal sealed class ProbeEvents : EventSource
{
    [Event(1)]
    public void Batch(long checksum, long allocated) => WriteEvent(1, checksum, allocated);
}

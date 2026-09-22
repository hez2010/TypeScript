using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;

namespace TypeScript.Compiler.Diagnostics;

// Instrumented CPU/allocation attribution, not statistical stack sampling.
// Counters are real OS thread CPU time and CLR allocation/heap byte counters.
// Static phase names avoid dependence on JIT/rundown/native symbol resolution.
public sealed partial class NativeProfile : IDisposable
{
    private static NativeProfile? active;
    [ThreadStatic] private static Frame? current;
    [ThreadStatic] private static long sampleSession, lastSampleTime;
    [ThreadStatic] private static Dictionary<string, StackState>? stackStates;
    private static long nextSession;
    private readonly long session = Interlocked.Increment(ref nextSession);
    private readonly ConcurrentDictionary<string, Sample> samples = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Sample> stackSamples = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<object, Owner> owners = new();
    private readonly object gate = new();
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset date = DateTimeOffset.UtcNow;
    private readonly long processCpuStart = ProcessCpu();
    private readonly long processAllocatedStart = GC.GetTotalAllocatedBytes(precise: true);
    private int openScopes;
    private bool stopped;

    private sealed record Owner(string Name, long SourceBytes, int Nodes);

    private sealed class Sample(string[] stack)
    {
        public readonly string[] Stack = stack;
        public long Cpu, Allocated;
    }

    internal sealed class StackState
    {
        public long Cpu, Allocated, ReportedCpu, ReportedAllocated;
    }

    internal sealed class Frame(NativeProfile profile, string name, Frame? parent)
    {
        public readonly NativeProfile Profile = profile;
        public readonly string Name = name;
        public readonly Frame? Parent = parent;
        public readonly string Key = name + "\0" + parent?.Key;
        public StackState Samples = null!;
        public readonly int Thread = Environment.CurrentManagedThreadId;
        public long CpuStart, AllocatedStart, ChildCpu, ChildAllocated;
    }

    public static NativeProfile Start()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The phase-1 CPU counter route is validated on Windows; other hosts need a thread CPU clock adapter.");
        var profile = new NativeProfile();
        if (Interlocked.CompareExchange(ref active, profile, null) is not null)
            throw new InvalidOperationException("CPU profiling already in progress");
        return profile;
    }

    public static Scope Enter(string phase)
    {
        NativeProfile? profile = Volatile.Read(ref active);
        if (profile is null)
            return default;
        lock (profile.gate)
        {
            if (!ReferenceEquals(Volatile.Read(ref active), profile))
                return default;
            Interlocked.Increment(ref profile.openScopes);
        }
        var frame = new Frame(profile, phase, current);
        if (sampleSession != profile.session)
        {
            sampleSession = profile.session;
            lastSampleTime = Stopwatch.GetTimestamp();
            (stackStates ??= new(StringComparer.Ordinal)).Clear();
        }
        if (!stackStates!.TryGetValue(frame.Key, out var state))
            stackStates.Add(frame.Key, state = new());
        frame.Samples = state;
        current = frame;
        frame.CpuStart = ThreadCpu();
        frame.AllocatedStart = GC.GetAllocatedBytesForCurrentThread();
        return new(frame);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly Frame? frame;

        internal Scope(Frame frame) => this.frame = frame;

        public void Dispose()
        {
            if (frame is null)
                return;
            if (frame.Thread != Environment.CurrentManagedThreadId || !ReferenceEquals(current, frame))
                throw new InvalidOperationException("Profile scopes must close in order on their owning thread");
            long cpu = ThreadCpu() - frame.CpuStart;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - frame.AllocatedStart;
            current = frame.Parent;
            if (frame.Parent is { } parent)
            {
                parent.ChildCpu += cpu;
                parent.ChildAllocated += allocated;
            }
            Sample sample = frame.Profile.samples.GetOrAdd(frame.Key, static (_, frame) =>
            {
                var stack = new List<string>();
                for (Frame? cursor = frame; cursor is not null; cursor = cursor.Parent)
                    stack.Add(cursor.Name);
                return new Sample([.. stack]);
            }, frame);
            Interlocked.Add(ref sample.Cpu, Math.Max(0, cpu - frame.ChildCpu));
            Interlocked.Add(ref sample.Allocated, Math.Max(0, allocated - frame.ChildAllocated));
            frame.Samples.Cpu += Math.Max(0, cpu - frame.ChildCpu);
            frame.Samples.Allocated += Math.Max(0, allocated - frame.ChildAllocated);
            Interlocked.Decrement(ref frame.Profile.openScopes);
        }
    }

    public static void TrackOwner(object owner, string name, long sourceBytes, int nodes)
    {
        Volatile.Read(ref active)?.owners.AddOrUpdate(owner, new(name, sourceBytes, nodes));
    }

    // Cooperative 100 Hz checkpoints inside owned compiler loops. Capture real
    // NativeAOT managed stack names through the BCL, without thread suspension.
    // Attribution is approximate and biased toward polling locations.
    public static void Poll()
    {
        var profile = Volatile.Read(ref active);
        if (profile is not null && ReferenceEquals(current?.Profile, profile))
            profile.SampleStack();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SampleStack()
    {
        long now = Stopwatch.GetTimestamp();
        if (now - lastSampleTime < Stopwatch.Frequency / 100)
            return;
        Frame frame = current!;
        StackState state = frame.Samples;
        long cpu = state.Cpu + Math.Max(0, ThreadCpu() - frame.CpuStart - frame.ChildCpu);
        long allocated = state.Allocated + Math.Max(
            0,
            GC.GetAllocatedBytesForCurrentThread() - frame.AllocatedStart - frame.ChildAllocated);
        string trace = new StackTrace(skipFrames: 1, fNeedFileInfo: false).ToString();
        Sample sample = stackSamples.GetOrAdd(
            trace,
            static trace => new Sample(trace.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        Interlocked.Add(ref sample.Cpu, Math.Max(0, cpu - state.ReportedCpu));
        Interlocked.Add(ref sample.Allocated, Math.Max(0, allocated - state.ReportedAllocated));
        state.ReportedCpu = cpu;
        state.ReportedAllocated = allocated;
        lastSampleTime = Stopwatch.GetTimestamp();
    }

    public void Stop(string directory)
    {
        lock (gate)
        {
            if (stopped)
                throw new InvalidOperationException("CPU profiling not in progress");
            if (Volatile.Read(ref openScopes) != 0)
                throw new InvalidOperationException("Cannot stop while compiler scopes are active");
            if (!ReferenceEquals(Interlocked.CompareExchange(ref active, null, this), this))
                throw new InvalidOperationException("Profile ownership changed");
            stopped = true;
        }
        Directory.CreateDirectory(directory);
        long duration = (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1_000_000);
        var records = samples.Values.OrderBy(sample => string.Join("\0", sample.Stack), StringComparer.Ordinal).ToArray();
        long otherCpu = Math.Max(0, ProcessCpu() - processCpuStart - records.Sum(sample => sample.Cpu));
        long otherAllocated = Math.Max(
            0,
            GC.GetTotalAllocatedBytes(precise: true) - processAllocatedStart - records.Sum(sample => sample.Allocated));
        Write(
            Path.Combine(directory, "cpu.pb.gz"),
            [("cpu", "nanoseconds")],
            records.Select(sample => (sample.Stack, new[] { sample.Cpu })).Append(
                (new[] { "Runtime and other process work (outside scopes)" }, new[] { otherCpu })),
            "Instrumented compiler-phase thread CPU time; exclusive counters, not statistical instruction samples. Remaining process CPU is grouped separately, including runtime/background work.",
            duration);
        Write(
            Path.Combine(directory, "alloc.pb.gz"),
            [("alloc_space", "bytes")],
            records.Select(
                sample => (sample.Stack, new[] { sample.Allocated })).Append(
                    (new[] { "Runtime and profiler allocations (outside scopes)" }, new[] { otherAllocated })),
            "Measured managed allocation bytes; compiler-phase attribution, not allocation stack sampling. Remaining process allocations are grouped separately.",
            duration);
        var stacks = stackSamples.Values.ToArray();
        Write(
            Path.Combine(directory, "cpu-stacks.pb.gz"),
            [("cpu", "nanoseconds")],
            stacks.Select(sample => (sample.Stack, new[] { sample.Cpu })),
            "Cooperative 100 Hz checkpoints with actual NativeAOT managed stacks. Exclusive phase-local CPU since its prior checkpoint is attributed to the observed stack; polling-location bias and unsampled phase tails apply. See cpu.pb.gz for complete accounting.",
            duration);
        Write(
            Path.Combine(directory, "alloc-stacks.pb.gz"),
            [("alloc_space", "bytes")],
            stacks.Select(sample => (sample.Stack, new[] { sample.Allocated })),
            "Cooperative checkpoints with actual NativeAOT managed stacks. Phase-local allocation deltas are attributed approximately to the observed stack, not to individual allocation sites. See alloc.pb.gz for complete accounting.",
            duration);
    }

    public void SaveHeap(string path)
    {
        long bytes = GC.GetTotalMemory(forceFullCollection: true);
        var retained = new List<(string[] Stack, long[] Values)> { (new[] { "Managed heap (whole process)" }, new[] { bytes, 0L, 0L }) };
        foreach (var entry in owners)
        {
            Owner owner = entry.Value;
            retained.Add((new[] { owner.Name, "Retained compiler owners" }, new[] { 0L, owner.SourceBytes, (long)owner.Nodes }));
            GC.KeepAlive(entry.Key);
        }
        Write(
            path,
            [("inuse_space", "bytes"), ("source_bytes", "bytes"), ("syntax_nodes", "count")],
            retained,
            "inuse_space is measured whole-process managed heap bytes after collection. Separate source_bytes and syntax_nodes attribute retained source owners; they are not estimates of each owner's managed object size.",
            0);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (stopped)
                return;
            if (Volatile.Read(ref openScopes) != 0)
                throw new InvalidOperationException("Active profile scopes remain");
            Interlocked.CompareExchange(ref active, null, this);
            stopped = true;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out long created, out long exited, out long kernel, out long user);

    private static long ThreadCpu()
    {
        if (!GetThreadTimes(-2, out _, out _, out long kernel, out long user))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        return checked((kernel + user) * 100);
    }

    private static long ProcessCpu()
    {
        using var process = Process.GetCurrentProcess();
        return checked(process.TotalProcessorTime.Ticks * 100);
    }

    // Small schema-specific protobuf writer using BCL buffers and gzip. The
    // format has no BCL serializer. All field IDs follow google/pprof profile.proto;
    // Go's independent profile reader validates each produced artifact.
    private void Write(
        string path,
        (string Name, string Unit)[] kinds,
        IEnumerable<(string[] Stack, long[] Values)> values,
        string comment,
        long duration)
    {
        var strings = new List<string> { "" };
        var stringIds = new Dictionary<string, int>(StringComparer.Ordinal) { [""] = 0 };
        var functions = new Dictionary<string, int>(StringComparer.Ordinal);
        var profile = new Proto();
        int String(string value)
        {
            if (stringIds.TryGetValue(value, out int id))
                return id;
            id = strings.Count;
            strings.Add(value);
            stringIds.Add(value, id);
            return id;
        }
        foreach (var kind in kinds)
        {
            var type = new Proto();
            type.Number(1, (ulong)String(kind.Name));
            type.Number(2, (ulong)String(kind.Unit));
            profile.Message(1, type);
        }
        foreach (var sample in values)
        {
            if (sample.Values.Length != kinds.Length)
                throw new InvalidDataException("Profile sample width mismatch");
            var record = new Proto();
            foreach (string frame in sample.Stack)
            {
                if (!functions.TryGetValue(frame, out int id))
                {
                    id = functions.Count + 1;
                    functions.Add(frame, id);
                }
                record.Number(1, (ulong)id);
            }
            foreach (long value in sample.Values)
            {
                if (value < 0)
                    throw new InvalidDataException("Negative counter");
                record.Number(2, (ulong)value);
            }
            profile.Message(2, record);
        }
        foreach (var (name, id) in functions)
        {
            var function = new Proto();
            function.Number(1, (ulong)id);
            function.Number(2, (ulong)String(name));
            profile.Message(5, function);
            var line = new Proto();
            line.Number(1, (ulong)id);
            var location = new Proto();
            location.Number(1, (ulong)id);
            location.Message(4, line);
            profile.Message(4, location);
        }
        profile.Number(9, (ulong)date.ToUnixTimeMilliseconds() * 1_000_000);
        profile.Number(10, (ulong)duration);
        profile.Number(13, (ulong)String(comment));
        profile.Number(14, (ulong)String(kinds[0].Name));
        foreach (string text in strings)
            profile.Bytes(6, Encoding.UTF8.GetBytes(text));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        using var gzip = new GZipStream(stream, CompressionLevel.Fastest);
        gzip.Write(profile.Data);
    }

    private sealed class Proto
    {
        private readonly MemoryStream buffer = new();
        private readonly BinaryWriter writer;

        public Proto() => writer = new(buffer, Encoding.UTF8, leaveOpen: true);

        public ReadOnlySpan<byte> Data => buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length));

        private void Varint(ulong value) => writer.Write7BitEncodedInt64(unchecked((long)value));

        public void Number(int field, ulong value)
        {
            Varint((ulong)(field << 3));
            Varint(value);
        }

        public void Bytes(int field, ReadOnlySpan<byte> bytes)
        {
            Varint((ulong)((field << 3) | 2));
            Varint((ulong)bytes.Length);
            writer.Write(bytes);
        }

        public void Message(int field, Proto message) => Bytes(field, message.Data);
    }
}

using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text.Json;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.Diagnostics;

/// <summary>Chrome trace events and checker-local type tables consumed by TypeScript trace tooling.</summary>
internal sealed partial class CompilationTrace : IAsyncDisposable
{
    private const int FlushThreshold = 256 * 1024;
    private readonly IFileSystem fileSystem;
    private readonly Utf8String directory, tracePath, configFile;
    private readonly object gate = new();
    private readonly ArrayBufferWriter<byte> buffer = new();
    private readonly Dictionary<Utf8String, int> threads = [];
    private readonly Dictionary<int, Utf8String> threadNames = [];
    private readonly List<TypeRecorder> recorders = [];
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly bool deterministic;
    private long timestamp;
    private Exception? writeError;
    private bool stopped;
    private bool stopping;

    internal CompilationTrace(IFileSystem fileSystem, Utf8String directory, Utf8String configFile, bool deterministic = false)
    {
        this.fileSystem = fileSystem; this.directory = directory; this.configFile = configFile; this.deterministic = deterministic;
        tracePath = CompilerPath.Combine(directory, "trace.json"u8);
        fileSystem.WriteFile(tracePath, "[\n"u8);
        WriteEvent("M"u8, "__metadata"u8, "process_name"u8, 0, 1, name: "tsgo-cs"u8, first: true);
        WriteEvent("M"u8, "__metadata"u8, "thread_name"u8, 0, 1, name: "Main"u8);
        WriteEvent("M"u8, "disabled-by-default-devtools.timeline"u8, "TracingStartedInBrowser"u8, 0, 1);
        Flush();
    }

    internal TypeRecorder Register(TypeContext context)
    {
        lock (gate)
        {
            var recorder = new TypeRecorder(recorders.Count, context);
            recorders.Add(recorder); return recorder;
        }
    }

    internal sealed class TypeRecorder(int index, TypeContext context)
    {
        internal int Index { get; } = index;
        internal TypeContext Context { get; } = context;
        internal Checker? Checker { get; set; }
        internal List<Type> Types { get; } = [];
        internal void Record(Type type) => Types.Add(type);
    }

    internal EventScope? Begin(Utf8String category, Utf8String name, Utf8String path, int? checker, uint sourceId, uint targetId, bool sampled)
    {
        lock (gate)
        {
            if (stopped || writeError is not null || sampled && deterministic) return null;
            double start = Timestamp();
            int thread = Thread(path, checker);
            if (!sampled) WriteEvent("B"u8, category, name, start, thread, path, checker, sourceId, targetId);
            return new(this, category, name, start, thread, path, checker, sourceId, targetId, sampled);
        }
    }

    internal sealed class EventScope(CompilationTrace owner, Utf8String category, Utf8String name, double start, int thread,
        Utf8String path, int? checker, uint sourceId, uint targetId, bool sampled) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.gate)
            {
                if (owner.stopped || owner.writeError is not null) return;
                double end = owner.Timestamp();
                // Sample short type operations only when they cross the reference's 10 ms boundary.
                if (sampled && 10000 - start % 10000 > end - start) return;
                owner.WriteEvent(sampled ? "X"u8 : "E"u8, category, name, sampled ? start : end, thread,
                    path, checker, sourceId, targetId, duration: sampled ? end - start : null);
            }
        }
    }

    private double Timestamp() => deterministic ? ++timestamp : Stopwatch.GetElapsedTime(started).TotalMicroseconds;

    private int Thread(Utf8String path, int? checker)
    {
        if (checker is null && path.IsEmpty) return 1;
        Utf8String key = checker is { } index ? "checker:"u8 + Utf8String.Format(index) : "file:"u8 + path;
        if (threads.TryGetValue(key, out int found)) return found;
        int id = checker is { } number ? number + 2 : 1_000_000 + (int)(XxHash3.HashToUInt64(key.Span) % 1_000_000_000);
        while (threadNames.TryGetValue(id, out var prior) && prior != key) id++;
        threads.Add(key, id); threadNames.Add(id, key);
        WriteEvent("M"u8, "__metadata"u8, "thread_name"u8, 0, id, name: key);
        return id;
    }

    private void WriteEvent(Utf8String phase, Utf8String category, Utf8String eventName, double time, int thread,
        Utf8String path = default, int? checker = null, uint sourceId = 0, uint targetId = 0, double? duration = null, Utf8String name = default, bool first = false)
    {
        if (writeError is not null) return;
        if (!first) buffer.Write(",\n"u8);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteNumber("pid"u8, 1); writer.WriteNumber("tid"u8, thread);
            String(writer, "ph"u8, phase); String(writer, "cat"u8, category); writer.WriteNumber("ts"u8, time); String(writer, "name"u8, eventName);
            if (duration is { } value) writer.WriteNumber("dur"u8, value);
            if (!path.IsEmpty || checker is not null || sourceId != 0 || targetId != 0 || !name.IsEmpty)
            {
                writer.WriteStartObject("args"u8);
                if (!path.IsEmpty) String(writer, "path"u8, path);
                if (checker is { } index) writer.WriteNumber("checkerId"u8, index);
                if (sourceId != 0) writer.WriteNumber("sourceId"u8, sourceId);
                if (targetId != 0) writer.WriteNumber("targetId"u8, targetId);
                if (!name.IsEmpty) String(writer, "name"u8, name);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        if (buffer.WrittenCount >= FlushThreshold) Flush();
    }

    private static void String(Utf8JsonWriter writer, ReadOnlySpan<byte> name, Utf8String value)
    { writer.WritePropertyName(name); JsonStrings.WriteString(writer, value.Span); }

    private void Flush()
    {
        try { if (writeError is null) fileSystem.AppendFile(tracePath, buffer.WrittenSpan); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { writeError = error; }
        finally { buffer.Clear(); }
    }

    public async ValueTask DisposeAsync()
    {
        TypeRecorder[] captured;
        lock (gate)
        {
            if (stopping) return;
            stopping = true;
            captured = recorders.ToArray();
        }
        try
        {
            // Type displays can query their checker and emit additional type-operation events.
            // Do not hold the event lock while formatting them.
            foreach (var recorder in captured) await WriteTypesAsync(recorder).ConfigureAwait(false);
            lock (gate)
            {
                stopped = true;
                buffer.Write("\n]\n"u8); Flush();
                if (writeError is not null) throw writeError;
                var legend = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(legend))
                {
                    writer.WriteStartArray();
                    foreach (var recorder in recorders.OrderBy(recorder => TypesPath(recorder.Index), Utf8StringComparer.Ordinal))
                    {
                        writer.WriteStartObject();
                        if (!configFile.IsEmpty) String(writer, "configFilePath"u8, configFile);
                        String(writer, "tracePath"u8, tracePath); String(writer, "typesPath"u8, TypesPath(recorder.Index));
                        writer.WriteNumber("checkerId"u8, recorder.Index); writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                fileSystem.WriteFile(CompilerPath.Combine(directory, "legend.json"u8), legend.WrittenSpan);
            }
        }
        finally
        {
            lock (gate)
            {
                stopped = true;
                foreach (var recorder in recorders) recorder.Types.Clear();
                recorders.Clear(); threads.Clear(); threadNames.Clear(); buffer.Clear();
            }
        }
    }

    private Utf8String TypesPath(int index) => CompilerPath.Combine(directory, "types_"u8 + Utf8String.Format(index) + ".json"u8);
}

using System.Collections.Concurrent;
using System.Diagnostics;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.Diagnostics;

/// <summary>Optional per-compilation counters and tracing, isolated across concurrent command sessions.</summary>
internal sealed class CompilationCapture : IAsyncDisposable
{
    private static readonly AsyncLocal<CompilationCapture?> ambient = new();
    internal static CompilationCapture? Current => ambient.Value;
    private readonly CompilationCapture? previous;
    private readonly long[] checkKindAllocations = new long[512];
    /// <summary>
    /// Records the allocation a checked node is responsible for on its own, i.e. the subtree total
    /// minus what its children already reported. Fills the checker half of the allocation map that
    /// the printer-side equivalent already covered.
    /// </summary>
    private readonly long[] transformKindAllocations = new long[512];
    /// <summary>Transform-phase counterpart of <see cref="NoteCheckKind"/>.</summary>
    internal void NoteTransformKind(int kind, long bytes)
    {
        if (bytes > 0 && (uint)kind < (uint)transformKindAllocations.Length)
            Interlocked.Add(ref transformKindAllocations[kind], bytes);
    }

    internal void NoteCheckKind(int kind, long bytes)
    {
        if (bytes > 0 && (uint)kind < (uint)checkKindAllocations.Length)
            Interlocked.Add(ref checkKindAllocations[kind], bytes);
    }

    private readonly long[] probeAllocations = new long[AllocationProbes.Count];
    private readonly long[] probeDurations = new long[AllocationProbes.Count];
    private long commentAdds, commentSets, nodeDataCalls, linkCreates;

    private void NoteProbe(int id, long bytes, long ticks)
    {
        if ((uint)id >= (uint)probeAllocations.Length)
            return;
        if (bytes > 0) Interlocked.Add(ref probeAllocations[id], bytes);
        if (ticks > 0) Interlocked.Add(ref probeDurations[id], ticks);
    }

    internal void NoteCommentAdd() => Interlocked.Increment(ref commentAdds);
    internal void NoteCommentSet() => Interlocked.Increment(ref commentSets);
    internal void NoteNodeData() => Interlocked.Increment(ref nodeDataCalls);
    internal void NoteLinkCreate() => Interlocked.Increment(ref linkCreates);

    private static int activeCaptures;

    /// <summary>
    /// True while any capture is active. Checked by every probe call site, so it must stay a plain
    /// static read: allocation probes are only collected for diagnostic runs, and a normal compile
    /// must not pay an <c>AsyncLocal</c> read on its hot paths.
    /// </summary>
    internal static bool ProbesEnabled => Volatile.Read(ref activeCaptures) != 0;

    /// <summary>
    /// Start of an inclusive probe frame. Uses the per-thread allocation counter and the timestamp
    /// clock: probes are collected only for diagnostic runs, but they still fire on hot paths, and
    /// these two reads are what keep that affordable. Per-thread allocation attribution is also more
    /// exact than the process-wide counter when several checkers run in parallel.
    /// </summary>
    internal static ProbeMark Mark() => ProbesEnabled
        ? new ProbeMark(GC.GetAllocatedBytesForCurrentThread(), Stopwatch.GetTimestamp())
        : default;

    /// <summary>Reports what happened since <paramref name="mark"/>; no-op without a capture.</summary>
    internal static void Report(int id, ProbeMark mark)
    {
        if (mark.Allocated == 0 || Current is not { } capture)
            return;
        capture.NoteProbe(id, GC.GetAllocatedBytesForCurrentThread() - mark.Allocated, Stopwatch.GetTimestamp() - mark.Started);
    }

    /// <summary>Inclusive allocation and time captured at the start of a probe frame.</summary>
    internal readonly struct ProbeMark(long allocated, long started)
    {
        internal readonly long Allocated = allocated;
        internal readonly long Started = started;
    }
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly long allocated = GC.GetTotalAllocatedBytes();
    private readonly ConcurrentDictionary<Utf8String, long> durations = new();
    private readonly ConcurrentDictionary<Utf8String, long> allocations = new();
    private readonly List<WeakReference<TypeContext>> contexts = [];
    private readonly TextWriter warnings;
    private readonly CompilationTrace? trace;
    private long types, instantiations;
    private volatile bool disposed;
    private bool closing;

    private CompilationCapture(IFileSystem fileSystem, Utf8String currentDirectory, ParsedConfig config, TextWriter warnings, bool allowTrace, bool deterministic)
    {
        this.warnings = warnings;
        previous = ambient.Value;
        if (allowTrace && config.Options.GenerateTrace is { IsEmpty: false } directory)
        {
            try { trace = new(fileSystem, CompilerPath.Resolve(currentDirectory, directory), config.FileName, deterministic); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { warnings.WriteLine($"Warning: Failed to start tracing: {error.Message}"); }
        }
        ambient.Value = this;
        Interlocked.Increment(ref activeCaptures);
    }

    internal static CompilationCapture? Start(IFileSystem fileSystem, Utf8String currentDirectory, ParsedConfig config,
        TextWriter? warnings = null, bool allowTrace = true, bool deterministic = false) =>
        config.Options.Diagnostics == true || config.Options.ExtendedDiagnostics == true || allowTrace && config.Options.GenerateTrace is { IsEmpty: false }
            ? new(fileSystem, currentDirectory, config, warnings ?? TextWriter.Null, allowTrace, deterministic) : null;

    internal CompilationTrace.TypeRecorder? Register(TypeContext context)
    {
        lock (contexts) contexts.Add(new(context));
        return trace?.Register(context);
    }

    internal void TypeCreated(Type type, CompilationTrace.TypeRecorder? recorder)
    {
        Interlocked.Increment(ref types);
        recorder?.Record(type);
    }

    internal void Instantiated() => Interlocked.Increment(ref instantiations);

    internal Scope Begin(Utf8String category, Utf8String name, Utf8String path = default, int? checker = null,
        uint sourceId = 0, uint targetId = 0, bool sampled = false)    {
        if (disposed) return default;
        long start = Stopwatch.GetTimestamp();
        long allocated = GC.GetTotalAllocatedBytes();
        var token = trace?.Begin(category, name, path, checker, sourceId, targetId, sampled);
        return new(this, category, start, allocated, token);
    }

    /// <summary>Starts a scope for the active capture, or returns a no-op scope when none is active.</summary>
    internal static Scope Measure(Utf8String category)
    {
        var capture = Current;
        if (capture is null) return default;
        // The transform attribution frames are only valid inside the transform scope: rewriters also
        // run while printing, and counting those frames would double-book allocation across phases.
        if (category == "transform"u8) transformDepth.Value++;
        return capture.Begin(category, category);
    }

    private static readonly AsyncLocal<int> transformDepth = new();
    internal static bool InTransformPhase => transformDepth.Value > 0;

    internal readonly struct Scope(CompilationCapture owner, Utf8String category, long started, long allocated, CompilationTrace.EventScope? trace) : IDisposable
    {
        public void Dispose()
        {
            if (owner is null || owner.disposed) return;
            if (category == "transform"u8) transformDepth.Value--;
            long elapsed = Stopwatch.GetTimestamp() - started;
            owner.durations.AddOrUpdate(category, elapsed, (_, value) => value + elapsed);
            long growth = GC.GetTotalAllocatedBytes() - allocated;
            if (growth > 0)
                owner.allocations.AddOrUpdate(category, growth, (_, value) => value + growth);
            trace?.Dispose();
        }
    }

    internal CompilationStatistics Statistics(CompilerProgram program, double configTime = 0)
    {
        int identifiers = 0;
        var pending = new Stack<SyntaxNode>();
        foreach (var file in program.SourceFiles)
        {
            pending.Push(file.Syntax);
            while (pending.TryPop(out var node))
            {
                if (node.Kind is SyntaxKind.Identifier or SyntaxKind.PrivateIdentifier) identifiers++;
                for (int i = 0; i < node.ChildCount; i++) pending.Push(node.GetChild(i));
            }
        }
        double Seconds(Utf8String category) => durations.GetValueOrDefault(category) / (double)Stopwatch.Frequency;
        long Allocated(Utf8String category) => allocations.GetValueOrDefault(category);
        string[] passes = [.. durations.Keys.Where(key => key.Span.StartsWith("xform:"u8)).Select(key => key.ToString())];
        return new()
        {
            Files = program.SourceFiles.Count, Lines = program.SourceFiles.Sum(file => file.Syntax.Source.LineStarts.Length),
            Identifiers = identifiers, Symbols = program.SourceFiles.Sum(file => file.Binding.SymbolCount),
            Types = Volatile.Read(ref types), Instantiations = Volatile.Read(ref instantiations),
            ManagedBytes = GC.GetTotalMemory(forceFullCollection: true), AllocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: true) - allocated),
            ConfigTime = configTime, ProgramTime = Seconds("program"u8), ParseTime = Seconds("parse"u8), BindTime = Seconds("bind"u8),
            CheckTime = Seconds("check"u8), EmitTime = Seconds("emit"u8),
            TransformTime = Seconds("transform"u8), PrintTime = Seconds("print"u8), BuildInfoTime = Seconds("buildInfo"u8),
            ChangesTime = Seconds("changes"u8), TotalTime = Stopwatch.GetElapsedTime(started).TotalSeconds + configTime,
            AllocatedProgram = Allocated("program"u8), AllocatedParse = Allocated("parse"u8), AllocatedBind = Allocated("bind"u8),
            AllocatedCheck = Allocated("check"u8), AllocatedEmit = Allocated("emit"u8),
            AllocatedTransform = Allocated("transform"u8), AllocatedPrint = Allocated("print"u8),
            CheckKindAllocations = (long[])checkKindAllocations.Clone(),
            TransformKindAllocations = (long[])transformKindAllocations.Clone(),
            PassDurations = passes.ToDictionary(
                name => name,
                name => durations.GetValueOrDefault(Utf8String.FromString(name)) / (double)Stopwatch.Frequency),
            ProbeAllocations = (long[])probeAllocations.Clone(),
            ProbeDurations = (long[])probeDurations.Clone(),
            CommentAdds = Volatile.Read(ref commentAdds), CommentSets = Volatile.Read(ref commentSets),
            NodeDataCalls = Volatile.Read(ref nodeDataCalls), LinkCreates = Volatile.Read(ref linkCreates),
        };
    }

    public ValueTask DisposeAsync()
    {
        if (closing) return ValueTask.CompletedTask;
        closing = true;
        // Restore the caller's context synchronously, before asynchronous type formatting.
        ambient.Value = previous;
        return FinishAsync();
    }

    private async ValueTask FinishAsync()
    {
        try { if (trace is not null) await trace.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { warnings.WriteLine($"Warning: Failed to stop tracing: {error.Message}"); }
        finally
        {
            disposed = true;
            Interlocked.Decrement(ref activeCaptures);
            lock (contexts)
            {
                foreach (var reference in contexts)
                    if (reference.TryGetTarget(out var context) && ReferenceEquals(context.Capture, this)) context.DetachCapture();
                contexts.Clear();
            }
            durations.Clear();
        }
    }
}

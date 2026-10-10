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
    private readonly long[] probeOwn = new long[AllocationProbes.Count];
    private readonly long[] probeChildren = new long[AllocationProbes.Count];
    private readonly long[] probeCounts = new long[AllocationProbes.Count];
    private long commentAdds, commentSets, nodeDataCalls, linkCreates;

    // Maximum nesting depth of the relation entry point reached on any thread. The checked-in-async
    // shape keeps this depth off the stack (state machines live on the heap), so the checker sync
    // rewrite needs it to size the dedicated large-stack thread. Collected only while probes are on.
    [ThreadStatic]
    private static int relationDepth;
    private static int maxRelationDepth;
    [ThreadStatic]
    private static long lowestStack, highestStack;
    private static long maxRelationStackBytes;
    internal static int MaxRelationDepth => Volatile.Read(ref maxRelationDepth);
    internal static long MaxRelationStackBytes => Volatile.Read(ref maxRelationStackBytes);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static unsafe long StackAddress()
    {
        byte marker = 0;
        return (long)&marker;
    }

    internal static void NoteRelationEnter()
    {
        int depth = ++relationDepth;
        if (depth > Volatile.Read(ref maxRelationDepth))
            Volatile.Write(ref maxRelationDepth, depth);
        // Stack span traversed while the relation entry point is on the stack. The sync rewrite
        // needs a dedicated large-stack thread, so the range the recursion actually spans is
        // measured instead of assumed. Indicative only: an await that resumes on another thread
        // re-bases on that thread's own samples.
        long address = StackAddress();
        if (lowestStack == 0 || address < lowestStack)
            lowestStack = address;
        if (address > highestStack)
            highestStack = address;
        long span = highestStack - lowestStack;
        if (span > Volatile.Read(ref maxRelationStackBytes))
            Volatile.Write(ref maxRelationStackBytes, span);
    }

    internal static void NoteRelationExit() => relationDepth--;

    private static long stackGuardCalls, stackGuardYields;
    internal static long StackGuardCalls => Volatile.Read(ref stackGuardCalls);
    internal static long StackGuardYields => Volatile.Read(ref stackGuardYields);

    /// <summary>
    /// The runtime execution-stack guard used at the top of the recursive checking entry points.
    /// Beyond its <see cref="ConfigureAwaitOptions"/> result it counts how often the guard forces a
    /// real yield, which is what promotes the async state machines on this path to the heap: the
    /// checker sync rewrite removes the need for the guard entirely, so the rate is the input R
    /// needs to size the dedicated checker thread.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static ConfigureAwaitOptions StackGuard()
    {
        if (System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            if (ProbesEnabled)
                Interlocked.Increment(ref stackGuardCalls);
            return ConfigureAwaitOptions.None;
        }
        if (ProbesEnabled)
            Interlocked.Increment(ref stackGuardYields);
        return ConfigureAwaitOptions.ForceYielding;
    }

    private void NoteProbe(int id, long bytes, long ticks, FrameNode? node)
    {
        if ((uint)id >= (uint)probeAllocations.Length)
            return;
        if (bytes > 0) Interlocked.Add(ref probeAllocations[id], bytes);
        if (ticks > 0) Interlocked.Add(ref probeDurations[id], ticks);
        if (node is not null)
        {
            // Exclusive attribution: the frame's own bytes minus what its direct children reported.
            long own = bytes - node.ChildBytes;
            if (own > 0) Interlocked.Add(ref probeOwn[id], own);
            if (node.ChildBytes > 0) Interlocked.Add(ref probeChildren[id], node.ChildBytes);
            Interlocked.Increment(ref probeCounts[id]);
        }
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
    internal static ProbeMark Mark()
    {
        if (!ProbesEnabled) return default;
        // Pooled: a fresh node per frame would charge ~40 bytes to the parent's frame on every probe
        // call (about 2M per real-project run), which would swamp the numbers being measured.
        var pool = nodePool ??= [];
        var node = pool.Count > 0 ? pool.Pop() : new FrameNode();
        node.Parent = currentFrame;
        currentFrame = node;
        return new ProbeMark(GC.GetAllocatedBytesForCurrentThread(), Stopwatch.GetTimestamp(), node);
    }

    [ThreadStatic]
    private static Stack<FrameNode>? nodePool;

    /// <summary>Reports what happened since <paramref name="mark"/>; no-op without a capture.</summary>
    internal static void Report(int id, ProbeMark mark)
    {
        if (mark.Node is not { } node)
            return;
        // Always unwind the frame stack, even when this frame is not being recorded.
        currentFrame = node.Parent;
        if (Current is not { } capture)
        {
            node.Recycle();
            return;
        }
        var bytes = GC.GetAllocatedBytesForCurrentThread() - mark.Allocated;
        // Children finished before this frame (LIFO), so the node already carries their sum.
        node.Id = id;
        node.Bytes = bytes;
        if (node.Parent is { } parent)
            parent.ChildBytes += bytes;
        capture.NoteProbe(id, bytes, Stopwatch.GetTimestamp() - mark.Started, node);
        node.Recycle();
    }

    internal sealed class FrameNode
    {
        internal int Id = -1;
        internal FrameNode? Parent;
        internal long Bytes;
        internal long ChildBytes;
        internal void Recycle()
        {
            Id = -1;
            Parent = null;
            Bytes = 0;
            ChildBytes = 0;
            nodePool!.Push(this);
        }
    }

    // Thread-static rather than AsyncLocal: an AsyncLocal write per frame allocates, and that
    // allocation lands in the parent's frame. The trade-off matches the assumption the inclusive
    // numbers already make - frames complete synchronously on their starting thread - so a frame
    // that suspends across threads only loses its exclusive attribution, not its inclusive one.
    [ThreadStatic]
    private static FrameNode? currentFrame;

    /// <summary>Inclusive allocation and time captured at the start of a probe frame.</summary>
    internal readonly struct ProbeMark(long allocated, long started, FrameNode? node)
    {
        internal readonly long Allocated = allocated;
        internal readonly long Started = started;
        internal readonly FrameNode? Node = node;
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
            ProbeOwn = (long[])probeOwn.Clone(),
            ProbeChildren = (long[])probeChildren.Clone(),
            ProbeCounts = (long[])probeCounts.Clone(),
            ProbeDurations = (long[])probeDurations.Clone(),
            CommentAdds = Volatile.Read(ref commentAdds), CommentSets = Volatile.Read(ref commentSets),
            NodeDataCalls = Volatile.Read(ref nodeDataCalls), LinkCreates = Volatile.Read(ref linkCreates),
            MaxRelationDepth = MaxRelationDepth,
            MaxRelationStackBytes = MaxRelationStackBytes,
            StackGuardCalls = StackGuardCalls,
            StackGuardYields = StackGuardYields,
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

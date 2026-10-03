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
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly long allocated = GC.GetTotalAllocatedBytes();
    private readonly ConcurrentDictionary<Utf8String, long> durations = new();
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
        uint sourceId = 0, uint targetId = 0, bool sampled = false)
    {
        if (disposed) return default;
        long start = Stopwatch.GetTimestamp();
        var token = trace?.Begin(category, name, path, checker, sourceId, targetId, sampled);
        return new(this, category, start, token);
    }

    internal readonly struct Scope(CompilationCapture owner, Utf8String category, long started, CompilationTrace.EventScope? trace) : IDisposable
    {
        public void Dispose()
        {
            if (owner is null || owner.disposed) return;
            long elapsed = Stopwatch.GetTimestamp() - started;
            owner.durations.AddOrUpdate(category, elapsed, (_, value) => value + elapsed);
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
        return new()
        {
            Files = program.SourceFiles.Count, Lines = program.SourceFiles.Sum(file => file.Syntax.Source.LineStarts.Length),
            Identifiers = identifiers, Symbols = program.SourceFiles.Sum(file => file.Binding.SymbolCount),
            Types = Volatile.Read(ref types), Instantiations = Volatile.Read(ref instantiations),
            ManagedBytes = GC.GetTotalMemory(forceFullCollection: true), AllocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: true) - allocated),
            ConfigTime = configTime, ProgramTime = Seconds("program"u8), ParseTime = Seconds("parse"u8), BindTime = Seconds("bind"u8),
            CheckTime = Seconds("check"u8), EmitTime = Seconds("emit"u8), BuildInfoTime = Seconds("buildInfo"u8),
            ChangesTime = Seconds("changes"u8), TotalTime = Stopwatch.GetElapsedTime(started).TotalSeconds + configTime,
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

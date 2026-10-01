using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Execution;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Mapping;

namespace TypeScript.Compiler.Watching;

public sealed record BuildWatchCycleResult(bool Rebuilt, BuildResult? Build, Diagnostic? Starting = null, Diagnostic? Finished = null);

/// <summary>Retains a solution builder and reconciles its watches after each serialized build cycle.</summary>
public sealed class BuildWatchSession : IAsyncDisposable
{
    private readonly ProjectBuilder builder;
    private readonly Utf8String[] projectPaths;
    private readonly BuildOptions options;
    private readonly NativeWatchBackend? nativeBackend;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextWriter? warnings;
    private bool started, disposed;
    public WatchManager Watches { get; }
    internal ProjectBuilder Builder => builder;

    public BuildWatchSession(IFileSystem fileSystem, Utf8String currentDirectory, IReadOnlyList<Utf8String> projectPaths,
        CompilerOptions? compilerOptions = null, BuildOptions? buildOptions = null, Utf8String defaultLibraryDirectory = default,
        IWatchBackend? backend = null, ContentMapperHost? mapperHost = null, TextWriter? warnings = null,
        Func<DateTime>? now = null, bool hashWithText = false)
    {
        this.projectPaths = projectPaths.ToArray(); options = buildOptions ?? new(); this.warnings = warnings;
        builder = new(fileSystem, currentDirectory, compilerOptions, defaultLibraryDirectory, mapperHost, now, hashWithText) { WatchMode = true };
        if (backend is null) backend = nativeBackend = new();
        Watches = new(backend, fileSystem.DirectoryExists, warnings);
    }

    public async ValueTask<BuildWatchCycleResult> StartAsync(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("The watch session has already started.");
            var result = await BuildAsync(true, cancellation).ConfigureAwait(false);
            started = true; return result;
        }
        finally { gate.Release(); }
    }

    public async ValueTask<BuildWatchCycleResult> CycleAsync(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!started) throw new InvalidOperationException("The watch session has not started.");
            var (changes, overflow) = Watches.DrainEvents();
            if (!await builder.InvalidateAsync(changes, overflow, Watches, cancellation).ConfigureAwait(false)) return new(false, null);
            return await BuildAsync(false, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Watches.ForceOverflow(signal: true); throw; }
        finally { gate.Release(); }
    }

    private async ValueTask<BuildWatchCycleResult> BuildAsync(bool initial, CancellationToken cancellation)
    {
        var result = await builder.BuildAsync(projectPaths, options, cancellation).ConfigureAwait(false);
        try { Watches.ReconcileWatches(builder.GetDesiredWatches(Watches)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { warnings?.Write(error.Message + "\n"); Watches.ForceOverflow(); }
        int errors = result.Diagnostics.Count;
        return new(true, result,
            new(initial ? Messages.Starting_compilation_in_watch_mode : Messages.File_change_detected_Starting_incremental_compilation, 0, 0, []),
            errors == 1 ? new(Messages.Found_1_error_Watching_for_file_changes, 0, 0, [])
                : new(Messages.Found_0_errors_Watching_for_file_changes, 0, 0, [Utf8String.FromString(errors.ToString(System.Globalization.CultureInfo.InvariantCulture))]));
    }

    public async Task RunAsync(Func<BuildWatchCycleResult, CancellationToken, ValueTask> report, CancellationToken cancellation = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        if (!started) await report(await StartAsync(linked.Token).ConfigureAwait(false), linked.Token).ConfigureAwait(false);
        await Watches.RunAsync(async token => await report(await CycleAsync(token).ConfigureAwait(false), token).ConfigureAwait(false), linked.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true; Watches.Dispose();
            if (nativeBackend is not null) await nativeBackend.DisposeAsync().ConfigureAwait(false);
            await builder.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}

using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects.TypeAcquisition;

namespace TypeScript.Compiler.Projects;

/// <summary>Publishes editor snapshots only while their parent and captured notification batch remain current.</summary>
public sealed partial class ProjectSession : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly ProjectSnapshotHost host;
    private readonly IFileSystem fileSystem;
    private readonly List<FileChange> pending = [];
    private readonly CancellationTokenSource lifetime = new();
    private ProjectWorkspaceSnapshot? current;
    private CompilerOptions? inferredOptions;
    private UserPreferences? pendingPreferences;
    private long revision;
    private bool disposed;
    internal IFileSystem FileSystem => fileSystem;
    internal bool IsDisposed { get { lock (sync) return disposed; } }

    internal ProjectSnapshotHost RetainHost()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this); host.Retain(); return host;
        }
    }

    internal event Action<ProjectWorkspaceSnapshot?, ProjectWorkspaceSnapshot>? SnapshotChanged;

    internal ProjectWorkspaceSnapshot.Lease? AcquireCurrentSnapshot()
    {
        lock (sync) return disposed ? null : current?.Acquire();
    }

    internal void PublishGlobalDiagnostics(Action<ProjectWorkspaceSnapshot> publish)
    {
        lock (sync) if (!disposed && current is not null) publish(current);
    }

    public event Action? DiagnosticsRefreshRequested;
    public event Action? CodeLensRefreshRequested;
    public event Action? InlayHintsRefreshRequested;
    public event Action<Utf8String, Exception>? TypingsInstallationFailed;

    public ProjectSession(IFileSystem fileSystem, ProjectHostOptions? options = null, INpmExecutor? npm = null)
    {
        this.fileSystem = fileSystem;
        host = new(fileSystem, options);
        if (npm is not null && !host.Options.TypingsLocation.IsEmpty) typingsInstaller = new(fileSystem, host.Options.TypingsLocation, npm);
    }

    public void Notify(FileChange change)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending.Add(change); revision++;
        }
    }

    public void SetInferredOptions(CompilerOptions options)
    {
        var owned = new CompilerOptions(); owned.Merge(options);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            inferredOptions = owned; revision++;
        }
    }

    public void Configure(UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        bool refreshCodeLens, refreshInlayHints, refreshDiagnostics;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var previous = pendingPreferences ?? current?.UserPreferences ?? new();
            refreshDiagnostics = previous.EnableValidation != preferences.EnableValidation || previous.ReportStyleChecksAsWarnings != preferences.ReportStyleChecksAsWarnings
                || previous.CustomConfigFileName != preferences.CustomConfigFileName;
            refreshCodeLens = (pendingPreferences ?? current?.UserPreferences ?? new()).CodeLens != preferences.CodeLens;
            refreshInlayHints = (pendingPreferences ?? current?.UserPreferences ?? new()).InlayHints != preferences.InlayHints;
            pendingPreferences = preferences; revision++;
        }
        if (refreshDiagnostics) DiagnosticsRefreshRequested?.Invoke();
        if (refreshCodeLens) CodeLensRefreshRequested?.Invoke();
        if (refreshInlayHints) InlayHintsRefreshRequested?.Invoke();
    }

    public ValueTask<ProjectWorkspaceSnapshot.Lease> GetSnapshotAsync(
        IReadOnlyList<Utf8String>? files = null, CancellationToken cancellation = default) =>
        UpdateAsync(new() { EnsureFiles = files ?? [] }, false, cancellation);

    internal ValueTask<ProjectWorkspaceSnapshot.Lease> ApiUpdateAsync(SnapshotRequest request, CancellationToken cancellation = default) =>
        UpdateAsync(request, true, cancellation);

    public ValueTask<ProjectWorkspaceSnapshot.Lease> GetWorkspaceSnapshotAsync(CancellationToken cancellation = default) =>
        UpdateAsync(new() { EnsureAllPrograms = true, LoadProjectTrees = true }, false, cancellation);

    internal ValueTask<ProjectWorkspaceSnapshot.Lease> GetProjectTreeSnapshotAsync(IReadOnlyList<Utf8String> projects, CancellationToken cancellation) =>
        UpdateAsync(new() { LoadProjectTrees = true, RequestedProjectTrees = projects }, false, cancellation);

    internal ValueTask<ProjectWorkspaceSnapshot.Lease> GetConfiguredSnapshotAsync(Utf8String file, CancellationToken cancellation) =>
        UpdateAsync(new() { EnsureConfiguredFiles = [file] }, false, cancellation);

    internal ValueTask<ProjectWorkspaceSnapshot.Lease> SetContentMapperContributionsAsync(ContentMapper[] mappers,
        IReadOnlyList<Utf8String> documents, CancellationToken cancellation) =>
        UpdateAsync(new() { InferredContentMappers = mappers, EnsureConfiguredFiles = documents }, false, cancellation);

    internal ValueTask<ProjectWorkspaceSnapshot.Lease> GetWorkspaceSnapshotForFilesAsync(IReadOnlyList<Utf8String> files, CancellationToken cancellation) =>
        UpdateAsync(new() { EnsureFiles = files, LoadProjectTrees = true }, false, cancellation);

    private async ValueTask<ProjectWorkspaceSnapshot.Lease> UpdateAsync(SnapshotRequest update, bool api, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            long version;
            ProjectWorkspaceSnapshot.Lease? parent;
            SnapshotRequest request;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                parent = current?.Acquire(); version = revision;
                request = update with
                {
                    FileChanges = pending.ToArray(), InferredOptions = inferredOptions, TypingsChanges = pendingTypings.Values.ToArray(), UserPreferences = pendingPreferences,
                    Locale = update.Locale ?? (pendingPreferences is { Locale.IsEmpty: false } configured ? configured.Locale : null),
                    CustomConfigFileName = update.CustomConfigFileName ?? pendingPreferences?.CustomConfigFileName,
                };
            }
            await using (parent)
            {
                if (!api && parent is not null && request.FileChanges.Count == 0 && request.InferredOptions is null && request.TypingsChanges.Count == 0 && request.UserPreferences is null
                    && !request.LoadProjectTrees && request.EnsureConfiguredFiles.Count == 0 && request.InferredContentMappers is null
                    && request.EnsureFiles.All(file => parent.Snapshot.GetDefaultProject(file) is { IsDirty: false })
                    && (!request.EnsureAllPrograms || parent.Snapshot.Projects.All(project => !project.IsDirty)))
                {
                    lock (sync)
                        if (!disposed && revision == version && ReferenceEquals(current, parent.Snapshot)) return parent.Snapshot.Acquire();
                }
                ProjectWorkspaceSnapshot next;
                try { next = await host.CreateAsync(request, parent?.Snapshot, linked.Token).ConfigureAwait(false); }
                catch (ArgumentException) when (api)
                {
                    // An invalid API transaction must not discard editor changes captured alongside it.
                    if (request.FileChanges.Count != 0 || request.InferredOptions is not null || request.TypingsChanges.Count != 0 || request.UserPreferences is not null)
                        await (await GetSnapshotAsync(cancellation: linked.Token).ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                ProjectWorkspaceSnapshot? replaced = null;
                ProjectWorkspaceSnapshot.Lease? result = null;
                lock (sync)
                {
                    if (!disposed && revision == version && ReferenceEquals(current, parent?.Snapshot))
                    {
                        replaced = current; current = next; pending.Clear(); inferredOptions = null; pendingTypings.Clear(); pendingPreferences = null;
                        result = next.Acquire();
                        SnapshotChanged?.Invoke(replaced, next);
                    }
                }
                if (result is not null)
                {
                    if (replaced is not null) await replaced.DisposeAsync().ConfigureAwait(false);
                    ScheduleTypings(next, parent?.Snapshot);
                    return result;
                }
                await next.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        ProjectWorkspaceSnapshot? snapshot;
        lock (sync)
        {
            if (disposed) return;
            disposed = true; snapshot = current; current = null; pending.Clear();
        }
        lifetime.Cancel();
        try
        {
            if (typingsInstaller is not null) await typingsInstaller.DisposeAsync().ConfigureAwait(false);
            await WaitForBackgroundTasksAsync().ConfigureAwait(false);
            if (snapshot is not null) await snapshot.DisposeAsync().ConfigureAwait(false);
        }
        finally { await host.DisposeAsync().ConfigureAwait(false); }
    }
}

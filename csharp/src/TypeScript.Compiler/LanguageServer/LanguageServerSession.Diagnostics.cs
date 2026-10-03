using System.Threading.Channels;
using System.Text.Json;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;
using static TypeScript.Compiler.LanguageServer.LspJson;

namespace TypeScript.Compiler.LanguageServer;

internal sealed partial class LanguageServerSession
{
    private readonly Channel<ReadOnlyMemory<byte>> diagnosticPublications = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new() { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly HashSet<Task> diagnosticUpdates = [];
    private CancellationTokenSource? scheduledSnapshotUpdate, scheduledDiagnosticsRefresh;
    private Task diagnosticPublisher = Task.CompletedTask;
    private bool pushDiagnosticsEnabled;

    private async ValueTask<RpcResponse> DocumentDiagnosticsAsync(LanguageServiceDocument service, ProjectSnapshot project,
        ProjectWorkspaceSnapshot snapshot, CancellationToken cancellation)
    {
        var client = DiagnosticOptions(capabilities, DiagnosticLocale(snapshot));
        int level = Integer(initializationOptions, "trackFlakyDiagnostics"u8);
        if (level == 0)
            return DocumentDiagnostics(await service.GetDiagnosticsAsync(project, snapshot.UserPreferences, client, cancellation).ConfigureAwait(false), client.VisualStudio);
        var (before, after) = await service.GetDiagnosticsAroundEmitAsync(project, snapshot.UserPreferences, client, cancellation).ConfigureAwait(false);
        var difference = FlakyDiagnostics.Compare(before, after);
        if (!difference.Full.IsEmpty)
        {
            logger.Log(1, difference.Full);
            telemetry?.RequestFailure("textDocument.diagnostic.flakeLog"u8, difference.Sanitized);
            if (level == 2) throw new FlakyDiagnostics.Failure(difference.Full);
        }
        return DocumentDiagnostics(before, client.VisualStudio);
    }

    private void StartDiagnostics()
    {
        pushDiagnosticsEnabled = !Boolean(initializationOptions, "disablePushDiagnostics"u8);
        if (!pushDiagnosticsEnabled) return;
        projects!.SnapshotChanged += (previous, current) => ProjectDiagnostics.Changes(previous, current, (project, populated) =>
            QueueProjectDiagnostics(project, populated, current));
        diagnosticPublisher = PublishDiagnosticsAsync();
    }

    private void QueueProjectDiagnostics(ProjectSnapshot project, bool populated, ProjectWorkspaceSnapshot snapshot)
    {
        var client = DiagnosticOptions(capabilities, DiagnosticLocale(snapshot), push: true);
        var diagnostics = populated && snapshot.UserPreferences.EnableValidation != false ? ProjectDiagnostics.Get(project, encoding, client) : [];
        var data = RpcResponse.Json(writer =>
        {
            writer.WriteStartObject(); String(writer, "uri"u8, DocumentUris.FromFileName(project.Id));
            writer.WritePropertyName("diagnostics"u8); WriteDiagnostics(writer, diagnostics, client.VisualStudio); writer.WriteEndObject();
        }).Data;
        diagnosticPublications.Writer.TryWrite(data);
    }

    private void PublishGlobalDiagnostics()
    {
        if (!pushDiagnosticsEnabled || state != 2) return;
        projects?.PublishGlobalDiagnostics(snapshot =>
        {
            if (snapshot.UserPreferences.EnableValidation == false) return;
            foreach (var project in snapshot.Projects)
                if (project.Kind == ProjectKind.Configured && project.Resource?.Checkers.TakeNewGlobalDiagnostics() == true)
                    QueueProjectDiagnostics(project, true, snapshot);
        });
    }

    private async Task PublishDiagnosticsAsync()
    {
        try
        {
            await foreach (var data in diagnosticPublications.Reader.ReadAllAsync(apiLifetime.Token).ConfigureAwait(false))
                await connection!.NotifyAsync("textDocument/publishDiagnostics"u8, data, apiLifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (apiLifetime.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { while (diagnosticPublications.Reader.TryRead(out _)) { } }
    }

    private Utf8String DiagnosticLocale(ProjectWorkspaceSnapshot snapshot) => snapshot.Locale.IsEmpty ? String(initializeParams, "locale"u8) : snapshot.Locale;

    private static bool IsMappedFile(ProjectWorkspaceSnapshot snapshot, Utf8String fileName) =>
        snapshot.Configurations.Values.SelectMany(config => config.ContentMappers).Concat(snapshot.InferredContentMappers)
            .Any(mapper => mapper.Extensions.Any(extension => fileName.EndsWith(extension)));

    private ValueTask WatchedFilesChangedAsync(JsonElement parameters, CancellationToken cancellation) =>
        WatchedFilesChangedAsync(Array(Get(parameters, "changes"u8)).Where(change => Integer(change, "type"u8) is >= 1 and <= 3)
            .Select(change => new FileChange(Integer(change, "type"u8) switch { 1 => FileChangeKind.WatchCreate, 3 => FileChangeKind.WatchDelete, _ => FileChangeKind.WatchChange },
                DocumentUris.ToFileName(String(change, "uri"u8)))).ToArray(), cancellation);

    private async ValueTask WatchedFilesChangedAsync(IReadOnlyList<FileChange> changes, CancellationToken cancellation)
    {
        await using var current = projects!.AcquireCurrentSnapshot();
        var snapshot = current?.Snapshot;
        bool refresh = false, update = false;
        HashSet<Utf8String> mapperFiles = [];
        if (snapshot is not null)
            foreach (var project in snapshot.Projects)
                if (project.Resource?.Mapper is { } mapper)
                    mapperFiles.UnionWith((await mapper.WatchedFilesAsync(cancellation).ConfigureAwait(false)).Select(snapshot.Host.Path));
        foreach (var change in changes)
        {
            var name = change.FileName;
            projects.Notify(change);
            var path = snapshot?.Host.Path(name) ?? name;
            if (snapshot is not null)
                update |= snapshot.Configurations.ContainsKey(path) || snapshot.Configurations.Values
                    .Any(config => config.ExtendedConfigFiles.Any(file => snapshot.Host.Path(file) == path));
            var extension = CompilerPath.Extension(name);
            refresh |= extension == ".ts"u8 || extension == ".tsx"u8 || extension == ".js"u8 || extension == ".jsx"u8
                || extension == ".mts"u8 || extension == ".cts"u8 || extension == ".mjs"u8 || extension == ".cjs"u8 || extension == ".json"u8
                || snapshot is not null && (IsMappedFile(snapshot, name) || mapperFiles.Contains(path));
            if (extension.IsEmpty)
                refresh |= change.Kind != FileChangeKind.WatchDelete ? fileSystem.DirectoryExists(name) : snapshot?.FileSystem.IsCachedDirectory(path) == true
                    || snapshot?.FileSystem.Overlay.HasOverlayDirectory(path) == true || CompilerPath.BaseName(path) == "node_modules"u8 || path.Contains("/node_modules/"u8);
        }
        if (refresh) ScheduleDiagnosticsRefresh();
        if (update) ScheduleSnapshotUpdate();
    }

    private void CancelScheduledSnapshotUpdate()
    { lock (diagnosticUpdates) scheduledSnapshotUpdate?.Cancel(); }

    private void CancelScheduledDiagnosticsRefresh()
    { lock (diagnosticUpdates) scheduledDiagnosticsRefresh?.Cancel(); }

    private void ScheduleSnapshotUpdate() => ScheduleDiagnosticWork(true, TimeSpan.FromMilliseconds(500));
    private void ScheduleDiagnosticsRefresh() => ScheduleDiagnosticsRefresh(TimeSpan.FromMilliseconds(500));
    private void ScheduleDiagnosticsRefresh(TimeSpan delay) => ScheduleDiagnosticWork(false, delay);

    private void ScheduleDiagnosticWork(bool update, TimeSpan delay)
    {
        CancellationTokenSource pending;
        lock (diagnosticUpdates)
        {
            (update ? scheduledSnapshotUpdate : scheduledDiagnosticsRefresh)?.Cancel();
            pending = CancellationTokenSource.CreateLinkedTokenSource(apiLifetime.Token);
            if (update) scheduledSnapshotUpdate = pending; else scheduledDiagnosticsRefresh = pending;
            var task = RunAsync();
            diagnosticUpdates.Add(task);
            _ = task.ContinueWith(done => { lock (diagnosticUpdates) diagnosticUpdates.Remove(done); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        async Task RunAsync()
        {
            try
            {
                // Yield even for immediate mapper refreshes so notifications in one batch coalesce.
                await Task.Yield();
                if (delay > TimeSpan.Zero) await Task.Delay(delay, pending.Token).ConfigureAwait(false);
                pending.Token.ThrowIfCancellationRequested();
                if (update) { await using var lease = await projects!.GetSnapshotAsync(cancellation: pending.Token).ConfigureAwait(false); }
                else RefreshDiagnostics();
            }
            catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (apiLifetime.IsCancellationRequested) { }
            finally
            {
                lock (diagnosticUpdates)
                {
                    if (update && ReferenceEquals(scheduledSnapshotUpdate, pending)) scheduledSnapshotUpdate = null;
                    if (!update && ReferenceEquals(scheduledDiagnosticsRefresh, pending)) scheduledDiagnosticsRefresh = null;
                    pending.Dispose();
                }
            }
        }
    }
}

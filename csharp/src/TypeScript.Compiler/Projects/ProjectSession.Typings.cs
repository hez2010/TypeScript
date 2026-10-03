using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Projects.TypeAcquisition;

namespace TypeScript.Compiler.Projects;

public sealed partial class ProjectSession
{
    private sealed record TypingsJob(object Identity, TypingsInfo Info, Utf8String[] Files, TaskCompletionSource Completion);
    private readonly TypingsInstaller? typingsInstaller;
    private readonly Dictionary<Utf8String, TypingsJob> typingsJobs = [];
    private readonly HashSet<Task> backgroundTasks = [];
    private readonly Dictionary<Utf8String, TypingsStateChange> pendingTypings = [];
    private bool typeAcquisitionEnabled = true;

    public async ValueTask SetAutomaticTypeAcquisitionAsync(bool enabled)
    {
        ProjectWorkspaceSnapshot.Lease? snapshot = null;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            typeAcquisitionEnabled = enabled;
            revision++;
            if (!enabled) pendingTypings.Clear();
            else snapshot = current?.Acquire();
        }
        if (snapshot is not null) await using (snapshot) ScheduleTypings(snapshot.Snapshot, null);
    }

    private void ScheduleTypings(ProjectWorkspaceSnapshot snapshot, ProjectWorkspaceSnapshot? previous)
    {
        if (typingsInstaller is null) return;
        foreach (var project in snapshot.Projects)
        {
            if (project.Program is null || project.GetTypeAcquisition().Enable != true) continue;
            var info = project.ComputeTypingsInfo();
            var files = project.Program.SourceFiles.Select(file => file.Syntax.FileName).ToArray();
            bool newFiles = previous?.GetProject(project.Id)?.Program is not { } old || !old.HasSameFileNames(project.Program);
            if (!newFiles && info.Equals(project.Typings?.Info)) continue;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = new TypingsJob(project.Identity, info, files, completion);
            ProjectWorkspaceSnapshot.Lease lease;
            lock (sync)
            {
                if (disposed || !typeAcquisitionEnabled) return;
                if (typingsJobs.TryGetValue(project.Id, out var running) && running.Identity == project.Identity
                    && running.Info.Equals(info) && running.Files.SequenceEqual(files)) continue;
                typingsJobs[project.Id] = job;
                backgroundTasks.Add(completion.Task);
                lease = snapshot.Acquire();
            }
            _ = InstallProjectTypingsAsync(lease, project, job);
        }
    }

    private async Task InstallProjectTypingsAsync(ProjectWorkspaceSnapshot.Lease lease, ProjectSnapshot project, TypingsJob job)
    {
        try
        {
            var root = project.Kind == ProjectKind.Configured ? CompilerPath.DirectoryName(project.Configuration.FileName) : host.Options.CurrentDirectory;
            var progress = host.Options.Progress;
            var displayName = progress is null ? default : project.DisplayName(host.Options.CurrentDirectory);
            if (progress is not null) await progress(Diagnostics.Messages.Installing_types_for_0, displayName, false).ConfigureAwait(false);
            TypingsInstallResult result;
            try
            {
                result = await typingsInstaller!.InstallAsync(new(job.Info.Acquisition, project.Configuration.Options, job.Files,
                    root, job.Info.UnresolvedImports, fileSystem), lifetime.Token).ConfigureAwait(false);
            }
            finally { if (progress is not null) await progress(Diagnostics.Messages.Installing_types_for_0, displayName, true).ConfigureAwait(false); }
            bool refresh = false;
            lock (sync)
            {
                if (!disposed && typeAcquisitionEnabled && current?.GetProject(project.Id) is { } latest
                    && ReferenceEquals(latest.Identity, job.Identity) && job.Info.Equals(latest.ComputeTypingsInfo())
                    && !result.Files.SequenceEqual(latest.TypingsFiles))
                {
                    pendingTypings[project.Id] = new(project.Id, job.Info, result) { ProjectIdentity = job.Identity };
                    revision++; refresh = true;
                }
            }
            if (refresh) DiagnosticsRefreshRequested?.Invoke();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { TypingsInstallationFailed?.Invoke(project.Id, error); }
        finally
        {
            try { await lease.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                lock (sync)
                {
                    if (typingsJobs.GetValueOrDefault(project.Id) == job) typingsJobs.Remove(project.Id);
                    backgroundTasks.Remove(job.Completion.Task);
                }
                job.Completion.TrySetResult();
            }
        }
    }

    public async Task WaitForBackgroundTasksAsync(CancellationToken cancellation = default)
    {
        while (true)
        {
            Task[] pending;
            lock (sync) pending = backgroundTasks.ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).WaitAsync(cancellation).ConfigureAwait(false);
        }
    }
}

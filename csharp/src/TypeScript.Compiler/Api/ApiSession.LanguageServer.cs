using System.Text.Json;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compiler.Api;

public sealed partial class ApiSession
{
    private readonly ProjectSession? projectSession;
    private readonly SemaphoreSlim languageServerUpdate = new(1, 1);
    private ApiOpenState? languageServerOpens;
    private readonly HashSet<Utf8String> createdPrograms = [];

    private async ValueTask<RpcResponse> GetLanguageServerSnapshotAsync(JsonElement parameters, CancellationToken cancellation)
    {
        if (projectSession is null) throw new ApiException("getCurrentLanguageServerSnapshot requires an LSP-connected API session");
        ulong baseId = ApiJson.UInt64(parameters, "baseSnapshot"u8);
        await using var previous = baseId == 0 ? null : Pin(baseId);
        await languageServerUpdate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var changes = ApiJson.Get(parameters, "changes"u8);
            var (request, opens) = Reconcile(ReadSnapshotRequest(changes), languageServerOpens);
            foreach (var program in request.ReconfigurePrograms)
                if (!createdPrograms.Contains(program.Id)) throw new ApiException($"synthetic program is not owned by this API session: {program.Id}");
            request = request with { RemovePrograms = request.RemovePrograms.Where(createdPrograms.Contains).ToArray() };
            ProjectWorkspaceSnapshot.Lease lease;
            try { lease = await projectSession.ApiUpdateAsync(request, cancellation).ConfigureAwait(false); }
            catch (ArgumentException error) { throw new ApiException($"failed to update language server snapshot: {error.Message}", inner: error); }
            languageServerOpens = opens;
            createdPrograms.ExceptWith(request.RemovePrograms);
            createdPrograms.UnionWith(lease.Snapshot.CreatedPrograms.Select(project => project.Id));
            RpcResponse response;
            try { response = SnapshotResponse(lease.Snapshot, previous?.Data.Snapshot, changes); }
            catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
            await RegisterAsync(new(lease.Snapshot, lease, opens, null)).ConfigureAwait(false);
            return response;
        }
        finally { languageServerUpdate.Release(); }
    }

    private async ValueTask ReleaseLanguageServerReferencesAsync()
    {
        if (projectSession is null || languageServerOpens is not { } opens || opens.Projects.Count + opens.Files.Count + createdPrograms.Count == 0) return;
        try
        {
            await using var lease = await projectSession.ApiUpdateAsync(new()
            {
                CloseProjects = opens.Projects.ToArray(), CloseFiles = opens.Files.ToArray(), RemovePrograms = createdPrograms.ToArray(),
            }).ConfigureAwait(false);
            languageServerOpens = null; createdPrograms.Clear();
        }
        catch (ObjectDisposedException) { } // The editor may already have shut down its project session.
        catch (OperationCanceledException) when (projectSession.IsDisposed) { }
    }
}

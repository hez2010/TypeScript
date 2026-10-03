using System.Text.Json;
using TypeScript.Compiler.Api;
using TypeScript.Compiler.Protocol;
using static TypeScript.Compiler.LanguageServer.LspJson;

namespace TypeScript.Compiler.LanguageServer;

internal sealed partial class LanguageServerSession
{
    private readonly CancellationTokenSource apiLifetime = new();
    private readonly List<Task> apiConnections = [];
    private readonly HashSet<Task> refreshRequests = [];

    private async Task<ReadOnlyMemory<byte>> CallClientAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation)
    {
        var result = await connection!.CallAsync(method, parameters, cancellation).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result, new() { MaxDepth = int.MaxValue });
        LspProtocol.ValidateResponse(method, document.RootElement);
        return result;
    }

    private void RefreshDiagnostics() => RefreshFeature("diagnostics"u8, "workspace/diagnostic/refresh"u8);
    private void RefreshCodeLenses() => RefreshFeature("codeLens"u8, "workspace/codeLens/refresh"u8);
    private void RefreshInlayHints() => RefreshFeature("inlayHint"u8, "workspace/inlayHint/refresh"u8);
    private void RefreshFeature(Utf8String feature, Utf8String method)
    {
        if (!Boolean(Get(Get(capabilities, "workspace"u8), feature), "refreshSupport"u8)) return;
        var task = RefreshAsync();
        lock (refreshRequests) refreshRequests.Add(task);
        _ = task.ContinueWith(done => { lock (refreshRequests) refreshRequests.Remove(done); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        async Task RefreshAsync()
        {
            try { await CallClientAsync(method, ReadOnlyMemory<byte>.Empty, apiLifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (RpcException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private RpcResponse InitializeApiSession(JsonElement parameters)
    {
        var path = String(parameters, "pipe"u8);
        if (path.IsEmpty)
        {
            string name = "tsgo-api-" + Guid.NewGuid().ToString("N");
            path = Utf8String.FromString(OperatingSystem.IsWindows() ? "\\\\.\\pipe\\" + name : Path.Combine(Path.GetTempPath(), name));
        }
        var listener = new RpcPipeListener(path);
        var session = new ApiSession(projects!);
        apiConnections.Add(ServeApiAsync(listener, session));
        return RpcResponse.Json(writer =>
        {
            writer.WriteStartObject(); String(writer, "sessionId"u8, session.Id); String(writer, "pipe"u8, path); writer.WriteEndObject();
        });
    }

    private async Task ServeApiAsync(RpcPipeListener listener, ApiSession session)
    {
        await using (session)
        using (listener)
        {
            try
            {
                await using var stream = await listener.AcceptAsync(apiLifetime.Token).ConfigureAwait(false);
                await using var peer = new RpcConnection(stream, stream, session);
                await peer.RunAsync(apiLifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (apiLifetime.IsCancellationRequested) { }
            catch (IOException) { } // Closing a client pipe ends that API session.
        }
    }

    private async ValueTask StopApiSessionsAsync()
    {
        await apiLifetime.CancelAsync().ConfigureAwait(false);
        if (telemetry is not null) await telemetry.DisposeAsync().ConfigureAwait(false);
        await StopFileWatchingAsync().ConfigureAwait(false);
        if (progress is not null) await progress.DisposeAsync().ConfigureAwait(false);
        profiler.Dispose();
        logMessages.Writer.TryComplete();
        await logPublisher.ConfigureAwait(false);
        diagnosticPublications.Writer.TryComplete();
        contentMapperUpdates.Writer.TryComplete();
        Task[] updates; lock (diagnosticUpdates) updates = diagnosticUpdates.ToArray();
        await Task.WhenAll(updates).ConfigureAwait(false);
        await diagnosticPublisher.ConfigureAwait(false);
        await contentMapperRegistrar.ConfigureAwait(false);
        Task[] refreshes; lock (refreshRequests) refreshes = refreshRequests.ToArray();
        await Task.WhenAll(refreshes).ConfigureAwait(false);
        await Task.WhenAll(apiConnections).ConfigureAwait(false);
        apiConnections.Clear();
    }
}

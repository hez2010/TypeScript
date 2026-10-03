using System.Text.Json;
using System.Threading.Channels;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Watching;
using static TypeScript.Compiler.LanguageServer.LspJson;

namespace TypeScript.Compiler.LanguageServer;

internal sealed partial class LanguageServerSession
{
    private readonly Channel<ProjectWatchGroup[]> watchUpdates = Channel.CreateUnbounded<ProjectWatchGroup[]>(new() { SingleReader = true });
    private readonly Channel<IReadOnlyList<FileChange>> nativeChanges = Channel.CreateUnbounded<IReadOnlyList<FileChange>>(new() { SingleReader = true });
    private LspFileWatcher? nativeWatcher;
    private NativeWatchBackend? ownedWatchBackend;
    private Task watchRegistrar = Task.CompletedTask, nativeChangeReader = Task.CompletedTask;

    private bool SupportsFileWatching => Boolean(Get(Get(capabilities, "workspace"u8), "didChangeWatchedFiles"u8), "dynamicRegistration"u8)
        || options.WatchBackend is not null || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    private void StartFileWatching()
    {
        if (!SupportsFileWatching) return;
        var capability = Get(Get(capabilities, "workspace"u8), "didChangeWatchedFiles"u8);
        bool dynamicRegistration = Boolean(capability, "dynamicRegistration"u8);
        bool relative = Boolean(capability, "relativePatternSupport"u8);
        if (!dynamicRegistration)
        {
            var backend = options.WatchBackend ?? (ownedWatchBackend = new NativeWatchBackend());
            nativeWatcher = new(fileSystem, backend, changes => nativeChanges.Writer.TryWrite(changes), logger.MapperMessage);
            nativeChangeReader = ReadNativeChangesAsync();
        }
        var registry = new LspWatchRegistry(RegisterAsync, UnregisterAsync, error => logger.MapperMessage("file watching: "u8 + Utf8String.FromString(error.Message)));
        projects!.SnapshotChanged += (_, snapshot) => watchUpdates.Writer.TryWrite(ProjectWatchPlan.Create(snapshot, relative));
        watchRegistrar = UpdateAsync();

        async Task UpdateAsync()
        {
            try
            {
                await foreach (var plan in watchUpdates.Reader.ReadAllAsync(apiLifetime.Token).ConfigureAwait(false))
                    await registry.UpdateAsync(plan, apiLifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (apiLifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (apiLifetime.IsCancellationRequested) { }
        }
        async ValueTask RegisterAsync(Utf8String id, LspWatchPattern pattern, CancellationToken cancellation)
        {
            if (nativeWatcher is not null) { nativeWatcher.Watch(id, [pattern]); return; }
            var parameters = RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); writer.WriteStartArray("registrations"u8); writer.WriteStartObject();
                String(writer, "id"u8, id); writer.WriteString("method"u8, "workspace/didChangeWatchedFiles"u8);
                writer.WriteStartObject("registerOptions"u8); writer.WriteStartArray("watchers"u8); writer.WriteStartObject();
                if (pattern.BaseUri.IsEmpty) String(writer, "globPattern"u8, pattern.Pattern);
                else
                {
                    writer.WriteStartObject("globPattern"u8); String(writer, "baseUri"u8, pattern.BaseUri);
                    String(writer, "pattern"u8, pattern.Pattern); writer.WriteEndObject();
                }
                writer.WriteNumber("kind"u8, pattern.Kind); writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
                writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
            });
            await CallClientAsync("client/registerCapability"u8, parameters.Data, cancellation).ConfigureAwait(false);
        }
        async ValueTask UnregisterAsync(Utf8String id, CancellationToken cancellation)
        {
            if (nativeWatcher is not null) { nativeWatcher.Unwatch(id); return; }
            var parameters = RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); writer.WriteStartArray("unregisterations"u8); writer.WriteStartObject();
                String(writer, "id"u8, id); writer.WriteString("method"u8, "workspace/didChangeWatchedFiles"u8);
                writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
            });
            await CallClientAsync("client/unregisterCapability"u8, parameters.Data, cancellation).ConfigureAwait(false);
        }
    }

    private async Task ReadNativeChangesAsync()
    {
        try
        {
            await foreach (var changes in nativeChanges.Reader.ReadAllAsync(apiLifetime.Token).ConfigureAwait(false))
                await WatchedFilesChangedAsync(changes, apiLifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (apiLifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (apiLifetime.IsCancellationRequested) { }
    }

    private async ValueTask StopFileWatchingAsync()
    {
        watchUpdates.Writer.TryComplete(); nativeChanges.Writer.TryComplete();
        await watchRegistrar.ConfigureAwait(false);
        nativeWatcher?.Dispose();
        if (ownedWatchBackend is { } backend) { ownedWatchBackend = null; await backend.DisposeAsync().ConfigureAwait(false); }
        await nativeChangeReader.ConfigureAwait(false);
        while (watchUpdates.Reader.TryRead(out _)) { }
        while (nativeChanges.Reader.TryRead(out _)) { }
    }
}

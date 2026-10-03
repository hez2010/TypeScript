using TypeScript.Compiler.Protocol;
using static TypeScript.Compiler.LanguageServer.LspJson;

namespace TypeScript.Compiler.LanguageServer;

internal sealed partial class LanguageServerSession
{
    private SessionTelemetry? telemetry;

    private void StartTelemetry()
    {
        if (!Boolean(initializationOptions, "enableTelemetry"u8)) return;
        long started = options.TimeProvider.GetTimestamp();
        telemetry = new((data, cancellation) => connection!.NotifyAsync("telemetry/event"u8, data, cancellation),
            CollectAsync, options.TimeProvider, apiLifetime.Token);
        projects!.SnapshotChanged += (previous, current) =>
        {
            foreach (var project in current.Projects)
                if (previous is null || !previous.ProjectMap.ContainsKey(project.Id)) telemetry.ProjectAdded(project);
        };

        async ValueTask<RpcResponse?> CollectAsync()
        {
            if (state != 2) return null;
            await using var lease = projects!.AcquireCurrentSnapshot();
            if (lease is null) return null;
            var snapshot = lease.Snapshot;
            var gc = GC.GetGCMemoryInfo();
            return SessionTelemetry.Performance(new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["openFileCount"] = snapshot.FileSystem.Overlay.Overlays.Count,
                ["projectCount"] = snapshot.Projects.Count,
                ["configCount"] = snapshot.Configurations.Count,
                ["cachedDiskFileCount"] = snapshot.FileSystem.CachedDiskFileCount,
                ["uptimeSeconds"] = options.TimeProvider.GetElapsedTime(started).TotalSeconds,
                // CLR counters keep distinct names when Go's metric has a different definition.
                ["clrHeapSizeBytes"] = gc.HeapSizeBytes,
                ["clrHeapFragmentedBytes"] = gc.FragmentedBytes,
                ["clrTotalCommittedBytes"] = gc.TotalCommittedBytes,
                ["clrAllocatedBytes"] = GC.GetTotalAllocatedBytes(),
                ["clrGen0Collections"] = GC.CollectionCount(0),
                ["clrGen1Collections"] = GC.CollectionCount(1),
                ["clrGen2Collections"] = GC.CollectionCount(2),
                ["clrThreadPoolThreads"] = ThreadPool.ThreadCount,
            });
        }
    }

    private void ReportRequestFailure(Utf8String method, Exception error)
    {
        if (telemetry is null) return;
        // No exception messages, arguments, source paths, or external plugin frames enter telemetry.
        var frames = (error.StackTrace ?? "").Split('\n').Select(line => line.Trim())
            .Where(line => line.StartsWith("at TypeScript.Compiler.", StringComparison.Ordinal))
            .Select(line => line.Split('(')[0]);
        telemetry.RequestFailure(method, Utf8String.FromString(error.GetType().Name + "\n" + string.Join('\n', frames)));
    }
}

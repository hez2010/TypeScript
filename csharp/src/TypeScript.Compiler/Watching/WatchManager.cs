using System.Threading.Channels;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Watching;

/// <summary>Owns directory watches and accumulates events while a compiler cycle is running.</summary>
public sealed class WatchManager(IWatchBackend backend, Func<Utf8String, bool> directoryExists, TextWriter? warningWriter = null) : IDisposable
{
    private readonly object sync = new();
    private readonly object changedSync = new();
    private readonly Dictionary<Utf8String, Entry> watched = new(Utf8StringComparer.Ordinal);
    private Dictionary<Utf8String, WatchEventKind> changed = new(Utf8StringComparer.Ordinal);
    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, AllowSynchronousContinuations = false });
    private bool overflow, disposed;
    private sealed class Entry(bool recursive)
    {
        internal readonly bool Recursive = recursive;
        internal IDisposable? Subscription;
        internal bool Closed;
    }

    public IReadOnlyDictionary<Utf8String, bool> WatchedDirectories
    {
        get { lock (sync) return watched.ToDictionary(pair => pair.Key, pair => pair.Value.Recursive, Utf8StringComparer.Ordinal); }
    }

    public (IReadOnlyDictionary<Utf8String, WatchEventKind> Paths, bool Overflow) DrainEvents()
    {
        lock (changedSync)
        {
            var result = (changed, overflow);
            changed = new(Utf8StringComparer.Ordinal); overflow = false;
            return result;
        }
    }

    public void ForceOverflow(bool signal = false)
    {
        lock (changedSync) overflow = true;
        if (signal) signals.Writer.TryWrite(true);
    }

    public IReadOnlyDictionary<Utf8String, bool> ResolveDesiredDirectories(IReadOnlyDictionary<Utf8String, bool> desired)
    {
        var resolved = new Dictionary<Utf8String, bool>(Utf8StringComparer.Ordinal);
        foreach (var (directory, recursive) in desired)
        {
            var target = directory;
            bool recursiveTarget = recursive;
            while (!directoryExists(target))
            {
                var parent = CompilerPath.DirectoryName(target);
                if (parent == target) break;
                target = parent; recursiveTarget = false;
            }
            if (!directoryExists(target) || !WatchPaths.CanWatchDirectory(target)) continue;
            resolved[target] = resolved.GetValueOrDefault(target) || recursiveTarget;
        }
        return resolved;
    }

    public void ReconcileWatches(IReadOnlyDictionary<Utf8String, bool> desired)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (var (directory, entry) in watched.ToArray())
                if (!desired.TryGetValue(directory, out bool recursive) || recursive != entry.Recursive)
                {
                    entry.Closed = true;
                    watched.Remove(directory);
                    entry.Subscription?.Dispose();
                }
            var additions = desired.Where(pair => !watched.ContainsKey(pair.Key)).ToArray();
            if (additions.Length == 0) return;
            var entries = additions.Select(pair => new Entry(pair.Value)).ToArray();
            var requests = additions.Select((pair, index) => new WatchDirectoryRequest(pair.Key,
                notification => OnEvents(pair.Key, entries[index], notification), pair.Value, WatchPaths.ShouldIgnore)).ToArray();
            try
            {
                // Publish identities first: even a backend which calls back during registration cannot
                // resurrect a watch that terminated before the batch finished.
                for (int i = 0; i < additions.Length; i++) watched[additions[i].Key] = entries[i];
                var subscriptions = backend.WatchDirectories(requests);
                if (subscriptions.Count != entries.Length)
                {
                    foreach (var subscription in subscriptions) subscription.Dispose();
                    throw new IOException("The watch backend returned an incomplete batch.");
                }
                for (int i = 0; i < entries.Length; i++)
                {
                    entries[i].Subscription = subscriptions[i];
                    if (entries[i].Closed) subscriptions[i].Dispose();
                }
            }
            catch
            {
                for (int i = 0; i < additions.Length; i++)
                {
                    entries[i].Closed = true; entries[i].Subscription?.Dispose();
                    if (watched.GetValueOrDefault(additions[i].Key) == entries[i]) watched.Remove(additions[i].Key);
                }
                throw;
            }
        }
    }

    private void OnEvents(Utf8String directory, Entry identity, WatchNotification notification)
    {
        lock (sync)
        {
            if (disposed || identity.Closed) return;
            if (notification.Error == WatchErrorKind.Terminated)
            {
                identity.Closed = true;
                if (watched.GetValueOrDefault(directory) == identity) watched.Remove(directory);
                identity.Subscription?.Dispose();
            }
            if (notification.Error is WatchErrorKind.Overflow or WatchErrorKind.Terminated)
            {
                ForceOverflow(signal: true);
                return;
            }
            if (notification.Error == WatchErrorKind.Error)
            {
                warningWriter?.Write($"Warning: File watch error: {notification.Exception?.Message}\n");
                return;
            }
            if (notification.Events.Count == 0) return;
            lock (changedSync)
                foreach (var change in notification.Events) changed[change.Path] = change.Kind;
            signals.Writer.TryWrite(true);
        }
    }

    public bool IsPathUnderWatch(Utf8String path, bool caseSensitive)
    {
        lock (sync) return watched.Keys.Any(directory => CompilerPath.Contains(directory, path, caseSensitive));
    }

    public async Task RunAsync(Func<CancellationToken, ValueTask> cycle, CancellationToken cancellation)
    {
        try
        {
            await foreach (var _ in signals.Reader.ReadAllAsync(cancellation).ConfigureAwait(false))
                await cycle(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { CloseAllWatches(); }
    }

    public void CloseAllWatches()
    {
        IDisposable[] subscriptions;
        lock (sync)
        {
            foreach (var entry in watched.Values) entry.Closed = true;
            subscriptions = watched.Values.Select(entry => entry.Subscription).OfType<IDisposable>().ToArray();
            watched.Clear();
        }
        foreach (var subscription in subscriptions) subscription.Dispose();
    }

    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; signals.Writer.TryComplete(); }
        CloseAllWatches();
    }
}

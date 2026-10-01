using System.Diagnostics;
using System.Threading.Channels;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Watching;

/// <summary>Native directory notifications supplied by the runtime on Windows, Linux and macOS.</summary>
public sealed class NativeWatchBackend : IWatchBackend, IAsyncDisposable
{
    private readonly object sync = new();
    private readonly HashSet<Subscription> subscriptions = [];
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task dispatch;
    private long firstEvent, lastEvent;
    private bool disposed;

    public NativeWatchBackend() => dispatch = DispatchAsync();

    public IReadOnlyList<IDisposable> WatchDirectories(IReadOnlyList<WatchDirectoryRequest> requests)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var created = new List<Subscription>(requests.Count);
            try
            {
                foreach (var request in requests)
                {
                    var subscription = new Subscription(this, request);
                    created.Add(subscription);
                    subscriptions.Add(subscription);
                    subscription.Start();
                }
                return created;
            }
            catch
            {
                foreach (var subscription in created) subscription.Dispose();
                throw;
            }
        }
    }

    private void Changed(Subscription subscription, string path, NativeChange change)
    {
        var normalized = CompilerPath.Normalize(Utf8String.FromString(path));
        lock (sync)
        {
            if (disposed || subscription.Closed || subscription.Request.Ignore?.Invoke(normalized) == true) return;
            subscription.Events.Add(normalized, change);
            Signal();
        }
    }

    private void Failed(Subscription subscription, WatchErrorKind kind, Exception? error = null)
    {
        lock (sync)
        {
            if (disposed || subscription.Closed) return;
            // Termination must survive a simultaneous overflow so the manager can replace the watch.
            if (subscription.Error != WatchErrorKind.Terminated)
            { subscription.Error = kind; subscription.Exception = error; }
            Signal();
        }
    }

    private void Signal()
    {
        lastEvent = Stopwatch.GetTimestamp();
        if (firstEvent == 0) firstEvent = lastEvent;
        wake.Writer.TryWrite(true);
    }

    private async Task DispatchAsync()
    {
        try
        {
            await foreach (var signal in wake.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
            {
                while (true)
                {
                    await Task.Delay(50, shutdown.Token).ConfigureAwait(false);
                    List<(Subscription Subscription, WatchNotification Notification)> ready = [];
                    lock (sync)
                    {
                        long now = Stopwatch.GetTimestamp();
                        if (firstEvent != 0 && Stopwatch.GetElapsedTime(lastEvent, now).TotalMilliseconds < 50
                            && Stopwatch.GetElapsedTime(firstEvent, now).TotalMilliseconds < 500) continue;
                        firstEvent = lastEvent = 0;
                        while (wake.Reader.TryRead(out _)) { }
                        foreach (var subscription in subscriptions)
                        {
                            var events = subscription.Events.Drain();
                            if (events.Count == 0 && subscription.Error == WatchErrorKind.None) continue;
                            ready.Add((subscription, new(events, subscription.Error, subscription.Exception)));
                            subscription.Error = WatchErrorKind.None; subscription.Exception = null;
                        }
                    }
                    // Callbacks are serialized and run outside the backend lock. A callback may
                    // close its own watch or reconcile the complete set of subscriptions.
                    foreach (var (subscription, notification) in ready)
                        if (!Volatile.Read(ref subscription.Closed)) subscription.Request.Callback(notification);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (!disposed)
            {
                disposed = true;
                foreach (var subscription in subscriptions.ToArray()) subscription.Dispose();
                wake.Writer.TryComplete(); shutdown.Cancel();
            }
        }
        await dispatch.ConfigureAwait(false);
        shutdown.Dispose();
    }

    private sealed class Subscription(NativeWatchBackend owner, WatchDirectoryRequest request) : IDisposable
    {
        internal readonly WatchDirectoryRequest Request = request;
        internal readonly WatchEventBuffer Events = new();
        internal WatchErrorKind Error;
        internal Exception? Exception;
        internal bool Closed;
        private FileSystemWatcher? watcher, parentWatcher;

        internal void Start()
        {
            string directory = Request.Directory.ToString();
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            watcher = new(directory)
            {
                IncludeSubdirectories = Request.Recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes
            };
            watcher.Created += (_, e) => owner.Changed(this, e.FullPath, NativeChange.Create);
            watcher.Changed += (_, e) => owner.Changed(this, e.FullPath, NativeChange.Update);
            watcher.Deleted += (_, e) => owner.Changed(this, e.FullPath, NativeChange.Delete);
            watcher.Renamed += (_, e) =>
            {
                owner.Changed(this, e.OldFullPath, NativeChange.Delete);
                owner.Changed(this, e.FullPath, NativeChange.Create);
            };
            watcher.Error += (_, e) => owner.Failed(this, !Directory.Exists(directory) ? WatchErrorKind.Terminated
                : e.GetException() is InternalBufferOverflowException ? WatchErrorKind.Overflow : WatchErrorKind.Error, e.GetException());

            // Watching the parent also detects deletion/renaming of the root itself. The manager
            // then watches an existing ancestor until the desired directory appears again.
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
            if (parent is not null && Directory.Exists(parent))
            {
                var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                bool IsRoot(string path) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), root, comparison);
                parentWatcher = new(parent) { NotifyFilter = NotifyFilters.DirectoryName, IncludeSubdirectories = false };
                parentWatcher.Deleted += (_, e) => { if (IsRoot(e.FullPath)) owner.Failed(this, WatchErrorKind.Terminated); };
                parentWatcher.Renamed += (_, e) => { if (IsRoot(e.OldFullPath)) owner.Failed(this, WatchErrorKind.Terminated); };
                parentWatcher.Error += (_, e) => owner.Failed(this, Directory.Exists(directory) ? WatchErrorKind.Overflow : WatchErrorKind.Terminated, e.GetException());
                parentWatcher.EnableRaisingEvents = true;
            }
            watcher.EnableRaisingEvents = true;
            if (!Directory.Exists(directory)) owner.Failed(this, WatchErrorKind.Terminated);
        }

        public void Dispose()
        {
            lock (owner.sync)
            {
                if (Closed) return;
                Volatile.Write(ref Closed, true);
                owner.subscriptions.Remove(this);
                watcher?.Dispose(); parentWatcher?.Dispose();
                Events.Drain();
            }
        }
    }
}

internal enum NativeChange { Create, Update, Delete }

/// <summary>Coalesces atomic saves and cancels files created and removed within one delivery batch.</summary>
internal sealed class WatchEventBuffer
{
    private sealed class Entry { internal long Created, Updated, Deleted; }
    private readonly Dictionary<Utf8String, Entry> entries = new(Utf8StringComparer.Ordinal);
    private long sequence;

    internal void Add(Utf8String path, NativeChange change)
    {
        if (!entries.TryGetValue(path, out var entry)) entries[path] = entry = new();
        long next = ++sequence;
        switch (change)
        {
            case NativeChange.Create:
                if (entry.Deleted > entry.Created && entry.Deleted > entry.Updated)
                { entry.Created = entry.Deleted = 0; entry.Updated = next; }
                else entry.Created = next;
                break;
            case NativeChange.Update: entry.Updated = next; break;
            case NativeChange.Delete: entry.Deleted = next; break;
        }
    }

    internal IReadOnlyList<WatchEvent> Drain()
    {
        var result = new List<WatchEvent>(entries.Count);
        foreach (var (path, entry) in entries)
        {
            if (entry.Deleted != 0)
            {
                if (entry.Created != 0 && entry.Created < entry.Deleted && entry.Updated < entry.Deleted) continue;
                result.Add(new(path, WatchEventKind.Delete));
            }
            else if (entry.Created != 0 || entry.Updated != 0) result.Add(new(path, WatchEventKind.Update));
        }
        entries.Clear();
        return result;
    }
}

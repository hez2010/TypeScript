using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Watching;

namespace TypeScript.Compiler.LanguageServer;

internal sealed record LspWatchPattern(Utf8String Pattern, Utf8String BaseUri = default, int Kind = 7)
{
    internal Utf8String Key => BaseUri.IsEmpty ? Pattern : BaseUri + "/"u8 + Pattern;
    internal bool Recursive => Key.Contains("**"u8, StringComparison.Ordinal);
    internal Utf8String Root => BaseUri.IsEmpty ? RootFromGlob(Pattern)
        : RootFromGlob(CompilerPath.Combine(DocumentUris.ToFileName(BaseUri), Pattern));

    internal static Utf8String RootFromGlob(Utf8String pattern)
    {
        pattern = CompilerPath.NormalizeSlashes(pattern);
        int end = pattern.Length;
        for (int index = 0; index < pattern.Length; index++)
            if (pattern[index] is (byte)'*' or (byte)'?' or (byte)'[' or (byte)'{') { end = index; break; }
        while (end > 0 && pattern[end - 1] == '/') end--;
        return end == 0 ? default : CompilerPath.Normalize(pattern[..end]);
    }
}

/// <summary>Maintains LSP watches across missing, deleted, and recreated directories.</summary>
internal sealed class LspFileWatcher(IFileSystem fileSystem, IWatchBackend backend, Action<IReadOnlyList<FileChange>> onChanges,
    Action<Utf8String>? log = null) : IDisposable
{
    private readonly IFileSystem fileSystem = fileSystem;
    private readonly IWatchBackend backend = backend;
    private readonly Action<Utf8String>? log = log;
    private readonly object sync = new();
    private readonly Dictionary<Utf8String, List<Entry>> watches = [];
    private readonly Dictionary<Utf8String, FileChange> pending = [];
    private Timer? flushTimer;
    private bool closed;

    internal void Watch(Utf8String id, IReadOnlyList<LspWatchPattern> patterns)
    {
        List<Entry> entries = [];
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (!watches.TryAdd(id, entries)) throw new InvalidOperationException($"Watcher {id} already exists");
        }
        try
        {
            foreach (var pattern in patterns)
            {
                if (pattern.Root is not { IsEmpty: false } root) continue;
                var entry = new Entry(this, root, pattern.Kind, pattern.Recursive);
                // Publish before installation so Close/Unwatch can prevent an in-flight install.
                lock (sync)
                {
                    ObjectDisposedException.ThrowIf(closed || !watches.TryGetValue(id, out var active) || active != entries, this);
                    entries.Add(entry);
                }
                entry.Reconcile(false);
                lock (sync)
                    ObjectDisposedException.ThrowIf(closed || !watches.TryGetValue(id, out var active) || active != entries, this);
            }
        }
        catch
        {
            lock (sync) if (watches.GetValueOrDefault(id) == entries) watches.Remove(id);
            foreach (var entry in entries) entry.Dispose();
            throw;
        }
    }

    internal void Unwatch(Utf8String id)
    {
        List<Entry> entries;
        lock (sync)
            if (!watches.Remove(id, out entries!)) throw new InvalidOperationException($"No watcher with id {id}");
        foreach (var entry in entries) entry.Dispose();
    }

    public void Dispose()
    {
        Entry[] entries;
        lock (sync)
        {
            if (closed) return;
            closed = true;
            entries = watches.Values.SelectMany(value => value).ToArray(); watches.Clear();
            flushTimer?.Dispose(); flushTimer = null; pending.Clear();
        }
        foreach (var entry in entries) entry.Dispose();
    }

    private void Forward(int kind, IReadOnlyList<WatchEvent> events)
    {
        lock (sync)
        {
            if (closed) return;
            foreach (var item in events)
            {
                var change = item.Kind switch
                {
                    WatchEventKind.Update when (kind & 3) != 0 => FileChangeKind.WatchChange,
                    WatchEventKind.Delete when (kind & 4) != 0 => FileChangeKind.WatchDelete,
                    _ => (FileChangeKind?)null
                };
                if (change is null) continue;
                var path = CompilerPath.NormalizeSlashes(item.Path);
                pending[path] = new(change.Value, path);
            }
            ScheduleFlush();
        }
    }

    private void SyntheticCreates(Utf8String directory, int kind, bool recursive)
    {
        if ((kind & 1) == 0) return;
        List<Utf8String> paths = [directory];
        var directories = new Stack<Utf8String>();
        var comparer = fileSystem.CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
        HashSet<Utf8String> visited = new(comparer);
        var parent = CompilerPath.DirectoryName(directory);
        if (!recursive || parent == directory || comparer.Equals(fileSystem.RealPath(directory), CompilerPath.Combine(fileSystem.RealPath(parent), CompilerPath.BaseName(directory))))
            directories.Push(directory);
        while (directories.TryPop(out var current))
        {
            var real = fileSystem.RealPath(current);
            if (!visited.Add(real)) continue;
            var entries = fileSystem.GetAccessibleEntries(current);
            foreach (var file in entries.Files) paths.Add(CompilerPath.Combine(current, file));
            foreach (var name in entries.Directories)
            {
                var child = CompilerPath.Combine(current, name); paths.Add(child);
                if (recursive && entries.SymbolicLinks?.Contains(name) != true && comparer.Equals(fileSystem.RealPath(child), CompilerPath.Combine(real, name)))
                    directories.Push(child);
            }
        }
        lock (sync)
        {
            if (closed) return;
            foreach (var path in paths) pending.TryAdd(path, new(FileChangeKind.WatchCreate, path));
            ScheduleFlush();
        }
    }

    private void ScheduleFlush() => flushTimer ??= new(_ => Flush(), null, 75, Timeout.Infinite);

    internal void Flush()
    {
        FileChange[] changes;
        lock (sync)
        {
            if (closed) return;
            changes = pending.Values.ToArray(); pending.Clear();
            flushTimer?.Dispose(); flushTimer = null;
        }
        if (changes.Length > 0) onChanges(changes);
    }

    private sealed class Entry(LspFileWatcher owner, Utf8String target, int kind, bool recursive) : IDisposable
    {
        private readonly object sync = new();
        private IDisposable? subscription;
        private Utf8String watchedDirectory;
        private bool watchingTarget;
        private volatile bool closed;

        internal void Reconcile(bool synthetic)
        {
            lock (sync)
            {
                while (!closed)
                {
                    if (owner.fileSystem.DirectoryExists(target))
                    {
                        if (watchingTarget && subscription is not null) return;
                        Install(target, true);
                        if (synthetic) owner.SyntheticCreates(target, kind, recursive);
                        return;
                    }
                    var ancestor = target;
                    while (!owner.fileSystem.DirectoryExists(ancestor))
                    {
                        var parent = CompilerPath.DirectoryName(ancestor);
                        if (parent == ancestor || parent.IsEmpty) { Clear(); return; }
                        ancestor = parent;
                    }
                    if (!watchingTarget && subscription is not null && watchedDirectory == ancestor) return;
                    Install(ancestor, false);
                    // Creation between the existence check and subscription must also promote.
                    synthetic = true;
                }
            }
        }

        private void Install(Utf8String directory, bool isTarget)
        {
            var next = owner.backend.WatchDirectories([new(directory, isTarget ? TargetChanged : AncestorChanged, isTarget && recursive)])[0];
            var previous = Interlocked.Exchange(ref subscription, next);
            watchedDirectory = directory; watchingTarget = isTarget;
            previous?.Dispose();
            if (closed) Clear();
        }

        private void TargetChanged(WatchNotification notification)
        {
            if (notification.Error != WatchErrorKind.None)
                owner.log?.Invoke(Utf8String.FromString($"lspwatcher: watch {notification.Error} in {target}: {notification.Exception?.Message}"));
            if (notification.Events.Count != 0) owner.Forward(kind, notification.Events);
            if (notification.Error != WatchErrorKind.Terminated) return;
            lock (sync) { if (closed) return; Clear(); }
            Recover();
        }

        private void AncestorChanged(WatchNotification notification) => Recover();
        private void Recover()
        {
            try { Reconcile(true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            { owner.log?.Invoke(Utf8String.FromString($"lspwatcher: failed to recover {target}: {error.Message}")); }
        }

        private void Clear()
        {
            var previous = Interlocked.Exchange(ref subscription, null);
            watchedDirectory = default; watchingTarget = false;
            previous?.Dispose();
        }

        public void Dispose()
        {
            closed = true;
            Interlocked.Exchange(ref subscription, null)?.Dispose();
        }
    }
}

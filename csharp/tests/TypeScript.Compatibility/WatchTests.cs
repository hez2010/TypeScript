using System.Threading.Channels;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Execution;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Text;
using TypeScript.Compiler.Watching;

namespace TypeScript.Compatibility;

internal static class WatchTests
{
    internal static async Task<int> SafetyAsync()
    {
        int assertions = 0;
        void Check(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
        foreach (bool sensitive in new[] { false, true })
        {
            var set = new DirectoryWatchSet(sensitive);
            set.Set((Utf8String)"/repo/src"u8, true); set.Set((Utf8String)"/repo/config"u8, false); set.Set((Utf8String)"/repo/node_modules/a"u8, false);
            foreach (var (path, expected) in new (Utf8String, bool)[]
            {
                ((Utf8String)"/repo/src"u8, true), ((Utf8String)"/repo/src/nested"u8, true), ((Utf8String)"/repo/src/nested/deep"u8, true),
                ((Utf8String)"/repo/config"u8, true), ((Utf8String)"/repo/config/nested"u8, false), ((Utf8String)"/repo/node_modules/a"u8, true),
                ((Utf8String)"/repo/node_modules/b"u8, false), ((Utf8String)"/repo"u8, false), ((Utf8String)"/other"u8, false)
            }) Check(set.Covers(path) == expected, $"coverage {path}");
            Check(set.Covers("/REPO/SRC/nested"u8) == !sensitive, "case-sensitive recursive coverage");
            Check(set.Covers("/repo/node_modules/A"u8) == !sensitive, "case-sensitive exact coverage");
            set.Set((Utf8String)"/repo/SRC"u8, false);
            Check(set.Directories.Count == (sensitive ? 4 : 3), "canonical deduplication");
            set.Set((Utf8String)"/repo/config"u8, true); set.Set((Utf8String)"/repo/config"u8, false);
            Check(set.Covers("/repo/config/nested"u8), "recursive upgrade is retained");
        }
        foreach (var path in new Utf8String[] { "/repo/.git"u8, "/repo/.git/config"u8, "/repo/node_modules/.cache/f"u8, "C:\\repo\\.#file"u8 })
            Check(WatchPaths.ShouldIgnore(path), "ignored temporary path");
        foreach (var path in new Utf8String[] { "/repo/.github/workflow"u8, "/repo/node_modules/pkg/index.d.ts"u8, "/repo/f.ts"u8 })
            Check(!WatchPaths.ShouldIgnore(path), "regular path is retained");
        foreach (var path in new Utf8String[] { "/"u8, "/home"u8, "/home/user"u8, "/home/user/project"u8, "C:/"u8, "C:/Users/name"u8 })
            Check(!WatchPaths.CanWatchDirectory(path), "broad ancestor is not watched");
        foreach (var path in new Utf8String[] { "/home/user/project/src"u8, "C:/repo/src"u8, "C:/Users/name/project/src"u8 })
            Check(WatchPaths.CanWatchDirectory(path), "project directory is watchable");

        // Atomic save, temporary-file removal, repeated modification and delete/recreate are distinct transitions.
        var buffer = new WatchEventBuffer();
        buffer.Add("a"u8, NativeChange.Create); buffer.Add("a"u8, NativeChange.Update); buffer.Add("a"u8, NativeChange.Delete);
        buffer.Add("b"u8, NativeChange.Delete); buffer.Add("b"u8, NativeChange.Create);
        buffer.Add("c"u8, NativeChange.Update); buffer.Add("c"u8, NativeChange.Update);
        buffer.Add("d"u8, NativeChange.Update); buffer.Add("d"u8, NativeChange.Delete);
        var batch = buffer.Drain().ToDictionary(change => change.Path, change => change.Kind);
        Check(batch.Count == 3 && !batch.ContainsKey("a"u8), "temporary create/delete cancels");
        Check(batch["b"u8] == WatchEventKind.Update && batch["c"u8] == WatchEventKind.Update && batch["d"u8] == WatchEventKind.Delete, "coalesced event kinds");
        Check(buffer.Drain().Count == 0, "drain consumes the batch");

        var backend = new MockBackend();
        var existing = new HashSet<Utf8String> { "/home/user/project/src"u8, "/home/user/project"u8 };
        using var warnings = new StringWriter();
        using var manager = new WatchManager(backend, existing.Contains, warnings);
        var desired = new Dictionary<Utf8String, bool> { ["/home/user/project/src/missing"u8] = true };
        var resolved = manager.ResolveDesiredDirectories(desired);
        Check(resolved.Count == 1 && !resolved["/home/user/project/src"u8], "missing directory watches its existing ancestor nonrecursively");
        manager.ReconcileWatches(resolved);
        var first = backend.Subscriptions.Single();
        manager.ReconcileWatches(resolved);
        Check(backend.Subscriptions.Count == 1, "unchanged watch is retained");
        Check(manager.IsPathUnderWatch("/home/user/project/src/child/f"u8, true), "event filtering recognizes watched trees");
        first.Request.Callback(new([new("/home/user/project/src/f.ts"u8, WatchEventKind.Delete)]));
        first.Request.Callback(new([new("/home/user/project/src/f.ts"u8, WatchEventKind.Update)]));
        var changes = manager.DrainEvents();
        Check(changes.Paths.Count == 1 && changes.Paths.Values.Single() == WatchEventKind.Update && !changes.Overflow, "manager retains most recent event kind");
        Check(manager.DrainEvents().Paths.Count == 0, "manager drain is atomic");
        first.Request.Callback(new([], WatchErrorKind.Overflow));
        Check(manager.DrainEvents().Overflow && manager.WatchedDirectories.Count == 1, "overflow retains watches and invalidates inputs");
        first.Request.Callback(new([], WatchErrorKind.Error, new IOException("denied")));
        Check(warnings.ToString() == "Warning: File watch error: denied\n" && !manager.DrainEvents().Overflow, "ordinary watch error is reported");
        manager.ReconcileWatches(new Dictionary<Utf8String, bool> { ["/home/user/project/src"u8] = true });
        Check(first.Closed && backend.Subscriptions.Count == 2, "recursive change replaces watch");
        first.Request.Callback(new([], WatchErrorKind.Terminated));
        Check(manager.WatchedDirectories.Count == 1 && !manager.DrainEvents().Overflow, "stale termination cannot close replacement");
        backend.Subscriptions[^1].Request.Callback(new([], WatchErrorKind.Terminated));
        Check(manager.WatchedDirectories.Count == 0 && manager.DrainEvents().Overflow && backend.Subscriptions[^1].Closed, "termination closes current identity");
        backend.Fail = true;
        try { manager.ReconcileWatches(resolved); Check(false, "batch failure must throw"); } catch (IOException) { }
        Check(manager.WatchedDirectories.Count == 0, "failed batch leaves no phantom watch");
        backend.Fail = false; backend.TerminateDuringCreation = true;
        manager.ReconcileWatches(resolved);
        Check(manager.WatchedDirectories.Count == 0 && backend.Subscriptions[^1].Closed, "termination during registration is not lost");
        backend.TerminateDuringCreation = false;
        manager.ReconcileWatches(resolved);
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int cycles = 0;
        var loop = manager.RunAsync(async token =>
        {
            manager.DrainEvents();
            if (Interlocked.Increment(ref cycles) == 1) { entered.SetResult(); await resume.Task.WaitAsync(token); }
            else cancel.Cancel();
        }, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        backend.Subscriptions[^1].Request.Callback(new([new("/home/user/project/src/later.ts"u8, WatchEventKind.Update)]));
        resume.SetResult();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        Check(cycles == 2 && manager.WatchedDirectories.Count == 0, "events during a cycle trigger a subsequent cycle and cancellation closes watches");
        manager.Dispose(); manager.Dispose();
        backend.Subscriptions[^1].Request.Callback(new([], WatchErrorKind.Overflow));
        Check(!manager.DrainEvents().Overflow, "late callback after disposal is ignored");
        assertions += await ConfigurationDuringEmitAsync();
        return assertions;
    }

    private static async Task<int> ConfigurationDuringEmitAsync()
    {
        Utf8String root = "/home/user/project/Src"u8, configPath = root + "/tsconfig.json"u8, source = root + "/a.ts"u8;
        var memory = new MemoryFileSystem([], caseSensitive: false, currentDirectory: root);
        memory.WriteFile(configPath, "{\"compilerOptions\":{\"lib\":[\"es5\"],\"outDir\":\"dist\",\"incremental\":true},\"files\":[\"a.ts\"]}"u8);
        memory.WriteFile(source, "export const value = 1;"u8);
        var backend = new MockBackend { CaseSensitive = false };
        DateTime time = DateTime.UtcNow;
        var fs = new WatchTransitionTests.TrackingFileSystem(new LibraryFileSystem(memory), () => time = time.AddSeconds(1));
        await using var session = new ProjectWatchSession(fs, root, new ConfigParser(fs, root).Parse(configPath),
            defaultLibraryDirectory: "bundled:///libs"u8, backend: backend);
        await session.StartAsync();
        memory.WriteFile(source, "export const value = 2;"u8);
        backend.SendChangedPaths([new(source, WatchEventKind.Update)]);
        fs.OnWrite = path =>
        {
            if (!path.EndsWith("/a.js"u8, StringComparison.Ordinal)) return;
            fs.OnWrite = null;
            memory.WriteFile(configPath, "{\"compilerOptions\":{\"lib\":[\"es5\"],\"outDir\":\"updated\",\"incremental\":true},\"files\":[\"a.ts\"]}"u8);
            // The first cycle captures this new mtime after emit; its queued event must still reparse the config.
            backend.SendChangedPaths([new(configPath.ToLowerInvariant(), WatchEventKind.Update)]);
        };
        await session.CycleAsync();
        if (memory.FileExists(root + "/updated/a.js"u8)) throw new InvalidOperationException("Config edit must happen during the old emission");
        var changed = await session.CycleAsync();
        if (!changed.Rebuilt || !memory.FileExists(root + "/updated/a.js"u8)) throw new InvalidOperationException("Config change during emit was lost");
        return 2;
    }

    internal static async Task<int> NativeSafetyAsync()
    {
        int assertions = 0;
        void Check(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
        string directory = Path.Combine(Path.GetTempPath(), "typescript-csharp-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string root = Path.Combine(directory, "project"); Directory.CreateDirectory(root);
            Utf8String PathOf(string path) => CompilerPath.Normalize(Utf8String.FromString(Path.GetFullPath(path)));
            var delivered = Channel.CreateUnbounded<WatchNotification>();
            await using var backend = new NativeWatchBackend();
            int callbacks = 0, concurrent = 0, maxConcurrent = 0;
            void Receive(WatchNotification notification)
            {
                int active = Interlocked.Increment(ref concurrent);
                maxConcurrent = Math.Max(maxConcurrent, active);
                Interlocked.Increment(ref callbacks);
                delivered.Writer.TryWrite(notification);
                Interlocked.Decrement(ref concurrent);
            }
            var watches = backend.WatchDirectories([new(PathOf(root), Receive, true, WatchPaths.ShouldIgnore)]);
            async Task<WatchNotification> Await(Func<WatchNotification, bool> predicate)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    var notification = await delivered.Reader.ReadAsync(timeout.Token);
                    if (predicate(notification)) return notification;
                }
            }
            async Task Event(string path, WatchEventKind kind) => await Await(notification => notification.Events.Any(change => change.Path == PathOf(path) && change.Kind == kind));
            string file = Path.Combine(root, "file.ts");
            await File.WriteAllTextAsync(file, "export const x = 1;"); await Event(file, WatchEventKind.Update);
            Check(true, "native file creation");
            await File.AppendAllTextAsync(file, "\nexport const y = 2;"); await Event(file, WatchEventKind.Update);
            Check(true, "native file update");
            string renamed = Path.Combine(root, "renamed.ts"); File.Move(file, renamed);
            var rename = await Await(notification => notification.Events.Any(change => change.Path == PathOf(file) && change.Kind == WatchEventKind.Delete));
            if (!rename.Events.Any(change => change.Path == PathOf(renamed) && change.Kind == WatchEventKind.Update)) await Event(renamed, WatchEventKind.Update);
            Check(true, "native rename reports old and new paths");
            string nested = Path.Combine(root, "nested"); Directory.CreateDirectory(nested);
            string nestedFile = Path.Combine(nested, "deep.ts"); await File.WriteAllTextAsync(nestedFile, "export {};"); await Event(nestedFile, WatchEventKind.Update);
            Check(true, "recursive watch discovers new descendants");
            File.Delete(nestedFile); await Event(nestedFile, WatchEventKind.Delete); Check(true, "native deletion");
            string ignored = Path.Combine(root, ".git"); Directory.CreateDirectory(ignored);
            await File.WriteAllTextAsync(Path.Combine(ignored, "index"), "ignore");
            string marker = Path.Combine(root, "marker.ts"); await File.WriteAllTextAsync(marker, "export {};");
            var filtered = await Await(notification => notification.Events.Any(change => change.Path == PathOf(marker)));
            Check(filtered.Events.All(change => !WatchPaths.ShouldIgnore(change.Path)), "ignored directories do not deliver events");
            string moved = Path.Combine(directory, "moved"); Directory.Move(root, moved);
            await Await(notification => notification.Error == WatchErrorKind.Terminated);
            Check(true, "renaming watched root terminates subscription");
            watches[0].Dispose();
            Directory.CreateDirectory(root);
            using var replacement = backend.WatchDirectories([new(PathOf(root), Receive)])[0];
            string recreated = Path.Combine(root, "after.ts"); await File.WriteAllTextAsync(recreated, "export {};"); await Event(recreated, WatchEventKind.Update);
            Check(true, "watch can be recreated after root replacement");
            try
            {
                backend.WatchDirectories([new(PathOf(root), Receive), new(PathOf(Path.Combine(directory, "missing")), Receive)]);
                Check(false, "missing directory must reject batch");
            }
            catch (DirectoryNotFoundException) { Check(true, "native batch failure rolls back subscriptions"); }
            await backend.DisposeAsync();
            int closedCount = callbacks;
            await File.AppendAllTextAsync(recreated, "\n// after close");
            await Task.Delay(150);
            Check(callbacks == closedCount && maxConcurrent == 1, "callbacks are serialized and stop after disposal");
            return assertions;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static async Task<int> CommandSafetyAsync()
    {
        int assertions = 0;
        void Check(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
        string directory = Path.Combine(Path.GetTempPath(), "typescript-csharp-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (bool build in new[] { false, true })
            {
                string project = Path.Combine(directory, build ? "build" : "project"); Directory.CreateDirectory(project);
                string config = Path.Combine(project, "tsconfig.json"), source = Path.Combine(project, "a.ts");
                string Configuration(string output) => "{\"compilerOptions\":{\"lib\":[\"es5\"],\"incremental\":true,\"composite\":true,\"strict\":true,\"outDir\":\"" + output + "\"},\"include\":[\"*.ts\"]}";
                await File.WriteAllTextAsync(config, Configuration("dist"));
                await File.WriteAllTextAsync(source, "export function value(): number { return 1; }");
                await File.WriteAllTextAsync(Path.Combine(project, "b.ts"), "import { value } from './a'; export const result: number = value();");
                using var output = new CommandOutput();
                var fileSystem = new LibraryFileSystem(new PhysicalFileSystem());
                Utf8String root = CompilerPath.Normalize(Utf8String.FromString(project));
                await using var command = new CompilerCommand(fileSystem, root, output);
                Utf8String[] arguments = build ? ["-b"u8, "-w"u8, "--pretty"u8, "false"u8, "--preserveWatchOutput"u8]
                    : ["-w"u8, "--pretty"u8, "false"u8, "--preserveWatchOutput"u8];
                using var cancel = new CancellationTokenSource();
                Check(await command.ExecuteAsync(arguments) == CompilerExitStatus.Success, "initial native watch compilation");
                Check(File.Exists(Path.Combine(project, "dist", "a.js")) && command.Watches!.WatchedDirectories.Count != 0, "native command emits and subscribes");
                var loop = command.RunWatchAsync(cancel.Token);
                async Task AwaitText(int start, string expected, Func<bool>? ready = null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    try
                    {
                        while (!output.Snapshot()[start..].Contains(expected, StringComparison.Ordinal) || ready?.Invoke() == false)
                        {
                            if (loop.IsCompleted) await loop;
                            await Task.Delay(20, timeout.Token);
                        }
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    { throw new InvalidOperationException($"Native {(build ? "build" : "project")} watch did not report {expected}; outDir={command.Program?.Program.Configuration.Options.OutDir}; roots={string.Join(',', command.Program?.Program.RootFileNames ?? [])}; files={string.Join(',', Directory.GetFiles(project, "*", SearchOption.AllDirectories))}: {output.Snapshot()}"); }
                }
                try
                {
                    int start = output.Snapshot().Length;
                    await File.WriteAllTextAsync(source, "export function value(): string { return 'changed'; }");
                    await AwaitText(start, "Found 1 error.");
                    Check(output.Snapshot()[start..].Contains("TS2322", StringComparison.Ordinal), "signature change invalidates consumer");
                    start = output.Snapshot().Length;
                    await File.WriteAllTextAsync(source, "export function value(): number { return 3; }");
                    await AwaitText(start, "Found 0 errors.", () => File.ReadAllText(Path.Combine(project, "dist", "a.js")).Contains("return 3", StringComparison.Ordinal));
                    Check(File.ReadAllText(Path.Combine(project, "dist", "a.js")).Contains("return 3", StringComparison.Ordinal), "native watch recovers and emits current source");
                    start = output.Snapshot().Length;
                    await File.WriteAllTextAsync(config, Configuration("updated"));
                    await AwaitText(start, "Found 0 errors.", () => File.Exists(Path.Combine(project, "updated", "a.js")));
                    Check(File.Exists(Path.Combine(project, "updated", "a.js")), "native config edit redirects output");
                    start = output.Snapshot().Length;
                    await File.WriteAllTextAsync(Path.Combine(project, "added.ts"), "export const added = true;");
                    await AwaitText(start, "Found 0 errors.", () => File.Exists(Path.Combine(project, "updated", "added.js")));
                    Check(File.Exists(Path.Combine(project, "updated", "added.js")), "native wildcard discovers created root");
                    if (build) await command.DisposeAsync(); else cancel.Cancel();
                    await loop.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(command.Watches!.WatchedDirectories.Count == 0, "cancellation or concurrent disposal releases native watches");
                    await command.DisposeAsync(); await command.DisposeAsync();
                    int closed = output.Snapshot().Length;
                    await File.WriteAllTextAsync(source, "export const value = 4;");
                    await Task.Delay(150);
                    Check(output.Snapshot().Length == closed, "disposed command produces no callbacks");
                    try { await command.CycleAsync(); Check(false, "disposed command must reject cycles"); }
                    catch (ObjectDisposedException) { Check(true, "disposed command rejects cycles"); }
                }
                finally { cancel.Cancel(); await loop.WaitAsync(TimeSpan.FromSeconds(5)); }
                Utf8String missing = CompilerPath.Combine(root, "missing.ts"u8);
                Check(fileSystem.RealPath(missing) == missing, "native realpath preserves missing paths");
            }
            return assertions;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class CommandOutput : StringWriter
    {
        private readonly object gate = new();
        public override void Write(string? value) { lock (gate) base.Write(value); }
        internal string Snapshot() { lock (gate) return ToString(); }
    }

    internal sealed class MockBackend : IWatchBackend
    {
        internal sealed class Subscription(WatchDirectoryRequest request) : IDisposable
        {
            internal readonly WatchDirectoryRequest Request = request;
            internal bool Closed;
            public void Dispose() => Closed = true;
        }
        internal readonly List<Subscription> Subscriptions = [];
        internal bool Fail, TerminateDuringCreation;
        internal bool CaseSensitive = true;
        internal Func<Utf8String, bool>? DirectoryExists;
        internal void SendChangedPaths(IReadOnlyList<WatchEvent> changes)
        {
            var events = new List<WatchEvent>(changes);
            var directories = new HashSet<Utf8String>();
            foreach (var change in changes)
            {
                var directory = CompilerPath.DirectoryName(change.Path);
                while (directory.Length > CompilerPath.RootLength(directory) && directories.Add(directory))
                {
                    events.Add(new(directory, WatchEventKind.Update));
                    directory = CompilerPath.DirectoryName(directory);
                }
            }
            foreach (var subscription in Subscriptions.ToArray().Where(subscription => !subscription.Closed))
            {
                var request = subscription.Request;
                var comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var selected = events.Where(change => request.Ignore?.Invoke(change.Path) != true
                    && (request.Recursive ? CompilerPath.Contains(request.Directory, change.Path, CaseSensitive) && !change.Path.Equals(request.Directory, comparison)
                        : CompilerPath.DirectoryName(change.Path).Equals(request.Directory, comparison))).ToArray();
                if (selected.Length != 0) request.Callback(new(selected));
            }
        }
        internal void SendOverflow()
        {
            foreach (var subscription in Subscriptions.ToArray().Where(subscription => !subscription.Closed)) subscription.Request.Callback(new([], WatchErrorKind.Overflow));
        }
        public IReadOnlyList<IDisposable> WatchDirectories(IReadOnlyList<WatchDirectoryRequest> requests)
        {
            if (Fail) throw new IOException("test watch failure");
            foreach (var request in requests) if (DirectoryExists is not null && !DirectoryExists(request.Directory)) throw new DirectoryNotFoundException(request.Directory.ToString());
            var created = requests.Select(request => new Subscription(request)).ToArray();
            Subscriptions.AddRange(created);
            if (TerminateDuringCreation) foreach (var request in requests) request.Callback(new([], WatchErrorKind.Terminated));
            return created;
        }
    }
}

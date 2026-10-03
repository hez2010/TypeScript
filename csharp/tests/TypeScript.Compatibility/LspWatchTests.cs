using System.Text.Json;
using System.Threading.Channels;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Watching;

namespace TypeScript.Compatibility;

internal static class LspWatchTests
{
    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            try { WriteLine(line); }
            catch (ArgumentException error) { Console.Error.WriteLine(error.Message); Console.WriteLine("{\"error\":true}"); }
        }
        static void WriteLine(string line)
        {
            using var document = JsonDocument.Parse(line);
            var input = document.RootElement;
            Utf8String Text(string name) => input.TryGetProperty(name, out var value) ? Utf8String.FromString(value.GetString()!) : default;
            Utf8String[] Texts(string name) => input.TryGetProperty(name, out var value) ? value.EnumerateArray().Select(item => Utf8String.FromString(item.GetString()!)).ToArray() : [];
            bool Flag(string name) => input.TryGetProperty(name, out var value) && value.GetBoolean();
            string mode = input.GetProperty("mode").GetString()!;
            using var buffer = new MemoryStream(); using var writer = new Utf8JsonWriter(buffer);
            if (mode == "registry")
            {
                int attempts = 0, removed = 0, errors = 0; bool fail = false; HashSet<Utf8String> active = [];
                var registry = new LspWatchRegistry((id, pattern, _) =>
                {
                    attempts++;
                    if (fail && pattern.Pattern == "/b/*"u8 || !active.Add(id)) throw new IOException("registration failure");
                    return ValueTask.CompletedTask;
                }, (id, _) => { active.Remove(id); removed++; return ValueTask.CompletedTask; }, _ => errors++);
                ProjectWatchGroup a = new("a"u8, [new("/a-shared/*"u8)]), b = new("b"u8, [new("/a-shared/*"u8), new("/b/*"u8)]);
                bool shared = Text("pattern") == "shared"u8;
                void Update(ProjectWatchGroup[] groups) => registry.UpdateAsync(groups, default).AsTask().GetAwaiter().GetResult();
                if (shared) Update([a]);
                fail = true; Update(shared ? [a, b] : [b]);
                fail = false; errors = 0; Update(shared ? [a, b] : [b]); int retryErrors = errors;
                Update(shared ? [a] : []); Update([]);
                writer.WriteStartObject(); writer.WriteNumber("attempts", attempts); writer.WriteNumber("unregistered", removed);
                writer.WriteNumber("active", active.Count); writer.WriteNumber("retryErrors", retryErrors); writer.WriteEndObject();
            }
            else if (mode == "root") JsonStrings.WriteString(writer, LspWatchPattern.RootFromGlob(Text("pattern")));
            else if (mode is "components" or "parents")
            {
                writer.WriteStartArray();
                foreach (var path in mode == "components" ? ProjectWatchPlan.Components(Text("pattern")) : ProjectWatchPlan.CommonParents(Texts("files"), Flag("caseSensitive")))
                    JsonStrings.WriteString(writer, path);
                writer.WriteEndArray();
            }
            else
            {
                var patterns = mode switch
                {
                    "resolution" => ProjectWatchPlan.Resolution(Texts("files"), Text("workspace"), Text("library"), Text("currentDirectory"), Flag("caseSensitive"), Flag("relative")),
                    "typings" => ProjectWatchPlan.Typings(Texts("files"), Text("typings"), Text("workspace"), Flag("caseSensitive"), Flag("relative")),
                    "exact" => ProjectWatchPlan.ExactFiles(Texts("files"), Text("workspace"), Flag("caseSensitive"), Flag("relative")),
                    _ => throw new InvalidDataException(mode)
                };
                writer.WriteStartArray();
                foreach (var pattern in patterns)
                {
                    writer.WriteStartObject(); writer.WriteString("pattern", pattern.Pattern.Span); writer.WriteString("baseUri", pattern.BaseUri.Span);
                    writer.WriteNumber("kind", pattern.Kind); writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.Flush(); Console.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        }
    }

    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        void Fails(Action action, string message)
        {
            bool failed = false; try { action(); } catch (Exception error) when (error is IOException or InvalidOperationException) { failed = true; }
            Check(failed, message);
        }
        foreach (var (pattern, root) in new (Utf8String, Utf8String)[]
        {
            ((Utf8String)"/abs/path/**/*"u8, (Utf8String)"/abs/path"u8), ((Utf8String)"/abs/path/"u8, (Utf8String)"/abs/path"u8), ((Utf8String)"/abs/path/?.ts"u8, (Utf8String)"/abs/path"u8),
            ((Utf8String)"/abs/path/{a,b}/*"u8, (Utf8String)"/abs/path"u8), ((Utf8String)"C:\\repo\\**\\*"u8, (Utf8String)"C:/repo"u8), ((Utf8String)"**/*.ts"u8, default),
            ((Utf8String)"/abs/path/a.ts"u8, (Utf8String)"/abs/path/a.ts"u8), ((Utf8String)"/abs/path/a[0].ts"u8, (Utf8String)"/abs/path/a"u8)
        }) Check(LspWatchPattern.RootFromGlob(pattern) == root, $"rootFromGlob {pattern}");
        Check(new LspWatchPattern("**/*"u8, "file:///external/project"u8).Root == "/external/project"u8, "Relative pattern root");
        foreach (bool recursive in new[] { false, true })
        {
            var fs = new MemoryFileSystem([], true); fs.CreateDirectory("/project"u8);
            var backend = new Backend(); List<FileChange> events = [];
            using var watcher = new LspFileWatcher(fs, backend, changes => { lock (events) events.AddRange(changes); });
            watcher.Watch("id"u8, [new(recursive ? "/project/pkg/**/*"u8 : "/project/pkg/*"u8)]);
            Check(backend.Active.Single().Directory == "/project"u8 && !backend.Active.Single().Recursive, "Missing target tracks nearest ancestor non-recursively");
            Fails(() => watcher.Watch("id"u8, []), "Duplicate watcher identity rejected");
            Fails(() => watcher.Unwatch("unknown"u8), "Unknown identity rejected");
            backend.Emit("/project"u8, new([new("/project/unrelated.ts"u8, WatchEventKind.Update)])); watcher.Flush();
            Check(events.Count == 0, "Ancestor activity is not forwarded");
            fs.WriteFile("/project/pkg/index.ts"u8, []); fs.WriteFile("/project/pkg/nested/deep.ts"u8, []);
            backend.Emit("/project"u8, new([new("/project/pkg"u8, WatchEventKind.Update)])); watcher.Flush();
            Check(backend.Active.Single().Directory == "/project/pkg"u8 && backend.Active.Single().Recursive == recursive, "Promoted target preserves recursion");
            Check(events.Count == (recursive ? 4 : 3) && events.All(change => change.Kind == FileChangeKind.WatchCreate), "Synthetic create depth");
            events.Clear();
            backend.Emit("/project/pkg"u8, new([new("/project/pkg/index.ts"u8, WatchEventKind.Update)], WatchErrorKind.Overflow)); watcher.Flush();
            Check(events.Count == 1 && events[0].Kind == FileChangeKind.WatchChange, "Overflow retains real events without inventing invalidations");
            events.Clear();
            backend.Emit("/project/pkg"u8, new([new("/project/pkg/index.ts"u8, WatchEventKind.Delete)], WatchErrorKind.Terminated)); watcher.Flush();
            Check(events.Single(change => change.FileName == "/project/pkg/index.ts"u8).Kind == FileChangeKind.WatchDelete,
                "Immediate recreation reattaches without overwriting a pending delete");
            Check(backend.Active.Single().Directory == "/project/pkg"u8, "Terminated target already present gets a fresh subscription");
            foreach (var path in new Utf8String[] { "/project/pkg/nested/deep.ts"u8, "/project/pkg/nested"u8, "/project/pkg/index.ts"u8, "/project/pkg"u8 }) fs.Remove(path);
            backend.Emit("/project/pkg"u8, new([], WatchErrorKind.Terminated)); watcher.Flush();
            Check(backend.Active.Single().Directory == "/project"u8, "Deleted target falls back to ancestor");
            watcher.Unwatch("id"u8); Check(!backend.Active.Any(), "Unwatch closes all subscriptions");
            watcher.Watch("empty"u8, []); watcher.Dispose(); watcher.Dispose();
            Fails(() => watcher.Watch("closed"u8, []), "Closed watcher rejects registrations");
        }
        foreach (int kind in Enumerable.Range(0, 8))
        {
            var fs = new MemoryFileSystem([], true); fs.CreateDirectory("/project"u8);
            var backend = new Backend(); List<FileChange> events = [];
            using var watcher = new LspFileWatcher(fs, backend, events.AddRange);
            watcher.Watch("id"u8, [new("/project/pkg/**/*"u8, Kind: kind)]);
            fs.WriteFile("/project/pkg/index.ts"u8, []); backend.Emit("/project"u8, new([])); watcher.Flush();
            Check(events.Count == ((kind & 1) != 0 ? 2 : 0), "Synthetic create mask"); events.Clear();
            backend.Emit("/project/pkg"u8, new([new("/project/pkg/a.ts"u8, WatchEventKind.Update), new("/project/pkg/b.ts"u8, WatchEventKind.Delete)])); watcher.Flush();
            Check(events.Count == (((kind & 3) != 0 ? 1 : 0) + ((kind & 4) != 0 ? 1 : 0)), "Native event kind mask");
        }
        foreach (bool rootLink in new[] { false, true })
        {
            var fs = new MemoryFileSystem([], true); fs.CreateDirectory("/project"u8); fs.WriteFile("/external/file.ts"u8, []);
            var backend = new Backend(); List<FileChange> events = [];
            using var watcher = new LspFileWatcher(fs, backend, events.AddRange);
            watcher.Watch("link"u8, [new("/project/pkg/**/*"u8)]);
            if (!rootLink) fs.WriteFile("/project/pkg/local.ts"u8, []);
            fs.CreateSymbolicLink(rootLink ? "/project/pkg"u8 : "/project/pkg/linked"u8, "/external"u8);
            backend.Emit("/project"u8, new([])); watcher.Flush();
            Check(events.Count == (rootLink ? 1 : 3), "Synthetic recursive creates report symlinks without traversing them");
        }
        {
            var fs = new MemoryFileSystem([], true); fs.CreateDirectory("/project"u8);
            var backend = new Backend();
            using var watcher = new LspFileWatcher(fs, backend, _ => { });
            watcher.Watch("id"u8, [new("/project/a/b/c/*"u8)]);
            foreach (var (parent, next) in new (Utf8String, Utf8String)[] { ((Utf8String)"/project"u8, (Utf8String)"/project/a"u8), ((Utf8String)"/project/a"u8, (Utf8String)"/project/a/b"u8), ((Utf8String)"/project/a/b"u8, (Utf8String)"/project/a/b/c"u8) })
            {
                fs.CreateDirectory(next); backend.Emit(parent, new([]));
                Check(backend.Active.Single().Directory == next, "Multi-level ancestor descent");
            }
        }
        {
            var fs = new MemoryFileSystem([], true); fs.CreateDirectory("/project"u8);
            var backend = new Backend { OnWatch = request => { if (request.Directory == "/project"u8) fs.WriteFile("/project/a/b/c/a.ts"u8, []); } };
            List<FileChange> events = [];
            using var watcher = new LspFileWatcher(fs, backend, events.AddRange);
            watcher.Watch("id"u8, [new("/project/a/b/c/**/*"u8)]); watcher.Flush();
            Check(backend.Active.Single().Directory == "/project/a/b/c"u8 && events.Count == 2, "Atomic creation while installing ancestor promotes in one pass");
        }
        {
            var fs = new MemoryFileSystem([], true); fs.CreateDirectory("/project/a"u8); fs.CreateDirectory("/project/b"u8);
            var backend = new Backend { OnWatch = request => { if (request.Directory == "/project/b"u8) throw new IOException("capacity"); } };
            using var watcher = new LspFileWatcher(fs, backend, _ => { });
            Fails(() => watcher.Watch("id"u8, [new("/project/a/*"u8), new("/project/b/*"u8)]), "Partial backend failure is reported");
            Check(!backend.Active.Any(), "Entire failed identity rolled back"); backend.OnWatch = null;
            watcher.Watch("id"u8, [new("/project/a/*"u8), new("/project/b/*"u8)]);
            Check(backend.Active.Count() == 2, "Registration can retry after backend failure");
        }
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var fs = new MemoryFileSystem([], true); fs.CreateDirectory("/project"u8);
            var backend = new Backend { OnWatch = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); } };
            using var watcher = new LspFileWatcher(fs, backend, _ => { });
            var install = Task.Run(() => { try { watcher.Watch("id"u8, [new("/project/*"u8)]); return false; } catch (ObjectDisposedException) { return true; } });
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "Install entered backend");
            watcher.Dispose(); release.Set();
            Check(await install.WaitAsync(TimeSpan.FromSeconds(10)), "Close during installation returns closed error");
            Check(!backend.Active.Any(), "Close during installation leaks no subscription");
        }
        List<(Utf8String Id, LspWatchPattern Pattern)> adds = []; List<Utf8String> removes = [];
        bool fail = false, stall = false;
        var registry = new LspWatchRegistry(async (id, pattern, cancellation) =>
        {
            adds.Add((id, pattern));
            if (stall) await Task.Delay(Timeout.Infinite, cancellation);
            if (fail && pattern.Pattern == "/b/*"u8) throw new IOException("registration rejected");
        }, (id, _) => { removes.Add(id); return ValueTask.CompletedTask; });
        ProjectWatchGroup a = new("a"u8, [new("/shared/*"u8)]), b = new("b"u8, [new("/shared/*"u8), new("/b/*"u8)]);
        await registry.UpdateAsync([a, b], default); var sharedId = adds[0].Id;
        Check(adds.Count == 2, "Shared globs register once");
        await registry.UpdateAsync([a, b], default); Check(adds.Count == 2, "Unchanged groups retain registrations");
        await registry.UpdateAsync([b], default); Check(removes.Count == 0, "Removing one group retains shared registration");
        await registry.UpdateAsync([], default); Check(removes.Count == 2 && removes.Contains(sharedId), "Last reference unregisters original identity");
        adds.Clear(); removes.Clear(); await registry.UpdateAsync([a], default);
        fail = true; await registry.UpdateAsync([a, b], default);
        var retryId = adds[^1].Id; fail = false; await registry.UpdateAsync([a, b], default);
        Check(adds[^1].Id == retryId, "Retry retains pending identity");
        await registry.UpdateAsync([], default); Check(removes.Count == 2, "Failed shared acquisition does not leak references");
        adds.Clear(); removes.Clear(); fail = true;
        await registry.UpdateAsync([b], default);
        Check(removes.Count == 1 && removes[0] == adds[0].Id, "Partial successful registration is closed on failure");
        fail = false; await registry.UpdateAsync([b], default); await registry.UpdateAsync([], default);
        Check(removes.Count == 3, "Retry removes all final registrations");
        adds.Clear(); removes.Clear(); stall = true; await registry.UpdateAsync([a], default);
        Check(adds.Count == 1, "Unresponsive client times out"); stall = false;
        await registry.UpdateAsync([a], default); Check(adds.Count == 2 && adds[0].Id == adds[1].Id, "Timeout permits later retry");
        await registry.UpdateAsync([], default);
        return checks;
    }

    internal static async Task<int> NativeAsync(string directory)
    {
        directory = Path.GetFullPath(Path.Combine(directory, Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var changes = Channel.CreateUnbounded<FileChange>();
        await using var backend = new NativeWatchBackend();
        using var watcher = new LspFileWatcher(new PhysicalFileSystem(), backend, events => { foreach (var change in events) changes.Writer.TryWrite(change); });
        var target = CompilerPath.NormalizeSlashes(Utf8String.FromString(Path.Combine(directory, "pkg")));
        watcher.Watch("native"u8, [new(target + "/**/*"u8)]);
        Directory.CreateDirectory(target.ToString());
        var file = target + "/a.ts"u8;
        await File.WriteAllTextAsync(file.ToString(), "export const a = 1;", timeout.Token);
        await Expect(file, change => change.Kind is FileChangeKind.WatchCreate or FileChangeKind.WatchChange);
        await File.WriteAllTextAsync(file.ToString(), "export const a = 2;", timeout.Token);
        await Expect(file, change => change.Kind == FileChangeKind.WatchChange);
        File.Delete(file.ToString()); await Expect(file, change => change.Kind == FileChangeKind.WatchDelete);
        Directory.Delete(target.ToString()); await Task.Delay(300, timeout.Token);
        Directory.CreateDirectory(target.ToString());
        await File.WriteAllTextAsync(file.ToString(), "export const a = 3;", timeout.Token);
        await Expect(file, change => change.Kind is FileChangeKind.WatchCreate or FileChangeKind.WatchChange);
        return 4;
        async Task Expect(Utf8String path, Func<FileChange, bool> condition)
        {
            await foreach (var change in changes.Reader.ReadAllAsync(timeout.Token)) if (change.FileName == path && condition(change)) return;
        }
    }

    private sealed class Backend : IWatchBackend
    {
        private sealed class Subscription(WatchDirectoryRequest request) : IDisposable
        { internal readonly WatchDirectoryRequest Request = request; internal bool Closed; public void Dispose() => Closed = true; }
        private readonly List<Subscription> subscriptions = [];
        internal Action<WatchDirectoryRequest>? OnWatch;
        internal IEnumerable<WatchDirectoryRequest> Active => subscriptions.Where(item => !item.Closed).Select(item => item.Request);
        public IReadOnlyList<IDisposable> WatchDirectories(IReadOnlyList<WatchDirectoryRequest> requests)
        {
            var result = new List<IDisposable>();
            foreach (var request in requests) { OnWatch?.Invoke(request); var subscription = new Subscription(request); subscriptions.Add(subscription); result.Add(subscription); }
            return result;
        }
        internal void Emit(Utf8String directory, WatchNotification notification)
        { foreach (var request in Active.Where(request => request.Directory == directory).ToArray()) request.Callback(notification); }
    }
}

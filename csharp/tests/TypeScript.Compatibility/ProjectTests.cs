using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static partial class ProjectTests
{
    internal static async Task<int> SafetyAsync()
    {
        int assertions = 0;
        void Check(bool value, string reason) { assertions++; if (!value) throw new InvalidOperationException(reason); }
        var document = new DocumentSnapshot("/p/main.ts"u8, "a😀b\r\nsecond\u2028line\rfinal"u8, 1, isOverlay: true, matchesDiskText: false);
        Check(document.LineMap.LineCount == 3, "LSP does not treat an ECMAScript paragraph separator as a newline");
        Check(document.LineMap.ToOffset(new(0, 3), PositionEncoding.Utf16) == 5, "UTF-16 astral coordinate");
        Check(document.LineMap.ToOffset(new(0, 2), PositionEncoding.Utf16) == 1, "A coordinate inside a surrogate pair rounds down");
        Check(document.LineMap.ToOffset(new(0, 5), PositionEncoding.Utf8) == 5, "UTF-8 byte coordinate");
        Check(document.LineMap.ToOffset(new(0, uint.MaxValue), PositionEncoding.Utf16) == 8, "Columns clamp at the following line start");
        Check(document.LineMap.ToOffset(new(uint.MaxValue, 0), PositionEncoding.Utf8) == document.Text.Length, "Lines clamp at EOF");
        Check(document.LineMap.ToPosition(5, PositionEncoding.Utf16) == new DocumentPosition(0, 3), "Byte position to UTF-16");
        var changed = document.Apply([new("x"u8, new(new(0, 1), new(0, 3))), new("!"u8, new(new(0, 2), new(0, 2)))], 2, PositionEncoding.Utf16);
        Check(changed.Text == "ax!b\r\nsecond\u2028line\rfinal"u8 && document.Text.StartsWith("a😀b"u8), "Edits are sequential and previous snapshots remain immutable");
        Check(changed.Version == 2 && changed.Hash != document.Hash && !changed.MatchesDiskText, "An edit owns a new version and content hash");
        Check(ReferenceEquals(changed.Apply([], 3, PositionEncoding.Utf8), changed), "An empty edit batch does not advance the version");
        Check(changed.WithDiskMatch(true).MatchesDiskText && !changed.MatchesDiskText, "Disk state changes do not mutate retained overlays");
        foreach (var (uri, path) in new (Utf8String, Utf8String)[]
        {
            ((Utf8String)"file:///path/to/file.ts"u8, (Utf8String)"/path/to/file.ts"u8),
            ((Utf8String)"file://server/share/file.ts"u8, (Utf8String)"//server/share/file.ts"u8),
            ((Utf8String)"file:///D%3A/work/a%20b%23.ts"u8, (Utf8String)"d:/work/a b#.ts"u8),
            ((Utf8String)"file:///path/file.ts#section"u8, (Utf8String)"/path/file.ts"u8),
            ((Utf8String)"untitled:Untitled-1#fragment"u8, (Utf8String)"^/untitled/ts-nul-authority/Untitled-1#fragment"u8),
            ((Utf8String)"untitled://wsl%2Bubuntu/home/test.ts"u8, (Utf8String)"^/untitled/wsl%2Bubuntu/home/test.ts"u8),
            ((Utf8String)"bundled:///libs/lib.d.ts"u8, (Utf8String)"bundled:///libs/lib.d.ts"u8)
        }) Check(DocumentUris.ToFileName(uri) == path, $"Document URI {uri}");
        Check(DocumentUris.FromFileName("d:/work/a b#.ts"u8) == "file:///d%3A/work/a%20b%23.ts"u8, "File URI escaping");
        Utf8String unpaired = Utf8String.FromString("/bad-\ud800.ts");
        Check(DocumentUris.ToFileName(DocumentUris.FromFileName(unpaired)) == unpaired, "File URI retains WTF-8 bytes");
        Check(DocumentUris.FromFileName("^/untitled/wsl%2Bubuntu/home/test.ts"u8) == "untitled://wsl%2Bubuntu/home/test.ts"u8, "Dynamic URI retains its escaped authority");
        foreach (bool sensitive in new[] { false, true })
        {
            var disk = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
            {
                ["/src/f.ts"u8] = "disk"u8.ToArray(), ["/masked"u8] = "file"u8.ToArray()
            }, sensitive);
            var original = new OverlayFileSystem(disk, "/"u8);
            var opened = original.Apply([new(FileChangeKind.Open, "/src/f.ts"u8, 1, "a😀b"u8)]);
            Check(opened.Summary.Opened == "/src/f.ts"u8 && original.GetDocument("/src/f.ts"u8)!.Text == "disk"u8,
                "Opening an overlay does not mutate its predecessor");
            var saved = opened.FileSystem.Apply([new(FileChangeKind.Save, "/src/f.ts"u8)]);
            Check(saved.Summary.IsEmpty && saved.FileSystem.GetDocument("/src/f.ts"u8)!.MatchesDiskText, "Save only updates disk-match metadata");
            var edited = saved.FileSystem.Apply([new(FileChangeKind.Change, "/src/f.ts"u8, 2,
                Edits: [new("X"u8, new(new(0, 1), new(0, 3)))])], PositionEncoding.Utf16);
            Check(edited.Summary.Changed.Contains("/src/f.ts"u8) && edited.FileSystem.GetDocument("/src/f.ts"u8)!.Text == "aXb"u8,
                "Editor text edits use the negotiated coordinate encoding");
            Check(original.Apply([new(FileChangeKind.WatchCreate, "/new.ts"u8), new(FileChangeKind.WatchDelete, "/new.ts"u8)]).Summary.IsEmpty,
                "Transient files do not invalidate projects");
            Check(original.Apply([new(FileChangeKind.WatchDelete, "/new.ts"u8), new(FileChangeKind.WatchCreate, "/new.ts"u8)])
                .Summary.Changed.Contains("/new.ts"u8), "Atomic replacement is a change");
            var virtualDirectory = opened.FileSystem.Apply([new(FileChangeKind.Open, "/masked/child.ts"u8, 1, "virtual"u8)]).FileSystem;
            Check(virtualDirectory.DirectoryExists("/masked"u8) && !virtualDirectory.FileExists("/masked"u8)
                && virtualDirectory.GetAccessibleEntries("/masked"u8).Files.SequenceEqual(new Utf8String[] { "child.ts"u8 }),
                "An overlay directory masks a host file");
            byte[] owned = virtualDirectory.ReadFile("/masked/child.ts"u8)!;
            owned[0] = (byte)'X';
            Check(virtualDirectory.GetDocument("/masked/child.ts"u8)!.Text == "virtual"u8, "ReadFile returns owned storage");
            var snapshot = new SnapshotFileSystem(original, "/"u8);
            var before = snapshot.GetDocument("/src/f.ts"u8);
            disk.WriteFile("/src/f.ts"u8, "changed"u8);
            Check(ReferenceEquals(before, snapshot.GetDocument("/src/f.ts"u8)) && before!.Text == "disk"u8,
                "Reads in an old snapshot remain stable across host writes");
            var invalidation = original.Apply([new(FileChangeKind.WatchChange, "/src/f.ts"u8)]);
            var successor = new SnapshotFileSystem(invalidation.FileSystem, "/"u8, snapshot, invalidation.Summary);
            Check(successor.GetDocument("/src/f.ts"u8)!.Text == "changed"u8, "Invalidation refreshes a changed file in its successor");
            var unchanged = new SnapshotFileSystem(original, "/"u8, snapshot);
            Check(ReferenceEquals(before, unchanged.GetDocument("/src/f.ts"u8)), "Unchanged file handles are shared between snapshots");
        }

        var files = new Dictionary<Utf8String, byte[]> { ["/p/main.ts"u8] = "const x = 1; const y = 2;"u8.ToArray() };
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/p"u8,
            new("/p/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        var source = program.SourceFiles[0].Syntax;
        var time = new ManualTime();
        using var pool = new ProjectCheckerPool(program, 3, TimeSpan.FromMinutes(1), time);
        using var request = new ProjectRequest();
        Checker query;
        using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Query, request, source))
        {
            query = lease.Checker;
            using var nested = await pool.AcquireAsync(ProjectCheckerLifetime.Query, request, source);
            Check(ReferenceEquals(query, nested.Checker), "Nested acquisitions within one request reuse its checker");
        }
        using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Query, request, source))
            Check(ReferenceEquals(query, lease.Checker), "Request affinity survives a release");
        using var anotherRequest = new ProjectRequest();
        using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Query, anotherRequest, source))
            Check(ReferenceEquals(query, lease.Checker), "A new request preserves file affinity");
        Checker persistent;
        using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Api, request))
        {
            persistent = lease.Checker;
            Check(!ReferenceEquals(query, persistent), "API identity has a dedicated checker");
        }
        using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Diagnostics, request))
            Check(!ReferenceEquals(query, lease.Checker) && !ReferenceEquals(persistent, lease.Checker), "Diagnostics have a dedicated checker");
        time.Advance(TimeSpan.FromMinutes(2));
        pool.ExpireIdle();
        using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Query, anotherRequest, source))
        {
            Check(!ReferenceEquals(query, lease.Checker), "Idle editor checkers expire");
            query = lease.Checker;
        }
        using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Api, request))
            Check(ReferenceEquals(persistent, lease.Checker), "Idle cleanup preserves API identity");
        pool.Discard();
        time.Advance(TimeSpan.FromMinutes(2));
        pool.ExpireIdle();
        using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Query, anotherRequest, source))
            Check(ReferenceEquals(query, lease.Checker), "A retained old program remains usable after replacement");

        using var cancel = new CancellationTokenSource();
        using var canceledRequest = new ProjectRequest(cancel.Token);
        using (var held = await pool.AcquireAsync(ProjectCheckerLifetime.Api, request))
        {
            Task<ProjectCheckerPool.Lease> waiting = pool.AcquireAsync(ProjectCheckerLifetime.Api, canceledRequest).AsTask();
            Check(!waiting.IsCompleted, "API checkers have exclusive ownership");
            cancel.Cancel();
            try { using var unexpected = await waiting; Check(false, "Waiting cancellation must throw"); }
            catch (OperationCanceledException) { assertions++; }
        }
        using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Api, request))
            Check(ReferenceEquals(persistent, lease.Checker), "Canceling a waiter does not discard the active API checker");
        using var lateCancel = new CancellationTokenSource();
        using var lateRequest = new ProjectRequest(lateCancel.Token);
        TypeScript.Compiler.Checking.Type completedType;
        var literal = source.DescendantsAndSelf().OfType<NumericLiteralNode>().First();
        using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Api, lateRequest))
        {
            completedType = await lease.Checker.GetTypeAtLocationAsync(literal, lateCancel.Token);
            lateCancel.Cancel();
        }
        using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Api, request))
        {
            Check(ReferenceEquals(persistent, lease.Checker), "Cancellation after a completed query preserves the API checker");
            Check(ReferenceEquals(completedType, await lease.Checker.GetTypeAtLocationAsync(literal)),
                "A completed query retains its type identity after late cancellation");
        }
        using var runningCancel = new CancellationTokenSource();
        using var runningRequest = new ProjectRequest(runningCancel.Token);
        Checker poisoned;
        using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Diagnostics, runningRequest))
        {
            poisoned = lease.Checker;
            poisoned.BeforeSourceElement = _ => runningCancel.Cancel();
            try { await poisoned.CheckSourceFileAsync(source, runningCancel.Token); Check(false, "Expected source-check cancellation"); }
            catch (OperationCanceledException) { assertions++; }
        }
        using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Diagnostics, request))
        {
            Check(!ReferenceEquals(poisoned, lease.Checker), "A canceled checker is replaced before reuse");
            await lease.Checker.CheckSourceFileAsync(source);
        }
        using var waitRequest = new ProjectRequest();
        var active = await pool.AcquireAsync(ProjectCheckerLifetime.Api, request);
        var blocked = pool.AcquireAsync(ProjectCheckerLifetime.Api, waitRequest).AsTask();
        pool.Dispose();
        try { using var unexpected = await blocked; Check(false, "Pool disposal must wake blocked acquisitions"); }
        catch (OperationCanceledException) { assertions++; }
        active.Dispose(); active.Dispose();
        request.Dispose(); request.Dispose();
        assertions += await CheckerPoolContractsAsync(program);
        assertions += await FileSystemContractsAsync();
        assertions += await SnapshotsAsync();
        assertions += await SessionRacesAsync();
        return assertions;
    }

    private static async Task<int> SessionRacesAsync()
    {
        int assertions = 0;
        void Check(bool value, string reason) { assertions++; if (!value) throw new InvalidOperationException(reason); }
        var cache = new ProjectParseCache();
        var js = await cache.ParseAsync(new("/index.js"u8), new SourceText("module.exports = 0;"u8), default);
        Check(TypeScript.Compiler.Binding.Binder.IsBound(js), "The shared parse cache binds before publishing syntax");
        Check(TypeScript.Compiler.Binding.Binder.Bind(js).CommonJSModuleIndicator is not null, "Published JavaScript syntax has its CommonJS binding");
        var memory = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/p/tsconfig.json"u8] = "{\"compilerOptions\":{\"noLib\":true},\"files\":[\"a.ts\"]}"u8.ToArray(),
            ["/p/a.ts"u8] = "const a = 0;"u8.ToArray()
        });
        var fs = new CallbackFileSystem(memory);
        await using var session = new ProjectSession(fs);
        session.Notify(new(FileChangeKind.Open, "/p/a.ts"u8, 1, "const a = 1;"u8));
        int callbacks = 0;
        fs.BeforeRead = _ =>
        {
            if (Interlocked.Increment(ref callbacks) == 1)
                session.Notify(new(FileChangeKind.Change, "/p/a.ts"u8, 2, Edits: [new("const a = 2;"u8)]));
        };
        await using var latest = await session.GetSnapshotAsync(["/p/a.ts"u8]);
        Check(latest.Snapshot.FileSystem.GetDocument("/p/a.ts"u8)!.Version == 2
            && latest.Snapshot.GetDefaultProject("/p/a.ts"u8)!.Program!.GetFile("/p/a.ts"u8)!.Syntax.Source.Text == "const a = 2;"u8,
            "A notification during construction prevents stale snapshot adoption");
        using var cancellation = new CancellationTokenSource();
        fs.BeforeRead = _ => cancellation.Cancel();
        memory.WriteFile("/p/tsconfig.json"u8, "{\"compilerOptions\":{\"noLib\":true,\"strict\":true},\"files\":[\"a.ts\"]}"u8);
        session.Notify(new(FileChangeKind.WatchChange, "/p/tsconfig.json"u8));
        try { await using var invalid = await session.GetSnapshotAsync(["/p/a.ts"u8], cancellation.Token); Check(false, "Cancellation must abort publication"); }
        catch (OperationCanceledException) { assertions++; }
        fs.BeforeRead = null;
        await using var retried = await session.GetSnapshotAsync(["/p/a.ts"u8]);
        Check(retried.Snapshot.GetDefaultProject("/p/a.ts"u8)!.Configuration.Options.Strict == true,
            "A canceled update leaves its notifications available for retry");
        var queries = Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var lease = await session.GetSnapshotAsync(["/p/a.ts"u8]);
            return lease.Snapshot.GetDefaultProject("/p/a.ts"u8)!.Program;
        }).ToArray();
        var programs = await Task.WhenAll(queries).WaitAsync(TimeSpan.FromSeconds(30));
        Check(programs.All(program => ReferenceEquals(program, programs[0])), "Concurrent readers share an unchanged program and release their leases");
        var weak = await ReleasedProgramAsync();
        for (int attempt = 0; attempt < 3 && weak.IsAlive; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Yield();
        }
        Check(!weak.IsAlive, "Disposing the final snapshot releases the program and its checker timer");
        return assertions;
    }

    private static async Task<WeakReference> ReleasedProgramAsync()
    {
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = "const a = 1;"u8.ToArray() });
        await using var host = new ProjectSnapshotHost(fs);
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8);
        await using var snapshot = await host.CreateAsync(new() { CreatePrograms = [new(["/a.ts"u8], options)] });
        using var request = new ProjectRequest();
        using var checker = await snapshot.CreatedPrograms[0].Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request);
        return new(snapshot.CreatedPrograms[0].Program);
    }

    private sealed class CallbackFileSystem(IFileSystem inner) : IFileSystem
    {
        internal Action<Utf8String>? BeforeRead;
        public bool CaseSensitive => inner.CaseSensitive;
        public bool FileExists(Utf8String path) => inner.FileExists(path);
        public bool DirectoryExists(Utf8String path) => inner.DirectoryExists(path);
        public byte[]? ReadFile(Utf8String path) { BeforeRead?.Invoke(path); return inner.ReadFile(path); }
        public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents) => inner.WriteFile(path, contents);
        public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents) => inner.AppendFile(path, contents);
        public void Remove(Utf8String path) => inner.Remove(path);
        public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc) => inner.SetTimes(path, accessTimeUtc, writeTimeUtc);
        public DirectoryEntries GetAccessibleEntries(Utf8String path) => inner.GetAccessibleEntries(path);
        public FileEntry? Stat(Utf8String path) => inner.Stat(path);
        public Utf8String RealPath(Utf8String path) => inner.RealPath(path);
    }

    private static async Task<int> SnapshotsAsync()
    {
        int assertions = 0;
        void Check(bool value, string reason) { assertions++; if (!value) throw new InvalidOperationException(reason); }
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/p/tsconfig.json"u8] = "{\"compilerOptions\":{\"noLib\":true},\"files\":[\"a.ts\"]}"u8.ToArray(),
            ["/p/a.ts"u8] = "export const a = 1;"u8.ToArray(),
            ["/q/b.ts"u8] = "export const b = 1;"u8.ToArray(),
        };
        var fs = new MemoryFileSystem(files);
        await using var host = new ProjectSnapshotHost(fs);
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8);
        await using var initial = await host.CreateAsync(new()
        {
            OpenProjects = ["/p/tsconfig.json"u8],
            CreatePrograms = [new(["/q/b.ts"u8], options)]
        });
        Check(initial.Projects.Count == 2 && initial.CreatedPrograms.Count == 1, "Configured and synthetic projects coexist");
        var configured = initial.GetProject("/p/tsconfig.json"u8)!;
        var synthetic = initial.CreatedPrograms[0];
        await using var unchanged = await host.CreateAsync(new() { FileChanges = [new(FileChangeKind.WatchChange, "/p/a.ts"u8)], EnsureAllPrograms = true }, initial);
        Check(ReferenceEquals(unchanged.GetProject(configured.Id)!.Program, configured.Program), "Unchanged disk notifications preserve the program identity");
        Check(synthetic.Id == "/dev/null/synthetic/1"u8 && synthetic.Program!.GetFile("/q/b.ts"u8) is not null, "Synthetic programs own their root graph");
        foreach (Utf8String alias in new Utf8String[] { "/dev/null/synthetic/01"u8, "/dev/null/synthetic/+1"u8, "/dev/null/synthetic/0001"u8 })
            Check(ReferenceEquals(synthetic, initial.GetProject(alias)), "Synthetic project aliases resolve to the canonical identity");
        foreach (Utf8String invalid in new Utf8String[] { "/dev/null/synthetic/0"u8, "/dev/null/synthetic/-1"u8,
            "/dev/null/synthetic/ 1"u8, "/dev/null/synthetic/1 "u8, "/dev/null/synthetic/9223372036854775808"u8, "/dev/null/synthetic/invalid"u8 })
            Check(ProjectIds.Canonicalize(invalid) == invalid, "Invalid numeric suffixes are not synthetic project aliases");
        Check(initial.GetDefaultProject("/q/b.ts"u8) is null, "Synthetic programs do not participate in editor project selection");
        await using var shared = await host.CreateAsync(new() { CreatePrograms = [new(["/q/b.ts"u8], options)] }, initial);
        Check(ReferenceEquals(shared.CreatedPrograms[0].Program!.GetFile("/q/b.ts"u8)!.Syntax, synthetic.Program!.GetFile("/q/b.ts"u8)!.Syntax),
            "Unchanged sources share parse identity across independent programs");
        fs.WriteFile("/p/a.ts"u8, "export const a = 2;"u8);
        await using var dirty = await host.CreateAsync(new() { FileChanges = [new(FileChangeKind.WatchChange, "/p/a.ts"u8)] }, initial);
        Check(dirty.GetProject(configured.Id)!.IsDirty && ReferenceEquals(dirty.GetProject(configured.Id)!.Program, configured.Program),
            "File notifications mark API projects dirty without advancing their programs");
        await using var ensured = await host.CreateAsync(new() { EnsurePrograms = [configured.Id] }, dirty);
        var updated = ensured.GetProject(configured.Id)!;
        Check(!updated.IsDirty && !ReferenceEquals(updated.Program, configured.Program)
            && updated.Program!.GetFile("/p/a.ts"u8)!.Syntax.Source.Text == "export const a = 2;"u8, "Ensuring refreshes the requested project");
        Check(configured.Program!.GetFile("/p/a.ts"u8)!.Syntax.Source.Text == "export const a = 1;"u8, "Retained snapshots preserve their old source graph");
        Check(ReferenceEquals(ensured.GetProject(synthetic.Id)!.Program, synthetic.Program), "Ensuring one project preserves unrelated programs");
        await using var branch = await host.CreateAsync(new() { ReconfigurePrograms = [new(synthetic.Id, new(["/p/a.ts"u8], options))] }, initial);
        Check(branch.GetProject(synthetic.Id)!.Program!.GetFile("/p/a.ts"u8) is not null && synthetic.Program!.GetFile("/q/b.ts"u8) is not null,
            "API branches reconfigure independently");
        try
        {
            await using var invalid = await host.CreateAsync(new() { ReconfigurePrograms = [new(synthetic.Id, new([], options))], RemovePrograms = [synthetic.Id] }, initial);
            Check(false, "A conflicting update must fail atomically");
        }
        catch (ArgumentException) { assertions++; }
        Check(initial.GetProject(synthetic.Id)!.Program!.GetFile("/q/b.ts"u8) is not null, "Rejected updates preserve the parent");
        await using var replaced = await host.CreateAsync(new() { RemovePrograms = [synthetic.Id], CreatePrograms = [new(["/q/b.ts"u8], options)] }, initial);
        Check(replaced.CreatedPrograms[0].Id == synthetic.Id && !ReferenceEquals(replaced.CreatedPrograms[0].Program, synthetic.Program),
            "Project names may be reused while program identity remains distinct");
        await using var twice = await host.CreateAsync(new() { OpenProjects = [configured.Id] }, initial);
        await using var closedOnce = await host.CreateAsync(new() { CloseProjects = [configured.Id] }, twice);
        Check(closedOnce.GetProject(configured.Id) is not null, "Configured project opens are counted");
        await using var closedTwice = await host.CreateAsync(new() { CloseProjects = [configured.Id] }, closedOnce);
        Check(closedTwice.GetProject(configured.Id) is null, "The last configured close releases an unneeded project");
        await using var opened = await host.CreateAsync(new() { OpenFiles = ["/p/a.ts"u8, "/q/b.ts"u8], InferredOptions = options });
        Check(opened.GetDefaultProject("/p/a.ts"u8)?.Kind == ProjectKind.Configured, "Opened files discover their closest configuration");
        Check(opened.GetDefaultProject("/q/b.ts"u8)?.Kind == ProjectKind.Inferred, "Loose files form an inferred project");
        await using var closed = await host.CreateAsync(new() { CloseFiles = ["/p/a.ts"u8, "/q/b.ts"u8] }, opened);
        Check(closed.Projects.Count == 0, "Closing API files cleans unused configured and inferred projects");
        Check(!closed.FileSystem.TryGetCachedDocument("/p/a.ts"u8, out _), "Closing the last project drops unrelated disk cache entries");
        await using var session = new ProjectSession(fs);
        var editorOptions = new CompilerOptions(); editorOptions.Merge(options); editorOptions.SetRaw("allowNonTsExtensions"u8, "true"u8);
        session.SetInferredOptions(editorOptions);
        session.Notify(new(FileChangeKind.Open, "/p/a.ts"u8, 1, "export const a = '😀';"u8));
        await using var editor = await session.GetSnapshotAsync(["/p/a.ts"u8]);
        Check(editor.Snapshot.GetDefaultProject("/p/a.ts"u8)!.Program!.GetFile("/p/a.ts"u8)!.Syntax.Source.Text == "export const a = '😀';"u8,
            "Editor programs compile the current overlay");
        session.Notify(new(FileChangeKind.Change, "/p/a.ts"u8, 2, Edits: [new("export const a = 3;"u8)]));
        await using var edited = await session.GetSnapshotAsync(["/p/a.ts"u8]);
        Check(edited.Snapshot.GetDefaultProject("/p/a.ts"u8)!.Program!.GetFile("/p/a.ts"u8)!.Syntax.Source.Text == "export const a = 3;"u8
            && editor.Snapshot.FileSystem.GetDocument("/p/a.ts"u8)!.Text == "export const a = '😀';"u8, "Editor leases retain their source version");
        session.Notify(new(FileChangeKind.Open, "^/untitled/ts-nul-authority/Untitled-1"u8, 1, "const fresh = 1;"u8, TypeScript.Compiler.Syntax.ScriptKind.TS));
        await using var untitled = await session.GetSnapshotAsync(["^/untitled/ts-nul-authority/Untitled-1"u8]);
        Check(untitled.Snapshot.GetDefaultProject("^/untitled/ts-nul-authority/Untitled-1"u8)?.Program?.SourceFiles.Count == 1,
            "Dynamic editor documents remain rooted at their virtual filename");
        session.Notify(new(FileChangeKind.Open, "^/untitled/ts-nul-authority/Untitled-2"u8, 1, "const element = <div/>;"u8, TypeScript.Compiler.Syntax.ScriptKind.TSX));
        await using var jsx = await session.GetSnapshotAsync(["^/untitled/ts-nul-authority/Untitled-2"u8]);
        Check(jsx.Snapshot.GetDefaultProject("^/untitled/ts-nul-authority/Untitled-2"u8)!.Program!.GetFile("^/untitled/ts-nul-authority/Untitled-2"u8)!
            .Syntax.ScriptKind == TypeScript.Compiler.Syntax.ScriptKind.TSX, "Extensionless overlays retain the editor's language selection");
        await session.DisposeAsync();
        Check(edited.Snapshot.GetDefaultProject("/p/a.ts"u8)!.Program!.GetFile("/p/a.ts"u8) is not null, "A request lease survives session disposal");
        await using var retained = initial.Acquire();
        await initial.DisposeAsync();
        using var query = new ProjectRequest();
        using var checker = await retained.Snapshot.GetProject(synthetic.Id)!.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Api, query);
        Check(checker.Checker is not null, "A retained snapshot keeps API checker ownership alive");
        return assertions;
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan duration) => now += duration;
    }
}

using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static partial class ProjectTests
{
    private static async Task<int> FileSystemContractsAsync()
    {
        int checks = 0;
        void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
        foreach (bool sensitive in new[] { false, true })
        {
            var disk = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
            {
                ["/src/a.ts"u8] = "disk a"u8.ToArray(), ["/src/deep/b.ts"u8] = "disk b"u8.ToArray(),
                ["/other/c.ts"u8] = "disk c"u8.ToArray(), ["/mask"u8] = "file"u8.ToArray(),
            }, sensitive);
            int reads = 0;
            var counted = new CallbackFileSystem(disk) { BeforeRead = _ => Interlocked.Increment(ref reads) };
            var overlay = new OverlayFileSystem(counted, "/"u8, [new("/src/a.ts"u8, "overlay a"u8, 1, isOverlay: true),
                new("/virtual/deep/extra.ts"u8, "extra"u8, 1, isOverlay: true), new("/mask/child.ts"u8, "child"u8, 1, isOverlay: true)]);
            var snapshot = new SnapshotFileSystem(overlay, "/"u8);
            var parallel = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => snapshot.GetDocument("/src/deep/b.ts"u8))));
            Check(reads == 1 && parallel.All(file => ReferenceEquals(file, parallel[0])), "Concurrent reads publish one source handle");
            Check(snapshot.GetDocument("/src/a.ts"u8)!.Text == "overlay a"u8 && reads == 1, "Overlays bypass disk reads");
            Check(snapshot.GetDocument("/missing.ts"u8) is null && snapshot.GetDocument("/missing.ts"u8) is null && reads == 2,
                "Missing reads are cached within a snapshot");
            Check(snapshot.DirectoryExists("/virtual/deep"u8) && snapshot.DirectoryExists("/mask"u8) && !snapshot.FileExists("/mask"u8),
                "Overlay ancestors exist and mask conflicting disk files");
            var entries = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => snapshot.GetAccessibleEntries("/src"u8))));
            Check(entries.All(entry => entry.Files.SequenceEqual(new Utf8String[] { "a.ts"u8 })
                && entry.Directories.SequenceEqual(new Utf8String[] { "deep"u8 })), "Concurrent directory reads preserve file and directory entries");
            entries[0].Files[0] = "corrupted"u8;
            Check(snapshot.GetAccessibleEntries("/src"u8).Files[0] == "a.ts"u8, "Directory callers own their result arrays");
            Check(snapshot.ReadFile("/src/a.ts"u8)!.AsSpan().SequenceEqual("overlay a"u8) && !snapshot.FileExists("/absent.ts"u8),
                "Source reads and existence checks share the overlay view");
            using (var observation = snapshot.ObserveFiles())
            {
                _ = snapshot.GetDocument("/src/a.ts"u8);
                using (var nested = snapshot.ObserveFiles())
                {
                    _ = snapshot.GetDocument("/other/c.ts"u8);
                    Check(nested.Files.ContainsKey("/other/c.ts"u8) && !observation.Files.ContainsKey("/other/c.ts"u8), "Nested observations have independent read sets");
                }
                _ = snapshot.FileExists("/src/deep/b.ts"u8);
                Check(observation.Files.Keys.Order().SequenceEqual(new Utf8String[] { "/src/a.ts"u8, "/src/deep/b.ts"u8 }), "Disposing nested observation restores its parent");
            }
            var old = snapshot.GetDocument("/src/deep/b.ts"u8);
            disk.Remove("/src/deep/b.ts"u8);
            disk.WriteFile("/src/d.ts"u8, "new d"u8);
            var changes = new FileChangeSummary(sensitive);
            changes.Deleted.Add("/src/deep"u8); changes.Created.Add("/src/d.ts"u8);
            var successor = new SnapshotFileSystem(overlay, "/"u8, snapshot, changes);
            Check(successor.GetDocument("/src/deep/b.ts"u8) is null && ReferenceEquals(old, snapshot.GetDocument("/src/deep/b.ts"u8)),
                "Directory deletion invalidates descendants without changing retained snapshots");
            Check(successor.GetAccessibleEntries("/src"u8).Files.Order().SequenceEqual(new Utf8String[] { "a.ts"u8, "d.ts"u8 }),
                "A successor merges created files and prunes deleted descendants");
            Check(successor.GetAccessibleEntries("/virtual"u8).Directories.SequenceEqual(new Utf8String[] { "deep"u8 }), "Nested overlay directory trees survive snapshot changes");
            successor.RetainFiles(["/src/a.ts"u8]);
            Check(!successor.TryGetCachedDocument("/other/c.ts"u8, out _) && snapshot.TryGetCachedDocument("/other/c.ts"u8, out _),
                "Trimming one cache does not mutate its predecessor");

            var linkedDisk = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/real/pkg/index.d.ts"u8] = "export const x: number;"u8.ToArray() }, sensitive, "/"u8,
                new Dictionary<Utf8String, Utf8String> { ["/p/node_modules/pkg"u8] = "/real/pkg"u8, ["/q/node_modules/pkg"u8] = "/real/pkg"u8 });
            var linkedOverlay = new OverlayFileSystem(linkedDisk, "/"u8);
            var linked = new SnapshotFileSystem(linkedOverlay, "/"u8);
            Utf8String first = "/p/node_modules/pkg/index.d.ts"u8, second = "/q/node_modules/pkg/index.d.ts"u8, real = "/real/pkg/index.d.ts"u8;
            var firstDocument = linked.GetDocument(first);
            var secondDocument = linked.GetDocument(second);
            var inherited = new SnapshotFileSystem(linkedOverlay, "/"u8, linked);
            Check(ReferenceEquals(firstDocument, inherited.GetDocument(first)) && ReferenceEquals(secondDocument, inherited.GetDocument(second)), "Unchanged alias handles are inherited");
            linkedDisk.WriteFile(real, "export const changed: string;"u8);
            var realChange = new FileChangeSummary(sensitive); realChange.Changed.Add(real);
            var changed = new SnapshotFileSystem(linkedOverlay, "/"u8, inherited, realChange);
            Check(changed.GetDocument(first)!.Text == "export const changed: string;"u8 && changed.GetDocument(second)!.Text == "export const changed: string;"u8,
                "A real-path notification invalidates every known package alias");
            Check(changed.RealPath(first) == real && changed.RealPath(second) == real, "Multiple package aliases resolve to one real path");
            Check(inherited.GetDocument(first)!.Text == "export const x: number;"u8, "Alias invalidation leaves previous snapshots intact");
            linkedDisk.Remove("/p/node_modules/pkg"u8);
            var unlink = new FileChangeSummary(sensitive); unlink.Deleted.Add("/p/node_modules/pkg"u8);
            var unlinked = new SnapshotFileSystem(linkedOverlay, "/"u8, changed, unlink);
            Check(unlinked.GetDocument(first) is null && unlinked.GetDocument(second) is not null, "Deleting one alias preserves other aliases");
            var discovery = unlinked.ForAutoImports();
            Check(discovery.ReadFile(real) is not null && !unlinked.TryGetCachedDocument(real, out _), "Auto-import discovery does not retain untracked source handles");
            linkedDisk.Remove(real);
            Check(discovery.ReadFile(real) is null, "Auto-import reads tolerate a file disappearing after discovery");
        }

        var cache = new ProjectParseCache();
        var options = new ParseOptions("/shared.ts"u8);
        var source = new SourceText("export const value = 1;"u8);
        var shared = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => cache.ParseAsync(options, source, default).AsTask()));
        Check(shared.All(file => ReferenceEquals(file, shared[0])) && TypeScript.Compiler.Binding.Binder.IsBound(shared[0]),
            "Concurrent parse requests share an already bound tree");
        var weak = await ParseWithoutOwnerAsync(cache);
        for (int attempt = 0; attempt < 5 && weak.IsAlive; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        cache.Trim();
        Check(!weak.IsAlive, "The shared parse cache does not retain a retired source graph");
        return checks;
    }

    private static async Task<WeakReference> ParseWithoutOwnerAsync(ProjectParseCache cache) =>
        new(await cache.ParseAsync(new("/retired.ts"u8), new("export const retired = true;"u8), default));
}

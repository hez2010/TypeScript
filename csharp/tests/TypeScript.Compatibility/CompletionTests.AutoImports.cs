using System.Runtime.CompilerServices;
using TypeScript.Compiler;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;

namespace TypeScript.Compatibility;

internal static partial class CompletionTests
{
    private static async Task<int> AutoImportSafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/p/tsconfig.json"u8] = "{\"compilerOptions\":{\"noLib\":true,\"types\":[]},\"files\":[\"main.ts\"]}"u8.ToArray(),
            ["/p/package.json"u8] = "{\"dependencies\":{\"pkg\":\"*\"}}"u8.ToArray(),
            ["/p/main.ts"u8] = "pkg"u8.ToArray(),
            ["/p/node_modules/pkg/package.json"u8] = "{\"types\":\"index.d.ts\"}"u8.ToArray(),
            ["/p/node_modules/pkg/index.d.ts"u8] = "export declare const pkgOne: number;"u8.ToArray(),
            ["/p/node_modules/pkg/private.d.ts"u8] = "export declare const pkgPrivate: number;"u8.ToArray(),
            ["/p/node_modules/unlisted/index.d.ts"u8] = "export declare const pkgUnlisted: number;"u8.ToArray(),
        }, true);
        await using var session = new ProjectSession(fs, new() { CurrentDirectory = "/p"u8, MaxCheckers = 1 });
        session.Notify(new(FileChangeKind.Open, "/p/main.ts"u8, 1, "pkg"u8));
        await using var first = await session.GetSnapshotAsync(["/p/main.ts"u8], deadline.Token);
        var oldProject = first.Snapshot.GetDefaultProject("/p/main.ts"u8)!;
        var firstItems = await Query(oldProject);
        Check(firstItems.Select(item => item.Label).SequenceEqual(new Utf8String[] { "pkgOne"u8 }), "Package discovery includes declared dependencies and their entry points");
        var repeated = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Query(oldProject)));
        Check(repeated.All(items => items.Select(item => item.Data).SequenceEqual(firstItems.Select(item => item.Data))), "Concurrent cached auto-import queries preserve completion identity");
        var data = await oldProject.AutoImports!.GetIndexAsync("/p"u8, new(), () => throw new InvalidOperationException("Expected populated export index"), deadline.Token);
        Check(data.Index.Search(default).All(export => export.Symbol is null), "Published export indexes detach checker symbols");
        Check(!first.Snapshot.FileSystem.TryGetCachedDocument("/p/node_modules/pkg/index.d.ts"u8, out _), "Auto-import-only source text is not retained in the snapshot cache");
        session.Configure(new() { ImportModuleSpecifierPreference = "relative"u8 });
        await using var configured = await session.GetSnapshotAsync(["/p/main.ts"u8], deadline.Token);
        var configuredProject = configured.Snapshot.GetDefaultProject("/p/main.ts"u8)!;
        var reused = await configuredProject.AutoImports!.GetIndexAsync("/p"u8, new(), () => throw new InvalidOperationException("Expected shared export metadata"), deadline.Token);
        Check(ReferenceEquals(data, reused), "Preference-only snapshots reuse detached export metadata");
        fs.WriteFile("/p/node_modules/pkg/index.d.ts"u8, "export declare const pkgTwo: number;"u8);
        session.Notify(new(FileChangeKind.WatchChange, "/p/node_modules/pkg/index.d.ts"u8));
        await using var changed = await session.GetSnapshotAsync(["/p/main.ts"u8], deadline.Token);
        var newProject = changed.Snapshot.GetDefaultProject("/p/main.ts"u8)!;
        Check(ReferenceEquals(oldProject.Program, newProject.Program), "Unimported package edits do not rebuild the main program");
        var currentItems = await Query(newProject);
        Check(currentItems.Select(item => item.Label).SequenceEqual(new Utf8String[] { "pkgTwo"u8 }), "Package edits invalidate export metadata against the new snapshot filesystem");
        Check((await Query(oldProject)).Select(item => item.Label).SequenceEqual(new Utf8String[] { "pkgOne"u8 }), "Retained snapshots preserve their previous package index");
        Check((await Query(newProject, new() { AutoImportFileExcludePatterns = ["**/node_modules/**"u8] })).Length == 0, "Exclusion changes rebuild the package index");
        Check((await Query(newProject)).Any(item => item.Label == "pkgTwo"u8), "Removing exclusions restores package exports");
        Check((await Query(newProject, new() { AutoImportEntrypointDirectorySearch = true })).Any(item => item.Label == "pkgPrivate"u8), "Directory-search preference changes discover additional package entry points");
        Check(!(await Query(newProject)).Any(item => item.Label == "pkgPrivate"u8), "Restoring entry-point-only discovery removes private package files");
        var service = new LanguageServiceDocument(newProject.Program!, newProject.Program!.GetFile("/p/main.ts"u8)!);
        var resolved = await service.ResolveCompletionItemAsync(newProject, currentItems[0], cancellation: deadline.Token);
        Check(resolved.Data == currentItems[0].Data && resolved.AdditionalTextEdits is [{ NewText: var edit }] && edit.Contains("import { pkgTwo } from \"pkg\";"u8), "Cached package completions resolve to the selected module's import edit");
        Check((await Query(newProject, new() { AutoImportSpecifierExcludeRegexes = ["^pkg$"u8] })).Length == 0, "Specifier exclusions invalidate cached module paths");
        Check((await Query(newProject)).Any(item => item.Label == "pkgTwo"u8), "Removing specifier exclusions restores the import");

        using var cache = new AutoImportCache(fs);
        using var canceled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = cache.GetIndexAsync("/p"u8, new(), async () =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, canceled.Token);
            return data;
        }, canceled.Token).AsTask();
        await entered.Task.WaitAsync(deadline.Token);
        bool built = false;
        var queued = cache.GetIndexAsync("/p"u8, new(), () => { built = true; return new(data); }, deadline.Token).AsTask();
        Check(!queued.IsCompleted, "Concurrent index publication waits for the in-progress builder");
        canceled.Cancel();
        try { await interrupted; Check(false, "Canceled index building must fail"); } catch (OperationCanceledException) { checks++; }
        Check(ReferenceEquals(await queued, data) && built, "Cancellation discards incomplete metadata and permits the queued build");

        var (released, retained) = await ReleasedAutoImportIndexAsync();
        // Let an inline async continuation release the producer's stack before collecting.
        await Task.Yield();
        for (int i = 0; i < 3 && released.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(retained.Index.Search("pkg"u8).Any(), "The retention probe keeps a populated auto-import index alive");
        Check(!released.IsAlive, "Retained auto-import metadata does not keep the disposed source tree alive");
        GC.KeepAlive(retained);
        return checks;

        async Task<CompletionItem[]> Query(ProjectSnapshot project, UserPreferences? preferences = null)
        {
            var document = new LanguageServiceDocument(project.Program!, project.Program!.GetFile("/p/main.ts"u8)!);
            var list = await document.GetCompletionsAsync(project, new(0, 3), preferences: preferences, cancellation: deadline.Token);
            return list!.Items.Where(item => item.Data?.AutoImport is not null).ToArray();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Source, AutoImportData Data)> ReleasedAutoImportIndexAsync()
    {
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/p/main.ts"u8] = "pkg"u8.ToArray(), ["/p/export.ts"u8] = "export const pkgValue = 1;"u8.ToArray(),
            ["/p/tsconfig.json"u8] = "{\"compilerOptions\":{\"noLib\":true},\"files\":[\"main.ts\",\"export.ts\"]}"u8.ToArray()
        }, true);
        await using var session = new ProjectSession(fs, new() { CurrentDirectory = "/p"u8 });
        session.Notify(new(FileChangeKind.Open, "/p/main.ts"u8, 1, "pkg"u8));
        await using var snapshot = await session.GetSnapshotAsync(["/p/main.ts"u8]);
        var project = snapshot.Snapshot.GetDefaultProject("/p/main.ts"u8)!;
        var service = new LanguageServiceDocument(project.Program!, project.Program!.GetFile("/p/main.ts"u8)!);
        await service.GetCompletionsAsync(project, new(0, 3));
        var data = await project.AutoImports!.GetIndexAsync("/p"u8, new(), () => throw new InvalidOperationException("Expected retained index"), default);
        return (new(project.Program.GetFile("/p/export.ts"u8)!.Syntax), data);
    }
}

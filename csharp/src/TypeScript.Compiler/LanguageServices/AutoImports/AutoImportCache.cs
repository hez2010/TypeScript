using System.Collections.Concurrent;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Projects;

namespace TypeScript.Compiler.LanguageServices;

internal sealed record AutoImportData(AutoImportIndex Index, IReadOnlyDictionary<Utf8String, ResolvedEntrypoint[]> Entrypoints);

// A snapshot owns only detached export metadata and strings. Builders and checker leases never enter this cache.
internal sealed class AutoImportCache(IFileSystem fileSystem, IReadOnlyDictionary<Utf8String, Utf8String>? referenceSources = null) : IDisposable
{
    private sealed record Entry(bool? DirectorySearch, Utf8String[] Excludes, AutoImportData Data);
    private readonly object sync = new();
    private readonly SemaphoreSlim buildGate = new(1);
    private readonly Dictionary<Utf8String, Entry> indexes = [];
    private readonly Dictionary<Utf8String, ConcurrentDictionary<Utf8String, Utf8String>> specifiers = [];
    private Utf8String[] specifierExcludes = [];
    private bool disposed;
    internal IFileSystem FileSystem { get; } = fileSystem is SnapshotFileSystem snapshot ? snapshot.ForAutoImports() : fileSystem;
    internal IReadOnlyDictionary<Utf8String, Utf8String> ReferenceSources { get; } = referenceSources ?? new Dictionary<Utf8String, Utf8String>();

    internal AutoImportCache Clone(IFileSystem fileSystem, bool reuseIndexes, IReadOnlySet<Utf8String> retainedFiles,
        IReadOnlyDictionary<Utf8String, Utf8String> referenceSources)
    {
        var copy = new AutoImportCache(fileSystem, referenceSources);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (reuseIndexes && ReferenceSources.Count == referenceSources.Count && ReferenceSources.All(pair => referenceSources.GetValueOrDefault(pair.Key) == pair.Value))
                foreach (var pair in indexes) copy.indexes.Add(pair.Key, pair.Value);
            foreach (var pair in specifiers) if (retainedFiles.Contains(pair.Key)) copy.specifiers.Add(pair.Key, pair.Value);
            copy.specifierExcludes = specifierExcludes;
        }
        return copy;
    }

    internal async ValueTask<AutoImportData> GetIndexAsync(Utf8String directory, UserPreferences preferences,
        Func<ValueTask<AutoImportData>> build, CancellationToken cancellation)
    {
        await buildGate.WaitAsync(cancellation);
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (indexes.TryGetValue(directory, out var entry) && entry.DirectorySearch == preferences.AutoImportEntrypointDirectorySearch
                    && Same(entry.Excludes, preferences.AutoImportFileExcludePatterns)) return entry.Data;
            }
            var data = await build();
            cancellation.ThrowIfCancellationRequested();
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                indexes[directory] = new(preferences.AutoImportEntrypointDirectorySearch, preferences.AutoImportFileExcludePatterns.ToArray(), data);
            }
            return data;
        }
        finally { buildGate.Release(); }
    }

    internal bool TryGetSpecifier(Utf8String file, Utf8String target, UserPreferences preferences, out Utf8String specifier)
    {
        lock (sync)
        {
            ValidateSpecifiers(preferences);
            specifier = default;
            return specifiers.TryGetValue(file, out var paths) && paths.TryGetValue(target, out specifier);
        }
    }

    internal void StoreSpecifier(Utf8String file, Utf8String target, Utf8String specifier, UserPreferences preferences)
    {
        lock (sync)
        {
            ValidateSpecifiers(preferences);
            if (!specifiers.TryGetValue(file, out var paths)) specifiers[file] = paths = new();
            paths[target] = specifier;
        }
    }

    private void ValidateSpecifiers(UserPreferences preferences)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Same(specifierExcludes, preferences.AutoImportSpecifierExcludeRegexes)) return;
        specifiers.Clear();
        specifierExcludes = preferences.AutoImportSpecifierExcludeRegexes.ToArray();
    }

    private static bool Same(IReadOnlyList<Utf8String> first, IReadOnlyList<Utf8String> second)
        => first.Count == second.Count && first.ToHashSet().SetEquals(second);

    public void Dispose()
    {
        lock (sync) { disposed = true; indexes.Clear(); specifiers.Clear(); specifierExcludes = []; }
    }
}

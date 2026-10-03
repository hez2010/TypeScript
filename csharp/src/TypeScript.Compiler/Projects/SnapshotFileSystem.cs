using System.Collections.Concurrent;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Projects;

/// <summary>Memoizes owned file handles for one snapshot, sharing only unaffected handles with its successor.</summary>
public sealed class SnapshotFileSystem : IFileSystem, ICompilerSyntaxProvider
{
    private readonly ConcurrentDictionary<Utf8String, Lazy<DocumentSnapshot?>> files;
    private readonly ConcurrentDictionary<Utf8String, Lazy<DirectoryEntries>> directories;
    private readonly ConcurrentDictionary<Utf8String, Utf8String> realPaths;
    private readonly ConcurrentDictionary<Utf8String, bool> directoryExists;
    private readonly Dictionary<Utf8String, Dictionary<Utf8String, Utf8String>> cachedDirectories;
    private readonly Utf8String currentDirectory;
    public OverlayFileSystem Overlay { get; }
    public bool CaseSensitive => Overlay.CaseSensitive;
    internal int CachedDiskFileCount => files.Count(pair => pair.Value.IsValueCreated && pair.Value.Value is { IsOverlay: false });
    internal ProjectParseCache? ParseCache { get; init; }
    private readonly AsyncLocal<FileObservation?> observation = new();

    internal FileObservation ObserveFiles() => new(this);
    internal sealed class FileObservation : IDisposable
    {
        private readonly SnapshotFileSystem owner;
        private readonly FileObservation? previous;
        internal ConcurrentDictionary<Utf8String, byte> Files { get; } = new();
        internal FileObservation(SnapshotFileSystem owner)
        {
            this.owner = owner; previous = owner.observation.Value; owner.observation.Value = this;
        }
        public void Dispose() => owner.observation.Value = previous;
    }

    public SnapshotFileSystem(OverlayFileSystem overlay, Utf8String currentDirectory,
        SnapshotFileSystem? previous = null, FileChangeSummary? changes = null)
    {
        Overlay = overlay;
        this.currentDirectory = currentDirectory;
        var comparer = CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
        files = new(comparer); directories = new(comparer); realPaths = new(comparer); directoryExists = new(comparer);
        cachedDirectories = new(comparer);
        if (previous is null || changes?.InvalidateAll == true || changes?.InvalidateFileCache == true) return;
        var invalidated = changes is null ? new HashSet<Utf8String>(comparer)
            : changes.Changed.Concat(changes.Created).Concat(changes.Deleted).Concat(changes.Closed)
                .Append(changes.Opened).Append(changes.Reopened).Where(path => !path.IsEmpty).Select(Path).ToHashSet(comparer);
        foreach (var (path, cached) in previous.files)
        {
            if (!cached.IsValueCreated || Changed(path) || overlay.HasOverlayDirectory(path)
                || changes?.InvalidateNodeModules == true && path.Contains("/node_modules/"u8)) continue;
            var document = cached.Value;
            if (overlay.Overlays.TryGetValue(path, out var current))
            {
                if (!ReferenceEquals(document, current)) continue;
            }
            else if (document?.IsOverlay == true) continue;
            files[path] = cached;
        }
        foreach (var (path, target) in previous.realPaths)
            if (!Changed(path) && !Changed(target)) realPaths[path] = target;
        foreach (var (path, entries) in previous.cachedDirectories)
        {
            var retained = entries.Where(entry => files.TryGetValue(entry.Key, out var file) && file.IsValueCreated && file.Value is not null
                || overlay.FileExists(entry.Key) || overlay.DirectoryExists(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value, comparer);
            if (retained.Count != 0) cachedDirectories.Add(path, retained);
        }
        AddCachedDirectories(previous.files.Values.Where(file => file.IsValueCreated).Select(file => file.Value)
            .OfType<DocumentSnapshot>().Where(file => !file.IsOverlay && (files.ContainsKey(Path(file.FileName)) || overlay.FileExists(file.FileName))));

        bool Changed(Utf8String path)
        {
            Utf8String target = previous.realPaths.GetValueOrDefault(path, path);
            return invalidated.Any(change => CompilerPath.Contains(change, path, CaseSensitive)
                || CompilerPath.Contains(change, target, CaseSensitive));
        }
    }

    private Utf8String Path(Utf8String path) => path.StartsWith("^/"u8) ? path : CompilerPath.Resolve(currentDirectory, path);
    public DocumentSnapshot? GetDocument(Utf8String path)
    {
        path = Path(path);
        observation.Value?.Files.TryAdd(CaseSensitive ? path : path.ToLowerInvariant(), 0);
        return files.GetOrAdd(path, key => new(() =>
        {
            if (key.Contains("/node_modules/"u8, CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) _ = RealPath(key);
            return Overlay.GetDocument(key);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
    internal bool TryGetCachedDocument(Utf8String path, out DocumentSnapshot? document)
    {
        if (files.TryGetValue(Path(path), out var cached) && cached.IsValueCreated) { document = cached.Value; return true; }
        document = null; return false;
    }
    internal void ShareDocument(Utf8String path, DocumentSnapshot? document) => files[Path(path)] = new(() => document);
    internal void ForgetDocument(Utf8String path) => files.TryRemove(Path(path), out _);
    internal void ForgetDirectory(Utf8String path)
    {
        foreach (var key in files.Keys) if (CompilerPath.Contains(path, key, CaseSensitive)) files.TryRemove(key, out _);
    }
    internal void RetainFiles(IEnumerable<Utf8String> paths)
    {
        var retained = paths.Select(Path).ToHashSet(files.Comparer);
        foreach (var path in files.Keys) if (!retained.Contains(path)) files.TryRemove(path, out _);
    }
    public byte[]? ReadFile(Utf8String path) => GetDocument(path)?.Text.Span.ToArray();
    public CompilerSource? ReadSource(Utf8String path) => GetDocument(path) is { } document ? new(document.Source, document.Kind) : null;
    public ValueTask<Ast.SourceFileNode> ParseSourceAsync(Syntax.ParseOptions options, Text.SourceText source, CancellationToken cancellation) =>
        ParseCache?.ParseAsync(options, source, cancellation) ?? Syntax.Parser.ParseSourceFileAsync(options, source, cancellation);
    public bool FileExists(Utf8String path) => GetDocument(path) is not null;
    public bool DirectoryExists(Utf8String path) => directoryExists.GetOrAdd(Path(path), key => Overlay.DirectoryExists(key));
    internal bool IsCachedDirectory(Utf8String path) => cachedDirectories.ContainsKey(Path(path))
        || directories.TryGetValue(Path(path), out var directory) && directory.IsValueCreated;
    internal bool WasMissingDirectory(Utf8String path) => directoryExists.TryGetValue(Path(path), out bool exists) && !exists;
    public FileEntry? Stat(Utf8String path) => Overlay.Stat(path);
    public Utf8String RealPath(Utf8String path) => realPaths.GetOrAdd(Path(path), key => Overlay.RealPath(key));
    public DirectoryEntries GetAccessibleEntries(Utf8String path)
    {
        var entries = directories.GetOrAdd(Path(path), key => new(() => MergeDirectory(key, Overlay.GetAccessibleEntries(key)),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return new(entries.Files.ToArray(), entries.Directories.ToArray(), entries.SymbolicLinks?.ToHashSet());
    }

    private DirectoryEntries MergeDirectory(Utf8String path, DirectoryEntries lower)
    {
        if (!cachedDirectories.TryGetValue(path, out var cached)) return lower;
        var comparer = CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
        var fileNames = new List<Utf8String>(); var directoryNames = new List<Utf8String>();
        var names = new HashSet<Utf8String>(comparer);
        var links = lower.SymbolicLinks?.ToHashSet(comparer) ?? new(comparer);
        foreach (var (child, name) in cached.OrderBy(entry => entry.Key, Utf8StringComparer.Ordinal))
        {
            if (!names.Add(name)) continue;
            links.Remove(name);
            bool file = !Overlay.HasOverlayDirectory(child) && (files.TryGetValue(child, out var handle)
                && handle.IsValueCreated && handle.Value is not null || Overlay.FileExists(child));
            (file ? fileNames : directoryNames).Add(name);
        }
        foreach (var name in lower.Files) if (names.Add(name)) fileNames.Add(name);
        foreach (var name in lower.Directories) if (names.Add(name)) directoryNames.Add(name);
        return new(fileNames.ToArray(), directoryNames.ToArray(), links);
    }

    private void AddCachedDirectories(IEnumerable<DocumentSnapshot> documents)
    {
        foreach (var document in documents)
        {
            Utf8String child = Path(document.FileName), spelling = document.FileName;
            while (true)
            {
                var parent = CompilerPath.DirectoryName(child);
                if (parent == child || parent.IsEmpty) break;
                if (!cachedDirectories.TryGetValue(parent, out var entries)) cachedDirectories[parent] = entries = new(files.Comparer);
                entries[child] = CompilerPath.BaseName(spelling);
                child = parent; spelling = CompilerPath.DirectoryName(spelling);
            }
        }
    }
    public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents) => Overlay.WriteFile(path, contents);
    public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents) => Overlay.AppendFile(path, contents);
    public void Remove(Utf8String path) => Overlay.Remove(path);
    public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc) => Overlay.SetTimes(path, accessTimeUtc, writeTimeUtc);

    // Package discovery may read thousands of files that are not part of any editor program.
    // Reuse existing handles without adding those files to the snapshot's retained cache.
    internal IFileSystem ForAutoImports() => new AutoImportFileSystem(this);

    private sealed class AutoImportFileSystem(SnapshotFileSystem snapshot) : IFileSystem, ICompilerSyntaxProvider
    {
        private DocumentSnapshot? Document(Utf8String path)
        {
            path = snapshot.RealPath(path);
            return snapshot.TryGetCachedDocument(path, out var cached) ? cached : snapshot.Overlay.GetDocument(path);
        }
        public bool CaseSensitive => snapshot.CaseSensitive;
        public bool FileExists(Utf8String path)
        {
            path = snapshot.RealPath(path);
            return snapshot.TryGetCachedDocument(path, out var cached) ? cached is not null : snapshot.Overlay.FileExists(path);
        }
        public bool DirectoryExists(Utf8String path) => snapshot.DirectoryExists(path);
        public byte[]? ReadFile(Utf8String path) => Document(path)?.Text.Span.ToArray();
        public CompilerSource? ReadSource(Utf8String path) => Document(path) is { } document ? new(document.Source, document.Kind) : null;
        public ValueTask<Ast.SourceFileNode> ParseSourceAsync(Syntax.ParseOptions options, Text.SourceText source, CancellationToken cancellation) =>
            snapshot.ParseSourceAsync(options, source, cancellation);
        public DirectoryEntries GetAccessibleEntries(Utf8String path) => snapshot.GetAccessibleEntries(path);
        public FileEntry? Stat(Utf8String path) => snapshot.Stat(path);
        public Utf8String RealPath(Utf8String path) => snapshot.RealPath(path);
        public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents) => throw new NotSupportedException();
        public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents) => throw new NotSupportedException();
        public void Remove(Utf8String path) => throw new NotSupportedException();
        public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc) => throw new NotSupportedException();
    }
}

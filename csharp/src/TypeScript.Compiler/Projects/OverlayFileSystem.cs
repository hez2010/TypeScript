using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;

namespace TypeScript.Compiler.Projects;

/// <summary>A published overlay layer. Applying editor events returns a new layer without changing the old one.</summary>
public sealed class OverlayFileSystem : IFileSystem
{
    private readonly Dictionary<Utf8String, DocumentSnapshot> overlays;
    private readonly Dictionary<Utf8String, Dictionary<Utf8String, Utf8String>> directories;
    private readonly Utf8String currentDirectory;
    private readonly Utf8StringComparer comparer;
    private readonly RequestFileSystem? requestLayer;
    public IFileSystem BaseFileSystem { get; }
    public bool CaseSensitive => BaseFileSystem.CaseSensitive;
    public IReadOnlyDictionary<Utf8String, DocumentSnapshot> Overlays { get; }

    public OverlayFileSystem(IFileSystem fileSystem, Utf8String currentDirectory,
        IEnumerable<DocumentSnapshot>? documents = null)
    {
        if (fileSystem is RequestFileSystem request)
        {
            var inner = new OverlayFileSystem(request.BaseFileSystem, currentDirectory, documents);
            requestLayer = request.WithBase(inner);
            fileSystem = requestLayer;
            documents = requestLayer.FilterOverlays(inner.Overlays.Values);
        }
        BaseFileSystem = fileSystem is OverlayFileSystem layer ? layer.BaseFileSystem : fileSystem;
        this.currentDirectory = currentDirectory;
        comparer = CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
        overlays = new(comparer);
        foreach (var document in documents ?? []) overlays[Path(document.FileName)] = document;
        Overlays = overlays.AsReadOnly();
        directories = new(comparer);
        foreach (var (path, document) in overlays)
        {
            Utf8String child = path, spelling = document.FileName;
            while (true)
            {
                Utf8String parent = CompilerPath.DirectoryName(child);
                if (parent == child || parent.IsEmpty) break;
                if (!directories.TryGetValue(parent, out var entries)) directories.Add(parent, entries = new(comparer));
                entries[child] = CompilerPath.BaseName(spelling);
                child = parent;
                spelling = CompilerPath.DirectoryName(spelling);
            }
        }
    }

    private Utf8String Path(Utf8String path) => path.StartsWith("^/"u8) ? path : CompilerPath.Resolve(currentDirectory, path);
    internal bool HasOverlayDirectory(Utf8String path) => directories.ContainsKey(Path(path));
    public DocumentSnapshot? GetDocument(Utf8String fileName)
    {
        if (requestLayer is not null) return requestLayer.GetDocument(fileName);
        Utf8String path = Path(fileName);
        if (overlays.TryGetValue(path, out var document)) return document;
        if (directories.ContainsKey(path)) return null;
        if (BaseFileSystem is ICompilerSourceProvider source) return source.ReadSource(path) is { } text
            ? new(fileName, text.Text.Text, kind: text.Kind) : null;
        byte[]? bytes = BaseFileSystem.ReadFile(path);
        return bytes is null ? null : new(fileName, new(SourceEncoding.DecodeBytes(bytes)));
    }

    public bool FileExists(Utf8String path) => requestLayer?.FileExists(path) ?? (overlays.ContainsKey(Path(path))
        || !directories.ContainsKey(Path(path)) && BaseFileSystem.FileExists(path));
    public bool DirectoryExists(Utf8String path) => requestLayer?.DirectoryExists(path) ?? (directories.ContainsKey(Path(path))
        || !overlays.ContainsKey(Path(path)) && BaseFileSystem.DirectoryExists(path));
    public byte[]? ReadFile(Utf8String path) => GetDocument(path)?.Text.Span.ToArray();
    public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents) => BaseFileSystem.WriteFile(path, contents);
    public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents) => BaseFileSystem.AppendFile(path, contents);
    public void Remove(Utf8String path) => BaseFileSystem.Remove(path);
    public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc) => BaseFileSystem.SetTimes(path, accessTimeUtc, writeTimeUtc);
    public Utf8String RealPath(Utf8String path) => BaseFileSystem.RealPath(path);

    public FileEntry? Stat(Utf8String path)
    {
        if (requestLayer is not null) return requestLayer.Stat(path);
        if (overlays.TryGetValue(Path(path), out var document))
            return new(CompilerPath.BaseName(document.FileName), false, document.Text.Length, default);
        if (directories.ContainsKey(Path(path))) return new(CompilerPath.BaseName(path), true, 0, default);
        return BaseFileSystem.Stat(path);
    }

    public DirectoryEntries GetAccessibleEntries(Utf8String path)
    {
        if (requestLayer is not null) return requestLayer.GetAccessibleEntries(path);
        Utf8String canonical = Path(path);
        if (overlays.ContainsKey(canonical)) return new([], []);
        var entries = BaseFileSystem.GetAccessibleEntries(path);
        if (!directories.TryGetValue(canonical, out var children)) return entries;
        var files = entries.Files.ToList();
        var folders = entries.Directories.ToList();
        var symlinks = entries.SymbolicLinks?.ToHashSet(comparer) ?? new(comparer);
        foreach (var (child, name) in children)
        {
            files.RemoveAll(value => comparer.Equals(value, name));
            folders.RemoveAll(value => comparer.Equals(value, name));
            symlinks.Remove(name);
            (overlays.ContainsKey(child) ? files : folders).Add(name);
        }
        return new(files.ToArray(), folders.ToArray(), symlinks);
    }

    private sealed class Events
    {
        internal FileChange? Open, Close;
        internal List<FileChange> Changes = [];
        internal bool WatchChanged, Saved, Created, Deleted;
    }

    public (OverlayFileSystem FileSystem, FileChangeSummary Summary) Apply(
        IReadOnlyList<FileChange> changes, PositionEncoding encoding = PositionEncoding.Utf8)
    {
        if (requestLayer?.BaseFileSystem is OverlayFileSystem inner)
        {
            var applied = inner.Apply(changes, encoding);
            requestLayer.ExpandChanges(applied.Summary);
            return (new(requestLayer, currentDirectory, applied.FileSystem.Overlays.Values), applied.Summary);
        }
        var summary = new FileChangeSummary(CaseSensitive);
        var grouped = new Dictionary<Utf8String, Events>(comparer);
        foreach (var change in changes)
        {
            Utf8String path = Path(change.FileName);
            if (!grouped.TryGetValue(path, out var events)) grouped.Add(path, events = new());
            else if (events.Open is not null) throw new InvalidOperationException("An open must be the last event for its document");
            if (change.Kind is FileChangeKind.WatchCreate or FileChangeKind.WatchChange or FileChangeKind.WatchDelete
                && !path.Contains("/node_modules/"u8)) summary.IncludesWatchChangeOutsideNodeModules = true;
            switch (change.Kind)
            {
                case FileChangeKind.Open:
                    events.Open = change; events.Close = null; events.Changes.Clear();
                    events.WatchChanged = events.Saved = events.Created = events.Deleted = false;
                    break;
                case FileChangeKind.Close:
                    events.Close = change; events.Changes.Clear(); events.Saved = events.WatchChanged = false;
                    break;
                case FileChangeKind.Change:
                    if (events.Close is not null) throw new InvalidOperationException("Cannot change a closed document");
                    events.Changes.Add(change); events.Saved = events.WatchChanged = false;
                    break;
                case FileChangeKind.Save: events.Saved = true; break;
                case FileChangeKind.WatchCreate:
                    if (events.Deleted) { events.Deleted = false; events.WatchChanged = true; }
                    else events.Created = true;
                    break;
                case FileChangeKind.WatchChange:
                    if (!events.Created) { events.WatchChanged = true; events.Saved = false; }
                    break;
                case FileChangeKind.WatchDelete:
                    events.WatchChanged = events.Saved = false;
                    if (events.Created) events.Created = false; else events.Deleted = true;
                    break;
            }
        }
        var next = new Dictionary<Utf8String, DocumentSnapshot>(overlays, comparer);
        foreach (var (path, events) in grouped)
        {
            next.TryGetValue(path, out var document);
            if (events.Open is { } opened)
            {
                if (!summary.Opened.IsEmpty || !summary.Reopened.IsEmpty)
                    throw new InvalidOperationException("Only one document may open in one event batch");
                if (document is not null && document.Text != opened.Content) summary.Changed.Add(path);
                else if (document is null) summary.Opened = path;
                else summary.Reopened = path;
                next[path] = new(opened.FileName, opened.Content, opened.Version, opened.ScriptKind, true, false);
                continue;
            }
            if (events.Close is not null && document is not null)
            {
                summary.Closed.Add(path); next.Remove(path); document = null;
            }
            if (events.WatchChanged)
            {
                if (document is null) summary.Changed.Add(path);
                else if (!events.Saved)
                {
                    byte[]? disk = BaseFileSystem.ReadFile(document.FileName);
                    bool matches = disk is not null && document.Text.Span.SequenceEqual(SourceEncoding.DecodeBytes(disk));
                    next[path] = document = document.WithDiskMatch(matches);
                }
            }
            if (events.Changes.Count > 0 && document is not null)
            {
                summary.Changed.Add(path);
                foreach (var change in events.Changes)
                    next[path] = document = document.Apply(change.Edits ?? [], change.Version, encoding);
            }
            if (events.Saved)
            {
                if (document is not null) next[path] = document = document.WithDiskMatch(true);
                else if (!events.WatchChanged) summary.Changed.Add(path);
            }
            if (events.Created && document is null) summary.Created.Add(path);
            if (events.Deleted && document is null) summary.Deleted.Add(path);
        }
        return (new(BaseFileSystem, currentDirectory, next.Values), summary);
    }
}

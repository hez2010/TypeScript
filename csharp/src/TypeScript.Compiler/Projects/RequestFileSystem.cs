using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Projects;

public enum RequestFileSystemKind { Full, Layer }
public sealed record RequestSymlink(Utf8String Target, bool Host = false);
public sealed record RequestFileSystemOptions(RequestFileSystemKind Kind)
{
    public IReadOnlyDictionary<Utf8String, Utf8String> Files { get; init; } = new Dictionary<Utf8String, Utf8String>();
    public IReadOnlyDictionary<Utf8String, DirectoryEntries> Directories { get; init; } = new Dictionary<Utf8String, DirectoryEntries>();
    public IReadOnlyDictionary<Utf8String, RequestSymlink> Symlinks { get; init; } = new Dictionary<Utf8String, RequestSymlink>();
    public IReadOnlyList<Utf8String> RemovedPaths { get; init; } = [];
}

/// <summary>An immutable, compacted API filesystem. Explicit directory listings are complete, ordered results.</summary>
public sealed partial class RequestFileSystem : IFileSystem, ICompilerSourceProvider
{
    private enum Fallback { Inherit, Allowed, Missing }
    private abstract record Entry(Utf8String Name);
    private sealed record FileData(Utf8String FileName, Utf8String Content) : Entry(FileName);
    private sealed record DirectoryData(Utf8String DirectoryName, DirectoryEntries? Listing = null) : Entry(DirectoryName);
    private sealed record LinkData(Utf8String LinkName, Utf8String Target, bool Host) : Entry(LinkName);
    private sealed class PathNode
    {
        internal Entry? Entry;
        internal Fallback Fallback;
        internal Dictionary<Utf8String, PathNode> Children = [];
        internal bool HasSymlinks;
        internal bool ReplacesSubtree => Entry is FileData or LinkData;
    }

    private readonly PathNode paths;
    private readonly Utf8String currentDirectory;
    private readonly Utf8StringComparer comparer;
    public IFileSystem BaseFileSystem { get; }
    public bool CaseSensitive => BaseFileSystem.CaseSensitive;
    public RequestFileSystemKind Kind { get; }

    private RequestFileSystem(RequestFileSystemKind kind, IFileSystem baseFileSystem, Utf8String cwd, PathNode paths)
    {
        Kind = kind; BaseFileSystem = baseFileSystem; currentDirectory = cwd; this.paths = paths;
        comparer = CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
    }

    public static RequestFileSystem Create(RequestFileSystemOptions options, IFileSystem baseFileSystem,
        Utf8String currentDirectory, FileChangeSummary? changes = null)
    {
        if (options.Kind is not (RequestFileSystemKind.Full or RequestFileSystemKind.Layer))
            throw new ArgumentException("Unknown request filesystem kind", nameof(options));
        var originalBase = baseFileSystem;
        if (options.Kind == RequestFileSystemKind.Full && baseFileSystem is RequestFileSystem old) baseFileSystem = old.BaseFileSystem;
        var result = new RequestFileSystem(options.Kind, baseFileSystem, currentDirectory, new());
        result.RegisterDirectory(currentDirectory);
        foreach (var (name, content) in options.Files)
        {
            var absolute = result.Absolute(name); var node = result.Ensure(result.Path(absolute));
            if (node.Entry is FileData existing) throw new ArgumentException($"Duplicate request filesystem file path '{existing.Name}' and '{absolute}'");
            node.Entry = new FileData(absolute, Utf8String.Copy(content.Span));
            result.RegisterDirectory(CompilerPath.DirectoryName(absolute));
        }
        var seen = new HashSet<Utf8String>();
        var listedDirectories = new List<Utf8String>();
        foreach (var (name, entries) in options.Directories)
        {
            var absolute = result.Absolute(name); var path = result.Path(absolute); var node = result.Ensure(path);
            if (!seen.Add(path)) throw new ArgumentException($"Duplicate request filesystem directory path '{absolute}'");
            if (node.Entry is not FileData) node.Entry = new DirectoryData(absolute, Copy(entries));
            result.RegisterDirectory(CompilerPath.DirectoryName(absolute));
            foreach (var child in entries.Directories) listedDirectories.Add(CompilerPath.Combine(absolute, child));
        }
        foreach (var name in options.Symlinks.Keys) result.RegisterDirectory(CompilerPath.DirectoryName(result.Absolute(name)));
        seen.Clear();
        foreach (var (name, link) in options.Symlinks)
        {
            var absolute = result.Absolute(name); var path = result.Path(absolute); var node = result.Ensure(path);
            if (!seen.Add(path)) throw new ArgumentException($"Duplicate request filesystem symlink path '{absolute}'");
            node.Entry ??= new LinkData(absolute, Absolute(link.Target, CompilerPath.DirectoryName(absolute)), link.Host);
        }
        foreach (var directory in listedDirectories) result.RegisterDirectory(directory);
        foreach (var path in options.RemovedPaths) result.Ensure(result.Path(result.Absolute(path))).Fallback = Fallback.Missing;
        var composed = result.Compose(null, result.paths, Fallback.Allowed);
        if (baseFileSystem is RequestFileSystem previous)
            result = new(previous.Kind, previous.BaseFileSystem, currentDirectory, result.Compose(previous.paths, composed, Fallback.Allowed));
        else result = new(result.Kind, baseFileSystem, currentDirectory, composed);
        if (options.Kind == RequestFileSystemKind.Layer && changes is not null) result.AddChanges(options, originalBase, changes);
        return result;
    }

    internal RequestFileSystem WithBase(IFileSystem fileSystem) => new(Kind, fileSystem, currentDirectory, paths);
    private Utf8String Absolute(Utf8String path) => Absolute(path, currentDirectory);
    private static Utf8String Absolute(Utf8String path, Utf8String cwd)
    {
        var absolute = CompilerPath.Resolve(cwd, path);
        return absolute.Length > CompilerPath.RootLength(absolute) ? absolute.TrimEnd((byte)'/') : absolute;
    }
    private Utf8String Path(Utf8String path) => CaseSensitive ? Absolute(path) : Absolute(path).ToLowerInvariant();
    private static List<Utf8String> Ancestors(Utf8String path)
    {
        var result = new List<Utf8String>();
        while (true)
        {
            result.Add(path); var parent = CompilerPath.DirectoryName(path);
            if (parent == path) break;
            path = parent;
        }
        result.Reverse(); return result;
    }
    private PathNode Ensure(Utf8String path)
    {
        var node = paths;
        foreach (var ancestor in Ancestors(path))
        {
            if (!node.Children.TryGetValue(ancestor, out var child)) node.Children.Add(ancestor, child = new());
            node = child;
        }
        return node;
    }
    private (PathNode? Node, Fallback Fallback) LookupNode(Utf8String path)
    {
        PathNode? node = paths; var fallback = Fallback.Inherit;
        foreach (var ancestor in Ancestors(Path(path)))
        {
            if (node is null) break;
            if (node.Fallback != Fallback.Inherit) fallback = node.Fallback;
            node = node.Children.GetValueOrDefault(ancestor);
        }
        if (node is not null && node.Fallback != Fallback.Inherit) fallback = node.Fallback;
        return (node, fallback);
    }
    private bool Blocks(Utf8String path) => LookupNode(path).Fallback == Fallback.Missing;
    private void RegisterDirectory(Utf8String name)
    {
        name = Absolute(name);
        while (true)
        {
            var node = Ensure(Path(name)); if (node.Entry is not null) return;
            node.Entry = new DirectoryData(name);
            var parent = CompilerPath.DirectoryName(name); if (parent == name) return; name = parent;
        }
    }
    private PathNode Compose(PathNode? original, PathNode overlay, Fallback fallback)
    {
        if (overlay.Fallback != Fallback.Inherit) { fallback = overlay.Fallback; original = null; }
        if (overlay.ReplacesSubtree) original = null;
        var result = new PathNode { Entry = original?.Entry, Fallback = original?.Fallback ?? Fallback.Inherit,
            Children = original is null ? [] : new(original.Children) };
        if (overlay.Fallback != Fallback.Inherit || overlay.ReplacesSubtree) result.Fallback = fallback;
        var previousDirectory = result.Entry as DirectoryData;
        var overlayDirectory = overlay.Entry as DirectoryData;
        if (overlay.Entry is not null)
        {
            result.Entry = overlay.Entry;
            if (overlayDirectory is { Listing: null } && previousDirectory is not null)
                result.Entry = overlayDirectory with { Listing = previousDirectory.Listing };
        }
        foreach (var (path, child) in overlay.Children)
            result.Children[path] = Compose(result.Children.GetValueOrDefault(path), child, fallback);
        if (result.Entry is DirectoryData { Listing: { } listing } directory && overlayDirectory?.Listing is null)
        {
            foreach (var (path, child) in overlay.Children)
            {
                var name = CompilerPath.BaseName(path);
                if (child.Fallback == Fallback.Missing || child.ReplacesSubtree)
                    listing = Filter(listing, value => !comparer.Equals(value, name));
                if (child.Entry is FileData file) listing = Merge(listing, new([CompilerPath.BaseName(file.Name)], []));
                else if (child.Entry is DirectoryData dir) listing = Merge(listing, new([], [CompilerPath.BaseName(dir.Name)]));
            }
            result.Entry = directory with { Listing = listing };
        }
        result.HasSymlinks = result.Entry is LinkData || result.Children.Values.Any(child => child.HasSymlinks);
        return result;
    }
    private IEnumerable<LinkData> Links()
    {
        var pending = new Stack<PathNode>(); pending.Push(paths);
        while (pending.TryPop(out var node))
        {
            if (!node.HasSymlinks) continue;
            if (node.Entry is LinkData link) yield return link;
            foreach (var child in node.Children.Values) pending.Push(child);
        }
    }
    private bool TrySuffix(Utf8String path, Utf8String prefix, out Utf8String suffix)
    {
        var canonical = CaseSensitive ? prefix : prefix.ToLowerInvariant();
        if (!(CaseSensitive ? path : path.ToLowerInvariant()).StartsWith(canonical)) { suffix = default; return false; }
        int offset = prefix.Length;
        if (!CaseSensitive)
        {
            offset = 0;
            for (int i = 0; i < canonical.Length;)
            {
                System.Text.Rune.DecodeFromUtf8(canonical.Span[i..], out _, out int width);
                i += Math.Max(1, width);
                if (offset < path.Length)
                {
                    System.Text.Rune.DecodeFromUtf8(path.Span[offset..], out _, out width);
                    offset += Math.Max(1, width);
                }
            }
        }
        suffix = path[offset..];
        return suffix.IsEmpty || prefix.EndsWith((byte)'/') || suffix.StartsWith((byte)'/');
    }
    private bool Contains(Utf8String parent, Utf8String path) => TrySuffix(path, parent, out _);
    private (Utf8String Path, bool Followed, bool Host, bool Ok) Resolve(Utf8String path)
    {
        path = Absolute(path); bool followed = false; var seen = new HashSet<Utf8String>();
        while (true)
        {
            var canonical = Path(path); PathNode? node = paths; LinkData? match = null;
            var ancestors = Ancestors(canonical);
            foreach (var ancestor in ancestors)
            {
                node = node?.Children.GetValueOrDefault(ancestor);
                if (node?.Entry is FileData && ancestor != canonical) return (path, followed, false, false);
            }
            node = paths;
            foreach (var ancestor in ancestors)
            {
                node = node?.Children.GetValueOrDefault(ancestor);
                if (node?.Entry is LinkData link) { match = link; break; }
            }
            if (match is null) return (path, followed, Links().Any(link => link.Host && Contains(Path(link.Target), canonical)), true);
            if (!seen.Add(Path(match.Name))) return (path, followed, false, false);
            followed = true;
            if (!TrySuffix(path, match.Name, out var suffix)) return (path, followed, false, false);
            path = Absolute(CompilerPath.Combine(match.Target, suffix.TrimStart((byte)'/')));
            if (match.Host) return (path, followed, true, true);
        }
    }
    private readonly record struct PathLookup(Utf8String Path, Entry? Entry, IFileSystem? FileSystem, bool Followed = false, bool Ok = true);
    private PathLookup Lookup(Utf8String path)
    {
        var absolute = Absolute(path); var (node, fallback) = LookupNode(absolute);
        if (node?.Entry is FileData or DirectoryData) return new(absolute, node.Entry, null);
        if (fallback == Fallback.Missing) return default;
        var resolved = Resolve(path); if (!resolved.Ok) return default;
        var (target, targetFallback) = LookupNode(resolved.Path);
        if (!resolved.Host && target?.Entry is FileData or DirectoryData)
            return new(resolved.Path, target.Entry, null, resolved.Followed);
        if (resolved.Host || Kind == RequestFileSystemKind.Layer)
            return targetFallback == Fallback.Missing ? default : new(resolved.Path, null, BaseFileSystem, resolved.Followed);
        return new(resolved.Path, null, null, resolved.Followed);
    }

    public byte[]? ReadFile(Utf8String path)
    {
        var found = Lookup(path);
        if (!found.Ok || found.Entry is DirectoryData) return null;
        return found.FileSystem is { } host ? host.ReadFile(found.Path) : (found.Entry as FileData)?.Content.Span.ToArray();
    }
    public CompilerSource? ReadSource(Utf8String path)
        => GetDocument(path) is { } document ? new(document.Source, document.Kind) : null;
    public DocumentSnapshot? GetDocument(Utf8String path)
    {
        var found = Lookup(path);
        if (!found.Ok || found.Entry is DirectoryData) return null;
        if (found.FileSystem is OverlayFileSystem overlay) return overlay.GetDocument(found.Path);
        if (found.FileSystem is SnapshotFileSystem snapshot) return snapshot.GetDocument(found.Path);
        if (found.FileSystem is ICompilerSourceProvider source) return source.ReadSource(found.Path) is { } text
            ? new(found.Path, text.Text.Text, kind: text.Kind) : null;
        if (found.FileSystem is { } host) return host.ReadFile(found.Path) is { } bytes
            ? new(path, new(SourceEncoding.DecodeBytes(bytes))) : null;
        return found.Entry is FileData file ? new(path, file.Content) : null;
    }
    public bool FileExists(Utf8String path)
    {
        var found = Lookup(path); return found.Ok && found.Entry is not DirectoryData
            && (found.Entry is FileData || found.FileSystem?.FileExists(found.Path) == true);
    }
    public bool DirectoryExists(Utf8String path)
    {
        var found = Lookup(path); return found.Ok && found.Entry is not FileData
            && (found.Entry is DirectoryData || found.FileSystem?.DirectoryExists(found.Path) == true);
    }
    public FileEntry? Stat(Utf8String path)
    {
        var found = Lookup(path); if (!found.Ok) return null;
        if (found.FileSystem is { } host)
        {
            if (host.Stat(found.Path) is { } info) return info;
            if (host.DirectoryExists(found.Path)) return new(CompilerPath.BaseName(found.Path), true, 0, default);
            return host.FileExists(found.Path) ? new(CompilerPath.BaseName(found.Path), false, 0, default) : null;
        }
        return found.Entry is { } entry ? new(CompilerPath.BaseName(entry.Name), entry is DirectoryData,
            entry is FileData file ? file.Content.Length : 0, default) : null;
    }
    public Utf8String RealPath(Utf8String path)
    {
        var found = Lookup(path); if (!found.Ok) return path;
        return found.FileSystem is { } host ? host.RealPath(found.Path) : found.Entry is not null || !found.Followed ? found.Path : path;
    }
    private Utf8String MutationPath(Utf8String path)
    {
        var resolved = Resolve(path);
        if (Kind != RequestFileSystemKind.Layer || !resolved.Ok) throw new IOException("Invalid request filesystem operation");
        return resolved.Path;
    }
    public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents) => BaseFileSystem.WriteFile(MutationPath(path), contents);
    public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents) => BaseFileSystem.AppendFile(MutationPath(path), contents);
    public void Remove(Utf8String path) => BaseFileSystem.Remove(MutationPath(path));
    public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc) => BaseFileSystem.SetTimes(MutationPath(path), accessTimeUtc, writeTimeUtc);

    public DirectoryEntries GetAccessibleEntries(Utf8String directoryName)
    {
        var found = Lookup(directoryName); if (!found.Ok || found.Entry is FileData) return new([], []);
        DirectoryEntries result;
        if (found.FileSystem is { } host) result = RemoveEntries(found.Path, host.GetAccessibleEntries(found.Path));
        else
        {
            var (node, _) = LookupNode(found.Path);
            bool explicitListing = node?.Entry is DirectoryData { Listing: not null };
            result = LocalEntries(node);
            if (Kind == RequestFileSystemKind.Layer && !explicitListing && !Blocks(directoryName) && !Blocks(found.Path))
                result = Merge(RemoveEntries(found.Path, BaseFileSystem.GetAccessibleEntries(found.Path)), result);
            if (node is not null)
            {
                foreach (var link in node.Children.Values.Select(child => child.Entry).OfType<LinkData>())
                {
                    var name = CompilerPath.BaseName(link.Name);
                    result = Filter(result, value => !comparer.Equals(name, value));
                    bool directory = DirectoryExists(link.Name);
                    if (!directory && !FileExists(link.Name)) continue;
                    result = directory ? new(result.Files, [.. result.Directories, name], result.SymbolicLinks)
                        : new([.. result.Files, name], result.Directories, result.SymbolicLinks);
                    result = result with { SymbolicLinks = new HashSet<Utf8String>(result.SymbolicLinks?.AsEnumerable() ?? [], comparer) { name } };
                }
                if (node.Children.Values.Any(child => child.Entry is LinkData)) result = Sorted(result);
            }
        }
        return Filter(result, name => LookupNode(CompilerPath.Combine(directoryName, name)).Node?.Entry is FileData or DirectoryData
            || !Blocks(CompilerPath.Combine(directoryName, name)));
    }
    private static DirectoryEntries LocalEntries(PathNode? node)
    {
        if (node?.Entry is not DirectoryData directory) return new([], []);
        if (directory.Listing is { } listing) return Copy(listing);
        return Sorted(new(node.Children.Values.Select(child => child.Entry).OfType<FileData>().Select(file => CompilerPath.BaseName(file.Name)).ToArray(),
            node.Children.Values.Select(child => child.Entry).OfType<DirectoryData>().Select(dir => CompilerPath.BaseName(dir.Name)).ToArray()));
    }
    private DirectoryEntries RemoveEntries(Utf8String path, DirectoryEntries entries) => Filter(entries, name => !Blocks(CompilerPath.Combine(path, name)));
    private static DirectoryEntries Copy(DirectoryEntries entries) => new([.. entries.Files], [.. entries.Directories],
        entries.SymbolicLinks is null ? null : new HashSet<Utf8String>(entries.SymbolicLinks));
    private static DirectoryEntries Filter(DirectoryEntries entries, Func<Utf8String, bool> predicate) =>
        new(entries.Files.Where(predicate).ToArray(), entries.Directories.Where(predicate).ToArray(),
            entries.SymbolicLinks?.Where(predicate).ToHashSet());
    private static DirectoryEntries Sorted(DirectoryEntries entries) => new(entries.Files.Order(Utf8StringComparer.Ordinal).ToArray(),
        entries.Directories.Order(Utf8StringComparer.Ordinal).ToArray(), entries.SymbolicLinks);
    private DirectoryEntries Merge(DirectoryEntries original, DirectoryEntries overlay)
    {
        var files = original.Files.ToList(); var directories = original.Directories.ToList();
        var links = new HashSet<Utf8String>(original.SymbolicLinks?.AsEnumerable() ?? [], comparer);
        foreach (var name in overlay.Files)
        {
            directories.RemoveAll(value => comparer.Equals(value, name));
            if (!files.Contains(name, comparer)) files.Add(name); links.Remove(name);
        }
        foreach (var name in overlay.Directories)
        {
            files.RemoveAll(value => comparer.Equals(value, name));
            if (!directories.Contains(name, comparer)) directories.Add(name); links.Remove(name);
        }
        links.UnionWith(overlay.SymbolicLinks?.AsEnumerable() ?? []);
        return Sorted(new(files.ToArray(), directories.ToArray(), links));
    }

    internal IReadOnlyList<DocumentSnapshot> FilterOverlays(IEnumerable<DocumentSnapshot> overlays) => overlays.Where(overlay =>
    {
        var found = Lookup(overlay.FileName);
        return found.FileSystem is not null && Path(found.Path) == Path(overlay.FileName);
    }).ToArray();

    public IReadOnlyList<Utf8String> GetAliases(Utf8String path, CancellationToken cancellation = default)
    {
        var links = Links().ToArray(); var seen = new HashSet<Utf8String> { Path(path) };
        var queue = new Queue<(Utf8String Path, HashSet<Utf8String> Links)>(); queue.Enqueue((Absolute(path), []));
        var result = new List<Utf8String>();
        while (queue.TryDequeue(out var candidate))
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var link in links)
            {
                // A host link terminates forward resolution. Without tracking the links already
                // used by an inverse path, a link to an ancestor expands forever here.
                var linkPath = Path(link.Name);
                if (candidate.Links.Contains(linkPath) || !TrySuffix(candidate.Path, link.Target, out var suffix)) continue;
                var alias = Absolute(CompilerPath.Combine(link.Name, suffix.TrimStart((byte)'/')));
                if (seen.Contains(Path(alias)) || !Resolve(alias).Ok) continue;
                seen.Add(Path(alias)); result.Add(alias); queue.Enqueue((alias, new(candidate.Links) { linkPath }));
            }
        }
        return result;
    }
    public void ExpandChanges(FileChangeSummary changes)
    {
        foreach (var set in new[] { changes.Changed, changes.Created, changes.Deleted })
            foreach (var path in set.ToArray()) set.UnionWith(GetAliases(path).Select(Path));
    }
    private void AddChanges(RequestFileSystemOptions options, IFileSystem original, FileChangeSummary changes)
    {
        void Add(Utf8String path, bool deleted)
        {
            if (deleted)
            {
                if (original.FileExists(path) || original.DirectoryExists(path)) changes.Deleted.Add(Path(path));
            }
            else (original.FileExists(path) ? changes.Changed : changes.Created).Add(Path(path));
        }
        IEnumerable<Utf8String> WithAliases(Utf8String path) => original is RequestFileSystem previous ? new[] { path }.Concat(previous.GetAliases(path)) : [path];
        void AddAliases(Utf8String path, bool deleted) { foreach (var alias in WithAliases(path)) Add(alias, deleted); }
        var files = options.Files.Keys.Select(Path).ToHashSet();
        foreach (var path in options.Files.Keys) AddAliases(Absolute(path), false);
        foreach (var path in options.RemovedPaths)
            if (!files.Contains(Path(path))) AddAliases(Absolute(path), true);
        foreach (var path in options.Directories.Keys.Concat(options.Symlinks.Keys))
        {
            var absolute = Absolute(path); AddAliases(absolute, true);
            foreach (var alias in WithAliases(absolute)) changes.Created.Add(Path(alias));
        }
        if (original is OverlayFileSystem overlays)
        {
            var preserved = FilterOverlays(overlays.Overlays.Values).Select(overlay => Path(overlay.FileName)).ToHashSet();
            foreach (var (path, overlay) in overlays.Overlays)
            {
                if (preserved.Contains(path) || changes.Closed.Contains(path)) continue;
                changes.Created.Remove(path);
                if (FileExists(overlay.FileName)) { changes.Deleted.Remove(path); changes.Changed.Add(path); }
                else { changes.Changed.Remove(path); changes.Deleted.Add(path); }
            }
        }
        if (changes.Changed.Count + changes.Created.Count + changes.Deleted.Count > 0) changes.IncludesWatchChangeOutsideNodeModules = true;
    }
}

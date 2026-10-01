namespace TypeScript.Compiler.Hosts;

public readonly record struct FileEntry(Utf8String Name, bool IsDirectory, long Length, DateTime LastWriteTimeUtc, bool IsSymbolicLink = false);
public readonly record struct DirectoryEntries(Utf8String[] Files, Utf8String[] Directories, IReadOnlySet<Utf8String>? SymbolicLinks = null);

public interface IFileSystem
{
    bool CaseSensitive { get; }

    bool FileExists(Utf8String path);

    bool DirectoryExists(Utf8String path);

    /// <summary>Returns an owned buffer whose ownership transfers to the caller.</summary>
    byte[]? ReadFile(Utf8String path);

    void WriteFile(Utf8String path, ReadOnlySpan<byte> contents);

    void AppendFile(Utf8String path, ReadOnlySpan<byte> contents);

    void Remove(Utf8String path);

    void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc);

    DirectoryEntries GetAccessibleEntries(Utf8String path);

    FileEntry? Stat(Utf8String path);

    Utf8String RealPath(Utf8String path);
}

public sealed class PhysicalFileSystem : IFileSystem
{
    public bool CaseSensitive { get; } = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();

    public bool FileExists(Utf8String path) => File.Exists(path.ToString());

    public bool DirectoryExists(Utf8String path) => Directory.Exists(path.ToString());

    public byte[]? ReadFile(Utf8String path)
    {
        try
        {
            return File.ReadAllBytes(path.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents)
    {
        Utf8String directory = CompilerPath.DirectoryName(path);
        if (!directory.IsEmpty)
            Directory.CreateDirectory(directory.ToString());
        File.WriteAllBytes(path.ToString(), contents);
    }

    public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents)
    {
        using FileStream stream = new(path.ToString(), FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(contents);
    }

    public void Remove(Utf8String path)
    {
        if (Directory.Exists(path.ToString()))
            Directory.Delete(path.ToString(), true);
        else
            File.Delete(path.ToString());
    }

    public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc)
    {
        File.SetLastAccessTimeUtc(path.ToString(), accessTimeUtc);
        File.SetLastWriteTimeUtc(path.ToString(), writeTimeUtc);
    }

    public DirectoryEntries GetAccessibleEntries(Utf8String path)
    {
        var files = new List<Utf8String>();
        var directories = new List<Utf8String>();
        var symlinks = new HashSet<Utf8String>(Utf8StringComparer.Ordinal);
        try
        {
            foreach (FileSystemInfo info in new DirectoryInfo(path.ToString()).EnumerateFileSystemInfos())
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    symlinks.Add(Utf8String.FromString(info.Name));
                ((info.Attributes & FileAttributes.Directory) != 0 ? directories : files).Add(Utf8String.FromString(info.Name));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        files.Sort(Utf8StringComparer.Ordinal);
        directories.Sort(Utf8StringComparer.Ordinal);
        return new(files.ToArray(), directories.ToArray(), symlinks);
    }

    public FileEntry? Stat(Utf8String path)
    {
        try
        {
            if (Directory.Exists(path.ToString()))
            {
                var info = new DirectoryInfo(path.ToString());
                return new(Utf8String.FromString(info.Name), true, 0, info.LastWriteTimeUtc, info.LinkTarget is not null);
            }
            var file = new FileInfo(path.ToString());
            return file.Exists ? new(Utf8String.FromString(file.Name), false, file.Length, file.LastWriteTimeUtc, file.LinkTarget is not null) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public Utf8String RealPath(Utf8String path)
    {
        try
        {
            if (!File.Exists(path.ToString()) && !Directory.Exists(path.ToString())) return path;
            Utf8String absolute = Utf8String.FromString(Path.GetFullPath(path.ToString()));
            Utf8String root = absolute[..CompilerPath.RootLength(absolute)];
            Utf8String current = root;
            // Resolve every component: resolving only the last misses links in parent directories.
            foreach (Utf8String component in absolute[root.Length..].Split((byte)Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                Utf8String next = CompilerPath.Combine(current, component);
                FileSystemInfo info = Directory.Exists(next.ToString()) ? new DirectoryInfo(next.ToString()) : new FileInfo(next.ToString());
                if (info.LinkTarget is not null)
                {
                    if (info.ResolveLinkTarget(true)?.FullName is { } target)
                        next = Utf8String.FromString(target);
                }
                else if (!CaseSensitive && Directory.Exists(current.ToString()))
                {
                    var actual = Directory.EnumerateFileSystemEntries(current.ToString()).FirstOrDefault(
                        p => Utf8StringComparer.OrdinalIgnoreCase.Equals(CompilerPath.BaseName(Utf8String.FromString(p)), component));
                    if (actual is not null)
                        next = Utf8String.FromString(actual);
                }
                current = next;
            }
            return CompilerPath.NormalizeSlashes(current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }
}

/// <summary>Owned file contents, directories and symbolic links with deterministic host enumeration.</summary>
public sealed class MemoryFileSystem : IFileSystem
{
    private readonly Dictionary<Utf8String, (byte[] Contents, DateTime Time)> files;
    private readonly HashSet<Utf8String> directories;
    private readonly Dictionary<Utf8String, Utf8String> links;
    private readonly object gate = new();
    private StringComparison Comparison => CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    public bool CaseSensitive { get; }
    public Utf8String CurrentDirectory { get; }

    public MemoryFileSystem(
        IEnumerable<KeyValuePair<Utf8String, byte[]>> initialFiles,
        bool caseSensitive = true,
        Utf8String currentDirectory = default,
        IEnumerable<KeyValuePair<Utf8String, Utf8String>>? symbolicLinks = null)
    {
        CaseSensitive = caseSensitive;
        CurrentDirectory = CompilerPath.Resolve(Utf8Literals.Slash, currentDirectory);
        var comparer = caseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
        files = new(comparer);
        directories = new(comparer);
        links = new(comparer);
        EnsureDirectories(CurrentDirectory);
        foreach (var entry in initialFiles)
        {
            Utf8String key = Key(entry.Key);
            if (directories.Contains(key))
                throw new IOException($"Path is a directory: {entry.Key}");
            EnsureDirectories(CompilerPath.DirectoryName(key));
            files[key] = (entry.Value.ToArray(), DateTime.UnixEpoch);
        }
        if (symbolicLinks is not null)
            foreach (var entry in symbolicLinks)
                CreateSymbolicLink(entry.Key, entry.Value);
    }

    private Utf8String Key(Utf8String path)
    {
        Utf8String key = CompilerPath.Resolve(CurrentDirectory, path);
        return key.Length > CompilerPath.RootLength(key) ? CompilerPath.RemoveTrailingSeparator(key) : key;
    }

    private void EnsureDirectories(Utf8String path)
    {
        var missing = new List<Utf8String>();
        while (true)
        {
            if (files.ContainsKey(path))
                throw new IOException($"Path is not a directory: {path}");
            if (directories.Contains(path))
                break;
            missing.Add(path);
            Utf8String parent = CompilerPath.DirectoryName(path);
            if (parent == path)
                break;
            path = parent;
        }
        foreach (Utf8String directory in missing)
            directories.Add(directory);
    }

    private Utf8String ResolveLinks(Utf8String path)
    {
        Utf8String key = Key(path);
        if (links.Count == 0)
            return key;
        var visited = new Dictionary<Utf8String, int>(links.Comparer);
        while (true)
        {
            bool changed = false;
            for (int end = CompilerPath.RootLength(key); end <= key.Length; end++)
            {
                if (end < key.Length && key[end] != '/')
                    continue;
                Utf8String prefix = key[..end];
                if (!links.TryGetValue(prefix, out Utf8String target))
                    continue;
                int remaining = key.Length - end;
                // Repeated directory aliases can consume another component (link/link/file).
                // A repeated link that consumes no remaining path is a genuine cycle.
                if (visited.TryGetValue(prefix, out int previousRemaining) && remaining >= previousRemaining)
                    throw new IOException($"Symbolic link cycle: {path}");
                visited[prefix] = remaining;
                key = CompilerPath.Resolve(target, key[end..].TrimStart((byte)'/'));
                changed = true;
                break;
            }
            if (!changed)
                return key;
        }
    }

    public void CreateDirectory(Utf8String path)
    {
        lock (gate)
            EnsureDirectories(ResolveLinks(path));
    }

    public void CreateSymbolicLink(Utf8String path, Utf8String target)
    {
        lock (gate)
        {
            Utf8String key = Key(path);
            if (files.ContainsKey(key) || directories.Contains(key) || links.ContainsKey(key))
                throw new IOException($"Path already exists: {path}");
            links[key] = CompilerPath.Resolve(CompilerPath.DirectoryName(key), target);
            EnsureDirectories(CompilerPath.DirectoryName(key));
        }
    }

    public bool FileExists(Utf8String path)
    {
        lock (gate)
            try
            {
                return files.ContainsKey(ResolveLinks(path));
            }
            catch (IOException)
            {
                return false;
            }
    }

    public bool DirectoryExists(Utf8String path)
    {
        lock (gate)
            try
            {
                return directories.Contains(ResolveLinks(path));
            }
            catch (IOException)
            {
                return false;
            }
    }

    public byte[]? ReadFile(Utf8String path)
    {
        lock (gate)
            try
            {
                return files.TryGetValue(ResolveLinks(path), out var file) ? file.Contents.ToArray() : null;
            }
            catch (IOException)
            {
                return null;
            }
    }

    public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents)
    {
        lock (gate)
        {
            Utf8String key = ResolveLinks(path);
            if (directories.Contains(key))
                throw new IOException($"Path is a directory: {path}");
            EnsureDirectories(CompilerPath.DirectoryName(key));
            files[key] = (contents.ToArray(), DateTime.UtcNow);
        }
    }

    public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents)
    {
        lock (gate)
        {
            Utf8String key = ResolveLinks(path);
            byte[] previous = files.TryGetValue(key, out var file) ? file.Contents : [];
            byte[] combined = new byte[checked(previous.Length + contents.Length)];
            previous.CopyTo(combined, 0);
            contents.CopyTo(combined.AsSpan(previous.Length));
            WriteFile(key, combined);
        }
    }

    public void Remove(Utf8String path)
    {
        lock (gate)
        {
            Utf8String key = Key(path);
            if (links.Remove(key))
                return;
            key = ResolveLinks(key);
            foreach (Utf8String file in files.Keys.Where(p => CompilerPath.Contains(key, p, CaseSensitive)).ToArray())
                files.Remove(file);
            foreach (Utf8String link in links.Keys.Where(p => CompilerPath.Contains(key, p, CaseSensitive)).ToArray())
                links.Remove(link);
            directories.RemoveWhere(p => CompilerPath.Contains(key, p, CaseSensitive));
        }
    }

    public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc)
    {
        lock (gate)
        {
            Utf8String key = ResolveLinks(path);
            if (!files.TryGetValue(key, out var file))
                throw new FileNotFoundException(path.ToString());
            files[key] = (file.Contents, writeTimeUtc);
        }
    }

    public DirectoryEntries GetAccessibleEntries(Utf8String path)
    {
        lock (gate)
        {
            Utf8String key;
            try
            {
                key = ResolveLinks(path);
            }
            catch (IOException)
            {
                return new([], []);
            }
            Utf8String prefix = CompilerPath.EnsureTrailingSeparator(key);
            var directFiles = new HashSet<Utf8String>(files.Comparer);
            var directDirectories = new HashSet<Utf8String>(files.Comparer);
            var symlinks = new HashSet<Utf8String>(files.Comparer);
            void Add(Utf8String entry, bool directory, bool link)
            {
                if (!entry.StartsWith(prefix, Comparison))
                    return;
                Utf8String rest = entry[prefix.Length..];
                if (rest.Length == 0 || rest.Contains((byte)'/'))
                    return;
                (directory ? directDirectories : directFiles).Add(rest);
                if (link)
                    symlinks.Add(rest);
            }
            foreach (Utf8String file in files.Keys)
                Add(file, false, false);
            foreach (Utf8String directory in directories)
                Add(directory, true, false);
            foreach (Utf8String link in links.Keys)
            {
                if (DirectoryExists(link))
                    Add(link, true, true);
                else if (FileExists(link))
                    Add(link, false, true);
            }
            return new(
                directFiles.Order(Utf8StringComparer.Ordinal).ToArray(),
                directDirectories.Order(Utf8StringComparer.Ordinal).ToArray(),
                symlinks);
        }
    }

    public FileEntry? Stat(Utf8String path)
    {
        lock (gate)
        {
            Utf8String key;
            try
            {
                key = ResolveLinks(path);
            }
            catch (IOException)
            {
                return null;
            }
            bool link = links.ContainsKey(Key(path));
            if (files.TryGetValue(key, out var file))
                return new(CompilerPath.BaseName(path), false, file.Contents.Length, file.Time, link);
            return directories.Contains(key) ? new(CompilerPath.BaseName(path), true, 0, DateTime.UnixEpoch, link) : null;
        }
    }

    public Utf8String RealPath(Utf8String path)
    {
        lock (gate)
        {
            Utf8String key;
            try
            {
                key = ResolveLinks(path);
            }
            catch (IOException)
            {
                return path;
            }
            if (files.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(key, out Utf8String actual, out _))
                return actual;
            return directories.TryGetValue(key, out actual) ? actual : path;
        }
    }
}

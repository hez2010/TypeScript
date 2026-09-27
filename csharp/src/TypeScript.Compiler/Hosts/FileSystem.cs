namespace TypeScript.Compiler.Hosts;

public readonly record struct FileEntry(string Name, bool IsDirectory, long Length, DateTime LastWriteTimeUtc, bool IsSymbolicLink = false);
public readonly record struct DirectoryEntries(string[] Files, string[] Directories, IReadOnlySet<string>? SymbolicLinks = null);

public interface IFileSystem
{
    bool CaseSensitive { get; }

    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>Returns an owned buffer whose ownership transfers to the caller.</summary>
    byte[]? ReadFile(string path);

    void WriteFile(string path, ReadOnlySpan<byte> contents);

    void AppendFile(string path, ReadOnlySpan<byte> contents);

    void Remove(string path);

    void SetTimes(string path, DateTime accessTimeUtc, DateTime writeTimeUtc);

    DirectoryEntries GetAccessibleEntries(string path);

    FileEntry? Stat(string path);

    string RealPath(string path);
}

public sealed class PhysicalFileSystem : IFileSystem
{
    public bool CaseSensitive { get; } = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public byte[]? ReadFile(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public void WriteFile(string path, ReadOnlySpan<byte> contents)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, contents);
    }

    public void AppendFile(string path, ReadOnlySpan<byte> contents)
    {
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(contents);
    }

    public void Remove(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);
        else
            File.Delete(path);
    }

    public void SetTimes(string path, DateTime accessTimeUtc, DateTime writeTimeUtc)
    {
        File.SetLastAccessTimeUtc(path, accessTimeUtc);
        File.SetLastWriteTimeUtc(path, writeTimeUtc);
    }

    public DirectoryEntries GetAccessibleEntries(string path)
    {
        var files = new List<string>();
        var directories = new List<string>();
        var symlinks = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (FileSystemInfo info in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    symlinks.Add(info.Name);
                ((info.Attributes & FileAttributes.Directory) != 0 ? directories : files).Add(info.Name);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        files.Sort(StringComparer.Ordinal);
        directories.Sort(StringComparer.Ordinal);
        return new(files.ToArray(), directories.ToArray(), symlinks);
    }

    public FileEntry? Stat(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                return new(info.Name, true, 0, info.LastWriteTimeUtc, info.LinkTarget is not null);
            }
            var file = new FileInfo(path);
            return file.Exists ? new(file.Name, false, file.Length, file.LastWriteTimeUtc, file.LinkTarget is not null) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public string RealPath(string path)
    {
        try
        {
            string absolute = Path.GetFullPath(path);
            string root = Path.GetPathRoot(absolute)!;
            string current = root;
            // Resolve every component: resolving only the last misses links in parent directories.
            foreach (string component in absolute[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                string next = Path.Combine(current, component);
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (info.LinkTarget is not null)
                    next = info.ResolveLinkTarget(true)?.FullName ?? next;
                else if (!CaseSensitive && Directory.Exists(current))
                {
                    string? actual = Directory.EnumerateFileSystemEntries(current).FirstOrDefault(
                        p => Path.GetFileName(p).Equals(component, StringComparison.OrdinalIgnoreCase));
                    if (actual is not null)
                        next = actual;
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
    private readonly Dictionary<string, (byte[] Contents, DateTime Time)> files;
    private readonly HashSet<string> directories;
    private readonly Dictionary<string, string> links;
    private readonly object gate = new();
    private StringComparison Comparison => CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    public bool CaseSensitive { get; }
    public string CurrentDirectory { get; }

    public MemoryFileSystem(
        IEnumerable<KeyValuePair<string, byte[]>> initialFiles,
        bool caseSensitive = true,
        string currentDirectory = "/",
        IEnumerable<KeyValuePair<string, string>>? symbolicLinks = null)
    {
        CaseSensitive = caseSensitive;
        CurrentDirectory = CompilerPath.Resolve("/", currentDirectory);
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        files = new(comparer);
        directories = new(comparer);
        links = new(comparer);
        EnsureDirectories(CurrentDirectory);
        foreach (var entry in initialFiles)
        {
            string key = Key(entry.Key);
            if (directories.Contains(key))
                throw new IOException("Path is a directory: " + entry.Key);
            EnsureDirectories(CompilerPath.DirectoryName(key));
            files[key] = (entry.Value.ToArray(), DateTime.UnixEpoch);
        }
        if (symbolicLinks is not null)
            foreach (var entry in symbolicLinks)
                CreateSymbolicLink(entry.Key, entry.Value);
    }

    private string Key(string path)
    {
        string key = CompilerPath.Resolve(CurrentDirectory, path);
        return key.Length > CompilerPath.RootLength(key) ? CompilerPath.RemoveTrailingSeparator(key) : key;
    }

    private void EnsureDirectories(string path)
    {
        var missing = new List<string>();
        while (true)
        {
            if (files.ContainsKey(path))
                throw new IOException("Path is not a directory: " + path);
            if (directories.Contains(path))
                break;
            missing.Add(path);
            string parent = CompilerPath.DirectoryName(path);
            if (parent == path)
                break;
            path = parent;
        }
        foreach (string directory in missing)
            directories.Add(directory);
    }

    private string ResolveLinks(string path)
    {
        string key = Key(path);
        if (links.Count == 0)
            return key;
        var visited = new Dictionary<string, int>(links.Comparer);
        while (true)
        {
            bool changed = false;
            for (int end = CompilerPath.RootLength(key); end <= key.Length; end++)
            {
                if (end < key.Length && key[end] != '/')
                    continue;
                string prefix = key[..end];
                if (!links.TryGetValue(prefix, out string? target))
                    continue;
                int remaining = key.Length - end;
                // Repeated directory aliases can consume another component (link/link/file).
                // A repeated link that consumes no remaining path is a genuine cycle.
                if (visited.TryGetValue(prefix, out int previousRemaining) && remaining >= previousRemaining)
                    throw new IOException("Symbolic link cycle: " + path);
                visited[prefix] = remaining;
                key = CompilerPath.Resolve(target, key[end..].TrimStart('/'));
                changed = true;
                break;
            }
            if (!changed)
                return key;
        }
    }

    public void CreateDirectory(string path)
    {
        lock (gate)
            EnsureDirectories(ResolveLinks(path));
    }

    public void CreateSymbolicLink(string path, string target)
    {
        lock (gate)
        {
            string key = Key(path);
            if (files.ContainsKey(key) || directories.Contains(key) || links.ContainsKey(key))
                throw new IOException("Path already exists: " + path);
            links[key] = CompilerPath.Resolve(CompilerPath.DirectoryName(key), target);
            EnsureDirectories(CompilerPath.DirectoryName(key));
        }
    }

    public bool FileExists(string path)
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

    public bool DirectoryExists(string path)
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

    public byte[]? ReadFile(string path)
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

    public void WriteFile(string path, ReadOnlySpan<byte> contents)
    {
        lock (gate)
        {
            string key = ResolveLinks(path);
            if (directories.Contains(key))
                throw new IOException("Path is a directory: " + path);
            EnsureDirectories(CompilerPath.DirectoryName(key));
            files[key] = (contents.ToArray(), DateTime.UtcNow);
        }
    }

    public void AppendFile(string path, ReadOnlySpan<byte> contents)
    {
        lock (gate)
        {
            string key = ResolveLinks(path);
            byte[] previous = files.TryGetValue(key, out var file) ? file.Contents : [];
            byte[] combined = new byte[checked(previous.Length + contents.Length)];
            previous.CopyTo(combined, 0);
            contents.CopyTo(combined.AsSpan(previous.Length));
            WriteFile(key, combined);
        }
    }

    public void Remove(string path)
    {
        lock (gate)
        {
            string key = Key(path);
            if (links.Remove(key))
                return;
            key = ResolveLinks(key);
            foreach (string file in files.Keys.Where(p => CompilerPath.Contains(key, p, CaseSensitive)).ToArray())
                files.Remove(file);
            foreach (string link in links.Keys.Where(p => CompilerPath.Contains(key, p, CaseSensitive)).ToArray())
                links.Remove(link);
            directories.RemoveWhere(p => CompilerPath.Contains(key, p, CaseSensitive));
        }
    }

    public void SetTimes(string path, DateTime accessTimeUtc, DateTime writeTimeUtc)
    {
        lock (gate)
        {
            string key = ResolveLinks(path);
            if (!files.TryGetValue(key, out var file))
                throw new FileNotFoundException(path);
            files[key] = (file.Contents, writeTimeUtc);
        }
    }

    public DirectoryEntries GetAccessibleEntries(string path)
    {
        lock (gate)
        {
            string key;
            try
            {
                key = ResolveLinks(path);
            }
            catch (IOException)
            {
                return new([], []);
            }
            string prefix = CompilerPath.EnsureTrailingSeparator(key);
            var directFiles = new HashSet<string>(files.Comparer);
            var directDirectories = new HashSet<string>(files.Comparer);
            var symlinks = new HashSet<string>(files.Comparer);
            void Add(string entry, bool directory, bool link)
            {
                if (!entry.StartsWith(prefix, Comparison))
                    return;
                string rest = entry[prefix.Length..];
                if (rest.Length == 0 || rest.Contains('/'))
                    return;
                (directory ? directDirectories : directFiles).Add(rest);
                if (link)
                    symlinks.Add(rest);
            }
            foreach (string file in files.Keys)
                Add(file, false, false);
            foreach (string directory in directories)
                Add(directory, true, false);
            foreach (string link in links.Keys)
            {
                if (DirectoryExists(link))
                    Add(link, true, true);
                else if (FileExists(link))
                    Add(link, false, true);
            }
            return new(
                directFiles.Order(StringComparer.Ordinal).ToArray(),
                directDirectories.Order(StringComparer.Ordinal).ToArray(),
                symlinks);
        }
    }

    public FileEntry? Stat(string path)
    {
        lock (gate)
        {
            string key;
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

    public string RealPath(string path)
    {
        lock (gate)
        {
            string key;
            try
            {
                key = ResolveLinks(path);
            }
            catch (IOException)
            {
                return Key(path);
            }
            if (files.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key, out string? actual, out _))
                return actual;
            return directories.TryGetValue(key, out actual) ? actual : key;
        }
    }
}

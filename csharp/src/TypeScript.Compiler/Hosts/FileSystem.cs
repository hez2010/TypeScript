using System.Collections.Frozen;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Hosts;

public sealed record FileEntry(string Name, bool IsDirectory, long Length, DateTime LastWriteTimeUtc, bool IsSymbolicLink = false);
public sealed record DirectoryEntries(string[] Files, string[] Directories, IReadOnlySet<string>? SymbolicLinks = null);

public interface IFileSystem
{
    bool CaseSensitive { get; }
    bool FileExists(string path);
    bool DirectoryExists(string path);
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
        try { return File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
    public void WriteFile(string path, ReadOnlySpan<byte> contents)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, contents);
    }
    public void AppendFile(string path, ReadOnlySpan<byte> contents)
    { using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read); stream.Write(contents); }
    public void Remove(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
        else File.Delete(path);
    }
    public void SetTimes(string path, DateTime accessTimeUtc, DateTime writeTimeUtc)
    { File.SetLastAccessTimeUtc(path, accessTimeUtc); File.SetLastWriteTimeUtc(path, writeTimeUtc); }
    public DirectoryEntries GetAccessibleEntries(string path)
    {
        var files = new List<string>(); var directories = new List<string>(); var symlinks = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (FileSystemInfo info in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) symlinks.Add(info.Name);
                ((info.Attributes & FileAttributes.Directory) != 0 ? directories : files).Add(info.Name);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        files.Sort(StringComparer.Ordinal); directories.Sort(StringComparer.Ordinal);
        return new(files.ToArray(), directories.ToArray(), symlinks);
    }
    public FileEntry? Stat(string path)
    {
        try
        {
            if (Directory.Exists(path))
            { var info = new DirectoryInfo(path); return new(info.Name, true, 0, info.LastWriteTimeUtc, info.LinkTarget is not null); }
            var file = new FileInfo(path);
            return file.Exists ? new(file.Name, false, file.Length, file.LastWriteTimeUtc, file.LinkTarget is not null) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
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
                if (info.LinkTarget is not null) next = info.ResolveLinkTarget(true)?.FullName ?? next;
                else if (!CaseSensitive && Directory.Exists(current))
                {
                    string? actual = Directory.EnumerateFileSystemEntries(current).FirstOrDefault(p => Path.GetFileName(p).Equals(component, StringComparison.OrdinalIgnoreCase));
                    if (actual is not null) next = actual;
                }
                current = next;
            }
            return CompilerPath.NormalizeSlashes(current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return path; }
    }
}

/// <summary>Immutable file contents with deterministic directory enumeration for compiler hosts and tests.</summary>
public sealed class MemoryFileSystem : IFileSystem
{
    private readonly Dictionary<string, (byte[] Contents, DateTime Time)> files;
    private readonly object gate = new();
    public bool CaseSensitive { get; }
    public string CurrentDirectory { get; }
    public MemoryFileSystem(IEnumerable<KeyValuePair<string, byte[]>> initialFiles, bool caseSensitive = true, string currentDirectory = "/")
    {
        CaseSensitive = caseSensitive; CurrentDirectory = currentDirectory;
        files = new(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var entry in initialFiles) files.Add(Key(entry.Key), (entry.Value.ToArray(), DateTime.UnixEpoch));
    }
    private string Key(string path) => CompilerPath.Resolve(CurrentDirectory, path);
    public bool FileExists(string path) { lock (gate) return files.ContainsKey(Key(path)); }
    public bool DirectoryExists(string path)
    { string prefix = CompilerPath.EnsureTrailingSeparator(Key(path)); lock (gate) return files.Keys.Any(p => p.StartsWith(prefix, CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)); }
    public byte[]? ReadFile(string path) { lock (gate) return files.TryGetValue(Key(path), out var file) ? file.Contents.ToArray() : null; }
    public void WriteFile(string path, ReadOnlySpan<byte> contents) { lock (gate) files[Key(path)] = (contents.ToArray(), DateTime.UtcNow); }
    public void AppendFile(string path, ReadOnlySpan<byte> contents)
    {
        lock (gate)
        {
            string key = Key(path);
            byte[] previous = files.TryGetValue(key, out var file) ? file.Contents : [];
            byte[] combined = new byte[checked(previous.Length + contents.Length)];
            previous.CopyTo(combined, 0); contents.CopyTo(combined.AsSpan(previous.Length));
            files[key] = (combined, DateTime.UtcNow);
        }
    }
    public void Remove(string path)
    { string key = Key(path); lock (gate) foreach (string file in files.Keys.Where(p => CompilerPath.Contains(key, p, CaseSensitive)).ToArray()) files.Remove(file); }
    public void SetTimes(string path, DateTime accessTimeUtc, DateTime writeTimeUtc)
    { lock (gate) { string key = Key(path); if (!files.TryGetValue(key, out var file)) throw new FileNotFoundException(path); files[key] = (file.Contents, writeTimeUtc); } }
    public DirectoryEntries GetAccessibleEntries(string path)
    {
        string prefix = CompilerPath.EnsureTrailingSeparator(Key(path));
        var directFiles = new HashSet<string>(files.Comparer); var directories = new HashSet<string>(files.Comparer);
        lock (gate)
        {
            foreach (string key in files.Keys)
            {
                if (!key.StartsWith(prefix, CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) continue;
                string rest = key[prefix.Length..]; int slash = rest.IndexOf('/');
                if (slash < 0) directFiles.Add(rest); else directories.Add(rest[..slash]);
            }
        }
        return new(directFiles.Order(StringComparer.Ordinal).ToArray(), directories.Order(StringComparer.Ordinal).ToArray());
    }
    public FileEntry? Stat(string path)
    {
        lock (gate)
        {
            if (files.TryGetValue(Key(path), out var file)) return new(CompilerPath.BaseName(path), false, file.Contents.Length, file.Time);
            return DirectoryExists(path) ? new(CompilerPath.BaseName(path), true, 0, DateTime.UnixEpoch) : null;
        }
    }
    public string RealPath(string path)
    {
        string key = Key(path);
        lock (gate) return files.Keys.FirstOrDefault(p => files.Comparer.Equals(p, key)) ?? key;
    }
}

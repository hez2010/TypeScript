using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Watching;

public enum WatchEventKind { Update = 1, Delete = 2 }
public enum WatchErrorKind { None, Overflow, Terminated, Error }
public readonly record struct WatchEvent(Utf8String Path, WatchEventKind Kind);
public sealed record WatchNotification(IReadOnlyList<WatchEvent> Events, WatchErrorKind Error = WatchErrorKind.None, Exception? Exception = null);
public sealed record WatchDirectoryRequest(Utf8String Directory, Action<WatchNotification> Callback, bool Recursive = false,
    Func<Utf8String, bool>? Ignore = null);

public interface IWatchBackend
{
    /// <summary>Creates the entire batch, or closes every new watch before throwing.</summary>
    IReadOnlyList<IDisposable> WatchDirectories(IReadOnlyList<WatchDirectoryRequest> requests);
}

public static class WatchPaths
{
    public static bool ShouldIgnore(Utf8String path)
    {
        path = CompilerPath.NormalizeSlashes(path);
        return path.EndsWith("/.git"u8, StringComparison.Ordinal) || path.Contains("/.git/"u8, StringComparison.Ordinal)
            || path.Contains("/node_modules/."u8, StringComparison.Ordinal) || path.Contains("/.#"u8, StringComparison.Ordinal);
    }

    public static bool CanWatchDirectory(Utf8String directory)
    {
        directory = CompilerPath.Normalize(directory);
        int rootLength = CompilerPath.RootLength(directory);
        var components = new List<Utf8String> { directory[..rootLength] };
        foreach (var part in directory.AsSpan(rootLength).Split((byte)'/'))
            if (part.End.Value > part.Start.Value) components.Add(Utf8String.Copy(directory.AsSpan(rootLength)[part]));
        return components.Count > 2 && components.Count > PerceivedRootLength(components) + 1;
    }

    internal static int PerceivedRootLength(IReadOnlyList<Utf8String> components)
    {
        if (components.Count <= 1) return 1;
        var root = components[0];
        int index = 1;
        bool dos = IsDrive(root);
        if (root != "/"u8 && !dos && components[1].Length >= 2 && IsLetter(components[1][0]) && components[1][^1] == '$')
        {
            if (components.Count == 2) return 2;
            index = 2; dos = true;
        }
        if (dos && (index >= components.Count || !components[index].Equals("users"u8, StringComparison.OrdinalIgnoreCase))) return index;
        if (index < components.Count && components[index].Equals("workspaces"u8, StringComparison.OrdinalIgnoreCase)) return index + 1;
        return index + 2;

        static bool IsDrive(Utf8String path) => path.Length >= 2 && IsLetter(path[0]) && path[1] == ':';
        static bool IsLetter(byte value) => value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';
    }
}

public sealed class DirectoryWatchSet(bool caseSensitive)
{
    private readonly Dictionary<Utf8String, bool> directories = new(Utf8StringComparer.Ordinal);
    public IReadOnlyDictionary<Utf8String, bool> Directories => directories;
    private Utf8String Canonical(Utf8String path) => caseSensitive ? path : path.ToLowerInvariant();

    public void Set(Utf8String directory, bool recursive)
    {
        directory = Canonical(directory);
        directories[directory] = directories.GetValueOrDefault(directory) || recursive;
    }

    public bool Covers(Utf8String directory)
    {
        directory = Canonical(directory);
        if (directories.ContainsKey(directory)) return true;
        int root = CompilerPath.RootLength(directory);
        while (directory.Length > root)
        {
            directory = CompilerPath.DirectoryName(directory);
            if (directories.GetValueOrDefault(directory)) return true;
        }
        return false;
    }
}

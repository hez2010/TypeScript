namespace TypeScript.Compiler.Hosts;

public static class FileMatcher
{
    /// <summary>Enumerates matching files in include order and deterministic directory order. A negative depth is unlimited.</summary>
    public static Utf8String[] ReadDirectory(IFileSystem fileSystem, Utf8String directory, Utf8String currentDirectory,
        IReadOnlyList<Utf8String>? extensions = null, IReadOnlyList<Utf8String>? excludes = null, IReadOnlyList<Utf8String>? includes = null,
        int depth = -1, CancellationToken cancellation = default)
    {
        directory = CompilerPath.Normalize(directory);
        Utf8String absolute = CompilerPath.Resolve(currentDirectory, directory);
        var includePatterns = (includes ?? []).Select(
            p => new FilePattern(CompilerPath.Resolve(absolute, p), fileSystem.CaseSensitive)).ToArray();
        var excludePatterns = (excludes ?? []).Select(
            p => new FilePattern(CompilerPath.Resolve(absolute, p), fileSystem.CaseSensitive, true)).ToArray();
        var roots = new List<Utf8String> { absolute };
        foreach (Utf8String candidate in includePatterns.Select(p => p.SearchRoot).Order(Utf8StringComparer.Ordinal))
            if (!roots.Any(root => CompilerPath.Contains(root, candidate, fileSystem.CaseSensitive)))
                roots.Add(candidate);
        var results = Enumerable.Range(0, Math.Max(1, includePatterns.Length)).Select(_ => new List<Utf8String>()).ToArray();
        var pending = new Stack<(Utf8String Path, int Depth)>();
        foreach (Utf8String root in roots.AsEnumerable().Reverse())
            pending.Push((root, depth));
        var visited = new HashSet<Utf8String>(fileSystem.CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(fileSystem.RealPath(item.Path)))
                continue;
            DirectoryEntries entries = fileSystem.GetAccessibleEntries(item.Path);
            foreach (Utf8String file in entries.Files)
            {
                if (extensions is { Count: > 0 } && !extensions.Any(e => file.EndsWith(e, StringComparison.Ordinal)))
                    continue;
                Utf8String path = CompilerPath.Combine(item.Path, file);
                if (excludePatterns.Any(p => p.Matches(path)))
                    continue;
                int index = includePatterns.Length == 0 ? 0 : Array.FindIndex(includePatterns, p => p.Matches(path));
                if (index >= 0)
                    results[index].Add(
                        CompilerPath.IsAbsolute(directory)
                            ? path
                            : CompilerPath.Relative(currentDirectory, path, fileSystem.CaseSensitive));
            }
            if (item.Depth == 1)
                continue;
            foreach (Utf8String child in entries.Directories.Reverse())
            {
                Utf8String path = CompilerPath.Combine(item.Path, child);
                if (!excludePatterns.Any(p => p.Matches(path, true))
                    && (includePatterns.Length == 0 || includePatterns.Any(p => p.Matches(path, true))))
                    pending.Push((path, item.Depth > 1 ? item.Depth - 1 : -1));
            }
        }
        return results.SelectMany(r => r).ToArray();
    }

    /// <summary>Directories that must be watched for new root files; true requests recursive watching.</summary>
    public static IReadOnlyDictionary<Utf8String, bool> WildcardDirectories(
        IEnumerable<Utf8String> includes,
        IEnumerable<Utf8String> excludes,
        Utf8String directory,
        bool caseSensitive)
    {
        var result = new Dictionary<Utf8String, bool>(caseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase);
        var excludePatterns = excludes.Select(p => new FilePattern(CompilerPath.Resolve(directory, p), caseSensitive, true)).ToArray();
        foreach (Utf8String include in includes)
        {
            Utf8String pattern = CompilerPath.Resolve(directory, include);
            if (excludePatterns.Any(p => p.Matches(pattern)))
                continue;
            int wildcard = pattern.AsSpan().IndexOfAny((byte)'*', (byte)'?');
            Utf8String path;
            bool recursive;
            if (wildcard >= 0)
            {
                int separator = pattern.LastIndexOf((byte)'/', wildcard);
                if (separator < 0)
                    continue;
                path = pattern[..separator];
                recursive = wildcard < pattern.LastIndexOf((byte)'/');
            }
            else
            {
                if (CompilerPath.BaseName(pattern).AsSpan().ContainsAny((byte)'.', (byte)'*', (byte)'?'))
                    continue;
                path = CompilerPath.RemoveTrailingSeparator(pattern);
                recursive = true;
            }
            result[path] = recursive || result.GetValueOrDefault(path);
        }
        foreach (Utf8String path in result.Keys.ToArray())
            if (result.Any(
                pair => pair.Value && !result.Comparer.Equals(pair.Key, path) && CompilerPath.Contains(pair.Key, path, caseSensitive)))
                result.Remove(path);
        return result;
    }
}

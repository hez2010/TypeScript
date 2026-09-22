namespace TypeScript.Compiler.Hosts;

public static class FileMatcher
{
    /// <summary>Enumerates matching files in include order and deterministic directory order. A negative depth is unlimited.</summary>
    public static string[] ReadDirectory(IFileSystem fileSystem, string directory, string currentDirectory,
        IReadOnlyList<string>? extensions = null, IReadOnlyList<string>? excludes = null, IReadOnlyList<string>? includes = null,
        int depth = -1, CancellationToken cancellation = default)
    {
        directory = CompilerPath.Normalize(directory);
        string absolute = CompilerPath.Resolve(currentDirectory, directory);
        var includePatterns = (includes ?? []).Select(
            p => new FilePattern(CompilerPath.Resolve(absolute, p), fileSystem.CaseSensitive)).ToArray();
        var excludePatterns = (excludes ?? []).Select(
            p => new FilePattern(CompilerPath.Resolve(absolute, p), fileSystem.CaseSensitive, true)).ToArray();
        var roots = new List<string> { absolute };
        foreach (string candidate in includePatterns.Select(p => p.SearchRoot).Order(StringComparer.Ordinal))
            if (!roots.Any(root => CompilerPath.Contains(root, candidate, fileSystem.CaseSensitive)))
                roots.Add(candidate);
        var results = Enumerable.Range(0, Math.Max(1, includePatterns.Length)).Select(_ => new List<string>()).ToArray();
        var pending = new Stack<(string Path, int Depth)>();
        foreach (string root in roots.AsEnumerable().Reverse())
            pending.Push((root, depth));
        var visited = new HashSet<string>(fileSystem.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(fileSystem.RealPath(item.Path)))
                continue;
            DirectoryEntries entries = fileSystem.GetAccessibleEntries(item.Path);
            foreach (string file in entries.Files)
            {
                if (extensions is { Count: > 0 } && !extensions.Any(e => file.EndsWith(e, StringComparison.Ordinal)))
                    continue;
                string path = CompilerPath.Combine(item.Path, file);
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
            foreach (string child in entries.Directories.Reverse())
            {
                string path = CompilerPath.Combine(item.Path, child);
                if (!excludePatterns.Any(p => p.Matches(path, true))
                    && (includePatterns.Length == 0 || includePatterns.Any(p => p.Matches(path, true))))
                    pending.Push((path, item.Depth > 1 ? item.Depth - 1 : -1));
            }
        }
        return results.SelectMany(r => r).ToArray();
    }

    /// <summary>Directories that must be watched for new root files; true requests recursive watching.</summary>
    public static IReadOnlyDictionary<string, bool> WildcardDirectories(
        IEnumerable<string> includes,
        IEnumerable<string> excludes,
        string directory,
        bool caseSensitive)
    {
        var result = new Dictionary<string, bool>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var excludePatterns = excludes.Select(p => new FilePattern(CompilerPath.Resolve(directory, p), caseSensitive, true)).ToArray();
        foreach (string include in includes)
        {
            string pattern = CompilerPath.Resolve(directory, include);
            if (excludePatterns.Any(p => p.Matches(pattern)))
                continue;
            int wildcard = pattern.AsSpan().IndexOfAny('*', '?');
            string path;
            bool recursive;
            if (wildcard >= 0)
            {
                int separator = pattern.LastIndexOf('/', wildcard);
                if (separator < 0)
                    continue;
                path = pattern[..separator];
                recursive = wildcard < pattern.LastIndexOf('/');
            }
            else
            {
                if (CompilerPath.BaseName(pattern).AsSpan().ContainsAny('.', '*', '?'))
                    continue;
                path = CompilerPath.RemoveTrailingSeparator(pattern);
                recursive = true;
            }
            result[path] = recursive || result.GetValueOrDefault(path);
        }
        foreach (string path in result.Keys.ToArray())
            if (result.Any(
                pair => pair.Value && !result.Comparer.Equals(pair.Key, path) && CompilerPath.Contains(pair.Key, path, caseSensitive)))
                result.Remove(path);
        return result;
    }
}

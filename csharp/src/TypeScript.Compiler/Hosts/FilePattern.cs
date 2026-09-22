using System.IO.Enumeration;

namespace TypeScript.Compiler.Hosts;

/// <summary>Compiler glob semantics: component wildcards, hidden/package exclusions and explicit minified files.</summary>
internal sealed class FilePattern
{
    private readonly string[] parts;
    private readonly bool[] wildcards;
    private readonly bool exclude;
    private readonly StringComparison comparison;
    public bool Valid { get; }
    public string SearchRoot { get; }

    public FilePattern(string pattern, bool caseSensitive, bool exclude = false)
    {
        pattern = CompilerPath.RemoveTrailingSeparator(CompilerPath.Normalize(pattern));
        this.exclude = exclude;
        comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int wildcard = pattern.AsSpan().IndexOfAny('*', '?');
        string last = CompilerPath.BaseName(pattern);
        bool implicitDirectory = !last.AsSpan().ContainsAny('.', '*', '?');
        SearchRoot = wildcard >= 0
            ? CompilerPath.DirectoryName(pattern[..wildcard])
            : implicitDirectory ? pattern : CompilerPath.DirectoryName(pattern);
        if (implicitDirectory)
            pattern = CompilerPath.Combine(pattern, "**/*");
        parts = Components(pattern);
        wildcards = new bool[parts.Length];
        Valid = exclude || parts[^1] != "**";
        for (int i = 0; i < parts.Length; i++)
            wildcards[i] = parts[i] != "**" && parts[i].AsSpan().ContainsAny('*', '?');
    }

    private static string[] Components(string path)
    {
        int root = CompilerPath.RootLength(path);
        return new[] { path[..root].TrimEnd('/') }.Concat(path[root..].Split('/', StringSplitOptions.RemoveEmptyEntries)).ToArray();
    }

    private static bool Package(string part) =>
        part.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || part.Equals("bower_components", StringComparison.OrdinalIgnoreCase)
            || part.Equals("jspm_packages", StringComparison.OrdinalIgnoreCase);

    public bool Matches(string path, bool directory = false)
    {
        if (!Valid)
            return false;
        bool[] active = new bool[parts.Length + 1];
        active[0] = true;
        foreach (string part in Components(CompilerPath.NormalizeSlashes(path)))
        {
            for (int i = 0; i < parts.Length; i++)
                if (active[i] && parts[i] == "**")
                    active[i + 1] = true;
            if (exclude && active[^1])
                return true;
            bool[] next = new bool[active.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!active[i])
                    continue;
                if (parts[i] == "**")
                {
                    if (exclude || !part.StartsWith('.') && !Package(part))
                        next[i] = true;
                }
                else if (wildcards[i])
                {
                    if (!exclude && (Package(part) || parts[i][0] is '*' or '?' && part.StartsWith('.')))
                        continue;
                    if (!exclude && !directory && part.EndsWith(".min.js", comparison) && !parts[i].Contains(".min.", comparison))
                        continue;
                    // Like the original TypeScript regular expressions, '?' consumes one
                    // UTF-16 code unit. Go permits its scalar-based matcher to differ here.
                    if (FileSystemName.MatchesSimpleExpression(parts[i], part, comparison == StringComparison.OrdinalIgnoreCase))
                        next[i + 1] = true;
                }
                else if (parts[i].Equals(part, comparison))
                    next[i + 1] = true;
            }
            active = next;
        }
        for (int i = 0; i < parts.Length; i++)
            if (active[i] && parts[i] == "**")
                active[i + 1] = true;
        return directory && !exclude ? active.Contains(true) : active[^1];
    }
}

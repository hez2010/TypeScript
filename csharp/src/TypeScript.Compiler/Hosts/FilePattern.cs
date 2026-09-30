using System.Text;

namespace TypeScript.Compiler.Hosts;

/// <summary>Compiler glob semantics: component wildcards, hidden/package exclusions and explicit minified files.</summary>
internal sealed class FilePattern
{
    private readonly Utf8String[] parts;
    private readonly bool[] wildcards;
    private readonly bool exclude;
    private readonly StringComparison comparison;
    public bool Valid { get; }
    public Utf8String SearchRoot { get; }

    public FilePattern(Utf8String pattern, bool caseSensitive, bool exclude = false)
    {
        pattern = CompilerPath.RemoveTrailingSeparator(CompilerPath.Normalize(pattern));
        this.exclude = exclude;
        comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int wildcard = pattern.AsSpan().IndexOfAny((byte)'*', (byte)'?');
        Utf8String last = CompilerPath.BaseName(pattern);
        bool implicitDirectory = !last.AsSpan().ContainsAny((byte)'.', (byte)'*', (byte)'?');
        SearchRoot = wildcard >= 0
            ? CompilerPath.DirectoryName(pattern[..wildcard])
            : implicitDirectory ? pattern : CompilerPath.DirectoryName(pattern);
        if (implicitDirectory)
            pattern = CompilerPath.Combine(pattern, Utf8Literals.RecursiveGlob);
        parts = Components(pattern);
        wildcards = new bool[parts.Length];
        Valid = exclude || parts[^1] != Utf8Literals.DoubleAsterisk;
        for (int i = 0; i < parts.Length; i++)
            wildcards[i] = parts[i] != Utf8Literals.DoubleAsterisk && parts[i].AsSpan().ContainsAny((byte)'*', (byte)'?');
    }

    private static Utf8String[] Components(Utf8String path)
    {
        int root = CompilerPath.RootLength(path);
        return new[] { path[..root].TrimEnd((byte)'/') }.Concat(path[root..].Split((byte)'/', StringSplitOptions.RemoveEmptyEntries)).ToArray();
    }

    private static bool Package(Utf8String part) =>
        part.Equals(Utf8Literals.NodeModules, StringComparison.OrdinalIgnoreCase)
            || part.Equals(Utf8Literals.BowerComponents, StringComparison.OrdinalIgnoreCase)
            || part.Equals(Utf8Literals.JspmPackages, StringComparison.OrdinalIgnoreCase);

    public bool Matches(Utf8String path, bool directory = false)
    {
        if (!Valid)
            return false;
        bool[] active = new bool[parts.Length + 1];
        active[0] = true;
        foreach (Utf8String part in Components(CompilerPath.NormalizeSlashes(path)))
        {
            for (int i = 0; i < parts.Length; i++)
                if (active[i] && parts[i] == Utf8Literals.DoubleAsterisk)
                    active[i + 1] = true;
            if (exclude && active[^1])
                return true;
            bool[] next = new bool[active.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!active[i])
                    continue;
                if (parts[i] == Utf8Literals.DoubleAsterisk)
                {
                    if (exclude || !part.StartsWith((byte)'.') && !Package(part))
                        next[i] = true;
                }
                else if (wildcards[i])
                {
                    if (!exclude && (Package(part) || parts[i][0] is (byte)'*' or (byte)'?' && part.StartsWith((byte)'.')))
                        continue;
                    if (!exclude && !directory && part.EndsWith(".min.js"u8, comparison) && !parts[i].Contains(".min."u8, comparison))
                        continue;
                    if (MatchComponent(parts[i], part))
                        next[i + 1] = true;
                }
                else if (parts[i].Equals(part, comparison))
                    next[i + 1] = true;
            }
            active = next;
        }
        for (int i = 0; i < parts.Length; i++)
            if (active[i] && parts[i] == Utf8Literals.DoubleAsterisk)
                active[i + 1] = true;
        return directory && !exclude ? active.Contains(true) : active[^1];
    }

    private bool MatchComponent(ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> value)
    {
        int p = 0, v = 0, star = -1, retry = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = ++p;
                retry = v;
                continue;
            }
            int point = Wtf8.Decode(value[v..], out int width);
            if (p < pattern.Length)
            {
                int expected = Wtf8.Decode(pattern[p..], out int patternWidth);
                if (expected == '?' || expected == point || comparison == StringComparison.OrdinalIgnoreCase
                    && Utf8StringComparer.Fold(expected) == Utf8StringComparer.Fold(point))
                {
                    p += patternWidth;
                    v += width;
                    continue;
                }
            }
            if (star < 0)
                return false;
            _ = Wtf8.Decode(value[retry..], out int retryWidth);
            v = retry += retryWidth;
            p = star;
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}

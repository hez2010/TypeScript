namespace TypeScript.Compiler.Hosts;

/// <summary>TypeScript paths include URLs, UNC roots and virtual files on every host OS.</summary>
public static class CompilerPath
{
    public static string NormalizeSlashes(string path) => path.Replace('\\', '/');
    public static bool IsUrl(string path) => EncodedRootLength(path) < 0;
    public static bool IsAbsolute(string path) => EncodedRootLength(path) != 0;
    public static bool IsDynamic(string path) => path.StartsWith("^/", StringComparison.Ordinal);
    public static bool HasTrailingSeparator(string path) => path.Length != 0 && path[^1] is '/' or '\\';
    public static int RootLength(string path) { int root = EncodedRootLength(path); return root < 0 ? ~root : root; }
    public static int EncodedRootLength(string path)
    {
        if (path.Length == 0) return 0;
        char first = path[0];
        if (first is '/' or '\\')
        {
            if (path.Length == 1 || path[1] != first) return 1;
            int next = path.IndexOf(first, 2);
            return next < 0 ? path.Length : next + 1;
        }
        if (char.IsAsciiLetter(first) && path.Length > 1 && path[1] == ':')
        { if (path.Length == 2) return 2; if (path[2] is '/' or '\\') return 3; }
        if (first == '^' && path.Length > 1 && path[1] == '/') return 2;
        int scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return 0;
        int authority = scheme + 3, end = path.IndexOf('/', authority);
        if (end < 0) return ~path.Length;
        if (path.AsSpan(0, scheme).SequenceEqual("file") && (end == authority || path.AsSpan(authority, end - authority).SequenceEqual("localhost")) && path.Length > end + 2 && char.IsAsciiLetter(path[end + 1]))
        {
            int separator = end + 2;
            int volumeEnd = path[separator] == ':' ? separator + 1 : path.AsSpan(separator).StartsWith("%3a", StringComparison.OrdinalIgnoreCase) ? separator + 3 : -1;
            if (volumeEnd == path.Length) return ~volumeEnd;
            if (volumeEnd >= 0 && path[volumeEnd] == '/') return ~(volumeEnd + 1);
        }
        return ~(end + 1);
    }
    public static string Combine(string first, params ReadOnlySpan<string> paths)
    {
        string result = NormalizeSlashes(first);
        foreach (string raw in paths)
        {
            if (raw.Length == 0) continue;
            string path = NormalizeSlashes(raw);
            result = result.Length == 0 || RootLength(path) != 0 ? path : EnsureTrailingSeparator(result) + path;
        }
        return result;
    }
    public static string Normalize(string path)
    {
        path = NormalizeSlashes(path);
        if (path.Length == 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') return path;
        int root = RootLength(path);
        var parts = new List<string>();
        foreach (Range range in path.AsSpan(root).Split('/'))
        {
            ReadOnlySpan<char> part = path.AsSpan(root)[range];
            if (part.IsEmpty || part.SequenceEqual(".")) continue;
            if (part.SequenceEqual(".."))
            {
                if (parts.Count > 0 && parts[^1] != "..") { parts.RemoveAt(parts.Count - 1); continue; }
                if (parts.Count == 0 && root != 0) continue;
            }
            parts.Add(part.ToString());
        }
        string prefix = root > 0 ? EnsureTrailingSeparator(path[..root]) : "";
        string result = prefix + string.Join('/', parts);
        return result.Length > 0 && HasTrailingSeparator(path) ? EnsureTrailingSeparator(result) : result;
    }
    public static string Resolve(string first, params ReadOnlySpan<string> paths) => Normalize(Combine(first, paths));
    public static string EnsureTrailingSeparator(string path) => HasTrailingSeparator(path) ? path : path + "/";
    public static string RemoveTrailingSeparator(string path) => HasTrailingSeparator(path) ? path[..^1] : path;
    public static string DirectoryName(string path)
    {
        path = NormalizeSlashes(path);
        int root = RootLength(path);
        if (root == path.Length) return path;
        path = RemoveTrailingSeparator(path);
        return path[..Math.Max(root, path.LastIndexOf('/'))];
    }
    public static string BaseName(string path)
    {
        path = NormalizeSlashes(path);
        int root = RootLength(path);
        if (root == path.Length) return "";
        path = RemoveTrailingSeparator(path);
        return path[Math.Max(root, path.LastIndexOf('/') + 1)..];
    }
    public static string Extension(string path)
    {
        string name = BaseName(path);
        int dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[dot..];
    }
    public static bool IsDeclarationFile(string path) => path.EndsWith(".d.ts", StringComparison.Ordinal) || path.EndsWith(".d.mts", StringComparison.Ordinal) || path.EndsWith(".d.cts", StringComparison.Ordinal) || path.EndsWith(".ts", StringComparison.Ordinal) && BaseName(path).Contains(".d.", StringComparison.Ordinal);
    public static bool Contains(string parent, string child, bool caseSensitive)
    {
        StringComparison comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (parent.Length == 0 || child.Length == 0) return false;
        parent = NormalizeSlashes(parent); child = NormalizeSlashes(child);
        if (!parent.AsSpan(0, RootLength(parent)).Equals(child.AsSpan(0, RootLength(child)), StringComparison.OrdinalIgnoreCase)) return false;
        parent = Normalize(parent); child = Normalize(child);
        string[] from = parent[RootLength(parent)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] target = child[RootLength(child)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (from.Length > target.Length) return false;
        for (int i = 0; i < from.Length; i++) if (!from[i].Equals(target[i], comparison)) return false;
        return true;
    }
    public static string Relative(string fromDirectory, string to, bool caseSensitive)
    {
        fromDirectory = NormalizeSlashes(fromDirectory); to = NormalizeSlashes(to);
        if (!fromDirectory.AsSpan(0, RootLength(fromDirectory)).Equals(to.AsSpan(0, RootLength(to)), StringComparison.OrdinalIgnoreCase)) return Normalize(to);
        fromDirectory = Normalize(fromDirectory); to = Normalize(to);
        StringComparison comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int fromRoot = RootLength(fromDirectory), toRoot = RootLength(to);
        if (!fromDirectory.AsSpan(0, fromRoot).Equals(to.AsSpan(0, toRoot), StringComparison.OrdinalIgnoreCase)) return to;
        string[] from = fromDirectory[fromRoot..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] target = to[toRoot..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        int common = 0;
        while (common < from.Length && common < target.Length && from[common].Equals(target[common], comparison)) common++;
        return string.Join('/', Enumerable.Repeat("..", from.Length - common).Concat(target.Skip(common)));
    }
}

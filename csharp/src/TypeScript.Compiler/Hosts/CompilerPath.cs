using System.Buffers;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Hosts;

/// <summary>TypeScript paths include URLs, UNC roots and virtual files on every host OS.</summary>
public static class CompilerPath
{
    public static string NormalizeSlashes(string path) => path.Replace('\\', '/');

    public static bool IsUrl(string path) => EncodedRootLength(path) < 0;

    public static bool IsAbsolute(string path) => EncodedRootLength(path) != 0;

    public static bool IsDynamic(string path) => path.StartsWith("^/", StringComparison.Ordinal);

    public static bool HasTrailingSeparator(string path) => path.Length != 0 && path[^1] is '/' or '\\';

    public static int RootLength(string path)
    {
        int root = EncodedRootLength(path);
        return root < 0 ? ~root : root;
    }

    public static int EncodedRootLength(ReadOnlySpan<char> path)
    {
        if (path.Length == 0)
            return 0;
        char first = path[0];
        if (first is '/' or '\\')
        {
            if (path.Length == 1 || path[1] != first)
                return 1;
            int next = path[2..].IndexOf(first);
            return next < 0 ? path.Length : next + 3;
        }
        if (char.IsAsciiLetter(first) && path.Length > 1 && path[1] == ':')
        {
            if (path.Length == 2)
                return 2;
            if (path[2] is '/' or '\\')
                return 3;
        }
        if (first == '^' && path.Length > 1 && path[1] == '/')
            return 2;
        int scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
            return 0;
        int authority = scheme + 3, end = path[authority..].IndexOf('/');
        if (end >= 0)
            end += authority;
        if (end < 0)
            return ~path.Length;
        if (path[..scheme].SequenceEqual("file")
            && (end == authority || path[authority..end].SequenceEqual("localhost"))
            && path.Length > end + 2
            && char.IsAsciiLetter(path[end + 1]))
        {
            int separator = end + 2;
            int volumeEnd = path[separator] == ':'
                ? separator + 1
                : path[separator..].StartsWith("%3a", StringComparison.OrdinalIgnoreCase) ? separator + 3 : -1;
            if (volumeEnd == path.Length)
                return ~volumeEnd;
            if (volumeEnd >= 0 && path[volumeEnd] == '/')
                return ~(volumeEnd + 1);
        }
        return ~(end + 1);
    }

    public static string Combine(string first, params ReadOnlySpan<string> paths)
    {
        string result = NormalizeSlashes(first);
        foreach (string raw in paths)
        {
            if (raw.Length == 0)
                continue;
            string path = NormalizeSlashes(raw);
            result = result.Length == 0 || RootLength(path) != 0 ? path : EnsureTrailingSeparator(result) + path;
        }
        return result;
    }

    public static string Normalize(string path)
    {
        path = NormalizeSlashes(path);
        if (path.Length == 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            return path;
        int root = RootLength(path);
        // Normalization cannot grow the path except to add a separator to a bare root.
        int capacity = checked(path.Length + 1);
        char[]? rented = null;
        Span<char> buffer = capacity <= 256 ? stackalloc char[256] : rented = ArrayPool<char>.Shared.Rent(capacity);
        try
        {
            path.AsSpan(0, root).CopyTo(buffer);
            int written = root;
            if (root > 0 && buffer[root - 1] is not ('/' or '\\'))
                buffer[written++] = '/';
            int bodyStart = written;
            ReadOnlySpan<char> body = path.AsSpan(root);
            foreach (Range range in body.Split('/'))
            {
                ReadOnlySpan<char> part = body[range];
                if (part is "" or ".")
                    continue;
                if (part is "..")
                {
                    if (written > bodyStart)
                    {
                        int previous = bodyStart + buffer[bodyStart..written].LastIndexOf('/') + 1;
                        if (!buffer[previous..written].SequenceEqual(".."))
                        {
                            written = Math.Max(bodyStart, previous - 1);
                            continue;
                        }
                    }
                    else if (root != 0)
                        continue;
                }
                if (written > bodyStart)
                    buffer[written++] = '/';
                part.CopyTo(buffer[written..]);
                written += part.Length;
            }
            if (written > 0 && HasTrailingSeparator(path) && buffer[written - 1] is not ('/' or '\\'))
                buffer[written++] = '/';
            ReadOnlySpan<char> normalized = buffer[..written];
            return normalized.SequenceEqual(path) ? path : normalized.ToString();
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    public static string Resolve(string first, params ReadOnlySpan<string> paths) => Normalize(Combine(first, paths));

    public static string EnsureTrailingSeparator(string path) => HasTrailingSeparator(path) ? path : path + "/";

    public static string RemoveTrailingSeparator(string path) => HasTrailingSeparator(path) ? path[..^1] : path;

    public static string DirectoryName(string path)
    {
        path = NormalizeSlashes(path);
        int root = RootLength(path);
        if (root == path.Length)
            return path;
        path = RemoveTrailingSeparator(path);
        return path[..Math.Max(root, path.LastIndexOf('/'))];
    }

    public static string BaseName(string path)
    {
        path = NormalizeSlashes(path);
        int root = RootLength(path);
        if (root == path.Length)
            return "";
        path = RemoveTrailingSeparator(path);
        return path[Math.Max(root, path.LastIndexOf('/') + 1)..];
    }

    public static string Extension(string path)
    {
        string name = BaseName(path);
        int dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[dot..];
    }

    public static TextSlice Extension(TextSlice path)
    {
        path = path.Replace('\\', '/');
        int root = EncodedRootLength(path.Span);
        if (root < 0)
            root = ~root;
        if (root == path.Length)
            return default;
        if (path[^1] == '/')
            path = path[..^1];
        int start = Math.Max(root, path.Span.LastIndexOf('/') + 1);
        int dot = path.Span[start..].LastIndexOf('.');
        return dot < 0 ? default : path[(start + dot)..];
    }

    public static bool IsDeclarationFile(string path) =>
        path.EndsWith(".d.ts", StringComparison.Ordinal)
            || path.EndsWith(".d.mts", StringComparison.Ordinal)
            || path.EndsWith(".d.cts", StringComparison.Ordinal)
            || path.EndsWith(".ts", StringComparison.Ordinal) && BaseName(path).Contains(".d.", StringComparison.Ordinal);

    public static bool Contains(string parent, string child, bool caseSensitive)
    {
        StringComparison comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (parent.Length == 0 || child.Length == 0)
            return false;
        parent = NormalizeSlashes(parent);
        child = NormalizeSlashes(child);
        if (!parent.AsSpan(0, RootLength(parent)).Equals(child.AsSpan(0, RootLength(child)), StringComparison.OrdinalIgnoreCase))
            return false;
        parent = Normalize(parent);
        child = Normalize(child);
        ReadOnlySpan<char> from = parent.AsSpan(RootLength(parent)).TrimEnd('/');
        ReadOnlySpan<char> target = child.AsSpan(RootLength(child)).TrimEnd('/');
        return from.IsEmpty || target.StartsWith(from, comparison)
            && (target.Length == from.Length || target.Length > from.Length && target[from.Length] == '/');
    }

    public static string Relative(string fromDirectory, string to, bool caseSensitive)
    {
        fromDirectory = NormalizeSlashes(fromDirectory);
        to = NormalizeSlashes(to);
        if (!fromDirectory.AsSpan(0, RootLength(fromDirectory)).Equals(to.AsSpan(0, RootLength(to)), StringComparison.OrdinalIgnoreCase))
            return Normalize(to);
        fromDirectory = Normalize(fromDirectory);
        to = Normalize(to);
        StringComparison comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int fromRoot = RootLength(fromDirectory), toRoot = RootLength(to);
        if (!fromDirectory.AsSpan(0, fromRoot).Equals(to.AsSpan(0, toRoot), StringComparison.OrdinalIgnoreCase))
            return to;
        ReadOnlySpan<char> from = fromDirectory.AsSpan(fromRoot).TrimEnd('/');
        ReadOnlySpan<char> target = to.AsSpan(toRoot).TrimEnd('/');
        int fromOffset = 0, targetOffset = 0;
        while (fromOffset < from.Length && targetOffset < target.Length)
        {
            int fromEnd = from[fromOffset..].IndexOf('/');
            fromEnd = fromEnd < 0 ? from.Length : fromOffset + fromEnd;
            int targetEnd = target[targetOffset..].IndexOf('/');
            targetEnd = targetEnd < 0 ? target.Length : targetOffset + targetEnd;
            if (!from[fromOffset..fromEnd].Equals(target[targetOffset..targetEnd], comparison))
                break;
            fromOffset = Math.Min(fromEnd + 1, from.Length);
            targetOffset = Math.Min(targetEnd + 1, target.Length);
        }
        int parents = fromOffset < from.Length ? from[fromOffset..].Count('/') + 1 : 0;
        int suffixLength = target.Length - targetOffset;
        int length = checked(parents * 3 + suffixLength - (parents > 0 && suffixLength == 0 ? 1 : 0));
        return string.Create(length, (to, Offset: toRoot + targetOffset, suffixLength, parents), static (output, state) =>
        {
            int written = 0;
            for (int i = 0; i < state.parents; i++)
            {
                output[written++] = '.';
                output[written++] = '.';
                if (written < output.Length)
                    output[written++] = '/';
            }
            state.to.AsSpan(state.Offset, state.suffixLength).CopyTo(output[written..]);
        });
    }
}

using System.Buffers;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Hosts;

/// <summary>TypeScript paths include URLs, UNC roots and virtual files on every host OS.</summary>
public static class CompilerPath
{
    private static bool IsAsciiLetter(int value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    public static Utf8String NormalizeSlashes(Utf8String path) => path.Replace((byte)'\\', (byte)'/');

    public static bool IsUrl(Utf8String path) => EncodedRootLength(path) < 0;

    public static bool IsAbsolute(Utf8String path) => EncodedRootLength(path) != 0;

    public static bool IsDynamic(Utf8String path) => path.StartsWith("^/"u8, StringComparison.Ordinal);

    public static Utf8String ResolveFileName(Utf8String currentDirectory, Utf8String fileName) =>
        IsDynamic(fileName) ? fileName : Resolve(currentDirectory, fileName);

    public static bool HasTrailingSeparator(Utf8String path) => path.Length != 0 && path[^1] is (byte)'/' or (byte)'\\';

    public static int RootLength(Utf8String path)
    {
        int root = EncodedRootLength(path);
        return root < 0 ? ~root : root;
    }

    public static int EncodedRootLength(ReadOnlySpan<byte> path)
    {
        if (path.Length == 0)
            return 0;
        int first = path[0];
        if (first is '/' or '\\')
        {
            if (path.Length == 1 || path[1] != first)
                return 1;
            int next = path[2..].IndexOf((byte)first);
            return next < 0 ? path.Length : next + 3;
        }
        if (IsAsciiLetter(first) && path.Length > 1 && path[1] == ':')
        {
            if (path.Length == 2)
                return 2;
            if (path[2] is (byte)'/' or (byte)'\\')
                return 3;
        }
        if (first == '^' && path.Length > 1 && path[1] == '/')
            return 2;
        int scheme = path.IndexOf("://"u8, StringComparison.Ordinal);
        if (scheme < 0)
            return 0;
        int authority = scheme + 3, end = path[authority..].IndexOf((byte)'/');
        if (end >= 0)
            end += authority;
        if (end < 0)
            return ~path.Length;
        if (path[..scheme].SequenceEqual("file"u8)
            && (end == authority || path[authority..end].SequenceEqual("localhost"u8))
            && path.Length > end + 2
            && IsAsciiLetter(path[end + 1]))
        {
            int separator = end + 2;
            int volumeEnd = path[separator] == ':'
                ? separator + 1
                : path[separator..].StartsWith("%3a"u8, StringComparison.OrdinalIgnoreCase) ? separator + 3 : -1;
            if (volumeEnd == path.Length)
                return ~volumeEnd;
            if (volumeEnd >= 0 && path[volumeEnd] == '/')
                return ~(volumeEnd + 1);
        }
        return ~(end + 1);
    }

    public static Utf8String Combine(Utf8String first, params ReadOnlySpan<Utf8String> paths)
    {
        Utf8String result = NormalizeSlashes(first);
        foreach (Utf8String raw in paths)
        {
            if (raw.Length == 0)
                continue;
            Utf8String path = NormalizeSlashes(raw);
            result = result.Length == 0 || RootLength(path) != 0 ? path : EnsureTrailingSeparator(result) + path;
        }
        return result;
    }

    public static Utf8String Normalize(Utf8String path)
    {
        path = NormalizeSlashes(path);
        if (path.Length == 2 && IsAsciiLetter(path[0]) && path[1] == ':')
            return path;
        int root = RootLength(path);
        // Normalization cannot grow the path except to add a separator to a bare root.
        int capacity = checked(path.Length + 1);
        byte[]? rented = null;
        Span<byte> buffer = capacity <= 256 ? stackalloc byte[256] : rented = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            path.AsSpan(0, root).CopyTo(buffer);
            int written = root;
            if (root > 0 && buffer[root - 1] is not ((byte)'/' or (byte)'\\'))
                buffer[written++] = (byte)'/';
            int bodyStart = written;
            ReadOnlySpan<byte> body = path.AsSpan(root);
            foreach (Range range in body.Split((byte)'/'))
            {
                ReadOnlySpan<byte> part = body[range];
                if (part.SequenceEqual(""u8) || part.SequenceEqual("."u8))
                    continue;
                if (part.SequenceEqual(".."u8))
                {
                    if (written > bodyStart)
                    {
                        int previous = bodyStart + buffer[bodyStart..written].LastIndexOf((byte)'/') + 1;
                        if (!buffer[previous..written].SequenceEqual(".."u8))
                        {
                            written = Math.Max(bodyStart, previous - 1);
                            continue;
                        }
                    }
                    else if (root != 0)
                        continue;
                }
                if (written > bodyStart)
                    buffer[written++] = (byte)'/';
                part.CopyTo(buffer[written..]);
                written += part.Length;
            }
            if (written > 0 && HasTrailingSeparator(path) && buffer[written - 1] is not ((byte)'/' or (byte)'\\'))
                buffer[written++] = (byte)'/';
            ReadOnlySpan<byte> normalized = buffer[..written];
            return normalized.SequenceEqual(path) ? path : Utf8String.Copy(normalized);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static Utf8String Resolve(Utf8String first, params ReadOnlySpan<Utf8String> paths) => Normalize(Combine(first, paths));

    public static Utf8String EnsureTrailingSeparator(Utf8String path) => HasTrailingSeparator(path) ? path : path + Utf8Literals.Slash;

    public static Utf8String RemoveTrailingSeparator(Utf8String path) => HasTrailingSeparator(path) ? path[..^1] : path;

    public static Utf8String DirectoryName(Utf8String path)
    {
        path = NormalizeSlashes(path);
        int root = RootLength(path);
        if (root == path.Length)
            return path;
        path = RemoveTrailingSeparator(path);
        return path[..Math.Max(root, path.LastIndexOf((byte)'/'))];
    }

    public static Utf8String BaseName(Utf8String path)
    {
        path = NormalizeSlashes(path);
        int root = RootLength(path);
        if (root == path.Length)
            return Utf8String.Empty;
        path = RemoveTrailingSeparator(path);
        return path[Math.Max(root, path.LastIndexOf((byte)'/') + 1)..];
    }

    public static Utf8String Extension(Utf8String path)
    {
        Utf8String name = BaseName(path);
        int dot = name.LastIndexOf((byte)'.');
        return dot < 0 ? Utf8String.Empty : name[dot..];
    }


    public static bool IsDeclarationFile(Utf8String path) =>
        path.EndsWith(".d.ts"u8, StringComparison.Ordinal)
            || path.EndsWith(".d.mts"u8, StringComparison.Ordinal)
            || path.EndsWith(".d.cts"u8, StringComparison.Ordinal)
            || path.EndsWith(".ts"u8, StringComparison.Ordinal) && BaseName(path).Contains(".d."u8, StringComparison.Ordinal);

    public static bool Contains(Utf8String parent, Utf8String child, bool caseSensitive)
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
        ReadOnlySpan<byte> from = parent.AsSpan(RootLength(parent)).TrimEnd((byte)'/');
        ReadOnlySpan<byte> target = child.AsSpan(RootLength(child)).TrimEnd((byte)'/');
        return from.IsEmpty || target.StartsWith(from, comparison)
            && (target.Length == from.Length || target.Length > from.Length && target[from.Length] == '/');
    }

    public static Utf8String Relative(Utf8String fromDirectory, Utf8String to, bool caseSensitive)
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
        ReadOnlySpan<byte> from = fromDirectory.AsSpan(fromRoot).TrimEnd((byte)'/');
        ReadOnlySpan<byte> target = to.AsSpan(toRoot).TrimEnd((byte)'/');
        int fromOffset = 0, targetOffset = 0;
        while (fromOffset < from.Length && targetOffset < target.Length)
        {
            int fromEnd = from[fromOffset..].IndexOf((byte)'/');
            fromEnd = fromEnd < 0 ? from.Length : fromOffset + fromEnd;
            int targetEnd = target[targetOffset..].IndexOf((byte)'/');
            targetEnd = targetEnd < 0 ? target.Length : targetOffset + targetEnd;
            if (!from[fromOffset..fromEnd].Equals(target[targetOffset..targetEnd], comparison))
                break;
            fromOffset = Math.Min(fromEnd + 1, from.Length);
            targetOffset = Math.Min(targetEnd + 1, target.Length);
        }
        int parents = fromOffset < from.Length ? from[fromOffset..].Count((byte)'/') + 1 : 0;
        int suffixLength = target.Length - targetOffset;
        int length = checked(parents * 3 + suffixLength - (parents > 0 && suffixLength == 0 ? 1 : 0));
        return Utf8String.Create(length, (to, Offset: toRoot + targetOffset, suffixLength, parents), static (output, state) =>
        {
            int written = 0;
            for (int i = 0; i < state.parents; i++)
            {
                output[written++] = (byte)'.';
                output[written++] = (byte)'.';
                if (written < output.Length)
                    output[written++] = (byte)'/';
            }
            state.to.AsSpan(state.Offset, state.suffixLength).CopyTo(output[written..]);
        });
    }
}

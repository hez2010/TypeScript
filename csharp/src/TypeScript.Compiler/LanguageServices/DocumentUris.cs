using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.LanguageServices;

public static class DocumentUris
{
    public static Utf8String ToFileName(Utf8String uri)
    {
        if (LibraryFileSystem.IsBundled(uri)) return uri;
        if (uri.StartsWith("file://"u8))
        {
            Utf8String rest = uri[7..];
            int suffix = rest.Span.IndexOfAny((byte)'?', (byte)'#');
            if (suffix >= 0) rest = rest[..suffix];
            int slash = rest.IndexOf((byte)'/');
            Utf8String authority = slash < 0 ? rest : rest[..slash];
            Utf8String path = slash < 0 ? Utf8String.Empty : Unescape(rest[slash..]);
            if (!authority.IsEmpty) return Utf8String.Concat("//"u8, Unescape(authority).Span, path.Span);
            if (path.Length >= 3 && path.Span[0] == '/' && Volume(path.Span[1]) && path.Span[2] == ':')
                return new Utf8StringBuilder(path.Length - 1).Append((byte)(path.Span[1] | 0x20)).Append(path.Span[2..]).ToUtf8String();
            return path;
        }
        int colon = uri.IndexOf((byte)':');
        if (colon <= 0) throw new ArgumentException("Invalid document URI", nameof(uri));
        Utf8String scheme = uri[..colon], value = uri[(colon + 1)..], host = "ts-nul-authority"u8;
        if (value.StartsWith("//"u8))
        {
            int separator = value.Span[2..].IndexOf((byte)'/');
            if (separator < 0) throw new ArgumentException("A document URI with an authority requires a path", nameof(uri));
            host = value[2..(separator + 2)];
            value = value[(separator + 3)..];
        }
        return Utf8String.ConcatMany("^/"u8, scheme, "/"u8, host, "/"u8, value);
    }

    public static Utf8String FromFileName(Utf8String fileName)
    {
        if (LibraryFileSystem.IsBundled(fileName)) return fileName;
        if (fileName.StartsWith("^/"u8))
        {
            int schemeEnd = fileName.Span[2..].IndexOf((byte)'/');
            if (schemeEnd < 0) throw new ArgumentException("Invalid dynamic file name", nameof(fileName));
            Utf8String scheme = fileName[2..(schemeEnd + 2)], rest = fileName[(schemeEnd + 3)..];
            int hostEnd = rest.IndexOf((byte)'/');
            if (hostEnd < 0) throw new ArgumentException("Invalid dynamic file name", nameof(fileName));
            Utf8String host = rest[..hostEnd], path = rest[(hostEnd + 1)..];
            return host == "ts-nul-authority"u8 ? Utf8String.ConcatMany(scheme, ":"u8, path)
                : Utf8String.ConcatMany(scheme, "://"u8, host, "/"u8, path);
        }
        var output = new Utf8StringBuilder("file://"u8);
        if (fileName.Length >= 2 && Volume(fileName.Span[0]) && fileName.Span[1] == ':')
        {
            output.Append('/').Append((byte)(fileName.Span[0] | 0x20)).Append("%3A"u8);
            fileName = fileName[2..];
        }
        if (fileName.StartsWith("//"u8)) fileName = fileName[2..];
        foreach (byte value in fileName.Span)
        {
            if (value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~' or (byte)'/')
                output.Append(value);
            else output.Append('%').Append("0123456789ABCDEF"u8[value >> 4]).Append("0123456789ABCDEF"u8[value & 15]);
        }
        return output.ToUtf8String();
    }

    private static bool Volume(byte value) => value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

    // System.Uri normalizes host spelling and converts through UTF-16. The wire path must retain WTF-8 bytes.
    private static Utf8String Unescape(Utf8String value)
    {
        var output = new Utf8StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            byte current = value.Span[i];
            if (current != '%') { output.Append(current); continue; }
            if (i + 2 >= value.Length || Hex(value.Span[i + 1]) < 0 || Hex(value.Span[i + 2]) < 0)
                throw new ArgumentException("Invalid URI escape", nameof(value));
            output.Append((byte)(Hex(value.Span[i + 1]) << 4 | Hex(value.Span[i + 2])));
            i += 2;
        }
        return output.ToUtf8String();
        static int Hex(byte value) => value is >= (byte)'0' and <= (byte)'9' ? value - '0'
            : (value | 0x20) is >= 'a' and <= 'f' ? (value | 0x20) - 'a' + 10 : -1;
    }
}

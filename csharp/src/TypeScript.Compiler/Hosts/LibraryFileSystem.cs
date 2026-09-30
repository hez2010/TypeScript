using System.Collections.Concurrent;
using System.Collections.Frozen;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Hosts;

public sealed class LibraryFileSystem(IFileSystem underlying) : IFileSystem
{
    public static readonly Utf8String Scheme = "bundled:///"u8;
    private static readonly Utf8String LibraryRoot = "bundled:///libs"u8;
    private static readonly Utf8String LibraryPrefix = "bundled:///libs/"u8;
#if EMBED_TYPESCRIPT_LIBRARIES
    public static bool Embedded => true;
    public Utf8String LibraryDirectory => LibraryRoot;
    private static readonly FrozenSet<Utf8String> Names = typeof(LibraryFileSystem).Assembly.GetManifestResourceNames()
        .Select(Utf8String.FromString).Where(
            n => n.StartsWith(
                "TypeScript.Libraries."u8,
                StringComparison.Ordinal)).Select(n => n["TypeScript.Libraries."u8.Length..]).ToFrozenSet(Utf8StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Utf8String, byte[]> Contents = new(Utf8StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Utf8String, SourceText> Sources = new(Utf8StringComparer.Ordinal);

    private static bool HasLibrary(Utf8String path)
    {
        if (CompilerPath.HasTrailingSeparator(path))
            return false;
        path = NormalizeBundled(path);
        return path.StartsWith(LibraryPrefix, StringComparison.Ordinal)
            && Names.GetAlternateLookup<ReadOnlySpan<byte>>().Contains(path.AsSpan(Scheme.Length + 5));
    }

    private static byte[]? Library(Utf8String path)
    {
        if (CompilerPath.HasTrailingSeparator(path))
            return null;
        path = NormalizeBundled(path);
        if (!HasLibrary(path))
            return null;
        Utf8String name = path[(Scheme.Length + 5)..];
        return Contents.GetOrAdd(name, static n =>
        {
            using Stream stream = typeof(LibraryFileSystem).Assembly.GetManifestResourceStream("TypeScript.Libraries." + n.ToString())!;
            byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
            stream.ReadExactly(bytes);
            return bytes;
        });
    }

#else
    public static bool Embedded => false;

    public Utf8String LibraryDirectory
    {
        get
        {
            Utf8String executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate compiler executable");
            Utf8String directory = CompilerPath.DirectoryName(underlying.RealPath(executable));
            if (!underlying.FileExists(CompilerPath.Combine(directory, "lib.d.ts")))
                throw new FileNotFoundException("Compiler libraries are missing beside the executable");
            return directory;
        }
    }

#endif
    public bool CaseSensitive => underlying.CaseSensitive;

    public static bool IsBundled(Utf8String path) => Embedded && path.StartsWith(Scheme, StringComparison.Ordinal);

    private static Utf8String NormalizeBundled(Utf8String path)
    {
        path = CompilerPath.Normalize(path);
        return path.Length > Scheme.Length ? CompilerPath.RemoveTrailingSeparator(path) : path;
    }

    public bool FileExists(Utf8String path)
    {
#if EMBED_TYPESCRIPT_LIBRARIES
        if (IsBundled(path))
            return HasLibrary(path);
#endif
        return underlying.FileExists(path);
    }

    internal SourceText? ReadBundledSource(Utf8String path)
    {
#if EMBED_TYPESCRIPT_LIBRARIES
        if (IsBundled(path) && Library(path) is { } bytes)
            return Sources.GetOrAdd(NormalizeBundled(path), static (_, bytes) =>
                SourceText.FromOwnedBytes(SourceEncoding.DecodeOwnedBytes(bytes)), bytes);
#endif
        return null;
    }

    public byte[]? ReadFile(Utf8String path)
    {
#if EMBED_TYPESCRIPT_LIBRARIES
        if (IsBundled(path))
            return Library(path)?.ToArray();
#endif
        return underlying.ReadFile(path);
    }

    public bool DirectoryExists(Utf8String path) => IsBundled(path)
        ? NormalizeBundled(path) is var normalized && (normalized == Scheme || normalized == LibraryRoot)
        : underlying.DirectoryExists(path);

    public DirectoryEntries GetAccessibleEntries(Utf8String path)
    {
#if EMBED_TYPESCRIPT_LIBRARIES
        if (IsBundled(path))
            return NormalizeBundled(path) switch
            {
                var normalized when normalized == Scheme => new([], ["libs"u8]),
                var normalized when normalized == LibraryRoot => new(Names.Order(Utf8StringComparer.Ordinal).ToArray(), []),
                _ => new([], [])
            };
#endif
        return underlying.GetAccessibleEntries(path);
    }

    public FileEntry? Stat(Utf8String path)
    {
#if EMBED_TYPESCRIPT_LIBRARIES
        if (IsBundled(path))
        {
            if (DirectoryExists(path))
                return new(CompilerPath.BaseName(path), true, 0, DateTime.MinValue);
            byte[]? bytes = Library(path);
            return bytes is null ? null : new(CompilerPath.BaseName(path), false, bytes.Length, DateTime.MinValue);
        }
#endif
        return underlying.Stat(path);
    }

    public Utf8String RealPath(Utf8String path) => IsBundled(path) ? NormalizeBundled(path) : underlying.RealPath(path);

    private static void Writable(Utf8String path)
    {
        if (IsBundled(path))
            throw new UnauthorizedAccessException("Bundled compiler libraries are read-only");
    }

    public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents)
    {
        Writable(path);
        underlying.WriteFile(path, contents);
    }

    public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents)
    {
        Writable(path);
        underlying.AppendFile(path, contents);
    }

    public void Remove(Utf8String path)
    {
        Writable(path);
        underlying.Remove(path);
    }

    public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc)
    {
        Writable(path);
        underlying.SetTimes(path, accessTimeUtc, writeTimeUtc);
    }
}

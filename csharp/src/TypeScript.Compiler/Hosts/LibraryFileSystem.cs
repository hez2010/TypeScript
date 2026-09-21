using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace TypeScript.Compiler.Hosts;

public sealed class LibraryFileSystem(IFileSystem underlying) : IFileSystem
{
    public const string Scheme = "bundled:///";
#if EMBED_TYPESCRIPT_LIBRARIES
    public static bool Embedded => true;
    public string LibraryDirectory => Scheme + "libs";
    private static readonly FrozenSet<string> Names = typeof(LibraryFileSystem).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("TypeScript.Libraries.", StringComparison.Ordinal)).Select(n => n["TypeScript.Libraries.".Length..]).ToFrozenSet(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte[]> Contents = new(StringComparer.Ordinal);
    private static bool HasLibrary(string path) => path.StartsWith(Scheme + "libs/", StringComparison.Ordinal)
        && Names.GetAlternateLookup<ReadOnlySpan<char>>().Contains(path.AsSpan(Scheme.Length + 5));
    private static byte[]? Library(string path)
    {
        if (!HasLibrary(path)) return null;
        string name = path[(Scheme.Length + 5)..];
        return Contents.GetOrAdd(name, static n =>
        {
            using Stream stream = typeof(LibraryFileSystem).Assembly.GetManifestResourceStream("TypeScript.Libraries." + n)!;
            byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
            stream.ReadExactly(bytes); return bytes;
        });
    }
#else
    public static bool Embedded => false;
    public string LibraryDirectory
    {
        get
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate compiler executable");
            string directory = CompilerPath.DirectoryName(underlying.RealPath(executable));
            if (!underlying.FileExists(CompilerPath.Combine(directory, "lib.d.ts"))) throw new FileNotFoundException("Compiler libraries are missing beside the executable");
            return directory;
        }
    }
#endif
    public bool CaseSensitive => underlying.CaseSensitive;
    public static bool IsBundled(string path) => Embedded && path.StartsWith(Scheme, StringComparison.Ordinal);
    public bool FileExists(string path)
    {
#if EMBED_TYPESCRIPT_LIBRARIES
        if (IsBundled(path)) return HasLibrary(path);
#endif
        return underlying.FileExists(path);
    }
    public byte[]? ReadFile(string path)
    {
#if EMBED_TYPESCRIPT_LIBRARIES
        if (IsBundled(path)) return Library(path)?.ToArray();
#endif
        return underlying.ReadFile(path);
    }
    public bool DirectoryExists(string path) => IsBundled(path) ? path is Scheme or Scheme + "libs" : underlying.DirectoryExists(path);
    public DirectoryEntries GetAccessibleEntries(string path)
    {
#if EMBED_TYPESCRIPT_LIBRARIES
        if (IsBundled(path)) return path switch { Scheme => new([], ["libs"]), Scheme + "libs" => new(Names.Order(StringComparer.Ordinal).ToArray(), []), _ => new([], []) };
#endif
        return underlying.GetAccessibleEntries(path);
    }
    public FileEntry? Stat(string path)
    {
#if EMBED_TYPESCRIPT_LIBRARIES
        if (IsBundled(path))
        {
            if (DirectoryExists(path)) return new(CompilerPath.BaseName(path), true, 0, DateTime.MinValue);
            byte[]? bytes = Library(path);
            return bytes is null ? null : new(CompilerPath.BaseName(path), false, bytes.Length, DateTime.MinValue);
        }
#endif
        return underlying.Stat(path);
    }
    public string RealPath(string path) => IsBundled(path) ? path : underlying.RealPath(path);
    private static void Writable(string path) { if (IsBundled(path)) throw new UnauthorizedAccessException("Bundled compiler libraries are read-only"); }
    public void WriteFile(string path, ReadOnlySpan<byte> contents) { Writable(path); underlying.WriteFile(path, contents); }
    public void AppendFile(string path, ReadOnlySpan<byte> contents) { Writable(path); underlying.AppendFile(path, contents); }
    public void Remove(string path) { Writable(path); underlying.Remove(path); }
    public void SetTimes(string path, DateTime accessTimeUtc, DateTime writeTimeUtc) { Writable(path); underlying.SetTimes(path, accessTimeUtc, writeTimeUtc); }
}

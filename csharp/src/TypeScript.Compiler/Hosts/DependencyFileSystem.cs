using System.Collections.Concurrent;

namespace TypeScript.Compiler.Hosts;

/// <summary>Records successful and failed reads so watch mode can invalidate module and package lookups.</summary>
internal sealed class DependencyFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly ConcurrentDictionary<Utf8String, byte> seen = new(Utf8StringComparer.Ordinal);
    internal IReadOnlyCollection<Utf8String> Paths => seen.Keys.ToArray();
    internal void Add(Utf8String path) => seen.TryAdd(path, 0);
    public bool CaseSensitive => inner.CaseSensitive;
    public bool FileExists(Utf8String path) { Add(path); return inner.FileExists(path); }
    public bool DirectoryExists(Utf8String path) { Add(path); return inner.DirectoryExists(path); }
    public byte[]? ReadFile(Utf8String path) { Add(path); return inner.ReadFile(path); }
    public DirectoryEntries GetAccessibleEntries(Utf8String path) { Add(path); return inner.GetAccessibleEntries(path); }
    public FileEntry? Stat(Utf8String path) { Add(path); return inner.Stat(path); }
    public Utf8String RealPath(Utf8String path) { Add(path); return inner.RealPath(path); }
    public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents) => inner.WriteFile(path, contents);
    public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents) => inner.AppendFile(path, contents);
    public void Remove(Utf8String path) => inner.Remove(path);
    public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc) => inner.SetTimes(path, accessTimeUtc, writeTimeUtc);
}

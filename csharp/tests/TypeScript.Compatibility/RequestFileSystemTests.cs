using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class RequestFileSystemTests
{
    internal static async Task<int> SafetyAsync()
    {
        int assertions = 0;
        void Check(bool condition, string reason) { assertions++; if (!condition) throw new InvalidOperationException(reason); }
        var disk = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        { ["/p/a.ts"u8] = "export const a = 1;"u8.ToArray(), ["/target/file.ts"u8] = "disk"u8.ToArray() });
        var overlayDocument = new DocumentSnapshot("/target/file.ts"u8, "overlay"u8, 3, TypeScript.Compiler.Syntax.ScriptKind.TSX, true, false);
        var overlay = new OverlayFileSystem(disk, "/"u8, [overlayDocument]);
        var request = RequestFileSystem.Create(new(RequestFileSystemKind.Layer)
        {
            Directories = new Dictionary<Utf8String, DirectoryEntries> { ["/target"u8] = new([], []) },
            Symlinks = new Dictionary<Utf8String, RequestSymlink> { ["/link"u8] = new("/target"u8) }
        }, disk, "/"u8);
        var layered = new OverlayFileSystem(request, "/"u8, overlay.Overlays.Values);
        Check(ReferenceEquals(layered.GetDocument("/target/file.ts"u8), overlayDocument), "Layering retains an unshadowed document handle");
        Check(ReferenceEquals(layered.GetDocument("/link/file.ts"u8), overlayDocument), "A request symlink retains its target overlay handle and language");
        Check(layered.GetAccessibleEntries("/target"u8).Files.Length == 0, "A complete request listing masks even a preserved overlay listing");
        var edited = layered.Apply([new(FileChangeKind.Change, "/target/file.ts"u8, 4,
            Edits: [new("changed"u8)])]);
        Check(edited.FileSystem.GetDocument("/link/file.ts"u8)?.Text == "changed"u8, "Edits are visible through request symlinks");
        Check(edited.Summary.Changed.SetEquals(new Utf8String[] { "/target/file.ts"u8, "/link/file.ts"u8 }), "Overlay changes invalidate request aliases");
        Check(layered.GetDocument("/link/file.ts"u8)?.Text == "overlay"u8, "Retained layered snapshots remain unchanged");
        var removed = RequestFileSystem.Create(new(RequestFileSystemKind.Layer) { RemovedPaths = ["/target"u8] }, overlay, "/"u8);
        var filtered = new OverlayFileSystem(removed, "/"u8, overlay.Overlays.Values);
        Check(filtered.Overlays.Count == 0 && !filtered.FileExists("/target/file.ts"u8), "A tombstone masks inherited overlays");
        var hostLink = RequestFileSystem.Create(new(RequestFileSystemKind.Full)
        {
            Symlinks = new Dictionary<Utf8String, RequestSymlink> { ["/link"u8] = new("/target"u8, true) }
        }, disk, "/"u8);
        Check(hostLink.ReadFile("/link/file.ts"u8)!.AsSpan().SequenceEqual("disk"u8), "An explicit host link is available from a full filesystem");
        Check(!hostLink.FileExists("/p/a.ts"u8), "A full filesystem cannot fall back to an unrelated host file");
        try { hostLink.WriteFile("/link/file.ts"u8, "bad"u8); throw new Exception("Full filesystem allowed mutation"); }
        catch (IOException) { assertions++; }
        var writable = RequestFileSystem.Create(new(RequestFileSystemKind.Layer)
        { Symlinks = new Dictionary<Utf8String, RequestSymlink> { ["/link"u8] = new("/target"u8) } }, disk, "/"u8);
        writable.WriteFile("/link/file.ts"u8, "written"u8); writable.AppendFile("/link/file.ts"u8, "+"u8);
        Check(disk.ReadFile("/target/file.ts"u8)!.AsSpan().SequenceEqual("written+"u8), "Layer mutations route to the resolved host path");
        var time = DateTime.UnixEpoch.AddSeconds(123); writable.SetTimes("/link/file.ts"u8, time, time);
        Check(disk.Stat("/target/file.ts"u8)?.LastWriteTimeUtc == time, "Timestamp mutations use the resolved path");
        writable.Remove("/link/file.ts"u8);
        Check(!disk.FileExists("/target/file.ts"u8), "Removals use the resolved path");
        var recursiveHost = RequestFileSystem.Create(new(RequestFileSystemKind.Full)
        { Symlinks = new Dictionary<Utf8String, RequestSymlink> { ["/target/link"u8] = new("/target"u8, true) } }, disk, "/"u8);
        Check(recursiveHost.GetAliases("/target/file.ts"u8).SequenceEqual(new Utf8String[] { "/target/link/file.ts"u8 }),
            "A host link to an ancestor cannot generate an infinite inverse alias chain");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { recursiveHost.GetAliases("/target/file.ts"u8, canceled.Token); throw new Exception("Alias expansion ignored cancellation"); }
            catch (OperationCanceledException) { assertions++; }
        }
        var original = RequestFileSystem.Create(new(RequestFileSystemKind.Full)
        { Files = new Dictionary<Utf8String, Utf8String> { ["/retained.ts"u8] = "retained"u8 } }, disk, "/"u8);
        var previous = original;
        for (int i = 0; i < 200; i++) previous = RequestFileSystem.Create(new(RequestFileSystemKind.Layer)
        { Files = new Dictionary<Utf8String, Utf8String> { ["/latest.ts"u8] = Utf8String.Format(i) } }, previous, "/"u8);
        Check(ReferenceEquals(previous.BaseFileSystem, disk) && previous.Kind == RequestFileSystemKind.Full, "Layers compact without retaining a filesystem chain");
        Check(original.ReadFile("/latest.ts"u8) is null && previous.FileExists("/retained.ts"u8), "Compaction is persistent and retains inherited entries");
        await using var host = new ProjectSnapshotHost(disk);
        var compilerOptions = new CompilerOptions(); compilerOptions.SetRaw("noLib"u8, "true"u8);
        await using var before = await host.CreateAsync(new() { CreatePrograms = [new(["/p/a.ts"u8], compilerOptions)] });
        var changes = new FileChangeSummary();
        var replacement = RequestFileSystem.Create(new(RequestFileSystemKind.Layer)
        { Files = new Dictionary<Utf8String, Utf8String> { ["/p/a.ts"u8] = "export const a = 2;"u8 } }, disk, "/"u8, changes);
        await using var after = await host.CreateAsync(new()
        { FileSystem = replacement, FileSystemChanges = changes, EnsureAllPrograms = true }, before);
        Check(after.CreatedPrograms.Count == 0 && after.Projects[0].Program!.GetFile("/p/a.ts"u8)?.Syntax.Source.Text == "export const a = 2;"u8,
            "API filesystem changes invalidate an ensured program");
        Check(before.Projects[0].Program!.GetFile("/p/a.ts"u8)?.Syntax.Source.Text == "export const a = 1;"u8,
            "A retained program keeps its original source after a filesystem layer");
        return assertions;
    }
    internal static void Lines()
    {
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            using var input = JsonDocument.Parse(line);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output)) Run(input.RootElement, writer);
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        }
    }

    internal static RequestFileSystemOptions ReadOptions(JsonElement input)
    {
        var kind = input.GetProperty("kind").GetString() switch
        {
            "full" => RequestFileSystemKind.Full,
            "layer" => RequestFileSystemKind.Layer,
            _ => (RequestFileSystemKind)(-1)
        };
        var files = ReadFiles(input, "files");
        var dirs = new Dictionary<Utf8String, DirectoryEntries>();
        if (input.TryGetProperty("directories", out var entries) && entries.ValueKind == JsonValueKind.Object)
            foreach (var entry in entries.EnumerateObject()) dirs.Add(Utf8String.FromString(entry.Name),
                new(Strings(entry.Value, "files"), Strings(entry.Value, "directories")));
        var links = new Dictionary<Utf8String, RequestSymlink>();
        if (input.TryGetProperty("symlinks", out var symlinks) && symlinks.ValueKind == JsonValueKind.Object)
            foreach (var link in symlinks.EnumerateObject()) links.Add(Utf8String.FromString(link.Name),
                new(JsonStrings.GetString(link.Value.GetProperty("target")), link.Value.TryGetProperty("host", out var host) && host.GetBoolean()));
        return new(kind) { Files = files, Directories = dirs, Symlinks = links, RemovedPaths = Strings(input, "removedPaths") };
    }
    private static Dictionary<Utf8String, Utf8String> ReadFiles(JsonElement input, string name)
    {
        var files = new Dictionary<Utf8String, Utf8String>();
        if (input.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Object)
            foreach (var value in values.EnumerateObject()) files.Add(Utf8String.FromString(value.Name), JsonStrings.GetString(value.Value));
        return files;
    }
    private static Utf8String[] Strings(JsonElement input, string name) => input.TryGetProperty(name, out var values)
        && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Select(JsonStrings.GetString).ToArray() : [];
    private static void Run(JsonElement input, Utf8JsonWriter writer)
    {
        var cwd = input.TryGetProperty("cwd", out var directory) ? JsonStrings.GetString(directory) : (Utf8String)"/"u8;
        bool caseSensitive = !input.TryGetProperty("caseSensitive", out var sensitive) || sensitive.GetBoolean();
        var files = ReadFiles(input, "files"); var links = ReadFiles(input, "symlinks");
        var disk = new MemoryFileSystem(files.Select(pair => KeyValuePair.Create(pair.Key, pair.Value.Span.ToArray())), caseSensitive, cwd, links);
        foreach (var path in Strings(input, "directories")) disk.CreateDirectory(path);
        var systems = new List<IFileSystem?> { new FixtureHost(disk) };
        writer.WriteStartArray();
        foreach (var step in input.GetProperty("steps").EnumerateArray())
        {
            int parent = step.TryGetProperty("base", out var basis) ? basis.GetInt32() : systems.Count - 1;
            var summary = new FileChangeSummary(caseSensitive);
            try
            {
                IFileSystem source = systems[parent] ?? throw new ArgumentException("Failed parent");
                var result = RequestFileSystem.Create(ReadOptions(step.GetProperty("request")), source, cwd, summary);
                systems.Add(result);
                writer.WriteStartObject();
                writer.WriteString("kind", result.Kind == RequestFileSystemKind.Full ? "full" : "layer");
                writer.WriteBoolean("compacted", result.BaseFileSystem is not RequestFileSystem);
                WriteSummary(writer, summary);
                writer.WritePropertyName("queries"); writer.WriteStartArray();
                foreach (var path in Strings(step, "queries")) WriteQuery(writer, result, path);
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            catch (ArgumentException)
            {
                systems.Add(null); writer.WriteStartObject(); writer.WriteBoolean("error", true); writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();
    }
    private static void WriteSummary(Utf8JsonWriter writer, FileChangeSummary summary)
    {
        writer.WritePropertyName("changes"); writer.WriteStartObject();
        WriteStrings(writer, "changed", summary.Changed.Order(Utf8StringComparer.Ordinal));
        WriteStrings(writer, "created", summary.Created.Order(Utf8StringComparer.Ordinal));
        WriteStrings(writer, "deleted", summary.Deleted.Order(Utf8StringComparer.Ordinal));
        writer.WriteBoolean("invalidateAll", summary.InvalidateAll);
        writer.WriteBoolean("outsideNodeModules", summary.IncludesWatchChangeOutsideNodeModules);
        writer.WriteEndObject();
    }
    private static void WriteQuery(Utf8JsonWriter writer, RequestFileSystem fs, Utf8String path)
    {
        writer.WriteStartObject(); writer.WriteBoolean("file", fs.FileExists(path)); writer.WriteBoolean("directory", fs.DirectoryExists(path));
        writer.WritePropertyName("content");
        if (fs.ReadFile(path) is { } bytes) JsonStrings.WriteString(writer, bytes); else writer.WriteNullValue();
        writer.WritePropertyName("realpath"); JsonStrings.WriteString(writer, fs.RealPath(path).Span);
        var entries = fs.GetAccessibleEntries(path);
        WriteStrings(writer, "files", entries.Files); WriteStrings(writer, "directories", entries.Directories);
        WriteStrings(writer, "symlinks", (entries.SymbolicLinks?.AsEnumerable() ?? []).Order(Utf8StringComparer.Ordinal));
        writer.WritePropertyName("stat");
        if (fs.Stat(path) is { } info)
        {
            writer.WriteStartObject(); writer.WritePropertyName("name"); JsonStrings.WriteString(writer, info.Name.Span);
            writer.WriteBoolean("directory", info.IsDirectory); writer.WriteNumber("size", info.Length); writer.WriteEndObject();
        }
        else writer.WriteNullValue();
        WriteStrings(writer, "aliases", fs.GetAliases(path).Order(Utf8StringComparer.Ordinal));
        writer.WriteEndObject();
    }
    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<Utf8String> values)
    {
        writer.WritePropertyName(name); writer.WriteStartArray();
        foreach (var value in values) JsonStrings.WriteString(writer, value.Span);
        writer.WriteEndArray();
    }

    // Go's vfstest Stat reports the stored spelling and follows the final link.
    // The compiler MemoryFileSystem reports the requested basename.
    private sealed class FixtureHost(MemoryFileSystem inner) : IFileSystem
    {
        public bool CaseSensitive => inner.CaseSensitive;
        public bool FileExists(Utf8String path) => inner.FileExists(path);
        public bool DirectoryExists(Utf8String path) => inner.DirectoryExists(path);
        public byte[]? ReadFile(Utf8String path) => inner.ReadFile(path);
        public Utf8String RealPath(Utf8String path) => inner.RealPath(path);
        public DirectoryEntries GetAccessibleEntries(Utf8String path) => inner.GetAccessibleEntries(path);
        public FileEntry? Stat(Utf8String path) => inner.Stat(path) is { } entry ? entry with { Name = CompilerPath.BaseName(inner.RealPath(path)) } : null;
        public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents) => inner.WriteFile(path, contents);
        public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents) => inner.AppendFile(path, contents);
        public void Remove(Utf8String path) => inner.Remove(path);
        public void SetTimes(Utf8String path, DateTime access, DateTime write) => inner.SetTimes(path, access, write);
    }
}

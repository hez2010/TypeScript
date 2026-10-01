using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Execution;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Text;
using TypeScript.Compiler.Watching;

namespace TypeScript.Compatibility;

internal static class ScriptedBuildTests
{
    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            try { Console.WriteLine(ExecuteAsync(document.RootElement).GetAwaiter().GetResult()); }
            catch (Exception error)
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                { writer.WriteStartObject(); writer.WriteString("error", error.Message); writer.WriteString("stack", error.StackTrace); writer.WriteEndObject(); }
                Console.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
            }
        }
    }

    private static async Task<string> ExecuteAsync(JsonElement input)
    {
        const uint directoryMode = 2147483648, linkMode = 134217728;
        uint Mode(JsonElement file) => file.GetProperty("mode").GetUInt32();
        byte[] Bytes(JsonElement file) => file.GetProperty("dataBase64").ValueKind == JsonValueKind.Null ? [] : file.GetProperty("dataBase64").GetBytesFromBase64();
        bool Regular(JsonElement file) => (Mode(file) & (directoryMode | linkMode)) == 0;
        DateTime Modified(JsonElement file) => file.GetProperty("modified").GetDateTime().ToUniversalTime();
        var cwd = JsonStrings.GetString(input.GetProperty("cwd"));
        bool sensitive = !input.GetProperty("caseInsensitive").GetBoolean();
        var initial = input.GetProperty("files").EnumerateObject().ToArray();
        var memory = new MemoryFileSystem(initial.Where(file => Regular(file.Value)).ToDictionary(JsonStrings.GetName, file => Bytes(file.Value)), sensitive, cwd);
        foreach (var file in initial.Where(file => (Mode(file.Value) & directoryMode) != 0)) memory.CreateDirectory(JsonStrings.GetName(file));
        foreach (var file in initial.Where(file => (Mode(file.Value) & linkMode) != 0))
        {
            var target = new Utf8String(Bytes(file.Value));
            if (!CompilerPath.IsAbsolute(target)) target = "/"u8 + target;
            memory.CreateSymbolicLink(JsonStrings.GetName(file), target);
        }
        long ticks = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        DateTime Now() => new(Interlocked.Add(ref ticks, TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        foreach (var file in initial.Where(file => Regular(file.Value)).OrderBy(file => Modified(file.Value)).ThenBy(JsonStrings.GetName, Utf8StringComparer.Ordinal))
        { var stamp = Now(); memory.SetTimes(JsonStrings.GetName(file), stamp, stamp); }
        var fs = new WatchTransitionTests.TrackingFileSystem(new BuildInfoFileSystem(new LibraryFileSystem(memory)), Now);
        var backend = new WatchTests.MockBackend { CaseSensitive = sensitive, DirectoryExists = fs.DirectoryExists };
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        string? EnvironmentValue(string name) => input.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Object && env.TryGetProperty(name, out var value) ? value.GetString() : null;
        await using var mappers = new ContentMapperHost(log: value => stderr.Write(value.ToString()))
        {
            StartProcess = start =>
            {
                var fixture = Environment.GetEnvironmentVariable("CSHARP_CONTENT_MAPPER_FIXTURE") ?? throw new InvalidOperationException("The pinned mapper fixture was not supplied.");
                var executable = start.FileName;
                // The Go test spawner rejects unknown commands before starting a process.
                if (executable is not ("compiler-test-mapper" or "verbatim-mapper" or "module-verbatim-mapper" or "dynamic-verbatim-mapper"
                    or "diagnostic-code-collision-mapper" or "failing-mapper" or "synthesizing-mapper" or "component-mapper" or "duplicate-mapper"
                    or "lisp-mapper" or "supplemental-mapper" or "supplemental-diagnostics-mapper" or "supplemental-globals-mapper"
                    or "supplemental-module-mapper" or "prefixed-supplemental-mapper" or "unmapped-folding-mapper" or "hoisting-mapper" or "duplicate-projection-mapper"))
                    throw new IOException($"contentmappertest: unknown mapper command [{string.Join(' ', new[] { executable }.Concat(start.ArgumentList))}]");
                start.FileName = fixture; start.ArgumentList.Insert(0, executable); start.WorkingDirectory = Path.GetDirectoryName(fixture)!;
                return Process.Start(start) ?? throw new IOException("Mapper fixture did not start.");
            }
        };
        CompilerCommand? command = null;
        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            writer.WriteStartArray();
            foreach (var step in input.GetProperty("steps").EnumerateArray())
            {
                var edits = step.TryGetProperty("edits", out var rawEdits) && rawEdits.ValueKind == JsonValueKind.Object ? rawEdits.EnumerateObject().ToArray() : [];
                foreach (var edit in edits.OrderBy(JsonStrings.GetName, Utf8StringComparer.Ordinal))
                {
                    var path = JsonStrings.GetName(edit);
                    if (edit.Value.ValueKind == JsonValueKind.Null) memory.Remove(path);
                    else if ((Mode(edit.Value) & directoryMode) != 0) memory.CreateDirectory(path);
                    else if ((Mode(edit.Value) & linkMode) != 0)
                    {
                        var target = new Utf8String(Bytes(edit.Value));
                        memory.CreateSymbolicLink(path, CompilerPath.IsAbsolute(target) ? target : "/"u8 + target);
                    }
                    else { memory.WriteFile(path, Bytes(edit.Value)); Now(); }
                }
                foreach (var edit in edits.Where(edit => edit.Value.ValueKind != JsonValueKind.Null && Regular(edit.Value))
                    .OrderBy(edit => Modified(edit.Value)).ThenBy(JsonStrings.GetName, Utf8StringComparer.Ordinal))
                { var stamp = Now(); memory.SetTimes(JsonStrings.GetName(edit), stamp, stamp); }
                fs.Writes.Clear(); fs.Removed.Clear(); fs.Touched.Clear(); stdout.GetStringBuilder().Clear(); stderr.GetStringBuilder().Clear();
                int status = 0;
                Exception? failure = null;
                try
                {
                    if (command?.Watches is null)
                    {
                        if (command is not null) await command.DisposeAsync();
                        command = new(fs, cwd, stdout, JsonStrings.GetString(input.GetProperty("libraryDirectory")),
                            input.GetProperty("outputIsTTY").GetBoolean(), EnvironmentValue, backend, mappers, Now, hashWithText: true)
                        {
                            OnEmittedFiles = emit => { foreach (var path in emit.EmittedFiles) { var stamp = Now(); memory.SetTimes(path, stamp, stamp); } }
                        };
                        var args = step.GetProperty("args");
                        status = (int)await command.ExecuteAsync(args.ValueKind == JsonValueKind.Null ? [] : args.EnumerateArray().Select(JsonStrings.GetString).ToArray());
                    }
                    else
                    {
                        backend.SendChangedPaths(edits.Select(edit => new WatchEvent(JsonStrings.GetName(edit), edit.Value.ValueKind == JsonValueKind.Null ? WatchEventKind.Delete : WatchEventKind.Update)).ToArray());
                        await command.CycleAsync();
                    }
                }
                catch (Exception error) { failure = error; }
                writer.WriteStartObject(); writer.WriteNumber("status", status); writer.WriteString("stdout", stdout.ToString()); writer.WriteString("stderr", stderr.ToString());
                writer.WriteStartArray("writes");
                foreach (var write in fs.Writes.OrderBy(write => write.Path, Utf8StringComparer.Ordinal))
                {
                    writer.WriteStartObject(); writer.WriteString("path", write.Path.Span);
                    if (write.Path.EndsWith(".tsbuildinfo"u8, StringComparison.Ordinal)) { writer.WritePropertyName("buildInfo"); writer.WriteRawValue(write.Text); }
                    else if (write.Text.Length != 0) writer.WriteBase64String("textBase64", write.Text);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("removed"); foreach (var path in fs.Removed.Order(Utf8StringComparer.Ordinal)) writer.WriteStringValue(path.Span); writer.WriteEndArray();
                writer.WriteStartArray("touched"); foreach (var path in fs.Touched.Order(Utf8StringComparer.Ordinal)) writer.WriteStringValue(path.Span); writer.WriteEndArray();
                writer.WriteStartObject("watches");
                if (command?.Watches is { } watches) foreach (var (path, recursive) in watches.WatchedDirectories.OrderBy(pair => pair.Key, Utf8StringComparer.Ordinal)) writer.WriteBoolean(path.Span, recursive);
                writer.WriteEndObject();
                if (failure is not null) { writer.WriteString("error", failure.Message); writer.WriteString("stack", failure.StackTrace); }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        finally { if (command is not null) await command.DisposeAsync(); }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed class BuildInfoFileSystem(IFileSystem inner) : IFileSystem
    {
        public bool CaseSensitive => inner.CaseSensitive;
        public bool FileExists(Utf8String path) => inner.FileExists(path);
        public bool DirectoryExists(Utf8String path) => inner.DirectoryExists(path);
        public DirectoryEntries GetAccessibleEntries(Utf8String path) => inner.GetAccessibleEntries(path);
        public FileEntry? Stat(Utf8String path) => inner.Stat(path);
        public Utf8String RealPath(Utf8String path) => inner.RealPath(path);
        public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents) => inner.WriteFile(path, contents);
        public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents) => inner.AppendFile(path, contents);
        public void Remove(Utf8String path) => inner.Remove(path);
        public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc) => inner.SetTimes(path, accessTimeUtc, writeTimeUtc);
        public byte[]? ReadFile(Utf8String path)
        {
            var bytes = inner.ReadFile(path);
            if (bytes is null || !path.EndsWith(".tsbuildinfo"u8, StringComparison.Ordinal)) return bytes;
            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (!document.RootElement.TryGetProperty("version", out var version) || version.GetString() != "FakeTSVersion") return bytes;
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    foreach (var property in document.RootElement.EnumerateObject())
                    { if (property.NameEquals("version")) writer.WriteString("version", "7.1.0-dev"); else property.WriteTo(writer); }
                    writer.WriteEndObject();
                }
                return buffer.WrittenSpan.ToArray();
            }
            catch (JsonException) { return bytes; }
        }
    }
}

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Execution;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class BuildTests
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
                {
                    writer.WriteStartObject(); writer.WriteString("error", error.Message); writer.WriteString("stack", error.StackTrace); writer.WriteEndObject();
                }
                Console.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
            }
        }
    }

    private static async Task<string> ExecuteAsync(JsonElement input)
    {
        Utf8String Text(JsonElement value, string name, Utf8String fallback = default) => value.TryGetProperty(name, out var text) ? JsonStrings.GetString(text) : fallback;
        var cwd = Text(input, "cwd", "/source"u8);
        bool sensitive = !input.TryGetProperty("caseInsensitive", out var insensitive) || !insensitive.GetBoolean();
        var files = input.GetProperty("files").EnumerateObject().ToDictionary(JsonStrings.GetName, property => JsonStrings.GetString(property.Value).Span.ToArray());
        long ticks = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        DateTime Now() => new(Interlocked.Add(ref ticks, TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        var memory = new MemoryFileSystem(files, sensitive, cwd);
        foreach (var file in files.Keys.Order(Utf8StringComparer.Ordinal)) { var time = Now(); memory.SetTimes(file, time, time); }
        var fs = new TrackingFileSystem(new LibraryFileSystem(memory), Now);
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray();
            foreach (var step in input.GetProperty("steps").EnumerateArray())
            {
                if (step.TryGetProperty("edits", out var edits))
                    foreach (var edit in edits.EnumerateObject().OrderBy(JsonStrings.GetName, Utf8StringComparer.Ordinal))
                    {
                        var path = JsonStrings.GetName(edit);
                        if (edit.Value.ValueKind == JsonValueKind.Null) memory.Remove(path);
                        else { memory.WriteFile(path, JsonStrings.GetString(edit.Value)); var time = Now(); memory.SetTimes(path, time, time); }
                    }
                if (step.TryGetProperty("touch", out var touched)) foreach (var path in touched.EnumerateArray())
                { var time = Now(); memory.SetTimes(JsonStrings.GetString(path), time, time); }
                if (step.TryGetProperty("times", out var times)) foreach (var entry in times.EnumerateObject())
                { var time = DateTime.UnixEpoch.AddMilliseconds(entry.Value.GetInt64()); memory.SetTimes(JsonStrings.GetName(entry), time, time); }
                fs.Writes.Clear(); fs.Removed.Clear(); fs.Touched.Clear();
                fs.FailWrite = Text(step, "failWrite"); fs.FailRemove = Text(step, "failRemove");
                var args = step.TryGetProperty("args", out var arguments) ? arguments.EnumerateArray().Select(JsonStrings.GetString).ToArray()
                    : ["--build"u8, "--verbose"u8, "--pretty"u8, "false"u8, "--singleThreaded"u8];
                var command = new CommandLineParser(fs, cwd).Parse(args, build: true);
                using var stdout = new StringWriter(CultureInfo.InvariantCulture);
                CompilerExitStatus status;
                if (command.Diagnostics.Length != 0)
                {
                    foreach (var diagnostic in command.Diagnostics) DiagnosticReporter.WritePlain(stdout, diagnostic, fs, cwd);
                    status = CompilerExitStatus.DiagnosticsWithOutputsSkipped;
                }
                else
                {
                    await using var builder = new ProjectBuilder(fs, cwd, command.Options, Text(input, "libraryDirectory", "bundled:///libs"u8), now: Now, hashWithText: true)
                    {
                        OnEmittedFiles = result =>
                        {
                            foreach (var file in result.EmittedFiles) { var stamp = Now(); memory.SetTimes(file, stamp, stamp); }
                        }
                    };
                    var options = BuildOptions.FromCommandLine(command.Options);
                    var result = await builder.BuildAsync(command.FileNames, options);
                    bool quiet = command.Options.Quiet == true;
                    if (!quiet)
                    {
                        if (!options.Clean) foreach (var message in result.Messages) DiagnosticReporter.WriteStatus(stdout, message, Now(), command.Options.Locale);
                        if (result.Projects.Count == 0)
                            foreach (var diagnostic in result.Diagnostics) DiagnosticReporter.WritePlain(stdout, diagnostic, fs, cwd, locale: command.Options.Locale);
                        foreach (var project in result.Projects)
                        {
                            foreach (var message in project.Messages) DiagnosticReporter.WriteStatus(stdout, message, Now(), command.Options.Locale);
                            foreach (var diagnostic in project.Diagnostics) DiagnosticReporter.WritePlain(stdout, diagnostic, fs, cwd, project.Program?.Program, command.Options.Locale);
                            if (command.Options.ListEmittedFiles == true) foreach (var file in project.EmittedFiles) stdout.Write($"TSFILE: {file}\n");
                        }
                        if (options.Clean) foreach (var message in result.Messages) DiagnosticReporter.WriteStatus(stdout, message, Now(), command.Options.Locale);
                    }
                    status = result.ExitStatus;
                }
                writer.WriteStartObject(); writer.WriteNumber("status", (int)status); writer.WriteString("stdout", stdout.ToString()); writer.WriteString("stderr", "");
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
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    private sealed class TrackingFileSystem(IFileSystem fs, Func<DateTime> now) : IFileSystem
    {
        internal List<(Utf8String Path, byte[] Text)> Writes { get; } = [];
        internal List<Utf8String> Removed { get; } = [];
        internal List<Utf8String> Touched { get; } = [];
        internal Utf8String FailWrite, FailRemove;
        private readonly object sync = new();
        public bool CaseSensitive => fs.CaseSensitive;
        public bool FileExists(Utf8String path) => fs.FileExists(path);
        public bool DirectoryExists(Utf8String path) => fs.DirectoryExists(path);
        public byte[]? ReadFile(Utf8String path) => fs.ReadFile(path);
        public DirectoryEntries GetAccessibleEntries(Utf8String path) => fs.GetAccessibleEntries(path);
        public FileEntry? Stat(Utf8String path) => fs.Stat(path);
        public Utf8String RealPath(Utf8String path) => fs.RealPath(path);
        public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents)
        {
            lock (sync)
            {
                if (path == FailWrite) throw new IOException("test write failure");
                fs.WriteFile(path, contents); var time = now(); fs.SetTimes(path, time, time); Writes.Add((path, contents.ToArray()));
            }
        }
        public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents) => fs.AppendFile(path, contents);
        public void Remove(Utf8String path)
        {
            if (path == FailRemove) throw new IOException("test remove failure");
            fs.Remove(path); lock (sync) Removed.Add(path);
        }
        public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc)
        {
            fs.SetTimes(path, accessTimeUtc, writeTimeUtc); lock (sync) Touched.Add(path);
        }
    }
}

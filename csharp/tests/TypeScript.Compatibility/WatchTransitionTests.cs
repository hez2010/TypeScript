using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Execution;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Text;
using TypeScript.Compiler.Watching;

namespace TypeScript.Compatibility;

internal static class WatchTransitionTests
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
        var backend = new WatchTests.MockBackend { CaseSensitive = sensitive, DirectoryExists = fs.DirectoryExists };
        ProjectWatchSession? session = null;
        BuildWatchSession? buildSession = null;
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var firstStep = input.GetProperty("steps")[0];
        var args = firstStep.TryGetProperty("args", out var arguments) ? arguments.EnumerateArray().Select(JsonStrings.GetString).ToArray()
            : ["--watch"u8, "--pretty"u8, "false"u8, "--singleThreaded"u8];
        bool buildMode = args.Length != 0 && (args[0] == "--build"u8 || args[0] == "-b"u8);
        var command = new CommandLineParser(fs, cwd).Parse(args, build: buildMode);
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
                fs.Writes.Clear(); fs.Removed.Clear(); fs.Touched.Clear();
                fs.FailWrite = Text(step, "failWrite"); fs.FailRemove = Text(step, "failRemove");
                stdout.GetStringBuilder().Clear();
                CompilerExitStatus status;
                if (command.Diagnostics.Length != 0)
                {
                    foreach (var diagnostic in command.Diagnostics) DiagnosticReporter.WritePlain(stdout, diagnostic, fs, cwd);
                    status = CompilerExitStatus.DiagnosticsWithOutputsSkipped;
                }
                else if (buildMode)
                {
                    BuildWatchCycleResult result;
                    bool initial = buildSession is null;
                    if (buildSession is null)
                    {
                        buildSession = new(fs, cwd, command.FileNames, command.Options, BuildOptions.FromCommandLine(command.Options),
                            Text(input, "libraryDirectory", "bundled:///libs"u8), backend, warnings: stdout, now: Now, hashWithText: true);
                        buildSession.Builder.OnEmittedFiles = result =>
                        {
                            foreach (var path in result.EmittedFiles) { var stamp = Now(); memory.SetTimes(path, stamp, stamp); }
                        };
                        result = await buildSession.StartAsync();
                    }
                    else
                    {
                        SendChanges(step);
                        result = await buildSession.CycleAsync();
                    }
                    if (result.Starting is { } starting) WriteStart(starting, command.Options);
                    if (result.Build is { } build && command.Options.Quiet != true)
                    {
                        foreach (var message in build.Messages) DiagnosticReporter.WriteStatus(stdout, message, Now(), command.Options.Locale);
                        if (build.Projects.Count == 0)
                            foreach (var diagnostic in build.Diagnostics) DiagnosticReporter.WritePlain(stdout, diagnostic, fs, cwd, locale: command.Options.Locale);
                        foreach (var project in build.Projects)
                        {
                            foreach (var message in project.Messages) DiagnosticReporter.WriteStatus(stdout, message, Now(), command.Options.Locale);
                            foreach (var diagnostic in project.Diagnostics) DiagnosticReporter.WritePlain(stdout, diagnostic, fs, cwd, project.Program?.Program, command.Options.Locale);
                            if (command.Options.ListEmittedFiles == true) foreach (var path in project.EmittedFiles) stdout.Write($"TSFILE: {path}\n");
                        }
                    }
                    if (result.Finished is { } finished) DiagnosticReporter.WriteStatus(stdout, finished, Now(), command.Options.Locale);
                    status = initial ? result.Build?.ExitStatus ?? CompilerExitStatus.Success : CompilerExitStatus.Success;
                }
                else
                {
                    WatchCycleResult result;
                    if (session is null)
                    {
                        var parser = new ConfigParser(fs, cwd);
                        var project = command.Options.Project;
                        if (project is not null)
                            project = project.Value.EndsWith(".json"u8, StringComparison.OrdinalIgnoreCase)
                                ? CompilerPath.Resolve(cwd, project.Value) : CompilerPath.Resolve(cwd, project.Value, "tsconfig.json"u8);
                        else if (command.FileNames.Length == 0) project = parser.FindConfig(cwd);
                        var config = project is { } path ? parser.Parse(path, command.Options)
                            : new ParsedConfig(default, command.Options, command.FileNames.Select(path => CompilerPath.Resolve(cwd, path)).ToArray(), [], [], []);
                        session = new(fs, cwd, config, command.Options, Text(input, "libraryDirectory", "bundled:///libs"u8), backend, warnings: stdout, hashWithText: true);
                        result = await session.StartAsync();
                    }
                    else
                    {
                        SendChanges(step);
                        result = await session.CycleAsync();
                    }
                    var options = session.Configuration.Options;
                    if (result.Starting is { } starting) WriteStart(starting, options);
                    if (options.Quiet != true)
                    {
                        foreach (var diagnostic in result.Diagnostics) DiagnosticReporter.WritePlain(stdout, diagnostic, fs, cwd, result.Program?.Program, options.Locale);
                        if (options.ListEmittedFiles == true) foreach (var path in result.EmittedFiles) stdout.Write($"TSFILE: {path}\n");
                    }
                    if (result.Finished is { } finished) DiagnosticReporter.WriteStatus(stdout, finished, Now(), options.Locale);
                    status = CompilerExitStatus.Success;
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
                writer.WriteStartObject("watches");
                if (session is not null) foreach (var (path, recursive) in session.Watches.WatchedDirectories.OrderBy(pair => pair.Key, Utf8StringComparer.Ordinal)) writer.WriteBoolean(path.Span, recursive);
                if (buildSession is not null) foreach (var (path, recursive) in buildSession.Watches.WatchedDirectories.OrderBy(pair => pair.Key, Utf8StringComparer.Ordinal)) writer.WriteBoolean(path.Span, recursive);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        if (session is not null) await session.DisposeAsync();
        if (buildSession is not null) await buildSession.DisposeAsync();
        return Encoding.UTF8.GetString(output.WrittenSpan);

        void SendChanges(JsonElement step)
        {
            var changes = new List<WatchEvent>();
            if (step.TryGetProperty("edits", out var edits)) foreach (var edit in edits.EnumerateObject())
                changes.Add(new(JsonStrings.GetName(edit), edit.Value.ValueKind == JsonValueKind.Null ? WatchEventKind.Delete : WatchEventKind.Update));
            if (step.TryGetProperty("touch", out var touched)) foreach (var path in touched.EnumerateArray()) changes.Add(new(JsonStrings.GetString(path), WatchEventKind.Update));
            backend.SendChangedPaths(changes);
            if (step.TryGetProperty("overflow", out var overflow) && overflow.GetBoolean()) backend.SendOverflow();
        }
        void WriteStart(TypeScript.Compiler.Diagnostics.Diagnostic starting, CompilerOptions options)
        {
            if (options.PreserveWatchOutput != true && options.Diagnostics != true && options.ExtendedDiagnostics != true) stdout.Write("\u001b[2J\u001b[3J\u001b[H");
            DiagnosticReporter.WriteStatus(stdout, starting, Now(), options.Locale);
        }
    }

    internal sealed class TrackingFileSystem(IFileSystem fs, Func<DateTime> now) : IFileSystem
    {
        internal List<(Utf8String Path, byte[] Text)> Writes { get; } = [];
        internal List<Utf8String> Removed { get; } = [];
        internal List<Utf8String> Touched { get; } = [];
        internal Utf8String FailWrite, FailRemove;
        internal Action<Utf8String>? OnWrite;
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
                OnWrite?.Invoke(path);
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

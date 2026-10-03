using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Projects.TypeAcquisition;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class TypingsTests
{
    internal static async Task<int> SafetyAsync()
    {
        int assertions = 0;
        void Check(bool value, string reason) { assertions++; if (!value) throw new InvalidOperationException(reason); }
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/p/app.js"u8] = "const value = 1;"u8.ToArray() });
        var npm = new TestNpm(fs);
        await using var installer = new TypingsInstaller(fs, "/cache"u8, npm, 2);
        var options = new CompilerOptions(); options.SetRaw("types"u8, "[]"u8);
        TypingsInstallRequest Request(params Utf8String[] imports) => new(new(true, DisableFilenameBasedTypeAcquisition: true),
            options, ["/p/app.js"u8], "/p"u8, imports, fs);
        var first = await installer.InstallAsync(Request("jquery"u8));
        Check(first.Files.SequenceEqual(new Utf8String[] { "/cache/node_modules/@types/jquery/index.d.ts"u8 }), "Installed declarations resolve through the module resolver");
        Check(npm.Calls.Count == 2, "The registry initializes once before the first package install");
        var cached = await installer.InstallAsync(Request("jquery"u8));
        Check(cached.Files.SequenceEqual(first.Files) && npm.Calls.Count == 2, "Up-to-date cached declarations avoid another install");
        await installer.InstallAsync(Request("invalid package"u8));
        Check(npm.Calls.Count == 2, "Invalid package names are never passed to npm");
        npm.Fail = "missing"u8;
        try { await installer.InstallAsync(Request("missing"u8)); Check(false, "Expected a simulated npm failure"); }
        catch (IOException) { assertions++; }
        int afterFailure = npm.Calls.Count;
        await installer.InstallAsync(Request("missing"u8));
        Check(npm.Calls.Count == afterFailure, "Failed package installs are remembered for the session");
        npm.Fail = default;
        npm.Block = "cancelled"u8;
        using var cancellation = new CancellationTokenSource();
        var pending = installer.InstallAsync(Request("cancelled"u8), cancellation.Token).AsTask();
        await npm.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        try { await pending; Check(false, "A canceled package request must finish as canceled"); }
        catch (OperationCanceledException) { assertions++; }
        npm.Block = default;
        var retry = await installer.InstallAsync(Request("cancelled"u8));
        Check(retry.Files.Contains("/cache/node_modules/@types/cancelled/index.d.ts"u8), "Cancellation does not poison the missing-package cache");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => installer.InstallAsync(Request("pkg"u8 + Utf8String.Format(index))).AsTask()));
        Check(concurrent.All(result => result.Files.Any(file => file.Contains("/@types/pkg"u8))) && npm.Maximum <= 2,
            "Concurrent projects share the configured npm concurrency limit");
        Check(npm.Calls.Count(call => call.Contains("types-registry@latest"u8)) == 1, "Concurrent requests reuse one registry initialization");
        await installer.DisposeAsync();
        try { await installer.InstallAsync(Request("pkg0"u8)); Check(false, "A disposed installer must reject new work"); }
        catch (ObjectDisposedException) { assertions++; }
        assertions += await SessionSafetyAsync();
        assertions += await LspSafetyAsync();
        return assertions;
    }

    private static async Task<int> LspSafetyAsync()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/p/jsconfig.json"u8] = "{\"compilerOptions\":{\"noLib\":true},\"typeAcquisition\":{\"enable\":true,\"disableFilenameBasedTypeAcquisition\":true}}"u8.ToArray(),
            ["/p/app.js"u8] = "import 'jquery';"u8.ToArray(),
        });
        var npm = new TestNpm(fs) { Block = "jquery"u8 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (server, client) = RpcTests.DuplexStream.Pair(); await using var clientOwner = client;
        var running = LanguageServer.RunAsync(fs, server, server, new() { CurrentDirectory = "/p"u8, TypingsLocation = "/cache"u8, Npm = npm }, timeout.Token);
        var peer = new RpcWire(client, client, false, 1_000_000);
        int nextId = 0;
        async ValueTask<RpcMessage> Request(Utf8String method, ReadOnlyMemory<byte> parameters = default)
        {
            var id = new RpcId(default, ++nextId);
            await peer.WriteAsync(id, method, new(parameters), null, timeout.Token);
            while (await peer.ReadAsync(timeout.Token) is { } response)
            {
                if (response.Method.IsEmpty) { Check(response.Id == id && response.Error is null, "LSP request succeeds while npm is pending"); return response; }
                if (response.Id is not null) await peer.WriteAsync(response.Id, default, RpcResponse.Null, null, timeout.Token);
            }
            throw new EndOfStreamException();
        }
        try
        {
            await Request("initialize"u8, "{\"processId\":null,\"rootUri\":\"file:///p\",\"capabilities\":{}}"u8.ToArray());
            await peer.WriteAsync(null, "initialized"u8, new("{}"u8.ToArray()), null, timeout.Token);
            await peer.WriteAsync(null, "textDocument/didOpen"u8, new("{\"textDocument\":{\"uri\":\"file:///p/app.js\",\"languageId\":\"javascript\",\"version\":1,\"text\":\"import 'jquery';\"}}"u8.ToArray()), null, timeout.Token);
            await Request("textDocument/diagnostic"u8, "{\"textDocument\":{\"uri\":\"file:///p/app.js\"}}"u8.ToArray());
            await npm.Started.Task.WaitAsync(timeout.Token);
            Check(npm.Calls.Count == 2, "LSP forwards the npm executor and typings location through its project session");
            npm.Release.TrySetResult();
            for (int attempt = 0; attempt < 100 && !fs.FileExists("/cache/node_modules/@types/jquery/index.d.ts"u8); attempt++) await Task.Delay(10, timeout.Token);
            Check(fs.FileExists("/cache/node_modules/@types/jquery/index.d.ts"u8), "LSP automatic acquisition installs into the configured cache");
            await Request("shutdown"u8);
            await peer.WriteAsync(null, "exit"u8, default, null, timeout.Token);
            await running.WaitAsync(timeout.Token);
        }
        finally { timeout.Cancel(); npm.Release.TrySetResult(); try { await running; } catch (OperationCanceledException) { } }
        return checks;
    }

    private static async Task<int> SessionSafetyAsync()
    {
        int assertions = 0;
        void Check(bool value, string reason) { assertions++; if (!value) throw new InvalidOperationException(reason); }
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/p/jsconfig.json"u8] = "{\"compilerOptions\":{\"noLib\":true},\"typeAcquisition\":{\"enable\":true,\"disableFilenameBasedTypeAcquisition\":true}}"u8.ToArray(),
            ["/p/app.js"u8] = "import 'jquery';"u8.ToArray()
        });
        var npm = new TestNpm(fs) { Block = "jquery"u8 };
        var progress = new ConcurrentQueue<(TypeScript.Compiler.Diagnostics.DiagnosticCode Code, Utf8String Name, bool Finish)>();
        await using var session = new ProjectSession(fs, new()
        {
            TypingsLocation = "/cache"u8,
            Progress = (message, name, finish) => { progress.Enqueue((message.Code, name, finish)); return default; },
        }, npm);
        int refreshes = 0;
        session.DiagnosticsRefreshRequested += () => Interlocked.Increment(ref refreshes);
        session.Notify(new(FileChangeKind.Open, "/p/app.js"u8, 1, "import 'jquery';"u8));
        await using var before = await session.GetSnapshotAsync(["/p/app.js"u8]);
        await npm.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(progress.Any(value => value.Code == TypeScript.Compiler.Diagnostics.DiagnosticCode.InstallingTypesFor0 && value.Name == "p/jsconfig.json"u8 && !value.Finish),
            "Type acquisition reports the project display name before waiting for npm");
        Check(before.Snapshot.GetDefaultProject("/p/app.js"u8)!.TypingsFiles.Count == 0, "An editor request does not block on a package install");
        session.Notify(new(FileChangeKind.Change, "/p/app.js"u8, 2, Edits: [new("import 'cancelled';"u8)]));
        await using var changed = await session.GetSnapshotAsync(["/p/app.js"u8]);
        for (int attempt = 0; attempt < 100 && !fs.FileExists("/cache/node_modules/@types/cancelled/index.d.ts"u8); attempt++) await Task.Delay(10);
        Check(fs.FileExists("/cache/node_modules/@types/cancelled/index.d.ts"u8), "A new project version can acquire its types while an older request is pending");
        npm.Release.TrySetResult();
        await session.WaitForBackgroundTasksAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await using var installed = await session.GetSnapshotAsync(["/p/app.js"u8]);
        var project = installed.Snapshot.GetDefaultProject("/p/app.js"u8)!;
        Check(project.TypingsFiles.SequenceEqual(new Utf8String[] { "/cache/node_modules/@types/cancelled/index.d.ts"u8 }),
            "A late install cannot replace results requested by the current project version");
        Check(project.Program!.GetFile("/cache/node_modules/@types/cancelled/index.d.ts"u8) is not null,
            "Accepted typings become roots of the next ensured program");
        Check(before.Snapshot.GetDefaultProject("/p/app.js"u8)!.TypingsFiles.Count == 0, "Type acquisition leaves earlier snapshots unchanged");
        Check(refreshes > 0, "Accepted type acquisitions request a diagnostic refresh");
        await session.SetAutomaticTypeAcquisitionAsync(false);
        await session.WaitForBackgroundTasksAsync();
        int previousCalls = npm.Calls.Count;
        session.Notify(new(FileChangeKind.Change, "/p/app.js"u8, 3, Edits: [new("import 'pkg0';"u8)]));
        await using var disabled = await session.GetSnapshotAsync(["/p/app.js"u8]);
        await session.WaitForBackgroundTasksAsync();
        Check(npm.Calls.Count == previousCalls, "Disabled automatic acquisition schedules no npm calls");
        foreach (var group in progress.GroupBy(value => value.Code))
            Check(group.Count(value => value.Finish) == group.Count(value => !value.Finish), "Project construction and concurrent installations finish each started progress operation");
        return assertions;
    }

    private sealed class TestNpm(MemoryFileSystem fs) : INpmExecutor
    {
        internal readonly ConcurrentQueue<Utf8String[]> Calls = new();
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Utf8String Fail, Block;
        internal int Maximum;
        private int active;

        public async ValueTask<NpmResult> InstallAsync(Utf8String directory, IReadOnlyList<Utf8String> arguments, CancellationToken cancellation)
        {
            Calls.Enqueue(arguments.ToArray());
            int current = Interlocked.Increment(ref active);
            int observed;
            do { observed = Maximum; } while (current > observed && Interlocked.CompareExchange(ref Maximum, current, observed) != observed);
            try
            {
                if (arguments.Contains("types-registry@latest"u8))
                {
                    var buffer = new ArrayBufferWriter<byte>();
                    using (var writer = new Utf8JsonWriter(buffer))
                    {
                        writer.WriteStartObject(); writer.WriteStartObject("entries");
                        foreach (var name in new Utf8String[] { "jquery"u8, "missing"u8, "cancelled"u8 }.Concat(Enumerable.Range(0, 8).Select(index => "pkg"u8 + Utf8String.Format(index))))
                        {
                            JsonStrings.WriteName(writer, name); writer.WriteStartObject(); writer.WriteString("latest", "1.0.0"); writer.WriteEndObject();
                        }
                        writer.WriteEndObject(); writer.WriteEndObject();
                    }
                    fs.WriteFile(CompilerPath.Combine(directory, "node_modules/types-registry/index.json"u8), buffer.WrittenSpan);
                    return new(default, 0);
                }
                if (!Block.IsEmpty && arguments.Contains("@types/"u8 + Block + "@latest"u8))
                {
                    Started.TrySetResult();
                    await Release.Task.WaitAsync(cancellation);
                }
                await Task.Delay(5, cancellation);
                if (!Fail.IsEmpty && arguments.Contains("@types/"u8 + Fail + "@latest"u8)) return new("test npm failure"u8, 1);
                foreach (var package in arguments.Where(argument => argument.StartsWith("@types/"u8)))
                {
                    var name = package[..package.LastIndexOf((byte)'@')];
                    fs.WriteFile(CompilerPath.Combine(directory, "node_modules"u8, name, "index.d.ts"u8), "export {};"u8);
                }
                return new(default, 0);
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }

    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            var output = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(output)) ExecuteAsync(input.RootElement, writer).GetAwaiter().GetResult();
            Console.WriteLine(Encoding.UTF8.GetString(output.WrittenSpan));
        }
    }

    private static Utf8String[]? Strings(JsonElement value, string name) => value.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
        ? values.EnumerateArray().Select(JsonStrings.GetString).ToArray() : null;
    private static bool? Boolean(JsonElement value, string name) => value.TryGetProperty(name, out var flag)
        ? flag.ValueKind == JsonValueKind.True ? true : flag.ValueKind == JsonValueKind.False ? false : null : null;
    private static Utf8String Text(JsonElement value, string name) => ProjectReplay.Text(value, name);

    private static async Task ExecuteAsync(JsonElement input, Utf8JsonWriter writer)
    {
        switch (Text(input, "operation"))
        {
            case var operation when operation == "validate"u8:
                var result = TypingsDiscovery.ValidatePackageName(Text(input, "text"));
                writer.WriteStartArray(); writer.WriteNumberValue((int)result.Result); JsonStrings.WriteString(writer, result.Name);
                writer.WriteBooleanValue(result.IsScope); writer.WriteEndArray(); break;
            case var operation when operation == "discover"u8:
                var fs = new MemoryFileSystem(input.GetProperty("files").EnumerateObject().ToDictionary(JsonStrings.GetName,
                    property => JsonStrings.GetString(property.Value).Span.ToArray()), Boolean(input, "caseSensitive") == true);
                var settings = input.GetProperty("acquisition");
                var options = new CompilerOptions();
                foreach (var property in input.GetProperty("compilerOptions").EnumerateObject()) options.Set(JsonStrings.GetName(property), property.Value);
                var cached = input.GetProperty("cache").EnumerateObject().ToDictionary(JsonStrings.GetName,
                    property => new CachedTyping(Text(property.Value, "fileName"), SemanticVersion.Parse(Text(property.Value, "version"))!));
                var registry = input.GetProperty("registry").EnumerateObject().ToDictionary(JsonStrings.GetName,
                    property => (IReadOnlyDictionary<Utf8String, Utf8String>)property.Value.EnumerateObject().ToDictionary(JsonStrings.GetName, version => JsonStrings.GetString(version.Value)));
                var discovered = TypingsDiscovery.Discover(fs, new(Boolean(settings, "enable"), Strings(settings, "include"), Strings(settings, "exclude"),
                    Boolean(settings, "disableFilenameBasedTypeAcquisition")), options, Strings(input, "fileNames") ?? [], Text(input, "projectRoot"),
                    Strings(input, "unresolvedImports"), cached, registry);
                writer.WriteStartArray(); Write(discovered.CachedFiles); Write(discovered.NewNames); Write(discovered.FilesToWatch); writer.WriteEndArray(); break;
            case var operation when operation == "batch"u8:
                var batches = new ConcurrentBag<IReadOnlyList<Utf8String>>();
                using (var slots = new SemaphoreSlim(input.GetProperty("concurrency").GetInt32()))
                {
                    bool failed = false;
                    try
                    {
                        await TypingsInstaller.InstallPackagesAsync(Strings(input, "packages") ?? [], slots, (values, _) =>
                        {
                            batches.Add(values);
                            if (Boolean(input, "fail") == true) throw new IOException("test npm failure");
                            return default;
                        });
                    }
                    catch (IOException) { failed = true; }
                    writer.WriteStartObject(); writer.WriteBoolean("failed", failed); writer.WriteStartArray("batches");
                    foreach (var batch in batches.OrderBy(batch => Utf8String.Join("\0"u8, batch), Utf8StringComparer.Ordinal))
                    {
                        writer.WriteStartArray(); foreach (var value in batch) JsonStrings.WriteString(writer, value); writer.WriteEndArray();
                    }
                    writer.WriteEndArray(); writer.WriteEndObject();
                }
                break;
            default: throw new ArgumentException("Unknown typings replay operation");
        }

        void Write(IEnumerable<Utf8String> values)
        {
            writer.WriteStartArray(); foreach (var value in values.Order(Utf8StringComparer.Ordinal)) JsonStrings.WriteString(writer, value); writer.WriteEndArray();
        }
    }
}

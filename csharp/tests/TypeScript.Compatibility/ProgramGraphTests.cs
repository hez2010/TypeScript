using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class ProgramGraphTests
{
    internal static async Task Safety()
    {
        int assertions = 0;
        void Check(bool condition, Utf8String message)
        {
            assertions++;
            if (!condition)
                throw new InvalidDataException(message.ToString());
        }
        static JsonElement Json(Utf8String text)
        {
            using var document = JsonDocument.Parse(text.Memory);
            return document.RootElement.Clone();
        }
        var options = new CompilerOptions();
        options.Set("noLib"u8, Json("true"u8));
        options.Set("noEmit"u8, Json("true"u8));
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/a.ts"u8] = Wtf8.Encode("import './b'; export const a = 1;"),
            ["/project/b.ts"u8] = Wtf8.Encode("export const b = 1;")
        });
        var config = new ParsedConfig("/project/tsconfig.json"u8, options, ["/project/a.ts"u8], [], [], []);
        var first = await CompilerProgram.CreateAsync(fs, "/project"u8, config, concurrency: 1);
        var second = await CompilerProgram.CreateAsync(fs, "/project"u8, config, first, concurrency: 4);
        Check(
            second.ReusedSourceFiles == 2
                && ReferenceEquals(first.GetFile("/project/a.ts"u8)!.Syntax, second.GetFile("/project/a.ts"u8)!.Syntax),
            "Unchanged syntax is retained by reference"u8);
        Check(
            ReferenceEquals(first.GetFile("/project/a.ts"u8)!.Binding, second.GetFile("/project/a.ts"u8)!.Binding),
            "Unchanged syntax retains symbol identity"u8);
        fs.WriteFile("/project/b.ts"u8, Wtf8.Encode("export const replacement = 2;"));
        var third = await CompilerProgram.CreateAsync(fs, "/project"u8, config, second);
        Check(
            third.ReusedSourceFiles == 1 && third.GetFile("/project/b.ts"u8)!.Binding.Symbol!.Exports.ContainsKey("replacement"u8),
            "Changed files rebuild binding"u8);
        Check(
            first.GetFile("/project/b.ts"u8)!.Binding.Symbol!.Exports.ContainsKey("b"u8)
                && !first.GetFile("/project/b.ts"u8)!.Binding.Symbol!.Exports.ContainsKey("replacement"u8),
            "Older snapshots preserve their symbols"u8);
        fs.WriteFile("/project/a.ts"u8, Wtf8.Encode("import './missing';"));
        var missing = await CompilerProgram.CreateAsync(fs, "/project"u8, config, third);
        Check(!missing.GetFile("/project/a.ts"u8)!.Resolutions[0].Resolution.IsResolved, "Negative module resolution is recorded"u8);
        fs.WriteFile("/project/missing.ts"u8, Wtf8.Encode("export const found=1;"));
        var found = await CompilerProgram.CreateAsync(fs, "/project"u8, config, missing);
        Check(
            found.GetFile("/project/missing.ts"u8) is not null && found.GetFile("/project/b.ts"u8) is null,
            "Filesystem changes invalidate negative resolutions and remove unreachable files"u8);
        Check(
            ReferenceEquals(found.GetFile("/project/a.ts"u8)!.Syntax, missing.GetFile("/project/a.ts"u8)!.Syntax),
            "Graph invalidation need not discard unchanged syntax"u8);
        var resolver = new ModuleResolver(fs, options, "/project"u8);
        var negative = resolver.Resolve("./new"u8, "/project/a.ts"u8);
        fs.WriteFile("/project/new.ts"u8, Wtf8.Encode("export {}"));
        resolver.Invalidate();
        Check(
            !negative.IsResolved && resolver.Resolve("./new"u8, "/project/a.ts"u8).IsResolved,
            "Explicit resolver invalidation refreshes negative caches"u8);
        fs.WriteFile("/project/node_modules/p/package.json"u8, Wtf8.Encode("{\"types\":\"one.d.ts\"}"));
        fs.WriteFile("/project/node_modules/p/one.d.ts"u8, Wtf8.Encode("export {}"));
        fs.WriteFile("/project/node_modules/p/two.d.ts"u8, Wtf8.Encode("export {}"));
        Check(resolver.Resolve("p"u8, "/project/a.ts"u8).FileName.EndsWith("/one.d.ts"u8, StringComparison.Ordinal), "Package path is resolved"u8);
        fs.WriteFile("/project/node_modules/p/package.json"u8, Wtf8.Encode("{\"types\":\"two.d.ts\"}"));
        resolver.Invalidate();
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => resolver.ResolveAsync("p"u8, "/project/a.ts"u8).AsTask()));
        Check(
            concurrent.All(r => r.FileName.EndsWith("/two.d.ts"u8, StringComparison.Ordinal)),
            "Package invalidation is consistent across concurrent readers"u8);
        options.Set("moduleDetection"u8, Json("\"force\""u8));
        Check(first.Configuration.Options.String("moduleDetection"u8) is null, "Programs own their compiler option snapshot"u8);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await CompilerProgram.CreateAsync(fs, "/project"u8, config, cancellation: cancelled.Token);
            throw new InvalidDataException("Expected program cancellation");
        }
        catch (OperationCanceledException)
        {
            assertions++;
        }
        // Publication waits for the first file, but its idle peer must still
        // admit a third file. A fixed window of outstanding tasks deadlocks here.
        using var thirdRead = new ManualResetEventSlim();
        var scheduledFiles = new Dictionary<Utf8String, byte[]>
        {
            ["/scheduled/a.ts"u8] = Wtf8.Encode("export const a = 1;"),
            ["/scheduled/b.ts"u8] = Wtf8.Encode("export const b = 2;"),
            ["/scheduled/c.ts"u8] = Wtf8.Encode("export const c = 3;")
        };
        var scheduledFs = new ObservedFileSystem(new MemoryFileSystem(scheduledFiles), path =>
        {
            if (path == "/scheduled/a.ts"u8 && !thirdRead.Wait(TimeSpan.FromSeconds(15)))
                throw new InvalidOperationException("Program publication blocked an idle parser worker");
            if (path == "/scheduled/c.ts"u8)
                thirdRead.Set();
        });
        var scheduledConfig = new ParsedConfig("/scheduled/tsconfig.json"u8, options, scheduledFiles.Keys.ToArray(), [], [], []);
        var scheduled = await CompilerProgram.CreateAsync(scheduledFs, "/scheduled"u8, scheduledConfig, concurrency: 2);
        Check(thirdRead.IsSet && scheduled.SourceFiles.Select(f => f.Syntax.FileName).SequenceEqual(scheduledFiles.Keys),
            "Workers refill behind a blocked publication and preserve root order"u8);
        Check(scheduled.SourceFiles.All(f => f.Binding.IsModule && f.Binding.Symbol!.Exports.Count == 1),
            "Every published file has completed binding"u8);
        var serialScheduled = await CompilerProgram.CreateAsync(new MemoryFileSystem(scheduledFiles), "/scheduled"u8,
            scheduledConfig, concurrency: 1);
        Check(serialScheduled.SourceFiles.Select(f => f.Syntax.FileName).SequenceEqual(scheduled.SourceFiles.Select(f => f.Syntax.FileName)),
            "Serial and parallel discovery produce the same graph"u8);

        using var otherRead = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        var failureReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int activeReads = 0;
        var failingFs = new ObservedFileSystem(new MemoryFileSystem(scheduledFiles), path =>
        {
            if (path == "/scheduled/a.ts"u8)
            {
                if (!otherRead.Wait(TimeSpan.FromSeconds(15)))
                    throw new InvalidOperationException("The second file did not start");
                failureReached.SetResult();
                throw new IOException("Expected file read failure");
            }
            if (path == "/scheduled/b.ts"u8)
            {
                Interlocked.Increment(ref activeReads);
                try
                {
                    otherRead.Set();
                    if (!releaseRead.Wait(TimeSpan.FromSeconds(15)))
                        throw new InvalidOperationException("The outstanding read was not released");
                }
                finally { Interlocked.Decrement(ref activeReads); }
            }
        });
        var failedBuild = CompilerProgram.CreateAsync(failingFs, "/scheduled"u8, scheduledConfig, concurrency: 2).AsTask();
        try
        {
            await failureReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Check(!failedBuild.IsCompleted, "Failed builds retain ownership of outstanding workers"u8);
        }
        finally { releaseRead.Set(); }
        try
        {
            await failedBuild;
            throw new InvalidDataException("Expected file read failure");
        }
        catch (IOException error) when (error.Message == "Expected file read failure")
        {
            Check(activeReads == 0, "Failed builds drain outstanding workers before returning"u8);
        }
        {
            var storage = new BoundSourceFile(new SourceFileNode());
            var original = new IdentifierNode();
            var entry = storage.Data(original);
            entry.Flags = NodeFlags.Synthesized;
            var view = storage.Get(original)!.Value;
            for (int i = 0; i < 3000; i++)
                storage.Data(new IdentifierNode()).Flags = NodeFlags.Ambient;
            entry.Flags |= NodeFlags.ThisNodeHasError;
            Check(storage.Get(original)!.Value.Flags == (NodeFlags.Synthesized | NodeFlags.ThisNodeHasError),
                "Binding slots retain their identity when the node index and chunk storage grow"u8);
            Check(view.Flags == entry.Flags, "Returned binding views retain the node state"u8);
            Check(storage.Get(new IdentifierNode()) is null, "An absent binding differs from a default binding value"u8);
        }
        const int depth = 12000;
        var deep = Parser.ParseSourceFile(
            new("/project/deep.ts"u8),
            new SourceText(Utf8String.Concat("function f(){ return "u8, new Utf8String('(', depth), "value"u8) + new Utf8String(')', depth) + "; }"u8));
        var previousContext = SynchronizationContext.Current;
        ValueTask<BoundSourceFile> operation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new RejectContext());
            operation = Binder.BindAsync(deep);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        var bound = await operation.ConfigureAwait(false);
        Check(
            bound.Diagnostics.Count == 0 && bound.Locals.ContainsKey("f"u8),
            "Deep binding completes without a native recursion limit or synchronization context dependency"u8);
        Check(ReferenceEquals(bound, await Binder.BindAsync(deep)), "Completed binding publication is idempotent"u8);
        const int chainLength = 3000;
        var chain = new Dictionary<Utf8String, byte[]>();
        for (int i = 0; i < chainLength; i++)
            chain[Utf8String.Copy("/chain/"u8) + i + ".ts"u8] = Wtf8.Encode(i + 1 < chainLength ? "import './" + (i + 1) + "';" : "export {};");
        var chainOptions = new CompilerOptions();
        chainOptions.Set("noLib"u8, Json("true"u8));
        var chainConfig = new ParsedConfig("/chain/tsconfig.json"u8, chainOptions, ["/chain/0.ts"u8], [], [], []);
        var graph = await CompilerProgram.CreateAsync(new MemoryFileSystem(chain), "/chain"u8, chainConfig);
        Check(graph.SourceFiles.Count == chainLength && graph.SourceFiles[0].Syntax.FileName == "/chain/2999.ts"u8
            && graph.SourceFiles[^1].Syntax.FileName == "/chain/0.ts"u8, "Deep import graph uses deterministic iterative postorder"u8);
        var jsOptions = new CompilerOptions();
        jsOptions.Set("noLib"u8, Json("true"u8));
        jsOptions.Set("allowJs"u8, Json("true"u8));
        jsOptions.Set("module"u8, Json("\"commonjs\""u8));
        var jsRoots = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.js"u8] = Wtf8.Encode("import { a } from 'b'; import { c } from 'c';"),
            ["/project/node_modules/b.ts"u8] = Wtf8.Encode("var a = 10;"),
            ["/project/node_modules/c.js"u8] = Wtf8.Encode("exports.a = 10;")
        }), "/project"u8, new("/project/tsconfig.json"u8, jsOptions,
            ["/project/main.js"u8, "/project/node_modules/b.ts"u8, "/project/node_modules/c.js"u8], [], [], []));
        Check(jsRoots.SourceFiles.Select(f => f.Syntax.FileName).SequenceEqual(
            [Utf8String.Copy("/project/node_modules/b.ts"u8), Utf8String.Copy("/project/node_modules/c.js"u8), Utf8String.Copy("/project/main.js"u8)]),
            "JavaScript depth elision retains dependency ordering when the target is also a root"u8);
        foreach (bool allow in new[] { false, true })
        {
            var arbitraryOptions = new CompilerOptions();
            arbitraryOptions.Set("noLib"u8, Json("true"u8));
            arbitraryOptions.Set("module"u8, Json("\"nodenext\""u8));
            arbitraryOptions.Set("allowArbitraryExtensions"u8, Json(allow ? Utf8String.Copy("true"u8) : Utf8String.Copy("false"u8)));
            var arbitrary = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
            {
                ["/project/main.ts"u8] = Wtf8.Encode("import mod = require('./native.node'); mod.value;"),
                ["/project/native.d.node.ts"u8] = Wtf8.Encode("export const value: number;")
            }), "/project"u8, new("/project/tsconfig.json"u8, arbitraryOptions, ["/project/main.ts"u8], [], [], []));
            Check(arbitrary.SourceFiles.Count == (allow ? 2 : 1), "Arbitrary-extension imports respect the graph inclusion option"u8);
            var checker = await arbitrary.CreateCheckerAsync();
            await checker.CheckProgramAsync();
            Check(checker.DiagnosticCodesForProgramFile(arbitrary.GetFile("/project/main.ts"u8)!.Syntax)
                .SequenceEqual(allow ? [] : new[] { DiagnosticCode.Module0WasResolvedTo1ButAllowArbitraryExtensionsIsNotSet }),
                "Disallowed arbitrary-extension imports report the option diagnostic"u8);
        }
        Console.WriteLine(
            $"Program reuse, invalidation and stack safety: {assertions} assertions; {depth} syntax levels; {chainLength} files");
    }

    private sealed class ObservedFileSystem(IFileSystem inner, Action<Utf8String> beforeRead) : IFileSystem
    {
        public bool CaseSensitive => inner.CaseSensitive;
        public byte[]? ReadFile(Utf8String path) { beforeRead(path); return inner.ReadFile(path); }
        public bool FileExists(Utf8String path) => inner.FileExists(path);
        public bool DirectoryExists(Utf8String path) => inner.DirectoryExists(path);
        public Utf8String RealPath(Utf8String path) => inner.RealPath(path);
        public DirectoryEntries GetAccessibleEntries(Utf8String path) => inner.GetAccessibleEntries(path);
        public FileEntry? Stat(Utf8String path) => inner.Stat(path);
        public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents) => inner.WriteFile(path, contents);
        public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents) => inner.AppendFile(path, contents);
        public void Remove(Utf8String path) => inner.Remove(path);
        public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc) =>
            inner.SetTimes(path, accessTimeUtc, writeTimeUtc);
    }

    private sealed class RejectContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            throw new InvalidOperationException("Captured hostile synchronization context");
    }

    internal static async Task Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var input = document.RootElement;
            Utf8String directory = TypeScript.Compiler.Configuration.JsonStrings.GetString(input.GetProperty("directory"u8))!;
            var files = new Dictionary<Utf8String, byte[]>();
            foreach (var file in input.GetProperty("files"u8).EnumerateObject())
                files[JsonStrings.GetName(file)] = JsonStrings.GetString(file.Value).Span.ToArray();
            var links = new Dictionary<Utf8String, Utf8String>();
            if (input.TryGetProperty("symlinks"u8, out var symlinks))
                foreach (var link in symlinks.EnumerateObject())
                    links[JsonStrings.GetName(link)] = JsonStrings.GetString(link.Value);
            Utf8String config;
            if (input.TryGetProperty("config"u8, out var configName))
                config = TypeScript.Compiler.Configuration.JsonStrings.GetString(configName)!;
            else
            {
                config = Utf8String.Concat(directory, "/tsconfig.json"u8);
                using var configStream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(configStream))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("compilerOptions"u8);
                    input.GetProperty("options"u8).WriteTo(writer);
                    writer.WritePropertyName("files"u8);
                    input.GetProperty("roots"u8).WriteTo(writer);
                    writer.WriteEndObject();
                }
                files[config] = configStream.ToArray();
            }
            var fs = new LibraryFileSystem(new MemoryFileSystem(files, input.GetProperty("sensitive"u8).GetBoolean(), directory, links));
            var parsed = new ConfigParser(fs, directory).Parse(config);
            int concurrency = input.TryGetProperty("concurrency"u8, out var count) ? count.GetInt32() : 4;
            var program = await CompilerProgram.CreateAsync(fs, directory, parsed,
                useProjectReferenceSources: input.TryGetProperty("useSources"u8, out var source) && source.GetBoolean(),
                concurrency: concurrency).ConfigureAwait(false);
            var diagnosticChecker = program.IncludeDiagnostics.Count == 0 ? null : await program.CreateCheckerAsync().ConfigureAwait(false);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                writer.WriteStartArray();
                foreach (var file in program.SourceFiles)
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(file.Syntax.FileName);
                    writer.WriteBooleanValue(file.Binding.IsModule);
                    writer.WriteNumberValue((int)file.ImpliedFormat);
                    writer.WriteStringValue(file.PackageType);
                    writer.WriteStartArray();
                    foreach (var import in file.Resolutions.Where(r => !r.TypeReference && !r.Augmentation && r.Node is not null))
                    {
                        writer.WriteStartArray();
                        writer.WriteStringValue(import.Specifier);
                        writer.WriteStringValue(import.Resolution.FileName);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteStartArray();
                foreach (var error in program.Diagnostics)
                    writer.WriteNumberValue((int)error.Code);
                foreach (var file in program.SourceFiles)
                    foreach (var error in DiagnosticCollection.SortAndDeduplicate(diagnosticChecker?.IncludeDiagnosticsForProgramFile(file.Syntax) ?? []))
                        writer.WriteNumberValue((int)error.Code);
                writer.WriteEndArray();
                writer.WriteEndArray();
            }
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }
}

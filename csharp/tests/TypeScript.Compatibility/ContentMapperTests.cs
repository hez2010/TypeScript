using System.Collections.Concurrent;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class ContentMapperTests
{
    internal static async Task Run(Utf8String repository)
    {
        int assertions = 0;
        void Check(bool valid, Utf8String message)
        {
            assertions++;
            if (!valid)
                throw new InvalidDataException(message.ToString());
        }
        static JsonElement Json(Utf8String text)
        {
            using var document = JsonDocument.Parse(text.Memory);
            return document.RootElement.Clone();
        }
        static Utf8String Quoted(Utf8String value)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                writer.WriteStringValue(value);
            return Utf8String.FromString(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }
        Utf8String fixture = Utf8String.FromString(Path.GetFullPath(Path.Combine(repository.ToString(), "csharp/tests/fixtures/mappers/mapper.mjs")));
        Utf8String artifacts = Utf8String.FromString(Path.GetFullPath(Path.Combine(repository.ToString(), "built/csharp/mapper-artifacts")));
        Directory.CreateDirectory(artifacts.ToString());
        var options = new CompilerOptions();
        options.Set("runExternalCode"u8, Json("true"u8));
        options.Set("noLib"u8, Json("true"u8));
        options.Set("target"u8, Json("\"esnext\""u8));
        options.Set("strict"u8, Json("true"u8));
        ContentMapper Mapper(Utf8String? json = null, bool dynamic = false, Utf8String? encoding = null, Utf8String? source = null) =>
            new(
                "fixture"u8,
                [".view"u8],
                Json(json ?? Utf8String.Copy("{}"u8)),
                repository,
                "fixture"u8,
                "1"u8,
                ["node"u8, fixture, encoding ?? Utf8String.Copy("utf-8"u8), source ?? Utf8String.Copy("fixture"u8)],
                Json("[\"target\",\"strict\"]"u8),
                dynamic);
        ParsedConfig Config(ContentMapper mapper, Utf8String? name = null, Utf8String[]? files = null) =>
            new(name ?? Utf8String.Copy("/project/tsconfig.json"u8), options, files ?? [], [], [], []) { ContentMappers = [mapper] };
        static JsonElement State(MapperResult result)
        {
            Utf8String text = result.Canonical.Text.Text;
            int start = text.Span.IndexOf((byte)'{'), end = text.Span.IndexOf((byte)';');
            using var document = JsonDocument.Parse(text[start..end].Memory);
            return document.RootElement.Clone();
        }
        var events = new ConcurrentQueue<Utf8String>();
        var waitArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var host = new ContentMapperHost(log: line =>
        {
            events.Enqueue(line);
            if (line.Contains("/project/wait.view"u8, StringComparison.Ordinal))
                waitArrived.TrySetResult();
        }))
        {
            var mapper = Mapper();
            var config = Config(mapper);
            var first = await host.GetProjectAsync(config);
            var second = await host.GetProjectAsync(config);
            Utf8String identity = await first.IdentityAsync(mapper);
            Check(
                host.Timings().Count == 0 && identity.StartsWith("fixture@1:"u8, StringComparison.Ordinal),
                "Static identities do not start a process"u8);
            var left = State(await first.TransformAsync(mapper, "/project/a.view"u8, new SourceText("x"u8)));
            var right = State(await second.TransformAsync(mapper, "/project/b.view"u8, new SourceText("y"u8)));
            Check(left.GetProperty("pid"u8).GetInt32() == right.GetProperty("pid"u8).GetInt32(), "Equivalent leases share a process"u8);
            Check(
                JsonStrings.GetString(left.GetProperty("handle"u8)) == JsonStrings.GetString(right.GetProperty("handle"u8))
                    && right.GetProperty("openCount"u8).GetInt32() == 1,
                "Equivalent leases share one mapper project"u8);
            Check(left.GetProperty("compilerOptions"u8).GetProperty("target"u8).ValueKind == JsonValueKind.Number
                && left.GetProperty("compilerOptions"u8).GetProperty("strict"u8).GetBoolean(),
                "Declared compiler options retain wire enum values"u8);
            Check(left.GetProperty("compilerOptions"u8).GetProperty("noLib"u8).GetBoolean(), "Project opening includes undeclared options"u8);
            using var declaredOptions = JsonDocument.Parse(ContentMapperHost.DeclaredOptions(mapper, options));
            Check(!declaredOptions.RootElement.TryGetProperty("noLib"u8, out _), "Transform identity includes only declared options"u8);
            await first.DisposeAsync();
            Check(
                State(await second.TransformAsync(mapper, "/project/b.view"u8, new SourceText("y"u8))).GetProperty("openCount"u8).GetInt32() == 1,
                "Disposing one lease preserves another"u8);
            using var cancel = new CancellationTokenSource();
            var waiting = second.TransformAsync(mapper, "/project/wait.view"u8, new SourceText("wait"u8), cancel.Token).AsTask();
            await waitArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            try
            {
                await waiting;
                throw new InvalidDataException("Expected cancellation");
            }
            catch (OperationCanceledException)
            {
                assertions++;
            }
            Check(
                (await second.TransformAsync(mapper, "/project/b.view"u8, new SourceText("y"u8))).Canonical.Text.Length > 0,
                "Cancellation preserves connection framing"u8);
            await second.RefreshAsync();
            var refreshed = State(await second.TransformAsync(mapper, "/project/b.view"u8, new SourceText("y"u8)));
            Check(
                refreshed.GetProperty("openCount"u8).GetInt32() == 2 && refreshed.GetProperty("closeCount"u8).GetInt32() == 1,
                "Refresh closes and reopens the project"u8);
            await host.SetLocaleAsync("ja-JP"u8);
            var localized = State(await second.TransformAsync(mapper, "/project/b.view"u8, new SourceText("y"u8)));
            Check(
                localized.GetProperty("pid"u8).GetInt32() != left.GetProperty("pid"u8).GetInt32()
                    && JsonStrings.GetString(localized.GetProperty("locale"u8)) == "ja-JP"u8,
                "Locale changes restart processes and project handles"u8);
            await second.DisposeAsync();
            Check(host.Timings().Where(t => t.Operation == "closeProject"u8).Sum(t => t.Count) == 2, "Last lease closes its project"u8);
        }
        Utf8String dynamicFile = Utf8String.FromString(Path.Combine(artifacts.ToString(), "mapper.config"));
        await File.WriteAllTextAsync(dynamicFile.ToString(), "first");
        await using (var host = new ContentMapperHost())
        {
            var mapper = Mapper(Utf8String.Copy("{\"dynamicFile\":"u8) + Quoted(dynamicFile) + "}"u8, true);
            await using var project = await host.GetProjectAsync(Config(mapper));
            Utf8String first = await project.IdentityAsync(mapper);
            Check((await project.WatchedFilesAsync()).SequenceEqual([dynamicFile]), "Dynamic watch dependencies are retained"u8);
            await File.WriteAllTextAsync(dynamicFile.ToString(), "second");
            await project.RefreshAsync();
            Check(await project.IdentityAsync(mapper) != first, "Dynamic configuration invalidates transform identity"u8);
        }
        var openArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var host = new ContentMapperHost(log: line =>
        {
            if (line.Contains("openProject"u8, StringComparison.Ordinal))
                openArrived.TrySetResult();
        }))
        {
            var mapper = Mapper("{\"openDelay\":100}"u8);
            await using var project = await host.GetProjectAsync(Config(mapper));
            using var cancellation = new CancellationTokenSource();
            var operation = project.TransformAsync(mapper, "/project/a.view"u8, new SourceText("first"u8), cancellation.Token).AsTask();
            await openArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try
            {
                await operation;
                throw new InvalidDataException("Expected open cancellation");
            }
            catch (OperationCanceledException)
            {
                assertions++;
            }
            Check(
                State(
                    await project.TransformAsync(
                        mapper,
                        "/project/b.view"u8,
                        new SourceText("second"u8))).GetProperty("openCount"u8).GetInt32() == 1,
                "A canceled caller does not abandon or duplicate shared project initialization"u8);
            await using var other = await host.GetProjectAsync(Config(mapper, "/other/tsconfig.json"u8));
            var first = State(await project.TransformAsync(mapper, "/project/c.view"u8, new SourceText("first"u8)));
            var second = State(await other.TransformAsync(mapper, "/other/c.view"u8, new SourceText("second"u8)));
            Check(
                first.GetProperty("pid"u8).GetInt32() == second.GetProperty("pid"u8).GetInt32()
                    && JsonStrings.GetString(first.GetProperty("handle"u8)) != JsonStrings.GetString(second.GetProperty("handle"u8)),
                "Distinct projects share a process while retaining separate configuration handles"u8);
        }
        foreach (var (encoding, source) in new[] { (Utf8String.Copy("utf-16"u8), Utf8String.Copy("fixture"u8)), (Utf8String.Copy("utf-32"u8), Utf8String.Copy("fixture"u8)), (Utf8String.Copy("utf-8"u8), Utf8String.Copy("ts"u8)), (Utf8String.Copy("utf-8"u8), Utf8String.Copy("TypeScript"u8)), (Utf8String.Copy("utf-8"u8), Utf8String.Copy(" "u8)) })
        {
            await using var host = new ContentMapperHost();
            var mapper = Mapper(encoding: encoding, source: source);
            await using var project = await host.GetProjectAsync(Config(mapper));
            try
            {
                await project.TransformAsync(mapper, "/project/a.view"u8, new SourceText("x"u8));
                throw new InvalidDataException("Expected invalid handshake");
            }
            catch (MapperException e) when (e.Stage == MapperFailure.Initialize)
            {
                assertions++;
            }
        }
        {
            await using var host = new ContentMapperHost();
            var mapper = Mapper(
                "{\"result\":{\"text\":\"😀\",\"extension\":\".ts\",\"mappings\":[[0,4,0,4,0]],\"diagnostics\":[{\"start\":0,\"length\":4,\"code\":44,\"messageText\":\"message\"}]}}"u8);
            await using var project = await host.GetProjectAsync(Config(mapper));
            var mapped = await project.TransformAsync(mapper, "/project/a.view"u8, new SourceText("😀"u8));
            Check(
                mapped.Canonical.Mappings.Segments[0].VirtualEnd == 4 && mapped.Diagnostics[0].Length == 4,
                "Mapper coordinates are UTF-8 byte offsets"u8);
        }
        var invalidResults = new Utf8String[]        {
            "{\"text\":\"😀\",\"extension\":\".ts\",\"mappings\":[[1,1,0,2,1]]}"u8,
            "{\"text\":\"x\",\"extension\":\".bad\"}"u8,
            "{\"text\":\"x\",\"extension\":\".ts\",\"mappings\":[[0,1,0,2,0]]}"u8,
            "{\"text\":\"x\",\"extension\":\".ts\",\"diagnosticDirectives\":{\"directives\":[[0,1,0,1,1]]}}"u8,
            "{\"text\":\"x\",\"extension\":\".ts\",\"diagnosticDirectives\":{\"directives\":[[0,1,0,1,7]]}}"u8,
            "{\"text\":\"xx\",\"extension\":\".ts\",\"diagnosticDirectives\":{\"directives\":[[0,1,0,2,0],[0,1,1,2,0]]}}"u8
        };
        foreach (Utf8String invalid in invalidResults)
        {
            await using var host = new ContentMapperHost();
            var mapper = Mapper(Utf8String.Copy("{\"result\":"u8) + invalid + "}"u8);
            await using var project = await host.GetProjectAsync(Config(mapper));
            try
            {
                await project.TransformAsync(mapper, "/project/a.view"u8, new SourceText("😀"u8));
                throw new InvalidDataException("Expected invalid mapping response");
            }
            catch (Exception e) when (e is MapperException or MappingException)
            {
                assertions++;
            }
        }
        await using (var host = new ContentMapperHost())
        {
            var mapper = Mapper(
                "{\"result\":{\"text\":\"import './dep'; export const value=1;\",\"extension\":\".ts\",\"supplemental\":[{\"text\":\"export const extra=1;\",\"extension\":\".ts\"}]}}"u8);
            var config = Config(mapper, files: ["/project/main.view"u8]);
            var fs = new MemoryFileSystem(
                new Dictionary<Utf8String, byte[]>
                {
                    ["/project/main.view"u8] = Wtf8.Encode("<view>😀</view>"),
                    ["/project/dep.ts"u8] = Wtf8.Encode("export const dep=1;")
                });
            await using var project = await host.GetProjectAsync(config);
            var program = await CompilerProgram.CreateAsync(fs, "/project"u8, config, mapperProject: project);
            Check(
                program.SourceFiles.Select(f => f.Syntax.FileName).SequenceEqual(
                    [Utf8String.Copy("/project/dep.ts"u8), Utf8String.Copy("/project/main.view.0.ts"u8), Utf8String.Copy("/project/main.view"u8)]),
                "Mapped imports and supplemental files enter the graph deterministically"u8);
            Check(
                program.GetFile("/project/main.view"u8)!.Mapping?.Original.Text == "<view>😀</view>"u8,
                "Mapped source retains original content"u8);
            Check(
                program.SourceFiles.All(
                    f => f.Syntax.DescendantsAndSelf().All(n => Enumerable.Range(0, n.ChildCount).All(i => n.GetChild(i).Parent == n))),
                "Mapped trees retain parent identity"u8);
            var next = await CompilerProgram.CreateAsync(fs, "/project"u8, config, program, mapperProject: project);
            Check(
                ReferenceEquals(program.GetFile("/project/main.view"u8)!.Syntax, next.GetFile("/project/main.view"u8)!.Syntax),
                "Unchanged mapper identity and output retain syntax and binding ownership"u8);
        }
        await using (var host = new ContentMapperHost())
        {
            var mapper = Mapper("{\"result\":{\"text\":\"x\",\"extension\":\".invalid\"}}"u8);
            var roots = Enumerable.Range(0, 8).Select(i => Utf8String.Concat("/project/"u8, Utf8String.Format(i), ".view"u8)).ToArray();
            var config = Config(mapper, files: roots);
            var fs = new MemoryFileSystem(roots.ToDictionary(path => path, _ => Wtf8.Encode("original")));
            await using var project = await host.GetProjectAsync(config);
            var program = await CompilerProgram.CreateAsync(fs, "/project"u8, config, mapperProject: project);
            Check(
                program.SourceFiles.Count == 8
                    && program.SourceFiles.All(f => f.Syntax.Source.Length == 0 && f.Mapping?.Original.Text == "original"u8),
                "Failed transforms retain all original source owners"u8);
            Check(host.Timings().Where(t => t.Operation == "transform"u8).Sum(t => t.Count) == 5
                && program.Diagnostics.Count(d => d.Code == Messages.The_content_mapper_0_failed_1_times_and_will_not_be_used.Code) == 1,
                "Mapper failure budget disables exactly once after five failures"u8);
        }
        await using (var host = new ContentMapperHost())
        {
            var mapper = Mapper();
            await using var project = await host.GetProjectAsync(Config(mapper));
            foreach (Utf8String text in new Utf8String[] { "crash"u8, "after-crash"u8 })
            {
                try
                {
                    await project.TransformAsync(mapper, "/project/a.view"u8, new SourceText(text));
                    throw new InvalidDataException("Expected mapper process failure");
                }
                catch (MapperException e) when (e.Stage == MapperFailure.Request)
                {
                    assertions++;
                }
            }
        }
        Utf8String physicalProject = Utf8String.FromString(Path.Combine(artifacts.ToString(), "project-" + Guid.NewGuid().ToString("N")));
        Utf8String packageDirectory = Utf8String.FromString(Path.Combine(physicalProject.ToString(), "node_modules", "fixture"));
        Directory.CreateDirectory(packageDirectory.ToString());
        await File.WriteAllTextAsync(
            Path.Combine(packageDirectory.ToString(), "package.json"),
            (Utf8String.Copy("{\"name\":\"fixture\",\"version\":\"1\",\"typescript\":{\"contentMapper\":{\"exec\":[\"node\","u8) + Quoted(fixture) + "]}}}"u8).ToString());
        await File.WriteAllTextAsync(
            Path.Combine(physicalProject.ToString(), "tsconfig.json"),
            "{\"compilerOptions\":{\"noLib\":true,\"noEmit\":true},\"include\":[\"*.view\"],\"contentMappers\":[{\"package\":\"fixture\",\"extensions\":[\".view\"]}]}");
        await File.WriteAllTextAsync(Path.Combine(physicalProject.ToString(), "main.view"), "original");
        var physical = new PhysicalFileSystem();
        var parsed = new ConfigParser(physical, physicalProject).Parse(CompilerPath.Combine(physicalProject, "tsconfig.json"u8), options);
        Check(
            parsed.ContentMappers.Length == 1 && parsed.FileNames.Length == 1 && parsed.Diagnostics.Length == 0,
            "Configuration resolves mapper manifests and discovers registered foreign extensions"u8);
        var physicalProgram = await CompilerProgram.CreateAsync(physical, physicalProject, parsed);
        Check(
            physicalProgram.SourceFiles.Single().Mapping is not null,
            "Owned mapper host executes a configuration-discovered package and closes it after construction"u8);
        var syntax = Parser.ParseSourceFile(new("/project/test.view"u8, ScriptKind.TS), new SourceText("__field + generated"u8));
        var map = new MappedSourceFile(syntax, new SourceText("field"u8), new SpanMap([new(0, 7, 0, 5, MappingKind.Alias)]),
            "/project/test.view.ts"u8,
            "fixture@1"u8,
            "identity"u8,
            [new(0, 5, 0, 7, true, "fixture"u8, (DiagnosticCode)777, "Unused expectation"u8)]);
        var error = new Diagnostic(Messages.Cannot_find_name_0, 0, 7, ["__field"u8]) { FileName = syntax.FileName };
        var presentation = map.Present(error);
        Check(presentation.Length == 5 && presentation.Message.Contains("field"u8, StringComparison.Ordinal)
            && !presentation.Message.Contains("__field"u8, StringComparison.Ordinal) && error.Arguments[0] == "__field"u8,
            "Alias presentation preserves stored diagnostic arguments"u8);
        Check(map.Present(error with { Start = 10, Length = 3 }).Synthesized, "Diagnostics in synthesized gaps retain virtual text"u8);
        Check(map.ApplyDiagnosticDirectives([error]).Count == 0, "Mapped expect directives consume compiler diagnostics"u8);
        var external = error with { Source = "fixture"u8, Length = 5 };
        var remaining = map.ApplyDiagnosticDirectives([external]);
        Check(
            remaining.Count == 2 && (int)remaining[1].Code == 777 && remaining[1].Source == "fixture"u8,
            "Mapper errors do not satisfy an expected compiler diagnostic"u8);
        Check(ReferenceEquals(map.Present(external).Text, map.Original), "Mapper-authored ranges are already in original coordinates"u8);
        Console.WriteLine($"Content mapper lifecycle and graph: {assertions} assertions");
    }
}

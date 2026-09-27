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
    internal static async Task Run(string repository)
    {
        int assertions = 0;
        void Check(bool valid, string message)
        {
            assertions++;
            if (!valid)
                throw new InvalidDataException(message);
        }
        static JsonElement Json(string text)
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        static string Quoted(string value)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                writer.WriteStringValue(value);
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        string fixture = Path.GetFullPath(Path.Combine(repository, "csharp/tests/fixtures/mappers/mapper.mjs"));
        string artifacts = Path.GetFullPath(Path.Combine(repository, "built/csharp/mapper-artifacts"));
        Directory.CreateDirectory(artifacts);
        var options = new CompilerOptions();
        options.Set("runExternalCode", Json("true"));
        options.Set("noLib", Json("true"));
        options.Set("target", Json("\"esnext\""));
        options.Set("strict", Json("true"));
        ContentMapper Mapper(string json = "{}", bool dynamic = false, string encoding = "utf-16", string source = "fixture") =>
            new(
                "fixture",
                [".view"],
                Json(json),
                repository,
                "fixture",
                "1",
                ["node", fixture, encoding, source],
                Json("[\"target\",\"strict\"]"),
                dynamic);
        ParsedConfig Config(ContentMapper mapper, string name = "/project/tsconfig.json", string[]? files = null) =>
            new(name, options, files ?? [], [], [], []) { ContentMappers = [mapper] };
        static JsonElement State(MapperResult result)
        {
            string text = result.Canonical.Text.Text;
            int start = text.IndexOf('{'), end = text.IndexOf(';');
            using var document = JsonDocument.Parse(text[start..end]);
            return document.RootElement.Clone();
        }
        var events = new ConcurrentQueue<string>();
        var waitArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var host = new ContentMapperHost(log: line =>
        {
            events.Enqueue(line);
            if (line.Contains("/project/wait.view", StringComparison.Ordinal))
                waitArrived.TrySetResult();
        }))
        {
            var mapper = Mapper();
            var config = Config(mapper);
            var first = await host.GetProjectAsync(config);
            var second = await host.GetProjectAsync(config);
            string identity = await first.IdentityAsync(mapper);
            Check(
                host.Timings().Count == 0 && identity.StartsWith("fixture@1:", StringComparison.Ordinal),
                "Static identities do not start a process");
            var left = State(await first.TransformAsync(mapper, "/project/a.view", new SourceText("x")));
            var right = State(await second.TransformAsync(mapper, "/project/b.view", new SourceText("y")));
            Check(left.GetProperty("pid").GetInt32() == right.GetProperty("pid").GetInt32(), "Equivalent leases share a process");
            Check(
                left.GetProperty("handle").GetString() == right.GetProperty("handle").GetString()
                    && right.GetProperty("openCount").GetInt32() == 1,
                "Equivalent leases share one mapper project");
            Check(left.GetProperty("compilerOptions").GetProperty("target").ValueKind == JsonValueKind.Number
                && left.GetProperty("compilerOptions").GetProperty("strict").GetBoolean(),
                "Declared compiler options retain wire enum values");
            Check(left.GetProperty("compilerOptions").GetProperty("noLib").GetBoolean(), "Project opening includes undeclared options");
            using var declaredOptions = JsonDocument.Parse(ContentMapperHost.DeclaredOptions(mapper, options));
            Check(!declaredOptions.RootElement.TryGetProperty("noLib", out _), "Transform identity includes only declared options");
            await first.DisposeAsync();
            Check(
                State(await second.TransformAsync(mapper, "/project/b.view", new SourceText("y"))).GetProperty("openCount").GetInt32() == 1,
                "Disposing one lease preserves another");
            using var cancel = new CancellationTokenSource();
            var waiting = second.TransformAsync(mapper, "/project/wait.view", new SourceText("wait"), cancel.Token).AsTask();
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
                (await second.TransformAsync(mapper, "/project/b.view", new SourceText("y"))).Canonical.Text.Length > 0,
                "Cancellation preserves connection framing");
            await second.RefreshAsync();
            var refreshed = State(await second.TransformAsync(mapper, "/project/b.view", new SourceText("y")));
            Check(
                refreshed.GetProperty("openCount").GetInt32() == 2 && refreshed.GetProperty("closeCount").GetInt32() == 1,
                "Refresh closes and reopens the project");
            await host.SetLocaleAsync("ja-JP");
            var localized = State(await second.TransformAsync(mapper, "/project/b.view", new SourceText("y")));
            Check(
                localized.GetProperty("pid").GetInt32() != left.GetProperty("pid").GetInt32()
                    && localized.GetProperty("locale").GetString() == "ja-JP",
                "Locale changes restart processes and project handles");
            await second.DisposeAsync();
            Check(host.Timings().Where(t => t.Operation == "closeProject").Sum(t => t.Count) == 2, "Last lease closes its project");
        }
        string dynamicFile = Path.Combine(artifacts, "mapper.config");
        await File.WriteAllTextAsync(dynamicFile, "first");
        await using (var host = new ContentMapperHost())
        {
            var mapper = Mapper("{\"dynamicFile\":" + Quoted(dynamicFile) + "}", true);
            await using var project = await host.GetProjectAsync(Config(mapper));
            string first = await project.IdentityAsync(mapper);
            Check((await project.WatchedFilesAsync()).SequenceEqual([dynamicFile]), "Dynamic watch dependencies are retained");
            await File.WriteAllTextAsync(dynamicFile, "second");
            await project.RefreshAsync();
            Check(await project.IdentityAsync(mapper) != first, "Dynamic configuration invalidates transform identity");
        }
        var openArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var host = new ContentMapperHost(log: line =>
        {
            if (line.Contains("openProject", StringComparison.Ordinal))
                openArrived.TrySetResult();
        }))
        {
            var mapper = Mapper("{\"openDelay\":100}");
            await using var project = await host.GetProjectAsync(Config(mapper));
            using var cancellation = new CancellationTokenSource();
            var operation = project.TransformAsync(mapper, "/project/a.view", new SourceText("first"), cancellation.Token).AsTask();
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
                        "/project/b.view",
                        new SourceText("second"))).GetProperty("openCount").GetInt32() == 1,
                "A canceled caller does not abandon or duplicate shared project initialization");
            await using var other = await host.GetProjectAsync(Config(mapper, "/other/tsconfig.json"));
            var first = State(await project.TransformAsync(mapper, "/project/c.view", new SourceText("first")));
            var second = State(await other.TransformAsync(mapper, "/other/c.view", new SourceText("second")));
            Check(
                first.GetProperty("pid").GetInt32() == second.GetProperty("pid").GetInt32()
                    && first.GetProperty("handle").GetString() != second.GetProperty("handle").GetString(),
                "Distinct projects share a process while retaining separate configuration handles");
        }
        foreach (var (encoding, source) in new[] { ("utf-32", "fixture"), ("utf-8", "ts"), ("utf-8", "TypeScript"), ("utf-8", " ") })
        {
            await using var host = new ContentMapperHost();
            var mapper = Mapper(encoding: encoding, source: source);
            await using var project = await host.GetProjectAsync(Config(mapper));
            try
            {
                await project.TransformAsync(mapper, "/project/a.view", new SourceText("x"));
                throw new InvalidDataException("Expected invalid handshake");
            }
            catch (MapperException e) when (e.Stage == MapperFailure.Initialize)
            {
                assertions++;
            }
        }
        foreach (string encoding in new[] { "utf-8", "utf-16" })
        {
            await using var host = new ContentMapperHost();
            int length = encoding == "utf-8" ? 4 : 2;
            var mapper = Mapper(
                "{\"result\":{\"text\":\"😀\",\"extension\":\".ts\",\"mappings\":[[0," + length + ",0," + length + ",0]],\"diagnostics\":[{\"start\":0,\"length\":" + length + ",\"code\":44,\"messageText\":\"message\"}]}}",
                encoding: encoding);
            await using var project = await host.GetProjectAsync(Config(mapper));
            var mapped = await project.TransformAsync(mapper, "/project/a.view", new SourceText("😀"));
            Check(
                mapped.Canonical.Mappings.Segments[0].VirtualEnd == 4 && mapped.Diagnostics[0].Length == 4,
                "Mapper coordinates normalize to bytes: " + encoding);
        }
        var invalidResults = new[]
        {
            "{\"text\":\"😀\",\"extension\":\".ts\",\"mappings\":[[1,1,0,2,1]]}",
            "{\"text\":\"x\",\"extension\":\".bad\"}",
            "{\"text\":\"x\",\"extension\":\".ts\",\"mappings\":[[0,1,0,2,0]]}",
            "{\"text\":\"x\",\"extension\":\".ts\",\"diagnosticDirectives\":{\"directives\":[[0,1,0,1,1]]}}",
            "{\"text\":\"x\",\"extension\":\".ts\",\"diagnosticDirectives\":{\"directives\":[[0,1,0,1,7]]}}",
            "{\"text\":\"xx\",\"extension\":\".ts\",\"diagnosticDirectives\":{\"directives\":[[0,1,0,2,0],[0,1,1,2,0]]}}"
        };
        foreach (string invalid in invalidResults)
        {
            await using var host = new ContentMapperHost();
            var mapper = Mapper("{\"result\":" + invalid + "}");
            await using var project = await host.GetProjectAsync(Config(mapper));
            try
            {
                await project.TransformAsync(mapper, "/project/a.view", new SourceText("😀"));
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
                "{\"result\":{\"text\":\"import './dep'; export const value=1;\",\"extension\":\".ts\",\"supplemental\":[{\"text\":\"export const extra=1;\",\"extension\":\".ts\"}]}}");
            var config = Config(mapper, files: ["/project/main.view"]);
            var fs = new MemoryFileSystem(
                new Dictionary<string, byte[]>
                {
                    ["/project/main.view"] = Wtf8.Encode("<view>😀</view>"),
                    ["/project/dep.ts"] = Wtf8.Encode("export const dep=1;")
                });
            await using var project = await host.GetProjectAsync(config);
            var program = await CompilerProgram.CreateAsync(fs, "/project", config, mapperProject: project);
            Check(
                program.SourceFiles.Select(f => f.Syntax.FileName).SequenceEqual(
                    ["/project/dep.ts", "/project/main.view.0.ts", "/project/main.view"]),
                "Mapped imports and supplemental files enter the graph deterministically");
            Check(
                program.GetFile("/project/main.view")!.Mapping?.Original.Text == "<view>😀</view>",
                "Mapped source retains original content");
            Check(
                program.SourceFiles.All(
                    f => f.Syntax.DescendantsAndSelf().All(n => Enumerable.Range(0, n.ChildCount).All(i => n.GetChild(i).Parent == n))),
                "Mapped trees retain parent identity");
            var next = await CompilerProgram.CreateAsync(fs, "/project", config, program, mapperProject: project);
            Check(
                ReferenceEquals(program.GetFile("/project/main.view")!.Syntax, next.GetFile("/project/main.view")!.Syntax),
                "Unchanged mapper identity and output retain syntax and binding ownership");
        }
        await using (var host = new ContentMapperHost())
        {
            var mapper = Mapper("{\"result\":{\"text\":\"x\",\"extension\":\".invalid\"}}");
            var roots = Enumerable.Range(0, 8).Select(i => "/project/" + i + ".view").ToArray();
            var config = Config(mapper, files: roots);
            var fs = new MemoryFileSystem(roots.ToDictionary(path => path, _ => Wtf8.Encode("original")));
            await using var project = await host.GetProjectAsync(config);
            var program = await CompilerProgram.CreateAsync(fs, "/project", config, mapperProject: project);
            Check(
                program.SourceFiles.Count == 8
                    && program.SourceFiles.All(f => f.Syntax.Source.Length == 0 && f.Mapping?.Original.Text == "original"),
                "Failed transforms retain all original source owners");
            Check(host.Timings().Where(t => t.Operation == "transform").Sum(t => t.Count) == 5
                && program.Diagnostics.Count(d => d.Code == Messages.The_content_mapper_0_failed_1_times_and_will_not_be_used.Code) == 1,
                "Mapper failure budget disables exactly once after five failures");
        }
        await using (var host = new ContentMapperHost())
        {
            var mapper = Mapper();
            await using var project = await host.GetProjectAsync(Config(mapper));
            foreach (string text in new[] { "crash", "after-crash" })
            {
                try
                {
                    await project.TransformAsync(mapper, "/project/a.view", new SourceText(text));
                    throw new InvalidDataException("Expected mapper process failure");
                }
                catch (MapperException e) when (e.Stage == MapperFailure.Request)
                {
                    assertions++;
                }
            }
        }
        string physicalProject = Path.Combine(artifacts, "project-" + Guid.NewGuid().ToString("N"));
        string packageDirectory = Path.Combine(physicalProject, "node_modules", "fixture");
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(packageDirectory, "package.json"),
            "{\"name\":\"fixture\",\"version\":\"1\",\"typescript\":{\"contentMapper\":{\"exec\":[\"node\"," + Quoted(fixture) + "]}}}");
        await File.WriteAllTextAsync(
            Path.Combine(physicalProject, "tsconfig.json"),
            "{\"compilerOptions\":{\"noLib\":true,\"noEmit\":true},\"include\":[\"*.view\"],\"contentMappers\":[{\"package\":\"fixture\",\"extensions\":[\".view\"]}]}");
        await File.WriteAllTextAsync(Path.Combine(physicalProject, "main.view"), "original");
        var physical = new PhysicalFileSystem();
        var parsed = new ConfigParser(physical, physicalProject).Parse(Path.Combine(physicalProject, "tsconfig.json"), options);
        Check(
            parsed.ContentMappers.Length == 1 && parsed.FileNames.Length == 1 && parsed.Diagnostics.Length == 0,
            "Configuration resolves mapper manifests and discovers registered foreign extensions");
        var physicalProgram = await CompilerProgram.CreateAsync(physical, physicalProject, parsed);
        Check(
            physicalProgram.SourceFiles.Single().Mapping is not null,
            "Owned mapper host executes a configuration-discovered package and closes it after construction");
        var syntax = Parser.ParseSourceFile(new("/project/test.view", ScriptKind.TS), new SourceText("__field + generated"));
        var map = new MappedSourceFile(syntax, new SourceText("field"), new SpanMap([new(0, 7, 0, 5, MappingKind.Alias)]),
            "/project/test.view.ts",
            "fixture@1",
            "identity",
            [new(0, 5, 0, 7, true, "fixture", (DiagnosticCode)777, "Unused expectation")]);
        var error = new Diagnostic(Messages.Cannot_find_name_0, 0, 7, ["__field"]) { FileName = syntax.FileName };
        var presentation = map.Present(error);
        Check(presentation.Length == 5 && presentation.Message.Contains("field", StringComparison.Ordinal)
            && !presentation.Message.Contains("__field", StringComparison.Ordinal) && error.Arguments[0] == "__field",
            "Alias presentation preserves stored diagnostic arguments");
        Check(map.Present(error with { Start = 10, Length = 3 }).Synthesized, "Diagnostics in synthesized gaps retain virtual text");
        Check(map.ApplyDiagnosticDirectives([error]).Count == 0, "Mapped expect directives consume compiler diagnostics");
        var external = error with { Source = "fixture", Length = 5 };
        var remaining = map.ApplyDiagnosticDirectives([external]);
        Check(
            remaining.Count == 2 && (int)remaining[1].Code == 777 && remaining[1].Source == "fixture",
            "Mapper errors do not satisfy an expected compiler diagnostic");
        Check(ReferenceEquals(map.Present(external).Text, map.Original), "Mapper-authored ranges are already in original coordinates");
        Console.WriteLine($"Content mapper lifecycle and graph: {assertions} assertions");
    }
}

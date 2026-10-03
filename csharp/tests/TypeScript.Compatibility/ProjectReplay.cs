using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Projects.TypeAcquisition;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class ProjectReplay
{
    internal static void SnapshotLines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer)) SnapshotsAsync(input.RootElement, writer).GetAwaiter().GetResult();
            Console.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
        }
    }

    private static Utf8String[] Strings(JsonElement value, string name) => value.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
        ? values.EnumerateArray().Select(JsonStrings.GetString).ToArray() : [];
    private static CompilerOptions Options(JsonElement value)
    {
        var result = new CompilerOptions();
        foreach (var property in value.EnumerateObject()) result.Set(JsonStrings.GetName(property), property.Value);
        return result;
    }
    private static CreateProgramRequest Program(JsonElement value) => new(Strings(value, "rootFiles"),
        value.TryGetProperty("compilerOptions", out var options) && options.ValueKind == JsonValueKind.Object ? Options(options) : new())
        { References = Strings(value, "references").Select(path => new ProjectReference(path)).ToArray() };

    private static async Task SnapshotsAsync(JsonElement input, Utf8JsonWriter writer)
    {
        Utf8String cwd = Text(input, "cwd", "/"u8);
        bool sensitive = input.TryGetProperty("caseSensitive", out var sensitivity) && sensitivity.GetBoolean();
        var memory = new MemoryFileSystem(input.GetProperty("files").EnumerateObject()
            .ToDictionary(JsonStrings.GetName, property => JsonStrings.GetString(property.Value).Span.ToArray()), sensitive, cwd,
            input.TryGetProperty("symlinks", out var links) ? links.EnumerateObject().ToDictionary(JsonStrings.GetName, property => JsonStrings.GetString(property.Value)) : null);
        foreach (var directory in Strings(input, "directories")) memory.CreateDirectory(directory);
        var typingLocation = Text(input, "typingsLocation");
        var mapperHost = new ContentMapperHost { StartProcess = StartMapper };
        await using var host = new ProjectSnapshotHost(new LibraryFileSystem(memory), new()
            { CurrentDirectory = cwd, TypingsLocation = typingLocation, RunExternalCode = input.TryGetProperty("runExternalCode", out var external) && external.GetBoolean() }, mapperHost);
        var npm = input.TryGetProperty("npm", out var npmSpec) && npmSpec.ValueKind == JsonValueKind.Object ? new FixtureNpm(memory, npmSpec) : null;
        await using var installer = npm is null ? null : new TypingsInstaller(memory, typingLocation, npm);
        var pendingTypings = new Dictionary<Utf8String, TypingsStateChange>();
        var snapshots = new List<ProjectWorkspaceSnapshot?> { await host.CreateAsync() };
        var identities = new Dictionary<CompilerProgram, int>(ReferenceEqualityComparer.Instance);
        try
        {
            writer.WriteStartArray();
            foreach (var step in input.GetProperty("steps").EnumerateArray())
            {
                int basis = step.TryGetProperty("base", out var parent) ? parent.GetInt32() : snapshots.Count - 1;
                foreach (var directory in Strings(step, "removeDirectories")) memory.Remove(directory);
                foreach (var directory in Strings(step, "createDirectories")) memory.CreateDirectory(directory);
                if (step.TryGetProperty("symlinks", out var symlinks))
                    foreach (var property in symlinks.EnumerateObject())
                        if (property.Value.ValueKind == JsonValueKind.Null) memory.Remove(JsonStrings.GetName(property));
                        else memory.CreateSymbolicLink(JsonStrings.GetName(property), JsonStrings.GetString(property.Value));
                if (step.TryGetProperty("edits", out var edits))
                    foreach (var property in edits.EnumerateObject())
                        if (npm is not null && CompilerPath.Contains(typingLocation, JsonStrings.GetName(property), sensitive)) continue;
                        else if (property.Value.ValueKind == JsonValueKind.Null) memory.Remove(JsonStrings.GetName(property));
                        else memory.WriteFile(JsonStrings.GetName(property), JsonStrings.GetString(property.Value).Span);
                var changes = Strings(step, "changed").Select(path => new FileChange(FileChangeKind.WatchChange, path))
                    .Concat(Strings(step, "created").Select(path => new FileChange(FileChangeKind.WatchCreate, path)))
                    .Concat(Strings(step, "deleted").Select(path => new FileChange(FileChangeKind.WatchDelete, path)))
                    .Concat(step.TryGetProperty("fileChanges", out var events) ? events.EnumerateArray().Select(Change) : []).ToArray();
                IFileSystem? replacement = step.TryGetProperty("fileSystem", out var replacementFiles)
                    ? new LibraryFileSystem(new MemoryFileSystem(replacementFiles.EnumerateObject()
                        .ToDictionary(JsonStrings.GetName, property => JsonStrings.GetString(property.Value).Span.ToArray()), sensitive, cwd)) : null;
                var request = new SnapshotRequest
                {
                    UserPreferences = step.TryGetProperty("preferences", out var preferences) ? new UserPreferences().WithConfig(preferences) : null,
                    OpenProjects = Strings(step, "openProjects"), CloseProjects = Strings(step, "closeProjects"),
                    OpenFiles = Strings(step, "openFiles"), CloseFiles = Strings(step, "closeFiles"),
                    RemovePrograms = Strings(step, "removePrograms"), EnsurePrograms = [.. Strings(step, "ensurePrograms"), .. Strings(step, "resourceProjects")],
                    EnsureFiles = [.. Strings(step, "ensureFiles"), .. Strings(step, "ensureEditorFiles")], EnsureConfiguredFiles = Strings(step, "ensureConfiguredFiles"),
                    CreatePrograms = step.TryGetProperty("createPrograms", out var create) ? create.EnumerateArray().Select(Program).ToArray() : [],
                    ReconfigurePrograms = step.TryGetProperty("reconfigurePrograms", out var reconfigure)
                        ? reconfigure.EnumerateArray().Select(value => new ReconfigureProgramRequest(Text(value, "id"), Program(value))).ToArray() : [],
                    EnsureAllPrograms = step.TryGetProperty("ensureAllPrograms", out var all) && all.GetBoolean(),
                    LoadProjectTrees = step.TryGetProperty("loadProjectTrees", out var trees) && trees.GetBoolean(),
                    RequestedProjectTrees = step.TryGetProperty("requestedProjectTrees", out var requestedTrees) && requestedTrees.ValueKind == JsonValueKind.Array
                        ? Strings(step, "requestedProjectTrees") : null,
                    InferredContentMappers = step.TryGetProperty("inferredContentMappers", out var mappers) ? mappers.EnumerateArray().Select(mapper =>
                        new ContentMapper(Text(mapper, "package"), Strings(mapper, "extensions"), Element(mapper, "options"), Text(mapper, "packageDirectory"),
                            Text(mapper, "name"), Text(mapper, "version"), Strings(mapper, "exec"), Element(mapper, "compilerOptions"),
                            mapper.GetProperty("dynamicConfig").GetBoolean()) { ContributionId = Text(mapper, "contributionId") }).ToArray() : null,
                    FileChanges = changes, FileSystem = replacement,
                    ReplaceFileSystem = step.TryGetProperty("replaceFileSystem", out var replace) && replace.GetBoolean(),
                    InvalidateAll = step.TryGetProperty("invalidateAll", out var invalidate) && invalidate.GetBoolean(),
                    Locale = step.TryGetProperty("locale", out var locale) ? JsonStrings.GetString(locale) : null,
                    CustomConfigFileName = step.TryGetProperty("customConfigFileName", out var custom) ? JsonStrings.GetString(custom) : null,
                    InferredOptions = step.TryGetProperty("inferredOptions", out var inferred) && inferred.ValueKind == JsonValueKind.Object ? Options(inferred) : null,
                    TypingsChanges = Strings(step, "acquireTypes").Select(id => pendingTypings.TryGetValue(id, out var change)
                        ? change : throw new InvalidOperationException($"No candidate typings result for {id}")).ToArray()
                };
                ProjectWorkspaceSnapshot snapshot;
                try { snapshot = await host.CreateAsync(request, snapshots[basis]); }
                catch (ArgumentException)
                {
                    snapshots.Add(null); writer.WriteStartObject(); writer.WriteBoolean("error", true); writer.WriteEndObject(); continue;
                }
                snapshots.Add(snapshot);
                writer.WriteStartObject(); writer.WriteStartArray("projects");
                foreach (var project in snapshot.Projects)
                {
                    writer.WriteStartObject(); String(writer, "id", project.Id); writer.WriteNumber("kind", (int)project.Kind);
                    writer.WriteStartArray("roots");
                    foreach (var root in project.Configuration.FileNames) JsonStrings.WriteString(writer, root.Span);
                    writer.WriteEndArray(); String(writer, "config", project.Kind == ProjectKind.Configured ? project.Configuration.FileName : default);
                    int identity = 0;
                    if (project.Program is { } program && !identities.TryGetValue(program, out identity)) identities.Add(program, identity = identities.Count + 1);
                    writer.WriteNumber("program", identity); writer.WriteBoolean("dirty", project.IsDirty);
                    if (input.TryGetProperty("observeProgramUpdates", out var updates) && updates.GetBoolean())
                        writer.WriteNumber("updateKind", (int)project.ProgramUpdateKind);
                    writer.WriteStartArray("sources");
                    foreach (var file in project.Program?.SourceFiles ?? [])
                    {
                        if (file.Library) continue;
                        writer.WriteStartObject(); String(writer, "fileName", file.Syntax.FileName); String(writer, "text", file.Syntax.Source.Text);
                        writer.WriteNumber("kind", (int)file.Syntax.ScriptKind); writer.WriteEndObject();
                    }
                    writer.WriteEndArray(); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteStartObject("defaults");
                foreach (var name in Strings(step.TryGetProperty("queries", out _) ? step : input, "queries"))
                {
                    JsonStrings.WriteName(writer, name); JsonStrings.WriteString(writer, (snapshot.GetDefaultProject(name)?.Id ?? Utf8String.Empty).Span);
                }
                writer.WriteEndObject(); writer.WriteStartArray("created");
                foreach (var project in snapshot.CreatedPrograms) JsonStrings.WriteString(writer, project.Id.Span);
                writer.WriteEndArray();
                if (input.TryGetProperty("observePushDiagnostics", out var observePush) && observePush.GetBoolean())
                {
                    writer.WriteStartArray("pushes");
                    if (input.TryGetProperty("pushDiagnostics", out var push) && push.GetBoolean())
                        ProjectDiagnostics.Changes(snapshots[basis], snapshot, (project, populated) =>
                        {
                            writer.WriteStartObject(); String(writer, "uri", DocumentUris.FromFileName(project.Id));
                            writer.WritePropertyName("diagnostics");
                            LspJson.WriteDiagnostics(writer, populated ? ProjectDiagnostics.Get(project, Text(input, "encoding") == "utf-16"u8 ? PositionEncoding.Utf16 : PositionEncoding.Utf8,
                                new(Locale: snapshot.Locale)) : [], false);
                            writer.WriteEndObject();
                        });
                    writer.WriteEndArray();
                }
                if (step.TryGetProperty("prepareAutoImports", out var importingFile))
                {
                    var name = JsonStrings.GetString(importingFile);
                    writer.WriteStartArray("autoImports");
                    if (snapshot.GetDefaultProject(name) is { Resource: { } resource, Program: { } program } project && program.GetFile(name) is { } file)
                    {
                        using var query = new ProjectRequest();
                        using var checker = await resource.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, query, file.Syntax);
                        var view = new AutoImportView(program, checker.Checker, new(file.Syntax, file.Mapping, PositionEncoding.Utf8),
                            snapshot.UserPreferences, default, cache: project.AutoImports);
                        foreach (var export in await view.SearchAsync(default))
                        {
                            writer.WriteStartObject(); String(writer, "module", export.Id.Module); String(writer, "exportName", export.Id.Name);
                            String(writer, "fileName", export.FileName); writer.WriteNumber("syntax", (int)export.Syntax); writer.WriteNumber("flags", (uint)export.Flags);
                            String(writer, "name", export.Name); String(writer, "targetModule", export.Target.Module); String(writer, "targetName", export.Target.Name);
                            writer.WriteBoolean("typeOnly", export.TypeOnly); String(writer, "path", export.Path); String(writer, "packageName", export.PackageName);
                            writer.WriteNumber("kind", (int)export.Kind); writer.WriteBoolean("deprecated", export.Deprecated); writer.WriteEndObject();
                        }
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
                if (installer is not null && !(step.TryGetProperty("typeAcquisitionDisabled", out var disabled) && disabled.GetBoolean()))
                    foreach (var project in snapshot.Projects)
                    {
                        if (project.Program is null || project.GetTypeAcquisition().Enable != true) continue;
                        var info = project.ComputeTypingsInfo();
                        var files = project.Program.SourceFiles.Select(file => file.Syntax.FileName).ToArray();
                        var old = snapshots[basis]?.GetProject(project.Id);
                        bool newFiles = old?.Program is not { } previousProgram || !previousProgram.SourceFiles.Select(file => file.Syntax.FileName).SequenceEqual(files);
                        if (!newFiles && info.Equals(project.Typings?.Info)) continue;
                        var root = project.Kind == ProjectKind.Configured ? CompilerPath.DirectoryName(project.Configuration.FileName) : cwd;
                        try
                        {
                            var result = await installer.InstallAsync(new(info.Acquisition, project.Configuration.Options, files, root, info.UnresolvedImports, memory));
                            if (!result.Files.SequenceEqual(project.TypingsFiles)) pendingTypings[project.Id] = new(project.Id, info, result) { ProjectIdentity = project.Identity };
                        }
                        catch (IOException) { }
                    }
            }
            if (npm is not null)
            {
                writer.WriteStartObject(); writer.WriteStartArray("npmCalls");
                foreach (var (directory, arguments) in npm.Calls)
                {
                    writer.WriteStartObject(); String(writer, "cwd", directory); writer.WriteStartArray("args");
                    foreach (var argument in arguments) JsonStrings.WriteString(writer, argument);
                    writer.WriteEndArray(); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        finally { foreach (var snapshot in snapshots) if (snapshot is not null) await snapshot.DisposeAsync(); }

        Process StartMapper(ProcessStartInfo start)
        {
            if (input.TryGetProperty("mapperFixture", out var fixture))
            {
                start.ArgumentList.Insert(0, start.FileName); start.FileName = fixture.GetString()!;
                start.WorkingDirectory = Path.GetDirectoryName(start.FileName)!;
                return Process.Start(start) ?? throw new IOException("Pinned mapper fixture did not start");
            }
            if (!input.TryGetProperty("mapperResultsFile", out var results) || start.FileName != "component-mapper")
                throw new InvalidOperationException("Unexpected mapper fixture command: " + start.FileName);
            start.FileName = "node"; start.WorkingDirectory = Path.GetFullPath("."); start.ArgumentList.Clear();
            foreach (var argument in new[] { Path.GetFullPath("csharp/tests/fixtures/mappers/mapper.mjs"), "utf-8", "mapper", results.GetString()! })
                start.ArgumentList.Add(argument);
            return Process.Start(start) ?? throw new IOException("Mapper fixture did not start");
        }
    }

    private static JsonElement? Element(JsonElement value, string name) =>
        value.TryGetProperty(name, out var result) && result.ValueKind != JsonValueKind.Null ? result.Clone() : null;

    internal static void OverlayLines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer)) Overlay(input.RootElement, writer);
            Console.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
        }
    }

    internal static Utf8String Text(JsonElement value, string name, Utf8String fallback = default) =>
        value.TryGetProperty(name, out var text) && text.ValueKind != JsonValueKind.Null ? JsonStrings.GetString(text) : fallback;

    internal static void String(Utf8JsonWriter writer, string name, Utf8String value)
    { writer.WritePropertyName(name); JsonStrings.WriteString(writer, value.Span); }

    internal static FileChange Change(JsonElement input) => new((FileChangeKind)input.GetProperty("kind").GetInt32(), Text(input, "fileName"),
        input.GetProperty("version").GetInt32(), Text(input, "text"), (ScriptKind)input.GetProperty("scriptKind").GetInt32(),
        input.GetProperty("edits").EnumerateArray().Select(edit => new DocumentEdit(Text(edit, "text"),
            edit.TryGetProperty("range", out var range) ? new(Position(range.GetProperty("start")), Position(range.GetProperty("end"))) : null)).ToArray());

    private static DocumentPosition Position(JsonElement input) => new(input.GetProperty("line").GetUInt32(), input.GetProperty("character").GetUInt32());

    private static void Overlay(JsonElement input, Utf8JsonWriter writer)
    {
        var files = input.GetProperty("files").EnumerateObject().Where(property => property.Value.ValueKind != JsonValueKind.Null)
            .ToDictionary(JsonStrings.GetName, property => JsonStrings.GetString(property.Value).Span.ToArray());
        var documents = input.GetProperty("overlays").EnumerateArray().Select(value => new DocumentSnapshot(Text(value, "fileName"), Text(value, "text"),
            value.GetProperty("version").GetInt32(), (ScriptKind)value.GetProperty("kind").GetInt32(), true, value.GetProperty("matchesDisk").GetBoolean()));
        var fs = new OverlayFileSystem(new MemoryFileSystem(files, input.GetProperty("caseSensitive").GetBoolean()), "/"u8, documents);
        FileChangeSummary? summary = null;
        bool panicked = false;
        try
        {
            (fs, summary) = fs.Apply(input.GetProperty("changes").EnumerateArray().Select(Change).ToArray(),
                Text(input, "encoding") == "utf-16"u8 ? PositionEncoding.Utf16 : PositionEncoding.Utf8);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { panicked = true; }
        writer.WriteStartObject(); writer.WriteBoolean("panicked", panicked);
        if (!panicked)
        {
            writer.WriteStartObject("summary");
            String(writer, "opened", summary!.Opened); String(writer, "reopened", summary.Reopened);
            Paths("closed", summary.Closed); Paths("changed", summary.Changed); Paths("created", summary.Created); Paths("deleted", summary.Deleted);
            writer.WriteBoolean("outside", summary.IncludesWatchChangeOutsideNodeModules); writer.WriteEndObject();
            writer.WriteStartArray("overlays");
            foreach (var document in fs.Overlays.Values.OrderBy(document => document.FileName, Utf8StringComparer.Ordinal))
            {
                writer.WriteStartObject(); String(writer, "fileName", document.FileName); String(writer, "text", document.Text);
                writer.WriteNumber("version", document.Version); writer.WriteNumber("kind", (int)document.Kind);
                writer.WriteBoolean("matchesDisk", document.MatchesDiskText); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteStartObject("documents");
            foreach (var property in input.GetProperty("files").EnumerateObject())
            {
                var name = JsonStrings.GetName(property); JsonStrings.WriteName(writer, name);
                if (fs.GetDocument(name) is not { } document) { writer.WriteNullValue(); continue; }
                writer.WriteStartObject(); String(writer, "text", document.Text); writer.WriteNumber("version", document.Version);
                writer.WriteNumber("kind", (int)document.Kind); writer.WriteBoolean("matchesDisk", document.MatchesDiskText);
                writer.WriteBoolean("isOverlay", document.IsOverlay); writer.WriteString("hash", document.Hash.ToString("x32", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();

        void Paths(string name, IEnumerable<Utf8String> paths)
        {
            writer.WriteStartArray(name);
            foreach (var path in paths.Order(Utf8StringComparer.Ordinal)) JsonStrings.WriteString(writer, path.Span);
            writer.WriteEndArray();
        }
    }
}

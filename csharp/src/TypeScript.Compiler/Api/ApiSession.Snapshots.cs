using TypeScript.Compiler.Protocol;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Programs;

namespace TypeScript.Compiler.Api;

public sealed partial class ApiSession
{
    private Utf8String Document(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var uri = ApiJson.String(value, "uri"u8);
            return uri.IsEmpty ? host.FileName(default) : DocumentUris.ToFileName(uri);
        }
        return host.FileName(ApiJson.String(value));
    }
    private Utf8String[] Documents(JsonElement value, ReadOnlySpan<byte> property) => ApiJson.Array(value, property).Select(Document).ToArray();
    private SnapshotRequest ReadSnapshotRequest(JsonElement value)
    {
        var ensure = ApiJson.Get(value, "ensurePrograms"u8);
        if (ensure.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.True or JsonValueKind.Array))
            throw new ApiException("ensurePrograms must be true or an array of project IDs", true);
        var openProjects = Documents(value, "openProjects"u8);
        var openFiles = Documents(value, "openFiles"u8);
        return new()
        {
            OpenProjects = openProjects.Distinct(host.Comparer).ToArray(), CloseProjects = Documents(value, "closeProjects"u8).Distinct(host.Comparer).ToArray(),
            OpenFiles = openFiles.Distinct(host.Comparer).ToArray(), CloseFiles = Documents(value, "closeFiles"u8).Distinct(host.Comparer).ToArray(),
            EnsureFiles = openFiles,
            CreatePrograms = ApiJson.Array(value, "createPrograms"u8).Select((item, index) =>
                item.ValueKind == JsonValueKind.Null ? throw new ApiException($"createPrograms[{index}] must not be null") : ReadProgram(item)).ToArray(),
            ReconfigurePrograms = ApiJson.Array(value, "reconfigurePrograms"u8).Select((item, index) => item.ValueKind == JsonValueKind.Null
                ? throw new ApiException($"reconfigurePrograms[{index}] must not be null") : new ReconfigureProgramRequest(ProjectIds.Canonicalize(ApiJson.String(item, "id"u8)), ReadProgram(item))).ToArray(),
            RemovePrograms = ApiJson.Strings(value, "removePrograms"u8).Select(ProjectIds.Canonicalize).ToArray(), EnsureAllPrograms = ensure.ValueKind == JsonValueKind.True,
            EnsurePrograms = openProjects.Concat(ensure.ValueKind == JsonValueKind.Array ? ApiJson.Array(ensure).Select(ApiJson.String) : []).Distinct(host.Comparer).ToArray()
        };
    }
    private CreateProgramRequest ReadProgram(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ApiException("program must not be null");
        var options = ApiJson.Get(value, "options"u8);
        return new(Documents(value, "rootFiles"u8), ApiJson.CompilerOptions(ApiJson.Get(value, "compilerOptions"u8)))
        {
            References = ApiJson.Array(options, "projectReferences"u8).Select(reference => new ProjectReference(
                ApiJson.String(reference, "path"u8), ApiJson.Boolean(reference, "prepend"u8), ApiJson.Boolean(reference, "circular"u8))
                { OriginalPath = ApiJson.String(reference, "originalPath"u8) }).ToArray(),
            ConfigDiagnostics = ApiJson.Array(options, "configFileParsingDiagnostics"u8).Select(ReadDiagnostic).ToArray()
        };
    }
    private static Diagnostic ReadDiagnostic(JsonElement value)
    {
        var message = new DiagnosticMessage((DiagnosticCode)ApiJson.Int32(value, "code"u8),
            (DiagnosticCategory)ApiJson.Int32(value, "category"u8), default, ApiJson.String(value, "text"u8),
            ApiJson.Boolean(value, "reportsUnnecessary"u8), ApiJson.Boolean(value, "reportsDeprecated"u8));
        int start = ApiJson.Int32(value, "pos"u8), end = ApiJson.Int32(value, "end"u8);
        // The source API reconstructs supplied configuration diagnostics without a file association.
        return new(message, start, end - start, [])
        { MessageChain = ApiJson.Array(value, "messageChain"u8).Select(ReadDiagnostic).ToArray(),
            RelatedInformation = ApiJson.Array(value, "relatedInformation"u8).Select(ReadDiagnostic).ToArray() };
    }
    private (SnapshotRequest Request, ApiOpenState Opens) Reconcile(SnapshotRequest request, ApiOpenState? previous)
    {
        var opens = previous?.Clone() ?? new(host.Comparer);
        var closeProjects = request.CloseProjects.Where(path => opens.Projects.Remove(host.Path(path))).ToArray();
        var openProjects = request.OpenProjects.Where(path => opens.Projects.Add(host.Path(path))).ToArray();
        var closeFiles = request.CloseFiles.Where(path => opens.Files.Remove(host.Path(path))).ToArray();
        var openFiles = request.OpenFiles.Where(path => opens.Files.Add(host.Path(path))).ToArray();
        return (request with { OpenProjects = openProjects, CloseProjects = closeProjects, OpenFiles = openFiles, CloseFiles = closeFiles }, opens);
    }
    private FileChangeSummary ReadNotifications(JsonElement value)
    {
        var result = new FileChangeSummary(CaseSensitive) { InvalidateAll = ApiJson.Boolean(value, "invalidateAll"u8) };
        if (!result.InvalidateAll)
        {
            result.Changed.UnionWith(Documents(value, "changed"u8).Select(host.Path));
            result.Created.UnionWith(Documents(value, "created"u8).Select(host.Path));
            result.Deleted.UnionWith(Documents(value, "deleted"u8).Select(host.Path));
        }
        result.IncludesWatchChangeOutsideNodeModules = !result.IsEmpty;
        return result;
    }
    private static RequestFileSystemOptions ReadFileSystem(JsonElement value)
    {
        var kind = ApiJson.String(value, "kind"u8);
        var result = new RequestFileSystemOptions(kind == "full"u8 ? RequestFileSystemKind.Full : kind == "layer"u8
            ? RequestFileSystemKind.Layer : throw new ApiException($"unknown request filesystem kind \"{kind}\""));
        var files = new Dictionary<Utf8String, Utf8String>(); var directories = new Dictionary<Utf8String, DirectoryEntries>();
        var symlinks = new Dictionary<Utf8String, RequestSymlink>();
        if (ApiJson.Get(value, "files"u8) is { ValueKind: JsonValueKind.Object } contents)
            foreach (var file in contents.EnumerateObject()) files.Add(JsonStrings.GetName(file), ApiJson.String(file.Value));
        if (ApiJson.Get(value, "directories"u8) is { ValueKind: JsonValueKind.Object } listings)
            foreach (var directory in listings.EnumerateObject()) directories.Add(JsonStrings.GetName(directory),
                new(ApiJson.Strings(directory.Value, "files"u8), ApiJson.Strings(directory.Value, "directories"u8)));
        if (ApiJson.Get(value, "symlinks"u8) is { ValueKind: JsonValueKind.Object } links)
            foreach (var link in links.EnumerateObject()) symlinks.Add(JsonStrings.GetName(link),
                new(ApiJson.String(link.Value, "target"u8), ApiJson.Boolean(link.Value, "host"u8)));
        return result with { Files = files, Directories = directories, Symlinks = symlinks, RemovedPaths = ApiJson.Strings(value, "removedPaths"u8) };
    }
    private async ValueTask<RpcResponse> CreateSnapshotAsync(JsonElement parameters, ApiSnapshotData? parent, CancellationToken cancellation)
    {
        var (request, opens) = Reconcile(ReadSnapshotRequest(parameters), parent?.Opens);
        var changes = ReadNotifications(ApiJson.Get(parameters, "fileNotifications"u8));
        var source = parent?.FileSystem;
        bool replace = false;
        if (ApiJson.Get(parameters, "fileSystem"u8) is { ValueKind: JsonValueKind.Object } fileRequest)
        {
            var fsOptions = ReadFileSystem(fileRequest);
            try { source = RequestFileSystem.Create(fsOptions, source ?? fileSystem, CurrentDirectory, changes); }
            catch (ArgumentException error) { throw new ApiException(error.Message, inner: error); }
            replace = fsOptions.Kind == RequestFileSystemKind.Full;
        }
        request = request with { FileSystemChanges = changes, FileSystem = source, ReplaceFileSystem = replace };
        ProjectWorkspaceSnapshot snapshot;
        try { snapshot = await host.CreateAsync(request, parent?.Snapshot, cancellation).ConfigureAwait(false); }
        catch (ArgumentException error) { throw new ApiException($"failed to {(parent is null ? "create" : "update")} snapshot: {error.Message}", inner: error); }
        RpcResponse response;
        try { response = SnapshotResponse(snapshot, parent?.Snapshot, parameters); }
        catch { await snapshot.DisposeAsync().ConfigureAwait(false); throw; }
        await RegisterAsync(new(snapshot, snapshot, opens, source)).ConfigureAwait(false);
        return response;
    }
    private RpcResponse SnapshotResponse(ProjectWorkspaceSnapshot snapshot, ProjectWorkspaceSnapshot? previous, JsonElement request) => RpcResponse.Json(writer =>
    {
        writer.WriteStartObject(); writer.WriteNumber("snapshot"u8, snapshot.Id); writer.WritePropertyName("projects"u8); writer.WriteStartArray();
        foreach (var project in snapshot.Projects)
            if (project.ConfigLoaded && project.ApiIdentity != previous?.GetProject(project.Id)?.ApiIdentity) WriteProject(writer, project);
        writer.WriteEndArray();
        if (previous is not null) WriteSnapshotChanges(writer, previous, snapshot);
        writer.WritePropertyName("operation"u8); writer.WriteStartObject();
        if (ApiJson.Get(request, "createPrograms"u8).ValueKind == JsonValueKind.Array)
            ApiJson.Strings(writer, "createdPrograms"u8, snapshot.CreatedPrograms.Select(project => project.Id));
        if (ApiJson.Get(request, "openFiles"u8).ValueKind == JsonValueKind.Array)
        {
            writer.WritePropertyName("openedFiles"u8); writer.WriteStartArray();
            foreach (var file in Documents(request, "openFiles"u8))
            {
                var project = snapshot.GetDefaultProject(file) ?? throw new InvalidOperationException($"No project found for opened file {file}");
                writer.WriteStartObject(); ApiJson.String(writer, "project"u8, project.Id); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject(); writer.WriteEndObject();
    });
    private void WriteSnapshotChanges(Utf8JsonWriter writer, ProjectWorkspaceSnapshot previous, ProjectWorkspaceSnapshot next)
    {
        var removed = previous.Projects.Where(project => next.GetProject(project.Id) is null).Select(project => project.Id).ToArray();
        var changed = new List<(Utf8String Id, Utf8String[] Changed, Utf8String[] Deleted)>();
        foreach (var project in next.Projects)
        {
            var old = previous.GetProject(project.Id); if (old is null || ReferenceEquals(old.Program, project.Program)) continue;
            var modified = new List<Utf8String>(); var deleted = new List<Utf8String>();
            foreach (var file in old.Program?.SourceFiles ?? [])
            {
                var path = host.Path(file.Syntax.FileName); var current = project.Program?.GetFileByPath(path);
                if (current is null) deleted.Add(path);
                else if (!ReferenceEquals(current.Syntax, file.Syntax)) modified.Add(path);
            }
            if (modified.Count + deleted.Count != 0) changed.Add((project.Id, modified.ToArray(), deleted.ToArray()));
        }
        if (removed.Length == 0 && changed.Count == 0) return;
        writer.WritePropertyName("changes"u8); writer.WriteStartObject();
        if (changed.Count != 0)
        {
            writer.WritePropertyName("changedProjects"u8); writer.WriteStartObject();
            foreach (var project in changed)
            {
                JsonStrings.WriteName(writer, project.Id); writer.WriteStartObject();
                if (project.Changed.Length != 0) ApiJson.Strings(writer, "changedFiles"u8, project.Changed);
                if (project.Deleted.Length != 0) ApiJson.Strings(writer, "deletedFiles"u8, project.Deleted);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        if (removed.Length != 0) ApiJson.Strings(writer, "removedProjects"u8, removed);
        writer.WriteEndObject();
    }
    private void WriteProject(Utf8JsonWriter writer, ProjectSnapshot project)
    {
        writer.WriteStartObject(); ApiJson.String(writer, "id"u8, project.Id);
        ApiJson.String(writer, "configFileName"u8, project.Kind == ProjectKind.Configured ? project.Configuration.FileName : default);
        ApiJson.String(writer, "currentDirectory"u8, project.Kind == ProjectKind.Configured ? CompilerPath.DirectoryName(project.Configuration.FileName) : CurrentDirectory);
        writer.WriteBoolean("dirty"u8, project.IsDirty); writer.WritePropertyName("parsedCommandLine"u8); WriteConfiguration(writer, project.Configuration);
        ApiJson.Strings(writer, "rootFiles"u8, project.Configuration.FileNames);
        writer.WritePropertyName("compilerOptions"u8); WriteOptions(writer, project.Configuration.ApiOptions ?? project.Configuration.Options, project.Configuration.FileName); writer.WriteEndObject();
    }
    private static void WriteOptions(Utf8JsonWriter writer, CompilerOptions options, Utf8String configFileName = default, bool includeNull = false)
    {
        writer.WriteStartObject();
        foreach (var (name, value) in options.Values)
        {
            if (!includeNull && value.ValueKind == JsonValueKind.Null) continue;
            if (name == "configFilePath"u8 && !configFileName.IsEmpty) continue;
            JsonStrings.WriteName(writer, name);
            if (name == "lib"u8 && value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    var library = ApiJson.String(item);
                    JsonStrings.WriteString(writer, library.StartsWith("lib."u8, StringComparison.Ordinal) ? library : "lib."u8 + library + ".d.ts"u8);
                }
                writer.WriteEndArray();
            }
            else if (value.ValueKind == JsonValueKind.String && OptionDefinitions.Find(name, OptionGroup.Compiler) is { Kind: OptionKind.Enum } definition
                && definition.ValueIdentity(JsonStrings.GetString(value)) is { } identity)
                writer.WriteRawValue(OptionDefinitions.EnumValueJson(identity).Span);
            else ApiJson.Write(writer, value);
        }
        if (!configFileName.IsEmpty) ApiJson.String(writer, "configFilePath"u8, configFileName);
        writer.WriteEndObject();
    }
    private static void WriteConfiguration(Utf8JsonWriter writer, ParsedConfig config, bool commandLine = false, bool jsonContent = false)
    {
        writer.WriteStartObject(); ApiJson.Strings(writer, "fileNames"u8, config.FileNames);
        writer.WritePropertyName("options"u8); WriteOptions(writer, config.ApiOptions ?? config.Options, config.FileName);
        if (config.References.Length != 0)
        {
            writer.WritePropertyName("projectReferences"u8); writer.WriteStartArray();
            foreach (var reference in config.References)
            {
                writer.WriteStartObject(); ApiJson.String(writer, "path"u8, reference.Path);
                ApiJson.String(writer, "originalPath"u8, reference.OriginalPath);
                writer.WriteBoolean("circular"u8, reference.Circular);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        if (config.TypeAcquisition.Values.Count != 0) { writer.WritePropertyName("typeAcquisition"u8); WriteOptions(writer, config.TypeAcquisition); }
        if (!commandLine && (jsonContent || config.SourceFile is not null || config.Raw.ValueKind != JsonValueKind.Undefined))
            writer.WriteBoolean("compileOnSave"u8, config.CompileOnSave);
        if (config.Raw.ValueKind != JsonValueKind.Undefined) { writer.WritePropertyName("raw"u8); ApiJson.Write(writer, config.Raw); }
        writer.WritePropertyName("errors"u8); writer.WriteStartArray();
        foreach (var diagnostic in config.Diagnostics) WriteDiagnostic(writer, diagnostic,
            name => config.SourceFile?.FileName == name ? config.SourceFile.Source : null);
        writer.WriteEndArray(); writer.WriteEndObject();
    }
    private static void WriteDiagnostic(Utf8JsonWriter writer, Diagnostic diagnostic, Func<Utf8String, SourceText?>? getSource = null,
        CompilerProgram? program = null)
    {
        writer.WriteStartObject();
        var fileName = diagnostic.FileName;
        var source = fileName is { } name ? getSource?.Invoke(name) : null;
        var mapping = !diagnostic.IsMapperFailure && fileName is { } mappedName ? program?.GetFile(mappedName)?.Mapping : null;
        var presentation = mapping?.Present(diagnostic);
        source = presentation?.Text ?? source;
        int start = presentation?.Start ?? diagnostic.Start, end = start + (presentation?.Length ?? diagnostic.Length);
        if (program is not null && mapping is not null)
            fileName = program.SourceFiles.FirstOrDefault(file => file.SupplementalSourceFiles.Contains(mapping.Syntax.FileName))?.Syntax.FileName ?? fileName;
        if (source is not null)
        {
            start = Math.Clamp(start, 0, source.Length); end = Math.Clamp(end, start, source.Length);
            ApiJson.String(writer, "fileName"u8, fileName!.Value);
        }
        writer.WriteNumber("pos"u8, start); writer.WriteNumber("end"u8, end);
        if (source is not null)
        {
            var first = source.GetLineAndCharacter(start); var last = source.GetLineAndCharacter(end);
            writer.WritePropertyName("startPosition"u8); Position(first); writer.WritePropertyName("endPosition"u8); Position(last);
            writer.WritePropertyName("sourceLines"u8); writer.WriteStartArray();
            var lines = last.Line - first.Line >= 4 ? new[] { first.Line, first.Line + 1, last.Line - 1, last.Line }
                : Enumerable.Range(first.Line, last.Line - first.Line + 1).ToArray();
            foreach (int line in lines)
            {
                int lineStart = source.LineStarts[line], lineEnd = line + 1 < source.LineStarts.Length ? source.LineStarts[line + 1] : source.Length;
                writer.WriteStartObject(); writer.WriteNumber("line"u8, line); ApiJson.String(writer, "text"u8, source.Text[lineStart..lineEnd]); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteNumber("code"u8, (int)diagnostic.Code); writer.WriteNumber("category"u8, (int)diagnostic.Message.Category);
        if (diagnostic.Source is { IsEmpty: false } prefix) ApiJson.String(writer, "source"u8, prefix);
        ApiJson.String(writer, "text"u8, presentation is { } mapped && diagnostic.Source is null
            ? mapped.Message : diagnostic.Message.Format(arguments: diagnostic.Arguments));
        if (diagnostic.Message.ReportsUnnecessary) writer.WriteBoolean("reportsUnnecessary"u8, true);
        if (diagnostic.Message.ReportsDeprecated) writer.WriteBoolean("reportsDeprecated"u8, true);
        if (diagnostic.MessageChain.Count != 0 || presentation is { Synthesized: true })
        {
            writer.WritePropertyName("messageChain"u8); writer.WriteStartArray();
            foreach (var child in diagnostic.MessageChain) WriteDiagnostic(writer, child, getSource, program);
            if (mapping is not null && presentation is { Synthesized: true })
                WriteDiagnostic(writer, new(Messages.This_location_is_in_virtual_code_produced_by_the_content_mapper_0_and_has_no_corresponding_location_in_the_original_file,
                    -1, 0, [mapping.MapperIdentity]));
            writer.WriteEndArray();
        }
        if (diagnostic.RelatedInformation.Count != 0)
        {
            writer.WritePropertyName("relatedInformation"u8); writer.WriteStartArray();
            foreach (var child in diagnostic.RelatedInformation) WriteDiagnostic(writer, child, getSource, program); writer.WriteEndArray();
        }
        writer.WriteEndObject();
        void Position((int Line, int Character) position)
        { writer.WriteStartObject(); writer.WriteNumber("line"u8, position.Line); writer.WriteNumber("character"u8, position.Character); writer.WriteEndObject(); }
    }
}

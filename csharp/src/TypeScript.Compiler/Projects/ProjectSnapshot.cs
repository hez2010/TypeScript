using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Projects.TypeAcquisition;
using System.Globalization;

namespace TypeScript.Compiler.Projects;

public enum ProjectKind { Inferred, Configured, Synthetic }
public enum ProjectProgramUpdateKind { None, SameFileNames, NewFiles }

internal static class ProjectIds
{
    internal static Utf8String Canonicalize(Utf8String id)
    {
        ReadOnlySpan<byte> prefix = "/dev/null/synthetic/"u8;
        return id.StartsWith(prefix) && long.TryParse(id.Span[prefix.Length..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number) && number > 0
            ? "/dev/null/synthetic/"u8 + Utf8String.Format(number) : id;
    }
}

public sealed record ProjectHostOptions
{
    internal bool TrackFileWatches { get; init; }
    public Func<Diagnostics.DiagnosticMessage, Utf8String, bool, ValueTask>? Progress { get; init; }
    public Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process>? StartMapperProcess { get; init; }
    public Action<Utf8String>? MapperLog { get; init; }
    public Utf8String CurrentDirectory { get; init; } = "/"u8;
    public Utf8String DefaultLibraryDirectory { get; init; }
    public Utf8String TypingsLocation { get; init; }
    public PositionEncoding PositionEncoding { get; init; } = PositionEncoding.Utf8;
    public bool RunExternalCode { get; init; }
    public int Concurrency { get; init; } = 4;
    public int MaxCheckers { get; init; } = 4;
    public TimeSpan CheckerIdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public sealed record CreateProgramRequest(Utf8String[] RootFiles, CompilerOptions CompilerOptions)
{
    public ProjectReference[] References { get; init; } = [];
    public Diagnostics.Diagnostic[] ConfigDiagnostics { get; init; } = [];
}

public sealed record ReconfigureProgramRequest(Utf8String Id, CreateProgramRequest Program);

public sealed record SnapshotRequest
{
    public UserPreferences? UserPreferences { get; init; }
    public IReadOnlyList<Utf8String> OpenProjects { get; init; } = [];
    public IReadOnlyList<Utf8String> CloseProjects { get; init; } = [];
    public IReadOnlyList<Utf8String> OpenFiles { get; init; } = [];
    public IReadOnlyList<Utf8String> CloseFiles { get; init; } = [];
    public IReadOnlyList<CreateProgramRequest> CreatePrograms { get; init; } = [];
    public IReadOnlyList<ReconfigureProgramRequest> ReconfigurePrograms { get; init; } = [];
    public IReadOnlyList<Utf8String> RemovePrograms { get; init; } = [];
    public IReadOnlyList<Utf8String> EnsurePrograms { get; init; } = [];
    public IReadOnlyList<Utf8String> EnsureFiles { get; init; } = [];
    public IReadOnlyList<Utf8String> EnsureConfiguredFiles { get; init; } = [];
    public bool EnsureAllPrograms { get; init; }
    public bool LoadProjectTrees { get; init; }
    public IReadOnlyList<Utf8String>? RequestedProjectTrees { get; init; }
    public IReadOnlyList<FileChange> FileChanges { get; init; } = [];
    public FileChangeSummary? FileSystemChanges { get; init; }
    public IFileSystem? FileSystem { get; init; }
    public bool ReplaceFileSystem { get; init; }
    public bool InvalidateAll { get; init; }
    public CompilerOptions? InferredOptions { get; init; }
    public Utf8String? Locale { get; init; }
    public Utf8String? CustomConfigFileName { get; init; }
    public ContentMapper[]? InferredContentMappers { get; init; }
    public IReadOnlyList<TypingsStateChange> TypingsChanges { get; init; } = [];
}

/// <summary>One project's state in a snapshot. Dirty projects retain their last ensured program.</summary>
public sealed class ProjectSnapshot
{
    internal Utf8String DisplayName(Utf8String directory) => Kind == ProjectKind.Inferred ? CompilerPath.BaseName(directory)
        : CompilerPath.Relative(directory, Kind == ProjectKind.Configured ? Configuration.FileName : Id, true);
    public Utf8String Id { get; }
    public ProjectKind Kind { get; }
    public ParsedConfig Configuration { get; }
    public CompilerProgram? Program => Resource?.Program;
    public ulong ProgramLastUpdate { get; }
    public ProjectProgramUpdateKind ProgramUpdateKind { get; internal init; }
    internal bool ProgramDiagnosticsChanged { get; init; } = true;
    public bool IsDirty { get; }
    internal Utf8String PendingChangedFile { get; private init; }
    internal ProjectProgram? Resource { get; }
    internal AutoImportCache? AutoImports { get; private init; }
    internal bool ConfigLoaded { get; }
    internal IReadOnlyList<Utf8String> PotentialProjectReferences { get; init; } = [];
    internal object Identity { get; init; } = new();
    internal object ApiIdentity { get; init; } = new();
    internal TypingsStateChange? Typings { get; init; }
    public IReadOnlyList<Utf8String> TypingsFiles => Typings?.Result.Files ?? [];
    public IReadOnlyList<Utf8String> TypingsFilesToWatch => Typings?.Result.FilesToWatch ?? [];

    internal ProjectSnapshot(Utf8String id, ProjectKind kind, ParsedConfig configuration,
        ProjectProgram? resource = null, ulong programLastUpdate = 0, bool dirty = true, bool configLoaded = true)
    {
        Id = id; Kind = kind; Configuration = configuration; Resource = resource;
        ProgramLastUpdate = programLastUpdate; IsDirty = dirty;
        ConfigLoaded = configLoaded;
    }

    internal ProjectSnapshot Dirty(ParsedConfig? config = null, Utf8String changedFile = default) =>
        new(Id, Kind, config ?? Configuration, Resource, ProgramLastUpdate, configLoaded: config is not null || ConfigLoaded)
        { Identity = Identity, Typings = Typings, PotentialProjectReferences = PotentialProjectReferences, PendingChangedFile = changedFile };
    internal ProjectSnapshot WithAutoImports(AutoImportCache cache) => new(Id, Kind, Configuration, Resource, ProgramLastUpdate, IsDirty, ConfigLoaded)
    {
        Identity = Identity, ApiIdentity = ApiIdentity, Typings = Typings, PotentialProjectReferences = PotentialProjectReferences,
        ProgramUpdateKind = ProgramUpdateKind, ProgramDiagnosticsChanged = ProgramDiagnosticsChanged, AutoImports = cache, PendingChangedFile = PendingChangedFile
    };
    internal ProjectSnapshot WithTypings(TypingsStateChange typings) =>
        new(Id, Kind, Configuration, Resource, ProgramLastUpdate, configLoaded: ConfigLoaded)
        { Identity = Identity, Typings = typings, PotentialProjectReferences = PotentialProjectReferences };
    internal ProjectSnapshot WithPotentialReference(Utf8String id) => new(Id, Kind, Configuration, Resource, ProgramLastUpdate, IsDirty, ConfigLoaded)
    {
        Identity = Identity, Typings = Typings, ProgramUpdateKind = ProgramUpdateKind, ProgramDiagnosticsChanged = ProgramDiagnosticsChanged,
        PendingChangedFile = PendingChangedFile,
        PotentialProjectReferences = PotentialProjectReferences.Contains(id) ? PotentialProjectReferences : [.. PotentialProjectReferences, id],
    };
    public TypeAcquisitionOptions GetTypeAcquisition() => GetTypeAcquisition(Kind, Configuration);
    internal static TypeAcquisitionOptions GetTypeAcquisition(ProjectKind kind, ParsedConfig config)
    {
        if (kind != ProjectKind.Configured) return new(true, DisableFilenameBasedTypeAcquisition: false);
        var options = config.TypeAcquisition;
        return new(options.Boolean("enable"u8) ?? CompilerPath.BaseName(config.FileName) == "jsconfig.json"u8,
            options.Strings("include"u8), options.Strings("exclude"u8), options.Boolean("disableFilenameBasedTypeAcquisition"u8) ?? false);
    }
    public TypingsInfo ComputeTypingsInfo() => TypingsInfo.Capture(this);
    public bool ContainsFile(Utf8String path) => Program?.GetFileByPath(path) is not null;
    internal bool IsReferenceSource(Utf8String path) => Program is { ProjectReferences.UseSources: true } program
        && program.ProjectReferences.Sources.ContainsKey(path);
}

internal sealed class ProjectProgram(CompilerProgram program, ContentMapperProject? mapper, ProjectHostOptions options)
{
    internal Utf8String[] ObservedFiles { get; init; } = [];
    internal Utf8String[] MapperWatchedFiles { get; init; } = [];
    private int references = 1;
    internal CompilerProgram Program { get; } = program;
    internal ContentMapperProject? Mapper { get; } = mapper;
    internal ProjectCheckerPool Checkers { get; } = new(program, options.MaxCheckers, options.CheckerIdleTimeout, options.TimeProvider);
    internal void Retain()
    {
        if (Interlocked.Increment(ref references) <= 1) throw new ObjectDisposedException(nameof(ProjectProgram));
    }
    internal async ValueTask ReleaseAsync()
    {
        if (Interlocked.Decrement(ref references) != 0) return;
        Checkers.Dispose();
        if (Mapper is not null) await Mapper.DisposeAsync().ConfigureAwait(false);
    }

    internal static async ValueTask ReleaseAllAsync(IEnumerable<ProjectSnapshot> projects)
    {
        List<Exception>? failures = null;
        foreach (var project in projects)
        {
            try { if (project.Resource is { } resource) await resource.ReleaseAsync().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null) throw new AggregateException(failures);
    }
}

/// <summary>Owns a stable project graph. A lease keeps shared programs and mapper processes alive.</summary>
public sealed class ProjectWorkspaceSnapshot : IAsyncDisposable
{
    private int references = 1, ownerReleased;
    internal ProjectSnapshotHost Host { get; }
    internal Dictionary<Utf8String, ProjectSnapshot> ProjectMap { get; }
    internal Dictionary<Utf8String, int> OpenProjectCounts { get; }
    internal Dictionary<Utf8String, (Utf8String FileName, int Count)> OpenFileCounts { get; }
    internal CompilerOptions InferredOptions { get; }
    internal ContentMapper[] InferredContentMappers { get; }
    internal bool FileSystemOverridden { get; }
    internal Dictionary<Utf8String, Utf8String?> NearestConfigs { get; }
    internal IReadOnlyDictionary<Utf8String, ParsedConfig> Configurations { get; }
    public ulong Id { get; }
    public ulong ParentId { get; }
    internal Utf8String[] WatchedNodeModules { get; init; } = [];
    public SnapshotFileSystem FileSystem { get; }
    public IReadOnlyList<ProjectSnapshot> Projects { get; }
    public IReadOnlyList<ProjectSnapshot> CreatedPrograms { get; }
    public FileChangeSummary Changes { get; }
    public Utf8String Locale { get; }
    public Utf8String CustomConfigFileName { get; }
    public UserPreferences UserPreferences { get; }

    internal ProjectWorkspaceSnapshot(ProjectSnapshotHost host, ulong id, ulong parentId, SnapshotFileSystem fs,
        Dictionary<Utf8String, ProjectSnapshot> projects, Dictionary<Utf8String, int> openProjects,
        Dictionary<Utf8String, (Utf8String FileName, int Count)> openFiles, CompilerOptions inferredOptions,
        ContentMapper[] inferredMappers, ProjectSnapshot[] created, FileChangeSummary changes, bool fileSystemOverridden,
        Utf8String locale, Utf8String customConfigFileName, Dictionary<Utf8String, Utf8String?> nearestConfigs, UserPreferences userPreferences, Dictionary<Utf8String, ParsedConfig> configurations)
    {
        Host = host; Id = id; ParentId = parentId; FileSystem = fs; ProjectMap = projects;
        OpenProjectCounts = openProjects; OpenFileCounts = openFiles; InferredOptions = inferredOptions;
        InferredContentMappers = inferredMappers; CreatedPrograms = Array.AsReadOnly(created); Changes = changes;
        FileSystemOverridden = fileSystemOverridden;
        Locale = locale;
        CustomConfigFileName = customConfigFileName;
        NearestConfigs = nearestConfigs; Configurations = configurations;
        UserPreferences = userPreferences;
        Projects = Array.AsReadOnly(projects.Values.OrderBy(project => project.Kind switch
            { ProjectKind.Configured => 0, ProjectKind.Synthetic => 1, _ => 2 })
            .ThenBy(project => project.Id, Utf8StringComparer.Ordinal).ToArray());
        host.Retain();
    }

    public ProjectSnapshot? GetProject(Utf8String id) => ProjectMap.GetValueOrDefault(Host.Path(ProjectIds.Canonicalize(id)));

    public ProjectSnapshot? GetDefaultProject(Utf8String fileName)
    {
        Utf8String path = Host.Path(fileName);
        var configured = Projects.Where(project => project.Kind == ProjectKind.Configured && project.ContainsFile(path)).ToArray();
        if (configured.Length == 0) return Projects.FirstOrDefault(project => project.Kind == ProjectKind.Inferred && project.ContainsFile(path));
        if (configured.Length == 1) return configured[0];
        var direct = configured.Where(project => !project.IsReferenceSource(path)).ToArray();
        if (direct.Length <= 1) return direct.FirstOrDefault() ?? configured[0];
        if (!FileSystem.Overlay.Overlays.ContainsKey(path) && !OpenFileCounts.ContainsKey(path)) return configured[0];
        Utf8String? config = NearestConfigs.TryGetValue(path, out var known) ? known
            : Host.FindConfig(FileSystem, CompilerPath.DirectoryName(fileName), CustomConfigFileName);
        var visited = new HashSet<Utf8String>(ProjectMap.Comparer);
        while (config is { } configName)
        {
            var pending = new Queue<Utf8String>(); pending.Enqueue(Host.Path(configName));
            while (pending.TryDequeue(out var id))
            {
                if (!visited.Add(id) || !ProjectMap.TryGetValue(id, out var project)) continue;
                if (direct.Contains(project)) return project;
                foreach (var reference in project.Configuration.References) pending.Enqueue(Host.ReferencePath(reference.Path));
            }
            if (ProjectMap.GetValueOrDefault(Host.Path(configName))?.Configuration.Options.DisableSolutionSearching == true) break;
            config = Host.FindAncestorConfig(FileSystem, configName, CustomConfigFileName);
        }
        return configured[0];
    }

    public Lease Acquire()
    {
        int count = Volatile.Read(ref references);
        while (count > 0)
        {
            int actual = Interlocked.CompareExchange(ref references, count + 1, count);
            if (actual == count) return new(this);
            count = actual;
        }
        throw new ObjectDisposedException(nameof(ProjectWorkspaceSnapshot));
    }

    internal async ValueTask ReleaseAsync()
    {
        if (Interlocked.Decrement(ref references) != 0) return;
        try
        {
            foreach (var project in Projects) project.AutoImports?.Dispose();
            await ProjectProgram.ReleaseAllAsync(Projects).ConfigureAwait(false);
        }
        finally { await Host.ReleaseAsync().ConfigureAwait(false); }
    }

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref ownerReleased, 1) == 0 ? ReleaseAsync() : default;

    public sealed class Lease : IAsyncDisposable
    {
        private ProjectWorkspaceSnapshot? snapshot;
        internal Lease(ProjectWorkspaceSnapshot snapshot) => this.snapshot = snapshot;
        public ProjectWorkspaceSnapshot Snapshot => snapshot ?? throw new ObjectDisposedException(nameof(Lease));
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref snapshot, null)?.ReleaseAsync() ?? default;
    }
}

using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Incremental;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Watching;
using System.Diagnostics;

namespace TypeScript.Compiler.Execution;

/// <summary>Builds a project-reference graph in dependency order, with bounded parallel work and ordered results.</summary>
public sealed partial class ProjectBuilder : IAsyncDisposable
{
    private readonly IFileSystem fileSystem;
    private readonly Utf8String currentDirectory, libraryDirectory;
    private readonly CompilerOptions overrides = new();
    private readonly Utf8StringComparer comparer;
    private readonly Func<DateTime> now;
    private readonly bool hashWithText;
    private readonly ContentMapperHost mapperHost;
    private readonly bool ownsMapperHost;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Utf8String, ProjectTask> projects;
    private IReadOnlyList<ProjectTask> buildOrder = [];
    private bool disposed;
    private bool hasBuilt;
    internal Action<EmitResult>? OnEmittedFiles { get; set; }
    internal bool WatchMode { get; set; }

    private sealed class ProjectTask(Utf8String path)
    {
        internal Utf8String Path { get; } = path;
        internal ParsedConfig? Config;
        internal List<ProjectTask> Upstream = [];
        internal Dictionary<ProjectTask, Utf8String> ReferencePaths = [];
        internal ProjectBuildStatus Status;
        internal ProjectBuildResult? Result;
        internal Utf8String Input, Output, StatePath, Version;
        internal DateTime NewestInputTime, OldestOutputTime;
        internal BuildInfo? State;
        internal Utf8String CachedStatePath;
        internal DateTime StateTime;
        internal ContentMapperProject? Mapper;
        internal Utf8String[] MapperIdentities = [];
        internal bool HasTimes, UpstreamSkipped;
        internal bool ConfigDirty;
        internal bool Pending = true, StatusKnown;
        internal IncrementalProgram? Previous;
        internal Utf8String[] MapperPaths = [];
    }

    public ProjectBuilder(IFileSystem fileSystem, Utf8String currentDirectory, CompilerOptions? compilerOptions = null,
        Utf8String defaultLibraryDirectory = default, ContentMapperHost? mapperHost = null,
        Func<DateTime>? now = null, bool hashWithText = false)
    {
        this.fileSystem = fileSystem;
        this.currentDirectory = CompilerPath.Normalize(currentDirectory);
        libraryDirectory = defaultLibraryDirectory.Length == 0 && fileSystem is LibraryFileSystem libraries ? libraries.LibraryDirectory : defaultLibraryDirectory;
        if (compilerOptions is not null) overrides.Merge(compilerOptions);
        overrides.SetRaw("build"u8, "true"u8);
        comparer = fileSystem.CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
        projects = new(comparer);
        this.now = now is null ? () => DateTime.UtcNow : () => now().ToUniversalTime();
        this.hashWithText = hashWithText;
        this.mapperHost = mapperHost ?? new();
        ownsMapperHost = mapperHost is null;
    }

    public async ValueTask<BuildResult> BuildAsync(IReadOnlyList<Utf8String> projectPaths, BuildOptions? options = null,
        CancellationToken cancellation = default)
    {
        long started = Stopwatch.GetTimestamp();
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Builders, 1);
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var (order, graphErrors) = await CreateGraphAsync(projectPaths.Count == 0 ? ["."u8] : projectPaths, cancellation).ConfigureAwait(false);
            double configTime = Stopwatch.GetElapsedTime(started).TotalSeconds;
            buildOrder = order;
            var messages = new List<Diagnostic>();
            if (options.Verbose && !options.Clean)
                messages.Add(new(Messages.Projects_in_this_build_Colon_0, 0, 0,
                    [Utf8String.Concat(order.Select(task => "\r\n    * "u8 + Relative(task.Path)))]));
            if (graphErrors.Count != 0) return new(CompilerExitStatus.ProjectReferenceCycle, [], graphErrors, messages);
            using var slots = new SemaphoreSlim(overrides.SingleThreaded == true ? 1 : options.Builders);
            var work = new Dictionary<ProjectTask, Task>();
            foreach (var project in order)
            {
                var dependencies = project.Upstream.Select(task => work[task]).ToArray();
                work[project] = BuildAfterDependenciesAsync(project, dependencies);
            }
            await Task.WhenAll(work.Values).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            hasBuilt = true;
            var results = order.Select(task => task.Result!).ToArray();
            var diagnostics = results.SelectMany(result => result.Diagnostics).ToArray();
            if (options.Clean && options.Dry)
            {
                var deleted = results.SelectMany(result => result.DeletedFiles).ToArray();
                if (deleted.Length != 0) messages.Add(new(Messages.A_non_dry_build_would_delete_the_following_files_Colon_0, 0, 0,
                    [Utf8String.Concat(deleted.Select(path => "\r\n * "u8 + path))]));
            }
            var statistics = overrides.Diagnostics == true || overrides.ExtendedDiagnostics == true || results.Any(result => result.Statistics is not null)
                ? CompilationStatistics.Aggregate(results.Select(result => result.Statistics).OfType<CompilationStatistics>(), Stopwatch.GetElapsedTime(started).TotalSeconds) : null;
            if (statistics is not null) statistics = statistics with { ConfigTime = statistics.ConfigTime + configTime };
            return new(results.Select(result => result.ExitStatus).DefaultIfEmpty().Max(), results, diagnostics, messages) { Statistics = statistics };

            async Task BuildAfterDependenciesAsync(ProjectTask project, Task[] dependencies)
            {
                await Task.WhenAll(dependencies).ConfigureAwait(false);
                await slots.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    if (WatchMode && hasBuilt) UpdateFromUpstream(project, options);
                    if (WatchMode && !project.Pending)
                    {
                        var retainedErrors = project.Result?.Diagnostics ?? [];
                        var retainedMessages = options.Verbose && retainedErrors.Count != 0 && StatusMessage(project) is { } message
                            ? new[] { message } : [];
                        project.Result = new(project.Path, project.Status, CompilerExitStatus.Success, retainedErrors, retainedMessages, [], [], [], null);
                    }
                    else
                    {
                        project.Result = await BuildProjectAsync(project, options, cancellation).ConfigureAwait(false);
                        project.StatusKnown = true; project.Pending = false;
                    }
                }
                finally { slots.Release(); }
            }
        }
        finally { gate.Release(); }
    }

    private async ValueTask<(List<ProjectTask> Order, List<Diagnostic> Errors)> CreateGraphAsync(IReadOnlyList<Utf8String> paths, CancellationToken cancellation)
    {
        var previous = WatchMode ? new Dictionary<Utf8String, ProjectTask>(projects, comparer) : new(comparer);
        if (!WatchMode) foreach (var project in projects.Values)
            if (project.Mapper is not null) await project.Mapper.DisposeAsync().ConfigureAwait(false);
        projects.Clear();
        var parser = new ConfigParser(fileSystem, currentDirectory);
        Utf8String ConfigPath(Utf8String path)
        {
            path = CompilerPath.Resolve(currentDirectory, path);
            return path.EndsWith(".json"u8, StringComparison.OrdinalIgnoreCase) ? path : CompilerPath.Combine(path, "tsconfig.json"u8);
        }
        var roots = paths.Select(ConfigPath).ToArray();
        var pending = new Queue<Utf8String>(roots);
        while (pending.TryDequeue(out var path))
        {
            cancellation.ThrowIfCancellationRequested();
            if (projects.ContainsKey(path)) continue;
            var task = previous.Remove(path, out var old) ? old : new ProjectTask(path) { ConfigDirty = true };
            projects[path] = task;
            task.Upstream.Clear(); task.ReferencePaths.Clear();
            if (task.ConfigDirty)
            {
                var next = fileSystem.FileExists(path) ? parser.Parse(path, overrides, cancellation) : null;
                var mapper = next is { ContentMappers.Length: > 0, Options.RunExternalCode: true }
                    ? await mapperHost.GetProjectAsync(next, cancellation).ConfigureAwait(false) : null;
                if (task.Mapper is not null) await task.Mapper.DisposeAsync().ConfigureAwait(false);
                task.Mapper = mapper; task.Config = next; task.ConfigDirty = false;
                task.Pending = true; task.StatusKnown = false; task.MapperIdentities = []; task.MapperPaths = [];
            }
            if (task.Config is null) continue;
            task.StatePath = IncrementalOptions.GetBuildInfoFileName(task.Config, currentDirectory, fileSystem.CaseSensitive, true);
            foreach (var reference in task.Config.References) pending.Enqueue(ConfigPath(reference.Path));
        }
        foreach (var removed in previous.Values)
            if (removed.Mapper is not null) await removed.Mapper.DisposeAsync().ConfigureAwait(false);
        var order = new List<ProjectTask>();
        var errors = new List<Diagnostic>();
        var completed = new HashSet<ProjectTask>();
        var active = new HashSet<ProjectTask>();
        var stack = new Stack<(ProjectTask Task, int Next, bool Circular)>();
        foreach (var root in roots)
        {
            stack.Push((projects[root], -1, false));
            while (stack.TryPop(out var frame))
            {
                cancellation.ThrowIfCancellationRequested();
                var task = frame.Task;
                if (frame.Next == -1)
                {
                    if (completed.Contains(task)) continue;
                    if (!active.Add(task))
                    {
                        if (!frame.Circular) errors.Add(new(Messages.Project_references_may_not_form_a_circular_graph_Cycle_detected_Colon_0, 0, 0,
                            [Utf8String.Join("\n"u8, stack.Reverse().Select(entry => entry.Task.Path))]));
                        continue;
                    }
                    frame.Next = 0;
                }
                var references = task.Config?.References ?? [];
                if (frame.Next < references.Length)
                {
                    var reference = references[frame.Next++];
                    stack.Push(frame);
                    var child = projects[ConfigPath(reference.Path)];
                    if (!active.Contains(child))
                    {
                        task.Upstream.Add(child);
                        task.ReferencePaths.TryAdd(child, reference.Path);
                    }
                    stack.Push((child, -1, frame.Circular || reference.Circular));
                }
                else
                {
                    active.Remove(task); completed.Add(task); order.Add(task);
                }
            }
        }
        return (order, errors);
    }

    private Utf8String Relative(Utf8String path) => CompilerPath.Relative(currentDirectory, path, fileSystem.CaseSensitive);
    private DateTime Time(Utf8String path) => fileSystem.Stat(path)?.LastWriteTimeUtc ?? DateTime.MinValue;

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            foreach (var project in projects.Values)
                if (project.Mapper is not null) await project.Mapper.DisposeAsync().ConfigureAwait(false);
            projects.Clear();
            if (ownsMapperHost) await mapperHost.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}

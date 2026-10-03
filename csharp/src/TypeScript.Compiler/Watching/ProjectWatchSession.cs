using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Execution;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Incremental;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;

namespace TypeScript.Compiler.Watching;

public sealed record WatchCycleResult(bool Rebuilt, IncrementalProgram? Program, IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<Utf8String> EmittedFiles, CompilerExitStatus ExitStatus, Diagnostic? Starting = null, Diagnostic? Finished = null)
{
    internal CompilationStatistics? Statistics { get; init; }
}

/// <summary>A serialized compiler watch session retaining incremental state and mapper projects between cycles.</summary>
public sealed class ProjectWatchSession : IAsyncDisposable
{
    private readonly IFileSystem fileSystem;
    private readonly Utf8String currentDirectory, libraryDirectory;
    private readonly CompilerOptions overrides = new();
    private readonly Utf8StringComparer comparer;
    private readonly ContentMapperHost mapperHost;
    private readonly bool ownsMapperHost, hashWithText;
    private readonly NativeWatchBackend? nativeBackend;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextWriter? warnings;
    private ContentMapperProject? mapper;
    private ParsedConfig config;
    private IncrementalProgram? program;
    private HashSet<Utf8String> seen;
    private Utf8String[] configPaths = [], mapperPaths = [];
    private readonly Dictionary<Utf8String, DateTime?> configTimes;
    private bool started, disposed, configHasErrors, forceFullBuild;
    public WatchManager Watches { get; }
    public IncrementalProgram? Program => program;
    public ParsedConfig Configuration => config;

    public ProjectWatchSession(IFileSystem fileSystem, Utf8String currentDirectory, ParsedConfig configuration,
        CompilerOptions? commandLineOptions = null, Utf8String defaultLibraryDirectory = default, IWatchBackend? backend = null,
        ContentMapperHost? mapperHost = null, TextWriter? warnings = null, bool hashWithText = false)
    {
        this.fileSystem = fileSystem; this.currentDirectory = CompilerPath.Normalize(currentDirectory); config = configuration;
        this.warnings = warnings; this.hashWithText = hashWithText;
        libraryDirectory = defaultLibraryDirectory.IsEmpty && fileSystem is LibraryFileSystem libraries ? libraries.LibraryDirectory : defaultLibraryDirectory;
        comparer = fileSystem.CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
        seen = new(comparer); configTimes = new(comparer);
        if (commandLineOptions is not null) overrides.Merge(commandLineOptions);
        this.mapperHost = mapperHost ?? new(); ownsMapperHost = mapperHost is null;
        if (backend is null) backend = nativeBackend = new();
        Watches = new(backend, fileSystem.DirectoryExists, warnings);
    }

    public async ValueTask<WatchCycleResult> StartAsync(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("The watch session has already started.");
            await ReplaceMapperAsync(cancellation).ConfigureAwait(false);
            SetConfigPaths();
            var result = await BuildAsync(true, true, cancellation).ConfigureAwait(false);
            started = true;
            return result;
        }
        finally { gate.Release(); }
    }

    public async ValueTask<WatchCycleResult> CycleAsync(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!started) throw new InvalidOperationException("The watch session has not started.");
            var (events, overflow) = Watches.DrainEvents();
            var changes = new Dictionary<Utf8String, WatchEventKind>(comparer);
            foreach (var (path, kind) in events) changes[CompilerPath.Resolve(currentDirectory, path)] = kind;
            bool modified = false;
            bool manifestChanged = config.ContentMappers.Any(mapper => !mapper.PackageDirectory.IsEmpty
                && changes.ContainsKey(CompilerPath.Combine(mapper.PackageDirectory, "package.json"u8)));
            if (!config.FileName.IsEmpty && (manifestChanged || configHasErrors || configPaths.Any(changes.ContainsKey) || ConfigTimesChanged()))
            {
                var next = new ConfigParser(fileSystem, currentDirectory).Parse(config.FileName, overrides, cancellation);
                if (next.SourceFile is null)
                {
                    configHasErrors = true;
                    return new(false, program, next.Diagnostics, [], CompilerExitStatus.DiagnosticsWithOutputsSkipped, Finished: Completed(next.Diagnostics.Length));
                }
                modified = configHasErrors || !SameConfiguration(config, next);
                configHasErrors = false; config = next; SetConfigPaths();
                await ReplaceMapperAsync(cancellation).ConfigureAwait(false);
            }
            bool relevant = changes.Keys.Any(IsRelevant);
            if (!overflow && !modified && (!relevant || changes.Count == 0))
                return new(false, program, [], [], CompilerExitStatus.Success);
            forceFullBuild |= overflow || modified;
            if (mapper is not null && changes.Keys.Any(path => mapperPaths.Contains(path, comparer)))
            {
                forceFullBuild = true;
                try { await mapper.RefreshAsync(cancellation).ConfigureAwait(false); }
                catch (MapperException)
                {
                    return new(false, program, [new(Messages.The_content_mapper_process_could_not_be_started_or_initialized, 0, 0, [])], [],
                        CompilerExitStatus.DiagnosticsWithOutputsSkipped);
                }
            }
            return await BuildAsync(false, modified, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Watches.ForceOverflow(signal: true); throw; }
        finally { gate.Release(); }
    }

    public async Task RunAsync(Func<WatchCycleResult, CancellationToken, ValueTask> report, CancellationToken cancellation = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        if (!started) await report(await StartAsync(linked.Token).ConfigureAwait(false), linked.Token).ConfigureAwait(false);
        await Watches.RunAsync(async token => await report(await CycleAsync(token).ConfigureAwait(false), token).ConfigureAwait(false), linked.Token).ConfigureAwait(false);
    }

    private async ValueTask<WatchCycleResult> BuildAsync(bool initial, bool modified, CancellationToken cancellation)
    {
        await using var capture = CompilationCapture.Start(fileSystem, currentDirectory, config, warnings, allowTrace: false);
        if (!config.FileName.IsEmpty && config.WildcardDirectories.Count != 0)
            config = new ConfigParser(fileSystem, currentDirectory).ReloadFiles(config, cancellation);
        var tracking = new DependencyFileSystem(fileSystem);
        foreach (var path in configPaths.Concat(config.WildcardDirectories.Keys)) tracking.Add(path);
        await UpdateMapperPathsAsync(cancellation).ConfigureAwait(false);
        foreach (var path in mapperPaths) tracking.Add(path);
        var graph = await CompilerProgram.CreateAsync(tracking, currentDirectory, config,
            previous: forceFullBuild || modified ? null : program?.Program, defaultLibraryDirectory: libraryDirectory,
            mapperProject: mapper, cancellation: cancellation).ConfigureAwait(false);
        var identities = mapper is null ? [] : await mapper.IdentitiesAsync(cancellation).ConfigureAwait(false);
        var next = await IncrementalProgram.CreateAsync(graph, fileSystem, previous: program, mapperIdentities: identities,
            defaultLibraryDirectory: libraryDirectory, hashWithText: hashWithText, cancellation: cancellation).ConfigureAwait(false);
        var diagnostics = await next.GetDiagnosticsAsync(cancellation).ConfigureAwait(false);
        var emit = await next.EmitAsync(cancellation: cancellation).ConfigureAwait(false);
        diagnostics = DiagnosticCollection.SortAndDeduplicate(diagnostics.Concat(emit.Diagnostics));
        cancellation.ThrowIfCancellationRequested();
        program = next;
        seen = tracking.Paths.Select(path => CompilerPath.Resolve(currentDirectory, path)).ToHashSet(comparer);
        CaptureConfigTimes();
        bool reconciled = true;
        try { Watches.ReconcileWatches(DesiredWatches()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            warnings?.Write(error.Message + "\n");
            Watches.ForceOverflow(); forceFullBuild = true; reconciled = false;
        }
        if (reconciled) forceFullBuild = false;
        return new(true, next, diagnostics, emit.EmittedFiles, diagnostics.Count == 0 ? CompilerExitStatus.Success
            : emit.EmitSkipped ? CompilerExitStatus.DiagnosticsWithOutputsSkipped : CompilerExitStatus.DiagnosticsWithOutputsGenerated,
            new(initial ? Messages.Starting_compilation_in_watch_mode : Messages.File_change_detected_Starting_incremental_compilation, 0, 0, []),
            reconciled ? Completed(diagnostics.Count) : null) { Statistics = capture?.Statistics(graph) };
    }

    private void SetConfigPaths() => configPaths = config.FileName.IsEmpty ? [] : [config.FileName, .. config.ExtendedConfigFiles];
    private DateTime? Time(Utf8String path) => fileSystem.Stat(path)?.LastWriteTimeUtc;
    private bool ConfigTimesChanged() => configPaths.Any(path => !configTimes.TryGetValue(path, out var time) || time != Time(path));
    private void CaptureConfigTimes() { configTimes.Clear(); foreach (var path in configPaths) configTimes[path] = Time(path); }
    private static Diagnostic Completed(int errors) => errors == 1 ? new(Messages.Found_1_error_Watching_for_file_changes, 0, 0, [])
        : new(Messages.Found_0_errors_Watching_for_file_changes, 0, 0, [Utf8String.FromString(errors.ToString(System.Globalization.CultureInfo.InvariantCulture))]);

    private async ValueTask ReplaceMapperAsync(CancellationToken cancellation)
    {
        var next = config.ContentMappers.Length != 0 && config.Options.RunExternalCode == true
            ? await mapperHost.GetProjectAsync(config, cancellation).ConfigureAwait(false) : null;
        if (mapper is not null) await mapper.DisposeAsync().ConfigureAwait(false);
        mapper = next;
    }

    private async ValueTask UpdateMapperPathsAsync(CancellationToken cancellation)
    {
        mapperPaths = config.ContentMappers.Where(mapper => !mapper.PackageDirectory.IsEmpty)
            .Select(mapper => CompilerPath.Combine(mapper.PackageDirectory, "package.json"u8))
            .Concat(mapper is null ? [] : await mapper.WatchedFilesAsync(cancellation).ConfigureAwait(false)).Distinct(comparer).ToArray();
    }

    private bool IsRelevant(Utf8String path)
    {
        var canonical = CompilerPath.Resolve(currentDirectory, path);
        if (seen.Contains(canonical) || mapperPaths.Contains(canonical, comparer)) return true;
        if (!config.FileName.IsEmpty && (PossiblyMatchesFile(canonical) || PossiblyMatchesDirectory(canonical))) return true;
        return fileSystem.DirectoryExists(path) && Watches.IsPathUnderWatch(path, fileSystem.CaseSensitive);
    }

    private bool PossiblyMatchesDirectory(Utf8String path) => config.WildcardDirectories.Any(pair => pair.Value
        ? CompilerPath.Contains(pair.Key, path, fileSystem.CaseSensitive) : comparer.Equals(pair.Key, path));

    private bool PossiblyMatchesFile(Utf8String path)
    {
        if (config.FileNames.Contains(path, comparer)) return true;
        foreach (var include in config.Includes)
            if (!include.AsSpan().ContainsAny((byte)'*', (byte)'?') && CompilerPath.BaseName(include).Contains((byte)'.')
                && comparer.Equals(CompilerPath.Resolve(CompilerPath.DirectoryName(config.FileName), include), path)) return true;
        var directory = CompilerPath.DirectoryName(path);
        if (!PossiblyMatchesDirectory(directory)) return false;
        if (config.ContentMappers.SelectMany(mapper => mapper.Extensions).Any(extension => path.EndsWith(extension,
            fileSystem.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))) return true;
        var extension = CompilerPath.Extension(path);
        return extension == ".ts"u8 || extension == ".tsx"u8 || extension == ".mts"u8 || extension == ".cts"u8
            || extension == ".js"u8 || extension == ".jsx"u8 || extension == ".mjs"u8 || extension == ".cjs"u8 || extension == ".json"u8;
    }

    private IReadOnlyDictionary<Utf8String, bool> DesiredWatches()
    {
        var desired = new Dictionary<Utf8String, bool>(comparer);
        foreach (var (path, recursive) in config.WildcardDirectories) desired[fileSystem.RealPath(path)] = recursive;
        if (config.FileName.IsEmpty && desired.Count == 0) desired[fileSystem.RealPath(currentDirectory)] = false;
        foreach (var path in configPaths.Concat(config.FileName.IsEmpty ? config.FileNames.Select(path => CompilerPath.Resolve(currentDirectory, path)) : []))
            desired.TryAdd(CompilerPath.DirectoryName(fileSystem.RealPath(path)), false);
        var coverage = new DirectoryWatchSet(fileSystem.CaseSensitive);
        foreach (var (path, recursive) in Watches.ResolveDesiredDirectories(desired)) coverage.Set(path, recursive);
        foreach (var path in seen)
        {
            var directory = CompilerPath.DirectoryName(path);
            if (!coverage.Covers(directory) && WatchPaths.CanWatchDirectory(directory)) coverage.Set(directory, false);
        }
        return Watches.ResolveDesiredDirectories(coverage.Directories);
    }

    private static bool SameConfiguration(ParsedConfig left, ParsedConfig right) => SameOptions(left.Options, right.Options)
        && SameOptions(left.WatchOptions, right.WatchOptions) && SameOptions(left.TypeAcquisition, right.TypeAcquisition)
        && left.FileNames.SequenceEqual(right.FileNames) && left.References.Select(item => (item.Path, item.Circular, item.Prepend))
            .SequenceEqual(right.References.Select(item => (item.Path, item.Circular, item.Prepend)))
        && left.ContentMappers.Length == right.ContentMappers.Length && left.ContentMappers.Zip(right.ContentMappers).All(pair =>
            pair.First.Name == pair.Second.Name && pair.First.Version == pair.Second.Version && pair.First.Package == pair.Second.Package
            && pair.First.PackageDirectory == pair.Second.PackageDirectory && pair.First.DynamicConfig == pair.Second.DynamicConfig
            && pair.First.Exec.SequenceEqual(pair.Second.Exec) && pair.First.Extensions.SequenceEqual(pair.Second.Extensions)
            && SameJson(pair.First.Options, pair.Second.Options) && SameJson(pair.First.CompilerOptions, pair.Second.CompilerOptions));

    private static bool SameJson(JsonElement? left, JsonElement? right) => left is null ? right is null
        : right is not null && JsonElement.DeepEquals(left.Value, right.Value);

    private static bool SameOptions(CompilerOptions left, CompilerOptions right) => left.Values.Count == right.Values.Count
        && left.Values.All(pair => right.Values.TryGetValue(pair.Key, out var value) && JsonElement.DeepEquals(pair.Value, value));

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true; Watches.Dispose();
            if (nativeBackend is not null) await nativeBackend.DisposeAsync().ConfigureAwait(false);
            if (mapper is not null) await mapper.DisposeAsync().ConfigureAwait(false);
            if (ownsMapperHost) await mapperHost.DisposeAsync().ConfigureAwait(false);
            program = null; seen.Clear();
        }
        finally { gate.Release(); }
    }
}

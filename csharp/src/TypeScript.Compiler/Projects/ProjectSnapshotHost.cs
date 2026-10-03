using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Projects;

/// <summary>Builds independent snapshots without holding a session lock across filesystem or mapper callbacks.</summary>
public sealed class ProjectSnapshotHost : IAsyncDisposable
{
    internal static readonly Utf8String InferredId = "/dev/null/inferred"u8;
    private readonly object sync = new();
    private readonly IFileSystem fileSystem;
    private readonly ContentMapperHost mapperHost;
    private readonly ProjectParseCache parseCache = new();
    private int references = 1;
    private bool disposed;
    private long nextSnapshot;
    public ProjectHostOptions Options { get; }
    internal Utf8StringComparer Comparer { get; }

    public ProjectSnapshotHost(IFileSystem fileSystem, ProjectHostOptions? options = null)
        : this(fileSystem, options, new(log: options?.MapperLog) { StartProcess = options?.StartMapperProcess }) { }

    internal ProjectSnapshotHost(IFileSystem fileSystem, ProjectHostOptions? options, ContentMapperHost mapperHost)
    {
        this.fileSystem = fileSystem;
        this.mapperHost = mapperHost;
        Options = options ?? new();
        if (Options.DefaultLibraryDirectory.IsEmpty && fileSystem is LibraryFileSystem libraries)
            Options = Options with { DefaultLibraryDirectory = libraries.LibraryDirectory };
        if (Options.Concurrency < 1) throw new ArgumentOutOfRangeException(nameof(options));
        Comparer = fileSystem.CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
    }

    internal Utf8String FileName(Utf8String value) => CompilerPath.IsDynamic(value) ? value : CompilerPath.Resolve(Options.CurrentDirectory, value);
    internal Utf8String Path(Utf8String value)
    {
        var path = FileName(value);
        return fileSystem.CaseSensitive ? path : path.ToLowerInvariant();
    }
    internal Utf8String ReferencePath(Utf8String value) => Path(value.EndsWith(".json"u8, StringComparison.OrdinalIgnoreCase)
        ? value : CompilerPath.Combine(value, "tsconfig.json"u8));

    internal Utf8String? FindConfig(IFileSystem fs, Utf8String directory, Utf8String custom = default) =>
        SearchConfigs(fs, directory, custom, false, false, false);

    internal Utf8String? FindAncestorConfig(IFileSystem fs, Utf8String configName, Utf8String custom = default) =>
        SearchConfigs(fs, CompilerPath.DirectoryName(configName), custom, true, true, CompilerPath.BaseName(configName) != "tsconfig.json"u8);

    private Utf8String? SearchConfigs(IFileSystem fs, Utf8String directory, Utf8String custom, bool skipCustom, bool skipTs, bool skipJs)
    {
        if (CompilerPath.IsDynamic(directory)) return null;
        directory = FileName(directory);
        if (!custom.IsEmpty)
        {
            Utf8String current = directory;
            while (true)
            {
                var candidate = CompilerPath.Combine(current, custom);
                if (!skipCustom && fs.FileExists(candidate)) return candidate;
                var parent = CompilerPath.DirectoryName(current);
                if (CompilerPath.BaseName(current) == "node_modules"u8 || current == parent) break;
                current = parent; skipCustom = false;
            }
        }
        while (true)
        {
            foreach (Utf8String name in new Utf8String[] { "tsconfig.json"u8, "jsconfig.json"u8 })
            {
                if (name == "tsconfig.json"u8 ? skipTs : skipJs) continue;
                var candidate = CompilerPath.Combine(directory, name);
                if (fs.FileExists(candidate)) return candidate;
            }
            if (CompilerPath.BaseName(directory) == "node_modules"u8) return null;
            var parent = CompilerPath.DirectoryName(directory);
            if (parent == directory) return null;
            directory = parent; skipTs = skipJs = false;
        }
    }

    internal void Retain() => Interlocked.Increment(ref references);
    internal async ValueTask ReleaseAsync()
    {
        if (Interlocked.Decrement(ref references) == 0) await mapperHost.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask<ProjectWorkspaceSnapshot> CreateAsync(SnapshotRequest? request = null,
        ProjectWorkspaceSnapshot? previous = null, CancellationToken cancellation = default)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (previous is not null && previous.Host != this) throw new ArgumentException("Snapshot belongs to another host", nameof(previous));
            Retain();
        }
        try
        {
            await using var lease = previous?.Acquire();
            await using var builder = new Builder(this, request ?? new(), previous, cancellation);
            return await builder.BuildAsync().ConfigureAwait(false);
        }
        finally { await ReleaseAsync().ConfigureAwait(false); }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed) return default;
            disposed = true;
        }
        return ReleaseAsync();
    }

    private sealed class Builder : IAsyncDisposable
    {
        private readonly ProjectSnapshotHost host;
        private readonly SnapshotRequest request;
        private readonly CancellationToken cancellation;
        private readonly ProjectWorkspaceSnapshot? previous;
        private readonly ulong id;
        private readonly SnapshotFileSystem fs;
        private readonly FileChangeSummary changes;
        private readonly Dictionary<Utf8String, ProjectSnapshot> projects;
        private readonly Dictionary<Utf8String, int> openProjects;
        private readonly Dictionary<Utf8String, (Utf8String FileName, int Count)> openFiles;
        private readonly Dictionary<Utf8String, ParsedConfig> configs;
        private readonly HashSet<Utf8String> opened, retained;
        private readonly CompilerOptions inferredOptions;
        private readonly ContentMapper[] inferredMappers;
        private readonly Utf8String locale;
        private readonly Utf8String customConfig;
        private readonly Dictionary<Utf8String, Utf8String?> nearestConfigs;
        private readonly bool reloadConfigs;
        private readonly HashSet<Utf8String> mapperRefreshProjects, mapperReloadConfigs;
        private readonly Dictionary<Utf8String, Utf8String> selectedProjects;
        private readonly List<Utf8String> created = [];
        private bool transferred;

        internal Builder(ProjectSnapshotHost host, SnapshotRequest request, ProjectWorkspaceSnapshot? previous, CancellationToken cancellation)
        {
            this.host = host; this.request = request; this.previous = previous; this.cancellation = cancellation;
            id = checked((ulong)Interlocked.Increment(ref host.nextSnapshot));
            projects = previous is null ? new(host.Comparer) : new(previous.ProjectMap, host.Comparer);
            openProjects = previous is null ? new(host.Comparer) : new(previous.OpenProjectCounts, host.Comparer);
            openFiles = previous is null ? new(host.Comparer) : new(previous.OpenFileCounts, host.Comparer);
            configs = new(host.Comparer); opened = new(host.Comparer); retained = new(host.Comparer);
            selectedProjects = new(host.Comparer);
            customConfig = (request.CustomConfigFileName ?? previous?.CustomConfigFileName ?? Utf8String.Empty).Trim();
            if (customConfig.Contains((byte)'/') || customConfig.Contains((byte)'\\') || customConfig == "."u8 || customConfig == ".."u8) customConfig = default;
            var overlay = new OverlayFileSystem(host.fileSystem, host.Options.CurrentDirectory, previous?.FileSystem.Overlay.Overlays.Values);
            if (request.FileSystem is { } replacement)
            {
                if (replacement.CaseSensitive != host.fileSystem.CaseSensitive)
                    throw new ArgumentException("A snapshot filesystem must preserve the host's case sensitivity");
                overlay = new(replacement, host.Options.CurrentDirectory, overlay.Overlays.Values);
            }
            changes = new(host.fileSystem.CaseSensitive)
            { InvalidateAll = request.InvalidateAll || request.ReplaceFileSystem || previous?.FileSystemOverridden == true && request.FileSystem is null };
            if (request.FileSystemChanges is { } fileSystemChanges) changes.Merge(fileSystemChanges);
            // Open notifications form publication boundaries in the editor protocol.
            var batch = new List<FileChange>();
            foreach (var change in request.FileChanges)
            {
                batch.Add(change);
                if (change.Kind == FileChangeKind.Open) ApplyBatch();
            }
            if (batch.Count != 0) ApplyBatch();
            if (overlay.BaseFileSystem is RequestFileSystem requestFiles) requestFiles.ExpandChanges(changes);
            var mapperChanges = changes.Changed.Concat(changes.Created).Concat(changes.Deleted).ToHashSet(host.Comparer);
            bool refreshAllMappers = changes.InvalidateAll || changes.Changed.Count + changes.Deleted.Count > 1000;
            mapperRefreshProjects = new(host.Comparer); mapperReloadConfigs = new(host.Comparer);
            foreach (var project in projects.Values)
            {
                if (project.Resource is { MapperWatchedFiles.Length: > 0 } resource
                    && (refreshAllMappers || resource.MapperWatchedFiles.Any(path => mapperChanges.Contains(host.Path(path)))))
                    mapperRefreshProjects.Add(project.Id);
                if (project.Configuration.ContentMappers.Any(mapper => !mapper.PackageDirectory.IsEmpty
                    && mapperChanges.Contains(host.Path(CompilerPath.Combine(mapper.PackageDirectory, "package.json"u8)))))
                    mapperReloadConfigs.Add(host.Path(project.Configuration.FileName));
            }
            bool excessive = changes.HasExcessiveWatchEvents;
            if (!excessive && !changes.Opened.IsEmpty && !CompilerPath.IsDynamic(changes.Opened))
            {
                if (previous is null || !previous.FileSystem.TryGetCachedDocument(changes.Opened, out var cached) || cached is null)
                    changes.Created.Add(changes.Opened);
                else if (overlay.GetDocument(changes.Opened) is { } document && (document.Text != cached.Text || document.Kind != cached.Kind))
                    changes.Changed.Add(changes.Opened);
            }
            if (excessive && previous is not null)
            {
                bool overlaps = changes.Changed.Concat(changes.Deleted).Any(path => previous.FileSystem.Overlay.Overlays.ContainsKey(path)
                    || overlay.Overlays.ContainsKey(path) || previous.FileSystem.TryGetCachedDocument(path, out _));
                if (!overlaps && !changes.InvalidateAll) { changes.Changed.Clear(); changes.Deleted.Clear(); }
                else if (changes.IncludesWatchChangeOutsideNodeModules) changes.InvalidateFileCache = true;
                else changes.InvalidateNodeModules = true;
            }
            reloadConfigs = changes.InvalidateAll || excessive && changes.IncludesWatchChangeOutsideNodeModules
                && (changes.Changed.Concat(changes.Created).Concat(changes.Deleted).Any(IsConfigName)
                    || projects.Values.Any(project => project.Kind == ProjectKind.Configured && changes.Created.Concat(changes.Deleted)
                        .Any(path => MatchesConfigRoot(project.Configuration, path))));
            fs = new(overlay, host.Options.CurrentDirectory, previous?.FileSystem, changes) { ParseCache = host.parseCache };
            if (request.TypingsChanges.Count != 0)
            {
                if (!host.Options.TypingsLocation.IsEmpty) fs.ForgetDirectory(host.Options.TypingsLocation);
                foreach (var change in request.TypingsChanges)
                    foreach (var path in change.Result.Files) fs.ForgetDocument(path);
            }
            nearestConfigs = previous is null ? new(host.Comparer) : new(previous.NearestConfigs, host.Comparer);
            if (reloadConfigs || previous?.CustomConfigFileName != customConfig) nearestConfigs.Clear();
            else foreach (var path in nearestConfigs.Keys.ToArray())
                if (!overlay.Overlays.ContainsKey(path) || ConfigLookupChanged(path)) nearestConfigs.Remove(path);
            if (reloadConfigs && previous is not null)
                foreach (var config in previous.Projects.SelectMany(project => project.Program?.ProjectReferences.Projects.Values ?? [project.Configuration]))
                    foreach (var path in config.ExtendedConfigFiles.Append(config.FileName)) if (!path.IsEmpty) fs.ForgetDocument(path);
            if (!excessive && previous is not null)
                foreach (var path in changes.Changed.ToArray())
                    if (!overlay.Overlays.ContainsKey(path) && !IsConfigName(path) && previous.FileSystem.TryGetCachedDocument(path, out var before))
                    {
                        var after = fs.GetDocument(path);
                        if (before?.Text == after?.Text && before?.Kind == after?.Kind)
                        {
                            changes.Changed.Remove(path);
                            fs.ShareDocument(path, before);
                        }
                    }
            inferredOptions = CopyOptions(request.InferredOptions ?? previous?.InferredOptions ?? DefaultInferredOptions());
            inferredMappers = (request.InferredContentMappers ?? previous?.InferredContentMappers ?? []).ToArray();
            locale = request.Locale ?? previous?.Locale ?? Utf8String.Empty;
            foreach (var project in projects.Values) project.Resource?.Retain();

            void ApplyBatch()
            {
                var result = overlay.Apply(batch, host.Options.PositionEncoding);
                overlay = result.FileSystem;
                changes.Merge(result.Summary); changes.Closed.UnionWith(result.Summary.Closed);
                if (!result.Summary.Opened.IsEmpty) { opened.Add(result.Summary.Opened); changes.Opened = result.Summary.Opened; }
                if (!result.Summary.Reopened.IsEmpty) { opened.Add(result.Summary.Reopened); changes.Reopened = result.Summary.Reopened; }
                batch.Clear();
            }
        }

        private static CompilerOptions CopyOptions(CompilerOptions source)
        {
            var result = new CompilerOptions(); result.Merge(source); return result;
        }

        private static CompilerOptions DefaultInferredOptions()
        {
            var result = new CompilerOptions();
            foreach (Utf8String name in new Utf8String[] { "allowJs"u8, "allowImportingTsExtensions"u8, "strictNullChecks"u8,
                "strictFunctionTypes"u8, "sourceMap"u8, "allowNonTsExtensions"u8, "resolveJsonModule"u8 }) result.SetRaw(name, "true"u8);
            result.SetRaw("module"u8, "\"esnext\""u8); result.SetRaw("moduleResolution"u8, "\"bundler\""u8);
            result.SetRaw("target"u8, "\"es2025\""u8); result.SetRaw("jsx"u8, "\"react-jsx\""u8);
            return result;
        }

        private ParsedConfig Configure(ParsedConfig config)
        {
            var options = CopyOptions(config.Options);
            options.SetRaw("runExternalCode"u8, host.Options.RunExternalCode ? "true"u8 : "false"u8);
            return config with { Options = options, ApiOptions = config.ApiOptions ?? config.Options };
        }

        private ParsedConfig? ReadConfig(Utf8String fileName)
        {
            Utf8String path = host.Path(fileName);
            if (configs.TryGetValue(path, out var config)) return config;
            if (!fs.FileExists(fileName)) return null;
            var existing = previous?.Configurations.GetValueOrDefault(path);
            if (existing is not null && !NeedsConfigReload(existing)) config = existing;
            else
            {
                var sessionOptions = new CompilerOptions();
                sessionOptions.SetRaw("runExternalCode"u8, host.Options.RunExternalCode ? "true"u8 : "false"u8);
                config = Configure(new ConfigParser(fs, host.Options.CurrentDirectory).ParseForProject(fileName, sessionOptions, cancellation));
                if (existing is not null && !changes.InvalidateAll && !mapperReloadConfigs.Contains(path) && existing.SourceFile?.Source.Text == config.SourceFile?.Source.Text
                    && !changes.Changed.Concat(changes.Created).Concat(changes.Deleted).Concat(changes.Closed)
                        .Any(changed => host.Comparer.Equals(path, changed) || existing.ExtendedConfigFiles.Any(name => host.Comparer.Equals(host.Path(name), changed))))
                    // ReloadFileNamesOfParsedCommandLine preserves the configuration's parsing diagnostics.
                    config = existing with { FileNames = config.FileNames };
            }
            configs.Add(path, config);
            return config;
        }

        private bool MatchesConfigRoot(ParsedConfig config, Utf8String path) => config.FileNames.Contains(path, host.Comparer)
            || config.WildcardDirectories.Any(directory => directory.Value
                ? CompilerPath.Contains(directory.Key, path, host.fileSystem.CaseSensitive)
                : host.Comparer.Equals(directory.Key, CompilerPath.DirectoryName(path)));

        private bool NeedsConfigReload(ParsedConfig config)
        {
            if (changes.InvalidateAll || mapperReloadConfigs.Contains(host.Path(config.FileName))) return true;
            foreach (var path in changes.Changed.Concat(changes.Created).Concat(changes.Deleted).Concat(changes.Closed))
                if (reloadConfigs || host.Comparer.Equals(config.FileName, path) || config.ExtendedConfigFiles.Contains(path, host.Comparer)
                    || (changes.Created.Contains(path) || changes.Deleted.Contains(path)) && MatchesConfigRoot(config, path)
                        && (DocumentSnapshot.InferKind(path) != ScriptKind.Unknown || fs.DirectoryExists(path)
                            || config.FileNames.Any(root => CompilerPath.Contains(path, root, fs.CaseSensitive)))) return true;
            if (changes.Closed.Any(path => config.FileNames.Contains(path, host.Comparer) && !fs.FileExists(path))) return true;
            return opened.Any(path => !config.FileNames.Contains(path, host.Comparer) && MatchesConfigRoot(config, path));
        }

        private bool IsConfigName(Utf8String path) => CompilerPath.BaseName(path) is var name
            && (name == "tsconfig.json"u8 || name == "jsconfig.json"u8 || !customConfig.IsEmpty && host.Comparer.Equals(name, customConfig));

        private bool ConfigLookupChanged(Utf8String fileName) => reloadConfigs || changes.Changed.Concat(changes.Created).Concat(changes.Deleted)
            .Any(path => IsConfigName(path) && CompilerPath.Contains(CompilerPath.DirectoryName(path), fileName, fs.CaseSensitive));

        private bool Affected(ProjectSnapshot project)
        {
            if (previous is not null && previous.Locale != locale) return true;
            if (mapperRefreshProjects.Contains(project.Id)) return true;
            if (reloadConfigs || changes.HasExcessiveWatchEvents && changes.Changed.Count + changes.Deleted.Count > 1000) return true;
            if (changes.InvalidateAll) return true;
            if (changes.IsEmpty) return false;
            var config = project.Configuration;
            var touched = changes.Changed.Concat(changes.Created).Concat(changes.Deleted).Concat(changes.Closed).Concat(opened);
            foreach (Utf8String path in touched)
            {
                if (opened.Contains(path) && project.Program?.GetFileByPath(path) is { } old
                    && fs.GetDocument(path) is { } openedDocument && (old.Mapping?.Original.Text ?? old.Syntax.Source.Text) == openedDocument.Text
                    && (openedDocument.Kind == ScriptKind.Unknown || old.Syntax.ScriptKind == openedDocument.Kind)) continue;
                if (changes.Closed.Contains(path) && project.Program?.GetFileByPath(path) is { } closed
                    && fs.GetDocument(path) is { } disk && (closed.Mapping?.Original.Text ?? closed.Syntax.Source.Text) == disk.Text
                    && (disk.Kind == ScriptKind.Unknown || closed.Syntax.ScriptKind == disk.Kind)) continue;
                if (project.Program is null || project.ContainsFile(path) || config.FileNames.Any(root => host.Comparer.Equals(host.Path(root), path))
                    || host.Comparer.Equals(host.Path(config.FileName), path)
                    || config.ExtendedConfigFiles.Any(file => host.Comparer.Equals(host.Path(file), path))) return true;
                if (project.Program.MissingFiles.Any(file => host.Comparer.Equals(file, path))
                    || project.Program.PackageJsonLookupPaths.Any(file => host.Comparer.Equals(file, path))) return true;
                if (project.Program.ProjectReferences.Projects.Values.Any(reference => NeedsConfigReload(reference))) return true;
                if (opened.Contains(path) && !config.FileName.IsEmpty && MatchesConfigRoot(config, path)) return true;
                if (project.Program.SourceFiles.Any(file => file.Resolutions.Any(resolution => resolution.Resolution.AffectingLocations
                    .Any(location => CompilerPath.Contains(path, location, fs.CaseSensitive)
                        || previous?.FileSystem.WasMissingDirectory(location) == true && CompilerPath.Contains(location, path, fs.CaseSensitive))))) return true;
                if (changes.Created.Contains(path) || changes.Deleted.Contains(path))
                {
                    if (!config.FileName.IsEmpty && MatchesConfigRoot(config, path)
                        && (DocumentSnapshot.InferKind(path) != ScriptKind.Unknown || fs.DirectoryExists(path))) return true;
                    if (project.Program.MissingFiles.Any(file => CompilerPath.Contains(path, file, fs.CaseSensitive))) return true;
                }
            }
            return false;
        }

        internal async ValueTask<ProjectWorkspaceSnapshot> BuildAsync()
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var path in mapperRefreshProjects)
                if (projects[path].Resource?.Mapper is { } mapper) await mapper.RefreshAsync(cancellation).ConfigureAwait(false);
            foreach (var change in request.TypingsChanges)
                if (projects.TryGetValue(host.Path(change.ProjectId), out var project)
                    && (change.ProjectIdentity is null || ReferenceEquals(change.ProjectIdentity, project.Identity))
                    && change.Info.Equals(project.ComputeTypingsInfo())) projects[project.Id] = project.WithTypings(change with
                    { Result = new(change.Result.Files.ToArray(), change.Result.FilesToWatch.ToArray()) });
            foreach (var project in projects.Values.ToArray())
                if (Affected(project)) projects[project.Id] = project.Dirty(changedFile: SingleChangedFile(project));
            var reconfigured = new HashSet<Utf8String>(host.Comparer);
            var removed = request.RemovePrograms.Select(ProjectIds.Canonicalize).Select(host.Path).ToHashSet(host.Comparer);
            foreach (var item in request.ReconfigurePrograms)
            {
                Utf8String path = host.Path(ProjectIds.Canonicalize(item.Id));
                if (!reconfigured.Add(path)) throw new ArgumentException($"Synthetic program reconfigured more than once: {item.Id}");
                if (removed.Contains(path)) throw new ArgumentException($"Synthetic program cannot be reconfigured and removed: {item.Id}");
                if (projects.GetValueOrDefault(path)?.Kind != ProjectKind.Synthetic) throw new ArgumentException($"Synthetic program not found for reconfiguration: {item.Id}");
            }
            foreach (var path in removed)
                if (projects.GetValueOrDefault(path)?.Kind != ProjectKind.Synthetic) throw new ArgumentException($"Synthetic program not found for removal: {path}");
            foreach (var path in request.CloseProjects.Select(host.Path).Distinct(host.Comparer))
                if (openProjects.TryGetValue(path, out int count))
                    if (count > 1) openProjects[path] = count - 1; else openProjects.Remove(path);
            foreach (var name in request.OpenProjects.Distinct(host.Comparer))
            {
                var project = await EnsureConfiguredAsync(host.FileName(name)).ConfigureAwait(false)
                    ?? throw new ArgumentException($"Project not found for open: {name}");
                openProjects[project.Id] = openProjects.GetValueOrDefault(project.Id) + 1;
            }
            foreach (var path in request.CloseFiles.Select(host.Path).Distinct(host.Comparer))
                if (openFiles.TryGetValue(path, out var entry))
                    if (entry.Count > 1) openFiles[path] = (entry.FileName, entry.Count - 1); else openFiles.Remove(path);
            foreach (var name in request.OpenFiles.Distinct(host.Comparer))
            {
                Utf8String fileName = host.FileName(name), path = host.Path(name);
                openFiles[path] = (fileName, openFiles.GetValueOrDefault(path).Count + 1);
                opened.Add(path);
            }
            foreach (var path in removed) await RemoveAsync(path).ConfigureAwait(false);
            foreach (var creation in request.CreatePrograms)
            {
                int number = 1;
                while (projects.ContainsKey("/dev/null/synthetic/"u8 + Utf8String.Format(number))) number++;
                Utf8String path = "/dev/null/synthetic/"u8 + Utf8String.Format(number);
                projects.Add(path, new(path, ProjectKind.Synthetic, SyntheticConfig(creation)));
                await EnsureAsync(path).ConfigureAwait(false); created.Add(path);
            }
            foreach (var item in request.ReconfigurePrograms)
            {
                Utf8String path = host.Path(ProjectIds.Canonicalize(item.Id));
                projects[path] = projects[path].Dirty(SyntheticConfig(item.Program));
                await EnsureAsync(path).ConfigureAwait(false);
            }

            if (request.InferredOptions is not null || request.InferredContentMappers is not null)
                if (projects.TryGetValue(InferredId, out var inferred)) projects[InferredId] = inferred.Dirty(InferredConfig(inferred.Configuration.FileNames));
            bool refreshRoots = opened.Count != 0 || request.CloseFiles.Count != 0 || changes.Closed.Count != 0
                || request.InferredOptions is not null || request.InferredContentMappers is not null || previous?.CustomConfigFileName != customConfig || reloadConfigs;
            var inferredRoots = new List<Utf8String>();
            foreach (var name in AllOpenFiles())
            {
                Utf8String path = host.Path(name);
                ProjectSnapshot? project;
                if (opened.Contains(path) || fs.Overlay.Overlays.ContainsKey(path) && (ConfigLookupChanged(name) || previous?.CustomConfigFileName != customConfig))
                    project = await FindConfiguredForFileAsync(name, true).ConfigureAwait(false) ?? ExistingConfiguredForFile(path);
                else project = ExistingConfiguredForFile(path);
                if (project is null)
                {
                    if (IsSupported(name)) inferredRoots.Add(name);
                    else if (request.OpenFiles.Any(file => host.Comparer.Equals(host.Path(file), path)))
                        throw new ArgumentException($"no project found for opened file: {name}");
                }
                else retained.Add(project.Id);
            }
            inferredRoots = CollectInferredRoots();
            if (refreshRoots && inferredRoots.Count != 0)
            {
                var config = InferredConfig(inferredRoots.ToArray());
                if (!projects.TryGetValue(InferredId, out var inferred)) projects.Add(InferredId, new(InferredId, ProjectKind.Inferred, config));
                else if (!inferred.Configuration.FileNames.SequenceEqual(config.FileNames, host.Comparer)) projects[InferredId] = inferred.Dirty(config);
                if (request.InferredContentMappers is not null || opened.Any(path => inferredRoots.Any(root => host.Comparer.Equals(host.Path(root), path))))
                    await EnsureAsync(InferredId).ConfigureAwait(false);
            }
            else if (refreshRoots) await RemoveAsync(InferredId).ConfigureAwait(false);

            // Retain configured projects reachable from explicit opens and document placements.
            var reachable = new Queue<Utf8String>(openProjects.Keys.Concat(retained));
            var keep = new HashSet<Utf8String>(host.Comparer);
            while (reachable.TryDequeue(out var path))
            {
                if (!keep.Add(path) || !projects.TryGetValue(path, out var project)) continue;
                foreach (var reference in project.Configuration.References) reachable.Enqueue(host.ReferencePath(reference.Path));
            }
            if (opened.Count != 0 || request.CloseFiles.Count != 0 || request.CloseProjects.Count != 0)
                foreach (var project in projects.Values.Where(project => project.Kind == ProjectKind.Configured && !keep.Contains(project.Id)).ToArray())
                    await RemoveAsync(project.Id).ConfigureAwait(false);

            foreach (var name in request.EnsureFiles)
            {
                Utf8String path = host.Path(name);
                if (fs.Overlay.Overlays.ContainsKey(path) && ExistingConfiguredForFile(path) is null
                    && previous?.GetProject(InferredId)?.ContainsFile(path) != true)
                    foreach (var current in projects.Values.Where(project => project.Kind == ProjectKind.Configured).ToArray())
                        await EnsureAsync(current.Id).ConfigureAwait(false);
                var configured = fs.Overlay.Overlays.ContainsKey(path) ? ExistingConfiguredForFile(path) : null;
                if (configured is not null)
                {
                    await EnsureAsync(configured.Id).ConfigureAwait(false);
                    configured = projects.GetValueOrDefault(configured.Id);
                    if (configured?.ContainsFile(path) != true) configured = null;
                }
                bool inferredIsCurrent = fs.Overlay.Overlays.ContainsKey(path) && nearestConfigs.ContainsKey(path)
                    && projects.GetValueOrDefault(InferredId)?.ContainsFile(path) == true && !ConfigLookupChanged(name);
                if (configured is null && !inferredIsCurrent) configured = await FindConfiguredForFileAsync(host.FileName(name), true).ConfigureAwait(false);
                inferredRoots = CollectInferredRoots();
                if (configured is not null && fs.Overlay.Overlays.ContainsKey(path) && inferredRoots.Count == 0)
                    await RemoveAsync(InferredId).ConfigureAwait(false);
                if (configured is null && IsSupported(name))
                {
                    var roots = fs.Overlay.Overlays.ContainsKey(host.Path(name)) ? inferredRoots.ToArray() : [.. inferredRoots, host.FileName(name)];
                    var config = InferredConfig(roots);
                    if (!projects.TryGetValue(InferredId, out var inferred)) projects.Add(InferredId, new(InferredId, ProjectKind.Inferred, config));
                    else if (!inferred.Configuration.FileNames.SequenceEqual(roots, host.Comparer)) projects[InferredId] = inferred.Dirty(config);
                    await EnsureAsync(InferredId).ConfigureAwait(false);
                }
                foreach (var synthetic in projects.Values.Where(project => project.Kind == ProjectKind.Synthetic).ToArray())
                    if (synthetic.ContainsFile(host.Path(name)) || synthetic.Program?.MissingFiles.Contains(host.Path(name), host.Comparer) == true)
                        await EnsureAsync(synthetic.Id).ConfigureAwait(false);
            }
            foreach (var name in request.EnsureConfiguredFiles)
                await FindConfiguredForFileAsync(host.FileName(name), true).ConfigureAwait(false);
            foreach (var path in request.EnsurePrograms.Select(ProjectIds.Canonicalize).Select(host.Path))
                if (projects.ContainsKey(path)) await EnsureAsync(path).ConfigureAwait(false);
            if (request.LoadProjectTrees)
            {
                var requested = request.RequestedProjectTrees?.Select(host.Path).ToHashSet(host.Comparer);
                var currentTrees = projects.Values.Where(project => project.Kind == ProjectKind.Configured).ToArray();
                foreach (var project in currentTrees)
                    if (requested is null || (project.ConfigLoaded ? project.Configuration.References.Select(reference => host.ReferencePath(reference.Path))
                        : project.PotentialProjectReferences).Any(requested.Contains)) await EnsureAsync(project.Id).ConfigureAwait(false);
                var pendingTrees = new Queue<Utf8String>(currentTrees.Select(project => project.Id));
                var seen = new HashSet<Utf8String>(host.Comparer);
                while (pendingTrees.TryDequeue(out var path))
                {
                    if (!seen.Add(path)) continue;
                    if (!projects.TryGetValue(path, out var project) || project.Program is null || project.Configuration.Options.DisableReferencedProjectLoad == true) continue;
                    foreach (var reference in project.Configuration.References)
                    {
                        var child = host.ReferencePath(reference.Path);
                        if (requested is not null && !ContainsRequestedChild(project.Program.ProjectReferences, child, requested)) continue;
                        if (!projects.ContainsKey(child))
                        {
                            if (ReadConfig(child) is not { } config) continue;
                            projects.Add(child, new(child, ProjectKind.Configured, config));
                        }
                        await EnsureAsync(child).ConfigureAwait(false);
                        pendingTrees.Enqueue(child);
                    }
                }
            }
            if (request.EnsureAllPrograms)
                foreach (var path in projects.Keys.ToArray()) await EnsureAsync(path).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            fs.RetainFiles(projects.Values.SelectMany(project => project.Program is { } program
                    ? program.SourceFiles.Select(file => file.Syntax.FileName).Concat(program.MissingFiles).Concat(program.PackageJsonLookupPaths)
                    : project.Configuration.FileNames)
                .Concat(projects.Values.SelectMany(project => project.Configuration.ExtendedConfigFiles.Append(project.Configuration.FileName)))
                .Concat(projects.Values.SelectMany(project => project.Resource?.MapperWatchedFiles ?? []))
                .Concat(AllOpenFiles()));
            foreach (var project in projects.Values)
                if (project.Kind == ProjectKind.Configured && project.ConfigLoaded) configs.TryAdd(project.Id, project.Configuration);
            foreach (var name in nearestConfigs.Values.OfType<Utf8String>())
                if (previous?.Configurations.GetValueOrDefault(host.Path(name)) is { } known) configs.TryAdd(host.Path(name), known);
            var retainedImportFiles = AllOpenFiles().Concat(request.EnsureFiles).Select(host.Path).ToHashSet(host.Comparer);
            Dictionary<Utf8String, Utf8String> referenceSources = new(host.Comparer);
            foreach (var project in projects.Values)
                if (project.Program is { } program)
                    foreach (var (output, source) in program.ProjectReferences.Outputs) referenceSources.TryAdd(output, source.Source);
            foreach (var (path, project) in projects.ToArray())
            {
                var before = previous?.GetProject(path);
                var cache = before?.AutoImports?.Clone(fs, changes.IsEmpty && !changes.InvalidateFileCache && !changes.InvalidateNodeModules
                    && request.TypingsChanges.Count == 0 && ReferenceEquals(before.Program, project.Program), retainedImportFiles, referenceSources) ?? new AutoImportCache(fs, referenceSources);
                projects[path] = project.WithAutoImports(cache);
            }
            HashSet<Utf8String> watchedNodeModules = new(host.Comparer);
            if (host.Options.TrackFileWatches)
            {
                HashSet<Utf8String> seen = new(host.Comparer);
                foreach (var file in AllOpenFiles())
                {
                    if (CompilerPath.IsDynamic(file)) continue;
                    var directory = file;
                    while (true)
                    {
                        var parent = CompilerPath.DirectoryName(directory);
                        if (parent == directory || parent.IsEmpty || !seen.Add(parent)) break;
                        directory = parent;
                        var modules = CompilerPath.Combine(directory, "node_modules"u8);
                        if (fs.DirectoryExists(modules)) watchedNodeModules.Add(modules);
                    }
                }
            }
            var result = new ProjectWorkspaceSnapshot(host, id, previous?.Id ?? 0, fs, projects, openProjects, openFiles,
                inferredOptions, inferredMappers, created.Select(path => projects[path]).ToArray(), changes, request.FileSystem is not null, locale, customConfig, nearestConfigs,
                request.UserPreferences ?? previous?.UserPreferences ?? new(), configs) { WatchedNodeModules = watchedNodeModules.Order().ToArray() };
            transferred = true;
            return result;
        }

        private bool ContainsRequestedChild(ProjectReferences references, Utf8String root, HashSet<Utf8String> requested)
        {
            var seen = new HashSet<Utf8String>(host.Comparer) { root };
            var pending = new Stack<Utf8String>(references.References.GetValueOrDefault(root) ?? []);
            while (pending.TryPop(out var child))
            {
                cancellation.ThrowIfCancellationRequested();
                child = host.Path(child);
                if (!seen.Add(child)) continue;
                if (requested.Contains(child)) return true;
                foreach (var next in references.References.GetValueOrDefault(child) ?? []) pending.Push(next);
            }
            return false;
        }

        private IEnumerable<Utf8String> AllOpenFiles() => fs.Overlay.Overlays.Values.Select(document => document.FileName)
            .Concat(openFiles.Values.Select(file => file.FileName)).Distinct(host.Comparer).Order(Utf8StringComparer.Ordinal);
        private List<Utf8String> CollectInferredRoots() => AllOpenFiles().Where(file => ExistingConfiguredForFile(host.Path(file)) is null && IsSupported(file)).ToList();
        private bool IsSupported(Utf8String name) => CompilerPath.IsDynamic(name) || DocumentSnapshot.InferKind(name) != ScriptKind.Unknown
            || fs.Overlay.Overlays.ContainsKey(host.Path(name)) && CompilerPath.Extension(name).IsEmpty
            || inferredMappers.Any(mapper => mapper.Extensions.Any(extension => name.EndsWith(extension)));
        private ProjectSnapshot? ExistingConfiguredForFile(Utf8String path)
        {
            if (selectedProjects.TryGetValue(path, out var selected) && projects.TryGetValue(selected, out var current)
                && current.ContainsFile(path)) return current;
            var candidates = projects.Values.Where(project => project.Kind == ProjectKind.Configured && project.ContainsFile(path))
                .OrderBy(project => project.IsReferenceSource(path)).ThenBy(project => project.Id, Utf8StringComparer.Ordinal).ToArray();
            if (candidates.Count(project => !project.IsReferenceSource(path)) > 1
                && nearestConfigs.TryGetValue(path, out var nearest) && nearest is { } name
                && projects.TryGetValue(host.Path(name), out var preferred) && preferred.ContainsFile(path) && !preferred.IsReferenceSource(path)) return preferred;
            return candidates.FirstOrDefault();
        }

        private ParsedConfig SyntheticConfig(CreateProgramRequest creation) => Configure(new(default, CopyOptions(creation.CompilerOptions),
            creation.RootFiles.Select(host.FileName).ToArray(), creation.References.ToArray(), creation.ConfigDiagnostics.ToArray(), [])
        { ContentMappers = inferredMappers.ToArray() });
        private ParsedConfig InferredConfig(Utf8String[] roots) => Configure(new(default, CopyOptions(inferredOptions), roots, [], [], [])
        { ContentMappers = inferredMappers.ToArray() });

        private async ValueTask<ProjectSnapshot?> EnsureConfiguredAsync(Utf8String fileName)
        {
            Utf8String path = host.Path(fileName);
            if (!projects.TryGetValue(path, out var project))
            {
                if (ReadConfig(fileName) is not { } config) return null;
                projects.Add(path, new(path, ProjectKind.Configured, config));
            }
            await EnsureAsync(path).ConfigureAwait(false);
            return projects.GetValueOrDefault(path);
        }

        private async ValueTask<ProjectSnapshot?> FindConfiguredForFileAsync(Utf8String fileName, bool ensure)
        {
            if (CompilerPath.IsDynamic(fileName)) return null;
            Utf8String path = host.Path(fileName);
            Utf8String? configName;
            if (!nearestConfigs.TryGetValue(path, out configName))
            {
                configName = host.FindConfig(fs, CompilerPath.DirectoryName(fileName), customConfig);
                if (fs.Overlay.Overlays.ContainsKey(path)) nearestConfigs[path] = configName;
            }
            var visited = new HashSet<(Utf8String Path, bool Load)>();
            ProjectSnapshot? fallback = null;
            while (configName is { } name)
            {
                var pending = new Queue<(Utf8String Name, bool Load)>(); pending.Enqueue((name, true));
                while (pending.Count != 0)
                {
                    ProjectSnapshot? found = null;
                    int levelCount = pending.Count;
                    while (levelCount-- > 0 && pending.TryDequeue(out var entry))
                    {
                        Utf8String configPath = host.Path(entry.Name);
                        if (!visited.Add((configPath, entry.Load))) continue;
                        if (!entry.Load && !projects.ContainsKey(configPath)) continue;
                        if (ReadConfig(entry.Name) is not { } config) continue;
                        retained.Add(configPath);
                        bool canContain = config.FileNames.Length != 0 && (config.Options.Composite != true
                            || config.FileNames.Any(root => host.Comparer.Equals(host.Path(root), path)));
                        if (canContain)
                        {
                            if (!projects.TryGetValue(configPath, out var project)) projects.Add(configPath, project = new(configPath, ProjectKind.Configured, config));
                            if (ensure || project.Program is null) await EnsureAsync(configPath).ConfigureAwait(false);
                            project = projects.GetValueOrDefault(configPath);
                            if (project?.ContainsFile(path) == true)
                            {
                                if (!project.IsReferenceSource(path))
                                {
                                    found ??= project;
                                }
                                fallback ??= project;
                            }
                        }
                        foreach (var reference in config.References)
                            pending.Enqueue((host.ReferencePath(reference.Path), entry.Load && config.Options.DisableReferencedProjectLoad != true));
                        }
                    if (found is not null)
                    {
                        selectedProjects[path] = found.Id;
                        if (fs.Overlay.Overlays.ContainsKey(path)) CreateAncestorTree(found);
                        return found;
                    }
                }
                if (ReadConfig(name)?.Options.DisableSolutionSearching == true) break;
                configName = host.FindAncestorConfig(fs, name, customConfig);
            }
            if (fallback is not null) selectedProjects[path] = fallback.Id;
            return fallback;
        }

        private void CreateAncestorTree(ProjectSnapshot project)
        {
            var seen = new HashSet<Utf8String>(host.Comparer);
            while (seen.Add(project.Id))
            {
                if (project.ConfigLoaded && (project.Configuration.Options.Composite != true || project.Configuration.Options.DisableSolutionSearching == true)) return;
                if (host.FindAncestorConfig(fs, project.Configuration.FileName, customConfig) is not { } name) return;
                Utf8String path = host.Path(name);
                retained.Add(path);
                if (!projects.TryGetValue(path, out var ancestor))
                    projects.Add(path, ancestor = new(path, ProjectKind.Configured, new(name, new(), [], [], [], []), configLoaded: false));
                projects[path] = ancestor.WithPotentialReference(project.Id);
                project = projects[path];
            }
        }

        private async ValueTask EnsureAsync(Utf8String path)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!projects.TryGetValue(path, out var project)) throw new ArgumentException($"Project not found: {path}");
            if (!project.IsDirty) return;
            var config = project.Configuration;
            if (project.Kind == ProjectKind.Configured)
            {
                if (ReadConfig(config.FileName) is not { } updated) { await RemoveAsync(path).ConfigureAwait(false); return; }
                config = updated;
            }
            ContentMapperProject? mapper = null;
            var progress = host.Options.Progress;
            var displayName = progress is null ? default : project.DisplayName(host.Options.CurrentDirectory);
            if (progress is not null) await progress(Diagnostics.Messages.Project_0, displayName, false).ConfigureAwait(false);
            try
            {
                if (config.ContentMappers.Length > 0) mapper = await host.mapperHost.GetProjectAsync(config, cancellation).ConfigureAwait(false);
                var directory = project.Kind == ProjectKind.Configured ? CompilerPath.DirectoryName(config.FileName) : host.Options.CurrentDirectory;
                bool acquireTypes = ProjectSnapshot.GetTypeAcquisition(project.Kind, config).Enable == true;
                var programConfig = acquireTypes && project.TypingsFiles.Count != 0 ? config with { FileNames = [.. config.FileNames, .. project.TypingsFiles] } : config;
                using var observation = host.Options.TrackFileWatches ? fs.ObserveFiles() : null;
                var program = await CompilerProgram.CreateAsync(fs, directory, programConfig, project.Program,
                    useProjectReferenceSources: true, concurrency: host.Options.Concurrency,
                    defaultLibraryDirectory: host.Options.DefaultLibraryDirectory, mapperProject: mapper,
                    cancellation: cancellation, globalTypingsCache: acquireTypes ? host.Options.TypingsLocation : default).ConfigureAwait(false);
                var mapperFiles = config.ContentMappers.Where(mapper => !mapper.Package.IsEmpty && mapper.ContributionId.IsEmpty && !mapper.PackageDirectory.IsEmpty)
                    .Select(mapper => CompilerPath.Combine(mapper.PackageDirectory, "package.json"u8));
                if (mapper is not null && program.SourceFiles.Any(file => file.Mapping is not null))
                    mapperFiles = mapperFiles.Concat(await mapper.WatchedFilesAsync(cancellation).ConfigureAwait(false));
                var resource = new ProjectProgram(program, mapper, host.Options)
                {
                    ObservedFiles = observation is null ? [] : project.Program?.HasSameFileNames(program) == true
                        ? project.Resource!.ObservedFiles : observation.Files.Keys.Order().ToArray(),
                    MapperWatchedFiles = mapperFiles.Distinct().Order().ToArray(),
                };
                mapper = null;
                projects[path] = new(path, project.Kind, config, resource, id, false)
                {
                    Identity = project.Identity, Typings = project.Typings, PotentialProjectReferences = project.PotentialProjectReferences,
                    ProgramDiagnosticsChanged = !CanReuseProgramDiagnostics(project, config, program),
                    ProgramUpdateKind = project.Program?.HasSameFileNames(program) == true
                        ? ProjectProgramUpdateKind.SameFileNames : ProjectProgramUpdateKind.NewFiles
                };
                if (project.Resource is not null) await project.Resource.ReleaseAsync().ConfigureAwait(false);
            }
            finally
            {
                try { if (mapper is not null) await mapper.DisposeAsync().ConfigureAwait(false); }
                finally { if (progress is not null) await progress(Diagnostics.Messages.Project_0, displayName, true).ConfigureAwait(false); }
            }
        }

        private bool CanReuseProgramDiagnostics(ProjectSnapshot project, ParsedConfig config, CompilerProgram program)
        {
            if (project.Program is not { } old || changes.InvalidateAll || request.TypingsChanges.Count != 0
                || request.InferredOptions is not null || previous?.GetProject(project.Id)?.IsDirty == true && project.PendingChangedFile.IsEmpty) return false;
            // Configuration discovery rebuilds before source notifications are applied. A subsequent
            // single-file update observes that new graph, including newly included root files.
            var baseline = project.Kind == ProjectKind.Configured && !ReferenceEquals(config.SourceFile, project.Configuration.SourceFile) ? program : old;
            if (!baseline.HasSameFileNames(program)) return false;
            Utf8String changed = project.PendingChangedFile;
            foreach (var path in changes.Changed.Concat(changes.Created).Concat(changes.Deleted).Concat(opened).Distinct(host.Comparer))
            {
                if (baseline.GetFileByPath(path) is not null)
                {
                    if (changes.Deleted.Contains(path) || CompilerPath.BaseName(path) == "package.json"u8 || !changed.IsEmpty && changed != path) return false;
                    changed = path;
                }
                else if (baseline.PackageJsonLookupPaths.Contains(path, host.Comparer) || baseline.MissingFiles.Contains(path, host.Comparer)
                    || changes.Deleted.Contains(path) && baseline.SourceFiles.Any(file => CompilerPath.Contains(path, file.Syntax.FileName, fs.CaseSensitive))) return false;
            }
            return !changed.IsEmpty && baseline.CanReplaceFileForDiagnostics(program, changed);
        }

        private Utf8String SingleChangedFile(ProjectSnapshot project)
        {
            if (project.Program is null || project.IsDirty && project.PendingChangedFile.IsEmpty || reloadConfigs || changes.InvalidateAll) return default;
            var paths = changes.Changed.Concat(changes.Created).Concat(changes.Deleted).Concat(changes.Closed).Concat(opened).Distinct(host.Comparer).ToArray();
            if (paths.Length != 1 || changes.Deleted.Contains(paths[0]) || CompilerPath.BaseName(paths[0]) == "package.json"u8
                || project.Program.GetFileByPath(paths[0]) is null || !project.PendingChangedFile.IsEmpty && project.PendingChangedFile != paths[0]) return default;
            return paths[0];
        }

        private async ValueTask RemoveAsync(Utf8String path)
        {
            if (projects.Remove(path, out var project) && project.Resource is not null)
                await project.Resource.ReleaseAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (transferred) return;
            await ProjectProgram.ReleaseAllAsync(projects.Values).ConfigureAwait(false);
        }
    }
}

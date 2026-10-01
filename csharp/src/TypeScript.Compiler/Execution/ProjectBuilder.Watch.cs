using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Watching;

namespace TypeScript.Compiler.Execution;

public sealed partial class ProjectBuilder
{
    private static void ResetStatus(ProjectTask task) { task.StatusKnown = false; task.Pending = true; }

    private static void UpdateFromUpstream(ProjectTask task, BuildOptions options)
    {
        foreach (var upstream in task.Upstream)
        {
            if (upstream.Result?.Program is not { } program || options.StopBuildOnErrors && upstream.Status == ProjectBuildStatus.BuildErrors) continue;
            if (task.StatusKnown)
            {
                switch (task.Status)
                {
                    case ProjectBuildStatus.UpToDate when !program.HasChangedDeclaration:
                        task.Status = ProjectBuildStatus.UpToDateWithUpstreamTypes;
                        break;
                    case ProjectBuildStatus.UpToDate:
                    case ProjectBuildStatus.UpToDateWithUpstreamTypes:
                    case ProjectBuildStatus.UpToDateWithInputFileText:
                        if (program.HasChangedDeclaration) { task.Status = ProjectBuildStatus.InputFileNewer; task.Input = upstream.Path; }
                        break;
                    case ProjectBuildStatus.UpstreamErrors:
                        ResetStatus(task);
                        break;
                }
            }
            task.Pending = true;
        }
    }

    internal async ValueTask<bool> InvalidateAsync(IReadOnlyDictionary<Utf8String, WatchEventKind> changes,
        bool overflow, WatchManager watches, CancellationToken cancellation)
    {
        if (changes.Count == 0 && !overflow) return false;
        var normalized = new Dictionary<Utf8String, WatchEventKind>(comparer);
        foreach (var (path, kind) in changes) normalized[CompilerPath.Resolve(currentDirectory, path)] = kind;
        bool modified = overflow;
        var parser = new ConfigParser(fileSystem, currentDirectory);
        foreach (var task in projects.Values)
        {
            bool configChanged = overflow || normalized.ContainsKey(task.Path)
                || task.Config is { } config && config.ExtendedConfigFiles.Concat(config.ContentMappers
                    .Where(mapper => !mapper.PackageDirectory.IsEmpty).Select(mapper => CompilerPath.Combine(mapper.PackageDirectory, "package.json"u8)))
                    .Any(normalized.ContainsKey);
            if (configChanged)
            {
                task.ConfigDirty = true; task.Previous = null; ResetStatus(task); modified = true;
                continue;
            }
            if (task.Config is not { } resolved) continue;
            if (task.Mapper is not null && task.MapperPaths.Any(normalized.ContainsKey))
            {
                await task.Mapper.RefreshAsync(cancellation).ConfigureAwait(false);
                task.Previous = null; ResetStatus(task); modified = true;
            }
            if (resolved.FileNames.Any(normalized.ContainsKey) || StateFiles(task).Any(normalized.ContainsKey)
                || PackageFiles(task).Any(path => normalized.ContainsKey(path) || normalized.Any(change => change.Value == WatchEventKind.Delete
                    && CompilerPath.Contains(change.Key, path, fileSystem.CaseSensitive)))) { ResetStatus(task); modified = true; }
            var next = parser.ReloadFiles(resolved, cancellation);
            if (!resolved.FileNames.SequenceEqual(next.FileNames)) { task.Config = next; ResetStatus(task); modified = true; }
        }
        if (!modified && changes.Keys.Any(path => fileSystem.DirectoryExists(path) && watches.IsPathUnderWatch(path, fileSystem.CaseSensitive)))
        {
            foreach (var task in projects.Values) ResetStatus(task);
            modified = true;
        }
        return modified;
    }

    internal IReadOnlyDictionary<Utf8String, bool> GetDesiredWatches(WatchManager watches)
    {
        var desired = new DirectoryWatchSet(fileSystem.CaseSensitive);
        foreach (var task in buildOrder)
        {
            desired.Set(fileSystem.RealPath(CompilerPath.DirectoryName(task.Path)), false);
            if (task.Config is not { } config) continue;
            foreach (var path in config.ExtendedConfigFiles) desired.Set(CompilerPath.DirectoryName(fileSystem.RealPath(path)), false);
            foreach (var (path, recursive) in config.WildcardDirectories) desired.Set(fileSystem.RealPath(path), recursive);
            foreach (var path in config.FileNames) AddDirectory(CompilerPath.DirectoryName(CompilerPath.Resolve(currentDirectory, path)));
            foreach (var mapper in config.ContentMappers.Where(mapper => !mapper.PackageDirectory.IsEmpty)) AddDirectory(mapper.PackageDirectory);
            foreach (var path in task.MapperPaths.Concat(StateFiles(task))) AddDirectory(CompilerPath.DirectoryName(fileSystem.RealPath(path)));
            foreach (var path in PackageFiles(task))
            {
                var directory = CompilerPath.DirectoryName(path);
                var directories = new List<Utf8String> { directory };
                bool foundModules = false;
                for (var current = directory; ;)
                {
                    var parent = CompilerPath.DirectoryName(current);
                    if (parent.IsEmpty || parent == current) break;
                    directories.Add(parent);
                    if (CompilerPath.BaseName(parent) == "node_modules"u8)
                    {
                        foundModules = true;
                        var grandparent = CompilerPath.DirectoryName(parent);
                        if (!grandparent.IsEmpty && grandparent != parent) directories.Add(grandparent);
                        break;
                    }
                    current = parent;
                }
                if (foundModules) foreach (var item in directories) AddDirectory(item);
                else AddDirectory(directory);
            }
        }
        return watches.ResolveDesiredDirectories(desired.Directories);

        void AddDirectory(Utf8String directory)
        {
            if (!desired.Covers(directory) && WatchPaths.CanWatchDirectory(directory)) desired.Set(directory, false);
        }
    }

    private IEnumerable<Utf8String> StateFiles(ProjectTask task)
    {
        var directory = CompilerPath.DirectoryName(task.StatePath);
        foreach (var path in task.State?.FileNames ?? [])
            yield return !CompilerPath.IsAbsolute(path) && !path.StartsWith((byte)'.')
                ? CompilerPath.Combine(libraryDirectory, path) : CompilerPath.Resolve(directory, path);
    }

    private IEnumerable<Utf8String> PackageFiles(ProjectTask task)
    {
        var directory = CompilerPath.DirectoryName(task.StatePath);
        return (task.State?.PackageJsons ?? []).Concat(task.State?.MissingPackageJsons ?? [])
            .Select(path => CompilerPath.Resolve(directory, path))
            .Concat(task.Previous?.Program.PackageJsonLookupPaths ?? []).Distinct(comparer);
    }
}

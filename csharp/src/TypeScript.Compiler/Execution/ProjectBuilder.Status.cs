using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Incremental;

namespace TypeScript.Compiler.Execution;

public sealed partial class ProjectBuilder
{
    private ProjectBuildStatus GetStatus(ProjectTask task, BuildOptions buildOptions)
    {
        if (WatchMode && task.StatusKnown) return task.Status;
        task.NewestInputTime = task.OldestOutputTime = DateTime.MinValue; task.HasTimes = false;
        ProjectBuildStatus Status(ProjectBuildStatus kind, Utf8String input = default, Utf8String output = default)
        { task.Input = input; task.Output = output; return kind; }
        if (task.Config is not { } config) return Status(ProjectBuildStatus.ConfigFileNotFound);
        if (config.FileNames.Length == 0 && (config.HasReferences || config.References.Length != 0)) return Status(ProjectBuildStatus.Solution);
        foreach (var upstream in task.Upstream)
            if (buildOptions.StopBuildOnErrors && upstream.Status is ProjectBuildStatus.BuildErrors or ProjectBuildStatus.UpstreamErrors or ProjectBuildStatus.ConfigFileNotFound)
            {
                task.UpstreamSkipped = upstream.Status == ProjectBuildStatus.UpstreamErrors;
                return Status(ProjectBuildStatus.UpstreamErrors, task.ReferencePaths[upstream]);
            }
        if (buildOptions.Force) return Status(ProjectBuildStatus.ForceBuild);
        var infoPath = task.StatePath;
        if (!WatchMode || !comparer.Equals(task.CachedStatePath, infoPath))
        {
            var bytes = fileSystem.ReadFile(infoPath);
            task.State = bytes is null ? null : BuildInfo.TryRead(bytes);
            task.CachedStatePath = infoPath; task.StateTime = task.State is null ? DateTime.MinValue : Time(infoPath);
        }
        var info = task.State;
        if (info is null) return Status(ProjectBuildStatus.OutputMissing, output: infoPath);
        task.OldestOutputTime = task.StateTime;
        task.Version = info.Version;
        if (!info.IsValidVersion) return Status(ProjectBuildStatus.VersionChanged);
        if (!(info.ContentMapperIdentities ?? []).SequenceEqual(task.MapperIdentities)) return Status(ProjectBuildStatus.OptionsChanged, output: infoPath);
        var options = config.Options;
        if (info.Errors || options.NoCheck != true && (info.SemanticErrors || info.CheckPending)) return Status(ProjectBuildStatus.PendingErrors, output: infoPath);
        var infoDirectory = CompilerPath.DirectoryName(infoPath);
        bool incremental = IncrementalOptions.IsIncremental(options);
        if (incremental)
        {
            if (!info.IsIncremental) return Status(ProjectBuildStatus.OptionsChanged, output: infoPath);
            if ((options.Declaration == true || options.Composite == true) && info.EmitDiagnosticsPerFile is not null
                || options.NoCheck != true && (info.ChangeFileSet is not null || info.SemanticDiagnosticsPerFile is not null))
                return Status(ProjectBuildStatus.PendingErrors, output: infoPath);
            if (options.NoEmit != true && (info.ChangeFileSet is not null || info.AffectedFilesPendingEmit is not null))
                return Status(ProjectBuildStatus.PendingEmit, output: infoPath);
            if (options.NoEmit != true || options.Declaration == true || options.Composite == true)
            {
                var pending = BuildInfo.GetPendingEmitKind(BuildInfo.GetEmitKind(options), BuildInfo.GetEmitKind(IncrementalOptions.FromBuildInfo(info, infoDirectory)));
                if (options.NoEmit == true) pending &= FileEmitKind.DeclarationErrors;
                if (pending != 0) return Status(ProjectBuildStatus.OptionsChanged, output: infoPath);
            }
        }
        Utf8String File(int id) => id > 0 && id <= (info.FileNames?.Length ?? 0)
            ? CompilerPath.Resolve(infoDirectory, info.FileNames![id - 1]) : default;
        var roots = new Dictionary<Utf8String, (Utf8String Resolved, BuildInfoFileInfo Info)>(comparer);
        var redirects = (info.ResolvedRoot ?? []).ToDictionary(root => root.Resolved, root => root.Root);
        foreach (var root in info.Root ?? [])
        {
            if (root.Name.Length != 0) roots[CompilerPath.Resolve(infoDirectory, root.Name)] = default;
            else
                for (int id = root.Start; id <= (root.End == 0 ? root.Start : root.End); id++)
                    roots[File(redirects.GetValueOrDefault(id, id))] = (File(id), id <= (info.FileInfos?.Length ?? 0) ? info.FileInfos![id - 1] : default);
        }
        bool inputTextUnchanged = false;
        var seenRoots = new HashSet<Utf8String>(comparer);
        Utf8String newestInput = default, addedRoot = default, oldestOutput = infoPath;
        foreach (var input in config.FileNames)
        {
            var time = Time(input);
            if (time == DateTime.MinValue) return Status(ProjectBuildStatus.InputFileMissing, input);
            if (addedRoot.IsEmpty && !roots.ContainsKey(input)) addedRoot = input;
            if (time > task.OldestOutputTime)
            {
                var root = roots.GetValueOrDefault(input);
                if (!info.IsIncremental || root.Info.Version.Length == 0 || fileSystem.ReadFile(root.Resolved) is not { } content
                    || root.Info.Version != BuildInfo.ComputeHash(new(content), hashWithText)) return Status(ProjectBuildStatus.InputFileNewer, input, infoPath);
                inputTextUnchanged = true;
            }
            if (time > task.NewestInputTime) { task.NewestInputTime = time; newestInput = input; }
            seenRoots.Add(input);
        }
        foreach (var root in roots.Keys)
            if (!seenRoots.Contains(root)) return Status(ProjectBuildStatus.RootsChanged, root, infoPath);
        if (!addedRoot.IsEmpty) return Status(ProjectBuildStatus.InputFileNewer, addedRoot, infoPath);
        if (info.IsIncremental)
        {
            var resolvedRoots = roots.Values.Select(root => root.Resolved).ToHashSet(comparer);
            for (int index = 0; index < (info.FileInfos?.Length ?? 0); index++)
            {
                var name = info.FileNames![index];
                if (!CompilerPath.IsAbsolute(name) && !name.StartsWith((byte)'.')) continue;
                var input = CompilerPath.Resolve(infoDirectory, name);
                if (seenRoots.Contains(input) || resolvedRoots.Contains(input)) continue;
                if (IsSupplemental(input, roots.Keys) && !fileSystem.FileExists(input)) continue;
                var time = Time(input);
                if (time == DateTime.MinValue) return Status(ProjectBuildStatus.InputFileMissing, input);
                if (time > task.OldestOutputTime)
                {
                    var version = info.FileInfos![index].Version;
                    if (version.Length == 0 || fileSystem.ReadFile(input) is not { } content || version != BuildInfo.ComputeHash(new(content), hashWithText))
                        return Status(ProjectBuildStatus.InputFileNewer, input, infoPath);
                    inputTextUnchanged = true;
                }
            }
        }
        if (!incremental)
            foreach (var output in ConfigurationOutputs.GetFiles(config, currentDirectory, fileSystem.CaseSensitive))
            {
                var time = Time(output);
                if (time == DateTime.MinValue) return Status(ProjectBuildStatus.OutputMissing, output: output);
                if (time < task.NewestInputTime) return Status(ProjectBuildStatus.InputFileNewer, newestInput, output);
                if (time < task.OldestOutputTime) { task.OldestOutputTime = time; oldestOutput = output; }
            }
        bool upstreamTypesUnchanged = false;
        foreach (var upstream in task.Upstream)
        {
            if (upstream.Status == ProjectBuildStatus.Solution) continue;
            if (upstream.HasTimes && upstream.NewestInputTime != DateTime.MinValue && upstream.NewestInputTime < task.OldestOutputTime) continue;
            if (comparer.Equals(task.StatePath, upstream.StatePath)) return Status(ProjectBuildStatus.InputFileNewer, task.ReferencePaths[upstream], oldestOutput);
            var declarationTime = upstream.State?.LatestChangedDtsFile is { IsEmpty: false } declaration
                ? Time(CompilerPath.Resolve(CompilerPath.DirectoryName(upstream.StatePath), declaration)) : DateTime.MinValue;
            if (declarationTime != DateTime.MinValue && declarationTime < task.OldestOutputTime) { upstreamTypesUnchanged = true; continue; }
            return Status(ProjectBuildStatus.InputFileNewer, task.ReferencePaths[upstream], oldestOutput);
        }
        foreach (var file in new[] { task.Path }.Concat(config.ExtendedConfigFiles))
            if (Time(file) > task.OldestOutputTime) return Status(ProjectBuildStatus.InputFileNewer, file, oldestOutput);
        foreach (var name in info.PackageJsons ?? [])
        {
            var path = CompilerPath.Resolve(infoDirectory, name);
            var time = Time(path);
            if (time == DateTime.MinValue) return Status(ProjectBuildStatus.InputFileMissing, path);
            if (time > task.OldestOutputTime) return Status(ProjectBuildStatus.InputFileNewer, path, oldestOutput);
        }
        foreach (var name in info.MissingPackageJsons ?? [])
        {
            var path = CompilerPath.Resolve(infoDirectory, name);
            if (Time(path) != DateTime.MinValue) return Status(ProjectBuildStatus.InputFileNewer, path, oldestOutput);
        }
        task.HasTimes = true;
        return Status(upstreamTypesUnchanged ? ProjectBuildStatus.UpToDateWithUpstreamTypes
            : inputTextUnchanged ? ProjectBuildStatus.UpToDateWithInputFileText : ProjectBuildStatus.UpToDate, newestInput, oldestOutput);
    }

    private static bool IsSupplemental(Utf8String input, IEnumerable<Utf8String> roots)
    {
        foreach (var root in roots)
        {
            var prefix = root + "."u8;
            if (!input.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var suffix = input[prefix.Length..];
            int dot = suffix.IndexOf((byte)'.');
            if (dot <= 0 || !int.TryParse(suffix[..dot].Span, out _)) continue;
            var extension = suffix[dot..];
            if (extension == ".ts"u8 || extension == ".tsx"u8 || extension == ".mts"u8 || extension == ".cts"u8
                || extension == ".js"u8 || extension == ".jsx"u8 || extension == ".mjs"u8 || extension == ".cjs"u8
                || extension == ".d.ts"u8 || extension == ".d.mts"u8 || extension == ".d.cts"u8) return true;
        }
        return false;
    }
}

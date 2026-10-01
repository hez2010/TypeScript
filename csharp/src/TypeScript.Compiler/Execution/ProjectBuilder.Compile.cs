using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Incremental;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;

namespace TypeScript.Compiler.Execution;

public sealed partial class ProjectBuilder
{
    private async ValueTask<ProjectBuildResult> BuildProjectAsync(ProjectTask task, BuildOptions options, CancellationToken cancellation)
    {
        var errors = new List<Diagnostic>();
        var messages = new List<Diagnostic>();
        var deleted = new List<Utf8String>();
        var timestamps = new List<Utf8String>();
        IReadOnlyList<Utf8String> emitted = [];
        IncrementalProgram? incremental = null;
        CompilerExitStatus exit = CompilerExitStatus.Success;
        int trailingMessages = 0;
        if (options.Clean)
        {
            if (task.Config is not { } config)
            {
                errors.Add(new(Messages.File_0_not_found, 0, 0, [task.Path]));
                exit = CompilerExitStatus.DiagnosticsWithOutputsSkipped;
                task.Status = ProjectBuildStatus.ConfigFileNotFound;
            }
            else
            {
                task.Status = ProjectBuildStatus.UpToDate;
                var inputs = config.FileNames.ToHashSet(comparer);
                foreach (var output in ConfigurationOutputs.GetFiles(config, currentDirectory, fileSystem.CaseSensitive).Append(task.StatePath))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (output.IsEmpty || inputs.Contains(output) || !fileSystem.FileExists(output)) continue;
                    if (options.Dry) { deleted.Add(output); continue; }
                    try { fileSystem.Remove(output); deleted.Add(output); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { errors.Add(new(Messages.Failed_to_delete_file_0, 0, 0, [output])); }
                }
                if (errors.Count != 0) exit = CompilerExitStatus.DiagnosticsWithOutputsSkipped;
            }
            return Result();
        }
        if (task.Config is { ContentMappers.Length: > 0, Options.RunExternalCode: true } mappedConfig)
        {
            try
            {
                task.Mapper ??= await mapperHost.GetProjectAsync(mappedConfig, cancellation).ConfigureAwait(false);
                task.MapperIdentities = (await task.Mapper.IdentitiesAsync(cancellation).ConfigureAwait(false)).Order(Utf8StringComparer.Ordinal).ToArray();
                task.MapperPaths = (await task.Mapper.WatchedFilesAsync(cancellation).ConfigureAwait(false)).ToArray();
            }
            catch (MapperException error)
            {
                errors.Add(MapperDiagnostic(error, mappedConfig));
                task.Status = ProjectBuildStatus.BuildErrors;
                exit = CompilerExitStatus.DiagnosticsWithOutputsSkipped;
                return Result();
            }
        }
        task.Status = GetStatus(task, options);
        var originalStatus = task.Status;
        if (options.Verbose && StatusMessage(task) is { } statusMessage) messages.Add(statusMessage);
        switch (task.Status)
        {
            case ProjectBuildStatus.ConfigFileNotFound:
                errors.Add(new(Messages.File_0_not_found, 0, 0, [task.Path]));
                exit = CompilerExitStatus.DiagnosticsWithOutputsSkipped;
                return Result();
            case ProjectBuildStatus.UpstreamErrors:
                if (options.Verbose) messages.Add(new(task.UpstreamSkipped
                    ? Messages.Skipping_build_of_project_0_because_its_dependency_1_was_not_built
                    : Messages.Skipping_build_of_project_0_because_its_dependency_1_has_errors, 0, 0, [Relative(task.Path), Relative(task.Input)]));
                return Result();
            case ProjectBuildStatus.Solution:
                AddConfigErrors();
                return Result();
            case ProjectBuildStatus.UpToDate:
                if (options.Dry) messages.Add(new(Messages.Project_0_is_up_to_date, 0, 0, [task.Path]));
                AddConfigErrors();
                return Result();
            case ProjectBuildStatus.UpToDateWithInputFileText:
            case ProjectBuildStatus.UpToDateWithUpstreamTypes:
                if (options.Dry) messages.Add(new(Messages.A_non_dry_build_would_update_timestamps_for_output_of_project_0, 0, 0, [task.Path]));
                else UpdateTimestamps([], Messages.Updating_output_timestamps_of_project_0);
                task.Status = ProjectBuildStatus.UpToDate;
                AddConfigErrors();
                return Result(originalStatus);
        }
        if (options.Dry)
        {
            messages.Add(new(Messages.A_non_dry_build_would_build_project_0, 0, 0, [task.Path]));
            task.Status = ProjectBuildStatus.UpToDate;
            AddConfigErrors();
            return Result(originalStatus);
        }
        if (options.Verbose) messages.Add(new(Messages.Building_project_0, 0, 0, [Relative(task.Path)]));
        var program = await CompilerProgram.CreateAsync(fileSystem, currentDirectory, task.Config!,
            previous: WatchMode && !options.Force ? task.Previous?.Program : null,
            defaultLibraryDirectory: libraryDirectory, mapperProject: task.Mapper, cancellation: cancellation).ConfigureAwait(false);
        incremental = await IncrementalProgram.CreateAsync(program, fileSystem, previous: WatchMode && !options.Force ? task.Previous : null, mapperIdentities: task.MapperIdentities,
            defaultLibraryDirectory: libraryDirectory, hashWithText: hashWithText, reuseBuildInfo: !options.Force, cancellation: cancellation).ConfigureAwait(false);
        errors.AddRange(await incremental.GetDiagnosticsAsync(cancellation).ConfigureAwait(false));
        var emit = await incremental.EmitAsync(writeBuildInfo: (path, info, token) =>
        {
            token.ThrowIfCancellationRequested();
            fileSystem.WriteFile(path, info.ToJson());
            task.State = info;
            task.CachedStatePath = path; task.StateTime = now();
            return ValueTask.CompletedTask;
        }, cancellation: cancellation).ConfigureAwait(false);
        emitted = emit.EmittedFiles;
        OnEmittedFiles?.Invoke(emit);
        errors.AddRange(emit.Diagnostics);
        errors = DiagnosticCollection.SortAndDeduplicate(errors).ToList();
        exit = errors.Count == 0 ? CompilerExitStatus.Success
            : emit.EmitSkipped ? CompilerExitStatus.DiagnosticsWithOutputsSkipped : CompilerExitStatus.DiagnosticsWithOutputsGenerated;
        if ((task.Config!.Options.NoEmitOnError != true || errors.Count == 0)
            && (emitted.Count != 0 || originalStatus != ProjectBuildStatus.PendingErrors))
        {
            int before = messages.Count;
            UpdateTimestamps(emitted, Messages.Updating_unchanged_output_timestamps_of_project_0);
            trailingMessages = messages.Count - before;
        }
        task.Status = errors.Count == 0 ? ProjectBuildStatus.UpToDate : ProjectBuildStatus.BuildErrors;
        if (errors.Count == 0) task.Output = emitted.Count != 0 ? emitted[0]
            : ConfigurationOutputs.GetFiles(task.Config, currentDirectory, fileSystem.CaseSensitive).FirstOrDefault();
        if (WatchMode) task.Previous = incremental;
        return Result(originalStatus);

        void AddConfigErrors()
        {
            if (task.Config is { } config) errors.AddRange(config.Diagnostics);
            if (errors.Count != 0) exit = CompilerExitStatus.DiagnosticsWithOutputsSkipped;
        }
        ProjectBuildResult Result(ProjectBuildStatus? status = null) => new(task.Path, status ?? task.Status, exit,
            errors, messages, emitted, deleted, timestamps, incremental) { TrailingMessageCount = trailingMessages };
        void UpdateTimestamps(IReadOnlyList<Utf8String> emittedFiles, DiagnosticMessage message)
        {
            var written = emittedFiles.ToHashSet(comparer);
            var time = now();
            bool reported = false;
            void Touch(Utf8String path)
            {
                if (path.IsEmpty || written.Contains(path)) return;
                if (options.Verbose && !reported) { messages.Add(new(message, 0, 0, [Relative(task.Path)])); reported = true; }
                try
                {
                    fileSystem.SetTimes(path, time, time);
                    timestamps.Add(path);
                    if (comparer.Equals(path, task.StatePath)) task.OldestOutputTime = task.StateTime = time;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            if (task.Config!.Options.NoEmit != true && !IncrementalOptions.IsIncremental(task.Config.Options))
                foreach (var output in ConfigurationOutputs.GetFiles(task.Config, currentDirectory, fileSystem.CaseSensitive)) Touch(output);
            Touch(task.StatePath);
        }
    }

    internal static Diagnostic MapperDiagnostic(MapperException error, ParsedConfig config) => error.Stage == MapperFailure.Initialize
        ? new(Messages.The_content_mapper_0_could_not_be_initialized, 0, 0, [config.ContentMappers.FirstOrDefault()?.Name ?? default])
        {
            MessageChain = [new(Messages.The_content_mapper_process_could_not_be_started_or_initialized, 0, 0, [])]
        }
        : new(Messages.The_content_mapper_process_failed_while_handling_the_project_request, 0, 0, []);

    private Diagnostic? StatusMessage(ProjectTask task)
    {
        Diagnostic Message(DiagnosticMessage message, params Utf8String[] args) => new(message, 0, 0, args);
        var project = Relative(task.Path);
        return task.Status switch
        {
            ProjectBuildStatus.ConfigFileNotFound => Message(Messages.Project_0_is_out_of_date_because_config_file_does_not_exist, project),
            ProjectBuildStatus.UpstreamErrors => Message(task.UpstreamSkipped
                ? Messages.Project_0_can_t_be_built_because_its_dependency_1_was_not_built : Messages.Project_0_can_t_be_built_because_its_dependency_1_has_errors,
                project, Relative(task.Input)),
            ProjectBuildStatus.BuildErrors => Message(Messages.Project_0_is_out_of_date_because_it_has_errors, project),
            ProjectBuildStatus.UpToDate when task.HasTimes => Message(Messages.Project_0_is_up_to_date_because_newest_input_1_is_older_than_output_2,
                project, Relative(task.Input), Relative(task.Output)),
            ProjectBuildStatus.UpToDateWithUpstreamTypes => Message(Messages.Project_0_is_up_to_date_with_d_ts_files_from_its_dependencies, project),
            ProjectBuildStatus.UpToDateWithInputFileText => Message(Messages.Project_0_is_up_to_date_but_needs_to_update_timestamps_of_output_files_that_are_older_than_input_files, project),
            ProjectBuildStatus.InputFileMissing => Message(Messages.Project_0_is_out_of_date_because_input_1_does_not_exist, project, Relative(task.Input)),
            ProjectBuildStatus.OutputMissing => Message(Messages.Project_0_is_out_of_date_because_output_file_1_does_not_exist, project, Relative(task.Output)),
            ProjectBuildStatus.InputFileNewer => Message(Messages.Project_0_is_out_of_date_because_output_1_is_older_than_input_2, project, Relative(task.Output), Relative(task.Input)),
            ProjectBuildStatus.PendingEmit => Message(Messages.Project_0_is_out_of_date_because_buildinfo_file_1_indicates_that_some_of_the_changes_were_not_emitted, project, Relative(task.Output)),
            ProjectBuildStatus.PendingErrors => Message(Messages.Project_0_is_out_of_date_because_buildinfo_file_1_indicates_that_program_needs_to_report_errors, project, Relative(task.Output)),
            ProjectBuildStatus.OptionsChanged => Message(Messages.Project_0_is_out_of_date_because_buildinfo_file_1_indicates_there_is_change_in_compilerOptions, project, Relative(task.Output)),
            ProjectBuildStatus.RootsChanged => Message(Messages.Project_0_is_out_of_date_because_buildinfo_file_1_indicates_that_file_2_was_root_file_of_compilation_but_not_any_more,
                project, Relative(task.Output), Relative(task.Input)),
            ProjectBuildStatus.VersionChanged => Message(Messages.Project_0_is_out_of_date_because_output_for_it_was_generated_with_version_1_that_differs_with_current_version_2,
                project, task.Version, BuildInfo.CompilerVersion),
            ProjectBuildStatus.ForceBuild => Message(Messages.Project_0_is_being_forcibly_rebuilt, project),
            _ => null
        };
    }
}

using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Incremental;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Watching;

namespace TypeScript.Compiler.Execution;

/// <summary>Executes compiler arguments against a host and owns any resulting watch session.</summary>
public sealed partial class CompilerCommand : IAsyncDisposable
{
    private readonly IFileSystem fileSystem;
    private readonly Utf8String currentDirectory, libraryDirectory;
    private readonly TextWriter output;
    private readonly Func<DateTime> now;
    private readonly Func<string, string?> environment;
    private readonly bool outputIsTerminal, hashWithText;
    private readonly IWatchBackend? watchBackend;
    private readonly ContentMapperHost mapperHost;
    private readonly bool ownsMapperHost;
    private CompilerOptions options = new();
    private ProjectWatchSession? projectWatch;
    private BuildWatchSession? buildWatch;
    private bool disposed;
    internal Action<EmitResult>? OnEmittedFiles { get; set; }
    public WatchManager? Watches => projectWatch?.Watches ?? buildWatch?.Watches;
    public IncrementalProgram? Program { get; private set; }

    public CompilerCommand(IFileSystem fileSystem, Utf8String currentDirectory, TextWriter output,
        Utf8String defaultLibraryDirectory = default, bool outputIsTerminal = false, Func<string, string?>? environment = null,
        IWatchBackend? watchBackend = null, ContentMapperHost? mapperHost = null, Func<DateTime>? now = null, bool hashWithText = false)
    {
        this.fileSystem = fileSystem; this.currentDirectory = CompilerPath.Normalize(currentDirectory); this.output = output;
        libraryDirectory = defaultLibraryDirectory.IsEmpty && fileSystem is LibraryFileSystem libraries ? libraries.LibraryDirectory : defaultLibraryDirectory;
        this.outputIsTerminal = outputIsTerminal; this.environment = environment ?? Environment.GetEnvironmentVariable;
        this.watchBackend = watchBackend; this.mapperHost = mapperHost ?? new(); ownsMapperHost = mapperHost is null;
        this.now = now ?? (() => DateTime.Now); this.hashWithText = hashWithText;
    }

    private bool Pretty(CompilerOptions? value = null)
    {
        if ((value ?? options).Pretty is { } pretty) return pretty;
        if (environment("FORCE_COLOR") is { } force) return force is "" or "1" or "2" or "3" or "true";
        return string.IsNullOrEmpty(environment("NO_COLOR")) && environment("TERM") != "dumb" && outputIsTerminal;
    }

    public async ValueTask<CompilerExitStatus> ExecuteAsync(IReadOnlyList<Utf8String> arguments, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Watches is not null) throw new InvalidOperationException("This command already owns a watch session.");
        bool build = arguments.Count != 0 && arguments[0].ToLowerInvariant().ToString() is "-b" or "--b" or "-build" or "--build";
        var command = new CommandLineParser(fileSystem, currentDirectory).Parse(arguments, build);
        options = command.Options;
        if (command.Diagnostics.Length != 0) return ReportErrors(command.Diagnostics, CompilerExitStatus.DiagnosticsWithOutputsSkipped);
        await mapperHost.SetLocaleAsync(options.Locale ?? default, cancellation).ConfigureAwait(false);
        if (build)
        {
            if (options.Help == true) { PrintVersion(); PrintHelp(true); return CompilerExitStatus.Success; }
            var buildOptions = BuildOptions.FromCommandLine(options);
            if (options.Watch == true)
            {
                buildWatch = new(fileSystem, currentDirectory, command.FileNames, options, buildOptions, libraryDirectory,
                    watchBackend, mapperHost, output, now, hashWithText);
                buildWatch.Builder.OnEmittedFiles = OnEmittedFiles;
                var cycle = await buildWatch.StartAsync(cancellation).ConfigureAwait(false);
                Report(cycle);
                return cycle.Build?.ExitStatus ?? CompilerExitStatus.Success;
            }
            await using var builder = new ProjectBuilder(fileSystem, currentDirectory, options, libraryDirectory, mapperHost, now, hashWithText)
            { OnEmittedFiles = OnEmittedFiles };
            var result = await builder.BuildAsync(command.FileNames, buildOptions, cancellation).ConfigureAwait(false);
            Report(result, buildOptions.Clean);
            if (Pretty()) DiagnosticReporter.WriteSummary(output, result.Diagnostics, fileSystem, currentDirectory, locale: options.Locale);
            return result.ExitStatus;
        }
        if (options.Init == true) { WriteConfig(new CommandLineParser(fileSystem, currentDirectory).Parse(arguments, resolvePaths: false).Options); return CompilerExitStatus.Success; }
        if (options.Version == true) { PrintVersion(); return CompilerExitStatus.Success; }
        if (options.Help == true || options.All == true) { PrintHelp(false); return CompilerExitStatus.Success; }
        if (options.Watch == true && options.ListFilesOnly == true)
            return ReportError(Messages.Options_0_and_1_cannot_be_combined, "watch"u8, "listFilesOnly"u8);
        var parser = new ConfigParser(fileSystem, currentDirectory);
        Utf8String? configPath = null;
        if (options.Project is { IsEmpty: false } project)
        {
            if (command.FileNames.Length != 0) return ReportError(Messages.Option_project_cannot_be_mixed_with_source_files_on_a_command_line);
            project = CompilerPath.Normalize(project);
            if (fileSystem.DirectoryExists(project))
            {
                configPath = CompilerPath.Combine(project, "tsconfig.json"u8);
                if (!fileSystem.FileExists(configPath.Value)) return ReportError(Messages.Cannot_find_a_tsconfig_json_file_at_the_current_directory_Colon_0, configPath.Value);
            }
            else
            {
                configPath = project;
                if (!fileSystem.FileExists(project)) return ReportError(Messages.The_specified_path_does_not_exist_Colon_0, project);
            }
        }
        else if (options.IgnoreConfig != true || command.FileNames.Length == 0)
        {
            configPath = parser.FindConfig(currentDirectory);
            if (command.FileNames.Length != 0 && configPath is not null)
                return ReportError(Messages.X_tsconfig_json_is_present_but_will_not_be_loaded_if_files_are_specified_on_commandline_Use_ignoreConfig_to_skip_this_error);
            if (command.FileNames.Length == 0 && configPath is null)
            {
                if (options.ShowConfig == true) return ReportError(Messages.Cannot_find_a_tsconfig_json_file_at_the_current_directory_Colon_0, currentDirectory);
                PrintVersion(); PrintHelp(false); return CompilerExitStatus.DiagnosticsWithOutputsSkipped;
            }
        }
        var config = configPath is { } path ? parser.Parse(path, options, cancellation)
            : new ParsedConfig(default, options, command.FileNames, [], [], []);
        if (configPath is not null && config.SourceFile is null) return ReportErrors(config.Diagnostics, CompilerExitStatus.DiagnosticsWithOutputsGenerated);
        if (options.ShowConfig == true) { ShowConfig(config); return CompilerExitStatus.Success; }
        if (config.Options.Watch == true)
        {
            projectWatch = new(fileSystem, currentDirectory, config, options, libraryDirectory, watchBackend, mapperHost, output, hashWithText);
            Report(await projectWatch.StartAsync(cancellation).ConfigureAwait(false));
            return CompilerExitStatus.Success;
        }
        await using var mapper = config.ContentMappers.Length != 0 && config.Options.RunExternalCode == true
            ? await mapperHost.GetProjectAsync(config, cancellation).ConfigureAwait(false) : null;
        var graph = await CompilerProgram.CreateAsync(fileSystem, currentDirectory, config, defaultLibraryDirectory: libraryDirectory,
            mapperProject: mapper, cancellation: cancellation).ConfigureAwait(false);
        var identities = mapper is null ? [] : await mapper.IdentitiesAsync(cancellation).ConfigureAwait(false);
        Program = await IncrementalProgram.CreateAsync(graph, fileSystem, mapperIdentities: identities, defaultLibraryDirectory: libraryDirectory,
            hashWithText: hashWithText, cancellation: cancellation).ConfigureAwait(false);
        var diagnostics = await Program.GetDiagnosticsAsync(cancellation).ConfigureAwait(false);
        var emit = config.Options.ListFilesOnly == true ? new EmitResult(true, [], [], []) : await Program.EmitAsync(cancellation: cancellation).ConfigureAwait(false);
        OnEmittedFiles?.Invoke(emit);
        diagnostics = DiagnosticCollection.SortAndDeduplicate(diagnostics.Concat(emit.Diagnostics));
        WriteResolutionTrace(graph);
        foreach (var diagnostic in diagnostics) WriteDiagnostic(diagnostic, graph);
        ListFiles(graph, emit.EmittedFiles);
        if (Pretty(config.Options)) DiagnosticReporter.WriteSummary(output, diagnostics, fileSystem, currentDirectory, graph, options.Locale);
        return diagnostics.Count == 0 ? CompilerExitStatus.Success : emit.EmitSkipped
            ? CompilerExitStatus.DiagnosticsWithOutputsSkipped : CompilerExitStatus.DiagnosticsWithOutputsGenerated;
    }

    public async ValueTask CycleAsync(CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (projectWatch is not null) Report(await projectWatch.CycleAsync(cancellation).ConfigureAwait(false));
        else if (buildWatch is not null) Report(await buildWatch.CycleAsync(cancellation).ConfigureAwait(false));
    }

    public async Task RunWatchAsync(CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (projectWatch is not null) await projectWatch.RunAsync((cycle, _) => { Report(cycle); return ValueTask.CompletedTask; }, cancellation).ConfigureAwait(false);
        else if (buildWatch is not null) await buildWatch.RunAsync((cycle, _) => { Report(cycle); return ValueTask.CompletedTask; }, cancellation).ConfigureAwait(false);
    }

    private void Report(WatchCycleResult result)
    {
        Program = result.Program;
        var configOptions = projectWatch!.Configuration.Options;
        if (result.Starting is { } starting) WatchStatus(starting, configOptions);
        if (result.Rebuilt && result.Program is { } tracedProgram) WriteResolutionTrace(tracedProgram.Program);
        foreach (var diagnostic in result.Diagnostics) WriteDiagnostic(diagnostic, result.Program?.Program);
        if (result.Rebuilt && result.Program is { } program) ListFiles(program.Program, result.EmittedFiles);
        if (result.Rebuilt && Pretty(configOptions)) DiagnosticReporter.WriteSummary(output, result.Diagnostics, fileSystem, currentDirectory, result.Program?.Program, options.Locale);
        if (result.Finished is { } finished) DiagnosticReporter.WriteStatus(output, finished, now(), options.Locale, Pretty(configOptions));
    }

    private void Report(BuildWatchCycleResult result)
    {
        if (result.Starting is { } starting) WatchStatus(starting, options);
        if (result.Build is { } build) Report(build, false);
        if (result.Finished is { } finished) DiagnosticReporter.WriteStatus(output, finished, now(), options.Locale, Pretty());
    }

    private void Report(BuildResult result, bool clean)
    {
        if (options.Quiet == true) return;
        if (!clean) foreach (var message in result.Messages) DiagnosticReporter.WriteStatus(output, message, now(), options.Locale, Pretty());
        if (result.Projects.Count == 0) foreach (var diagnostic in result.Diagnostics) WriteDiagnostic(diagnostic);
        foreach (var project in result.Projects)
        {
            foreach (var message in project.Messages.Take(project.Messages.Count - project.TrailingMessageCount)) DiagnosticReporter.WriteStatus(output, message, now(), options.Locale, Pretty());
            if (project.Program is { } tracedProgram) WriteResolutionTrace(tracedProgram.Program);
            foreach (var diagnostic in project.Diagnostics) WriteDiagnostic(diagnostic, project.Program?.Program);
            if (project.Program is { } program) { Program = program; ListFiles(program.Program, project.EmittedFiles); }
            foreach (var message in project.Messages.TakeLast(project.TrailingMessageCount)) DiagnosticReporter.WriteStatus(output, message, now(), options.Locale, Pretty());
        }
        if (clean) foreach (var message in result.Messages) DiagnosticReporter.WriteStatus(output, message, now(), options.Locale, Pretty());
    }

    private void WatchStatus(Diagnostic diagnostic, CompilerOptions configOptions)
    {
        if (configOptions.PreserveWatchOutput != true && configOptions.Diagnostics != true && configOptions.ExtendedDiagnostics != true)
            output.Write("\u001b[2J\u001b[3J\u001b[H");
        DiagnosticReporter.WriteStatus(output, diagnostic, now(), options.Locale, Pretty(configOptions));
    }

    private void WriteDiagnostic(Diagnostic diagnostic, CompilerProgram? program = null)
    { if (options.Quiet != true) DiagnosticReporter.Write(output, diagnostic, fileSystem, currentDirectory, program, options.Locale, Pretty()); }

    private void WriteResolutionTrace(CompilerProgram program)
    {
        if (options.Quiet != true)
            foreach (var trace in program.ResolutionTrace) output.Write(trace.Format(options.Locale).ToString() + "\n");
    }

    private CompilerExitStatus ReportError(DiagnosticMessage message, params Utf8String[] arguments) =>
        ReportErrors([new(message, 0, 0, arguments)], CompilerExitStatus.DiagnosticsWithOutputsSkipped);
    private CompilerExitStatus ReportErrors(IReadOnlyList<Diagnostic> diagnostics, CompilerExitStatus status)
    { foreach (var diagnostic in diagnostics) WriteDiagnostic(diagnostic); return status; }

    private void ListFiles(CompilerProgram program, IReadOnlyList<Utf8String> emitted)
    {
        var config = program.Configuration.Options;
        if (config.ListEmittedFiles == true) foreach (var path in emitted) output.Write($"TSFILE: {path}\n");
        if (config.ExplainFiles == true) ExplainFiles(program);
        else if (config.ListFiles == true || config.ListFilesOnly == true)
            foreach (var file in program.SourceFiles) output.Write(file.Syntax.FileName.ToString() + "\n");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (projectWatch is not null) await projectWatch.DisposeAsync().ConfigureAwait(false);
        if (buildWatch is not null) await buildWatch.DisposeAsync().ConfigureAwait(false);
        if (ownsMapperHost) await mapperHost.DisposeAsync().ConfigureAwait(false);
        Program = null;
    }
}

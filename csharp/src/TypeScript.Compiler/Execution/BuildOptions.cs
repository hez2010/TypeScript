using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Incremental;

namespace TypeScript.Compiler.Execution;

public enum CompilerExitStatus
{
    Success, DiagnosticsWithOutputsSkipped, DiagnosticsWithOutputsGenerated, InvalidProject,
    ProjectReferenceCycle, NotImplemented
}

public sealed record BuildOptions
{
    public bool Dry { get; init; }
    public bool Force { get; init; }
    public bool Clean { get; init; }
    public bool Verbose { get; init; }
    public bool StopBuildOnErrors { get; init; }
    public int Builders { get; init; } = 4;

    public static BuildOptions FromCommandLine(CompilerOptions options) => new()
    {
        Dry = options.Boolean("dry"u8) == true, Force = options.Boolean("force"u8) == true,
        Clean = options.Boolean("clean"u8) == true, Verbose = options.Boolean("verbose"u8) == true,
        StopBuildOnErrors = options.Boolean("stopBuildOnErrors"u8) == true,
        Builders = options.SingleThreaded == true ? 1 : (int)(options.Number("builders"u8) ?? 4)
    };
}

public enum ProjectBuildStatus
{
    ConfigFileNotFound, BuildErrors, UpstreamErrors, UpToDate, UpToDateWithUpstreamTypes,
    UpToDateWithInputFileText, InputFileMissing, OutputMissing, InputFileNewer, PendingEmit,
    PendingErrors, OptionsChanged, RootsChanged, VersionChanged, ForceBuild, Solution
}

public sealed record ProjectBuildResult(Utf8String ConfigFile, ProjectBuildStatus Status,
    CompilerExitStatus ExitStatus, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<Diagnostic> Messages,
    IReadOnlyList<Utf8String> EmittedFiles, IReadOnlyList<Utf8String> DeletedFiles, IReadOnlyList<Utf8String> UpdatedTimestamps,
    IncrementalProgram? Program)
{
    internal int TrailingMessageCount { get; init; }
}

public sealed record BuildResult(CompilerExitStatus ExitStatus, IReadOnlyList<ProjectBuildResult> Projects,
    IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<Diagnostic> Messages);

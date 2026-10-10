using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.Emission;

public enum EmitOnly { All, JavaScript, Declarations, BuilderSignature }

public sealed record EmitOptions
{
    public IReadOnlyList<SourceFileNode>? SourceFiles { get; init; }
    public EmitOnly Only { get; init; }
    public bool Force { get; init; }
    public Func<Utf8String, Utf8String, EmitWriteFileData, CancellationToken, ValueTask>? WriteFile { get; init; }
    /// <summary>
    /// Overlaps a file's JavaScript print with its declaration transform. Only worth setting when the
    /// caller is not already emitting several files in parallel: with file-level parallelism the cores
    /// are busy, and a second concurrent pass per file costs more in GC contention than it saves
    /// (measured +3.7% on declarations and +4.7% on tiny-files), while a single large file gains 9.2%.
    /// </summary>
    public bool PipelineDeclarationTransform { get; init; }
}

public sealed record EmitWriteFileData(SourceFileNode SourceFile, int SourceMapUrlPosition = -1,
    IReadOnlyList<Diagnostic>? Diagnostics = null)
{
    public bool SkippedDeclarationWrite { get; set; }
}

public sealed record SourceMapEmitResult(Utf8String GeneratedFile, IReadOnlyList<Utf8String> InputSourceFileNames, SourceMap Map);

public sealed record EmitResult(bool EmitSkipped, IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<Utf8String> EmittedFiles, IReadOnlyList<SourceMapEmitResult> SourceMaps);

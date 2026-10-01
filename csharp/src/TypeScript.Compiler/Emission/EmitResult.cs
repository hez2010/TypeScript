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
}

public sealed record EmitWriteFileData(SourceFileNode SourceFile, int SourceMapUrlPosition = -1,
    IReadOnlyList<Diagnostic>? Diagnostics = null)
{
    public bool SkippedDeclarationWrite { get; set; }
}

public sealed record SourceMapEmitResult(Utf8String GeneratedFile, IReadOnlyList<Utf8String> InputSourceFileNames, SourceMap Map);

public sealed record EmitResult(bool EmitSkipped, IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<Utf8String> EmittedFiles, IReadOnlyList<SourceMapEmitResult> SourceMaps);

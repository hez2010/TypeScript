using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly List<(SourceFileNode? File, int Code)> diagnosticFiles = [];

    internal void TrackDiagnostic(SyntaxNode? node, int code) => diagnosticFiles.Add((SemanticSyntax.Source(node), code));

    internal SyntaxNode? DiagnosticNode => Expressions.CurrentNode ?? CurrentSourceNode;

    // File attribution is retained independently of the compatibility code lists.
    // Full message arguments, spans and related information remain separate work.
    internal IReadOnlyList<int> DiagnosticCodesForFile(SourceFileNode file) => diagnosticFiles.Concat(program.DiagnosticFiles)
        .Where(d => d.File == file).Select(d => d.Code).Order().ToArray();
}

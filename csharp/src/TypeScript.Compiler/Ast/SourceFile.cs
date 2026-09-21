using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Ast;

public sealed partial class SourceFileNode
{
    public string FileName { get; internal set; } = "";
    public SourceText Source { get; internal set; } = new("");
    public ScriptKind ScriptKind { get; internal set; }
    public bool IsDeclarationFile { get; internal set; }
    public IReadOnlyList<Diagnostic> ParseDiagnostics { get; internal set; } = [];
    public IReadOnlyList<CommentDirective> CommentDirectives { get; internal set; } = [];
}

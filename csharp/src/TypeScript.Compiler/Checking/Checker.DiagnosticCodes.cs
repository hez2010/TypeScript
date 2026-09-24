using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly List<(SyntaxNode? Node, int Code)> diagnosticFiles = [];

    internal void TrackDiagnostic(SyntaxNode? node, int code) => diagnosticFiles.Add((node, code));

    internal SyntaxNode? DiagnosticNode => Expressions.CurrentNode ?? CurrentSourceNode;

    // File attribution is retained independently of the compatibility code lists.
    // Full message arguments, spans and related information remain separate work.
    internal IReadOnlyList<int> DiagnosticCodesForFile(SourceFileNode? file) => diagnosticFiles.Concat(program.DiagnosticFiles)
        .Where(d => SemanticSyntax.Source(d.Node) == file).Select(d => d.Code).Order().ToArray();

    internal IReadOnlyList<int> DiagnosticCodesForProgramFile(SourceFileNode file)
    {
        if (SkipProgramFile(file))
            return [];
        var diagnostics = new List<(int Position, int Code)>();
        foreach (var (node, code) in diagnosticFiles.Concat(program.DiagnosticFiles))
            if (SemanticSyntax.Source(node) == file)
            {
                var scanner = new Scanner(file.Source);
                scanner.ResetPosition(file.Source.ToUtf16Position(Math.Max(0, node!.Pos)));
                scanner.Scan();
                diagnostics.Add((file.Source.ToBytePosition(scanner.TokenStart), code));
            }
        foreach (var diagnostic in program.Symbols.Binding(file)!.Diagnostics)
            diagnostics.Add((diagnostic.Start, diagnostic.Code));
        if ((file.Flags & NodeFlags.JavaScriptFile) != 0
            && (file.CheckJsDirective?.Enabled ?? program.Symbols.Program.Configuration.Options.Boolean("checkJs") ?? false))
            foreach (var diagnostic in file.JSDocDiagnostics)
                diagnostics.Add((diagnostic.Start, diagnostic.Code));
        bool plainJavaScript = (file.Flags & NodeFlags.JavaScriptFile) != 0 && file.CheckJsDirective?.Enabled != true
            && program.Symbols.Program.Configuration.Options.Boolean("checkJs") != true;
        if (plainJavaScript)
            return diagnostics.Where(d => JavaScriptDiagnostics.IsPlainError(d.Code)).Select(d => d.Code).Order().ToArray();
        if (file.CommentDirectives.Count == 0)
            return diagnostics.Select(d => d.Code).Order().ToArray();
        var directives = new Dictionary<int, bool>();
        foreach (var directive in file.CommentDirectives)
            directives[file.Source.GetLineAndCharacter(directive.Start).Line] = directive.ExpectError;
        var codes = new List<int>();
        foreach (var (position, code) in diagnostics)
        {
            bool ignored = false;
            for (int line = file.Source.GetLineAndCharacter(position).Line - 1; line >= 0; line--)
            {
                if (directives.ContainsKey(line))
                {
                    directives[line] = false;
                    ignored = true;
                    break;
                }
                int offset = file.Source.LineStarts[line];
                var text = file.Source.Text;
                while (offset < text.Length && text[offset] is ' ' or '\t')
                    offset++;
                if (!(offset == text.Length
                    || text[offset] is '\r' or '\n'
                    || offset + 1 < text.Length && text[offset] == '/' && text[offset + 1] == '/'))
                    break;
            }
            if (!ignored)
                codes.Add(code);
        }
        foreach (bool unused in directives.Values)
            if (unused)
                codes.Add(2578);
        codes.Sort();
        return codes;
    }
}

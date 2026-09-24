using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly List<(SyntaxNode? Node, int Code)> diagnosticFiles = [];
    private readonly List<(SourceFileNode File, Diagnostic Diagnostic)> sourceDiagnostics = [];

    internal IReadOnlyList<Diagnostic> DetailedDiagnosticsForFile(SourceFileNode file)
        => sourceDiagnostics.Where(d => d.File == file).Select(d => d.Diagnostic).ToArray();

    internal void TrackDiagnostic(SyntaxNode? node, int code) => diagnosticFiles.Add((node, code));

    internal SyntaxNode? DiagnosticNode => Expressions.CurrentNode ?? CurrentSourceNode;

    // File attribution is retained independently of the compatibility code lists.
    // Full message arguments, spans and related information remain separate work.
    internal IReadOnlyList<int> DiagnosticCodesForFile(SourceFileNode? file) => diagnosticFiles.Concat(program.DiagnosticFiles)
        .Where(d => SemanticSyntax.Source(d.Node) == file).Select(d => d.Code)
        .Concat(sourceDiagnostics.Where(d => d.File == file).Select(d => d.Diagnostic.Code)).Order().ToArray();

    internal IReadOnlyList<int> DiagnosticCodesForProgramFile(SourceFileNode file)
    {
        if (SkipProgramFile(file))
            return [];
        var diagnostics = new List<(int Position, int End, int Code)>();
        foreach (var (node, code) in diagnosticFiles.Concat(program.DiagnosticFiles))
            if (SemanticSyntax.Source(node) == file)
            {
                var scanner = new Scanner(file.Source);
                scanner.ResetPosition(file.Source.ToUtf16Position(Math.Max(0, node!.Pos)));
                scanner.Scan();
                int start = file.Source.ToBytePosition(scanner.TokenStart);
                diagnostics.Add((start, Math.Max(start, node.End), code));
            }
        foreach (var diagnostic in program.Symbols.Binding(file)!.Diagnostics)
            diagnostics.Add((diagnostic.Start, diagnostic.Start + diagnostic.Length, diagnostic.Code));
        foreach (var (source, diagnostic) in sourceDiagnostics)
            if (source == file)
                diagnostics.Add((diagnostic.Start, diagnostic.Start + diagnostic.Length, diagnostic.Code));
        if ((file.Flags & NodeFlags.JavaScriptFile) != 0
            && (file.CheckJsDirective?.Enabled ?? program.Symbols.Program.Configuration.Options.Boolean("checkJs") ?? false))
            foreach (var diagnostic in file.JSDocDiagnostics)
                diagnostics.Add((diagnostic.Start, diagnostic.Start + diagnostic.Length, diagnostic.Code));
        bool plainJavaScript = (file.Flags & NodeFlags.JavaScriptFile) != 0 && file.CheckJsDirective?.Enabled != true
            && program.Symbols.Program.Configuration.Options.Boolean("checkJs") != true;
        if (plainJavaScript)
            diagnostics.RemoveAll(d => !JavaScriptDiagnostics.IsPlainError(d.Code));
        else if (file.CommentDirectives.Count != 0)
        {
            var directives = new Dictionary<int, CommentDirective>();
            foreach (var directive in file.CommentDirectives)
                directives[file.Source.GetLineAndCharacter(directive.Start).Line] = directive;
            var filtered = new List<(int Position, int End, int Code)>();
            foreach (var diagnostic in diagnostics)
            {
                bool ignored = false;
                for (int line = file.Source.GetLineAndCharacter(diagnostic.Position).Line - 1; line >= 0; line--)
                {
                    if (directives.TryGetValue(line, out var directive))
                    {
                        directives[line] = directive with { ExpectError = false };
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
                    filtered.Add(diagnostic);
            }
            foreach (var directive in directives.Values)
                if (directive.ExpectError)
                    filtered.Add((directive.Start, directive.End, 2578));
            diagnostics = filtered;
        }
        if (program.Symbols.Program.GetFile(file.FileName)?.Mapping is not { } mapping)
            return diagnostics.Select(d => d.Code).Order().ToArray();
        IEnumerable<Diagnostic> mapped = diagnostics.Select(d => new Diagnostic(
            DiagnosticLocalization.GetMessage(d.Code), d.Position, d.End - d.Position, [])
        { FileName = file.FileName });
        if (!plainJavaScript)
            mapped = mapping.ApplyDiagnosticDirectives(mapped);
        return mapped.Where(d => !d.Message.ReportsUnnecessary || d.Source is not null
            || mapping.Map.VirtualToOriginalSpan(d.Start, d.Start + d.Length).Fidelity != MappingFidelity.None)
            .Select(d => d.Code).Order().ToArray();
    }
}

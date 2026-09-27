using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly List<(SyntaxNode? Node, Diagnostic Diagnostic)> diagnosticFiles = [];
    private readonly List<(SourceFileNode File, Diagnostic Diagnostic)> sourceDiagnostics = [];

    internal IReadOnlyList<Diagnostic> DetailedDiagnosticsForFile(SourceFileNode? file)
        => diagnosticFiles.Concat(program.DiagnosticFiles)
            .Where(d => SemanticSyntax.Source(d.Node) == file)
            .Select(d => WithRelatedInformation(d.Node, d.Diagnostic))
            .Concat(sourceDiagnostics.Where(d => d.File == file).Select(d => d.Diagnostic))
            .Distinct(DiagnosticEqualityComparer.Instance).ToArray();

    private Diagnostic WithRelatedInformation(SyntaxNode? node, Diagnostic diagnostic)
    {
        if (node is null)
            return diagnostic;
        if (diagnostic.Code == 2801 || MissingAwaitHints.Contains(node))
        {
            var hint = CheckerDiagnostic.Create(node, Messages.Did_you_forget_to_use_await);
            if (!diagnostic.RelatedInformation.Contains(hint, DiagnosticEqualityComparer.Instance))
                diagnostic = diagnostic with { RelatedInformation = [.. diagnostic.RelatedInformation, hint] };
        }
        if (diagnostic.Code == 2775 && AssertionRelatedDeclarations.TryGetValue(node, out var assertionDeclarations))
            diagnostic = diagnostic with
            {
                RelatedInformation = assertionDeclarations.Select(d => CheckerDiagnostic.Create(d.Declaration,
                    Messages.X_0_needs_an_explicit_type_annotation, TypeDisplay.SymbolName(d.Symbol))).ToArray()
            };
        if (IterationAwaitHints.Contains((node, diagnostic.Code)))
            diagnostic = diagnostic with
            {
                RelatedInformation =
                [
                    CheckerDiagnostic.Create(node, Messages.Did_you_forget_to_use_await),
                    .. diagnostic.RelatedInformation
                ]
            };
        if (diagnostic.Code is 2322 or 2345 or 2559 or 2560 or 2739 or 2740 or 2741)
        {
            bool construct = AssignmentHints.Contains((node, true));
            if (construct || AssignmentHints.Contains((node, false)))
            {
                var hint = CheckerDiagnostic.Create(node, construct ? Messages.Did_you_mean_to_use_new_with_this_expression
                    : Messages.Did_you_mean_to_call_this_expression);
                if (!diagnostic.RelatedInformation.Any(
                    d => d.Code == hint.Code && d.FileName == hint.FileName && d.Start == hint.Start && d.Length == hint.Length))
                    diagnostic = diagnostic with { RelatedInformation = [.. diagnostic.RelatedInformation, hint] };
            }
        }
        if (diagnostic.Code is 2552 or 2833 && SuggestedNameDeclarations.TryGetValue(node, out var suggestion))
            return diagnostic with
            {
                RelatedInformation = [CheckerDiagnostic.Create(
                suggestion.ValueDeclaration,
                Messages.X_0_is_declared_here,
                suggestion.Name)]
            };
        if (diagnostic.Code == 2741 && RequiredPropertyDeclarations.TryGetValue(node, out var missing)
            && missing[0].Declarations.FirstOrDefault() is { } declaration)
        {
            var related = CheckerDiagnostic.Create(declaration, Messages.X_0_is_declared_here, TypeDisplay.SymbolName(missing[0]));
            return diagnostic.RelatedInformation.Any(d => d.Code == related.Code && d.FileName == related.FileName
                && d.Start == related.Start && d.Length == related.Length && d.Arguments.SequenceEqual(related.Arguments))
                ? diagnostic : diagnostic with { RelatedInformation = [.. diagnostic.RelatedInformation, related] };
        }
        return diagnostic;
    }

    internal void TrackDiagnostic(SyntaxNode? node, int code, params string[] arguments)
        => diagnosticFiles.Add((node, CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), arguments)));

    internal void TrackDiagnostic(SyntaxNode? node, Diagnostic diagnostic) => diagnosticFiles.Add((node, diagnostic));

    private void ListError(SyntaxNode node, NodeList list, int code, params string[] arguments)
    {
        int start = list.Count == 0 ? list.Pos : CheckerDiagnostic.TokenRange(SemanticSyntax.Source(node)!, list.Pos).Start;
        Error(node, CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), arguments) with
        { Start = start, Length = Math.Max(0, list.End - start) });
    }

    private void TrailingCommaError(SyntaxNode node, NodeList list, int code = 1009)
        => Error(node, CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code)) with { Start = list.End - 1, Length = 1 });

    internal bool ReportTypeRecursionLimit()
    {
        var node = DiagnosticNode;
        if (node is not null && !reported.Add((node, 2589)))
            return false;
        TrackDiagnostic(node, 2589);
        return true;
    }

    private void ErrorOnFirstToken(SyntaxNode node, int code, params string[] arguments)
    {
        if (!reported.Add((node, code)))
            return;
        var file = SemanticSyntax.Source(node)!;
        var (start, end) = CheckerDiagnostic.TokenRange(file, node.Pos);
        Diagnostics.Add(code);
        diagnosticFiles.Add(
            (node, new(DiagnosticLocalization.GetMessage(code), start, end - start, arguments) { FileName = file.FileName }));
    }

    internal SyntaxNode? DiagnosticNode => Expressions.CurrentNode ?? CurrentSourceNode;

    internal IReadOnlyList<int> DiagnosticCodesForFile(SourceFileNode? file)
        => DetailedDiagnosticsForFile(file).Select(d => d.Code).Order().ToArray();

    internal IReadOnlyList<int> DiagnosticCodesForProgramFile(SourceFileNode file)
        => DetailedDiagnosticsForProgramFile(file).Select(d => d.Code).Order().ToArray();

    internal IReadOnlyList<Diagnostic> DetailedDiagnosticsForProgramFile(SourceFileNode file)
    {
        if (SkipProgramFile(file))
            return [];
        var diagnostics = new List<Diagnostic>(DetailedDiagnosticsForFile(file));
        foreach (var diagnostic in program.Symbols.Binding(file)!.Diagnostics)
            diagnostics.Add(diagnostic with { FileName = file.FileName });
        if ((file.Flags & NodeFlags.JavaScriptFile) != 0
            && (file.CheckJsDirective?.Enabled ?? program.Symbols.Program.Configuration.Options.Boolean("checkJs") ?? false))
            foreach (var diagnostic in file.JSDocDiagnostics)
                diagnostics.Add(diagnostic with { FileName = file.FileName });
        diagnostics = diagnostics.Distinct(DiagnosticEqualityComparer.Instance).ToList();
        bool plainJavaScript = (file.Flags & NodeFlags.JavaScriptFile) != 0 && file.CheckJsDirective?.Enabled != true
            && program.Symbols.Program.Configuration.Options.Boolean("checkJs") != true;
        if (plainJavaScript)
            diagnostics.RemoveAll(d => !JavaScriptDiagnostics.IsPlainError(d.Code));
        else
            diagnostics = FilterCommentDirectives(file, diagnostics, true);
        var includes = FilterCommentDirectives(
            file,
            program.Symbols.Program.IncludeDiagnostics.Where(d => d.FileName == file.FileName).ToArray(),
            false);
        if (program.Symbols.Program.GetFile(file.FileName)?.Mapping is not { } mapping)
            return DiagnosticCollection.SortAndDeduplicate(diagnostics.Concat(includes));
        IEnumerable<Diagnostic> mapped = diagnostics;
        if (!plainJavaScript)
            mapped = mapping.ApplyDiagnosticDirectives(mapped);
        return DiagnosticCollection.SortAndDeduplicate(mapped.Where(d => !d.Message.ReportsUnnecessary || d.Source is not null
            || mapping.Map.VirtualToOriginalSpan(d.Start, d.Start + d.Length).Fidelity != MappingFidelity.None)
            .Concat(includes));
    }

    private static List<Diagnostic> FilterCommentDirectives(SourceFileNode file, IReadOnlyList<Diagnostic> diagnostics, bool reportUnused)
    {
        if (file.CommentDirectives.Count == 0)
            return diagnostics.ToList();
        var directives = new Dictionary<int, CommentDirective>();
        foreach (var directive in file.CommentDirectives)
            directives[file.Source.GetLineAndCharacter(directive.Start).Line] = directive;
        var filtered = new List<Diagnostic>();
        foreach (var diagnostic in diagnostics)
        {
            bool ignored = false;
            for (int line = file.Source.GetLineAndCharacter(diagnostic.Start).Line - 1; line >= 0; line--)
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
            if (reportUnused && directive.ExpectError)
                filtered.Add(new(DiagnosticLocalization.GetMessage(2578), directive.Start, directive.End - directive.Start, [])
                { FileName = file.FileName });
        return filtered;
    }
}

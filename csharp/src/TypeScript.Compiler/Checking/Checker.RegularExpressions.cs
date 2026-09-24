using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private void CheckRegularExpression(SyntaxNode node)
    {
        var data = links.Nodes.Get(node);
        if ((data.Flags & NodeCheckFlags.TypeChecked) != 0)
            return;
        data.Flags |= NodeCheckFlags.TypeChecked;
        var file = SemanticSyntax.Source(node);
        if (file is null || file.ParseDiagnostics.Count != 0)
            return;
        var scanner = new Scanner(file.Source, jsx: file.ScriptKind is ScriptKind.JSX or ScriptKind.TSX) { TargetYear = TargetYear };
        scanner.ResetPosition(file.Source.ToUtf16Position(node.Pos));
        scanner.Scan();
        if (scanner.RescanSlashToken(true) != SyntaxKind.RegularExpressionLiteral)
            throw new InvalidOperationException("Regular expression node did not scan as a regular expression");
        int lastIndex = -1;
        foreach (var error in scanner.Diagnostics)
        {
            int start = file.Source.ToBytePosition(error.Start);
            int length = file.Source.ToBytePosition(error.Start + error.Length) - start;
            var diagnostic = error with { Start = start, Length = length, FileName = file.FileName };
            var previous = lastIndex >= 0 ? sourceDiagnostics[lastIndex].Diagnostic : null;
            if (error.Message.Category == DiagnosticCategory.Message && previous?.Start == start && previous.Length == length)
            {
                sourceDiagnostics[lastIndex] = (file, previous with
                {
                    RelatedInformation = [.. previous.RelatedInformation, diagnostic with { FileName = null }]
                });
            }
            else if (previous is null || previous.Start != start)
            {
                sourceDiagnostics.Add((file, diagnostic));
                Diagnostics.Add(diagnostic.Code);
                lastIndex = sourceDiagnostics.Count - 1;
            }
        }
    }
}

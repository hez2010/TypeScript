using System.Text;
using TypeScript.Compiler.LanguageServices;

namespace TypeScript.Compiler.LanguageServer;

internal static class FlakyDiagnostics
{
    internal static (Utf8String Full, Utf8String Sanitized) Compare(IReadOnlyList<DocumentDiagnostic> before, IReadOnlyList<DocumentDiagnostic> after)
    {
        var full = new StringBuilder(); var sanitized = new StringBuilder();
        foreach (var diagnostic in after) if (!before.Any(other => Equal(diagnostic, other))) Append(diagnostic, "after", "before");
        foreach (var diagnostic in before) if (!after.Any(other => Equal(diagnostic, other))) Append(diagnostic, "before", "after");
        return (Utf8String.FromString(full.ToString()), Utf8String.FromString(sanitized.ToString()));

        static bool Equal(DocumentDiagnostic first, DocumentDiagnostic second) => first.Code == second.Code && first.Range == second.Range && first.Message == second.Message;
        void Append(DocumentDiagnostic diagnostic, string present, string absent)
        {
            var range = diagnostic.Range;
            full.Append($"Diagnostic {diagnostic.Code} ({range.Start.Line}:{range.Start.Character}-{range.End.Line}:{range.End.Character}): {diagnostic.Message} was present {present} emit but not {absent} emit\n");
            sanitized.Append($"Diagnostic Code({diagnostic.Code}) was present {present} emit but not {absent} emit\n");
        }
    }

    internal sealed class Failure(Utf8String difference) : Exception("flaky diagnostic(s) logged:\n" + difference.ToString());
}

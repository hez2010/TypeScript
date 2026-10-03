using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.LanguageServices;

public static partial class SourceFormatter
{
    public static async ValueTask<Utf8String> FormatNodeForInsertionAsync(SyntaxNode node, SourceFileNode target, int position,
        FormatCodeSettings? options = null, CancellationToken cancellation = default)
        => await FormatNodeForInsertionAsync(node, target, position, options ?? new(), null, null, null, false, cancellation).ConfigureAwait(false);

    internal static async ValueTask<Utf8String> FormatNodeForInsertionAsync(SyntaxNode node, SourceFileNode target, int position,
        FormatCodeSettings options, EmitContext? context, int? initialIndentation, int? indentationDelta, bool startsNewLine,
        CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, target.Source.Length);
        var (text, positioned) = SyntaxPrinter.PrintAndPositionNode(node, source: context is null ? null : target,
            newLine: options.NewLineCharacter, indentSize: options.IndentSize, context: context, cancellation: cancellation);
        var file = new SourceFileNode
        {
            FileName = target.FileName, ScriptKind = target.ScriptKind, IsDeclarationFile = target.IsDeclarationFile,
            Source = new(text), Pos = 0, End = text.Length,
            Statements = new([positioned], positioned.Pos, positioned.End),
            EndOfFileToken = new TokenNode(SyntaxKind.EndOfFile) { Pos = text.Length, End = text.Length },
        };
        file.SetParents();
        int indentation = initialIndentation ?? await SmartIndenter.GetIndentationAsync(position, target, options,
            startsNewLine || LineStart(target, position) == position, cancellation).ConfigureAwait(false);
        int delta = indentationDelta ?? (SmartIndenter.ShouldIndentChild(options, node, null, null) ? options.IndentSize : 0);
        var changes = await FormatNodeAsync(positioned, file, indentation, delta, options, cancellation).ConfigureAwait(false);
        var result = new Utf8StringBuilder();
        int end = 0;
        foreach (var change in changes)
        {
            result.Append(text.Span[end..change.Start]);
            result.Append(change.NewText);
            end = change.End;
        }
        result.Append(text.Span[end..]);
        return result.ToUtf8String();
    }
}

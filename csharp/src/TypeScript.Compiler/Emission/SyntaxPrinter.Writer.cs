using TypeScript.Compiler.Text;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Emission;

public sealed partial class SyntaxPrinter
{
    private sealed class PrinterWriter(EmitTextWriter inner, bool omitTrailingSemicolon, bool trackPositions)
    {
        private bool pendingSemicolon;
        public int LastNonTriviaPosition { get; private set; }
        public int Position => inner.Position;
        public int Line => inner.Line;
        public int Column => inner.Column;
        public int Indentation => inner.Indentation;
        public bool AtLineStart => inner.AtLineStart;
        public bool HasTrailingComment => inner.HasTrailingComment;
        public bool HasTrailingWhitespace => inner.HasTrailingWhitespace;

        private void Commit()
        {
            if (pendingSemicolon)
            {
                inner.Write(";"u8);
                Track(";"u8);
                pendingSemicolon = false;
            }
        }

        public void Clear() { pendingSemicolon = false; LastNonTriviaPosition = 0; inner.Clear(); }
        public void IncreaseIndent() { Commit(); inner.IncreaseIndent(); }
        public void DecreaseIndent() { Commit(); inner.DecreaseIndent(); }
        public void WriteLine(bool force = false) { Commit(); inner.WriteLine(force); }
        public void Write(Utf8String text) => Write(text.Span);
        public void Write(ReadOnlySpan<byte> text) { Commit(); inner.Write(text); Track(text); }
        public void WriteLiteral(ReadOnlySpan<byte> text) { Commit(); inner.Write(text); Track(text, true); }
        public void RawWrite(Utf8String text) => RawWrite(text.Span);
        public void RawWrite(ReadOnlySpan<byte> text) { Commit(); inner.RawWrite(text); Track(text); }
        public void WriteComment(Utf8String text) { Commit(); inner.WriteComment(text); }
        public void WriteTrailingSemicolon()
        {
            if (omitTrailingSemicolon)
                pendingSemicolon = true;
            else
            {
                inner.Write(";"u8);
                Track(";"u8);
            }
        }

        private void Track(ReadOnlySpan<byte> text, bool force = false)
        {
            if (!trackPositions) return;
            if (!force && new Scanner(new SourceText(new Utf8String(text))).SkipTriviaAt(0) == text.Length) return;
            int end = text.Length;
            while (end > 0)
            {
                int point = Wtf8.DecodeLast(text[..end], out int width);
                if (!TokenFacts.IsWhiteSpace(point) && !TokenFacts.IsLineBreak(point)) break;
                end -= width;
            }
            LastNonTriviaPosition = inner.Position - text.Length + end;
        }
    }
}

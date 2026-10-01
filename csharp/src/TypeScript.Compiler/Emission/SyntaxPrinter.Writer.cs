using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

public sealed partial class SyntaxPrinter
{
    private sealed class PrinterWriter(EmitTextWriter inner, bool omitTrailingSemicolon)
    {
        private bool pendingSemicolon;
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
                pendingSemicolon = false;
            }
        }

        public void Clear() { pendingSemicolon = false; inner.Clear(); }
        public void IncreaseIndent() { Commit(); inner.IncreaseIndent(); }
        public void DecreaseIndent() { Commit(); inner.DecreaseIndent(); }
        public void WriteLine(bool force = false) { Commit(); inner.WriteLine(force); }
        public void Write(Utf8String text) => Write(text.Span);
        public void Write(ReadOnlySpan<byte> text) { Commit(); inner.Write(text); }
        public void RawWrite(Utf8String text) => RawWrite(text.Span);
        public void RawWrite(ReadOnlySpan<byte> text) { Commit(); inner.RawWrite(text); }
        public void WriteComment(Utf8String text) { Commit(); inner.WriteComment(text); }
        public void WriteTrailingSemicolon()
        {
            if (omitTrailingSemicolon)
                pendingSemicolon = true;
            else
                inner.Write(";"u8);
        }
    }
}

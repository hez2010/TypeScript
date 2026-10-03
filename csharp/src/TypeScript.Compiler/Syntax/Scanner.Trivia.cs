namespace TypeScript.Compiler.Syntax;

public sealed partial class Scanner
{
    internal int SkipTriviaAt(int position, bool stopAtComments = false, bool inJSDoc = false, bool stopAfterLineBreak = false)
    {
        if (position < 0) return position;
        pos = position;
        bool canConsumeStar = false;
        while (pos < end)
        {
            int ch = CodePoint(out int width);
            if (ch is '\r' or '\n')
            {
                if (ch == '\r' && Char(1) == '\n') pos++;
                pos++; if (stopAfterLineBreak) return pos;
                canConsumeStar = inJSDoc; continue;
            }
            if (TokenFacts.IsWhiteSpace(ch) || TokenFacts.IsLineBreak(ch))
            { pos += width; if (stopAfterLineBreak && TokenFacts.IsLineBreak(ch)) return pos; continue; }
            if (ch == '/' && !stopAtComments && Char(1) is '/' or '*')
            {
                bool multi = Char(1) == '*'; pos += 2;
                while (pos < end)
                {
                    if (multi && Char() == '*' && Char(1) == '/') { pos += 2; break; }
                    int point = CodePoint(out width);
                    if (!multi && TokenFacts.IsLineBreak(point)) break;
                    pos += width;
                }
                canConsumeStar = false; continue;
            }
            if (ch is '<' or '|' or '=' or '>' && IsConflictMarker(pos))
            { ScanConflictMarker(); canConsumeStar = false; continue; }
            if (ch == '#' && pos == 0 && Char(1) == '!')
            {
                while (pos < end && !TokenFacts.IsLineBreak(CodePoint(out width))) pos += width;
                canConsumeStar = false; continue;
            }
            if (ch == '*' && canConsumeStar) { pos++; canConsumeStar = false; continue; }
            break;
        }
        return pos;
    }
}

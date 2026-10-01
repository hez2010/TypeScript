using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

internal sealed partial class JsxTransformer
{
    private static Utf8String FixWhitespace(Utf8String text)
    {
        var result = new Utf8StringBuilder();
        int first = 0, lastEnd = -1;
        bool initial = true;
        for (int i = 0; i < text.Length;)
        {
            int point = Wtf8.Decode(text.Span[i..], out int width);
            if (TokenFacts.IsLineBreak(point))
            {
                if (first != -1 && lastEnd != -1) AddLine(text[first..lastEnd]);
                first = -1;
            }
            else if (!TokenFacts.IsWhiteSpace(point))
            {
                lastEnd = i + width;
                if (first == -1) first = i;
            }
            i += width;
        }
        if (first != -1) AddLine(text[first..]);
        return result.ToUtf8String();

        void AddLine(Utf8String line)
        {
            if (!initial) result.Append(' ');
            result.Append(DecodeEntities(line));
            initial = false;
        }
    }

    private static Utf8String DecodeEntities(Utf8String text)
    {
        int amp = text.Span.IndexOf((byte)'&');
        if (amp < 0) return text;
        var result = new Utf8StringBuilder(text.Length);
        while (amp >= 0)
        {
            result.Append(text[..amp]);
            text = text[amp..];
            int semi = text.Span.IndexOf((byte)';');
            if (semi < 0) break;
            int next;
            while ((next = text.Span[1..semi].IndexOf((byte)'&')) >= 0)
            {
                result.Append(text[..(next + 1)]);
                text = text[(next + 1)..];
                semi -= next + 1;
            }
            if (DecodeEntity(text[1..semi]) is { } point) result.AppendCodePoint(point <= 0x10FFFF ? point : 0xFFFD);
            else result.Append(text[..(semi + 1)]);
            text = text[(semi + 1)..];
            amp = text.Span.IndexOf((byte)'&');
        }
        result.Append(text);
        return result.ToUtf8String();
    }

    private static int? DecodeEntity(Utf8String entity)
    {
        if (entity.Length == 0) return null;
        if (entity[0] != '#') return Entities.TryGetValue(entity, out int point) ? point : null;
        int start = 1, radix = 10;
        if (entity.Length > 1 && entity[1] == 'x') { start++; radix = 16; }
        if (start == entity.Length) return null;
        int value = 0;
        for (int i = start; i < entity.Length; i++)
        {
            int digit = TokenFacts.HexDigit(entity[i]);
            if (digit < 0 || digit >= radix || value > (int.MaxValue - digit) / radix) return null;
            value = value * radix + digit;
        }
        return value;
    }
}

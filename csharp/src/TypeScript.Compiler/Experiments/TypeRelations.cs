using System.Globalization;

namespace TypeScript.Compiler.Experiments;

// Bounded phase-1 slice: primitive/literal/union/alias types, strictNullChecks.
// Corresponds to checker/relater.go isTypeRelatedTo, isSimpleTypeRelatedTo,
// and source-union/target-union branches. This is not a replacement checker.
public enum AtomKind
{
    Any,
    Unknown,
    Never,
    String,
    Number,
    BigInt,
    Boolean,
    Undefined,
    Null,
    Void,
    Symbol,
    Object,
    StringLiteral,
    NumberLiteral,
    True,
    False
}
public readonly record struct TypeAtom(AtomKind Kind, string? Value = null);

public interface IRelationPolicy
{
    static abstract bool Relates(TypeAtom source, TypeAtom target);
}

public readonly struct StrictAssignment : IRelationPolicy
{
    public static bool Relates(TypeAtom source, TypeAtom target)
    {
        AtomKind s = source.Kind, t = target.Kind;
        if (source == target || t == AtomKind.Any || s == AtomKind.Never || t == AtomKind.Unknown)
            return true;
        if (t == AtomKind.Never)
            return false;
        if (s == AtomKind.Any)
            return true;
        return (s, t) switch
        {
            (AtomKind.StringLiteral, AtomKind.String) => true,
            (AtomKind.NumberLiteral, AtomKind.Number) => true,
            (AtomKind.True or AtomKind.False, AtomKind.Boolean) => true,
            (AtomKind.Undefined, AtomKind.Void) => true,
            _ => false,
        };
    }
}

public static class TypeRelations
{
    public static bool Assignable<TPolicy>(ReadOnlySpan<TypeAtom> source, ReadOnlySpan<TypeAtom> target) where TPolicy : IRelationPolicy
    {
        foreach (TypeAtom s in source)
        {
            bool found = false;
            foreach (TypeAtom t in target)
                if (TPolicy.Relates(s, t))
                {
                    found = true;
                    break;
                }
            if (!found)
                return false;
        }
        return true;
    }

    public static bool AssignableConcrete(ReadOnlySpan<TypeAtom> source, ReadOnlySpan<TypeAtom> target)
    {
        foreach (TypeAtom s in source)
        {
            bool found = false;
            foreach (TypeAtom t in target)
                if (StrictAssignment.Relates(s, t))
                {
                    found = true;
                    break;
                }
            if (!found)
                return false;
        }
        return true;
    }

    // This representation parser only accepts the syntax needed by the slice.
    // Unsupported grammar throws; it cannot report a false compiler success.
    // Explicit parenthesis depth makes input-shaped nesting independent of stack.
    public static TypeAtom[] Parse(ReadOnlySpan<char> text, IReadOnlyList<TypeAtom[]> previousAliases)
    {
        List<TypeAtom> atoms = [];
        int depth = 0;
        bool expectType = true;
        while (!text.IsEmpty)
        {
            text = text.TrimStart();
            if (text.IsEmpty)
                break;
            char ch = text[0];
            if (ch == '(' && expectType)
            {
                depth++;
                text = text[1..];
                continue;
            }
            if (ch == ')' && !expectType && depth > 0)
            {
                depth--;
                text = text[1..];
                continue;
            }
            if (ch == '|' && !expectType)
            {
                expectType = true;
                text = text[1..];
                continue;
            }
            if (!expectType)
                throw new NotSupportedException("Type slice expects a union separator");
            if (ch is '\'' or '"')
            {
                int end = text[1..].IndexOf(ch);
                if (end < 0 || text.Slice(1, end).Contains('\\'))
                    throw new NotSupportedException("Escaped/unterminated literals need the full scanner");
                atoms.Add(new(AtomKind.StringLiteral, text.Slice(1, end).ToString()));
                text = text[(end + 2)..];
            }
            else
            {
                int end = text.IndexOfAny(" |()\t\r\n");
                if (end < 0)
                    end = text.Length;
                ReadOnlySpan<char> token = text[..end];
                text = text[end..];
                if (token.Length > 1
                    && token[0] == 'T'
                    && int.TryParse(token[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                    && index < previousAliases.Count)
                    atoms.AddRange(previousAliases[index]);
                else if (double.TryParse(
                    token,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture,
                    out double number)
                    && double.IsFinite(number))
                    atoms.Add(new(AtomKind.NumberLiteral, number == 0 ? "0" : number.ToString("R", CultureInfo.InvariantCulture)));
                else
                {
                    AtomKind kind = token switch
                    {
                        "any" => AtomKind.Any,
                        "unknown" => AtomKind.Unknown,
                        "never" => AtomKind.Never,
                        "string" => AtomKind.String,
                        "number" => AtomKind.Number,
                        "bigint" => AtomKind.BigInt,
                        "boolean" => AtomKind.Boolean,
                        "undefined" => AtomKind.Undefined,
                        "null" => AtomKind.Null,
                        "void" => AtomKind.Void,
                        "symbol" => AtomKind.Symbol,
                        "object" => AtomKind.Object,
                        "true" => AtomKind.True,
                        "false" => AtomKind.False,
                        _ => throw new NotSupportedException($"Unsupported type syntax: {token}"),
                    };
                    if (kind == AtomKind.Boolean)
                        atoms.AddRange([new(AtomKind.True), new(AtomKind.False)]);
                    else
                        atoms.Add(new(kind));
                }
            }
            expectType = false;
        }
        if (expectType || depth != 0)
            throw new InvalidDataException("Incomplete type expression");
        if (atoms.Any(atom => atom.Kind == AtomKind.Any))
            return [new(AtomKind.Any)];
        if (atoms.Any(atom => atom.Kind == AtomKind.Unknown))
            return [new(AtomKind.Unknown)];
        return atoms.Distinct().Where(atom => atom.Kind != AtomKind.Never).ToArray();
    }
}

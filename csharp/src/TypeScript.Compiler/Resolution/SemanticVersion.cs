using System.Globalization;
using System.Text;

namespace TypeScript.Compiler.Resolution;

/// <summary>The compiler's extended semver grammar (major-only and major/minor versions are accepted).</summary>
public sealed partial record SemanticVersion(uint Major, uint Minor = 0, uint Patch = 0, Utf8String Prerelease = default, Utf8String Build = default)
    : IComparable<SemanticVersion>
{
    public static SemanticVersion? Parse(Utf8String text) => TryParse(text, false, out var value, out _) ? value : null;

    internal static bool TryParse(Utf8String text, bool partial, out SemanticVersion value, out int wildcard)
    {
        value = new(0);
        wildcard = 3;
        Span<uint> components = stackalloc uint[3];
        components.Clear();
        int at = 0, count = 0;
        while (count < 3)
        {
            int start = at;
            while (at < text.Length && (int)text[at] is not ('.' or '-' or '+'))
                at++;
            var part = text.Span[start..at];
            if (partial && part.Length == 1 && (int)part[0] is '*' or 'x' or 'X')
                wildcard = Math.Min(wildcard, count);
            else if (!Numeric(part) || count < wildcard && !Component(part, out components[count]))
                return false;
            count++;
            if (at == text.Length || text[at] != '.')
                break;
            if (count == 3)
                return false;
            at++;
        }
        wildcard = Math.Min(wildcard, count);
        Utf8String pre = default, build = default;
        if (at < text.Length)
        {
            if (count != 3)
                return false;
            if (text[at] == '-')
            {
                int start = ++at;
                while (at < text.Length && text[at] != '+')
                    at++;
                pre = text[start..at];
                if (pre.IsEmpty || !ValidIdentifiers(pre, !partial, !partial))
                    return false;
            }
            if (at < text.Length && text[at] == '+')
            {
                build = text[(at + 1)..];
                at = text.Length;
                if (build.IsEmpty || !ValidIdentifiers(build, !partial, false))
                    return false;
            }
            if (at != text.Length)
                return false;
        }
        value = new(components[0], components[1], components[2], pre, build);
        return true;
    }

    private static bool ValidIdentifiers(Utf8String text, bool nonempty, bool prerelease)
    {
        foreach (var part in text.Split((byte)'.'))
        {
            if (part.IsEmpty)
            { if (nonempty) return false; else continue; }
            if (prerelease && Utf8Ascii.IsDigit(part[0]) && !Numeric(part))
                return false;
            ReadOnlySpan<byte> remaining = part;
            while (!remaining.IsEmpty)
            {
                int point = Wtf8.Decode(remaining, out int width);
                if (!Utf8Ascii.IsLetterOrDigit(point) && point is not ('-' or 0x17F or 0x212A))
                    return false;
                remaining = remaining[width..];
            }
        }
        return true;
    }

    internal static bool Component(ReadOnlySpan<byte> text, out uint value)
    {
        value = 0;
        return text.Length == 0 || uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    internal SemanticVersion Increment(int component) => component switch
    {
        0 => new(unchecked(Major + 1)),
        1 => new(Major, unchecked(Minor + 1)),
        2 => new(Major, Minor, unchecked(Patch + 1)),
        _ => throw new ArgumentOutOfRangeException(nameof(component))
    };

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
            return 1;
        int result = Major.CompareTo(other.Major);
        if (result != 0)
            return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0)
            return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0)
            return result;
        if (Prerelease.Length == 0)
            return other.Prerelease.Length == 0 ? 0 : 1;
        if (other.Prerelease.Length == 0)
            return -1;
        ReadOnlySpan<byte> left = Prerelease, right = other.Prerelease;
        var leftParts = left.Split((byte)'.');
        var rightParts = right.Split((byte)'.');
        while (leftParts.MoveNext())
        {
            if (!rightParts.MoveNext())
                return 1;
            ReadOnlySpan<byte> a = left[leftParts.Current], b = right[rightParts.Current];
            if (a.SequenceEqual(b))
                continue;
            bool numericA = Numeric(a), numericB = Numeric(b);
            if (numericA != numericB)
                return numericA ? -1 : 1;
            if (numericA && a.Length != b.Length)
                return a.Length.CompareTo(b.Length);
            return Math.Sign(a.SequenceCompareTo(b));
        }
        return rightParts.MoveNext() ? -1 : 0;
    }

    private static bool Numeric(ReadOnlySpan<byte> text) => text.Length > 0 && (text.Length == 1 || text[0] != '0')
        && !text.ContainsAnyExceptInRange((byte)'0', (byte)'9');

    public Utf8String ToUtf8String()
    {
        var text = new Utf8StringBuilder().Append(Major).Append((byte)'.').Append(Minor).Append((byte)'.').Append(Patch);
        if (!Prerelease.IsEmpty)
            text.Append((byte)'-').Append(Prerelease);
        if (!Build.IsEmpty)
            text.Append((byte)'+').Append(Build);
        return text.ToUtf8String();
    }
    public override string ToString() => ToUtf8String().ToString();
}

public sealed partial class VersionRange
{
    private readonly List<List<(Utf8String Operator, SemanticVersion Version)>> alternatives;

    private VersionRange(List<List<(Utf8String, SemanticVersion)>> alternatives) => this.alternatives = alternatives;

    private static bool Partial(Utf8String text, out SemanticVersion version, out int wildcard)
        => SemanticVersion.TryParse(text, true, out version, out wildcard);

    public static VersionRange? Parse(Utf8String text)
    {
        List<List<(Utf8String, SemanticVersion)>> alternatives = [];
        foreach (Utf8String segment in text.Trim().Split("||"u8, StringSplitOptions.TrimEntries))
        {
            if (segment.Length == 0)
                continue;
            List<(Utf8String, SemanticVersion)> comparators = [];
            var tokens = segment.SplitAny("\t\n\f\r "u8, StringSplitOptions.RemoveEmptyEntries);
            if (tokens is [var lower, var hyphen, var upper] && hyphen == "-"u8)
            {
                if (!Partial(lower, out var left, out int a)
                    || !Partial(upper, out var right, out int b))
                    return null;
                if (a != 0)
                    comparators.Add((Utf8Literals.GreaterThanOrEqual, left));
                if (b != 0)
                    comparators.Add((b < 3 ? Utf8Literals.LessThan : Utf8Literals.LessThanOrEqual, b < 3 ? right.Increment(b - 1) : right));
            }
            else
            {
                foreach (Utf8String part in tokens)
                {
                    int prefix = part.StartsWith("<="u8) || part.StartsWith(">="u8) ? 2
                        : !part.IsEmpty && (int)part[0] is '~' or '^' or '<' or '>' or '=' ? 1 : 0;
                    if (!Partial(part[prefix..], out var version, out int wildcard))
                        return null;
                    Utf8String op = part[..prefix];
                    if (wildcard == 0)
                    {
                        if (op == "<"u8 || op == ">"u8)
                            comparators.Add((Utf8Literals.LessThan, new(0, Prerelease: Utf8Literals.Zero)));
                        continue;
                    }
                    switch (op)
                    {
                        case var _ when op == "~"u8:
                        case var _ when op == "^"u8:
                            int increment = op == Utf8Literals.Tilde ? (wildcard == 1 ? 0 : 1)
                                : version.Major > 0 || wildcard == 1 ? 0 : version.Minor > 0 || wildcard == 2 ? 1 : 2;
                            comparators.Add((Utf8Literals.GreaterThanOrEqual, version));
                            comparators.Add((Utf8Literals.LessThan, version.Increment(increment)));
                            break;
                        case var _ when op == "<"u8:
                        case var _ when op == ">="u8:
                            comparators.Add((op, wildcard < 3 ? version with { Prerelease = Utf8Literals.Zero } : version));
                            break;
                        case var _ when op == "<="u8:
                        case var _ when op == ">"u8:
                            comparators.Add(wildcard < 3
                                ? (op == Utf8Literals.LessThanOrEqual ? Utf8Literals.LessThan : Utf8Literals.GreaterThanOrEqual, version.Increment(wildcard - 1) with { Prerelease = Utf8Literals.Zero })
                                : (op, version));
                            break;
                        case var _ when op == ""u8:
                        case var _ when op == "="u8:
                            if (wildcard == 3)
                                comparators.Add((Utf8Literals.EqualsToken, version));
                            else
                            {
                                comparators.Add((Utf8Literals.GreaterThanOrEqual, version with { Prerelease = Utf8Literals.Zero }));
                                comparators.Add((Utf8Literals.LessThan, version.Increment(wildcard - 1) with { Prerelease = Utf8Literals.Zero }));
                            }
                            break;
                        default:
                            throw new InvalidOperationException("Unknown version comparator");
                    }
                }
            }
            alternatives.Add(comparators);
        }
        return new(alternatives);
    }

    public bool Test(SemanticVersion version) => alternatives.Count == 0 || alternatives.Any(
        alternative => alternative.All(c => c.Operator switch
        {
            _ when c.Operator == "<"u8 => version.CompareTo(c.Version) < 0,
            _ when c.Operator == "<="u8 => version.CompareTo(c.Version) <= 0,
            _ when c.Operator == "="u8 => version.CompareTo(c.Version) == 0,
            _ when c.Operator == ">="u8 => version.CompareTo(c.Version) >= 0,
            _ when c.Operator == ">"u8 => version.CompareTo(c.Version) > 0,
            _ => throw new InvalidOperationException("Unknown version comparator")
        }));

    public Utf8String ToUtf8String()
    {
        Utf8String result = Utf8String.Join(" || "u8, alternatives.Select(a => Utf8String.Join((byte)' ', a.Select(c => c.Operator + c.Version.ToUtf8String()))));
        return result.Length == 0 ? (Utf8String)"*"u8 : result;
    }
    public override string ToString() => ToUtf8String().ToString();
}

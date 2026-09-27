using System.Globalization;
using System.Text.RegularExpressions;

namespace TypeScript.Compiler.Resolution;

/// <summary>The compiler's extended semver grammar (major-only and major/minor versions are accepted).</summary>
public sealed partial record SemanticVersion(uint Major, uint Minor = 0, uint Patch = 0, string Prerelease = "", string Build = "")
    : IComparable<SemanticVersion>
{
    [GeneratedRegex(@"\A(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*)(?:-([a-z0-9-.]+))?(?:\+([a-z0-9-.]+))?)?)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\A(?:0|[1-9][0-9]*|[a-z-][a-z0-9-]*)(?:\.(?:0|[1-9][0-9]*|[a-z-][a-z0-9-]*))*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrereleasePattern();

    [GeneratedRegex(@"\A[a-z0-9-]+(?:\.[a-z0-9-]+)*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildPattern();

    public static SemanticVersion? Parse(string text)
    {
        var match = VersionPattern().Match(text);
        if (!match.Success || !Component(match.Groups[1].ValueSpan, out uint major)
            || !Component(match.Groups[2].ValueSpan, out uint minor) || !Component(match.Groups[3].ValueSpan, out uint patch))
            return null;
        string pre = match.Groups[4].Value, build = match.Groups[5].Value;
        if (pre.Length != 0 && !PrereleasePattern().IsMatch(pre) || build.Length != 0 && !BuildPattern().IsMatch(build))
            return null;
        return new(major, minor, patch, pre, build);
    }

    internal static bool Component(ReadOnlySpan<char> text, out uint value)
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
        ReadOnlySpan<char> left = Prerelease, right = other.Prerelease;
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        while (leftParts.MoveNext())
        {
            if (!rightParts.MoveNext())
                return 1;
            ReadOnlySpan<char> a = left[leftParts.Current], b = right[rightParts.Current];
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

    private static bool Numeric(ReadOnlySpan<char> text) => text.Length > 0 && (text.Length == 1 || text[0] != '0')
        && !text.ContainsAnyExceptInRange('0', '9');

    public override string ToString() => $"{Major}.{Minor}.{Patch}"
        + (Prerelease.Length == 0 ? "" : "-" + Prerelease) + (Build.Length == 0 ? "" : "+" + Build);
}

public sealed partial class VersionRange
{
    private readonly List<List<(string Operator, SemanticVersion Version)>> alternatives;

    private VersionRange(List<List<(string, SemanticVersion)>> alternatives) => this.alternatives = alternatives;

    [GeneratedRegex(@"\A([x*0]|[1-9][0-9]*)(?:\.([x*0]|[1-9][0-9]*)(?:\.([x*0]|[1-9][0-9]*)(?:-([a-z0-9-.]+))?(?:\+([a-z0-9-.]+))?)?)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartialPattern();

    [GeneratedRegex(@"\A\s*([a-z0-9-+.*]+)\s+-\s+([a-z0-9-+.*]+)\s*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HyphenPattern();

    [GeneratedRegex(@"\A(<=|>=|[~^<>=])?\s*([a-z0-9-+.*]+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComparatorPattern();

    [GeneratedRegex(@"[\t\n\f\r ]+")]
    private static partial Regex Whitespace();

    private static bool Partial(string text, out SemanticVersion version, out int wildcard)
    {
        version = new(0);
        wildcard = 3;
        var match = PartialPattern().Match(text);
        if (!match.Success)
            return false;
        Span<uint> components = stackalloc uint[3];
        components.Clear();
        for (int i = 0; i < 3; i++)
        {
            ReadOnlySpan<char> part = match.Groups[i + 1].ValueSpan;
            if (part is "" or "*" or "x" or "X")
                wildcard = Math.Min(wildcard, i);
            if (i < wildcard && !SemanticVersion.Component(part, out components[i]))
                return false;
        }
        version = new(components[0], components[1], components[2], match.Groups[4].Value, match.Groups[5].Value);
        return true;
    }

    public static VersionRange? Parse(string text)
    {
        List<List<(string, SemanticVersion)>> alternatives = [];
        foreach (string segment in text.Trim().Split("||", StringSplitOptions.TrimEntries))
        {
            if (segment.Length == 0)
                continue;
            List<(string, SemanticVersion)> comparators = [];
            var hyphen = HyphenPattern().Match(segment);
            if (hyphen.Success)
            {
                if (!Partial(hyphen.Groups[1].Value, out var left, out int a)
                    || !Partial(hyphen.Groups[2].Value, out var right, out int b))
                    return null;
                if (a != 0)
                    comparators.Add((">=", left));
                if (b != 0)
                    comparators.Add((b < 3 ? "<" : "<=", b < 3 ? right.Increment(b - 1) : right));
            }
            else
            {
                foreach (string part in Whitespace().Split(segment))
                {
                    var match = ComparatorPattern().Match(part);
                    if (!match.Success || !Partial(match.Groups[2].Value, out var version, out int wildcard))
                        return null;
                    string op = match.Groups[1].Value;
                    if (wildcard == 0)
                    {
                        if (op is "<" or ">")
                            comparators.Add(("<", new(0, Prerelease: "0")));
                        continue;
                    }
                    switch (op)
                    {
                        case "~":
                        case "^":
                            int increment = op == "~" ? (wildcard == 1 ? 0 : 1)
                                : version.Major > 0 || wildcard == 1 ? 0 : version.Minor > 0 || wildcard == 2 ? 1 : 2;
                            comparators.Add((">=", version));
                            comparators.Add(("<", version.Increment(increment)));
                            break;
                        case "<":
                        case ">=":
                            comparators.Add((op, wildcard < 3 ? version with { Prerelease = "0" } : version));
                            break;
                        case "<=":
                        case ">":
                            comparators.Add(wildcard < 3
                                ? (op == "<=" ? "<" : ">=", version.Increment(wildcard - 1) with { Prerelease = "0" })
                                : (op, version));
                            break;
                        case "":
                        case "=":
                            if (wildcard == 3)
                                comparators.Add(("=", version));
                            else
                            {
                                comparators.Add((">=", version with { Prerelease = "0" }));
                                comparators.Add(("<", version.Increment(wildcard - 1) with { Prerelease = "0" }));
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
            "<" => version.CompareTo(c.Version) < 0,
            "<=" => version.CompareTo(c.Version) <= 0,
            "=" => version.CompareTo(c.Version) == 0,
            ">=" => version.CompareTo(c.Version) >= 0,
            ">" => version.CompareTo(c.Version) > 0,
            _ => throw new InvalidOperationException("Unknown version comparator")
        }));

    public override string ToString()
    {
        string result = string.Join(" || ", alternatives.Select(a => string.Join(' ', a.Select(c => c.Operator + c.Version))));
        return result.Length == 0 ? "*" : result;
    }
}

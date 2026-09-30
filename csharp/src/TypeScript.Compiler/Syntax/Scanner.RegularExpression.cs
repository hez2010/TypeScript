using TypeScript.Compiler.Text;
using System.Buffers;
using System.Globalization;
using System.Text;
using TypeScript.Compiler.Diagnostics;
using static TypeScript.Compiler.Syntax.TokenFacts;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Scanner
{
    public int TargetYear { get; set; } = int.MaxValue;

    private void ValidateRegularExpression(int flagsStart)
    {
        int seen = 0;
        for (int i = flagsStart; i < pos;)
        {
            if (Rune.DecodeFromUtf8(text.Span[i..pos], out _, out int width) != OperationStatus.Done)
                width = 1;
            int flag = "dgimsuvy"u8.IndexOf(text[i]);
            if (flag < 0)
                Error(Messages.Unknown_regular_expression_flag, i, width);
            else if ((seen & 1 << flag) != 0)
                Error(Messages.Duplicate_regular_expression_flag, i, width);
            else if ((int)text[i] is 'u' or 'v' && (seen & 0x60) != 0)
                Error(Messages.The_Unicode_u_flag_and_the_Unicode_Sets_v_flag_cannot_be_set_simultaneously, i, width);
            else
            {
                seen |= 1 << flag;
                int year = (int)text[i] switch { 's' => 2018, 'd' => 2022, 'v' => 2024, _ => 0 };
                if (TargetYear < year)
                    Error(Messages.This_regular_expression_flag_is_only_available_when_targeting_0_or_later, i, width, Utf8String.Concat("es"u8, Utf8String.Format(year)));
            }
            i += width;
        }
        new RegularExpressionValidator(this, TokenStart + 1, flagsStart - 1, (seen & 0x60) != 0, (seen & 0x40) != 0).Validate();
    }

    // ECMAScript's Annex B, Unicode sets, and backreferences require a language
    // parser rather than System.Text.RegularExpressions. Recursive productions
    // use heap stacks, including recovery for missing closing tokens.
    private sealed partial class RegularExpressionValidator(Scanner scanner, int start, int end, bool unicode, bool sets)
    {
        private readonly int expressionStart = start;
        private int at = start;
        private int pendingLowSurrogate;
        private int captures;
        private bool hasNamedCaptures;
        private readonly HashSet<Utf8String> names = new();
        private readonly Dictionary<Utf8String, int> activeNames = new();
        private readonly List<(Utf8String Name, int Start, int End)> namedReferences = [];
        private readonly List<(int Number, int Start, int End)> references = [];
        private readonly Stack<Group> groups = new();

        private sealed class Group(bool quantifiable)
        {
            public bool Quantifiable = quantifiable;
            public bool Atom;
            public HashSet<Utf8String>? Alternative;
            public HashSet<Utf8String>? Disjunction;
        }

        private int Ch(int offset = 0) => at + offset < end ? scanner.text[at + offset] : -1;

        private int Point(out int width)
        {
            int ch = Ch();
            width = ch < 0 ? 0 : 1;
            if (ch < 128)
                return ch;
            if (Rune.DecodeFromUtf8(scanner.text.Span[at..end], out var rune, out width) == OperationStatus.Done)
                return rune.Value;
            width = 1;
            return 0xFFFD;
        }

        private void Error(DiagnosticMessage message, int location, int length = 0, params Utf8String[] args)
        {
            scanner.Error(message, location, length, args);
        }

        private void Unexpected(int ch, int location) =>
            Error(Messages.Unexpected_0_Did_you_mean_to_escape_it_with_backslash, location, 1, Utf8String.FromCodePoint(ch));

        private void Expected(int ch)
        {
            if (Ch() == ch)
                at++;
            else
                Error(Messages.X_0_expected, at, 0, Utf8String.FromCodePoint(ch));
        }

        public void Validate()
        {
            bool inClass = false;
            for (int i = at; i < end; i++)
            {
                int ch = scanner.text[i];
                if (ch == '\\')
                {
                    i++;
                    continue;
                }
                if (ch == '[')
                    inClass = true;
                else if (ch == ']')
                    inClass = false;
                else if (!inClass
                    && ch == '('
                    && i + 3 < end
                    && scanner.text[i + 1] == '?'
                    && scanner.text[i + 2] == '<'
                    && (int)scanner.text[i + 3] is not ('=' or '!'))
                    hasNamedCaptures = true;
            }
            groups.Push(new(false));
            while (at < end)
            {
                Group group = groups.Peek();
                int before = at, ch = Ch();
                switch (ch)
                {
                    case '(':
                        at++;
                        bool quantifiable = true;
                        if (Ch() != '?')
                            captures++;
                        else
                        {
                            at++;
                            switch (Ch())
                            {
                                case '=' or '!':
                                    at++;
                                    quantifiable = !unicode;
                                    break;
                                case '<':
                                    int groupStart = at++;
                                    if (Ch() is '=' or '!')
                                    {
                                        at++;
                                        quantifiable = false;
                                    }
                                    else
                                    {
                                        GroupName(false);
                                        Expected('>');
                                        captures++;
                                        if (scanner.TargetYear < 2018)
                                            Error(
                                                Messages.Named_capturing_groups_are_only_available_when_targeting_ES2018_or_later,
                                                groupStart,
                                                at - groupStart);
                                    }
                                    break;
                                default:
                                    SubpatternFlags();
                                    Expected(':');
                                    break;
                            }
                        }
                        groups.Push(new(quantifiable));
                        break;
                    case ')':
                        if (groups.Count == 1)
                        {
                            Unexpected(ch, at++);
                            group.Atom = true;
                        }
                        else
                        {
                            at++;
                            CloseGroup();
                        }
                        break;
                    case '|':
                        at++;
                        FinishAlternative(group);
                        group.Atom = false;
                        break;
                    case '^' or '$':
                        at++;
                        group.Atom = false;
                        break;
                    case '\\':
                        at++;
                        if (Ch() is 'b' or 'B')
                        {
                            at++;
                            group.Atom = false;
                        }
                        else
                        {
                            AtomEscape();
                            group.Atom = true;
                        }
                        break;
                    case '[':
                        at++;
                        if (sets)
                            ClassSet();
                        else
                            ClassRanges();
                        Expected(']');
                        group.Atom = true;
                        break;
                    case '{':
                        Quantifier(group);
                        break;
                    case '*' or '+' or '?':
                        at++;
                        if (Ch() == '?')
                            at++;
                        if (!group.Atom)
                            Error(Messages.There_is_nothing_available_for_repetition, before, at - before);
                        group.Atom = false;
                        break;
                    case ']' or '}':
                        if (unicode)
                            Unexpected(ch, at);
                        at++;
                        group.Atom = true;
                        break;
                    case '/':
                        at = end;
                        break;
                    default:
                        SourceCharacter();
                        group.Atom = true;
                        break;
                }
            }
            while (groups.Count > 1)
            {
                Expected(')');
                CloseGroup();
            }
            foreach (var reference in namedReferences)
                if (!names.Contains(reference.Name))
                {
                    Error(
                        Messages.There_is_no_capturing_group_named_0_in_this_regular_expression,
                        reference.Start,
                        reference.End - reference.Start,
                        reference.Name);
                    Suggest(reference.Name, names, reference.Start, reference.End - reference.Start);
                }
            foreach (var reference in references)
                if (reference.Number > captures)
                {
                    if (captures == 0)
                        Error(
                            Messages.This_backreference_refers_to_a_group_that_does_not_exist_There_are_no_capturing_groups_in_this_regular_expression,
                            reference.Start,
                            reference.End - reference.Start);
                    else
                        Error(
                            Messages.This_backreference_refers_to_a_group_that_does_not_exist_There_are_only_0_capturing_groups_in_this_regular_expression,
                            reference.Start,
                            reference.End - reference.Start,
                            Utf8String.Format(captures));
                }
        }

        private void FinishAlternative(Group group)
        {
            if (group.Alternative is null)
                return;
            foreach (Utf8String name in group.Alternative)
            {
                if (--activeNames[name] == 0)
                    activeNames.Remove(name);
            }
            group.Disjunction = MergeNames(group.Disjunction, group.Alternative);
            group.Alternative = null;
        }

        private void CloseGroup()
        {
            Group child = groups.Pop();
            Group parent = groups.Peek();
            if (child.Disjunction is not null)
                foreach (Utf8String name in child.Disjunction)
                    if (child.Alternative?.Contains(name) != true)
                        activeNames[name] = activeNames.GetValueOrDefault(name) + 1;
            // Transfer the live alternative instead of removing and reinserting
            // its names at every enclosing parenthesis. Merge small into large
            // to keep a chain of deeply nested named groups linearithmic.
            parent.Alternative = MergeNames(parent.Alternative, MergeNames(child.Alternative, child.Disjunction));
            parent.Atom = child.Quantifiable;
        }

        private static HashSet<Utf8String>? MergeNames(HashSet<Utf8String>? left, HashSet<Utf8String>? right)
        {
            if (left is null)
                return right;
            if (right is null)
                return left;
            if (left.Count < right.Count)
                (left, right) = (right, left);
            left.UnionWith(right);
            return left;
        }

        private void AddName(Group group, Utf8String name)
        {
            if ((group.Alternative ??= new()).Add(name))
                activeNames[name] = activeNames.GetValueOrDefault(name) + 1;
        }

        private void GroupName(bool reference)
        {
            int nameStart = at;
            Utf8StringBuilder? name = null;
            int part = nameStart;
            bool first = true;
            while (at < end)
            {
                int saved = at, ch = Point(out int width);
                bool escaped = ch == '\\';
                if (escaped)
                {
                    if (Ch(1) != 'u')
                        break;
                    bool extended = Ch(2) == '{';
                    ch = UnicodeEscape(false);
                    if (!extended && ch is >= 0xD800 and <= 0xDBFF && Ch() == '\\' && Ch(1) == 'u' && Ch(2) != '{')
                    {
                        int low = UnicodeEscape(false);
                        ch = low is >= 0xDC00 and <= 0xDFFF ? 0x10000 + (ch - 0xD800 << 10) + low - 0xDC00 : -1;
                    }
                }
                else
                    at += width;
                if (!(first ? IsIdentifierStart(ch) : IsIdentifierPart(ch)))
                {
                    at = saved;
                    break;
                }
                if (escaped)
                {
                    (name ??= new()).Append(scanner.input.AsSpan().Slice(part, saved - part));
                    AppendCodePoint(name, ch);
                    part = at;
                }
                first = false;
            }
            if (first)
            {
                Error(Messages.Expected_a_capturing_group_name, at);
                return;
            }
            Utf8String value = name is null ? scanner.text[nameStart..at]
                : Utf8String.FromBuilder(name.Append(scanner.input.AsSpan().Slice(part, at - part)));
            if (reference)
                namedReferences.Add((value, nameStart, at));
            else if (activeNames.ContainsKey(value))
                Error(
                    Messages.Named_capturing_groups_with_the_same_name_must_be_mutually_exclusive_to_each_other,
                    nameStart,
                    at - nameStart);
            else
            {
                if (!names.Add(value) && scanner.TargetYear is >= 2018 and < 2025)
                    Error(
                        Messages.Duplicate_named_capturing_groups_are_only_available_when_targeting_0_or_later,
                        nameStart,
                        at - nameStart,
                        Utf8Literals.Es2025);
                AddName(groups.Peek(), value);
            }
        }

        private void SubpatternFlags()
        {
            int flagStart = at, seen = PatternModifiers(0);
            if (Ch() == '-')
            {
                at++;
                PatternModifiers(seen);
                if (at == flagStart + 1)
                    Error(Messages.Subpattern_flags_must_be_present_when_there_is_a_minus_sign, flagStart, at - flagStart);
            }
            if (at != flagStart && scanner.TargetYear < 2025)
                Error(
                    Messages.Regular_expression_pattern_modifiers_are_only_available_when_targeting_0_or_later,
                    flagStart,
                    at - flagStart,
                    Utf8Literals.Es2025);
        }

        private int PatternModifiers(int seen)
        {
            while (IsIdentifierPart(Point(out int width)))
            {
                int ch = Ch(), flag = "dgimsuvy"u8.IndexOf((byte)ch);
                if (flag < 0)
                    Error(Messages.Unknown_regular_expression_flag, at, width);
                else if ((seen & 1 << flag) != 0)
                    Error(Messages.Duplicate_regular_expression_flag, at, width);
                else if (ch is not ('i' or 'm' or 's'))
                    Error(Messages.This_regular_expression_flag_cannot_be_toggled_within_a_subpattern, at, width);
                else
                    seen |= 1 << flag;
                at += width;
            }
            return seen;
        }

        private ReadOnlySpan<byte> Digits()
        {
            int digitStart = at;
            while (IsDigit(Ch()))
                at++;
            return scanner.input.AsSpan().Slice(digitStart, at - digitStart);
        }

        private void Quantifier(Group group)
        {
            int quantifierStart = at++, digitsStart = at;
            ReadOnlySpan<byte> min = Digits();
            if (!unicode && min.IsEmpty)
            {
                group.Atom = true;
                return;
            }
            if (Ch() == ',')
            {
                at++;
                ReadOnlySpan<byte> max = Digits();
                if (min.IsEmpty)
                {
                    if (!max.IsEmpty || Ch() == '}')
                        Error(Messages.Incomplete_quantifier_Digit_expected, digitsStart);
                    else
                    {
                        Unexpected('{', quantifierStart);
                        group.Atom = true;
                        return;
                    }
                }
                else if (!max.IsEmpty && (unicode || Ch() == '}'))
                {
                    min = min.TrimStart((byte)'0');
                    max = max.TrimStart((byte)'0');
                    if (min.Length > max.Length || min.Length == max.Length && min.SequenceCompareTo(max) > 0)
                        Error(Messages.Numbers_out_of_order_in_quantifier, digitsStart, at - digitsStart);
                }
            }
            else if (min.IsEmpty)
            {
                if (unicode)
                    Unexpected('{', quantifierStart);
                group.Atom = true;
                return;
            }
            if (Ch() == '}')
                at++;
            else if (unicode)
                Expected('}');
            else
            {
                group.Atom = true;
                return;
            }
            if (Ch() == '?')
                at++;
            if (!group.Atom)
                Error(Messages.There_is_nothing_available_for_repetition, quantifierStart, at - quantifierStart);
            group.Atom = false;
        }

        private void AtomEscape()
        {
            if (Ch() == 'k')
            {
                at++;
                if (Ch() == '<')
                {
                    at++;
                    GroupName(true);
                    Expected('>');
                }
                else if (unicode || hasNamedCaptures)
                    Error(Messages.X_k_must_be_followed_by_a_capturing_group_name_enclosed_in_angle_brackets, at - 2, 2);
            }
            else if (sets && Ch() == 'q')
            {
                at++;
                Error(Messages.X_q_is_only_available_inside_character_class, at - 2, 2);
            }
            else if (CharacterClassEscape(out _))
            { }
            else if (Ch() is >= '1' and <= '9')
            {
                int digitStart = at, number = 0;
                while (IsDigit(Ch()))
                {
                    number = (int)Math.Min(int.MaxValue, (long)number * 10 + Ch() - '0');
                    at++;
                }
                references.Add((number, digitStart, at));
            }
            else
                CharacterEscape(true);
        }

        // Nonnegative: single character; -1: class; -2: malformed multi-character
        // escape, which cannot participate in an ordered range comparison.
        private int CharacterEscape(bool atom)
        {
            int escapeStart = at - 1, ch = Ch();
            if (ch < 0)
            {
                Error(Messages.Undetermined_character_escape, escapeStart, 1);
                return '\\';
            }
            at++;
            if (ch == 'c')
            {
                int letter = Ch();
                if (letter is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
                {
                    at++;
                    return letter & 31;
                }
                if (!unicode && !atom && (IsDigit(letter) || letter == '_'))
                {
                    at++;
                    return letter & 31;
                }
                if (unicode)
                    Error(Messages.X_c_must_be_followed_by_an_ASCII_letter, escapeStart, 2);
                else
                {
                    at--;
                    return '\\';
                }
                return letter;
            }
            if (ch is '^' or '$' or '/' or '\\' or '.' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '|')
                return ch;
            if (ch == '0' && !IsDigit(Ch()))
                return 0;
            if (ch is >= '0' and <= '7')
            {
                int value = ch - '0';
                if (ch <= '3' && Ch() is >= '0' and <= '7')
                {
                    value = value * 8 + Ch() - '0';
                    at++;
                }
                if (Ch() is >= '0' and <= '7')
                {
                    value = value * 8 + Ch() - '0';
                    at++;
                }
                Error(!atom && ch != '0'
                    ? Messages.Octal_escape_sequences_and_backreferences_are_not_allowed_in_a_character_class_If_this_was_intended_as_an_escape_sequence_use_the_syntax_0_instead
                    : Messages.Octal_escape_sequences_are_not_allowed_Use_the_syntax_0,
                    escapeStart, at - escapeStart, Utf8String.Concat("\\x"u8, Utf8String.Format(value, "x2")));
                return value;
            }
            if (ch is '8' or '9')
            {
                Error(
                    Messages.Decimal_escape_sequences_and_backreferences_are_not_allowed_in_a_character_class,
                    escapeStart,
                    at - escapeStart);
                return ch;
            }
            if (ch is 'b' or 'f' or 'n' or 'r' or 't' or 'v')
                return ch switch { 'b' => 8, 'f' => 12, 'n' => 10, 'r' => 13, 't' => 9, _ => 11 };
            if (ch == 'u')
            {
                at = escapeStart;
                bool extended = Ch(2) == '{';
                int value = UnicodeEscape(true);
                if (extended && !unicode)
                    Error(
                        Messages.Unicode_escape_sequences_are_only_available_when_the_Unicode_u_flag_or_the_Unicode_Sets_v_flag_is_set,
                        escapeStart,
                        at - escapeStart);
                if (!extended && unicode && value is >= 0xD800 and <= 0xDBFF && Ch() == '\\' && Ch(1) == 'u' && Ch(2) != '{')
                {
                    int saved = at, low = UnicodeEscape(true);
                    if (low is >= 0xDC00 and <= 0xDFFF)
                        return 0x10000 + (value - 0xD800 << 10) + low - 0xDC00;
                    at = saved;
                }
                return value < 0 ? -2 : value;
            }
            if (ch == 'x')
            {
                int value = 0;
                for (int i = 0; i < 2; i++)
                {
                    int digit = HexDigit(Ch());
                    if (digit < 0)
                    {
                        Error(Messages.Hexadecimal_digit_expected, at);
                        return -2;
                    }
                    value = value * 16 + digit;
                    at++;
                }
                return value;
            }
            at--;
            ch = Point(out int width);
            at += width;
            if (unicode)
                Error(Messages.This_character_cannot_be_escaped_in_a_regular_expression, escapeStart, at - escapeStart);
            return ch;
        }

        private int UnicodeEscape(bool report)
        {
            at += 2;
            bool extended = Ch() == '{';
            if (extended)
                at++;
            int digitsStart = at, value = 0;
            while (extended || at - digitsStart < 4)
            {
                int digit = HexDigit(Ch());
                if (digit < 0)
                    break;
                value = Math.Min(0x110000, value * 16 + digit);
                at++;
            }
            if (at - digitsStart < (extended ? 1 : 4))
            {
                if (report)
                    Error(Messages.Hexadecimal_digit_expected, at);
                return -1;
            }
            if (!extended)
                return value;
            bool invalid = value > 0x10FFFF;
            if (invalid && report)
                Error(Messages.An_extended_Unicode_escape_value_must_be_between_0x0_and_0x10FFFF_inclusive, digitsStart, at - digitsStart);
            if (Ch() == '}')
                at++;
            else
            {
                if (report)
                    Error(at == end ? Messages.Unexpected_end_of_text : Messages.Unterminated_Unicode_escape_sequence, at);
                invalid = true;
            }
            return invalid ? -1 : value;
        }

        private int SourceCharacter()
        {
            int point = Point(out int width);
            if (point < 0)
                return point;
            if (!unicode)
            {
                if (pendingLowSurrogate != 0)
                {
                    int low = pendingLowSurrogate;
                    pendingLowSurrogate = 0;
                    at += width;
                    return low;
                }
                if (point == 0xFFFD)
                    return scanner.text[at++];
                if (point > 0xFFFF)
                {
                    pendingLowSurrogate = 0xDC00 + (point - 0x10000 & 0x3FF);
                    return 0xD800 + (point - 0x10000 >> 10);
                }
            }
            at += width;
            return point;
        }

        private int ClassAtom()
        {
            if (Ch() != '\\')
                return SourceCharacter();
            at++;
            int ch = Ch();
            if (ch is 'b' or '-')
            {
                at++;
                return ch == 'b' ? 8 : ch;
            }
            return CharacterClassEscape(out _) ? -1 : CharacterEscape(false);
        }

        private void ClassRanges()
        {
            pendingLowSurrogate = 0;
            if (Ch() == '^')
                at++;
            while (at < end && Ch() != ']')
            {
                int minStart = at, min = ClassAtom();
                if (Ch() != '-')
                    continue;
                at++;
                if (at == end || Ch() == ']')
                    return;
                if (min == -1 && unicode)
                    Error(Messages.A_character_class_range_must_not_be_bounded_by_another_character_class, minStart, at - 1 - minStart);
                int maxStart = at, max = ClassAtom();
                if (max == -1 && unicode)
                    Error(Messages.A_character_class_range_must_not_be_bounded_by_another_character_class, maxStart, at - maxStart);
                else if (min >= 0 && max >= 0 && min > max)
                    Error(Messages.Range_out_of_order_in_character_class, minStart, at - minStart);
            }
        }

        private ReadOnlySpan<byte> Word()
        {
            int wordStart = at;
            while (Ch() is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
                at++;
            return scanner.input.AsSpan().Slice(wordStart, at - wordStart);
        }

        private bool CharacterClassEscape(out bool strings)
        {
            strings = false;
            int escapeStart = at - 1, ch = Ch();
            if (ch is 'd' or 'D' or 's' or 'S' or 'w' or 'W')
            {
                at++;
                return true;
            }
            if (ch is not ('p' or 'P'))
                return false;
            at++;
            if (Ch() != '{')
            {
                if (unicode)
                    Error(
                        Messages.X_0_must_be_followed_by_a_Unicode_property_value_expression_enclosed_in_braces,
                        escapeStart,
                        2,
                        Utf8String.FromCodePoint(ch));
                else
                {
                    at--;
                    return false;
                }
                return true;
            }
            at++;
            int propertyStart = at;
            ReadOnlySpan<byte> property = Word();
            if (Ch() == '=')
            {
                RegularExpressionUnicodeProperties.NonBinary.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(property, out Utf8String canonical);
                if (property.Length == 0)
                    Error(Messages.Expected_a_Unicode_property_name, at);
                else if (canonical.IsEmpty)
                {
                    Error(Messages.Unknown_Unicode_property_name, propertyStart, at - propertyStart);
                    Suggest(property, RegularExpressionUnicodeProperties.NonBinary.Keys, propertyStart, at - propertyStart);
                }
                int valueStart = ++at;
                ReadOnlySpan<byte> value = Word();
                if (value.Length == 0)
                    Error(Messages.Expected_a_Unicode_property_value, at);
                else if (!canonical.IsEmpty)
                {
                    var values = canonical == Utf8Literals.GeneralCategory
                        ? RegularExpressionUnicodeProperties.GeneralCategory
                        : RegularExpressionUnicodeProperties.Script;
                    if (!values.GetAlternateLookup<ReadOnlySpan<byte>>().Contains(value))
                    {
                        Error(Messages.Unknown_Unicode_property_value, valueStart, at - valueStart);
                        Suggest(value, values, valueStart, at - valueStart);
                    }
                }
            }
            else if (property.Length == 0)
                Error(Messages.Expected_a_Unicode_property_name_or_value, at);
            else if (RegularExpressionUnicodeProperties.Strings.GetAlternateLookup<ReadOnlySpan<byte>>().Contains(property))
            {
                if (!sets)
                    Error(
                        Messages.Any_Unicode_property_that_would_possibly_match_more_than_a_single_character_is_only_available_when_the_Unicode_Sets_v_flag_is_set,
                        propertyStart,
                        at - propertyStart);
                else if (ch == 'P')
                    Error(
                        Messages.Anything_that_would_possibly_match_more_than_a_single_character_is_invalid_inside_a_negated_character_class,
                        propertyStart,
                        at - propertyStart);
                else
                    strings = true;
            }
            else if (!RegularExpressionUnicodeProperties.GeneralCategory.GetAlternateLookup<ReadOnlySpan<byte>>().Contains(property)
                && !RegularExpressionUnicodeProperties.Binary.GetAlternateLookup<ReadOnlySpan<byte>>().Contains(property))
            {
                Error(Messages.Unknown_Unicode_property_name_or_value, propertyStart, at - propertyStart);
                Suggest(
                    property,
                    RegularExpressionUnicodeProperties.GeneralCategory
                        .Concat(RegularExpressionUnicodeProperties.Binary)
                        .Concat(RegularExpressionUnicodeProperties.Strings),
                    propertyStart,
                    at - propertyStart);
            }
            Expected('}');
            if (!unicode)
                Error(
                    Messages.Unicode_property_value_expressions_are_only_available_when_the_Unicode_u_flag_or_the_Unicode_Sets_v_flag_is_set,
                    escapeStart,
                    at - escapeStart);
            return true;
        }

        private void Suggest(ReadOnlySpan<byte> name, IEnumerable<Utf8String> candidates, int location, int length)
        {
            if (RegularExpressionUnicodeProperties.Suggest(name, candidates) is { } suggestion)
                Error(Messages.Did_you_mean_0, location, length, suggestion);
        }
    }
}

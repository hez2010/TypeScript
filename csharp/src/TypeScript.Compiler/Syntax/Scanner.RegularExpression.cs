using System.Globalization;
using System.Numerics;
using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Scanner
{
    public int TargetYear { get; set; } = int.MaxValue;

    private void ValidateRegularExpression(int flagsStart)
    {
        ReadOnlySpan<char> flags = text.AsSpan(flagsStart, pos - flagsStart);
        int seen = 0;
        for (int i = 0; i < flags.Length; i++)
        {
            int flag = "dgimsuvy".IndexOf(flags[i]);
            if (flag < 0) { Error(Messages.Unknown_regular_expression_flag, flagsStart + i, 1); continue; }
            if ((seen & (1 << flag)) != 0) { Error(Messages.Duplicate_regular_expression_flag, flagsStart + i, 1); continue; }
            if (flags[i] is 'u' or 'v' && (seen & ((1 << 5) | (1 << 6))) != 0)
            { Error(Messages.The_Unicode_u_flag_and_the_Unicode_Sets_v_flag_cannot_be_set_simultaneously, flagsStart + i, 1); continue; }
            seen |= 1 << flag;
            int year = flags[i] switch { 'u' or 'y' => 2015, 's' => 2018, 'd' => 2022, 'v' => 2024, _ => 0 };
            if (TargetYear < year) Error(Messages.This_regular_expression_flag_is_only_available_when_targeting_0_or_later, flagsStart + i, 1, "es" + year);
        }
        var validator = new RegularExpressionValidator(this, TokenStart + 1, flagsStart - 1, (seen & 0x60) != 0, (seen & 0x40) != 0);
        validator.Validate();
    }

    // ECMAScript grammar differs from System.Text.RegularExpressions. In particular,
    // .NET's regex parser cannot validate Unicode sets, Annex B, or JS backreferences.
    private sealed class RegularExpressionValidator(Scanner scanner, int start, int end, bool unicode, bool sets)
    {
        private int at = start;
        private int captures;
        private readonly HashSet<string> names = new(StringComparer.Ordinal);
        private readonly List<(string Name, int Start, int End)> namedReferences = [];
        private readonly List<(BigInteger Number, int Start, int End)> references = [];
        private int Ch(int offset = 0) => at + offset < end ? scanner.text[at + offset] : -1;
        private void Error(DiagnosticMessage message, int location, int length = 0, params string[] args) => scanner.Error(message, location, length, args);
        public void Validate()
        {
            var groups = new Stack<int>();
            bool atom = false;
            while (at < end)
            {
                int before = at, ch = Ch();
                switch (ch)
                {
                    case '(':
                        groups.Push(at++); atom = false;
                        if (Ch() != '?') { captures++; break; }
                        at++;
                        if (Ch() is ':' or '=' or '!') { at++; break; }
                        if (Ch() == '<')
                        {
                            at++;
                            if (Ch() is '=' or '!') { at++; break; }
                            captures++;
                            int nameStart = at;
                            string name = Name();
                            if (!names.Add(name)) Error(Messages.Named_capturing_groups_with_the_same_name_must_be_mutually_exclusive_to_each_other, nameStart, at - nameStart);
                            if (scanner.TargetYear < 2018) Error(Messages.Named_capturing_groups_are_only_available_when_targeting_ES2018_or_later, nameStart, at - nameStart);
                            Expected('>');
                            break;
                        }
                        SubpatternFlags();
                        Expected(':');
                        break;
                    case ')':
                        if (!groups.TryPop(out _)) Error(Messages.Unexpected_0_Did_you_mean_to_escape_it_with_backslash, at, 1, ")");
                        at++; atom = true; break;
                    case '[': CharacterClass(); atom = true; break;
                    case '\\': Escape(false); atom = true; break;
                    case '|': at++; atom = false; break;
                    case '^' or '$': at++; atom = false; break;
                    case '*' or '+' or '?':
                        at++;
                        if (!atom) Error(Messages.There_is_nothing_available_for_repetition, before, 1);
                        if (Ch() == '?') at++;
                        atom = false; break;
                    case '{':
                        at++;
                        if (!TokenFacts.IsDigit(Ch()))
                        {
                            if (unicode) Error(Messages.Unexpected_0_Did_you_mean_to_escape_it_with_backslash, before, 1, "{");
                            atom = true; break;
                        }
                        int digitsStart = at;
                        BigInteger min = Decimal();
                        BigInteger? max = min;
                        if (Ch() == ',') { at++; max = TokenFacts.IsDigit(Ch()) ? Decimal() : null; }
                        if (Ch() != '}' && !unicode) { at = before + 1; atom = true; break; }
                        if (max < min) Error(Messages.Numbers_out_of_order_in_quantifier, digitsStart, at - digitsStart);
                        Expected('}');
                        if (!atom) Error(Messages.There_is_nothing_available_for_repetition, before, at - before);
                        if (Ch() == '?') at++;
                        atom = false; break;
                    case ']' or '}':
                        if (unicode) Error(Messages.Unexpected_0_Did_you_mean_to_escape_it_with_backslash, at, 1, ((char)ch).ToString());
                        at++; atom = true; break;
                    default: at++; atom = true; break;
                }
            }
            if (groups.Count != 0) Error(Messages.X_0_expected, end, 0, ")");
            foreach (var reference in namedReferences)
                if (!names.Contains(reference.Name)) Error(Messages.There_is_no_capturing_group_named_0_in_this_regular_expression, reference.Start, reference.End - reference.Start, reference.Name);
            if (unicode)
                foreach (var reference in references)
                    if (reference.Number > captures) Error(captures == 0 ? Messages.This_backreference_refers_to_a_group_that_does_not_exist_There_are_no_capturing_groups_in_this_regular_expression : Messages.This_backreference_refers_to_a_group_that_does_not_exist_There_are_only_0_capturing_groups_in_this_regular_expression,
                        reference.Start, reference.End - reference.Start, captures.ToString(CultureInfo.InvariantCulture));
        }
        private void SubpatternFlags()
        {
            int start = at, seen = 0;
            bool minus = false, afterMinus = false;
            while (Ch() is not (-1 or ':' or ')'))
            {
                int ch = Ch();
                if (ch == '-' && !minus) { minus = true; at++; continue; }
                int bit = "dgimsuvy".IndexOf((char)ch);
                if (bit < 0) { Error(Messages.Unknown_regular_expression_flag, at, 1); at++; continue; }
                if ((seen & (1 << bit)) != 0) Error(Messages.Duplicate_regular_expression_flag, at, 1);
                else if (ch is not ('i' or 'm' or 's')) Error(Messages.This_regular_expression_flag_cannot_be_toggled_within_a_subpattern, at, 1);
                seen |= 1 << bit; afterMinus |= minus; at++;
            }
            if (minus && !afterMinus) Error(Messages.Subpattern_flags_must_be_present_when_there_is_a_minus_sign, start, at - start);
            if (scanner.TargetYear < 2025) Error(Messages.Regular_expression_pattern_modifiers_are_only_available_when_targeting_0_or_later, start, at - start, "es2025");
        }
        private BigInteger Decimal()
        {
            int start = at;
            while (TokenFacts.IsDigit(Ch())) at++;
            return BigInteger.Parse(scanner.text.AsSpan(start, at - start), CultureInfo.InvariantCulture);
        }
        private string Name()
        {
            int start = at;
            while (TokenFacts.IsIdentifierPart(Ch())) at++;
            if (at == start) Error(Messages.Expected_a_capturing_group_name, at);
            return scanner.text[start..at];
        }
        private void Expected(char ch)
        {
            if (Ch() == ch) at++;
            else Error(Messages.X_0_expected, at, 0, ch.ToString());
        }
        private int Escape(bool inClass)
        {
            int start = at++;
            int ch = Ch();
            if (ch < 0) { Error(Messages.Unexpected_end_of_text, at); return -1; }
            at++;
            if (ch is 'd' or 'D' or 's' or 'S' or 'w' or 'W') return -1;
            if (ch is 'b' or 'B' && !inClass) return -1;
            if (ch == 'k' && Ch() == '<')
            {
                at++; int nameStart = at;
                string name = Name();
                namedReferences.Add((name, nameStart, at)); Expected('>'); return -1;
            }
            if (ch is >= '1' and <= '9' && !inClass)
            { at--; BigInteger number = Decimal(); references.Add((number, start, at)); return -1; }
            if (ch is 'p' or 'P')
            {
                int propertyStart = at;
                if (Ch() != '{') { Error(Messages.X_0_must_be_followed_by_a_Unicode_property_value_expression_enclosed_in_braces, start, 2, ((char)ch).ToString()); return -1; }
                at++;
                while (Ch() >= 0 && Ch() != '}') at++;
                if (!unicode) Error(Messages.Unicode_property_value_expressions_are_only_available_when_the_Unicode_u_flag_or_the_Unicode_Sets_v_flag_is_set, start, at - start);
                if (at == propertyStart + 1) Error(Messages.Expected_a_Unicode_property_name_or_value, at);
                Expected('}'); return -1;
            }
            if (ch == 'q' && sets && inClass)
            { Expected('{'); while (Ch() >= 0 && Ch() != '}') { if (Ch() == '\\') Escape(true); else at++; } Expected('}'); return -1; }
            if (ch is 'u' or 'x')
            {
                int savedPos = scanner.pos; TokenFlags savedFlags = scanner.Flags;
                scanner.pos = start;
                string value = scanner.Escape(true);
                at = Math.Min(scanner.pos, end); scanner.pos = savedPos; scanner.Flags = savedFlags;
                return value.Length == 1 ? value[0] : value.Length == 2 && char.IsSurrogatePair(value, 0) ? char.ConvertToUtf32(value, 0) : -1;
            }
            if (ch == 'c')
            {
                int next = Ch();
                if (next is >= 'a' and <= 'z' or >= 'A' and <= 'Z') { at++; return next % 32; }
                if (unicode) Error(Messages.X_c_must_be_followed_by_an_ASCII_letter, start, 2);
                return ch;
            }
            if (ch is 'f' or 'n' or 'r' or 't' or 'v' or 'b') return ch switch { 'f' => 12, 'n' => 10, 'r' => 13, 't' => 9, 'v' => 11, _ => 8 };
            if (unicode && TokenFacts.IsIdentifierPart(ch) && ch != '0') Error(Messages.This_character_cannot_be_escaped_in_a_regular_expression, start, at - start);
            return ch == '0' ? 0 : ch;
        }
        private void CharacterClass()
        {
            // Explicit stack permits arbitrarily nested Unicode sets without native-stack growth.
            var stack = new Stack<(bool Negated, int Operator)>();
            at++;
            bool negated = Ch() == '^'; if (negated) at++;
            int operation = 0, previous = -1, previousStart = at;
            while (at < end)
            {
                int start = at, ch = Ch();
                if (ch == ']')
                {
                    at++;
                    if (!stack.TryPop(out var outer)) return;
                    (negated, operation) = outer; previous = -1; continue;
                }
                if (sets && ch == '[')
                { stack.Push((negated, operation)); at++; negated = Ch() == '^'; if (negated) at++; operation = 0; previous = -1; continue; }
                if (sets && ch is '&' or '-' && Ch(1) == ch)
                {
                    if (operation != 0 && operation != ch) Error(Messages.Operators_must_not_be_mixed_within_a_character_class_Wrap_it_in_a_nested_class_instead, at, 2);
                    operation = ch; at += 2; previous = -1; continue;
                }
                if (ch == '-' && previous >= 0 && Ch(1) != ']' && Ch(1) >= 0)
                {
                    at++;
                    int upper = Ch() == '\\' ? Escape(true) : Ch();
                    if (Ch(-1) != '\\' && scanner.text[at - 1] == '-') at++;
                    if (upper < 0 && unicode) Error(Messages.A_character_class_range_must_not_be_bounded_by_another_character_class, previousStart, at - previousStart);
                    else if (upper >= 0 && upper < previous) Error(Messages.Range_out_of_order_in_character_class, previousStart, at - previousStart);
                    previous = -1; continue;
                }
                previous = ch == '\\' ? Escape(true) : ch;
                if (ch != '\\') at++;
                previousStart = start;
            }
            Error(Messages.X_0_expected, end, 0, "]");
        }
    }
}

using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Scanner
{
    private sealed partial class RegularExpressionValidator
    {
        private enum SetStage
        {
            Start,
            First,
            Union,
            Next,
            Range,
            Operand,
            Sub,
            SubOperand,
            Done
        }

        private sealed class SetFrame
        {
            public SetStage Stage;
            public bool Negated;
            public bool Strings;
            public int Operator;
            public int Start;
            public int Operand;
            public int RangeStart;
        }

        private bool ClassExit => at >= end || Ch() == ']';

        private void Mixed(int location, int length) =>
            Error(Messages.Operators_must_not_be_mixed_within_a_character_class_Wrap_it_in_a_nested_class_instead, location, length);

        private void NegatedStrings(SetFrame frame, bool strings, int location)
        {
            if (frame.Negated && strings)
                Error(
                    Messages.Anything_that_would_possibly_match_more_than_a_single_character_is_invalid_inside_a_negated_character_class,
                    location,
                    at - location);
        }

        private void ClassSet()
        {
            var stack = new Stack<SetFrame>();
            stack.Push(new());
            int operand = -1;
            bool strings = false;
            while (stack.Count != 0)
            {
                SetFrame frame = stack.Peek();
                switch (frame.Stage)
                {
                    case SetStage.Start:
                        frame.Negated = Ch() == '^';
                        if (frame.Negated)
                            at++;
                        frame.Start = at;
                        if (ClassExit)
                        {
                            frame.Stage = SetStage.Done;
                            break;
                        }
                        if (Ch() is '-' or '&' && Ch(1) == Ch())
                        {
                            Error(Messages.Expected_a_class_set_operand, at);
                            operand = -1;
                            strings = false;
                            frame.Stage = SetStage.First;
                        }
                        else
                            ReadSetOperand(frame, SetStage.First);
                        break;
                    case SetStage.First:
                        frame.Operand = operand;
                        frame.Strings = strings;
                        if (Ch() is '-' or '&' && Ch(1) == Ch())
                        {
                            frame.Operator = Ch();
                            if (frame.Operator == '-')
                                NegatedStrings(frame, strings, frame.Start);
                            frame.Stage = SetStage.Sub;
                        }
                        else
                        {
                            NegatedStrings(frame, strings, frame.Start);
                            frame.Stage = SetStage.Union;
                        }
                        break;
                    case SetStage.Union:
                        if (at >= end)
                        {
                            frame.Stage = SetStage.Done;
                            break;
                        }
                        if (Ch() == '-')
                        {
                            at++;
                            if (ClassExit)
                            {
                                Unexpected('-', at - 1);
                                frame.Stage = SetStage.Done;
                                break;
                            }
                            if (Ch() == '-')
                            {
                                at++;
                                Mixed(at - 2, 2);
                                frame.Start = at - 2;
                                frame.Operand = -2;
                                break;
                            }
                            if (frame.Operand == -1)
                                Error(
                                    Messages.A_character_class_range_must_not_be_bounded_by_another_character_class,
                                    frame.Start,
                                    at - 1 - frame.Start);
                            frame.RangeStart = at;
                            ReadSetOperand(frame, SetStage.Range);
                        }
                        else if (Ch() == '&' && Ch(1) == '&')
                        {
                            frame.Start = at;
                            at += 2;
                            Mixed(at - 2, 2);
                            if (Ch() == '&')
                                Unexpected('&', at++);
                            frame.Operand = -2;
                        }
                        else
                            frame.Stage = SetStage.Next;
                        break;
                    case SetStage.Range:
                        NegatedStrings(frame, strings, frame.RangeStart);
                        frame.Strings |= strings;
                        if (operand == -1)
                            Error(
                                Messages.A_character_class_range_must_not_be_bounded_by_another_character_class,
                                frame.RangeStart,
                                at - frame.RangeStart);
                        else if (frame.Operand >= 0 && operand >= 0 && frame.Operand > operand)
                            Error(Messages.Range_out_of_order_in_character_class, frame.Start, at - frame.Start);
                        frame.Stage = SetStage.Next;
                        break;
                    case SetStage.Next:
                        if (ClassExit)
                        {
                            frame.Stage = SetStage.Done;
                            break;
                        }
                        frame.Start = at;
                        if (Ch() is '-' or '&' && Ch(1) == Ch())
                        {
                            Mixed(at, 2);
                            at += 2;
                            frame.Operand = -2;
                            frame.Stage = SetStage.Union;
                        }
                        else
                            ReadSetOperand(frame, SetStage.Operand);
                        break;
                    case SetStage.Operand:
                        frame.Operand = operand;
                        NegatedStrings(frame, strings, frame.Start);
                        frame.Strings |= strings;
                        frame.Stage = SetStage.Union;
                        break;
                    case SetStage.Sub:
                        if (ClassExit)
                        {
                            if (frame.Operator == '&')
                                NegatedStrings(frame, frame.Strings, frame.Start);
                            frame.Stage = SetStage.Done;
                            break;
                        }
                        int ch = Ch();
                        if (ch == '-')
                        {
                            at++;
                            if (Ch() == '-')
                            {
                                at++;
                                if (frame.Operator != '-')
                                    Mixed(at - 2, 2);
                            }
                            else
                                Mixed(at - 1, 1);
                        }
                        else if (ch == '&')
                        {
                            at++;
                            if (Ch() == '&')
                            {
                                at++;
                                if (frame.Operator != '&')
                                    Mixed(at - 2, 2);
                                if (Ch() == '&')
                                    Unexpected('&', at++);
                            }
                            else
                                Unexpected('&', at - 1);
                        }
                        else
                            Error(Messages.X_0_expected, at, 0, frame.Operator == '-' ? "--" : "&&");
                        if (ClassExit)
                        {
                            Error(Messages.Expected_a_class_set_operand, at);
                            if (frame.Operator == '&')
                                NegatedStrings(frame, frame.Strings, frame.Start);
                            frame.Stage = SetStage.Done;
                        }
                        else
                            ReadSetOperand(frame, SetStage.SubOperand);
                        break;
                    case SetStage.SubOperand:
                        if (frame.Operator == '&')
                            frame.Strings &= strings;
                        frame.Stage = SetStage.Sub;
                        break;
                    case SetStage.Done:
                        stack.Pop();
                        operand = -1;
                        strings = !frame.Negated && frame.Strings;
                        if (stack.Count != 0)
                            Expected(']');
                        break;
                }
            }
            void ReadSetOperand(SetFrame frame, SetStage continuation)
            {
                frame.Stage = continuation;
                strings = false;
                if (Ch() == '[')
                {
                    at++;
                    stack.Push(new());
                    return;
                }
                if (Ch() == '\\')
                {
                    at++;
                    if (CharacterClassEscape(out strings))
                    {
                        operand = -1;
                        return;
                    }
                    if (Ch() == 'q')
                    {
                        at++;
                        if (Ch() == '{')
                        {
                            at++;
                            strings = ClassStrings();
                            Expected('}');
                            operand = -1;
                        }
                        else
                        {
                            Error(Messages.X_q_must_be_followed_by_string_alternatives_enclosed_in_braces, at - 2, 2);
                            operand = 'q';
                        }
                        return;
                    }
                    at--;
                }
                operand = SetCharacter();
            }
        }

        private bool ClassStrings()
        {
            int count = 0;
            bool strings = false;
            while (at < end)
            {
                if (Ch() == '}')
                    return strings || count != 1;
                if (Ch() == '|')
                {
                    strings |= count != 1;
                    count = 0;
                    at++;
                }
                else
                {
                    SetCharacter();
                    count++;
                }
            }
            return strings;
        }

        private int SetCharacter()
        {
            int ch = Ch();
            if (ch == '\\')
            {
                at++;
                ch = Ch();
                if (ch == 'b')
                {
                    at++;
                    return 8;
                }
                if (ch is '&' or '-' or '!' or '#' or '%' or ',' or ':' or ';' or '<' or '=' or '>' or '@' or '`' or '~')
                {
                    at++;
                    return ch;
                }
                return CharacterEscape(false);
            }
            if (ch == Ch(1)
                && ch is '&' or '!' or '#' or '%' or '*' or '+' or ',' or '.' or ':' or ';' or '<' or '=' or '>' or '?' or '@' or '`'
                    or '~')
            {
                Error(
                    Messages.A_character_class_must_not_contain_a_reserved_double_punctuator_Did_you_mean_to_escape_it_with_backslash,
                    at,
                    2);
                at += 2;
                return -2;
            }
            if (ch is '/' or '(' or ')' or '[' or ']' or '{' or '}' or '-' or '|')
            {
                Unexpected(ch, at++);
                return ch;
            }
            return SourceCharacter();
        }
    }
}

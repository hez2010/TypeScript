using System.Buffers;
using System.Text;

namespace TypeScript.Compiler.Semantics;

// Boolean matching for the Go regexp syntax used by module-specifier exclusions.
// Thompson states and explicit parse/compile stacks avoid backtracking and CLR
// recursion. Text is decoded like Go, including one RuneError per invalid byte.
internal sealed partial class GoRegex
{
    private enum Op { Empty, Rune, Concat, Alternate, Repeat, Capture, Begin, End, BeginLine, EndLine, Boundary, NonBoundary, Split, Match }
    private sealed record Part(int[] Ranges, bool Fold = false, bool Negate = false)
    {
        internal bool Matches(int point)
        {
            bool match = Contains(point);
            if (!match && Fold)
                for (int next = GoUnicode.Fold(point); next != point; next = GoUnicode.Fold(next))
                    if (Contains(next)) { match = true; break; }
            return match != Negate;
        }
        private bool Contains(int point)
        {
            int low = 0, high = Ranges.Length / 2;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (point < Ranges[middle * 2]) high = middle;
                else if (point > Ranges[middle * 2 + 1]) low = middle + 1;
                else return true;
            }
            return false;
        }
    }
    private sealed record CharacterClass(Part[] Parts, bool Negate = false)
    {
        internal bool Matches(int point) => Parts.Any(part => part.Matches(point)) != Negate;
    }
    private sealed class Expression(Op op, Expression[]? children = null, CharacterClass? characters = null, int min = 0, int max = 0)
    {
        internal readonly Op Op = op;
        internal readonly Expression[] Children = children ?? [];
        internal readonly CharacterClass? Characters = characters;
        internal readonly int Min = min, Max = max;
        internal readonly int Height = 1 + (children is { Length: > 0 } ? children.Max(child => child.Height) : 0);
        internal readonly int Repetitions = op == Op.Repeat ? max == 0 ? 0 : Math.Max(1, Math.Max(min, max)) * (children![0].Repetitions)
            : children is { Length: > 0 } ? children.Max(child => child.Repetitions) : 1;
    }
    private readonly record struct Instruction(Op Op, int Next = 0, int Other = 0, CharacterClass? Characters = null);
    private readonly Instruction[] instructions;
    private readonly Utf8String literalPrefix;
    private readonly int prefixEnd, prefixLength;
    private sealed class InvalidPattern : Exception;

    internal static GoRegex? Parse(Utf8String pattern, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            List<int> points = [];
            for (int offset = 0; offset < pattern.Length;)
            {
                if ((points.Count & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                if (Rune.DecodeFromUtf8(pattern.Span[offset..], out var rune, out int width) != OperationStatus.Done) return null;
                points.Add(rune.Value); offset += width;
            }
            return new(new Parser(points.ToArray(), cancellation).Parse(), cancellation);
        }
        catch (InvalidPattern) { return null; }
    }

    internal static GoRegex? FromSpecifierPattern(Utf8String raw, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (raw.Length > 2 && raw[0] == '/' && raw.Span.LastIndexOf((byte)'/') is var last && last > 0)
        {
            bool middleSlash = false;
            for (int i = 1; i < last; i++) if (raw[i] == '/' && raw[i - 1] != '\\') { middleSlash = true; break; }
            if (!middleSlash)
            {
                bool ignoreCase = raw.Span[(last + 1)..].Contains((byte)'i');
                return Parse(ignoreCase ? Utf8String.ConcatMany("(?i:"u8, raw[1..last], ")"u8) : raw[1..last], cancellation);
            }
        }
        return Parse(raw, cancellation);
    }

    private GoRegex(Expression expression, CancellationToken cancellation)
    {
        expression = FactorPrefixes(expression, cancellation);
        List<Instruction> code = [new(Op.Match), default];
        Stack<(Expression Expression, int Start, int End)> pending = new();
        pending.Push((expression, 1, 0));
        while (pending.TryPop(out var task))
        {
            cancellation.ThrowIfCancellationRequested();
            var (node, start, end) = task;
            switch (node.Op)
            {
                case Op.Capture:
                    int captureStart = Allocate(), captureEnd = Allocate();
                    code[start] = new(Op.Capture, captureStart); code[captureEnd] = new(Op.Capture, end);
                    pending.Push((node.Children[0], captureStart, captureEnd)); break;
                case Op.Concat:
                    for (int i = 0; i < node.Children.Length; i++)
                    {
                        int next = i == node.Children.Length - 1 ? end : Allocate();
                        pending.Push((node.Children[i], start, next)); start = next;
                    }
                    break;
                case Op.Alternate:
                    for (int i = 0; i < node.Children.Length - 1; i++)
                    {
                        int child = Allocate(), next = Allocate();
                        code[start] = new(Op.Split, child, next); pending.Push((node.Children[i], child, end)); start = next;
                    }
                    pending.Push((node.Children[^1], start, end)); break;
                case Op.Repeat:
                    for (int i = 0; i < node.Min; i++)
                    {
                        int next = Allocate(); pending.Push((node.Children[0], start, next)); start = next;
                    }
                    if (node.Max < 0)
                    {
                        int child = Allocate(); code[start] = new(Op.Split, child, end); pending.Push((node.Children[0], child, start));
                    }
                    else
                    {
                        for (int i = node.Min; i < node.Max; i++)
                        {
                            int child = Allocate(), next = Allocate();
                            code[start] = new(Op.Split, child, end); pending.Push((node.Children[0], child, next)); start = next;
                        }
                        code[start] = new(Op.Empty, end);
                    }
                    break;
                default: code[start] = new(node.Op, end, Characters: node.Characters); break;
            }
        }
        instructions = code.ToArray();
        (literalPrefix, prefixEnd, prefixLength) = LiteralPrefix();
        int Allocate()
        {
            // The reference bounds compiled programs to 128 MiB / 40-byte Inst.
            if (code.Count >= (128 << 20) / 40) throw new InvalidPattern();
            code.Add(default); return code.Count - 1;
        }
    }

    private static Expression FactorPrefixes(Expression expression, CancellationToken cancellation)
    {
        var root = new Expression(Op.Capture, [expression]);
        Stack<(Expression Parent, int Index)> pending = new(); pending.Push((root, 0));
        while (pending.TryPop(out var task))
        {
            cancellation.ThrowIfCancellationRequested();
            var current = task.Parent.Children[task.Index];
            if (current.Op == Op.Alternate)
            {
                List<Expression> branches = [];
                for (int first = 0; first < current.Children.Length;)
                {
                    var sequence = Sequence(current.Children[first]);
                    int end = first + 1, common = sequence.Length;
                    while (end < current.Children.Length)
                    {
                        var next = Sequence(current.Children[end]); int count = 0;
                        while (count < common && count < next.Length && SameCharacter(sequence[count], next[count])) count++;
                        if (count == 0) break;
                        common = count; end++;
                    }
                    if (end == first + 1) branches.Add(current.Children[first]);
                    else
                    {
                        List<Expression> tails = [];
                        for (int i = first; i < end; i++)
                        {
                            var tail = Sequence(current.Children[i])[common..];
                            tails.Add(tail.Length switch { 0 => new(Op.Empty), 1 => tail[0], _ => new(Op.Concat, tail) });
                        }
                        branches.Add(new(Op.Concat, [.. sequence[..common], new(Op.Alternate, tails.ToArray())]));
                    }
                    first = end;
                }
                current = branches.Count == 1 ? branches[0] : new(Op.Alternate, branches.ToArray());
                if (current.Op == Op.Alternate)
                {
                    List<Expression> collapsed = [];
                    for (int i = 0; i < current.Children.Length;)
                    {
                        if (current.Children[i].Op == Op.Rune)
                        {
                            int start = i;
                            while (i < current.Children.Length && current.Children[i].Op == Op.Rune) i++;
                            if (i == start + 1) collapsed.Add(current.Children[start]);
                            else collapsed.Add(new(Op.Rune, characters: new(current.Children[start..i]
                                .Select(child => new Part(ClassRanges(child.Characters!).SelectMany(range => new[] { range.Lo, range.Hi }).ToArray())).ToArray())));
                        }
                        else
                        {
                            var child = current.Children[i++];
                            if (child.Op != Op.Empty || collapsed.LastOrDefault()?.Op != Op.Empty) collapsed.Add(child);
                        }
                    }
                    current = collapsed.Count == 1 ? collapsed[0] : new(Op.Alternate, collapsed.ToArray());
                }
                task.Parent.Children[task.Index] = current;
            }
            for (int i = 0; i < current.Children.Length; i++) pending.Push((current, i));
        }
        return root.Children[0];

        static Expression[] Sequence(Expression node) => node.Op == Op.Concat ? node.Children : node.Op == Op.Empty ? [] : [node];
        static bool SameCharacter(Expression a, Expression b)
        {
            if (a.Op != Op.Rune || b.Op != Op.Rune || a.Characters!.Negate != b.Characters!.Negate
                || a.Characters.Parts.Length != b.Characters.Parts.Length) return false;
            for (int i = 0; i < a.Characters.Parts.Length; i++)
            {
                var x = a.Characters.Parts[i]; var y = b.Characters.Parts[i];
                if (x.Fold != y.Fold || x.Negate != y.Negate || !x.Ranges.AsSpan().SequenceEqual(y.Ranges)) return false;
            }
            return true;
        }
    }

    internal bool IsMatch(Utf8String text, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!literalPrefix.IsEmpty && !text.StartsWith(literalPrefix)) return false;
        int[] points = GoUnicode.Runes(text), seen = new int[instructions.Length];
        List<int> next = [], current = [];
        Stack<int> pending = new();
        for (int position = prefixLength; position <= points.Length; position++)
        {
            cancellation.ThrowIfCancellationRequested();
            int before = position == 0 ? -1 : points[position - 1], after = position == points.Length ? -1 : points[position];
            if (literalPrefix.IsEmpty) pending.Push(1);
            else if (position == prefixLength) pending.Push(prefixEnd);
            foreach (int state in current) pending.Push(state);
            int visited = 0;
            while (pending.TryPop(out int state))
            {
                if (seen[state] == position + 1) continue;
                seen[state] = position + 1;
                if ((++visited & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                var instruction = instructions[state];
                switch (instruction.Op)
                {
                    case Op.Match: return true;
                    case Op.Rune: if (after >= 0 && instruction.Characters!.Matches(after)) next.Add(instruction.Next); break;
                    case Op.Split: pending.Push(instruction.Other); pending.Push(instruction.Next); break;
                    case Op.Empty: case Op.Capture: pending.Push(instruction.Next); break;
                    default:
                        bool matches = instruction.Op switch
                        {
                            Op.Begin => before == -1, Op.End => after == -1,
                            Op.BeginLine => before is -1 or '\n', Op.EndLine => after is -1 or '\n',
                            Op.Boundary => Word(before) != Word(after), Op.NonBoundary => Word(before) == Word(after), _ => false
                        };
                        if (matches) pending.Push(instruction.Next);
                        break;
                }
            }
            (current, next) = (next, current); next.Clear();
        }
        return false;
    }

    // Go's one-pass prefix shortcut writes escaped surrogate runes as U+FFFD
    // and checks their UTF-8 bytes before running the remaining instructions.
    // Preserve that observable behavior without changing general rune matching.
    private (Utf8String Prefix, int End, int Length) LiteralPrefix()
    {
        if (instructions[1].Op != Op.Begin) return default;
        int state = instructions[1].Next;
        while (instructions[state].Op == Op.Empty) state = instructions[state].Next;
        var text = new StringBuilder(); int length = 0; bool surrogate = false;
        while (instructions[state] is { Op: Op.Rune, Characters: { Negate: false, Parts: [{ Negate: false, Ranges: [var lo, var hi], Fold: var fold }] } } && lo == hi
            && lo != 0xfffd && (!fold || GoUnicode.Fold(lo) == lo))
        {
            surrogate |= lo is >= 0xd800 and <= 0xdfff;
            text.Append(char.ConvertFromUtf32(lo is >= 0xd800 and <= 0xdfff ? 0xfffd : lo));
            length++; state = instructions[state].Next;
            while (instructions[state].Op == Op.Empty) state = instructions[state].Next;
        }
        if (!surrogate || !OnePass()) return default;
        return (Utf8String.FromString(text.ToString()), state, length);
    }

    private bool OnePass()
    {
        // The reference skips one-pass analysis at 1000 instructions. Empty
        // concatenation/repetition connectors are not instructions in Go.
        if (instructions.Count(instruction => instruction.Op != Op.Empty) + 1 >= 1000) return false;
        bool alternatives = instructions.Any(instruction => instruction.Op == Op.Split);
        int Skip(int state)
        {
            while (instructions[state].Op == Op.Empty) state = instructions[state].Next;
            return state;
        }
        foreach (var instruction in instructions)
        {
            if (instruction.Op is Op.Match or Op.Empty) continue;
            if (instructions[Skip(instruction.Next)].Op == Op.Match)
            {
                if (instruction.Op is Op.Begin or Op.BeginLine or Op.EndLine or Op.Boundary or Op.NonBoundary) return false;
                if (alternatives && instruction.Op != Op.End) return false;
            }
            if (instruction.Op == Op.Split && instructions[Skip(instruction.Other)].Op == Op.Match) return false;
        }
        Dictionary<CharacterClass, (int Lo, int Hi)[]> ranges = [];
        foreach (var instruction in instructions)
        {
            if (instruction.Op != Op.Split) continue;
            var left = First(instruction.Next); var right = First(instruction.Other);
            if (left.Empty && right.Empty) return false;
            foreach (var a in left.Classes) foreach (var b in right.Classes)
                foreach (var x in Ranges(a)) foreach (var y in Ranges(b))
                    if (x.Lo <= y.Hi && y.Lo <= x.Hi) return false;
        }
        return true;

        (bool Empty, HashSet<CharacterClass> Classes) First(int state)
        {
            bool empty = false; HashSet<CharacterClass> classes = []; HashSet<int> seen = [];
            Stack<int> pending = new(); pending.Push(state);
            while (pending.TryPop(out int next))
            {
                if (!seen.Add(next)) continue;
                var instruction = instructions[next];
                if (instruction.Op == Op.Match) empty = true;
                else if (instruction.Op == Op.Rune) classes.Add(instruction.Characters!);
                else { pending.Push(instruction.Next); if (instruction.Op == Op.Split) pending.Push(instruction.Other); }
            }
            return (empty, classes);
        }
        (int Lo, int Hi)[] Ranges(CharacterClass characters)
        {
            if (ranges.TryGetValue(characters, out var cached)) return cached;
            return ranges[characters] = ClassRanges(characters);
        }
    }

    private static (int Lo, int Hi)[] ClassRanges(CharacterClass characters)
    {
        SortedSet<int> boundaries = [0, 0x110000];
        foreach (var part in characters.Parts)
        {
            for (int i = 0; i < part.Ranges.Length; i += 2) { boundaries.Add(part.Ranges[i]); boundaries.Add(part.Ranges[i + 1] + 1); }
            if (part.Fold)
                for (int i = 0; i < GoUnicode.SimpleFoldMappings.Length; i += 2)
                { int point = GoUnicode.SimpleFoldMappings[i]; boundaries.Add(point); boundaries.Add(point + 1); }
        }
        int[] points = boundaries.ToArray(); List<(int, int)> result = [];
        for (int i = 0; i + 1 < points.Length; i++) if (characters.Matches(points[i])) result.Add((points[i], points[i + 1] - 1));
        return result.ToArray();
    }

    private static bool Word(int c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';
    private static int Hex(int c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
    private static int[]? AsciiClass(string name) => name switch
    {
        "alnum" => ['0', '9', 'A', 'Z', 'a', 'z'], "alpha" => ['A', 'Z', 'a', 'z'], "ascii" => [0, 127],
        "blank" => ['\t', '\t', ' ', ' '], "cntrl" => [0, 31, 127, 127], "digit" => ['0', '9'], "graph" => [33, 126],
        "lower" => ['a', 'z'], "print" => [32, 126], "punct" => [33, 47, 58, 64, 91, 96, 123, 126],
        "space" => [9, 13, 32, 32], "upper" => ['A', 'Z'], "word" => ['0', '9', 'A', 'Z', '_', '_', 'a', 'z'],
        "xdigit" => ['0', '9', 'A', 'F', 'a', 'f'], _ => null
    };

    private sealed class Frame(int flags, bool capture = false)
    {
        internal int Flags = flags;
        internal readonly bool Capture = capture;
        internal readonly List<Expression> Items = [], Branches = [];
        internal void Branch() { Branches.Add(Join(Op.Concat, Items)); Items.Clear(); }
        internal Expression Finish() { Branch(); return Join(Op.Alternate, Branches); }
        private static Expression Join(Op op, List<Expression> children)
        {
            var flattened = children.SelectMany(child => child.Op == op ? child.Children : [child]).ToArray();
            return flattened.Length switch { 0 => new(Op.Empty), 1 => flattened[0], _ => new(op, flattened) };
        }
    }

    private sealed class Parser(int[] pattern, CancellationToken cancellation)
    {
        private int position;
        private long runeCount;
        private Frame frame = new(0);
        private readonly Stack<Frame> stack = new();
        private bool Fold => (frame.Flags & 1) != 0;
        private int Peek(int offset = 0) => position + offset < pattern.Length ? pattern[position + offset] : -1;
        private int Read()
        {
            if ((position & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            return position < pattern.Length ? pattern[position++] : throw new InvalidPattern();
        }
        private void Add(Expression expression)
        {
            if (expression.Height > 1000) throw new InvalidPattern();
            if (expression.Characters is { } characters)
            {
                runeCount += characters.Parts.Sum(part => part.Ranges.Length);
                if (runeCount > (128 << 20) / 4) throw new InvalidPattern();
            }
            frame.Items.Add(expression);
        }
        private void Literal(int point) => Add(new(Op.Rune, characters: new([new([point, point], Fold)])));

        internal Expression Parse()
        {
            bool lastRepeat = false;
            while (position < pattern.Length)
            {
                int c = Read();
                bool repeat = false;
                switch (c)
                {
                    case '(':
                        Open(); break;
                    case ')':
                        if (stack.Count == 0) throw new InvalidPattern();
                        var inner = frame.Finish(); bool capture = frame.Capture;
                        frame = stack.Pop(); Add(capture ? new(Op.Capture, [inner]) : inner); break;
                    case '|': frame.Branch(); break;
                    case '^': Add(new((frame.Flags & 2) == 0 ? Op.Begin : Op.BeginLine)); break;
                    case '$': Add(new((frame.Flags & 2) == 0 ? Op.End : Op.EndLine)); break;
                    case '.': Add(new(Op.Rune, characters: new([new((frame.Flags & 4) == 0 ? [0, 9, 11, 0x10ffff] : [0, 0x10ffff])]))); break;
                    case '[': Add(new(Op.Rune, characters: Class())); break;
                    case '*': case '+': case '?':
                        Repeat(c == '+' ? 1 : 0, c == '?' ? 1 : -1, lastRepeat, false); repeat = true; break;
                    case '{':
                        if (Count(out int min, out int max)) { Repeat(min, max, lastRepeat, true); repeat = true; }
                        else Literal(c);
                        break;
                    case '\\':
                        if (Peek() == 'Q')
                        {
                            position++;
                            while (position < pattern.Length && !(Peek() == '\\' && Peek(1) == 'E')) Literal(Read());
                            if (position < pattern.Length) position += 2;
                        }
                        else if (Peek() is 'A' or 'z' or 'b' or 'B')
                            Add(new(Read() switch { 'A' => Op.Begin, 'z' => Op.End, 'b' => Op.Boundary, _ => Op.NonBoundary }));
                        else if (Group() is { } group) Add(new(Op.Rune, characters: new([group])));
                        else Literal(Escape());
                        break;
                    default: Literal(c); break;
                }
                lastRepeat = repeat;
            }
            if (stack.Count != 0) throw new InvalidPattern();
            var result = frame.Finish();
            if (result.Height > 1000) throw new InvalidPattern();
            return result;
        }

        private void Open()
        {
            int flags = frame.Flags;
            bool capture = true;
            if (Peek() == '?')
            {
                position++;
                if (Peek() == '<' || Peek() == 'P' && Peek(1) == '<')
                {
                    if (Read() == 'P') position++;
                    int start = position;
                    while (Word(Peek())) position++;
                    if (position == start || Read() != '>') throw new InvalidPattern();
                }
                else
                {
                    capture = false;
                    bool negative = false, sawFlag = false;
                    for (;;)
                    {
                        int c = Read();
                        if (c is ':' or ')')
                        {
                            if (negative && !sawFlag) throw new InvalidPattern();
                            if (c == ')') { frame.Flags = flags; return; }
                            break;
                        }
                        if (c == '-' && !negative) { negative = true; sawFlag = false; continue; }
                        int bit = c switch { 'i' => 1, 'm' => 2, 's' => 4, 'U' => 8, _ => throw new InvalidPattern() };
                        if (negative) flags &= ~bit; else flags |= bit;
                        sawFlag = true;
                    }
                }
            }
            stack.Push(frame); frame = new(flags, capture);
        }

        private bool Count(out int min, out int max)
        {
            int saved = position;
            min = max = 0;
            if (!Number(out min)) { position = saved; return false; }
            max = min;
            if (Peek() == ',')
            {
                position++;
                if (Peek() == '}') max = -1;
                else if (!Number(out max)) { position = saved; return false; }
            }
            if (Peek() != '}') { position = saved; return false; }
            position++;
            if (min > 1000 || max > 1000 || max >= 0 && min > max) throw new InvalidPattern();
            return true;
        }
        private bool Number(out int result)
        {
            result = 0;
            if (Peek() is < '0' or > '9' || Peek() == '0' && Peek(1) is >= '0' and <= '9') return false;
            while (Peek() is >= '0' and <= '9') result = Math.Min(1001, result * 10 + Read() - '0');
            return true;
        }
        private void Repeat(int min, int max, bool lastRepeat, bool counted)
        {
            if (frame.Items.Count == 0 || lastRepeat) throw new InvalidPattern();
            if (Peek() == '?') position++;
            var child = frame.Items[^1]; frame.Items.RemoveAt(frame.Items.Count - 1);
            var expression = new Expression(Op.Repeat, [child], min: min, max: max);
            if (counted && Math.Max(min, max) >= 2 && expression.Repetitions > 1000) throw new InvalidPattern();
            Add(expression);
        }

        private int Escape()
        {
            int c = Read();
            if (c is >= '0' and <= '7')
            {
                if (c != '0' && Peek() is not (>= '0' and <= '7')) throw new InvalidPattern();
                int value = c - '0';
                for (int i = 1; i < 3 && Peek() is >= '0' and <= '7'; i++) value = value * 8 + Read() - '0';
                return value;
            }
            if (c == 'x')
            {
                if (Peek() == '{')
                {
                    position++; int value = 0, count = 0;
                    while (Hex(Peek()) >= 0)
                    {
                        value = value * 16 + Hex(Read()); count++;
                        if (value > 0x10ffff) throw new InvalidPattern();
                    }
                    if (Read() != '}' || count == 0) throw new InvalidPattern();
                    return value;
                }
                int first = Hex(Read()), second = Hex(Read());
                if (first < 0 || second < 0) throw new InvalidPattern();
                return first * 16 + second;
            }
            return c switch
            {
                'a' => 7, 'f' => 12, 'n' => 10, 'r' => 13, 't' => 9, 'v' => 11,
                _ when c < 128 && !Word(c) || c == '_' => c, _ => throw new InvalidPattern()
            };
        }

        // Called after the backslash; property folding precedes negation.
        private Part? Group()
        {
            int c = Peek();
            if (c is 'p' or 'P')
            {
                position++; string name;
                if (Peek() == '{')
                {
                    position++; var builder = new StringBuilder();
                    while (Peek() != '}' && Peek() != -1) builder.Append(char.ConvertFromUtf32(Read()));
                    if (Read() != '}') throw new InvalidPattern();
                    name = builder.ToString();
                }
                else name = char.ConvertFromUtf32(Read());
                bool negate = c == 'P';
                if (name.StartsWith('^')) { negate = !negate; name = name[1..]; }
                return new(Property(name, Fold) ?? throw new InvalidPattern(), Negate: negate);
            }
            int[]? ranges = c switch
            {
                'd' or 'D' => ['0', '9'], 'w' or 'W' => AsciiClass("word"), 's' or 'S' => [9, 10, 12, 13, 32, 32], _ => null
            };
            if (ranges is null) return null;
            position++; return new(ranges, Fold, c is 'D' or 'W' or 'S');
        }
        private CharacterClass Class()
        {
            bool negate = Peek() == '^'; if (negate) position++;
            List<Part> parts = [];
            bool first = true;
            while (Peek() != ']' || first)
            {
                first = false;
                if (Peek() == '[' && Peek(1) == ':')
                {
                    int end = position + 2;
                    while (end + 1 < pattern.Length && !(pattern[end] == ':' && pattern[end + 1] == ']')) end++;
                    if (end + 1 < pattern.Length)
                    {
                        position += 2; bool inverse = Peek() == '^'; if (inverse) position++;
                        var name = new StringBuilder(); while (position < end) name.Append(char.ConvertFromUtf32(Read()));
                        position = end + 2;
                        parts.Add(new(AsciiClass(name.ToString()) ?? throw new InvalidPattern(), Fold, inverse)); continue;
                    }
                }
                if (Peek() == '\\')
                {
                    position++;
                    if (Group() is { } group) { parts.Add(group); continue; }
                    position--;
                }
                int lo = Read() is var c && c == '\\' ? Escape() : c, hi = lo;
                if (Peek() == '-' && Peek(1) != ']' && Peek(1) != -1)
                {
                    position++; hi = Read() is var last && last == '\\' ? Escape() : last;
                    if (hi < lo) throw new InvalidPattern();
                }
                parts.Add(new([lo, hi], Fold));
            }
            position++; return new(parts.ToArray(), negate);
        }
    }
}

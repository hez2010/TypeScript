using System.Collections.Frozen;
using System.Buffers;
using System.Text;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Configuration;

public sealed record TestUnit(Utf8String Name, SourceText Source, IReadOnlyDictionary<Utf8String, Utf8String> Options)
{
    public Utf8String Content => Source.Text;
}
public sealed record TestSource(
    TestUnit[] Units,
    IReadOnlyDictionary<Utf8String, Utf8String> Symlinks,
    Utf8String CurrentDirectory,
    IReadOnlyDictionary<Utf8String, Utf8String> Options);

public static partial class TestDirectives
{
    public static TestSource Parse(Utf8String code, Utf8String fileName, bool allowImplicitFirstFile = false)
        => Parse(code.Span, fileName, allowImplicitFirstFile);

    private static bool Directive(ReadOnlySpan<byte> line, out Utf8String key, out Utf8String value)
    {
        key = value = default;
        if (!line.StartsWith("//"u8))
            return false;
        int at = 2;
        static bool Space(byte b) => (int)b is ' ' or '\t' or '\n' or '\f' or '\r';
        while (at < line.Length && Space(line[at]))
            at++;
        if (at == line.Length || line[at++] != '@')
            return false;
        int start = at;
        while (at < line.Length && (Utf8Ascii.IsLetterOrDigit(line[at]) || line[at] == '_'))
            at++;
        if (at == start)
            return false;
        var name = line[start..at];
        while (at < line.Length && Space(line[at]))
            at++;
        if (at == line.Length || line[at++] != ':')
            return false;
        while (at < line.Length && Space(line[at]))
            at++;
        key = Utf8String.Copy(name);
        value = Utf8String.Copy(line[at..]);
        return true;
    }

    public static TestSource Parse(ReadOnlySpan<byte> code, Utf8String fileName, bool allowImplicitFirstFile = false)
    {
        var units = new List<TestUnit>();
        var links = new Dictionary<Utf8String, Utf8String>(Utf8StringComparer.Ordinal);
        var options = new Dictionary<Utf8String, Utf8String>(Utf8StringComparer.Ordinal);
        var local = new Dictionary<Utf8String, Utf8String>(Utf8StringComparer.Ordinal);
        Utf8String name = allowImplicitFirstFile ? fileName : Utf8String.Empty, directory = Utf8String.Empty;
        var content = new ArrayBufferWriter<byte>();
        bool seenLine = false, seenFile = false;
        void Save()
        {
            units.Add(new(name, new SourceText(content.WrittenSpan), local.ToFrozenDictionary(Utf8StringComparer.Ordinal)));
            seenFile = true;
        }
        foreach (Range range in code.Split((byte)'\n'))
        {
            ReadOnlySpan<byte> line = code[range];
            if (range.End.GetOffset(code.Length) != code.Length && line.EndsWith("\r"u8))
                line = line[..^1];
            bool directive = Directive(line, out Utf8String rawKey, out Utf8String rawValue);
            int arrow = rawKey == "link"u8 ? rawValue.LastIndexOf("->"u8) : -1;
            if (arrow >= 0)
            {
                links[rawValue[(arrow + 2)..].Trim()] = rawValue[..arrow].Trim();
                continue;
            }
            if (!directive)
            {
                if (allowImplicitFirstFile ? seenLine : content.WrittenCount != 0)
                    content.Write("\n"u8);
                content.Write(line);
                seenLine = true;
                continue;
            }
            Utf8String key = rawKey.ToLowerInvariant(), value = rawValue.Trim();
            if (key == Utf8Literals.Currentdirectory)
                directory = value;
            if (key != Utf8Literals.Filename)
            {
                if (key == Utf8Literals.Symlink && name.Length != 0)
                    foreach (Utf8String target in value.Split((byte)',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        links[target] = name;
                else if (key == "emitthisfile"u8 || key == "noopen"u8)
                    local[key] = value;
                else
                    options[key] = value;
                continue;
            }
            if (name.Length != 0)
            {
                if (!allowImplicitFirstFile || content.WrittenCount != 0 || seenFile)
                    Save();
            }
            else if (content.WrittenCount != 0 && new Scanner(new SourceText(content.WrittenSpan)).Scan() != SyntaxKind.EndOfFile)
                throw new InvalidDataException("Non-comment test content appears before the first '// @Filename' directive");
            content.Clear();
            seenLine = false;
            name = value;
            local = new(Utf8StringComparer.Ordinal);
        }
        if (units.Count == 0 && name.Length == 0)
            name = CompilerPath.BaseName(fileName);
        Save();
        return new(
            units.ToArray(),
            links.ToFrozenDictionary(Utf8StringComparer.Ordinal),
            directory,
            options.ToFrozenDictionary(Utf8StringComparer.Ordinal));
    }

    public static IReadOnlyList<IReadOnlyDictionary<Utf8String, Utf8String>> Expand(
        IReadOnlyDictionary<Utf8String, Utf8String> settings,
        IReadOnlySet<Utf8String> varyBy)
    {
        if (settings.Count == 0)
            return [];
        var configurations = new List<Dictionary<Utf8String, Utf8String>> { new(Utf8StringComparer.Ordinal) };
        foreach (var setting in settings.OrderBy(p => p.Key, Utf8StringComparer.Ordinal))
        {
            Utf8String[] choices = varyBy.Contains(setting.Key) ? Variations(setting.Key, setting.Value.TrimEnd((byte)';')) : [setting.Value];
            if (choices.Length == 0)
                continue;
            if (checked(configurations.Count * choices.Length) > 25)
                throw new InvalidDataException("Provided test options exceeded the maximum number of variations");
            var next = new List<Dictionary<Utf8String, Utf8String>>();
            foreach (var current in configurations)
                foreach (Utf8String choice in choices)
                {
                    var copy = new Dictionary<Utf8String, Utf8String>(current, Utf8StringComparer.Ordinal) { [setting.Key] = choice };
                    next.Add(copy);
                }
            configurations = next;
        }
        return configurations.Select(c => (IReadOnlyDictionary<Utf8String, Utf8String>)c.ToFrozenDictionary(Utf8StringComparer.Ordinal)).ToArray();
    }

    private static Utf8String[] Variations(Utf8String option, Utf8String text)
    {
        Utf8String[] items = text.Split((byte)',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (items.Length == 0)
            return [];
        OptionDefinition definition = OptionDefinitions.Find(option) ?? throw new InvalidDataException($"Unknown test option '{option}'");
        var values = new Dictionary<Utf8String, Utf8String>(Utf8StringComparer.Ordinal);
        void Include(Utf8String item)
        {
            Utf8String key = definition.ValueIdentity(item) ?? throw new InvalidDataException($"Unknown value '{item}' for option '{option}'");
            values.TryAdd(key, item);
        }
        foreach (Utf8String item in items)
            if (item != Utf8Literals.Asterisk && item[0] is not ((byte)'-' or (byte)'!'))
                Include(item);
        if (items.Contains(Utf8Literals.Asterisk))
            foreach (Utf8String item in definition.Kind == OptionKind.Boolean ? [Utf8Literals.True, Utf8Literals.False] : definition.Values)
                Include(item);
        foreach (Utf8String item in items)
            if (item[0] is (byte)'-' or (byte)'!' && definition.ValueIdentity(item[1..]) is { } key)
                values.Remove(key);
        if (values.Count == 0)
            throw new InvalidDataException($"Variations in test option '@{option}' resulted in an empty set");
        return values.Values.ToArray();
    }
}

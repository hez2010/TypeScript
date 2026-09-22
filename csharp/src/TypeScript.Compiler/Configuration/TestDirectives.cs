using System.Collections.Frozen;
using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Configuration;

public sealed record TestUnit(string Name, SourceText Source, IReadOnlyDictionary<string, string> Options)
{
    public string Content => Source.Text;
}
public sealed record TestSource(
    TestUnit[] Units,
    IReadOnlyDictionary<string, string> Symlinks,
    string CurrentDirectory,
    IReadOnlyDictionary<string, string> Options);

public static partial class TestDirectives
{
    [GeneratedRegex(@"^//[\t\n\f\r ]*@([A-Za-z0-9_]+)[\t\n\f\r ]*:[\t\n\f\r ]*([^\r\n]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Directive();

    [GeneratedRegex(@"^//\s*@link\s*:\s*([^\r\n]*)\s*->\s*([^\r\n]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    public static TestSource Parse(string code, string fileName, bool allowImplicitFirstFile = false)
        => Parse(Wtf8.Encode(code), fileName, allowImplicitFirstFile);

    public static TestSource Parse(ReadOnlySpan<byte> code, string fileName, bool allowImplicitFirstFile = false)
    {
        var units = new List<TestUnit>();
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var local = new Dictionary<string, string>(StringComparer.Ordinal);
        string name = allowImplicitFirstFile ? fileName : "", directory = "";
        var content = new ArrayBufferWriter<byte>();
        bool seenLine = false, seenFile = false;
        void Save()
        {
            units.Add(new(name, new SourceText(content.WrittenSpan), local.ToFrozenDictionary(StringComparer.Ordinal)));
            seenFile = true;
        }
        foreach (Range range in code.Split((byte)'\n'))
        {
            ReadOnlySpan<byte> line = code[range];
            if (range.End.GetOffset(code.Length) != code.Length && line.EndsWith("\r"u8))
                line = line[..^1];
            string? directiveText = line.StartsWith("//"u8) ? Wtf8.DecodeString(line) : null;
            Match link = directiveText is null ? Match.Empty : Link().Match(directiveText);
            if (link.Success)
            {
                links[link.Groups[2].Value.Trim()] = link.Groups[1].Value.Trim();
                continue;
            }
            Match directive = directiveText is null ? Match.Empty : Directive().Match(directiveText);
            if (!directive.Success)
            {
                if (allowImplicitFirstFile ? seenLine : content.WrittenCount != 0)
                    content.Write("\n"u8);
                content.Write(line);
                seenLine = true;
                continue;
            }
            string key = directive.Groups[1].Value.ToLowerInvariant(), value = directive.Groups[2].Value.Trim();
            if (key == "currentdirectory")
                directory = value;
            if (key != "filename")
            {
                if (key == "symlink" && name.Length != 0)
                    foreach (string target in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        links[target] = name;
                else if (key is "emitthisfile" or "noopen")
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
            local = new(StringComparer.Ordinal);
        }
        if (units.Count == 0 && name.Length == 0)
            name = CompilerPath.BaseName(fileName);
        Save();
        return new(
            units.ToArray(),
            links.ToFrozenDictionary(StringComparer.Ordinal),
            directory,
            options.ToFrozenDictionary(StringComparer.Ordinal));
    }

    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Expand(
        IReadOnlyDictionary<string, string> settings,
        IReadOnlySet<string> varyBy)
    {
        if (settings.Count == 0)
            return [];
        var configurations = new List<Dictionary<string, string>> { new(StringComparer.Ordinal) };
        foreach (var setting in settings.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string[] choices = varyBy.Contains(setting.Key) ? Variations(setting.Key, setting.Value.TrimEnd(';')) : [setting.Value];
            if (choices.Length == 0)
                continue;
            if (checked(configurations.Count * choices.Length) > 25)
                throw new InvalidDataException("Provided test options exceeded the maximum number of variations");
            var next = new List<Dictionary<string, string>>();
            foreach (var current in configurations)
                foreach (string choice in choices)
                {
                    var copy = new Dictionary<string, string>(current, StringComparer.Ordinal) { [setting.Key] = choice };
                    next.Add(copy);
                }
            configurations = next;
        }
        return configurations.Select(c => (IReadOnlyDictionary<string, string>)c.ToFrozenDictionary(StringComparer.Ordinal)).ToArray();
    }

    private static string[] Variations(string option, string text)
    {
        string[] items = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (items.Length == 0)
            return [];
        OptionDefinition definition = OptionDefinitions.Find(option) ?? throw new InvalidDataException($"Unknown test option '{option}'");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        void Include(string item)
        {
            string key = definition.ValueIdentity(item) ?? throw new InvalidDataException($"Unknown value '{item}' for option '{option}'");
            values.TryAdd(key, item);
        }
        foreach (string item in items)
            if (item != "*" && item[0] is not ('-' or '!'))
                Include(item);
        if (items.Contains("*"))
            foreach (string item in definition.Kind == OptionKind.Boolean ? ["true", "false"] : definition.Values)
                Include(item);
        foreach (string item in items)
            if (item[0] is '-' or '!' && definition.ValueIdentity(item[1..]) is { } key)
                values.Remove(key);
        if (values.Count == 0)
            throw new InvalidDataException($"Variations in test option '@{option}' resulted in an empty set");
        return values.Values.ToArray();
    }
}

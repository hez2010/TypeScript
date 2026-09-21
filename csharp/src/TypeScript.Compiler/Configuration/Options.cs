using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Configuration;

public enum OptionKind { String, Number, Boolean, Object, List, ListOrElement, Enum }
public enum OptionGroup { Compiler, Build, Watch, TypeAcquisition }
public sealed record OptionDefinition(string Name, string ShortName, OptionGroup Group, OptionKind Kind,
    bool IsFilePath, bool IsConfigOnly, bool IsCommandLineOnly, string[] Values, string[] ValueIdentities, bool CanVary)
{
    public string? ValueIdentity(string value)
    {
        if (Kind == OptionKind.Boolean) return value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : value.Equals("false", StringComparison.OrdinalIgnoreCase) ? "false" : null;
        if (Kind != OptionKind.Enum) return value;
        int index = Array.FindIndex(Values, v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : ValueIdentities[index];
    }
}

public static partial class OptionDefinitions
{
    private static readonly Lazy<FrozenDictionary<string, OptionDefinition>> CompilerNames = new(() => Names(OptionGroup.Compiler));
    private static readonly Lazy<FrozenDictionary<string, OptionDefinition>> BuildNames = new(() => Names(OptionGroup.Build));
    private static readonly Lazy<FrozenDictionary<string, OptionDefinition>> WatchNames = new(() => Names(OptionGroup.Watch));
    private static FrozenDictionary<string, OptionDefinition> Names(OptionGroup group)
    {
        var names = new Dictionary<string, OptionDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in All.Where(o => o.Group == group))
        { names[option.Name] = option; if (option.ShortName.Length != 0) names[option.ShortName] = option; }
        return names.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
    public static OptionDefinition? Find(string name, OptionGroup group = OptionGroup.Compiler) =>
        (group == OptionGroup.Build ? BuildNames : group == OptionGroup.Watch ? WatchNames : CompilerNames).Value.GetValueOrDefault(name);
}

public sealed class CompilerOptions
{
    private readonly Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, JsonElement> Values => values;
    public void Set(string name, JsonElement value) => values[name] = value.Clone();
    public JsonElement? Get(string name) => values.TryGetValue(name, out var value) ? value : null;
    public bool? Boolean(string name) => Get(name) is { ValueKind: JsonValueKind.True } ? true : Get(name) is { ValueKind: JsonValueKind.False } ? false : null;
    public string? String(string name) => Get(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    public void Merge(CompilerOptions other) { foreach (var entry in other.values) values[entry.Key] = entry.Value; }
    internal void SetRaw(string name, string json) { using var document = JsonDocument.Parse(json); Set(name, document.RootElement); }
    internal void SetString(string name, string value)
    { using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) writer.WriteStringValue(value); using var document = JsonDocument.Parse(stream.ToArray()); Set(name, document.RootElement); }
}

public sealed record ParsedCommandLine(CompilerOptions Options, string[] FileNames, Diagnostic[] Diagnostics);

public sealed class CommandLineParser(IFileSystem fileSystem, string currentDirectory)
{
    public ParsedCommandLine Parse(IReadOnlyList<string> arguments, bool build = false)
    {
        var options = new CompilerOptions(); var files = new List<string>(); var errors = new List<Diagnostic>();
        var activeResponses = new HashSet<string>(fileSystem.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(IReadOnlyList<string> Args, int Index, string? Response)>();
        stack.Push((arguments, 0, null));
        void Error(DiagnosticMessage message, params string[] args) => errors.Add(new(message, 0, 0, args));
        while (stack.TryPop(out var frame))
        {
            int index = frame.Index;
            while (index < frame.Args.Count)
            {
                string arg = frame.Args[index++]; if (arg.Length == 0) continue;
                if (arg[0] == '@')
                {
                    string path = CompilerPath.Resolve(currentDirectory, arg[1..]);
                    if (!activeResponses.Add(path)) continue;
                    byte[]? bytes = fileSystem.ReadFile(path);
                    if (bytes is null) { Error(Messages.File_0_not_found, path); activeResponses.Remove(path); continue; }
                    string[] nested = ResponseArguments(SourceEncoding.Decode(bytes), errors);
                    stack.Push((frame.Args, index, frame.Response)); stack.Push((nested, 0, path));
                    frame = (frame.Args, frame.Args.Count, null); break;
                }
                if (arg[0] != '-') { files.Add(arg); continue; }
                string name = arg[(arg.StartsWith("--", StringComparison.Ordinal) ? 2 : 1)..];
                var definition = OptionDefinitions.Find(name, build ? OptionGroup.Build : OptionGroup.Compiler) ?? OptionDefinitions.Find(name, OptionGroup.Watch);
                if (definition is null) { Error(Messages.Unknown_compiler_option_0, name); continue; }
                string? next = index < frame.Args.Count ? frame.Args[index] : null;
                if (definition.IsConfigOnly)
                {
                    if (next is "false" or "null") { options.SetRaw(definition.Name, next); index++; }
                    else Error(Messages.Option_0_can_only_be_specified_in_tsconfig_json_file_or_set_to_false_or_null_on_command_line, definition.Name);
                    continue;
                }
                if (next == "null") { options.SetRaw(definition.Name, "null"); index++; continue; }
                if (definition.Kind == OptionKind.Boolean)
                { if (next is "true" or "false") { options.SetRaw(definition.Name, next); index++; } else options.SetRaw(definition.Name, "true"); continue; }
                if (next is null || next.StartsWith('-')) { Error(Messages.Compiler_option_0_expects_an_argument, definition.Name); continue; }
                index++;
                if (definition.Kind == OptionKind.Number)
                {
                    if (!int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) Error(Messages.Compiler_option_0_requires_a_value_of_type_1, definition.Name, "number");
                    else options.SetRaw(definition.Name, number.ToString(CultureInfo.InvariantCulture));
                }
                else if (definition.Kind == OptionKind.Enum)
                {
                    string lower = next.ToLowerInvariant();
                    if (!definition.Values.Contains(lower, StringComparer.Ordinal)) Error(Messages.Argument_for_0_option_must_be_Colon_1, definition.Name, string.Join(", ", definition.Values.Select(v => "'" + v + "'")));
                    else options.SetString(definition.Name, lower);
                }
                else if (definition.Kind is OptionKind.List or OptionKind.ListOrElement)
                {
                    string[] list = next.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    using var buffer = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(buffer))
                    { writer.WriteStartArray(); foreach (string item in list) writer.WriteStringValue(definition.IsFilePath ? CompilerPath.Resolve(currentDirectory, item) : item); writer.WriteEndArray(); }
                    using var document = JsonDocument.Parse(buffer.ToArray()); options.Set(definition.Name, document.RootElement);
                }
                else options.SetString(definition.Name, definition.IsFilePath ? CompilerPath.Resolve(currentDirectory, next) : next);
            }
            if (frame.Response is not null) activeResponses.Remove(frame.Response);
        }
        if (build && files.Count == 0) files.Add(".");
        return new(options, files.ToArray(), errors.ToArray());
    }
    private static string[] ResponseArguments(string text, List<Diagnostic> errors)
    {
        var result = new List<string>(); int pos = 0;
        while (pos < text.Length)
        {
            while (pos < text.Length && text[pos] <= ' ') pos++;
            if (pos == text.Length) break;
            bool quoted = text[pos] == '"'; if (quoted) pos++;
            int start = pos;
            while (pos < text.Length && (quoted ? text[pos] != '"' : text[pos] > ' ')) pos++;
            result.Add(text[start..pos]);
            if (quoted && pos == text.Length) { errors.Add(new(Messages.Unterminated_quoted_string_in_response_file_0, 0, 0, [""])); break; }
            if (quoted) pos++;
        }
        return result.ToArray();
    }
}

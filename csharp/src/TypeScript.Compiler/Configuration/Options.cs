using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Configuration;

public enum OptionKind { String, Number, Boolean, Object, List, ListOrElement, Enum }
public enum OptionGroup { Compiler, Build, Watch, TypeAcquisition }
public enum OptionValidation { None, Spec, Locale }
public sealed record OptionDefinition(string Name, string ShortName, OptionGroup Group, OptionKind Kind,
    bool IsFilePath, bool IsConfigOnly, bool IsCommandLineOnly, string[] Values, string[] ValueIdentities, bool CanVary)
{
    public OptionKind ElementKind { get; init; } = OptionKind.String;
    public bool ElementIsFilePath { get; init; }
    public long Minimum { get; init; }
    public bool AllowConfigDir { get; init; }
    public bool PreserveFalsy { get; init; }
    public OptionValidation Validation { get; init; }
    public string? ValueIdentity(string value)
    {
        if (Kind == OptionKind.Boolean) return value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : value.Equals("false", StringComparison.OrdinalIgnoreCase) ? "false" : null;
        if (Kind != OptionKind.Enum) return value;
        int index = Array.FindIndex(Values, v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : ValueIdentities[index];
    }
    public string TypeName => Kind == OptionKind.List ? "Array" : Kind == OptionKind.ListOrElement ? "string or Array" : Kind.ToString().ToLowerInvariant();
}

public static partial class OptionDefinitions
{
    private static readonly Lazy<FrozenDictionary<string, OptionDefinition>> CompilerNames = new(() => Names(OptionGroup.Compiler));
    private static readonly Lazy<FrozenDictionary<string, OptionDefinition>> BuildNames = new(() => Names(OptionGroup.Build));
    private static readonly Lazy<FrozenDictionary<string, OptionDefinition>> WatchNames = new(() => Names(OptionGroup.Watch));
    private static readonly Lazy<FrozenDictionary<string, OptionDefinition>> AcquisitionNames = new(() => Names(OptionGroup.TypeAcquisition));
    private static FrozenDictionary<string, OptionDefinition> Names(OptionGroup group)
    {
        var names = new Dictionary<string, OptionDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in All.Where(o => o.Group == group))
        { names[option.Name] = option; if (option.ShortName.Length != 0) names[option.ShortName] = option; }
        return names.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
    public static OptionDefinition? Find(string name, OptionGroup group = OptionGroup.Compiler) =>
        (group == OptionGroup.Build ? BuildNames : group == OptionGroup.Watch ? WatchNames : group == OptionGroup.TypeAcquisition ? AcquisitionNames : CompilerNames).Value.GetValueOrDefault(name);
    public static OptionDefinition? Suggest(string name, OptionGroup group)
    {
        OptionDefinition? best = null;
        int distanceLimit = (int)Math.Floor(name.Length * 0.4) * 10 + 9;
        foreach (var option in All.Where(o => o.Group == group).OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            if (option.Name == name || Math.Abs(option.Name.Length - name.Length) > Math.Max(2, name.Length * 0.34) || option.Name.Length < 3 && !option.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            int[] previous = Enumerable.Range(0, option.Name.Length + 1).Select(i => i * 10).ToArray(), current = new int[previous.Length];
            for (int i = 0; i < name.Length; i++)
            {
                current[0] = (i + 1) * 10;
                for (int j = 0; j < option.Name.Length; j++)
                {
                    int substitution = name[i] == option.Name[j] ? 0 : char.ToUpperInvariant(name[i]) == char.ToUpperInvariant(option.Name[j]) ? 1 : 20;
                    current[j + 1] = Math.Min(previous[j] + substitution, Math.Min(previous[j + 1], current[j]) + 10);
                }
                (previous, current) = (current, previous);
            }
            if (previous[^1] < distanceLimit) { best = option; distanceLimit = previous[^1]; }
        }
        return best;
    }
}

public sealed class CompilerOptions
{
    private readonly Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, JsonElement> Values => values;
    public void Set(string name, JsonElement value) => values[name] = value.Clone();
    public JsonElement? Get(string name) => values.TryGetValue(name, out var value) ? value : null;
    public bool? Boolean(string name) => Get(name) is { ValueKind: JsonValueKind.True } ? true : Get(name) is { ValueKind: JsonValueKind.False } ? false : null;
    public double? Number(string name) => Get(name) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;
    public string? String(string name) => Get(name) is { ValueKind: JsonValueKind.String } value ? JsonStrings.GetString(value) : null;
    public string[]? Strings(string name) => Get(name) is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(JsonStrings.GetString).ToArray() : null;
    public void Merge(CompilerOptions other) { foreach (var entry in other.values) values[entry.Key] = entry.Value; }
    internal void SetRaw(string name, string json) { using var document = JsonDocument.Parse(json); Set(name, document.RootElement); }
    internal void SetString(string name, string value) => Set(name, OptionValues.String(value));
    internal void SetArray(string name, IEnumerable<JsonElement> items) => Set(name, OptionValues.Array(items));
}

internal static class OptionValues
{
    internal static JsonElement String(string value)
    { using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) JsonStrings.WriteString(writer, value); return JsonStrings.Parse(stream); }
    internal static JsonElement Array(IEnumerable<JsonElement> values)
    { using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartArray(); foreach (var value in values) JsonStrings.WriteValue(writer, value); writer.WriteEndArray(); } return JsonStrings.Parse(stream); }
    internal static JsonElement Null { get; } = ParseNull();
    private static JsonElement ParseNull() { using var document = JsonDocument.Parse("null"); return document.RootElement.Clone(); }
    internal static bool IsTemplate(string value) => value.StartsWith("${configDir}", StringComparison.OrdinalIgnoreCase);
    internal static string PathValue(string value, string directory)
    {
        if (IsTemplate(value)) return CompilerPath.NormalizeSlashes(value);
        string path = CompilerPath.Resolve(directory, value);
        return path.Length > CompilerPath.RootLength(path) ? CompilerPath.RemoveTrailingSeparator(path) : path;
    }
    internal static DiagnosticMessage? SpecError(string value, bool include)
    {
        string[] parts = CompilerPath.NormalizeSlashes(value).TrimEnd('/').Split('/');
        if (include && parts[^1] == "**") return Messages.File_specification_cannot_end_in_a_recursive_directory_wildcard_Asterisk_Asterisk_Colon_0;
        bool recursive = false;
        foreach (string part in parts) { if (part == ".." && recursive) return Messages.File_specification_cannot_contain_a_parent_directory_that_appears_after_a_recursive_directory_wildcard_Asterisk_Asterisk_Colon_0; recursive |= part == "**"; }
        return null;
    }
    internal static void EnumError(OptionDefinition definition, Action<DiagnosticMessage, string[]> error)
        => error(Messages.Argument_for_0_option_must_be_Colon_1, ["--" + definition.Name, string.Join(", ", definition.Values.Select(v => "'" + v + "'"))]);
    private static bool IsLocale(string text)
    {
        if (!Regex.IsMatch(text, @"^(?:[A-Za-z]{2,8}|[iIxX])(?:[-_][A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)) return false;
        if (text.StartsWith("x-", StringComparison.OrdinalIgnoreCase)) return true;
        try { return CultureInfo.GetCultureInfo(text.Replace('_', '-')).ThreeLetterISOLanguageName.Length != 0; }
        catch (CultureNotFoundException) { return false; }
    }
    internal static JsonElement? Convert(OptionDefinition definition, JsonElement value, string directory, Action<DiagnosticMessage, string[]> error, bool element = false)
    {
        if (value.ValueKind == JsonValueKind.Null) return value;
        OptionKind kind = element ? definition.ElementKind : definition.Kind;
        bool valid = kind switch
        {
            OptionKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            OptionKind.String or OptionKind.Enum => value.ValueKind == JsonValueKind.String,
            OptionKind.Number => value.ValueKind == JsonValueKind.Number,
            OptionKind.Object => value.ValueKind == JsonValueKind.Object,
            OptionKind.List => value.ValueKind == JsonValueKind.Array,
            OptionKind.ListOrElement => value.ValueKind is JsonValueKind.Array or JsonValueKind.String,
            _ => false,
        };
        if (!valid) { error(Messages.Compiler_option_0_requires_a_value_of_type_1, [definition.Name, element ? kind.ToString().ToLowerInvariant() : definition.TypeName]); return null; }
        if (kind == OptionKind.Number && definition.Name is "checkers" or "builders" or "watchInterval")
        {
            if (!value.TryGetDecimal(out decimal number) || number < long.MinValue || number > long.MaxValue)
            { error(Messages.Compiler_option_0_requires_a_value_of_type_1, [definition.Name, "number in the supported 64-bit range"]); return null; }
            // Native worker counts/intervals use integer storage. The TypeScript
            // maxNodeModuleJsDepth option retains Number semantics, including Infinity.
            using var document = JsonDocument.Parse(decimal.ToInt64(number).ToString(CultureInfo.InvariantCulture));
            return document.RootElement.Clone();
        }
        if (kind is OptionKind.List or OptionKind.ListOrElement && value.ValueKind == JsonValueKind.Array)
        {
            var items = new List<JsonElement>();
            foreach (var item in value.EnumerateArray())
                if (Convert(definition, item, directory, error, true) is { } converted && (definition.PreserveFalsy || converted.ValueKind != JsonValueKind.Null && !(converted.ValueKind == JsonValueKind.String && JsonStrings.GetString(converted) == ""))) items.Add(converted);
            return Array(items);
        }
        if (kind == OptionKind.Enum)
        {
            string name = JsonStrings.GetString(value).ToLowerInvariant();
            if (name == "") return Null;
            if (!definition.Values.Contains(name, StringComparer.Ordinal)) { EnumError(definition, error); return null; }
            return String(name);
        }
        if (kind == OptionKind.String)
        {
            string text = JsonStrings.GetString(value);
            if (definition.Validation == OptionValidation.Spec && SpecError(text, false) is { } diagnostic) { error(diagnostic, [text]); return null; }
            if (definition.Validation == OptionValidation.Locale && !IsLocale(text))
            { error(Messages.Locale_must_be_an_IETF_BCP_47_language_tag_Examples_Colon_0_1, ["en", "ja-jp"]); return null; }
            if (element ? definition.ElementIsFilePath : definition.IsFilePath) return String(PathValue(text, directory));
        }
        return value;
    }
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
                    if (bytes is null) { Error(Messages.Cannot_read_file_0, path); activeResponses.Remove(path); continue; }
                    string[] nested = ResponseArguments(SourceEncoding.Decode(bytes), path, errors);
                    stack.Push((frame.Args, index, frame.Response)); stack.Push((nested, 0, path));
                    frame = (frame.Args, frame.Args.Count, null); break;
                }
                if (arg[0] != '-') { files.Add(arg); continue; }
                string name = arg[(arg.StartsWith("--", StringComparison.Ordinal) ? 2 : 1)..];
                var definition = OptionDefinitions.Find(name, build ? OptionGroup.Build : OptionGroup.Compiler) ?? OptionDefinitions.Find(name, OptionGroup.Watch);
                if (definition is null)
                {
                    var alternate = OptionDefinitions.Find(name, build ? OptionGroup.Compiler : OptionGroup.Build);
                    if (alternate is null && OptionDefinitions.Suggest(name, build ? OptionGroup.Build : OptionGroup.Compiler) is { } suggestion)
                    { Error(build ? Messages.Unknown_build_option_0_Did_you_mean_1 : Messages.Unknown_compiler_option_0_Did_you_mean_1, name, suggestion.Name); continue; }
                    Error(alternate is null ? build ? Messages.Unknown_build_option_0 : Messages.Unknown_compiler_option_0 : alternate.Name == "build" ? Messages.Option_build_must_be_the_first_command_line_argument : build ? Messages.Compiler_option_0_may_not_be_used_with_build : Messages.Compiler_option_0_may_only_be_used_with_build, name);
                    continue;
                }
                string? next = index < frame.Args.Count ? frame.Args[index] : null;
                if (definition.IsConfigOnly)
                {
                    if (next == "null" || next == "false" && definition.Kind == OptionKind.Boolean) { options.SetRaw(definition.Name, next); index++; }
                    else
                    {
                        Error(definition.Kind == OptionKind.Boolean ? Messages.Option_0_can_only_be_specified_in_tsconfig_json_file_or_set_to_false_or_null_on_command_line : Messages.Option_0_can_only_be_specified_in_tsconfig_json_file_or_set_to_null_on_command_line, definition.Name);
                        if (next == "true" && definition.Kind == OptionKind.Boolean || definition.Kind != OptionKind.Boolean && !string.IsNullOrEmpty(next) && !next.StartsWith('-')) index++;
                    }
                    continue;
                }
                if (next == "null") { options.SetRaw(definition.Name, "null"); index++; continue; }
                if (definition.Kind == OptionKind.Boolean)
                { if (next is "true" or "false") { options.SetRaw(definition.Name, next); index++; } else options.SetRaw(definition.Name, "true"); continue; }
                DiagnosticMessage mismatch = definition.Group == OptionGroup.Watch ? Messages.Watch_option_0_requires_a_value_of_type_1 : build ? Messages.Build_option_0_requires_a_value_of_type_1 : Messages.Compiler_option_0_expects_an_argument;
                if (next is null)
                {
                    Error(mismatch, definition.Name, definition.TypeName);
                    if (definition.Kind == OptionKind.List) options.SetRaw(definition.Name, "[]");
                    if (definition.Kind == OptionKind.Enum) OptionValues.EnumError(definition, Error);
                    continue;
                }
                if (definition.Kind == OptionKind.Number)
                {
                    index++;
                    if (!long.TryParse(next, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number)) Error(mismatch, definition.Name, "number");
                    else if (number < definition.Minimum) Error(Messages.Option_0_requires_value_to_be_greater_than_1, definition.Name, definition.Minimum.ToString(CultureInfo.InvariantCulture));
                    else options.SetRaw(definition.Name, number.ToString(CultureInfo.InvariantCulture));
                }
                else if (definition.Kind is OptionKind.List or OptionKind.ListOrElement)
                {
                    string trimmed = next.Trim();
                    var list = new List<JsonElement>(); int beforeErrors = errors.Count;
                    if (!trimmed.StartsWith('-') && trimmed.Length != 0)
                        foreach (string item in trimmed.Split(','))
                        {
                            string value = definition.ElementKind == OptionKind.Enum ? item.Trim() : item;
                            if (OptionValues.Convert(definition, OptionValues.String(value), currentDirectory, Error, true) is { } converted && converted.ValueKind != JsonValueKind.Null && !(converted.ValueKind == JsonValueKind.String && JsonStrings.GetString(converted) == "")) list.Add(converted);
                        }
                    if (list.Count != 0 || errors.Count != beforeErrors) index++;
                    options.SetArray(definition.Name, list);
                }
                else
                {
                    index++;
                    if (OptionValues.Convert(definition, OptionValues.String(definition.Kind == OptionKind.Enum ? next.Trim() : next), currentDirectory, Error) is { } value) options.Set(definition.Name, value);
                    else if (definition.Kind == OptionKind.Enum) options.SetRaw(definition.Name, "null");
                }
            }
            if (frame.Response is not null) activeResponses.Remove(frame.Response);
        }
        if (build)
        {
            if (files.Count == 0) files.Add(".");
            foreach (var (left, right) in new[] { ("clean", "force"), ("clean", "verbose"), ("clean", "watch"), ("watch", "dry") })
                if (options.Boolean(left) == true && options.Boolean(right) == true) Error(Messages.Options_0_and_1_cannot_be_combined, left, right);
        }
        return new(options, files.ToArray(), errors.ToArray());
    }
    private static string[] ResponseArguments(string text, string path, List<Diagnostic> errors)
    {
        var result = new List<string>(); int pos = 0;
        while (pos < text.Length)
        {
            while (pos < text.Length && text[pos] <= ' ') pos++;
            if (pos == text.Length) break;
            bool quoted = text[pos] == '"'; if (quoted) pos++;
            int start = pos;
            while (pos < text.Length && (quoted ? text[pos] != '"' : text[pos] > ' ')) pos++;
            if (quoted && pos == text.Length) { errors.Add(new(Messages.Unterminated_quoted_string_in_response_file_0, 0, 0, [path])); break; }
            result.Add(text[start..pos]);
            if (quoted) pos++;
        }
        return result.ToArray();
    }
}

using TypeScript.Compiler.Text;
using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Configuration;

public enum OptionKind
{
    String,
    Number,
    Boolean,
    Object,
    List,
    ListOrElement,
    Enum
}
public enum OptionGroup
{
    Compiler,
    Build,
    Watch,
    TypeAcquisition
}
public enum OptionValidation
{
    None,
    Spec,
    Locale
}
[Flags]
public enum OptionEffects
{
    None = 0, DeclarationPath = 1, ProgramStructure = 2, SemanticDiagnostics = 4,
    BuildInfo = 8, BindDiagnostics = 16, SourceFile = 32, ModuleResolution = 64, Emit = 128
}
public sealed record OptionDefinition(Utf8String Name, Utf8String ShortName, OptionGroup Group, OptionKind Kind,
    bool IsFilePath, bool IsConfigOnly, bool IsCommandLineOnly, Utf8String[] Values, Utf8String[] ValueIdentities, bool CanVary)
{
    public OptionKind ElementKind { get; init; } = OptionKind.String;
    public bool ElementIsFilePath { get; init; }
    public long Minimum { get; init; }
    public bool AllowConfigDir { get; init; }
    public bool PreserveFalsy { get; init; }
    public OptionValidation Validation { get; init; }
    public Utf8String[] DeprecatedValues { get; init; } = [];
    public OptionEffects Effects { get; init; }
    public bool StrictFlag { get; init; }
    public bool AllowJsFlag { get; init; }
    public DiagnosticMessage? Description { get; init; }
    public DiagnosticMessage? Category { get; init; }
    public DiagnosticMessage? DefaultDescription { get; init; }
    public Utf8String DefaultValue { get; init; } = "undefined"u8;
    public bool EnumDefaultValue { get; init; }
    public bool SimplifiedHelp { get; init; }

    public Utf8String? ValueIdentity(Utf8String value)
    {
        if (Kind == OptionKind.Boolean)
            return value.Equals(Utf8Literals.True, StringComparison.OrdinalIgnoreCase)
                ? Utf8Literals.True
                : value.Equals(Utf8Literals.False, StringComparison.OrdinalIgnoreCase) ? Utf8Literals.False : (Utf8String?)null;
        if (Kind != OptionKind.Enum)
            return value;
        int index = Array.FindIndex(Values, v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? (Utf8String?)null : ValueIdentities[index];
    }

    public Utf8String TypeName =>
        Kind == OptionKind.List ? Utf8Literals.Array : Kind == OptionKind.ListOrElement ? Utf8Literals.StringOrArray : Utf8String.EnumName(Kind).ToLowerInvariant();
}

public static partial class OptionDefinitions
{
    private static readonly Lazy<FrozenDictionary<Utf8String, OptionDefinition>> CompilerNames = new(() => Names(OptionGroup.Compiler));
    private static readonly Lazy<FrozenDictionary<Utf8String, OptionDefinition>> BuildNames = new(() => Names(OptionGroup.Build));
    private static readonly Lazy<FrozenDictionary<Utf8String, OptionDefinition>> WatchNames = new(() => Names(OptionGroup.Watch));
    private static readonly Lazy<FrozenDictionary<Utf8String, OptionDefinition>> AcquisitionNames =
        new(() => Names(OptionGroup.TypeAcquisition));

    private static FrozenDictionary<Utf8String, OptionDefinition> Names(OptionGroup group)
    {
        var names = new Dictionary<Utf8String, OptionDefinition>(Utf8StringComparer.OrdinalIgnoreCase);
        foreach (var option in All.Where(o => o.Group == group))
        {
            names[option.Name] = option;
            if (option.ShortName.Length != 0)
                names[option.ShortName] = option;
        }
        return names.ToFrozenDictionary(Utf8StringComparer.OrdinalIgnoreCase);
    }

    public static OptionDefinition? Find(Utf8String name, OptionGroup group = OptionGroup.Compiler) =>
            (group == OptionGroup.Build
                ? BuildNames
                : group == OptionGroup.Watch
                    ? WatchNames
                    : group == OptionGroup.TypeAcquisition ? AcquisitionNames : CompilerNames).Value.GetValueOrDefault(name);

    public static OptionDefinition? Suggest(Utf8String name, OptionGroup group)
    {
        OptionDefinition? best = null;
        int distanceLimit = (int)Math.Floor(name.Length * 0.4) * 10 + 9;
        foreach (var option in All.Where(o => o.Group == group).OrderBy(o => o.Name, Utf8StringComparer.Ordinal))
        {
            if (option.Name == name
                || Math.Abs(option.Name.Length - name.Length) > Math.Max(2, name.Length * 0.34)
                || option.Name.Length < 3 && !option.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            int[] previous = Enumerable.Range(0, option.Name.Length + 1).Select(i => i * 10).ToArray(), current = new int[previous.Length];
            for (int i = 0; i < name.Length; i++)
            {
                current[0] = (i + 1) * 10;
                for (int j = 0; j < option.Name.Length; j++)
                {
                    int substitution = name[i] == option.Name[j]
                        ? 0
                        : Utf8Ascii.ToUpper(name[i]) == Utf8Ascii.ToUpper(option.Name[j]) ? 1 : 20;
                    current[j + 1] = Math.Min(previous[j] + substitution, Math.Min(previous[j + 1], current[j]) + 10);
                }
                (previous, current) = (current, previous);
            }
            if (previous[^1] < distanceLimit)
            {
                best = option;
                distanceLimit = previous[^1];
            }
        }
        return best;
    }
}

public sealed partial class CompilerOptions
{
    public bool StrictOption(Utf8String name) => EffectiveStrict(Boolean(name));
    private bool EffectiveStrict(bool? value) => value ?? Strict ?? true;
    internal bool StrictNoImplicitAny => EffectiveStrict(NoImplicitAny);
    internal bool EffectiveNoImplicitThis => EffectiveStrict(NoImplicitThis);
    internal bool EffectiveStrictPropertyInitialization => EffectiveStrict(StrictPropertyInitialization);
    internal bool EffectiveStrictBuiltinIteratorReturn => EffectiveStrict(StrictBuiltinIteratorReturn);
    internal bool EffectiveStrictNullChecks => EffectiveStrict(StrictNullChecks);
    internal bool EffectiveStrictFunctionTypes => EffectiveStrict(StrictFunctionTypes);
    internal bool EffectiveUseUnknownInCatchVariables => EffectiveStrict(UseUnknownInCatchVariables);
    internal bool EffectiveStrictBindCallApply => EffectiveStrict(StrictBindCallApply);

    // Resolved target years are positive. An integer cache also permits benign
    // concurrent initialization by parsers and checkers sharing these options.
    private int emitTargetYear;
    internal int EmitTargetYear => emitTargetYear == 0 ? emitTargetYear = ComputeTargetYear() : emitTargetYear;

    private int emitModuleKind = int.MinValue;
    internal int EmitModuleKind => emitModuleKind == int.MinValue ? emitModuleKind = ComputeModuleKind() : emitModuleKind;
    internal ModuleKind EmitModule => (ModuleKind)EmitModuleKind;

    private int ComputeTargetYear() => Target switch
    {
        ScriptTarget.ES5 => 2009,
        >= ScriptTarget.ES2015 and <= ScriptTarget.ES2025 => (int)Target + 2013,
        ScriptTarget.ESNext => int.MaxValue,
        _ => 2025
    };

    private int ComputeModuleKind() => Module != ModuleKind.None ? (int)Module
        : EmitTargetYear == int.MaxValue ? 99 : EmitTargetYear >= 2022 ? 7 : EmitTargetYear >= 2020 ? 6 : EmitTargetYear >= 2015 ? 5 : 1;

    internal ModuleResolutionKind EmitModuleResolutionKind => ModuleResolution switch
    {
        ModuleResolutionKind.Unknown or ModuleResolutionKind.Classic or ModuleResolutionKind.Node10 =>
            EmitModuleKind switch
            {
                100 or 101 or 102 => ModuleResolutionKind.Node16,
                199 => ModuleResolutionKind.NodeNext,
                _ => ModuleResolutionKind.Bundler
            },
        _ => ModuleResolution
    };

    internal ModuleDetectionKind EmitModuleDetectionKind => ModuleDetection != ModuleDetectionKind.None
        ? ModuleDetection : EmitModuleKind is >= 100 and <= 199 ? ModuleDetectionKind.Force : ModuleDetectionKind.Auto;

    private readonly Dictionary<Utf8String, JsonElement> values = new(Utf8StringComparer.Ordinal);
    public IReadOnlyDictionary<Utf8String, JsonElement> Values => values;

    public void Set(Utf8String name, JsonElement value) => Store(name, value.Clone());

    private void Store(Utf8String name, JsonElement value)
    {
        values[name] = value;
        Utf8String? text = value.ValueKind == JsonValueKind.String ? JsonStrings.GetString(value) : (Utf8String?)null;
        AssignTyped(name, value, text);
        if (name == Utf8Literals.Target)
            emitTargetYear = 0;
        if (name == "module"u8 || name == "target"u8)
            emitModuleKind = int.MinValue;
    }

    public JsonElement? Get(Utf8String name) => values.TryGetValue(name, out var value) ? value : null;

    public bool? Boolean(Utf8String name) =>
        Get(name)?.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };

    public double? Number(Utf8String name) => Get(name) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

    public Utf8String? String(Utf8String name) => Get(name) is { ValueKind: JsonValueKind.String } value ? JsonStrings.GetString(value) : (Utf8String?)null;

    public Utf8String[]? Strings(Utf8String name) =>
        Get(name) is { ValueKind: JsonValueKind.Array } value
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(JsonStrings.GetString).ToArray()
            : null;

    public void Merge(CompilerOptions other)
    {
        foreach (var entry in other.values)
            Store(entry.Key, entry.Value);
    }

    internal void SetRaw(Utf8String name, Utf8String json)
    {
        using var document = JsonDocument.Parse(json.Memory);
        Set(name, document.RootElement);
    }

    internal void SetString(Utf8String name, Utf8String value) => Set(name, OptionValues.String(value));

    internal void SetArray(Utf8String name, IEnumerable<JsonElement> items) => Set(name, OptionValues.Array(items));
}

internal static class OptionValues
{
    internal static JsonElement String(Utf8String value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            JsonStrings.WriteString(writer, value);
        return JsonStrings.Parse(stream);
    }

    internal static JsonElement Array(IEnumerable<JsonElement> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var value in values)
                JsonStrings.WriteValue(writer, value);
            writer.WriteEndArray();
        }
        return JsonStrings.Parse(stream);
    }

    internal static JsonElement Null { get; } = ParseNull();

    private static JsonElement ParseNull()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    internal static bool IsTemplate(Utf8String value) => value.StartsWith("${configDir}"u8, StringComparison.OrdinalIgnoreCase);

    internal static Utf8String PathValue(Utf8String value, Utf8String directory)
    {
        if (IsTemplate(value))
            return CompilerPath.NormalizeSlashes(value);
        Utf8String path = CompilerPath.Resolve(directory, value);
        return path.Length > CompilerPath.RootLength(path) ? CompilerPath.RemoveTrailingSeparator(path) : path;
    }

    internal static DiagnosticMessage? SpecError(Utf8String value, bool include)
    {
        Utf8String[] parts = CompilerPath.NormalizeSlashes(value).TrimEnd((byte)'/').Split((byte)'/');
        if (include && parts[^1] == Utf8Literals.DoubleAsterisk)
            return Messages.File_specification_cannot_end_in_a_recursive_directory_wildcard_Asterisk_Asterisk_Colon_0;
        bool recursive = false;
        foreach (Utf8String part in parts)
        {
            if (part == Utf8Literals.ParentDirectory && recursive)
                return Messages.File_specification_cannot_contain_a_parent_directory_that_appears_after_a_recursive_directory_wildcard_Asterisk_Asterisk_Colon_0;
            recursive |= part == Utf8Literals.DoubleAsterisk;
        }
        return null;
    }

    internal static void EnumError(OptionDefinition definition, Action<DiagnosticMessage, Utf8String[]> error)
            =>
                error(
                    Messages.Argument_for_0_option_must_be_Colon_1,
                    [Utf8String.Concat("--"u8, definition.Name), Utf8String.Join(", "u8,
                        definition.Values.Where(v => !definition.DeprecatedValues.Contains(v)).Select(v => "'"u8 + v + "'"u8))]);

    internal static JsonElement? Convert(
        OptionDefinition definition,
        JsonElement value,
        Utf8String directory,
        Action<DiagnosticMessage, Utf8String[]> error,
        bool element = false)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return value;
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
        if (!valid)
        {
            error(
                Messages.Compiler_option_0_requires_a_value_of_type_1,
                [definition.Name, element ? Utf8String.EnumName(kind).ToLowerInvariant() : definition.TypeName]);
            return null;
        }
        if (kind == OptionKind.Number && (definition.Name == "checkers"u8 || definition.Name == "builders"u8 || definition.Name == "watchInterval"u8))
        {
            if (!value.TryGetDecimal(out decimal number) || number < long.MinValue || number > long.MaxValue)
            {
                error(Messages.Compiler_option_0_requires_a_value_of_type_1, [definition.Name, Utf8Literals.NumberInTheSupported64Bit]);
                return null;
            }
            // Native worker counts/intervals use integer storage. The TypeScript
            // maxNodeModuleJsDepth option retains Number semantics, including Infinity.
            using var document = JsonDocument.Parse(Utf8String.Format(decimal.ToInt64(number)).Memory);
            return document.RootElement.Clone();
        }
        if (kind is OptionKind.List or OptionKind.ListOrElement && value.ValueKind == JsonValueKind.Array)
        {
            var items = new List<JsonElement>();
            foreach (var item in value.EnumerateArray())
                if (Convert(definition, item, directory, error, true) is { } converted
                    && (definition.PreserveFalsy
                        || converted.ValueKind != JsonValueKind.Null
                            && !(converted.ValueKind == JsonValueKind.String && JsonStrings.GetString(converted) == Utf8String.Empty)))
                    items.Add(converted);
            return Array(items);
        }
        if (kind == OptionKind.Enum)
        {
            Utf8String name = JsonStrings.GetString(value).ToLowerInvariant();
            if (name == Utf8String.Empty)
                return Null;
            if (!definition.Values.Contains(name, Utf8StringComparer.Ordinal))
            {
                EnumError(definition, error);
                return null;
            }
            return String(name);
        }
        if (kind == OptionKind.String)
        {
            Utf8String text = JsonStrings.GetString(value);
            if (definition.Validation == OptionValidation.Spec && SpecError(text, false) is { } diagnostic)
            {
                error(diagnostic, [text]);
                return null;
            }
            if (definition.Validation == OptionValidation.Locale && !LocaleIdentifier.IsValid(text))
            {
                error(Messages.Locale_must_be_an_IETF_BCP_47_language_tag_Examples_Colon_0_1, [Utf8Literals.En, Utf8Literals.JaJp]);
                return null;
            }
            if (element ? definition.ElementIsFilePath : definition.IsFilePath)
                return String(PathValue(text, directory));
        }
        return value;
    }
}

public readonly record struct ParsedCommandLine(CompilerOptions Options, Utf8String[] FileNames, Diagnostic[] Diagnostics);

public sealed class CommandLineParser(IFileSystem fileSystem, Utf8String currentDirectory)
{
    public ParsedCommandLine Parse(IReadOnlyList<Utf8String> arguments, bool build = false, bool resolvePaths = true)
    {
        var options = new CompilerOptions();
        var files = new List<Utf8String>();
        var errors = new List<Diagnostic>();
        var activeResponses = new HashSet<Utf8String>(fileSystem.CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(IReadOnlyList<Utf8String> Args, int Index, Utf8String? Response)>();
        stack.Push((arguments, 0, null));
        void Error(DiagnosticMessage message, params Utf8String[] args) => errors.Add(new(message, 0, 0, args));
        JsonElement CommandPath(JsonElement value, bool filePath) => resolvePaths && filePath && value.ValueKind == JsonValueKind.String
            && OptionValues.IsTemplate(JsonStrings.GetString(value)) ? OptionValues.String(CompilerPath.Resolve(currentDirectory, JsonStrings.GetString(value))) : value;
        while (stack.TryPop(out var frame))
        {
            int index = frame.Index;
            while (index < frame.Args.Count)
            {
                Utf8String arg = frame.Args[index++];
                if (arg.Length == 0)
                    continue;
                if (arg[0] == '@')
                {
                    Utf8String path = CompilerPath.Resolve(currentDirectory, arg[1..]);
                    if (!activeResponses.Add(path))
                        continue;
                    byte[]? bytes = fileSystem.ReadFile(path);
                    if (bytes is null)
                    {
                        Error(Messages.Cannot_read_file_0, path);
                        activeResponses.Remove(path);
                        continue;
                    }
                    Utf8String[] nested = ResponseArguments(SourceEncoding.Decode(bytes), path, errors);
                    stack.Push((frame.Args, index, frame.Response));
                    stack.Push((nested, 0, path));
                    frame = (frame.Args, frame.Args.Count, null);
                    break;
                }
                if (arg[0] != '-')
                {
                    files.Add(arg);
                    continue;
                }
                Utf8String name = arg[(arg.StartsWith("--"u8, StringComparison.Ordinal) ? 2 : 1)..];
                var definition = OptionDefinitions.Find(
                    name,
                    build ? OptionGroup.Build : OptionGroup.Compiler) ?? OptionDefinitions.Find(name, OptionGroup.Watch);
                if (definition is null)
                {
                    var alternate = OptionDefinitions.Find(name, build ? OptionGroup.Compiler : OptionGroup.Build);
                    if (alternate is null
                        && OptionDefinitions.Suggest(name, build ? OptionGroup.Build : OptionGroup.Compiler) is { } suggestion)
                    {
                        Error(
                            build ? Messages.Unknown_build_option_0_Did_you_mean_1 : Messages.Unknown_compiler_option_0_Did_you_mean_1,
                            arg,
                            suggestion.Name);
                        continue;
                    }
                    Error(
                        alternate is null
                            ? build ? Messages.Unknown_build_option_0 : Messages.Unknown_compiler_option_0
                            : alternate.Name == Utf8Literals.Build
                                ? Messages.Option_build_must_be_the_first_command_line_argument
                                : build
                                    ? Messages.Compiler_option_0_may_not_be_used_with_build
                                    : Messages.Compiler_option_0_may_only_be_used_with_build,
                        alternate is null ? arg : name);
                    continue;
                }
                Utf8String? next = index < frame.Args.Count ? frame.Args[index] : (Utf8String?)null;
                if (!resolvePaths) definition = definition with { IsFilePath = false, ElementIsFilePath = false };
                if (definition.IsConfigOnly)
                {
                    if (next == Utf8Literals.Null || next == Utf8Literals.False && definition.Kind == OptionKind.Boolean)
                    {
                        options.SetRaw(definition.Name, next.GetValueOrDefault());
                        index++;
                    }
                    else
                    {
                        Error(
                            definition.Kind == OptionKind.Boolean
                                ? Messages.Option_0_can_only_be_specified_in_tsconfig_json_file_or_set_to_false_or_null_on_command_line
                                : Messages.Option_0_can_only_be_specified_in_tsconfig_json_file_or_set_to_null_on_command_line,
                            definition.Name);
                        if (next == Utf8Literals.True && definition.Kind == OptionKind.Boolean
                            || definition.Kind != OptionKind.Boolean && next is { IsEmpty: false } && !next.Value.StartsWith((byte)'-'))
                            index++;
                    }
                    continue;
                }
                if (next == Utf8Literals.Null)
                {
                    options.SetRaw(definition.Name, Utf8Literals.Null);
                    index++;
                    continue;
                }
                if (definition.Kind == OptionKind.Boolean)
                {
                    if (Utf8String.Equals(next, "true"u8) || Utf8String.Equals(next, "false"u8))
                    {
                        options.SetRaw(definition.Name, next.GetValueOrDefault());
                        index++;
                    }
                    else
                        options.SetRaw(definition.Name, Utf8Literals.True);
                    continue;
                }
                DiagnosticMessage mismatch = definition.Group == OptionGroup.Watch
                    ? Messages.Watch_option_0_requires_a_value_of_type_1
                    : build ? Messages.Build_option_0_requires_a_value_of_type_1 : Messages.Compiler_option_0_expects_an_argument;
                if (next is null)
                {
                    Error(mismatch, definition.Name, definition.TypeName);
                    if (definition.Kind == OptionKind.List)
                        options.SetRaw(definition.Name, Utf8Literals.EmptyBrackets);
                    if (definition.Kind == OptionKind.Enum)
                        OptionValues.EnumError(definition, Error);
                    continue;
                }
                if (definition.Kind == OptionKind.Number)
                {
                    index++;
                    if (!long.TryParse(next.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number))
                        Error(mismatch, definition.Name, Utf8Literals.NumberKeyword);
                    else if (number < definition.Minimum)
                        Error(
                            Messages.Option_0_requires_value_to_be_greater_than_1,
                            definition.Name,
                            Utf8String.Format(definition.Minimum));
                    else
                        options.SetRaw(definition.Name, Utf8String.Format(number));
                }
                else if (definition.Kind is OptionKind.List or OptionKind.ListOrElement)
                {
                    Utf8String trimmed = next.Value.Trim();
                    var list = new List<JsonElement>();
                    int beforeErrors = errors.Count;
                    if (!trimmed.StartsWith((byte)'-') && trimmed.Length != 0)
                        foreach (Utf8String item in trimmed.Split((byte)','))
                        {
                            Utf8String value = definition.ElementKind == OptionKind.Enum ? item.Trim() : item;
                            if (OptionValues.Convert(definition, OptionValues.String(value), currentDirectory, Error, true) is { } converted
                                && converted.ValueKind != JsonValueKind.Null
                                && !(converted.ValueKind == JsonValueKind.String && JsonStrings.GetString(converted) == Utf8String.Empty))
                                list.Add(CommandPath(converted, definition.ElementIsFilePath));
                        }
                    if (list.Count != 0 || errors.Count != beforeErrors)
                        index++;
                    options.SetArray(definition.Name, list);
                }
                else
                {
                    index++;
                    if (OptionValues.Convert(
                        definition,
                        OptionValues.String(definition.Kind == OptionKind.Enum ? next.Value.Trim() : next.Value),
                        currentDirectory,
                        Error) is { } value)
                        options.Set(definition.Name, CommandPath(value, definition.IsFilePath));
                    else if (definition.Kind == OptionKind.Enum)
                        options.SetRaw(definition.Name, Utf8Literals.Null);
                }
            }
            if (frame.Response is not null)
                activeResponses.Remove(frame.Response.Value);
        }
        if (build)
        {
            if (files.Count == 0)
                files.Add(Utf8Literals.Dot);
            foreach (var (left, right) in new[] { (Utf8Literals.Clean, Utf8Literals.Force), (Utf8Literals.Clean, Utf8Literals.Verbose), (Utf8Literals.Clean, Utf8Literals.Watch), (Utf8Literals.Watch, Utf8Literals.Dry) })
                if (options.Boolean(left) == true && options.Boolean(right) == true)
                    Error(Messages.Options_0_and_1_cannot_be_combined, left, right);
        }
        return new(options, files.ToArray(), errors.ToArray());
    }

    private static Utf8String[] ResponseArguments(Utf8String text, Utf8String path, List<Diagnostic> errors)
    {
        var result = new List<Utf8String>();
        int pos = 0;
        while (pos < text.Length)
        {
            while (pos < text.Length && text[pos] <= ' ')
                pos++;
            if (pos == text.Length)
                break;
            bool quoted = text[pos] == '"';
            if (quoted)
                pos++;
            int start = pos;
            while (pos < text.Length && (quoted ? text[pos] != '"' : text[pos] > ' '))
                pos++;
            if (quoted && pos == text.Length)
            {
                errors.Add(new(Messages.Unterminated_quoted_string_in_response_file_0, 0, 0, [path]));
                break;
            }
            result.Add(text[start..pos]);
            if (quoted)
                pos++;
        }
        return result.ToArray();
    }
}

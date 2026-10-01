using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Incremental;

public static class IncrementalOptions
{
    public static bool IsIncremental(CompilerOptions options) => options.Incremental == true || options.Composite == true;

    public static Utf8String GetBuildInfoFileName(ParsedConfig config, Utf8String currentDirectory, bool caseSensitive, bool build = false)
    {
        var options = config.Options;
        if (!IsIncremental(options) && !build && options.Build != true) return default;
        if (options.TsBuildInfoFile is { IsEmpty: false } explicitPath) return CompilerPath.Resolve(currentDirectory, explicitPath);
        Utf8String configPath = options.ConfigFilePath ?? config.FileName;
        if (configPath.Length == 0) return default;
        configPath = CompilerPath.Resolve(currentDirectory, configPath);
        configPath = configPath[..^CompilerPath.Extension(configPath).Length];
        if (options.OutDir is { IsEmpty: false } output)
        {
            output = CompilerPath.Resolve(currentDirectory, output);
            configPath = options.RootDir is { IsEmpty: false } root
                ? CompilerPath.Resolve(output, CompilerPath.Relative(CompilerPath.Resolve(currentDirectory, root), configPath, caseSensitive))
                : CompilerPath.Combine(output, CompilerPath.BaseName(configPath));
        }
        return configPath + ".tsbuildinfo"u8;
    }

    public static bool HaveChanges(CompilerOptions? previous, CompilerOptions? current, OptionEffects effects)
    {
        if (ReferenceEquals(previous, current)) return false;
        if (previous is null || current is null) return true;
        foreach (var option in OptionDefinitions.All)
        {
            if (option.Group != OptionGroup.Compiler || (option.Effects & effects) == 0) continue;
            if (option.StrictFlag)
            {
                if (previous.StrictOption(option.Name) != current.StrictOption(option.Name)) return true;
            }
            else if (option.AllowJsFlag)
            {
                if ((previous.AllowJs ?? previous.CheckJs == true) != (current.AllowJs ?? current.CheckJs == true)) return true;
            }
            else
            {
                var oldValue = Value(previous, option);
                var newValue = Value(current, option);
                if (oldValue.HasValue != newValue.HasValue || oldValue is { } old && newValue is { } next && !JsonElement.DeepEquals(old, next)) return true;
            }
        }
        return false;
    }

    internal static JsonElement? ToBuildInfo(CompilerOptions options, Utf8String directory, bool caseSensitive)
    {
        using var stream = new MemoryStream();
        int count = 0;
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var option in OptionDefinitions.All.Where(option => option.Group == OptionGroup.Compiler && (option.Effects & OptionEffects.BuildInfo) != 0))
            {
                if (Value(options, option) is not { } value) continue;
                count++;
                JsonStrings.WriteName(writer, option.Name);
                JsonStrings.WriteValue(writer, ConvertPaths(option, value, path => Relative(directory, path, caseSensitive)));
            }
            writer.WriteEndObject();
        }
        return count == 0 ? null : JsonStrings.Parse(stream);
    }

    internal static CompilerOptions FromBuildInfo(BuildInfo info, Utf8String directory)
    {
        var options = new CompilerOptions();
        if (info.Options is not { } values) return options;
        foreach (var property in values.EnumerateObject())
        {
            var name = JsonStrings.GetName(property);
            if (OptionDefinitions.Find(name) is not { } definition) continue;
            options.Set(name, ConvertPaths(definition, property.Value, path =>
            {
                var absolute = CompilerPath.Resolve(directory, path);
                return absolute.Length > CompilerPath.RootLength(absolute) ? CompilerPath.RemoveTrailingSeparator(absolute) : absolute;
            }));
        }
        return options;
    }

    internal static Utf8String Relative(Utf8String directory, Utf8String path, bool caseSensitive)
    {
        var relative = CompilerPath.Relative(directory, path, caseSensitive);
        return !CompilerPath.IsAbsolute(relative) && relative != "."u8 && relative != ".."u8
            && !relative.StartsWith("./"u8, StringComparison.Ordinal) && !relative.StartsWith("../"u8, StringComparison.Ordinal) ? "./"u8 + relative : relative;
    }

    private static JsonElement? Value(CompilerOptions options, OptionDefinition option)
    {
        if (options.Get(option.Name) is not { } value || value.ValueKind == JsonValueKind.Null) return null;
        if (option.Kind == OptionKind.Enum && value.ValueKind == JsonValueKind.String)
        {
            var text = JsonStrings.GetString(value);
            if (option.ValueIdentity(text) is { } identity)
            {
                using var document = JsonDocument.Parse(OptionDefinitions.EnumValueJson(identity).Memory);
                value = document.RootElement.Clone();
            }
        }
        // Unset enum and string fields have their Go zero value. Boolean false is a distinct tristate.
        if (option.Kind == OptionKind.Enum && value.ValueKind == JsonValueKind.Number && value.GetDouble() == 0
            || option.Kind == OptionKind.String && value.ValueKind == JsonValueKind.String && JsonStrings.GetString(value).IsEmpty) return null;
        return value;
    }

    private static JsonElement ConvertPaths(OptionDefinition option, JsonElement value, Func<Utf8String, Utf8String> convert)
    {
        if (option.ElementIsFilePath && value.ValueKind == JsonValueKind.Array)
            return OptionValues.Array(value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? OptionValues.String(convert(JsonStrings.GetString(item))) : item));
        if (option.IsFilePath && value.ValueKind == JsonValueKind.String && JsonStrings.GetString(value) is { IsEmpty: false } path)
            return OptionValues.String(convert(path));
        return value;
    }
}

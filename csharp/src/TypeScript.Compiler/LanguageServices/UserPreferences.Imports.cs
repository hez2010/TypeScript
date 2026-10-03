using System.Text.Json;
using TypeScript.Compiler.Configuration;

namespace TypeScript.Compiler.LanguageServices;

public enum OrganizeImportsSort { Auto, Ordinal, OrdinalIgnoreCase, Natural, NaturalIgnoreCase }
public enum OrganizeImportsCaseFirst { None, Lower, Upper }
public enum OrganizeImportsTypeOrder { Auto, Last, Inline, First }

public sealed partial record UserPreferences
{
    public bool? PreferTypeOnlyAutoImports { get; init; }
    public bool? AutoImportEntrypointDirectorySearch { get; init; }
    public IReadOnlyList<Utf8String> AutoImportFileExcludePatterns { get; init; } = [];
    public IReadOnlyList<Utf8String> AutoImportSpecifierExcludeRegexes { get; init; } = [];
    public OrganizeImportsSort OrganizeImportsSort { get; init; }
    public bool? OrganizeImportsIgnoreCase { get; init; }
    public bool OrganizeImportsUnicodeCollation { get; init; }
    public bool? OrganizeImportsNumericCollation { get; init; }
    public bool? OrganizeImportsAccentCollation { get; init; }
    public OrganizeImportsCaseFirst OrganizeImportsCaseFirst { get; init; }
    public OrganizeImportsTypeOrder OrganizeImportsTypeOrder { get; init; }

    private UserPreferences ReadImportPreferences(JsonElement value, bool configuration)
    {
        var result = this;
        var organization = configuration ? Get(value, "organizeImports") : value;
        JsonElement Item(string raw, string name) => Get(organization, configuration ? name : raw);
        if (Get(value, "preferTypeOnlyAutoImports") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } typeOnly)
            result = result with { PreferTypeOnlyAutoImports = Boolean(typeOnly) };
        if (Get(value, "autoImportEntrypointDirectorySearch") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } directorySearch)
            result = result with { AutoImportEntrypointDirectorySearch = Boolean(directorySearch) };
        if (Get(value, "autoImportFileExcludePatterns") is { ValueKind: JsonValueKind.Array } excludedFiles)
            result = result with { AutoImportFileExcludePatterns = excludedFiles.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(JsonStrings.GetString).ToArray() };
        if (Get(value, "autoImportSpecifierExcludeRegexes") is { ValueKind: JsonValueKind.Array } excludedSpecifiers)
            result = result with { AutoImportSpecifierExcludeRegexes = excludedSpecifiers.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(JsonStrings.GetString).ToArray() };
        if (Get(value, configuration ? "importModuleSpecifier" : "importModuleSpecifierPreference") is { ValueKind: JsonValueKind.String } specifier)
            result = result with { ImportModuleSpecifierPreference = JsonStrings.GetString(specifier).ToLowerInvariant() };
        if (Get(value, "importModuleSpecifierEnding") is { ValueKind: JsonValueKind.String } ending)
            result = result with { ImportModuleSpecifierEnding = JsonStrings.GetString(ending).ToLowerInvariant() };
        if (Item("organizeImportsSort", "sort") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } sort)
            result = result with { OrganizeImportsSort = sort.ValueKind == JsonValueKind.String ? sort.GetString()?.ToLowerInvariant() switch
                { "ordinal" => OrganizeImportsSort.Ordinal, "ordinalignorecase" => OrganizeImportsSort.OrdinalIgnoreCase,
                    "natural" => OrganizeImportsSort.Natural, "naturalignorecase" => OrganizeImportsSort.NaturalIgnoreCase, _ => OrganizeImportsSort.Auto } : OrganizeImportsSort.Auto };
        if (Item("organizeImportsIgnoreCase", "caseSensitivity") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } ignoreCase)
            result = result with { OrganizeImportsIgnoreCase = configuration && ignoreCase.ValueKind == JsonValueKind.String
                ? ignoreCase.GetString()?.ToLowerInvariant() switch { "caseinsensitive" => true, "casesensitive" => false, _ => null } : Boolean(ignoreCase) };
        if (Item("organizeImportsCollation", "unicodeCollation") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } collation)
            result = result with { OrganizeImportsUnicodeCollation = collation.ValueKind == JsonValueKind.String && collation.GetString()?.ToLowerInvariant() == "unicode" };
        if (Item("organizeImportsNumericCollation", "numericCollation") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } numeric)
            result = result with { OrganizeImportsNumericCollation = Boolean(numeric) };
        if (Item("organizeImportsAccentCollation", "accentCollation") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } accents)
            result = result with { OrganizeImportsAccentCollation = Boolean(accents) };
        if (Item("organizeImportsCaseFirst", "caseFirst") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } caseFirst)
            result = result with { OrganizeImportsCaseFirst = caseFirst.ValueKind == JsonValueKind.String ? caseFirst.GetString() switch
                { "lower" => OrganizeImportsCaseFirst.Lower, "upper" => OrganizeImportsCaseFirst.Upper, _ => OrganizeImportsCaseFirst.None } : OrganizeImportsCaseFirst.None };
        if (Item("organizeImportsTypeOrder", "typeOrder") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } typeOrder)
            result = result with { OrganizeImportsTypeOrder = typeOrder.ValueKind == JsonValueKind.String ? typeOrder.GetString() switch
                { "last" => OrganizeImportsTypeOrder.Last, "inline" => OrganizeImportsTypeOrder.Inline, "first" => OrganizeImportsTypeOrder.First, _ => OrganizeImportsTypeOrder.Auto } : OrganizeImportsTypeOrder.Auto };
        return result;
    }
}

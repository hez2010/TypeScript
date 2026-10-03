using System.Text.Json;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial record UserPreferences
{
    public Utf8String Locale { get; init; }
    public Utf8String CustomConfigFileName { get; init; }
    public FormatCodeSettings FormatCodeSettings { get; init; } = new();
    public bool? EnableValidation { get; init; } = true;
    public bool? ReportStyleChecksAsWarnings { get; init; } = true;
    public bool? EnableFormatting { get; init; } = true;
    public bool? ExcludeLibrarySymbolsInNavTo { get; init; } = true;
    public Utf8String WorkspaceSymbolsScope { get; init; } = "allOpenProjects"u8;
    public int MaximumHoverLength { get; init; }
    public bool? IncludeAutomaticOptionalChainCompletions { get; init; } = true;
    public bool? IncludeCompletionsForImportStatements { get; init; } = true;
    public bool? IncludeCompletionsForModuleExports { get; init; } = true;
    public bool? IncludeCompletionsWithObjectLiteralMethodSnippets { get; init; }
    public bool? IncludeCompletionsWithClassMemberSnippets { get; init; }
    public Utf8String JsxAttributeCompletionStyle { get; init; }
    public bool PreferGoToSourceDefinition { get; init; }
    public bool UseAliasesForRename { get; init; } = true;
    public bool AllowRenameOfImportPath { get; init; } = true;
    public CodeLensPreferences CodeLens { get; init; } = new();
    public InlayHintPreferences InlayHints { get; init; } = new();
    public bool? EnableAutoClosingTags { get; init; } = true;
    public bool? EnableJSDocCompletions { get; init; } = true;
    public bool? GenerateReturnInDocTemplate { get; init; } = true;
    public Utf8String QuotePreference { get; init; }
    public Utf8String ImportModuleSpecifierPreference { get; init; }
    public Utf8String ImportModuleSpecifierEnding { get; init; }

    internal static UserPreferences ReadConfiguration(JsonElement configuration)
    {
        JsonElement Section(string name, int index) => configuration.ValueKind == JsonValueKind.Array
            ? index < configuration.GetArrayLength() ? configuration[index] : default : Get(configuration, name);
        UserPreferences result = new();
        var editor = Section("editor", 3);
        if (editor.ValueKind == JsonValueKind.Object)
        {
            var format = FormatCodeSettings.Read(editor, configuration: true);
            if (!editor.TryGetProperty("indentSize", out _) && Get(editor, "tabSize").ValueKind == JsonValueKind.Number) format = format with { IndentSize = format.TabSize };
            if (!editor.TryGetProperty("convertTabsToSpaces", out _) && Get(editor, "insertSpaces") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } spaces)
                format = format with { ConvertTabsToSpaces = Boolean(spaces) };
            result = result with { FormatCodeSettings = format };
        }
        foreach (var section in new[] { Section("javascript", 2), Section("typescript", 1), Section("js/ts", 0) }) result = result.WithConfig(section);
        return result;
    }

    internal UserPreferences WithConfig(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object) return this;
        var result = this;
        foreach (var raw in new[] { config, Get(config, "unstable") })
        {
            result = result.ReadImportPreferences(raw, false);
            if (Get(raw, "includeCompletionsWithObjectLiteralMethodSnippets") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } objectSnippets)
                result = result with { IncludeCompletionsWithObjectLiteralMethodSnippets = Boolean(objectSnippets) };
            if (Get(raw, "includeCompletionsWithClassMemberSnippets") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } classSnippets)
                result = result with { IncludeCompletionsWithClassMemberSnippets = Boolean(classSnippets) };
            if (Get(raw, "includeAutomaticOptionalChainCompletions") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } optionalChains)
                result = result with { IncludeAutomaticOptionalChainCompletions = Boolean(optionalChains) };
            if (Get(raw, "includeCompletionsForImportStatements") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } importStatements)
                result = result with { IncludeCompletionsForImportStatements = Boolean(importStatements) };
            if (Get(raw, "includeCompletionsForModuleExports") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } moduleExports)
                result = result with { IncludeCompletionsForModuleExports = Boolean(moduleExports) };
            if (Get(raw, "jsxAttributeCompletionStyle") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } jsxStyle)
                result = result with { JsxAttributeCompletionStyle = JsxStyle(jsxStyle) };
            if (Get(raw, "locale") is { ValueKind: JsonValueKind.String } locale) result = result with { Locale = Configuration.JsonStrings.GetString(locale) };
            if (Get(raw, "customConfigFileName") is { ValueKind: JsonValueKind.String } configName) result = result with { CustomConfigFileName = Configuration.JsonStrings.GetString(configName) };
            if (Get(raw, "validateEnabled") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } validate)
                result = result with { EnableValidation = Boolean(validate) };
            if (Get(raw, "reportStyleChecksAsWarnings") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } style)
                result = result with { ReportStyleChecksAsWarnings = Boolean(style) };
            result = result with { CodeLens = ReadCodeLens(raw, result.CodeLens, false) };
            result = result with { InlayHints = ReadInlayHints(raw, result.InlayHints, false) };
            if (Get(raw, "autoClosingTags") is { ValueKind: JsonValueKind.True or JsonValueKind.False } autoClosing)
                result = result with { EnableAutoClosingTags = autoClosing.GetBoolean() };
            if (Get(raw, "completeJSDocs") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } jsdoc)
                result = result with { EnableJSDocCompletions = Boolean(jsdoc) };
            if (Get(raw, "generateReturnInDocTemplate") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } returns)
                result = result with { GenerateReturnInDocTemplate = Boolean(returns) };
            result = result with { FormatCodeSettings = FormatCodeSettings.Read(raw, result.FormatCodeSettings, configuration: true) };
            if (Get(raw, "formatEnabled") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } enabled)
                result = result with { EnableFormatting = Boolean(enabled) };
            if (Get(raw, "excludeLibrarySymbolsInNavTo") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } exclude)
                result = result with { ExcludeLibrarySymbolsInNavTo = Boolean(exclude) };
            if (Get(raw, "maximumHoverLength") is { ValueKind: JsonValueKind.Number } maximum)
                result = result with { MaximumHoverLength = maximum.GetInt32() };
            if (Get(raw, "preferGoToSourceDefinition") is { ValueKind: JsonValueKind.True or JsonValueKind.False } sourceDefinition)
                result = result with { PreferGoToSourceDefinition = sourceDefinition.GetBoolean() };
            if (Get(raw, "useAliasesForRename") is { ValueKind: JsonValueKind.True or JsonValueKind.False } aliases)
                result = result with { UseAliasesForRename = aliases.GetBoolean() };
            if (Get(raw, "allowRenameOfImportPath") is { ValueKind: JsonValueKind.True or JsonValueKind.False } imports)
                result = result with { AllowRenameOfImportPath = imports.GetBoolean() };
            if (Get(raw, "quotePreference") is { ValueKind: JsonValueKind.String } quote)
                result = result with { QuotePreference = Configuration.JsonStrings.GetString(quote) };
        }
        var validation = Get(config, "validate");
        var validationEnabled = Get(validation, "enabled");
        if (validationEnabled.ValueKind == JsonValueKind.Undefined) validationEnabled = Get(validation, "enable");
        if (validationEnabled.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) result = result with { EnableValidation = Boolean(validationEnabled) };
        if (Get(config, "reportStyleChecksAsWarnings") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } styleEnabled)
            result = result with { ReportStyleChecksAsWarnings = Boolean(styleEnabled) };
        var preferences = Get(config, "preferences");
        result = result.ReadImportPreferences(preferences, true);
        if (Get(preferences, "jsxAttributeCompletionStyle") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } jsxCompletionStyle)
            result = result with { JsxAttributeCompletionStyle = JsxStyle(jsxCompletionStyle) };
        var suggest = Get(config, "suggest");
        if (Get(Get(suggest, "objectLiteralMethodSnippets"), "enabled") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } objectMethodSnippets)
            result = result with { IncludeCompletionsWithObjectLiteralMethodSnippets = Boolean(objectMethodSnippets) };
        if (Get(Get(suggest, "classMemberSnippets"), "enabled") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } classMemberSnippets)
            result = result with { IncludeCompletionsWithClassMemberSnippets = Boolean(classMemberSnippets) };
        if (Get(suggest, "includeAutomaticOptionalChainCompletions") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } optionalChainCompletions)
            result = result with { IncludeAutomaticOptionalChainCompletions = Boolean(optionalChainCompletions) };
        if (Get(suggest, "includeCompletionsForImportStatements") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } statementCompletions)
            result = result with { IncludeCompletionsForImportStatements = Boolean(statementCompletions) };
        if (Get(suggest, "autoImports") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } autoImports)
            result = result with { IncludeCompletionsForModuleExports = Boolean(autoImports) };
        var jsdocSettings = Get(suggest, "jsdoc");
        var jsdocEnabled = Get(jsdocSettings, "enabled");
        if (jsdocEnabled.ValueKind == JsonValueKind.Undefined) jsdocEnabled = Get(suggest, "completeJSDocs");
        if (jsdocEnabled.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) result = result with { EnableJSDocCompletions = Boolean(jsdocEnabled) };
        if (Get(jsdocSettings, "generateReturns") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } generateReturns)
            result = result with { GenerateReturnInDocTemplate = Boolean(generateReturns) };
        result = result with { CodeLens = ReadCodeLens(config, result.CodeLens, true) };
        result = result with { InlayHints = ReadInlayHints(config, result.InlayHints, true) };
        if (Get(Get(config, "autoClosingTags"), "enabled") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } autoClosingEnabled)
            result = result with { EnableAutoClosingTags = Boolean(autoClosingEnabled) };
        if (Get(preferences, "useAliasesForRenames") is { ValueKind: JsonValueKind.True or JsonValueKind.False } renameAliases)
            result = result with { UseAliasesForRename = renameAliases.GetBoolean() };
        if (Get(preferences, "quoteStyle") is { ValueKind: JsonValueKind.String } quoteStyle)
            result = result with { QuotePreference = Configuration.JsonStrings.GetString(quoteStyle) };
        if (Get(preferences, "importModuleSpecifier") is { ValueKind: JsonValueKind.String } specifier)
            result = result with { ImportModuleSpecifierPreference = Configuration.JsonStrings.GetString(specifier) };
        if (Get(preferences, "importModuleSpecifierEnding") is { ValueKind: JsonValueKind.String } ending)
            result = result with { ImportModuleSpecifierEnding = Configuration.JsonStrings.GetString(ending) };
        var format = Get(config, "format");
        result = result with { FormatCodeSettings = FormatCodeSettings.Read(format, result.FormatCodeSettings, configuration: true) };
        var enable = Get(format, "enabled");
        if (enable.ValueKind == JsonValueKind.Undefined) enable = Get(format, "enable");
        if (enable.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) result = result with { EnableFormatting = Boolean(enable) };
        var symbols = Get(config, "workspaceSymbols");
        if (Get(symbols, "excludeLibrarySymbols") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } excludeLibrary)
            result = result with { ExcludeLibrarySymbolsInNavTo = Boolean(excludeLibrary) };
        if (Get(symbols, "scope") is { ValueKind: JsonValueKind.String } scope)
            result = result with { WorkspaceSymbolsScope = Configuration.JsonStrings.GetString(scope) };
        return result;
    }

    private static JsonElement Get(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    private static bool? Boolean(JsonElement value) => value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static Utf8String JsxStyle(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString() is "braces" or "none"
        ? Configuration.JsonStrings.GetString(value) : "auto"u8;

    private static InlayHintPreferences ReadInlayHints(JsonElement value, InlayHintPreferences previous, bool configuration)
    {
        JsonElement Item(string raw, string section, string property) => configuration
            ? Get(Get(Get(value, "inlayHints"), section), property) : Get(value, raw);
        bool? Read(string raw, string section, string property, bool? fallback, bool invert = false)
        {
            var item = Item(raw, section, property);
            if (item.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return fallback;
            var result = Boolean(item);
            return configuration && invert ? !result : result;
        }
        var names = Item("includeInlayParameterNameHints", "parameterNames", "enabled");
        return previous with
        {
            ParameterNames = names.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? previous.ParameterNames
                : names.ValueKind == JsonValueKind.String && names.GetString() is "all" or "literals" ? Configuration.JsonStrings.GetString(names) : default,
            ParameterNamesWhenArgumentMatches = Read("includeInlayParameterNameHintsWhenArgumentMatchesName", "parameterNames", "suppressWhenArgumentMatchesName", previous.ParameterNamesWhenArgumentMatches, true),
            ParameterTypes = Read("includeInlayFunctionParameterTypeHints", "parameterTypes", "enabled", previous.ParameterTypes),
            VariableTypes = Read("includeInlayVariableTypeHints", "variableTypes", "enabled", previous.VariableTypes),
            VariableTypesWhenNameMatches = Read("includeInlayVariableTypeHintsWhenTypeMatchesName", "variableTypes", "suppressWhenTypeMatchesName", previous.VariableTypesWhenNameMatches, true),
            PropertyTypes = Read("includeInlayPropertyDeclarationTypeHints", "propertyDeclarationTypes", "enabled", previous.PropertyTypes),
            ReturnTypes = Read("includeInlayFunctionLikeReturnTypeHints", "functionLikeReturnTypes", "enabled", previous.ReturnTypes),
            EnumValues = Read("includeInlayEnumMemberValueHints", "enumMemberValues", "enabled", previous.EnumValues),
        };
    }

    private static CodeLensPreferences ReadCodeLens(JsonElement value, CodeLensPreferences previous, bool configuration)
    {
        bool? Read(string raw, string section, string property, bool? fallback)
        {
            var item = configuration ? Get(Get(value, section), property) : Get(value, raw);
            return item.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? fallback : Boolean(item);
        }
        return previous with
        {
            ReferencesEnabled = Read("referencesCodeLensEnabled", "referencesCodeLens", "enabled", previous.ReferencesEnabled),
            ImplementationsEnabled = Read("implementationsCodeLensEnabled", "implementationsCodeLens", "enabled", previous.ImplementationsEnabled),
            ShowOnAllFunctions = Read("referencesCodeLensShowOnAllFunctions", "referencesCodeLens", "showOnAllFunctions", previous.ShowOnAllFunctions),
            ShowOnInterfaceMethods = Read("implementationsCodeLensShowOnInterfaceMethods", "implementationsCodeLens", "showOnInterfaceMethods", previous.ShowOnInterfaceMethods),
            ShowOnAllClassMethods = Read("implementationsCodeLensShowOnAllClassMethods", "implementationsCodeLens", "showOnAllClassMethods", previous.ShowOnAllClassMethods),
        };
    }
}

public sealed record CodeLensPreferences(bool? ReferencesEnabled = null, bool? ImplementationsEnabled = null,
    bool? ShowOnAllFunctions = null, bool? ShowOnInterfaceMethods = null, bool? ShowOnAllClassMethods = null);

public sealed record InlayHintPreferences(Utf8String ParameterNames = default, bool? ParameterNamesWhenArgumentMatches = null,
    bool? ParameterTypes = null, bool? VariableTypes = null, bool? VariableTypesWhenNameMatches = null,
    bool? PropertyTypes = null, bool? ReturnTypes = null, bool? EnumValues = null)
{
    internal bool Enabled => !ParameterNames.IsEmpty || ParameterTypes == true || VariableTypes == true
        || PropertyTypes == true || ReturnTypes == true || EnumValues == true;
}

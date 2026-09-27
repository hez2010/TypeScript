using TypeScript.Compiler.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;

namespace TypeScript.Compiler.Configuration;

public sealed record ContentMapper(string Package, string[] Extensions, JsonElement? Options, string PackageDirectory,
    string Name, string Version, string[] Exec, JsonElement? CompilerOptions, bool DynamicConfig)
{
    public SourceFileNode? SourceFile { get; init; }
    public SyntaxNode? OptionsSyntax { get; init; }
}

public sealed partial class ConfigParser
{
    private ContentMapper[] ReadContentMappers(
        JsonElement? raw,
        ConfigSyntax source,
        CompilerOptions options,
        string directory,
        List<Diagnostic> errors)
    {
        if (raw is null || raw.Value.ValueKind == JsonValueKind.Null)
            return [];
        void Error(DiagnosticMessage message, params TextSlice[] args) =>
            errors.Add(source.Diagnostic(message, source.Value("contentMappers"), args));
        if (raw.Value.ValueKind != JsonValueKind.Array)
        {
            Error(Messages.Compiler_option_0_requires_a_value_of_type_1, "contentMappers", "Array");
            return [];
        }
        var definitions = new List<(string Package, string[] Extensions, JsonElement? Options, SyntaxNode? OptionsSyntax)>();
        var seen = new HashSet<string>(fileSystem.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        string[] builtins = [".ts", ".tsx", ".d.ts", ".mts", ".cts", ".d.mts", ".d.cts", ".js", ".jsx", ".mjs", ".cjs", ".json"];
        int index = 0;
        foreach (var mapper in raw.Value.EnumerateArray())
        {
            SyntaxNode? syntax = source.Value("contentMappers") is ArrayLiteralExpressionNode array && index < (array.Elements?.Count ?? 0)
                ? array.Elements![index]
                : null;
            index++;
            if (mapper.ValueKind != JsonValueKind.Object)
            {
                if (mapper.ValueKind != JsonValueKind.Null)
                    Error(Messages.Compiler_option_0_requires_a_value_of_type_1, "contentMappers", "object");
                continue;
            }
            bool valid = true;
            if (!mapper.TryGetProperty("package", out var package)
                || package.ValueKind != JsonValueKind.String
                || JsonStrings.GetString(package) == "")
            {
                Error(Messages.Compiler_option_0_requires_a_value_of_type_1, "contentMapper.package", "string");
                valid = false;
            }
            if (!mapper.TryGetProperty("extensions", out var extensions)
                || extensions.ValueKind != JsonValueKind.Array
                || extensions.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
            {
                Error(Messages.Compiler_option_0_requires_a_value_of_type_1, "contentMapper.extensions", "string[]");
                valid = false;
            }
            JsonElement? mapperOptions = null;
            if (mapper.TryGetProperty("options", out var value))
            {
                if (value.ValueKind != JsonValueKind.Object)
                {
                    Error(Messages.Compiler_option_0_requires_a_value_of_type_1, "contentMapper.options", "object");
                    valid = false;
                }
                else
                    mapperOptions = value;
            }
            if (!valid)
                continue;
            var accepted = new List<string>();
            foreach (var item in extensions.EnumerateArray())
            {
                string extension = JsonStrings.GetString(item);
                if (!extension.StartsWith('.'))
                    Error(Messages.Content_mapper_file_extension_0_must_begin_with_a, extension);
                else if (builtins.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    Error(
                        Messages.Content_mapper_file_extension_0_is_a_built_in_extension_and_cannot_be_registered_by_a_content_mapper,
                        extension);
                else if (!seen.Add(extension))
                    Error(Messages.Content_mapper_file_extension_0_is_registered_by_more_than_one_content_mapper, extension);
                else
                    accepted.Add(extension);
            }
            SyntaxNode? optionSyntax = syntax is ObjectLiteralExpressionNode obj
                ? obj.Properties?.OfType<PropertyAssignmentNode>().LastOrDefault(
                    p => p.Name is StringLiteralNode { Text.Span: "options" } || p.Name is IdentifierNode { Text.Span: "options" })?.Initializer
                : null;
            definitions.Add((JsonStrings.GetString(package), accepted.ToArray(), mapperOptions, optionSyntax));
        }
        if (definitions.Count > 0 && options.Boolean("runExternalCode") != true)
        {
            Error(Messages.Content_mappers_require_the_runExternalCode_command_line_flag_to_be_enabled);
            return [];
        }
        var result = new List<ContentMapper>();
        foreach (var definition in definitions)
        {
            string? packageDirectory = new ModuleResolver(fileSystem, new CompilerOptions(), currentDirectory)
                .ResolvePackageDirectory(definition.Package, CompilerPath.Combine(directory, "tsconfig.json"));
            if (packageDirectory is null || !fileSystem.FileExists(CompilerPath.Combine(packageDirectory, "package.json")))
            {
                Error(Messages.The_content_mapper_package_0_could_not_be_resolved, definition.Package);
                continue;
            }
            JsonElement package;
            try
            {
                using var document = JsonDocument.Parse(
                    SourceEncoding.Decode(fileSystem.ReadFile(CompilerPath.Combine(packageDirectory, "package.json"))!),
                    new JsonDocumentOptions { MaxDepth = int.MaxValue });
                package = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                Error(Messages.The_package_json_of_the_content_mapper_package_0_could_not_be_parsed, definition.Package);
                continue;
            }
            if (package.ValueKind != JsonValueKind.Object
                || !package.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || JsonStrings.GetString(name) == "")
            {
                Error(Messages.The_package_json_of_the_content_mapper_package_0_does_not_specify_a_name, definition.Package);
                continue;
            }
            if (!package.TryGetProperty("typescript", out var typescript)
                || typescript.ValueKind != JsonValueKind.Object
                || !typescript.TryGetProperty("contentMapper", out var mapper)
                || mapper.ValueKind != JsonValueKind.Object)
            {
                Error(
                    Messages.The_package_json_of_the_content_mapper_package_0_does_not_declare_a_typescript_contentMapper_object,
                    definition.Package);
                continue;
            }
            if (!mapper.TryGetProperty("exec", out var exec)
                || exec.ValueKind != JsonValueKind.Array
                || exec.GetArrayLength() == 0
                || exec.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
            {
                Error(
                    Messages.The_typescript_contentMapper_exec_of_the_content_mapper_package_0_must_be_a_non_empty_array_of_strings,
                    definition.Package);
                continue;
            }
            string version = package.TryGetProperty("version", out var versionValue) && versionValue.ValueKind == JsonValueKind.String
                ? JsonStrings.GetString(versionValue)
                : "";
            JsonElement? compiler = mapper.TryGetProperty("compilerOptions", out var compilerValue) ? compilerValue : null;
            bool dynamic = mapper.TryGetProperty("dynamicConfig", out var dynamicValue) && dynamicValue.ValueKind == JsonValueKind.True;
            result.Add(
                new(
                    definition.Package,
                    definition.Extensions,
                    definition.Options,
                    packageDirectory,
                    JsonStrings.GetString(name),
                    version,
                    exec.EnumerateArray().Select(v => JsonStrings.GetString(v)).ToArray(),
                    compiler,
                    dynamic)
                { SourceFile = source.Source, OptionsSyntax = definition.OptionsSyntax });
        }
        return result.ToArray();
    }
}

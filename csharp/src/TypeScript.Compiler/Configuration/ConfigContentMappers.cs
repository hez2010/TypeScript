using TypeScript.Compiler.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;

namespace TypeScript.Compiler.Configuration;

public sealed record ContentMapper(Utf8String Package, Utf8String[] Extensions, JsonElement? Options, Utf8String PackageDirectory,
    Utf8String Name, Utf8String Version, Utf8String[] Exec, JsonElement? CompilerOptions, bool DynamicConfig)
{
    public SourceFileNode? SourceFile { get; init; }
    public SyntaxNode? OptionsSyntax { get; init; }
    public Utf8String ContributionId { get; init; }
}

public sealed partial class ConfigParser
{
    private ContentMapper[] ReadContentMappers(
        JsonElement? raw,
        ConfigSyntax source,
        CompilerOptions options,
        Utf8String directory,
        List<Diagnostic> errors)
    {
        if (raw is null || raw.Value.ValueKind == JsonValueKind.Null)
            return [];
        void Error(DiagnosticMessage message, params Utf8String[] args) =>
            errors.Add(source.Diagnostic(message, source.Value(Utf8Literals.ContentMappers), args));
        if (raw.Value.ValueKind != JsonValueKind.Array)
        {
            Error(Messages.Compiler_option_0_requires_a_value_of_type_1, Utf8Literals.ContentMappers, Utf8Literals.Array);
            return [];
        }
        var definitions = new List<(Utf8String Package, Utf8String[] Extensions, JsonElement? Options, SyntaxNode? OptionsSyntax)>();
        var seen = new HashSet<Utf8String>(fileSystem.CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase);
        Utf8String[] builtins = [Utf8Literals.Ts, Utf8Literals.Tsx, Utf8Literals.DTs, Utf8Literals.Mts, Utf8Literals.Cts, Utf8Literals.DMts, Utf8Literals.DCts, Utf8Literals.Js, Utf8Literals.Jsx, Utf8Literals.Mjs, Utf8Literals.Cjs, Utf8Literals.Json];
        int index = 0;
        foreach (var mapper in raw.Value.EnumerateArray())
        {
            SyntaxNode? syntax = source.Value(Utf8Literals.ContentMappers) is ArrayLiteralExpressionNode array && index < (array.Elements?.Count ?? 0)
                ? array.Elements![index]
                : null;
            index++;
            if (mapper.ValueKind != JsonValueKind.Object)
            {
                if (mapper.ValueKind != JsonValueKind.Null)
                    Error(Messages.Compiler_option_0_requires_a_value_of_type_1, Utf8Literals.ContentMappers, Utf8Literals.Object);
                continue;
            }
            bool valid = true;
            if (!mapper.TryGetProperty("package"u8, out var package)
                || package.ValueKind != JsonValueKind.String
                || JsonStrings.GetString(package) == Utf8String.Empty)
            {
                Error(Messages.Compiler_option_0_requires_a_value_of_type_1, Utf8Literals.ContentMapperPackage, Utf8Literals.StringKeyword);
                valid = false;
            }
            if (!mapper.TryGetProperty("extensions"u8, out var extensions)
                || extensions.ValueKind != JsonValueKind.Array
                || extensions.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
            {
                Error(Messages.Compiler_option_0_requires_a_value_of_type_1, Utf8Literals.ContentMapperExtensions, Utf8Literals.StringArray);
                valid = false;
            }
            JsonElement? mapperOptions = null;
            if (mapper.TryGetProperty("options"u8, out var value))
            {
                if (value.ValueKind != JsonValueKind.Object)
                {
                    Error(Messages.Compiler_option_0_requires_a_value_of_type_1, Utf8Literals.ContentMapperOptions, Utf8Literals.Object);
                    valid = false;
                }
                else
                    mapperOptions = value;
            }
            if (!valid)
                continue;
            var accepted = new List<Utf8String>();
            foreach (var item in extensions.EnumerateArray())
            {
                Utf8String extension = JsonStrings.GetString(item);
                if (!extension.StartsWith((byte)'.'))
                    Error(Messages.Content_mapper_file_extension_0_must_begin_with_a, extension);
                else if (builtins.Contains(extension, Utf8StringComparer.OrdinalIgnoreCase))
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
                    p => p.Name is StringLiteralNode { Text.Span: var matchedText } && matchedText.SequenceEqual("options"u8) || p.Name is IdentifierNode { Text.Span: var matchedText2 } && matchedText2.SequenceEqual("options"u8))?.Initializer
                : null;
            definitions.Add((JsonStrings.GetString(package), accepted.ToArray(), mapperOptions, optionSyntax));
        }
        if (definitions.Count > 0 && options.RunExternalCode != true)
        {
            Error(Messages.Content_mappers_require_the_runExternalCode_command_line_flag_to_be_enabled);
            return [];
        }
        var result = new List<ContentMapper>();
        foreach (var definition in definitions)
        {
            Utf8String? packageDirectory = new ModuleResolver(fileSystem, new CompilerOptions(), currentDirectory)
                .ResolvePackageDirectory(definition.Package, CompilerPath.Combine(directory, Utf8Literals.TsconfigJson));
            if (packageDirectory is null || !fileSystem.FileExists(CompilerPath.Combine(packageDirectory.Value, Utf8Literals.PackageJson)))
            {
                Error(Messages.The_content_mapper_package_0_could_not_be_resolved, definition.Package);
                continue;
            }
            JsonElement package;
            try
            {
                using var document = JsonDocument.Parse(
                    SourceEncoding.DecodeBytes(fileSystem.ReadFile(CompilerPath.Combine(packageDirectory.Value, Utf8Literals.PackageJson))!),
                    new JsonDocumentOptions { MaxDepth = int.MaxValue });
                package = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                Error(Messages.The_package_json_of_the_content_mapper_package_0_could_not_be_parsed, definition.Package);
                continue;
            }
            if (package.ValueKind != JsonValueKind.Object
                || !package.TryGetProperty("name"u8, out var name)
                || name.ValueKind != JsonValueKind.String
                || JsonStrings.GetString(name) == Utf8String.Empty)
            {
                Error(Messages.The_package_json_of_the_content_mapper_package_0_does_not_specify_a_name, definition.Package);
                continue;
            }
            if (!package.TryGetProperty("typescript"u8, out var typescript)
                || typescript.ValueKind != JsonValueKind.Object
                || !typescript.TryGetProperty("contentMapper"u8, out var mapper)
                || mapper.ValueKind != JsonValueKind.Object)
            {
                Error(
                    Messages.The_package_json_of_the_content_mapper_package_0_does_not_declare_a_typescript_contentMapper_object,
                    definition.Package);
                continue;
            }
            if (!mapper.TryGetProperty("exec"u8, out var exec)
                || exec.ValueKind != JsonValueKind.Array
                || exec.GetArrayLength() == 0
                || exec.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
            {
                Error(
                    Messages.The_typescript_contentMapper_exec_of_the_content_mapper_package_0_must_be_a_non_empty_array_of_strings,
                    definition.Package);
                continue;
            }
            Utf8String version = package.TryGetProperty("version"u8, out var versionValue) && versionValue.ValueKind == JsonValueKind.String
                ? JsonStrings.GetString(versionValue)
                : Utf8String.Empty;
            JsonElement? compiler = mapper.TryGetProperty("compilerOptions"u8, out var compilerValue) ? compilerValue : null;
            bool dynamic = mapper.TryGetProperty("dynamicConfig"u8, out var dynamicValue) && dynamicValue.ValueKind == JsonValueKind.True;
            result.Add(
                new(
                    definition.Package,
                    definition.Extensions,
                    definition.Options,
                    packageDirectory.Value,
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

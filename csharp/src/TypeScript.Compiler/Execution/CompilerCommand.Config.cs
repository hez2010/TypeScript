using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Execution;

public sealed partial class CompilerCommand
{
    private void WriteConfig(CompilerOptions raw)
    {
        var path = CompilerPath.Combine(currentDirectory, "tsconfig.json"u8);
        if (fileSystem.FileExists(path)) { ReportError(Messages.A_tsconfig_json_file_is_already_defined_at_Colon_0, path); return; }
        var lines = new List<string> { "{", "  // " + Localize(Messages.Visit_https_Colon_Slash_Slashaka_ms_Slashtsconfig_to_read_more_about_this_file), "  \"compilerOptions\": {" };
        var remaining = raw.Values.Keys.Where(name => name != "init"u8 && name != "help"u8 && name != "watch"u8).ToList();
        void Header(DiagnosticMessage message) => lines.Add("    // " + Localize(message));
        void Emit(string name, string fallback, bool optional = false)
        {
            var key = Utf8String.FromString(name); remaining.Remove(key);
            var value = raw.Get(key);
            string text = value is null ? fallback : Value(value.Value);
            lines.Add("    " + (optional && value is null ? "// " : "") + "\"" + name + "\": " + text + ",");
        }
        string Value(JsonElement value) => value.ValueKind == JsonValueKind.Array
            ? "[" + string.Join(", ", value.EnumerateArray().Select(Value)) + "]" : value.GetRawText();
        Header(Messages.File_Layout); Emit("rootDir", "\"./src\"", true); Emit("outDir", "\"./dist\"", true); lines.Add("");
        Header(Messages.Environment_Settings); Header(Messages.See_also_https_Colon_Slash_Slashaka_ms_Slashtsconfig_Slashmodule);
        Emit("module", "\"nodenext\""); Emit("target", "\"esnext\""); Emit("types", "[]");
        if (raw.Get("lib"u8) is not null) Emit("lib", "[]");
        Header(Messages.For_nodejs_Colon); lines.Add("    // \"lib\": [\"esnext\"],"); lines.Add("    // \"types\": [\"node\"],"); Header(Messages.X_and_npm_install_D_types_Slashnode); lines.Add("");
        Header(Messages.Other_Outputs); Emit("sourceMap", "true"); Emit("declaration", "true"); Emit("declarationMap", "true"); lines.Add("");
        Header(Messages.Stricter_Typechecking_Options); Emit("noUncheckedIndexedAccess", "true"); Emit("exactOptionalPropertyTypes", "true"); lines.Add("");
        Header(Messages.Style_Options);
        foreach (var name in new[] { "noImplicitReturns", "noImplicitOverride", "noUnusedLocals", "noUnusedParameters", "noFallthroughCasesInSwitch", "noPropertyAccessFromIndexSignature" }) Emit(name, "true", true);
        lines.Add(""); Header(Messages.Recommended_Options);
        Emit("strict", "true"); Emit("jsx", "\"react-jsx\""); Emit("verbatimModuleSyntax", "true"); Emit("isolatedModules", "true");
        Emit("noUncheckedSideEffectImports", "true"); Emit("moduleDetection", "\"force\""); Emit("skipLibCheck", "true");
        if (remaining.Count != 0) lines.Add("");
        while (remaining.Count != 0) Emit(remaining[0].ToString(), "null");
        lines.Add("  }"); lines.Add("}"); lines.Add("");
        try { fileSystem.WriteFile(path, Utf8String.FromString(string.Join('\n', lines))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        output.Write('\n'); this.Header("Created a new tsconfig.json"); output.Write("You can learn more at https://aka.ms/tsconfig\n");
    }

    private void ShowConfig(ParsedConfig config)
    {
        var directory = config.FileName.IsEmpty ? currentDirectory : CompilerPath.DirectoryName(config.FileName);
        Utf8String Relative(Utf8String path)
        {
            var relative = CompilerPath.Relative(directory, CompilerPath.Resolve(directory, path), fileSystem.CaseSensitive);
            return !CompilerPath.IsAbsolute(relative) && relative != "."u8 && relative != ".."u8
                && !relative.StartsWith("./"u8, StringComparison.Ordinal) && !relative.StartsWith("../"u8, StringComparison.Ordinal) ? "./"u8 + relative : relative;
        }
        var values = new Dictionary<Utf8String, JsonElement>(Utf8StringComparer.Ordinal);
        foreach (var name in OptionDefinitions.CompilerFieldOrder)
        {
            var key = Utf8String.FromString(name); var definition = OptionDefinitions.Find(key);
            if (definition is null || definition.Category == Messages.Command_line_Options || definition.Category == Messages.Output_Formatting) continue;
            if (config.Options.Get(key) is not { } value || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind == JsonValueKind.String && JsonStrings.GetString(value).IsEmpty) continue;
            if (name is "showConfig" or "configFile" or "configFilePath" or "help" or "init" or "listFilesOnly" or "listEmittedFiles" or "project" or "build" or "version") continue;
            values[key] = CanonicalOptionValue(definition, value, Relative);
        }
        var provided = values.Keys.ToHashSet();
        var defaults = new CompilerOptions();
        void Implied(string name, string[] dependencies, Func<CompilerOptions, string> compute)
        {
            var key = Utf8String.FromString(name);
            if (provided.Contains(key) || !dependencies.Any(name => provided.Contains(Utf8String.FromString(name)))) return;
            string value = compute(config.Options);
            if (value == compute(defaults)) return;
            using var document = JsonDocument.Parse(value);
            values[key] = CanonicalOptionValue(OptionDefinitions.Find(key)!, document.RootElement, Relative);
        }
        string Number(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string Boolean(bool value) => value ? "true" : "false";
        Implied("module", ["target"], value => Number(value.EmitModuleKind));
        Implied("moduleResolution", ["module", "target"], value => Number((int)value.EmitModuleResolutionKind));
        Implied("moduleDetection", ["module", "target"], value => Number((int)value.EmitModuleDetectionKind));
        Implied("isolatedModules", ["verbatimModuleSyntax"], value => Boolean(value.IsolatedModules == true || value.VerbatimModuleSyntax == true));
        Implied("preserveConstEnums", ["isolatedModules", "verbatimModuleSyntax"], value => Boolean(value.PreserveConstEnums == true || value.IsolatedModules == true || value.VerbatimModuleSyntax == true));
        Implied("declaration", ["composite"], value => Boolean(value.Declaration == true || value.Composite == true));
        Implied("declarationMap", ["declaration", "composite"], value => Boolean(value.DeclarationMap == true && (value.Declaration == true || value.Composite == true)));
        Implied("incremental", ["composite"], value => Boolean(value.Incremental == true || value.Composite == true));
        Implied("useDefineForClassFields", ["target", "module"], value => Boolean(value.UseDefineForClassFields ?? value.EmitTargetYear >= 2022));
        Implied("resolveJsonModule", ["moduleResolution", "module", "target"], value => Boolean(value.ResolveJsonModule ?? (value.EmitModule is ModuleKind.Node20 or ModuleKind.NodeNext || value.EmitModuleResolutionKind == ModuleResolutionKind.Bundler)));
        Implied("allowJs", ["checkJs"], value => Boolean(value.AllowJs ?? value.CheckJs == true));
        Implied("allowImportingTsExtensions", ["rewriteRelativeImportExtensions"], value => Boolean(value.AllowImportingTsExtensions == true || value.RewriteRelativeImportExtensions == true));
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new() { Indented = true, IndentSize = 4, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject(); writer.WriteStartObject("compilerOptions");
            foreach (var (key, value) in values) { writer.WritePropertyName(key.Span); value.WriteTo(writer); }
            writer.WriteEndObject();
            if (config.References.Length != 0)
            {
                writer.WriteStartArray("references");
                foreach (var reference in config.References)
                {
                    writer.WriteStartObject(); writer.WriteString("path", Relative(reference.Path).Span);
                    if (reference.Circular) writer.WriteBoolean("circular", true); writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            void Array(string name, IEnumerable<Utf8String> entries)
            {
                var list = entries.ToArray(); if (list.Length == 0) return;
                writer.WriteStartArray(name); foreach (var value in list) writer.WriteStringValue(value.Span); writer.WriteEndArray();
            }
            Array("files", config.FileNames.Select(Relative));
            if (config.DisplayIncludes.Length != 1 || config.DisplayIncludes[0] != "**/*"u8) Array("include", config.DisplayIncludes);
            Array("exclude", config.DisplayExcludes);
            if (config.CompileOnSave) writer.WriteBoolean("compileOnSave", true);
            writer.WriteEndObject();
        }
        output.Write(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static JsonElement CanonicalOptionValue(OptionDefinition definition, JsonElement value, Func<Utf8String, Utf8String> relative)
    {
        if (definition.IsFilePath && value.ValueKind == JsonValueKind.String) return OptionValues.String(relative(JsonStrings.GetString(value)));
        if (definition.Kind == OptionKind.Enum)
        {
            var identity = value.ValueKind == JsonValueKind.Number ? Utf8String.FromString(value.GetRawText())
                : OptionDefinitions.EnumValueJson(definition.ValueIdentity(JsonStrings.GetString(value)) ?? default);
            int index = Array.FindIndex(definition.ValueIdentities, item => OptionDefinitions.EnumValueJson(item) == identity);
            if (index >= 0) return OptionValues.String(definition.Values[index]);
        }
        if (definition.ElementIsFilePath && value.ValueKind == JsonValueKind.Array)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer)) { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) writer.WriteStringValue(relative(JsonStrings.GetString(item)).Span); writer.WriteEndArray(); }
            using var document = JsonDocument.Parse(buffer.WrittenMemory); return document.RootElement.Clone();
        }
        return value.Clone();
    }
}

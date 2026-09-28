using TypeScript.Compiler.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;

namespace TypeScript.Compiler.Configuration;

public readonly record struct ProjectReference(string Path, bool Prepend = false, bool Circular = false);
public sealed record ParsedConfig(string FileName, CompilerOptions Options, string[] FileNames,
    ProjectReference[] References, Diagnostic[] Diagnostics, string[] ExtendedConfigFiles)
{
    public CompilerOptions WatchOptions { get; init; } = new();
    public CompilerOptions TypeAcquisition { get; init; } = new();
    public bool CompileOnSave { get; init; }
    public SourceFileNode? SourceFile { get; init; }
    public ContentMapper[] ContentMappers { get; init; } = [];
    public IReadOnlyDictionary<string, bool> WildcardDirectories { get; init; } = new Dictionary<string, bool>();
}

public sealed partial class ConfigParser(IFileSystem fileSystem, string currentDirectory)
{
    private sealed record ConfigLayer(ConfigSyntax Syntax, string[] Parents, string Identity);

    private sealed record ConfigValues(
        CompilerOptions Options,
        CompilerOptions Watch,
        CompilerOptions Acquisition,
        string[]? Files,
        string[]? Include,
        string[]? Exclude,
        bool? CompileOnSave,
        JsonElement? Mappers,
        ConfigSyntax? MapperSource);

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = int.MaxValue
    };

    public string? FindConfig(string startDirectory, string name = "tsconfig.json")
    {
        string directory = CompilerPath.Resolve(currentDirectory, startDirectory);
        while (true)
        {
            string candidate = CompilerPath.Combine(directory, name);
            if (fileSystem.FileExists(candidate))
                return candidate;
            string parent = CompilerPath.DirectoryName(directory);
            if (parent == directory)
                return null;
            directory = parent;
        }
    }

    public ParsedConfig Parse(string fileName, CompilerOptions? existing = null, CancellationToken cancellation = default)
    {
        fileName = CompilerPath.Resolve(currentDirectory, fileName);
        StringComparer comparer = fileSystem.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var layers = new Dictionary<string, ConfigLayer>(comparer);
        var values = new Dictionary<string, ConfigValues>(comparer);
        var active = new HashSet<string>(comparer);
        var errors = new List<Diagnostic>();
        var extended = new List<string>();
        var work = new Stack<(string Path, bool Finish)>();
        work.Push((fileName, false));
        while (work.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            if (values.ContainsKey(item.Path))
                continue;
            if (!item.Finish)
            {
                string identity = fileSystem.RealPath(item.Path);
                if (!active.Add(identity))
                {
                    errors.Add(
                        new(
                            Messages.Circularity_detected_while_resolving_configuration_Colon_0,
                            0,
                            0,
                            [TextSlice.Join(" -> ", active.Append(item.Path))]));
                    continue;
                }
                byte[]? bytes = fileSystem.ReadFile(item.Path);
                if (bytes is null)
                {
                    errors.Add(new(Messages.Cannot_read_file_0, 0, 0, [item.Path]));
                    active.Remove(identity);
                    continue;
                }
                var syntax = new ConfigSyntax(item.Path, bytes, errors, cancellation);
                JsonElement root = syntax.Root;
                var parents = new List<string>();
                if (root.TryGetProperty("extends", out var extends))
                {
                    IEnumerable<JsonElement> elements = extends.ValueKind == JsonValueKind.Array ? extends.EnumerateArray() : [extends];
                    foreach (var element in elements)
                    {
                        if (element.ValueKind != JsonValueKind.String)
                        {
                            errors.Add(
                                syntax.Diagnostic(
                                    Messages.Compiler_option_0_requires_a_value_of_type_1,
                                    syntax.Value("extends"),
                                    "extends",
                                    "string or Array"));
                            continue;
                        }
                        string specifier = JsonStrings.GetString(element);
                        if (specifier.Length == 0)
                        {
                            errors.Add(
                                syntax.Diagnostic(
                                    Messages.Compiler_option_0_cannot_be_given_an_empty_string,
                                    syntax.Value("extends"),
                                    "extends"));
                            continue;
                        }
                        string? parent = ResolveExtends(specifier, CompilerPath.DirectoryName(item.Path));
                        if (parent is null)
                            errors.Add(syntax.Diagnostic(Messages.File_0_not_found, syntax.Value("extends"), specifier));
                        else
                            parents.Add(parent);
                    }
                }
                layers[item.Path] = new(syntax, parents.ToArray(), identity);
                work.Push((item.Path, true));
                for (int i = parents.Count - 1; i >= 0; i--)
                    work.Push((parents[i], false));
                continue;
            }
            ConfigLayer current = layers[item.Path];
            ConfigSyntax source = current.Syntax;
            JsonElement currentRoot = source.Root;
            var options = new CompilerOptions();
            var watch = new CompilerOptions();
            var acquisition = new CompilerOptions();
            string[]? files = null, include = null, exclude = null;
            bool? compileOnSave = null;
            JsonElement? mappers = null;
            ConfigSyntax? mapperSource = null;
            foreach (string parent in current.Parents)
                if (values.TryGetValue(parent, out var inherited))
                {
                    options.Merge(inherited.Options);
                    watch.Merge(inherited.Watch);
                    files = inherited.Files ?? files;
                    include = inherited.Include ?? include;
                    exclude = inherited.Exclude ?? exclude;
                    compileOnSave = inherited.CompileOnSave ?? compileOnSave;
                    if (inherited.Mappers is { } value)
                    {
                        mappers = value;
                        mapperSource = inherited.MapperSource;
                    }
                }
            if (CompilerPath.BaseName(item.Path) == "jsconfig.json")
            {
                foreach (string name in new[] { "allowJs", "skipLibCheck", "noEmit" })
                    if (options.Get(name) is null)
                        options.SetRaw(name, "true");
                if (options.MaxNodeModuleJsDepth is null)
                    options.SetRaw("maxNodeModuleJsDepth", "2");
                acquisition.SetRaw("enable", "true");
            }
            string directory = CompilerPath.DirectoryName(item.Path);
            void Error(DiagnosticMessage message, SyntaxNode? node, params TextSlice[] args) =>
                errors.Add(source.Diagnostic(message, node, args));
            void ReadOptions(string section, OptionGroup group, CompilerOptions output)
            {
                if (!currentRoot.TryGetProperty(section, out var container) || container.ValueKind == JsonValueKind.Null)
                    return;
                if (container.ValueKind != JsonValueKind.Object)
                {
                    Error(Messages.Compiler_option_0_requires_a_value_of_type_1, source.Value(section), section, "object");
                    return;
                }
                foreach (var property in container.EnumerateObject())
                {
                    var definition = OptionDefinitions.Find(JsonStrings.GetName(property), group);
                    if (definition is null || definition.Name != JsonStrings.GetName(property))
                    {
                        if (OptionDefinitions.Suggest(JsonStrings.GetName(property), group) is { } suggestion)
                            Error(
                                group == OptionGroup.Watch
                                    ? Messages.Unknown_watch_option_0_Did_you_mean_1
                                    : group == OptionGroup.TypeAcquisition
                                        ? Messages.Unknown_type_acquisition_option_0_Did_you_mean_1
                                        : Messages.Unknown_compiler_option_0_Did_you_mean_1,
                                source.PropertyName(section, JsonStrings.GetName(property)),
                                JsonStrings.GetName(property),
                                suggestion.Name);
                        else
                            Error(
                                group == OptionGroup.Watch
                                    ? Messages.Unknown_watch_option_0
                                    : group == OptionGroup.TypeAcquisition
                                        ? Messages.Unknown_type_acquisition_option_0
                                        : Messages.Unknown_compiler_option_0,
                                source.PropertyName(section, JsonStrings.GetName(property)),
                                JsonStrings.GetName(property));
                        continue;
                    }
                    if (definition.IsCommandLineOnly)
                    {
                        Error(
                            Messages.Option_0_can_only_be_specified_on_command_line,
                            source.PropertyName(section, JsonStrings.GetName(property)),
                            JsonStrings.GetName(property));
                        continue;
                    }
                    if (OptionValues.Convert(
                        definition,
                        property.Value,
                        directory,
                        (message, args) => Error(message, source.Value(section, JsonStrings.GetName(property)), args)) is { } converted)
                        output.Set(definition.Name, converted);
                    else
                        output.SetRaw(definition.Name, "null");
                    if (JsonStrings.GetName(property) == "paths" && property.Value.ValueKind == JsonValueKind.Object)
                        output.SetString("pathsBasePath", directory);
                }
            }
            ReadOptions("compilerOptions", OptionGroup.Compiler, options);
            ReadOptions("watchOptions", OptionGroup.Watch, watch);
            ReadOptions("typeAcquisition", OptionGroup.TypeAcquisition, acquisition);
            if (!currentRoot.TryGetProperty("compilerOptions", out _))
                foreach (var property in currentRoot.EnumerateObject())
                    if (OptionDefinitions.Find(JsonStrings.GetName(property)) is { } definition
                        && definition.Name == JsonStrings.GetName(property))
                    {
                        Error(
                            Messages.X_0_should_be_set_inside_the_compilerOptions_object_of_the_config_json_file,
                            source.PropertyName("", JsonStrings.GetName(property)),
                            JsonStrings.GetName(property));
                        break;
                    }
            if (currentRoot.TryGetProperty("excludes", out _))
                Error(Messages.Unknown_option_excludes_Did_you_mean_exclude, source.PropertyName("", "excludes"));
            if (currentRoot.TryGetProperty("compileOnSave", out var compile))
            {
                if (compile.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                    Error(Messages.Compiler_option_0_requires_a_value_of_type_1, source.Value("compileOnSave"), "compileOnSave", "boolean");
                compileOnSave = compile.ValueKind == JsonValueKind.True;
            }
            string[]? Paths(string key, string[]? inherited)
            {
                if (!currentRoot.TryGetProperty(key, out var property))
                    return inherited;
                if (property.ValueKind == JsonValueKind.Null)
                    return null;
                if (property.ValueKind != JsonValueKind.Array)
                {
                    Error(Messages.Compiler_option_0_requires_a_value_of_type_1, source.Value(key), key, "Array");
                    return null;
                }
                var list = new List<string>();
                int index = 0;
                foreach (var element in property.EnumerateArray())
                {
                    SyntaxNode? node = source.Value(key) is ArrayLiteralExpressionNode array && index < (array.Elements?.Count ?? 0)
                        ? array.Elements![index]
                        : source.Value(key);
                    index++;
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        if (element.ValueKind != JsonValueKind.Null)
                            Error(Messages.Compiler_option_0_requires_a_value_of_type_1, node, key, "string");
                        continue;
                    }
                    string text = JsonStrings.GetString(element);
                    if (key != "files" && OptionValues.SpecError(text, key == "include") is { } error)
                    {
                        Error(error, node, text);
                        continue;
                    }
                    list.Add(OptionValues.PathValue(text, directory));
                }
                return list.ToArray();
            }
            files = Paths("files", files);
            include = Paths("include", include);
            exclude = Paths("exclude", exclude);
            if (currentRoot.TryGetProperty("contentMappers", out var ownMappers))
            {
                mappers = ownMappers;
                mapperSource = source;
            }
            values[item.Path] = new(options, watch, acquisition, files, include, exclude, compileOnSave, mappers, mapperSource);
            if (item.Path != fileName)
                extended.Add(item.Path);
            active.Remove(current.Identity);
        }
        if (!values.TryGetValue(fileName, out var result))
            return new(fileName, existing ?? new(), [], [], errors.ToArray(), extended.ToArray());
        string rootDirectory = CompilerPath.DirectoryName(fileName);
        string Substitute(string value) =>
            OptionValues.IsTemplate(value) ? OptionValues.PathValue("./" + value["${configDir}".Length..], rootDirectory) : value;
        void SubstituteOptions(CompilerOptions options, OptionGroup group)
        {
            foreach (var pair in options.Values.ToArray())
            {
                var definition = OptionDefinitions.Find(pair.Key, group);
                if (definition?.AllowConfigDir != true)
                    continue;
                if (pair.Value.ValueKind == JsonValueKind.String)
                    options.SetString(pair.Key, Substitute(JsonStrings.GetString(pair.Value)));
                else if (pair.Value.ValueKind == JsonValueKind.Array)
                    options.SetArray(
                        pair.Key,
                        pair.Value.EnumerateArray().Select(
                            v => v.ValueKind == JsonValueKind.String ? OptionValues.String(Substitute(JsonStrings.GetString(v))) : v));
                else if (pair.Key == "paths" && pair.Value.ValueKind == JsonValueKind.Object)
                {
                    using var buffer = new MemoryStream();
                    var namePatches = new List<(int Start, int End, string Name)>();
                    using (var writer = new Utf8JsonWriter(buffer))
                    {
                        writer.WriteStartObject();
                        foreach (var property in pair.Value.EnumerateObject())
                        {
                            JsonStrings.WriteName(writer, JsonStrings.GetName(property), namePatches);
                            var value = property.Value.ValueKind == JsonValueKind.Array
                                ? OptionValues.Array(
                                    property.Value.EnumerateArray().Select(
                                        v => v.ValueKind == JsonValueKind.String
                                            ? OptionValues.String(Substitute(JsonStrings.GetString(v)))
                                            : v))
                                : property.Value;
                            JsonStrings.WriteValue(writer, value);
                        }
                        writer.WriteEndObject();
                    }
                    options.Set(pair.Key, JsonStrings.Parse(buffer, namePatches));
                }
            }
        }
        SubstituteOptions(result.Options, OptionGroup.Compiler);
        SubstituteOptions(result.Watch, OptionGroup.Watch);
        if (existing is not null)
            result.Options.Merge(existing);
        string[] includes = (result.Include ?? (result.Files is null
            ? [CompilerPath.Combine(rootDirectory, "**/*")]
            : [])).Select(Substitute).ToArray();
        string[] excludes = (result.Exclude ?? new[]
        {
            result.Options.OutDir,
            result.Options.DeclarationDir
        }.OfType<string>().ToArray()).Select(Substitute).ToArray();
        ConfigSyntax main = layers[fileName].Syntax;
        ContentMapper[] contentMappers = ReadContentMappers(
            result.Mappers,
            result.MapperSource ?? main,
            result.Options,
            rootDirectory,
            errors);
        string[] filesSelected = SelectFiles(
            result.Files?.Select(Substitute) ?? [],
            includes,
            excludes,
            result.Options,
            contentMappers.SelectMany(m => m.Extensions).ToArray(),
            rootDirectory,
            cancellation);
        var references = new List<ProjectReference>();
        if (main.Root.TryGetProperty("references", out var refs) && refs.ValueKind != JsonValueKind.Null)
        {
            if (refs.ValueKind != JsonValueKind.Array)
                errors.Add(
                    main.Diagnostic(
                        Messages.Compiler_option_0_requires_a_value_of_type_1,
                        main.Value("references"),
                        "references",
                        "Array"));
            else
                foreach (var reference in refs.EnumerateArray())
                {
                    if (reference.ValueKind != JsonValueKind.Object)
                    {
                        if (reference.ValueKind != JsonValueKind.Null)
                            errors.Add(
                                main.Diagnostic(
                                    Messages.Compiler_option_0_requires_a_value_of_type_1,
                                    main.Value("references"),
                                    "references",
                                    "object"));
                        continue;
                    }
                    if (!reference.TryGetProperty("path", out var referencePath) || referencePath.ValueKind != JsonValueKind.String)
                    {
                        errors.Add(
                            main.Diagnostic(
                                Messages.Compiler_option_0_requires_a_value_of_type_1,
                                main.Value("references"),
                                "reference.path",
                                "string"));
                        continue;
                    }
                    if (JsonStrings.GetString(referencePath) == "")
                    {
                        errors.Add(
                            main.Diagnostic(
                                Messages.Compiler_option_0_cannot_be_given_an_empty_string,
                                main.Value("references"),
                                "reference.path"));
                        continue;
                    }
                    bool circular = false;
                    if (reference.TryGetProperty("circular", out var circularValue))
                    {
                        if (circularValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            errors.Add(
                                main.Diagnostic(
                                    Messages.Compiler_option_0_requires_a_value_of_type_1,
                                    main.Value("references"),
                                    "reference.circular",
                                    "boolean"));
                        circular = circularValue.ValueKind == JsonValueKind.True;
                    }
                    references.Add(new(CompilerPath.Resolve(rootDirectory, JsonStrings.GetString(referencePath)), Circular: circular));
                }
        }
        if (filesSelected.Length == 0 && result.Files is null && !main.Root.TryGetProperty("references", out _))
            errors.Add(
                new(
                    Messages.No_inputs_were_found_in_config_file_0_Specified_include_paths_were_1_and_exclude_paths_were_2,
                    0,
                    0,
                    [
                            fileName,
                            OptionValues.Array(includes.Select(OptionValues.String)).GetRawText(),
                            OptionValues.Array(excludes.Select(OptionValues.String)).GetRawText()
                        ]));
        if (main.Root.TryGetProperty("files", out var filesProperty)
            && filesProperty.ValueKind == JsonValueKind.Array
            && filesProperty.GetArrayLength() == 0
            && references.Count == 0
            && !main.Root.TryGetProperty("extends", out _))
            errors.Add(main.Diagnostic(Messages.The_files_list_in_config_file_0_is_empty, main.Value("files"), fileName));
        return new(
            fileName,
            result.Options,
            filesSelected,
            references.ToArray(),
            errors.ToArray(),
            extended.Distinct(comparer).ToArray())
        {
            WatchOptions = result.Watch,
            TypeAcquisition = result.Acquisition,
            CompileOnSave = result.CompileOnSave ?? false,
            SourceFile = main.Source,
            ContentMappers = contentMappers,
            WildcardDirectories = FileMatcher.WildcardDirectories(includes, excludes, rootDirectory, fileSystem.CaseSensitive)
        };
    }

    private string[] SelectFiles(
        IEnumerable<string> literalFiles,
        string[] includes,
        string[] excludes,
        CompilerOptions options,
        string[] extraExtensions,
        string root,
        CancellationToken cancellation)
    {
        StringComparer comparer = fileSystem.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var literals = new HashSet<string>(literalFiles, comparer);
        var wildcards = new List<string>();
        var jsonFiles = new List<string>();
        bool allowJs = options.AllowJs ?? options.CheckJs ?? false;
        bool resolveJson = options.ResolveJsonModule ?? options.ModuleResolution == ModuleResolutionKind.Bundler;
        string[] extensions = new[]
        {
            ".ts",
            ".tsx",
            ".mts",
            ".cts"
        }.Concat(allowJs ? [".js", ".jsx", ".mjs", ".cjs"] : []).Concat(resolveJson ? [".json"] : []).Concat(extraExtensions).ToArray();
        string[] candidates = includes.Length == 0
            ? []
            : FileMatcher.ReadDirectory(fileSystem, root, root, extensions, excludes, includes, cancellation: cancellation);
        FilePattern[] jsonPatterns = includes.Where(p => p.EndsWith(
            ".json",
            StringComparison.Ordinal)).Select(p => new FilePattern(p, fileSystem.CaseSensitive)).ToArray();
        string[][] groups = [[".ts", ".tsx", ".d.ts", ".js", ".jsx"], [".cts", ".d.cts", ".cjs"], [".mts", ".d.mts", ".mjs"]];
        foreach (string file in candidates)
        {
            if (literals.Contains(file) || wildcards.Contains(file, comparer))
                continue;
            if (file.EndsWith(".json", StringComparison.Ordinal))
            {
                if (jsonPatterns.Any(p => p.Matches(file)) && !jsonFiles.Contains(file, comparer))
                    jsonFiles.Add(file);
                continue;
            }
            string[]? group = groups.FirstOrDefault(g => g.Any(e => file.EndsWith(e, StringComparison.Ordinal)));
            if (group is not null)
            {
                string extension = group.OrderByDescending(e => e.Length).First(e => file.EndsWith(e, StringComparison.Ordinal));
                string stem = file[..^extension.Length];
                int rank = Array.IndexOf(group, extension);
                if (group.Take(rank).Any(
                    e => !(e == ".d.ts" && extension is ".js" or ".jsx")
                        && (literals.Contains(stem + e) || wildcards.Contains(stem + e, comparer))))
                    continue;
                wildcards.RemoveAll(p => group.Skip(rank + 1).Any(e => comparer.Equals(p, stem + e)));
            }
            wildcards.Add(file);
        }
        return literals.Concat(wildcards).Concat(jsonFiles).ToArray();
    }

    private string? ResolveExtends(string specifier, string directory)
    {
        string? File(string path) =>
            fileSystem.FileExists(path)
                ? path
                : !path.EndsWith(".json", StringComparison.Ordinal) && fileSystem.FileExists(path + ".json") ? path + ".json" : null;
        specifier = CompilerPath.NormalizeSlashes(specifier);
        if (CompilerPath.IsAbsolute(specifier)
            || specifier.StartsWith("./", StringComparison.Ordinal)
            || specifier.StartsWith("../", StringComparison.Ordinal))
            return File(CompilerPath.Resolve(directory, specifier));
        var resolved = ModuleResolver.ResolveConfig(
            fileSystem,
            currentDirectory,
            specifier,
            CompilerPath.Combine(directory, "tsconfig.json"));
        return resolved.IsResolved ? resolved.FileName : null;
    }

    public static bool GlobMatches(string pattern, string path, bool caseSensitive, bool exclude = false) =>
        new FilePattern(pattern, caseSensitive, exclude).Matches(path);
}

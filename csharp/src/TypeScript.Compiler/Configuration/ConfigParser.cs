using TypeScript.Compiler.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;

namespace TypeScript.Compiler.Configuration;

public readonly record struct ProjectReference(Utf8String Path, bool Prepend = false, bool Circular = false)
{
    internal int SourceStart { get; init; }
    internal int SourceLength { get; init; }
}
public sealed record ParsedConfig(Utf8String FileName, CompilerOptions Options, Utf8String[] FileNames,
    ProjectReference[] References, Diagnostic[] Diagnostics, Utf8String[] ExtendedConfigFiles)
{
    public CompilerOptions WatchOptions { get; init; } = new();
    public CompilerOptions TypeAcquisition { get; init; } = new();
    public bool CompileOnSave { get; init; }
    public SourceFileNode? SourceFile { get; init; }
    public ContentMapper[] ContentMappers { get; init; } = [];
    public IReadOnlyDictionary<Utf8String, bool> WildcardDirectories { get; init; } = new Dictionary<Utf8String, bool>();
    internal Utf8String[] LiteralFiles { get; init; } = [];
    internal Utf8String[] Includes { get; init; } = [];
    internal Utf8String[] Excludes { get; init; } = [];
    internal Utf8String[] DisplayIncludes { get; init; } = [];
    internal Utf8String[] DisplayExcludes { get; init; } = [];
    internal bool HasReferences { get; init; }
}

public sealed partial class ConfigParser(IFileSystem fileSystem, Utf8String currentDirectory)
{
    private sealed record ConfigLayer(ConfigSyntax Syntax, Utf8String[] Parents, Utf8String Identity);

    private sealed record ConfigValues(
        CompilerOptions Options,
        CompilerOptions Watch,
        CompilerOptions Acquisition,
        Utf8String[]? Files,
        Utf8String[]? Include,
        Utf8String[]? Exclude,
        bool? CompileOnSave,
        JsonElement? Mappers,
        ConfigSyntax? MapperSource,
        JsonElement? DisplayInclude,
        JsonElement? DisplayExclude);

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = int.MaxValue
    };

    public Utf8String? FindConfig(Utf8String startDirectory) => FindConfig(startDirectory, "tsconfig.json"u8);

    public Utf8String? FindConfig(Utf8String startDirectory, Utf8String name)
    {
        Utf8String directory = CompilerPath.Resolve(currentDirectory, startDirectory);
        while (true)
        {
            Utf8String candidate = CompilerPath.Combine(directory, name);
            if (fileSystem.FileExists(candidate))
                return candidate;
            Utf8String parent = CompilerPath.DirectoryName(directory);
            if (parent == directory)
                return null;
            directory = parent;
        }
    }

    public ParsedConfig Parse(Utf8String fileName, CompilerOptions? existing = null, CancellationToken cancellation = default)
    {
        fileName = CompilerPath.Resolve(currentDirectory, fileName);
        Utf8StringComparer comparer = fileSystem.CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
        var layers = new Dictionary<Utf8String, ConfigLayer>(comparer);
        var values = new Dictionary<Utf8String, ConfigValues>(comparer);
        var active = new HashSet<Utf8String>(comparer);
        var errors = new List<Diagnostic>();
        var extended = new List<Utf8String>();
        var work = new Stack<(Utf8String Path, bool Finish)>();
        work.Push((fileName, false));
        while (work.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            if (values.ContainsKey(item.Path))
                continue;
            if (!item.Finish)
            {
                Utf8String identity = fileSystem.RealPath(item.Path);
                if (!active.Add(identity))
                {
                    errors.Add(
                        new(
                            Messages.Circularity_detected_while_resolving_configuration_Colon_0,
                            0,
                            0,
                            [Utf8String.Join(" -> "u8, active.Append(item.Path))]));
                    continue;
                }
                byte[]? bytes = fileSystem.ReadFile(item.Path);
                if (bytes is null)
                {
                    errors.Add(new(Messages.Cannot_read_file_0, 0, 0, [item.Path]));
                    if (item.Path != fileName) extended.Add(item.Path);
                    active.Remove(identity);
                    continue;
                }
                var syntax = new ConfigSyntax(item.Path, bytes, errors, cancellation);
                JsonElement root = syntax.Root;
                var parents = new List<Utf8String>();
                if (root.TryGetProperty("extends"u8, out var extends))
                {
                    IEnumerable<JsonElement> elements = extends.ValueKind == JsonValueKind.Array ? extends.EnumerateArray() : [extends];
                    foreach (var element in elements)
                    {
                        if (element.ValueKind != JsonValueKind.String)
                        {
                            errors.Add(
                                syntax.Diagnostic(
                                    Messages.Compiler_option_0_requires_a_value_of_type_1,
                                    syntax.Value(Utf8Literals.ExtendsKeyword),
                                    Utf8Literals.ExtendsKeyword,
                                    Utf8Literals.StringOrArray));
                            continue;
                        }
                        Utf8String specifier = JsonStrings.GetString(element);
                        if (specifier.Length == 0)
                        {
                            errors.Add(
                                syntax.Diagnostic(
                                    Messages.Compiler_option_0_cannot_be_given_an_empty_string,
                                    syntax.Value(Utf8Literals.ExtendsKeyword),
                                    Utf8Literals.ExtendsKeyword));
                            continue;
                        }
                        Utf8String? parent = ResolveExtends(specifier, CompilerPath.DirectoryName(item.Path));
                        if (parent is null)
                            errors.Add(syntax.Diagnostic(Messages.File_0_not_found, syntax.Value(Utf8Literals.ExtendsKeyword), specifier));
                        else
                            parents.Add(parent.Value);
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
            Utf8String[]? files = null, include = null, exclude = null;
            bool? compileOnSave = null;
            JsonElement? mappers = null;
            JsonElement? displayInclude = null, displayExclude = null;
            ConfigSyntax? mapperSource = null;
            foreach (Utf8String parent in current.Parents)
                if (values.TryGetValue(parent, out var inherited))
                {
                    options.Merge(inherited.Options);
                    watch.Merge(inherited.Watch);
                    files = inherited.Files ?? files;
                    include = inherited.Include ?? include;
                    exclude = inherited.Exclude ?? exclude;
                    JsonElement? InheritSpecs(JsonElement? specs)
                    {
                        if (specs is null || specs.Value.ValueKind != JsonValueKind.Array) return specs;
                        var relative = CompilerPath.Relative(CompilerPath.DirectoryName(item.Path), CompilerPath.DirectoryName(parent), fileSystem.CaseSensitive);
                        if (relative.IsEmpty || relative == "."u8) return specs;
                        return OptionValues.Array(specs.Value.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                            && !OptionValues.IsTemplate(JsonStrings.GetString(value)) && !CompilerPath.IsAbsolute(JsonStrings.GetString(value))
                                ? OptionValues.String(CompilerPath.Combine(relative, JsonStrings.GetString(value))) : value));
                    }
                    displayInclude = InheritSpecs(inherited.DisplayInclude) ?? displayInclude;
                    displayExclude = InheritSpecs(inherited.DisplayExclude) ?? displayExclude;
                    compileOnSave = inherited.CompileOnSave ?? compileOnSave;
                    if (inherited.Mappers is { } value)
                    {
                        mappers = value;
                        mapperSource = inherited.MapperSource;
                    }
                }
            if (CompilerPath.BaseName(item.Path) == Utf8Literals.JsconfigJson)
            {
                foreach (Utf8String name in new Utf8String[] { Utf8Literals.AllowJs, Utf8Literals.SkipLibCheck, Utf8Literals.NoEmit })
                    if (options.Get(name) is null)
                        options.SetRaw(name, Utf8Literals.True);
                if (options.MaxNodeModuleJsDepth is null)
                    options.SetRaw(Utf8Literals.MaxNodeModuleJsDepth, Utf8Literals.Two);
                acquisition.SetRaw(Utf8Literals.Enable, Utf8Literals.True);
            }
            Utf8String directory = CompilerPath.DirectoryName(item.Path);
            void Error(DiagnosticMessage message, SyntaxNode? node, params Utf8String[] args) =>
                errors.Add(source.Diagnostic(message, node, args));
            void ReadOptions(Utf8String section, OptionGroup group, CompilerOptions output)
            {
                if (!currentRoot.TryGetProperty(section, out var container) || container.ValueKind == JsonValueKind.Null)
                    return;
                if (container.ValueKind != JsonValueKind.Object)
                {
                    Error(Messages.Compiler_option_0_requires_a_value_of_type_1, source.Value(section), section, Utf8Literals.Object);
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
                        output.SetRaw(definition.Name, Utf8Literals.Null);
                    if (JsonStrings.GetName(property) == Utf8Literals.Paths && property.Value.ValueKind == JsonValueKind.Object)
                        output.SetString(Utf8Literals.PathsBasePath, directory);
                }
            }
            ReadOptions(Utf8Literals.CompilerOptions, OptionGroup.Compiler, options);
            ReadOptions(Utf8Literals.WatchOptions, OptionGroup.Watch, watch);
            ReadOptions(Utf8Literals.TypeAcquisition, OptionGroup.TypeAcquisition, acquisition);
            if (!currentRoot.TryGetProperty("compilerOptions"u8, out _))
                foreach (var property in currentRoot.EnumerateObject())
                    if (OptionDefinitions.Find(JsonStrings.GetName(property)) is { } definition
                        && definition.Name == JsonStrings.GetName(property))
                    {
                        Error(
                            Messages.X_0_should_be_set_inside_the_compilerOptions_object_of_the_config_json_file,
                            source.PropertyName(Utf8String.Empty, JsonStrings.GetName(property)),
                            JsonStrings.GetName(property));
                        break;
                    }
            if (currentRoot.TryGetProperty("excludes"u8, out _))
                Error(Messages.Unknown_option_excludes_Did_you_mean_exclude, source.PropertyName(Utf8String.Empty, Utf8Literals.Excludes));
            if (currentRoot.TryGetProperty("compileOnSave"u8, out var compile))
            {
                if (compile.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                    Error(Messages.Compiler_option_0_requires_a_value_of_type_1, source.Value(Utf8Literals.CompileOnSave), Utf8Literals.CompileOnSave, Utf8Literals.BooleanKeyword);
                compileOnSave = compile.ValueKind == JsonValueKind.True;
            }
            Utf8String[]? Paths(Utf8String key, Utf8String[]? inherited)
            {
                if (!currentRoot.TryGetProperty(key, out var property))
                    return inherited;
                if (property.ValueKind == JsonValueKind.Null)
                    return null;
                if (property.ValueKind != JsonValueKind.Array)
                {
                    Error(Messages.Compiler_option_0_requires_a_value_of_type_1, source.Value(key), key, Utf8Literals.Array);
                    return null;
                }
                var list = new List<Utf8String>();
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
                            Error(Messages.Compiler_option_0_requires_a_value_of_type_1, node, key, Utf8Literals.StringKeyword);
                        continue;
                    }
                    Utf8String text = JsonStrings.GetString(element);
                    if (key != Utf8Literals.Files && OptionValues.SpecError(text, key == Utf8Literals.Include) is { } error)
                    {
                        Error(error, node, text);
                        continue;
                    }
                    list.Add(OptionValues.PathValue(text, directory));
                }
                return list.ToArray();
            }
            files = Paths(Utf8Literals.Files, files);
            include = Paths(Utf8Literals.Include, include);
            exclude = Paths(Utf8Literals.Exclude, exclude);
            if (currentRoot.TryGetProperty("include"u8, out var rawInclude) && rawInclude.ValueKind == JsonValueKind.Array) displayInclude = rawInclude;
            if (currentRoot.TryGetProperty("exclude"u8, out var rawExclude) && rawExclude.ValueKind == JsonValueKind.Array) displayExclude = rawExclude;
            if (currentRoot.TryGetProperty("contentMappers"u8, out var ownMappers))
            {
                mappers = ownMappers;
                mapperSource = source;
            }
            values[item.Path] = new(options, watch, acquisition, files, include, exclude, compileOnSave, mappers, mapperSource, displayInclude, displayExclude);
            if (item.Path != fileName)
                extended.Add(item.Path);
            active.Remove(current.Identity);
        }
        if (!values.TryGetValue(fileName, out var result))
            return new(fileName, existing ?? new(), [], [], errors.ToArray(), extended.ToArray());
        Utf8String rootDirectory = CompilerPath.DirectoryName(fileName);
        Utf8String Substitute(Utf8String value) =>
            OptionValues.IsTemplate(value) ? OptionValues.PathValue(Utf8Literals.CurrentDirectoryPrefix + value["${configDir}".Length..], rootDirectory) : value;
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
                else if (pair.Key == Utf8Literals.Paths && pair.Value.ValueKind == JsonValueKind.Object)
                {
                    using var buffer = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(buffer))
                    {
                        writer.WriteStartObject();
                        foreach (var property in pair.Value.EnumerateObject())
                        {
                            JsonStrings.WriteName(writer, JsonStrings.GetName(property));
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
                    options.Set(pair.Key, JsonStrings.Parse(buffer));
                }
            }
        }
        SubstituteOptions(result.Options, OptionGroup.Compiler);
        SubstituteOptions(result.Watch, OptionGroup.Watch);
        if (existing is not null)
            result.Options.Merge(existing);
        Utf8String[] includes = (result.Include ?? (result.Files is null
            ? [CompilerPath.Combine(rootDirectory, Utf8Literals.RecursiveGlob)]
            : [])).Select(Substitute).ToArray();
        Utf8String[] excludes = (result.Exclude ?? new[]
        {
            result.Options.OutDir,
            result.Options.DeclarationDir
        }.OfType<Utf8String>().ToArray()).Select(Substitute).ToArray();
        ConfigSyntax main = layers[fileName].Syntax;
        JsonElement DisplaySpecValues(Utf8String key, Utf8String[] paths)
        {
            var raw = key == Utf8Literals.Include ? result.DisplayInclude : result.DisplayExclude;
            if (raw is { ValueKind: JsonValueKind.Array } values)
                return OptionValues.Array(values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                    ? OptionValues.String(Substitute(JsonStrings.GetString(value))) : value));
            return OptionValues.Array((key == Utf8Literals.Include ? paths.Select(path => CompilerPath.Relative(rootDirectory, path, fileSystem.CaseSensitive))
                : paths).Select(OptionValues.String));
        }
        Utf8String[] DisplaySpecs(Utf8String key, Utf8String[] paths)
        {
            return DisplaySpecValues(key, paths).EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(JsonStrings.GetString)
                .Where(value => OptionValues.SpecError(value, key == Utf8Literals.Include) is null).ToArray();
        }
        ContentMapper[] contentMappers = ReadContentMappers(
            result.Mappers,
            result.MapperSource ?? main,
            result.Options,
            rootDirectory,
            errors);
        Utf8String[] filesSelected = SelectFiles(
            result.Files?.Select(Substitute) ?? [],
            includes,
            excludes,
            result.Options,
            contentMappers.SelectMany(m => m.Extensions).ToArray(),
            rootDirectory,
            cancellation);
        var references = new List<ProjectReference>();
        if (main.Root.TryGetProperty("references"u8, out var refs) && refs.ValueKind != JsonValueKind.Null)
        {
            if (refs.ValueKind != JsonValueKind.Array)
                errors.Add(
                    main.Diagnostic(
                        Messages.Compiler_option_0_requires_a_value_of_type_1,
                        main.Value(Utf8Literals.References),
                        Utf8Literals.References,
                        Utf8Literals.Array));
            else
            {
                int referenceIndex = 0;
                foreach (var reference in refs.EnumerateArray())
                {
                    var referenceNode = (main.Value(Utf8Literals.References) as ArrayLiteralExpressionNode)?.Elements?[referenceIndex++];
                    SyntaxNode? ReferenceValue(Utf8String name) => (referenceNode as ObjectLiteralExpressionNode)?.Properties?
                        .OfType<PropertyAssignmentNode>().FirstOrDefault(property => property.Name is StringLiteralNode text && text.Text == name
                            || property.Name is IdentifierNode id && id.Text == name)?.Initializer ?? referenceNode;
                    if (reference.ValueKind != JsonValueKind.Object)
                    {
                        if (reference.ValueKind != JsonValueKind.Null)
                            errors.Add(
                                main.Diagnostic(
                                    Messages.Compiler_option_0_requires_a_value_of_type_1,
                                    referenceNode,
                                    Utf8Literals.References,
                                    Utf8Literals.Object));
                        continue;
                    }
                    if (!reference.TryGetProperty("path"u8, out var referencePath) || referencePath.ValueKind != JsonValueKind.String)
                    {
                        errors.Add(
                            main.Diagnostic(
                                Messages.Compiler_option_0_requires_a_value_of_type_1,
                                ReferenceValue("path"u8),
                                Utf8Literals.ReferencePath,
                                Utf8Literals.StringKeyword));
                        continue;
                    }
                    if (JsonStrings.GetString(referencePath) == Utf8String.Empty)
                    {
                        errors.Add(
                            main.Diagnostic(
                                Messages.Compiler_option_0_cannot_be_given_an_empty_string,
                                ReferenceValue("path"u8),
                                Utf8Literals.ReferencePath));
                        continue;
                    }
                    bool circular = false;
                    if (reference.TryGetProperty("circular"u8, out var circularValue))
                    {
                        if (circularValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            errors.Add(
                                main.Diagnostic(
                                    Messages.Compiler_option_0_requires_a_value_of_type_1,
                                    ReferenceValue("circular"u8),
                                    Utf8Literals.ReferenceCircular,
                                    Utf8Literals.BooleanKeyword));
                        circular = circularValue.ValueKind == JsonValueKind.True;
                    }
                    var location = main.Span(referenceNode);
                    references.Add(new(CompilerPath.Resolve(rootDirectory, JsonStrings.GetString(referencePath)), Circular: circular)
                    { SourceStart = location.Start, SourceLength = location.Length });
                }
            }
        }
        if (filesSelected.Length == 0 && result.Files is null && !main.Root.TryGetProperty("references"u8, out _))
            errors.Add(
                new(
                    Messages.No_inputs_were_found_in_config_file_0_Specified_include_paths_were_1_and_exclude_paths_were_2,
                    0,
                    0,
                    [
                            fileName,
                            JsonStrings.Raw(DisplaySpecValues(Utf8Literals.Include, includes)),
                            JsonStrings.Raw(DisplaySpecValues(Utf8Literals.Exclude, excludes))
                        ]));
        if (main.Root.TryGetProperty("files"u8, out var filesProperty)
            && filesProperty.ValueKind == JsonValueKind.Array
            && filesProperty.GetArrayLength() == 0
            && references.Count == 0
            && !main.Root.TryGetProperty("extends"u8, out _))
            errors.Add(main.Diagnostic(Messages.The_files_list_in_config_file_0_is_empty, main.Value(Utf8Literals.Files), fileName));
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
            WildcardDirectories = FileMatcher.WildcardDirectories(includes, excludes, rootDirectory, fileSystem.CaseSensitive),
            LiteralFiles = result.Files?.Select(Substitute).ToArray() ?? [], Includes = includes, Excludes = excludes,
            DisplayIncludes = DisplaySpecs(Utf8Literals.Include, includes), DisplayExcludes = DisplaySpecs(Utf8Literals.Exclude, excludes),
            HasReferences = main.Root.TryGetProperty("references"u8, out var rawReferences) && rawReferences.ValueKind == JsonValueKind.Array
        };
    }

    internal ParsedConfig ReloadFiles(ParsedConfig config, CancellationToken cancellation) => config with
    {
        FileNames = SelectFiles(config.LiteralFiles, config.Includes, config.Excludes, config.Options,
            config.ContentMappers.SelectMany(mapper => mapper.Extensions).ToArray(), CompilerPath.DirectoryName(config.FileName), cancellation)
    };

    private Utf8String[] SelectFiles(
        IEnumerable<Utf8String> literalFiles,
        Utf8String[] includes,
        Utf8String[] excludes,
        CompilerOptions options,
        Utf8String[] extraExtensions,
        Utf8String root,
        CancellationToken cancellation)
    {
        Utf8StringComparer comparer = fileSystem.CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
        var literals = new HashSet<Utf8String>(literalFiles, comparer);
        var wildcards = new List<Utf8String>();
        var jsonFiles = new List<Utf8String>();
        bool allowJs = options.AllowJs ?? options.CheckJs ?? false;
        bool resolveJson = options.ResolveJsonModule ?? options.EmitModuleResolutionKind == ModuleResolutionKind.Bundler
            || options.EmitModule is ModuleKind.Node20 or ModuleKind.NodeNext;
        Utf8String[] extensions = new Utf8String[]
        {
            Utf8Literals.Ts,
            Utf8Literals.Tsx,
            Utf8Literals.Mts,
            Utf8Literals.Cts
        }.Concat(allowJs ? [Utf8Literals.Js, Utf8Literals.Jsx, Utf8Literals.Mjs, Utf8Literals.Cjs] : []).Concat(resolveJson ? [Utf8Literals.Json] : []).Concat(extraExtensions).ToArray();
        Utf8String[] candidates = includes.Length == 0
            ? []
            : FileMatcher.ReadDirectory(fileSystem, root, root, extensions, excludes, includes, cancellation: cancellation);
        FilePattern[] jsonPatterns = includes.Where(p => p.EndsWith(
            ".json"u8,
            StringComparison.Ordinal)).Select(p => new FilePattern(p, fileSystem.CaseSensitive)).ToArray();
        Utf8String[][] groups = [[Utf8Literals.Ts, Utf8Literals.Tsx, Utf8Literals.DTs, Utf8Literals.Js, Utf8Literals.Jsx], [Utf8Literals.Cts, Utf8Literals.DCts, Utf8Literals.Cjs], [Utf8Literals.Mts, Utf8Literals.DMts, Utf8Literals.Mjs]];
        foreach (Utf8String file in candidates)
        {
            if (literals.Contains(file) || wildcards.Contains(file, comparer))
                continue;
            if (file.EndsWith(".json"u8, StringComparison.Ordinal))
            {
                if (jsonPatterns.Any(p => p.Matches(file)) && !jsonFiles.Contains(file, comparer))
                    jsonFiles.Add(file);
                continue;
            }
            Utf8String[]? group = groups.FirstOrDefault(g => g.Any(e => file.EndsWith(e, StringComparison.Ordinal)));
            if (group is not null)
            {
                Utf8String extension = group.OrderByDescending(e => e.Length).First(e => file.EndsWith(e, StringComparison.Ordinal));
                Utf8String stem = file[..^extension.Length];
                int rank = Array.IndexOf(group, extension);
                if (group.Take(rank).Any(
                    e => !(e == Utf8Literals.DTs && (extension == ".js"u8 || extension == ".jsx"u8))
                        && (literals.Contains(stem + e) || wildcards.Contains(stem + e, comparer))))
                    continue;
                wildcards.RemoveAll(p => group.Skip(rank + 1).Any(e => comparer.Equals(p, stem + e)));
            }
            wildcards.Add(file);
        }
        return literals.Concat(wildcards).Concat(jsonFiles).ToArray();
    }

    private Utf8String? ResolveExtends(Utf8String specifier, Utf8String directory)
    {
        Utf8String? File(Utf8String path) =>
            fileSystem.FileExists(path)
                ? path
                : !path.EndsWith(".json"u8, StringComparison.Ordinal) && fileSystem.FileExists(path + Utf8Literals.Json) ? path + Utf8Literals.Json : (Utf8String?)null;
        specifier = CompilerPath.NormalizeSlashes(specifier);
        if (CompilerPath.IsAbsolute(specifier)
            || specifier.StartsWith("./"u8, StringComparison.Ordinal)
            || specifier.StartsWith("../"u8, StringComparison.Ordinal))
        {
            var path = CompilerPath.Resolve(directory, specifier);
            return path.EndsWith(".json"u8, StringComparison.Ordinal) ? path : File(path);
        }
        var resolved = ModuleResolver.ResolveConfig(
            fileSystem,
            currentDirectory,
            specifier,
            CompilerPath.Combine(directory, Utf8Literals.TsconfigJson));
        return resolved.IsResolved ? resolved.FileName : (Utf8String?)null;
    }

    public static bool GlobMatches(Utf8String pattern, Utf8String path, bool caseSensitive, bool exclude = false) =>
        new FilePattern(pattern, caseSensitive, exclude).Matches(path);
}

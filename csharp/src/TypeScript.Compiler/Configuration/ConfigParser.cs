using System.Text.Json;
using System.Text.RegularExpressions;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Configuration;

public sealed record ProjectReference(string Path, bool Prepend = false, bool Circular = false);
public sealed record ParsedConfig(string FileName, CompilerOptions Options, string[] FileNames,
    ProjectReference[] References, Diagnostic[] Diagnostics, string[] ExtendedConfigFiles);

public sealed class ConfigParser(IFileSystem fileSystem, string currentDirectory)
{
    private sealed record ConfigLayer(string Path, JsonElement Root, string[] Parents);
    private sealed record ConfigValues(CompilerOptions Options, string[]? Files, string[]? Include, string[]? Exclude);
    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = int.MaxValue };
    public string? FindConfig(string startDirectory, string name = "tsconfig.json")
    {
        string directory = CompilerPath.Resolve(currentDirectory, startDirectory);
        while (true)
        {
            string candidate = CompilerPath.Combine(directory, name);
            if (fileSystem.FileExists(candidate)) return candidate;
            string parent = CompilerPath.DirectoryName(directory);
            if (parent == directory) return null;
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
        var errors = new List<Diagnostic>(); var extended = new List<string>();
        void Error(DiagnosticMessage message, string file, params string[] args) => errors.Add(new(message, 0, 0, args) { FileName = file });
        var work = new Stack<(string Path, bool Finish)>(); work.Push((fileName, false));
        while (work.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            if (values.ContainsKey(item.Path)) continue;
            if (!item.Finish)
            {
                if (!active.Add(item.Path)) { Error(Messages.Circularity_detected_while_resolving_configuration_Colon_0, item.Path, item.Path); continue; }
                byte[]? bytes = fileSystem.ReadFile(item.Path);
                if (bytes is null) { Error(Messages.Cannot_read_file_0, item.Path, item.Path); active.Remove(item.Path); continue; }
                JsonElement root;
                try { using var document = JsonDocument.Parse(SourceEncoding.Decode(bytes), JsonOptions); root = document.RootElement.Clone(); }
                catch (JsonException e) { Error(Messages.X_0_expected, item.Path, "JSON value: " + e.Message); active.Remove(item.Path); continue; }
                if (root.ValueKind != JsonValueKind.Object) { Error(Messages.The_root_value_of_a_0_file_must_be_an_object, item.Path, "tsconfig.json"); active.Remove(item.Path); continue; }
                var parents = new List<string>();
                if (root.TryGetProperty("extends", out var extends))
                {
                    IEnumerable<JsonElement> elements = extends.ValueKind == JsonValueKind.Array ? extends.EnumerateArray() : [extends];
                    foreach (var element in elements)
                    {
                        if (element.ValueKind != JsonValueKind.String) { Error(Messages.Compiler_option_0_requires_a_value_of_type_1, item.Path, "extends", "string"); continue; }
                        string specifier = element.GetString()!;
                        string? parent = ResolveExtends(specifier, CompilerPath.DirectoryName(item.Path));
                        if (parent is null) Error(Messages.File_0_not_found, item.Path, specifier);
                        else parents.Add(parent);
                    }
                }
                var layer = new ConfigLayer(item.Path, root, parents.ToArray()); layers[item.Path] = layer;
                work.Push((item.Path, true));
                for (int i = parents.Count - 1; i >= 0; i--) work.Push((parents[i], false));
                continue;
            }
            ConfigLayer current = layers[item.Path];
            var options = new CompilerOptions(); string[]? files = null, include = null, exclude = null;
            foreach (string parent in current.Parents)
                if (values.TryGetValue(parent, out var inherited))
                { options.Merge(inherited.Options); files = inherited.Files ?? files; include = inherited.Include ?? include; exclude = inherited.Exclude ?? exclude; }
            string directory = CompilerPath.DirectoryName(current.Path);
            if (current.Root.TryGetProperty("compilerOptions", out var compiler))
            {
                if (compiler.ValueKind != JsonValueKind.Object && compiler.ValueKind != JsonValueKind.Null) Error(Messages.Compiler_option_0_requires_a_value_of_type_1, current.Path, "compilerOptions", "object");
                if (compiler.ValueKind == JsonValueKind.Object)
                    foreach (var property in compiler.EnumerateObject())
                    {
                        var definition = OptionDefinitions.Find(property.Name);
                        if (definition is null || definition.ShortName == property.Name) { Error(Messages.Unknown_compiler_option_0, current.Path, property.Name); continue; }
                        if (definition.IsCommandLineOnly) { Error(Messages.Option_0_can_only_be_specified_on_command_line, current.Path, property.Name); continue; }
                        if (!ValidateValue(definition, property.Value)) { Error(Messages.Compiler_option_0_requires_a_value_of_type_1, current.Path, property.Name, definition.Kind.ToString().ToLowerInvariant()); continue; }
                        if (property.Value.ValueKind == JsonValueKind.String && definition.IsFilePath)
                            options.SetString(definition.Name, CompilerPath.Resolve(directory, property.Value.GetString()!.Replace("${configDir}", directory, StringComparison.Ordinal)));
                        else options.Set(definition.Name, property.Value);
                    }
            }
            string[]? Paths(string key, string[]? inherited)
            {
                if (!current.Root.TryGetProperty(key, out var property)) return inherited;
                if (property.ValueKind != JsonValueKind.Array) { Error(Messages.Compiler_option_0_requires_a_value_of_type_1, current.Path, key, "Array"); return null; }
                var list = new List<string>();
                foreach (var element in property.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String) { Error(Messages.Compiler_option_0_requires_a_value_of_type_1, current.Path, key, "string"); continue; }
                    list.Add(CompilerPath.Resolve(directory, element.GetString()!.Replace("${configDir}", directory, StringComparison.Ordinal)));
                }
                return list.ToArray();
            }
            files = Paths("files", files); include = Paths("include", include); exclude = Paths("exclude", exclude);
            values[item.Path] = new(options, files, include, exclude);
            if (item.Path != fileName) extended.Add(item.Path);
            active.Remove(item.Path);
        }
        if (!values.TryGetValue(fileName, out var result)) return new(fileName, existing ?? new(), [], [], errors.ToArray(), extended.ToArray());
        if (existing is not null) result.Options.Merge(existing);
        string rootDirectory = CompilerPath.DirectoryName(fileName);
        string[] includes = result.Include ?? (result.Files is null ? [CompilerPath.Combine(rootDirectory, "**/*")] : []);
        string[] excludes = result.Exclude ?? new[] { "node_modules", "bower_components", "jspm_packages" }.Select(n => CompilerPath.Combine(rootDirectory, "**/" + n + "/**/*")).Concat(result.Options.String("outDir") is { } outDir ? [CompilerPath.Combine(outDir, "**/*")] : []).ToArray();
        var selected = new HashSet<string>(result.Files ?? [], comparer);
        if (includes.Length != 0)
        {
            Regex[] includePatterns = includes.Select(p => GlobRegex(p, fileSystem.CaseSensitive)).ToArray();
            Regex[] excludePatterns = excludes.Select(p => GlobRegex(p, fileSystem.CaseSensitive)).ToArray();
            var pending = new Stack<string>();
            foreach (string include in includes)
            {
                int wildcard = include.AsSpan().IndexOfAny('*', '?');
                string searchRoot = wildcard >= 0 ? CompilerPath.DirectoryName(include[..wildcard]) : fileSystem.DirectoryExists(include) ? include : CompilerPath.DirectoryName(include);
                pending.Push(searchRoot.Length == 0 ? rootDirectory : searchRoot);
            }
            var visited = new HashSet<string>(comparer);
            while (pending.TryPop(out string? directory))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!visited.Add(fileSystem.RealPath(directory))) continue;
                DirectoryEntries entries = fileSystem.GetAccessibleEntries(directory);
                foreach (string file in entries.Files)
                {
                    string path = CompilerPath.Combine(directory, file), extension = CompilerPath.Extension(file);
                    bool supported = extension is ".ts" or ".tsx" or ".mts" or ".cts" || result.Options.Boolean("allowJs") == true && extension is ".js" or ".jsx" or ".mjs" or ".cjs" || result.Options.Boolean("resolveJsonModule") == true && extension == ".json";
                    if (supported && includePatterns.Any(p => p.IsMatch(path)) && !excludePatterns.Any(p => p.IsMatch(path))) selected.Add(path);
                }
                foreach (string child in entries.Directories.Reverse())
                {
                    string path = CompilerPath.Combine(directory, child);
                    if (child is "node_modules" or "bower_components" or "jspm_packages" && !includes.Any(p => p.Contains("/" + child + "/", StringComparison.Ordinal))) continue;
                    pending.Push(path);
                }
            }
        }
        var references = new List<ProjectReference>();
        if (layers[fileName].Root.TryGetProperty("references", out var refs) && refs.ValueKind == JsonValueKind.Array)
            foreach (var reference in refs.EnumerateArray())
                if (reference.ValueKind == JsonValueKind.Object && reference.TryGetProperty("path", out var referencePath) && referencePath.ValueKind == JsonValueKind.String)
                    references.Add(new(CompilerPath.Resolve(rootDirectory, referencePath.GetString()!)));
        return new(fileName, result.Options, selected.Order(StringComparer.Ordinal).ToArray(), references.ToArray(), errors.ToArray(), extended.Distinct(comparer).ToArray());
    }
    private static bool ValidateValue(OptionDefinition definition, JsonElement value) => value.ValueKind == JsonValueKind.Null || definition.Kind switch
    {
        OptionKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        OptionKind.String => value.ValueKind == JsonValueKind.String,
        OptionKind.Enum => value.ValueKind == JsonValueKind.String && definition.Values.Contains(value.GetString()!, StringComparer.OrdinalIgnoreCase),
        OptionKind.Number => value.ValueKind == JsonValueKind.Number,
        OptionKind.Object => value.ValueKind == JsonValueKind.Object,
        OptionKind.List => value.ValueKind == JsonValueKind.Array,
        OptionKind.ListOrElement => value.ValueKind is JsonValueKind.Array or JsonValueKind.String,
        _ => false,
    };
    private string? ResolveExtends(string specifier, string directory)
    {
        string? File(string path) => fileSystem.FileExists(path) ? path : fileSystem.FileExists(path + ".json") ? path + ".json" : null;
        if (CompilerPath.IsAbsolute(specifier) || specifier.StartsWith('.')) return File(CompilerPath.Resolve(directory, specifier));
        while (true)
        {
            string candidate = CompilerPath.Combine(directory, "node_modules", specifier);
            string? exact = File(candidate); if (exact is not null) return exact;
            if (fileSystem.ReadFile(CompilerPath.Combine(candidate, "package.json")) is { } bytes)
            {
                try
                {
                    using var package = JsonDocument.Parse(bytes, JsonOptions);
                    if (package.RootElement.TryGetProperty("tsconfig", out var config) && config.ValueKind == JsonValueKind.String)
                    { string? target = File(CompilerPath.Resolve(candidate, config.GetString()!)); if (target is not null) return target; }
                }
                catch (JsonException) { }
            }
            exact = File(CompilerPath.Combine(candidate, "tsconfig.json")); if (exact is not null) return exact;
            string parent = CompilerPath.DirectoryName(directory); if (parent == directory) return null; directory = parent;
        }
    }
    public static bool GlobMatches(string pattern, string path, bool caseSensitive)
        => GlobRegex(pattern, caseSensitive).IsMatch(CompilerPath.NormalizeSlashes(path));
    private static Regex GlobRegex(string pattern, bool caseSensitive)
    {
        pattern = CompilerPath.NormalizeSlashes(pattern);
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return new("^" + Regex.Escape(CompilerPath.RemoveTrailingSeparator(pattern)) + "(?:/.*)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase));
        string expression = Regex.Escape(pattern).Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal).Replace(@"\*\*", ".*", StringComparison.Ordinal).Replace(@"\*", "[^/]*", StringComparison.Ordinal).Replace(@"\?", "[^/]", StringComparison.Ordinal);
        return new("^" + expression + "$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase));
    }
}

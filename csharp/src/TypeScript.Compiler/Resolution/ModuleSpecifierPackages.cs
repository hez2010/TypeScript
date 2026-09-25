using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Resolution;

internal enum PackageSpecifierMatch
{
    Exact,
    Directory,
    Pattern
}

internal sealed partial class ModuleSpecifierPackages(IFileSystem fileSystem, CompilerOptions options, string currentDirectory,
    string commonSourceDirectory, IReadOnlyList<string>? contentMapperExtensions = null, SemanticVersion? compilerVersion = null)
{
    private readonly PackageJsonCache packages = new(fileSystem, currentDirectory);
    private readonly SemanticVersion version = compilerVersion ?? new(7, 1, 0, "dev");
    private StringComparison Comparison => fileSystem.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    internal string FromExports(string target, string directory, string name, JsonElement exports, IReadOnlyList<string> conditions,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (PackageJson.MapKind(exports) == PackageMapKind.Subpaths)
            foreach (var mapping in PackageJson.Properties(exports))
            {
                string subName = CompilerPath.Normalize(CompilerPath.Combine(name, mapping.Name));
                var result = FromMap(
                    target,
                    directory,
                    subName,
                    mapping.Value,
                    conditions,
                    MatchMode(mapping.Name),
                    false,
                    false,
                    cancellation);
                if (result.Length != 0)
                    return result;
            }
        return FromMap(target, directory, name, exports, conditions, PackageSpecifierMatch.Exact, false, false, cancellation);
    }

    internal string FromImports(string target, string sourceDirectory, ReferenceResolutionMode importMode, bool preferTypeScript,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (options.Boolean("resolvePackageJsonImports") == false)
            return "";
        PackageJson? package = null;
        foreach (string directory in PackageJsonCache.Ancestors(CompilerPath.Resolve(currentDirectory, sourceDirectory)))
        {
            cancellation.ThrowIfCancellationRequested();
            if (packages.Get(directory).Contents is { } found)
            {
                package = found;
                break;
            }
        }
        if (package?.Get("imports") is not { ValueKind: JsonValueKind.Object } imports)
            return "";
        var conditions = Conditions(importMode);
        foreach (var entry in PackageJson.Properties(imports))
        {
            string key = entry.Name;
            if (key is "#" or "#/" || !key.StartsWith('#'))
                continue;
            if (key.StartsWith("#/", StringComparison.Ordinal) && ResolutionKind() == "node16")
                continue;
            string result = FromMap(
                target,
                package.Directory,
                key,
                entry.Value,
                conditions,
                MatchMode(key),
                true,
                preferTypeScript,
                cancellation);
            if (result.Length != 0)
                return result;
        }
        return "";
    }

    internal string[] Conditions(ReferenceResolutionMode mode)
    {
        string resolution = ResolutionKind();
        return [mode == ReferenceResolutionMode.Import || mode == 0 && resolution == "bundler" ? "import" : "require",
            .. options.Boolean("noDtsResolution") != true ? new[] { "types" } : [],
            .. resolution != "bundler" ? new[] { "node" } : [], .. options.Strings("customConditions") ?? []];
    }

    private string ResolutionKind() => options.String("moduleResolution") switch
    {
        "bundler" => "bundler",
        "node16" => "node16",
        "nodenext" => "nodenext",
        _ => options.Number("moduleResolution") switch
        {
            100 => "bundler",
            3 => "node16",
            99 => "nodenext",
            _ => options.String("module") switch
            {
                "node16" or "node18" or "node20" => "node16",
                "nodenext" => "nodenext",
                _ => options.Number("module") switch { 100 or 101 or 102 => "node16", 199 => "nodenext", _ => "bundler" }
            }
        }
    };

    private static PackageSpecifierMatch MatchMode(string key) => key.EndsWith('/') ? PackageSpecifierMatch.Directory
        : key.Contains('*') ? PackageSpecifierMatch.Pattern : PackageSpecifierMatch.Exact;

    internal string FromMap(string target, string directory, string name, JsonElement map, IReadOnlyList<string> conditions,
        PackageSpecifierMatch mode, bool imports, bool preferTypeScript, CancellationToken cancellation = default)
    {
        var pending = new Stack<JsonElement>();
        pending.Push(map);
        while (pending.TryPop(out var value))
        {
            cancellation.ThrowIfCancellationRequested();
            switch (value.ValueKind)
            {
                case JsonValueKind.Array:
                    for (int i = value.GetArrayLength() - 1; i >= 0; i--)
                        pending.Push(value[i]);
                    break;
                case JsonValueKind.Object:
                    var entries = PackageJson.Properties(value).ToArray();
                    for (int i = entries.Length - 1; i >= 0; i--)
                    {
                        string key = entries[i].Name;
                        if (key == "default" || conditions.Contains(key)
                            || conditions.Contains("types") && key.StartsWith("types@", StringComparison.Ordinal)
                                && VersionRange.Parse(key[6..])?.Test(version) == true)
                            pending.Push(entries[i].Value);
                    }
                    break;
                case JsonValueKind.String:
                    string result = MatchTarget(target, directory, name, JsonStrings.GetString(value), mode, imports, preferTypeScript);
                    if (result.Length != 0)
                        return result;
                    break;
            }
        }
        return "";
    }

    private string MatchTarget(string target, string directory, string name, string value, PackageSpecifierMatch mode,
        bool imports, bool preferTypeScript)
    {
        string output = imports ? OutputFile(target, false) : "";
        string declaration = imports ? OutputFile(target, true) : "";
        string pattern = CompilerPath.Normalize(CompilerPath.Combine(directory, value));
        string extension = ModuleSpecifierPaths.Extension(target);
        string swapped = extension is ".ts" or ".tsx" or ".d.ts" or ".mts" or ".d.mts" or ".cts" or ".d.cts"
            ? ModuleSpecifierPaths.WithoutExtension(target) + ModuleSpecifierPaths.JavaScriptFileExtension(target, options) : "";
        bool typed = preferTypeScript && !CompilerPath.IsDeclarationFile(target)
            && (target.EndsWith(".ts", StringComparison.Ordinal) || target.EndsWith(".tsx", StringComparison.Ordinal)
            || target.EndsWith(".mts", StringComparison.Ordinal) || target.EndsWith(".cts", StringComparison.Ordinal));
        switch (mode)
        {
            case PackageSpecifierMatch.Exact:
                return swapped.Length != 0 && Equal(swapped, pattern) || Equal(target, pattern)
                    || output.Length != 0 && Equal(output, pattern) || declaration.Length != 0 && Equal(declaration, pattern) ? name : "";
            case PackageSpecifierMatch.Directory:
                if (typed && Contains(target, pattern))
                    return DirectoryName(target, true);
                if (swapped.Length != 0 && Contains(pattern, swapped))
                    return DirectoryName(swapped, true);
                if (!typed && Contains(pattern, target))
                    return DirectoryName(target, true);
                if (output.Length != 0 && Contains(pattern, output))
                    return DirectoryName(output, false);
                if (declaration.Length != 0 && Contains(pattern, declaration))
                    return CompilerPath.Combine(
                        name,
                        ChangeExtension(
                            Relative(pattern, declaration),
                            ModuleSpecifierPaths.JavaScriptFileExtension(declaration, options)));
                return "";
            case PackageSpecifierMatch.Pattern:
                int star = pattern.IndexOf('*');
                string prefix = star < 0 ? pattern : pattern[..star], suffix = star < 0 ? "" : pattern[(star + 1)..];
                if (typed && Matches(target))
                    return Substitute(target);
                if (swapped.Length != 0 && Matches(swapped))
                    return Substitute(swapped);
                if (!typed && Matches(target))
                    return Substitute(target);
                if (output.Length != 0 && Matches(output))
                    return Substitute(output);
                if (declaration.Length != 0 && Matches(declaration))
                    return ChangeFullExtension(Substitute(declaration), ModuleSpecifierPaths.JavaScriptFileExtension(declaration, options));
                return "";
                bool Matches(string path) => path.Length >= prefix.Length + suffix.Length
                    && path.StartsWith(prefix, Comparison) && path.EndsWith(suffix, Comparison);
                string Substitute(string path)
                {
                    string replacement = path[prefix.Length..(path.Length - suffix.Length)];
                    int index = name.IndexOf('*');
                    return index < 0 ? name : name[..index] + replacement + name[(index + 1)..];
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        string DirectoryName(string path, bool includeValue) => includeValue
            ? CompilerPath.Normalize(CompilerPath.Combine(name, value, Relative(pattern, path)))
            : CompilerPath.Combine(name, Relative(pattern, path));
    }

    private bool Equal(string first, string second) => CompilerPath.Resolve(currentDirectory, first)
        .Equals(CompilerPath.Resolve(currentDirectory, second), Comparison);

    private bool Contains(string directory, string path) => CompilerPath.Contains(CompilerPath.Resolve(currentDirectory, directory),
            CompilerPath.Resolve(currentDirectory, path), fileSystem.CaseSensitive);

    private string Relative(string directory, string path) => CompilerPath.Relative(CompilerPath.Resolve(currentDirectory, directory),
            CompilerPath.Resolve(currentDirectory, path), fileSystem.CaseSensitive);

    internal string OutputFile(string source, bool declaration)
    {
        string directory = declaration
            ? options.String("declarationDir") ?? options.String("outDir") ?? ""
            : options.String("outDir") ?? "";
        string path = directory.Length == 0 ? source : CompilerPath.Resolve(CompilerPath.Resolve(currentDirectory, directory),
            Relative(commonSourceDirectory, source));
        if (!declaration)
        {
            string outputExtension = source.EndsWith(".json", StringComparison.Ordinal) ? ".json"
                : (options.String("jsx") == "preserve" || options.Number("jsx") == 1)
                    && (source.EndsWith(".jsx", StringComparison.Ordinal) || source.EndsWith(".tsx", StringComparison.Ordinal)) ? ".jsx"
                : source.EndsWith(".mts", StringComparison.Ordinal) || source.EndsWith(".mjs", StringComparison.Ordinal) ? ".mjs"
                : source.EndsWith(".cts", StringComparison.Ordinal) || source.EndsWith(".cjs", StringComparison.Ordinal) ? ".cjs" : ".js";
            return ChangeExtension(path, outputExtension);
        }
        var mapped = contentMapperExtensions?.Where(e => path.EndsWith(
            e,
            StringComparison.Ordinal)).OrderByDescending(e => e.Length).FirstOrDefault();
        if (mapped is not null)
            return path[..^mapped.Length] + ".d" + mapped + ".ts";
        string bare = ModuleSpecifierPaths.WithoutExtension(path);
        if (bare == path)
            bare = path[..(path.Length - CompilerPath.Extension(path).Length)];
        string extension = path.EndsWith(".mts", StringComparison.Ordinal) || path.EndsWith(".mjs", StringComparison.Ordinal) ? ".d.mts"
            : path.EndsWith(".cts", StringComparison.Ordinal) || path.EndsWith(".cjs", StringComparison.Ordinal) ? ".d.cts"
            : path.EndsWith(".ts", StringComparison.Ordinal) || path.EndsWith(".tsx", StringComparison.Ordinal)
                || path.EndsWith(".js", StringComparison.Ordinal) || path.EndsWith(".jsx", StringComparison.Ordinal) ? ".d.ts"
            : CompilerPath.Extension(path) is { Length: > 0 } other ? ".d" + other + ".ts" : ".d.ts";
        return bare + extension;
    }

    private static string ChangeExtension(string path, string extension)
    {
        string current = ModuleSpecifierPaths.Extension(path);
        return current.Length == 0 ? path : path[..^current.Length] + extension;
    }

    private static string ChangeFullExtension(string path, string extension)
    {
        if (CompilerPath.IsDeclarationFile(path))
        {
            int marker = path.LastIndexOf(".d.", StringComparison.Ordinal);
            if (marker >= 0)
                return path[..marker] + extension;
        }
        return ChangeExtension(path, extension);
    }
}

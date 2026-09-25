using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Resolution;

internal enum ModuleSpecifierEnding
{
    Minimal,
    Index,
    JavaScript,
    TypeScript
}

// Shared naming rules for declaration output and module-specifier generation.
// Package discovery and package exports/imports choose candidates separately.
internal static class ModuleSpecifierPaths
{
    private static readonly string[] extensions =
        [
            ".d.ts",
            ".d.mts",
            ".d.cts",
            ".mjs",
            ".mts",
            ".cjs",
            ".cts",
            ".ts",
            ".js",
            ".tsx",
            ".jsx",
            ".json"
        ];
    private static readonly string[] shadowExtensions =
        [
            ".ts",
            ".tsx",
            ".d.ts",
            ".js",
            ".jsx",
            ".cts",
            ".d.cts",
            ".cjs",
            ".mts",
            ".d.mts",
            ".mjs",
            ".node",
            ".json"
        ];

    internal static ModuleSpecifierEnding[] AllowedEndings(CompilerOptions options, SourceFileNode source,
        ReferenceResolutionMode defaultMode, ReferenceResolutionMode syntaxMode = 0, string preference = "", string oldSpecifier = "",
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var preferred = PreferredEnding(options, source, defaultMode, preference, oldSpecifier, cancellation);
        if (syntaxMode != defaultMode)
            preferred = PreferredEnding(
                options,
                source,
                syntaxMode == 0 ? defaultMode : syntaxMode,
                preference,
                oldSpecifier,
                cancellation);
        bool allowTypeScript = AllowsTypeScript(options) || CompilerPath.IsDeclarationFile(source.FileName);
        var effective = syntaxMode == 0 ? defaultMode : syntaxMode;
        if (effective == ReferenceResolutionMode.Import && NodeResolution(options))
            return allowTypeScript
                ? [ModuleSpecifierEnding.TypeScript, ModuleSpecifierEnding.JavaScript]
                : [ModuleSpecifierEnding.JavaScript];
        return preferred switch
        {
            ModuleSpecifierEnding.JavaScript => allowTypeScript
                ?
                    [
                        ModuleSpecifierEnding.JavaScript,
                        ModuleSpecifierEnding.TypeScript,
                        ModuleSpecifierEnding.Minimal,
                        ModuleSpecifierEnding.Index
                    ]
                : [ModuleSpecifierEnding.JavaScript, ModuleSpecifierEnding.Minimal, ModuleSpecifierEnding.Index],
            ModuleSpecifierEnding.TypeScript =>
                [
                    ModuleSpecifierEnding.TypeScript,
                    ModuleSpecifierEnding.Minimal,
                    ModuleSpecifierEnding.JavaScript,
                    ModuleSpecifierEnding.Index
                ],
            ModuleSpecifierEnding.Index => allowTypeScript
                ?
                    [
                        ModuleSpecifierEnding.Index,
                        ModuleSpecifierEnding.Minimal,
                        ModuleSpecifierEnding.TypeScript,
                        ModuleSpecifierEnding.JavaScript
                    ]
                : [ModuleSpecifierEnding.Index, ModuleSpecifierEnding.Minimal, ModuleSpecifierEnding.JavaScript],
            _ => allowTypeScript
                ?
                    [
                        ModuleSpecifierEnding.Minimal,
                        ModuleSpecifierEnding.Index,
                        ModuleSpecifierEnding.TypeScript,
                        ModuleSpecifierEnding.JavaScript
                    ]
                : [ModuleSpecifierEnding.Minimal, ModuleSpecifierEnding.Index, ModuleSpecifierEnding.JavaScript]
        };
    }

    private static ModuleSpecifierEnding PreferredEnding(CompilerOptions options, SourceFileNode source,
        ReferenceResolutionMode mode, string preference, string oldSpecifier, CancellationToken cancellation)
    {
        if (JavaScriptExtension(oldSpecifier))
            return ModuleSpecifierEnding.JavaScript;
        if (oldSpecifier.EndsWith("/index", StringComparison.Ordinal))
            return ModuleSpecifierEnding.Index;
        bool node = NodeResolution(options);
        if (preference == "js" || mode == ReferenceResolutionMode.Import && node)
            return AllowsTypeScript(options) && InferEnding(source, mode, node, cancellation) != ModuleSpecifierEnding.JavaScript
                ? ModuleSpecifierEnding.TypeScript : ModuleSpecifierEnding.JavaScript;
        if (preference == "minimal")
            return ModuleSpecifierEnding.Minimal;
        if (preference == "index")
            return ModuleSpecifierEnding.Index;
        if (AllowsTypeScript(options))
            return InferEnding(source, mode, node, cancellation);
        foreach (var import in source.Imports)
        {
            cancellation.ThrowIfCancellationRequested();
            string text = ImportText(import);
            if (Relative(text) && !MandatoryExtension(text))
                return TypeScriptExtension(text) || JavaScriptExtension(text)
                    ? ModuleSpecifierEnding.JavaScript
                    : ModuleSpecifierEnding.Minimal;
        }
        return ModuleSpecifierEnding.Minimal;
    }

    private static ModuleSpecifierEnding InferEnding(
        SourceFileNode source,
        ReferenceResolutionMode mode,
        bool node,
        CancellationToken cancellation)
    {
        bool js = false;
        foreach (var import in source.Imports)
        {
            cancellation.ThrowIfCancellationRequested();
            string text = ImportText(import);
            if (!Relative(text) || node && mode == ReferenceResolutionMode.Require || MandatoryExtension(text))
                continue;
            if (TypeScriptExtension(text))
                return ModuleSpecifierEnding.TypeScript;
            js |= JavaScriptExtension(text);
        }
        return js ? ModuleSpecifierEnding.JavaScript : ModuleSpecifierEnding.Minimal;
    }

    private static bool NodeResolution(CompilerOptions options)
    {
        string? resolution = options.String("moduleResolution");
        if (resolution is "node16" or "nodenext" || options.Number("moduleResolution") is 3 or 99)
            return true;
        if (resolution == "bundler" || options.Number("moduleResolution") == 100)
            return false;
        return options.String("module") is "node16" or "node18" or "node20" or "nodenext"
            || options.Number("module") is >= 100 and <= 199;
    }

    private static bool AllowsTypeScript(CompilerOptions options) => options.Boolean("allowImportingTsExtensions") == true
        || options.Boolean("rewriteRelativeImportExtensions") == true;

    private static string ImportText(SyntaxNode node) => node switch
    { StringLiteralNode text => text.Text, NoSubstitutionTemplateLiteralNode text => text.Text, _ => "" };

    private static bool Relative(string path) =>
        path is "." or ".." || path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith("../", StringComparison.Ordinal);

    private static bool MandatoryExtension(string path) => Extension(path) is ".mts" or ".d.mts" or ".mjs" or ".cts" or ".d.cts" or ".cjs";

    private static bool TypeScriptExtension(string path) =>
        Extension(path) is ".ts" or ".tsx" or ".d.ts" or ".mts" or ".d.mts" or ".cts" or ".d.cts";

    private static bool JavaScriptExtension(string path) => Extension(path) is ".js" or ".jsx" or ".mjs" or ".cjs";

    private static string Extension(string path) => extensions.FirstOrDefault(e => path.EndsWith(e, StringComparison.Ordinal)) ?? "";

    private static string WithoutExtension(string path) => path[..(path.Length - Extension(path).Length)];

    internal static string ProcessEnding(string fileName, IReadOnlyList<ModuleSpecifierEnding> endings, CompilerOptions options,
        IFileSystem? fileSystem = null, string currentDirectory = "", CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfZero(endings.Count);
        string extension = Extension(fileName);
        if (extension is ".json" or ".mjs" or ".cjs" or "")
            return fileName;
        string bare = WithoutExtension(fileName);
        int js = Index(endings, ModuleSpecifierEnding.JavaScript), ts = Index(endings, ModuleSpecifierEnding.TypeScript);
        if (extension is ".mts" or ".cts" or ".d.mts" or ".d.cts" && ts >= 0 && ts < js)
            return fileName;
        if (extension is ".d.mts" or ".d.cts")
            return bare + DeclarationJavaScriptExtension(extension);
        if (extension is ".mts" or ".cts")
            return bare + JavaScriptFileExtension(fileName, options);
        if (extension == ".ts" && RealNonJavaScriptFileName(fileName) is { Length: > 0 } real)
            return real;
        switch (endings[0])
        {
            case ModuleSpecifierEnding.Minimal:
                string withoutIndex = bare.EndsWith("/index", StringComparison.Ordinal) ? bare[..^6] : bare;
                return withoutIndex != bare && fileSystem is not null && shadowExtensions.Any(e =>
                    fileSystem.FileExists(CompilerPath.Resolve(currentDirectory, withoutIndex + e))) ? bare : withoutIndex;
            case ModuleSpecifierEnding.Index:
                return bare;
            case ModuleSpecifierEnding.JavaScript:
                return bare + JavaScriptFileExtension(fileName, options);
            case ModuleSpecifierEnding.TypeScript:
                if (!CompilerPath.IsDeclarationFile(fileName))
                    return fileName;
                int extensionless = -1;
                for (int i = 0; i < endings.Count; i++)
                    if (endings[i] is ModuleSpecifierEnding.Minimal or ModuleSpecifierEnding.Index)
                    {
                        extensionless = i;
                        break;
                    }
                return extensionless >= 0 && extensionless < js ? bare : bare + JavaScriptFileExtension(fileName, options);
            default:
                throw new ArgumentOutOfRangeException(nameof(endings));
        }
    }

    private static int Index(IReadOnlyList<ModuleSpecifierEnding> endings, ModuleSpecifierEnding value)
    {
        for (int i = 0; i < endings.Count; i++)
            if (endings[i] == value)
                return i;
        return -1;
    }

    internal static string DeclarationJavaScriptExtension(string extension) => extension switch
    { ".d.ts" => ".js", ".d.mts" => ".mjs", ".d.cts" => ".cjs", _ => extension[2..^3] };

    internal static string RealNonJavaScriptFileName(string fileName)
    {
        string name = CompilerPath.BaseName(fileName);
        if (!fileName.EndsWith(".ts", StringComparison.Ordinal) || !name.Contains(".d.", StringComparison.Ordinal)
            || name.EndsWith(".d.ts", StringComparison.Ordinal))
            return "";
        string bare = fileName[..^3];
        return bare[..bare.IndexOf(".d.", StringComparison.Ordinal)] + bare[bare.LastIndexOf('.')..];
    }

    private static string JavaScriptFileExtension(string path, CompilerOptions options) => Extension(path) switch
    {
        ".ts" or ".d.ts" => ".js",
        ".tsx" => options.String("jsx") == "preserve" || options.Number("jsx") == 1 ? ".jsx" : ".js",
        ".js" => ".js",
        ".jsx" => ".jsx",
        ".json" => ".json",
        ".d.mts" or ".mts" or ".mjs" => ".mjs",
        ".d.cts" or ".cts" or ".cjs" => ".cjs",
        _ => throw new ArgumentException("Unsupported module filename extension", nameof(path))
    };

    internal static string FromRootDirectories(IReadOnlyList<string> roots, string moduleFile, string sourceDirectory,
        IReadOnlyList<ModuleSpecifierEnding> endings, CompilerOptions options, IFileSystem fileSystem, string currentDirectory,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var targets = RelativeToRoots(moduleFile, roots, fileSystem.CaseSensitive, cancellation);
        string shortest = "";
        int separators = 0;
        foreach (string source in RelativeToRoots(sourceDirectory, roots, fileSystem.CaseSensitive, cancellation))
            foreach (string target in targets)
            {
                cancellation.ThrowIfCancellationRequested();
                string candidate = NonModulePath(CompilerPath.Relative(source, target, fileSystem.CaseSensitive));
                int count = candidate.Count(c => c == '/');
                if (shortest.Length == 0 || count < separators)
                {
                    shortest = candidate;
                    separators = count;
                }
            }
        return shortest.Length == 0 ? "" : ProcessEnding(shortest, endings, options, fileSystem, currentDirectory, cancellation);
    }

    private static IReadOnlyList<string> RelativeToRoots(
        string path,
        IReadOnlyList<string> roots,
        bool caseSensitive,
        CancellationToken cancellation)
    {
        var result = new List<string>();
        foreach (string root in roots)
        {
            cancellation.ThrowIfCancellationRequested();
            string relative = RelativeIfSameVolume(path, root, caseSensitive);
            if (!relative.StartsWith("..", StringComparison.Ordinal))
                result.Add(relative);
        }
        return result;
    }

    internal static string RelativeIfSameVolume(string path, string directory, bool caseSensitive)
    {
        string relative = CompilerPath.Relative(directory, CompilerPath.Resolve(directory, path), caseSensitive);
        return CompilerPath.IsAbsolute(relative) && !CompilerPath.IsUrl(relative) ? "" : relative;
    }

    internal static string NonModulePath(string path) => !CompilerPath.IsAbsolute(path) && !Relative(path) ? "./" + path : path;

    internal static string FromPaths(
        string relativeToBase,
        JsonElement paths,
        IReadOnlyList<ModuleSpecifierEnding> endings,
        string baseDirectory,
        CompilerOptions options,
        IFileSystem fileSystem,
        string currentDirectory,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (paths.ValueKind != JsonValueKind.Object)
            return "";
        var comparison = fileSystem.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var mapping in PackageJson.Properties(paths))
            foreach (var value in mapping.Value.EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();
                string normalized = CompilerPath.Normalize(JsonStrings.GetString(value));
                string pattern = RelativeIfSameVolume(normalized, baseDirectory, fileSystem.CaseSensitive);
                if (pattern.Length == 0)
                    pattern = normalized;
                int star = pattern.IndexOf('*');
                var candidates = endings.Select(
                    e => (Ending: e, Value: ProcessEnding(relativeToBase, [e], options, fileSystem, currentDirectory))).ToList();
                if (Extension(pattern).Length != 0)
                    candidates.Add((ModuleSpecifierEnding.JavaScript, relativeToBase));
                if (star >= 0)
                {
                    string prefix = pattern[..star], suffix = pattern[(star + 1)..];
                    foreach (var candidate in candidates)
                        if (candidate.Value.Length >= prefix.Length + suffix.Length && candidate.Value.StartsWith(prefix, comparison)
                            && candidate.Value.EndsWith(suffix, comparison) && Valid(candidate))
                        {
                            string matched = candidate.Value[prefix.Length..(candidate.Value.Length - suffix.Length)];
                            if (!Relative(matched))
                            {
                                int replacement = mapping.Name.IndexOf('*');
                                return replacement < 0
                                    ? mapping.Name
                                    : mapping.Name[..replacement] + matched + mapping.Name[(replacement + 1)..];
                            }
                        }
                }
                else if (candidates.Any(c => c.Ending != ModuleSpecifierEnding.Minimal && pattern == c.Value)
                    || candidates.Any(c => c.Ending == ModuleSpecifierEnding.Minimal && pattern == c.Value && Valid(c)))
                    return mapping.Name;
            }
        return "";
        bool Valid((ModuleSpecifierEnding Ending, string Value) candidate) => candidate.Ending != ModuleSpecifierEnding.Minimal
            || candidate.Value == ProcessEnding(relativeToBase, [candidate.Ending], options, fileSystem, currentDirectory);
    }
}

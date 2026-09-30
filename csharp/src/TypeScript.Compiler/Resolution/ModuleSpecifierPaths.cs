using TypeScript.Compiler.Text;
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
    private static readonly Utf8String[] extensions =
        [
            Utf8Literals.DTs,
            Utf8Literals.DMts,
            Utf8Literals.DCts,
            Utf8Literals.Mjs,
            Utf8Literals.Mts,
            Utf8Literals.Cjs,
            Utf8Literals.Cts,
            Utf8Literals.Ts,
            Utf8Literals.Js,
            Utf8Literals.Tsx,
            Utf8Literals.Jsx,
            Utf8Literals.Json
        ];
    private static readonly Utf8String[] shadowExtensions =
        [
            Utf8Literals.Ts,
            Utf8Literals.Tsx,
            Utf8Literals.DTs,
            Utf8Literals.Js,
            Utf8Literals.Jsx,
            Utf8Literals.Cts,
            Utf8Literals.DCts,
            Utf8Literals.Cjs,
            Utf8Literals.Mts,
            Utf8Literals.DMts,
            Utf8Literals.Mjs,
            Utf8Literals.NodeExtension,
            Utf8Literals.Json
        ];

    internal static ModuleSpecifierEnding[] AllowedEndings(CompilerOptions options, SourceFileNode source,
        ReferenceResolutionMode defaultMode, ReferenceResolutionMode syntaxMode = 0, Utf8String preference = default, Utf8String oldSpecifier = default,
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
        ReferenceResolutionMode mode, Utf8String preference, Utf8String oldSpecifier, CancellationToken cancellation)
    {
        if (JavaScriptExtension(oldSpecifier))
            return ModuleSpecifierEnding.JavaScript;
        if (oldSpecifier.EndsWith("/index"u8, StringComparison.Ordinal))
            return ModuleSpecifierEnding.Index;
        bool node = NodeResolution(options);
        if (preference == Utf8Literals.JsFormat || mode == ReferenceResolutionMode.Import && node)
            return AllowsTypeScript(options) && InferEnding(source, mode, node, cancellation) != ModuleSpecifierEnding.JavaScript
                ? ModuleSpecifierEnding.TypeScript : ModuleSpecifierEnding.JavaScript;
        if (preference == Utf8Literals.Minimal)
            return ModuleSpecifierEnding.Minimal;
        if (preference == Utf8Literals.Index)
            return ModuleSpecifierEnding.Index;
        if (AllowsTypeScript(options))
            return InferEnding(source, mode, node, cancellation);
        foreach (var import in source.Imports)
        {
            cancellation.ThrowIfCancellationRequested();
            Utf8String text = ImportText(import);
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
            Utf8String text = ImportText(import);
            if (!Relative(text) || node && mode == ReferenceResolutionMode.Require || MandatoryExtension(text))
                continue;
            if (TypeScriptExtension(text))
                return ModuleSpecifierEnding.TypeScript;
            js |= JavaScriptExtension(text);
        }
        return js ? ModuleSpecifierEnding.JavaScript : ModuleSpecifierEnding.Minimal;
    }

    private static bool NodeResolution(CompilerOptions options) =>
        options.EmitModuleResolutionKind is ModuleResolutionKind.Node16 or ModuleResolutionKind.NodeNext;

    private static bool AllowsTypeScript(CompilerOptions options) => options.AllowImportingTsExtensions == true
        || options.RewriteRelativeImportExtensions == true;

    private static Utf8String ImportText(SyntaxNode node) => node switch
    { StringLiteralNode text => text.Text, NoSubstitutionTemplateLiteralNode text => text.Text, _ => Utf8String.Empty };

    private static bool Relative(ReadOnlySpan<byte> path) =>
        path.SequenceEqual("."u8) || path.SequenceEqual(".."u8) || path.StartsWith("./"u8, StringComparison.Ordinal) || path.StartsWith("../"u8, StringComparison.Ordinal);

    private static bool MandatoryExtension(ReadOnlySpan<byte> path) => Extension(path) is var matchedText && (matchedText == ".mts"u8 || matchedText == ".d.mts"u8 || matchedText == ".mjs"u8 || matchedText == ".cts"u8 || matchedText == ".d.cts"u8 || matchedText == ".cjs"u8);

    private static bool TypeScriptExtension(ReadOnlySpan<byte> path) =>
        Extension(path) is var matchedText9 && (matchedText9 == ".ts"u8 || matchedText9 == ".tsx"u8 || matchedText9 == ".d.ts"u8 || matchedText9 == ".mts"u8 || matchedText9 == ".d.mts"u8 || matchedText9 == ".cts"u8 || matchedText9 == ".d.cts"u8);

    private static bool JavaScriptExtension(ReadOnlySpan<byte> path) => Extension(path) is var matchedText10 && (matchedText10 == ".js"u8 || matchedText10 == ".jsx"u8 || matchedText10 == ".mjs"u8 || matchedText10 == ".cjs"u8);

    internal static Utf8String Extension(ReadOnlySpan<byte> path)
    {
        foreach (Utf8String extension in extensions)
            if (path.EndsWith(extension, StringComparison.Ordinal))
                return extension;
        return Utf8String.Empty;
    }

    internal static Utf8String WithoutExtension(Utf8String path) => path[..(path.Length - Extension(path).Length)];

    internal static Utf8String ProcessEnding(Utf8String fileName, IReadOnlyList<ModuleSpecifierEnding> endings, CompilerOptions options,
        IFileSystem? fileSystem = null, Utf8String currentDirectory = default, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfZero(endings.Count);
        Utf8String extension = Extension(fileName);
        if (extension == ".json"u8 || extension == ".mjs"u8 || extension == ".cjs"u8 || extension == ""u8)
            return fileName;
        Utf8String bare = WithoutExtension(fileName);
        int js = Index(endings, ModuleSpecifierEnding.JavaScript), ts = Index(endings, ModuleSpecifierEnding.TypeScript);
        if ((extension == ".mts"u8 || extension == ".cts"u8 || extension == ".d.mts"u8 || extension == ".d.cts"u8) && ts >= 0 && ts < js)
            return fileName;
        if (extension == ".d.mts"u8 || extension == ".d.cts"u8)
            return bare + DeclarationJavaScriptExtension(extension);
        if (extension == ".mts"u8 || extension == ".cts"u8)
            return bare + JavaScriptFileExtension(fileName, options);
        if (extension == Utf8Literals.Ts && RealNonJavaScriptFileName(fileName) is { Length: > 0 } real)
            return real;
        switch (endings[0])
        {
            case ModuleSpecifierEnding.Minimal:
                Utf8String withoutIndex = bare.EndsWith("/index"u8, StringComparison.Ordinal) ? bare[..^6] : bare;
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

    internal static Utf8String DeclarationJavaScriptExtension(Utf8String extension) => extension switch
    { _ when extension == ".d.ts"u8 => Utf8Literals.Js, _ when extension == ".d.mts"u8 => Utf8Literals.Mjs, _ when extension == ".d.cts"u8 => Utf8Literals.Cjs, _ => extension[2..^3] };

    internal static Utf8String RealNonJavaScriptFileName(Utf8String fileName)
    {
        Utf8String name = CompilerPath.BaseName(fileName);
        if (!fileName.EndsWith(".ts"u8, StringComparison.Ordinal) || !name.Contains(".d."u8, StringComparison.Ordinal)
            || name.EndsWith(".d.ts"u8, StringComparison.Ordinal))
            return Utf8String.Empty;
        Utf8String bare = fileName[..^3];
        return bare[..bare.IndexOf(".d."u8, StringComparison.Ordinal)] + bare[bare.LastIndexOf((byte)'.')..];
    }

    internal static Utf8String JavaScriptFileExtension(Utf8String path, CompilerOptions options) => Extension(path) switch
    {
        var matchedText2 when matchedText2 == ".ts"u8 || matchedText2 == ".d.ts"u8 => Utf8Literals.Js,
        var matchedText3 when matchedText3 == ".tsx"u8 => options.Jsx == JsxEmit.Preserve ? Utf8Literals.Jsx : Utf8Literals.Js,
        var matchedText4 when matchedText4 == ".js"u8 => Utf8Literals.Js,
        var matchedText5 when matchedText5 == ".jsx"u8 => Utf8Literals.Jsx,
        var matchedText6 when matchedText6 == ".json"u8 => Utf8Literals.Json,
        var matchedText7 when matchedText7 == ".d.mts"u8 || matchedText7 == ".mts"u8 || matchedText7 == ".mjs"u8 => Utf8Literals.Mjs,
        var matchedText8 when matchedText8 == ".d.cts"u8 || matchedText8 == ".cts"u8 || matchedText8 == ".cjs"u8 => Utf8Literals.Cjs,
        _ => throw new ArgumentException("Unsupported module filename extension", nameof(path))
    };

    internal static Utf8String FromRootDirectories(IReadOnlyList<Utf8String> roots, Utf8String moduleFile, Utf8String sourceDirectory,
        IReadOnlyList<ModuleSpecifierEnding> endings, CompilerOptions options, IFileSystem fileSystem, Utf8String currentDirectory,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var targets = RelativeToRoots(moduleFile, roots, fileSystem.CaseSensitive, cancellation);
        Utf8String shortest = default;
        int separators = 0;
        foreach (Utf8String source in RelativeToRoots(sourceDirectory, roots, fileSystem.CaseSensitive, cancellation))
            foreach (Utf8String target in targets)
            {
                cancellation.ThrowIfCancellationRequested();
                Utf8String candidate = NonModulePath(CompilerPath.Relative(source, target, fileSystem.CaseSensitive));
                int count = candidate.Span.Count((byte)'/');
                if (shortest.Length == 0 || count < separators)
                {
                    shortest = candidate;
                    separators = count;
                }
            }
        return shortest.Length == 0 ? Utf8String.Empty : ProcessEnding(shortest, endings, options, fileSystem, currentDirectory, cancellation);
    }

    private static IReadOnlyList<Utf8String> RelativeToRoots(
        Utf8String path,
        IReadOnlyList<Utf8String> roots,
        bool caseSensitive,
        CancellationToken cancellation)
    {
        var result = new List<Utf8String>();
        foreach (Utf8String root in roots)
        {
            cancellation.ThrowIfCancellationRequested();
            Utf8String relative = RelativeIfSameVolume(path, root, caseSensitive);
            if (!relative.StartsWith(".."u8, StringComparison.Ordinal))
                result.Add(relative);
        }
        return result;
    }

    internal static Utf8String RelativeIfSameVolume(Utf8String path, Utf8String directory, bool caseSensitive)
    {
        Utf8String relative = CompilerPath.Relative(directory, CompilerPath.Resolve(directory, path), caseSensitive);
        return CompilerPath.IsAbsolute(relative) && !CompilerPath.IsUrl(relative) ? Utf8String.Empty : relative;
    }

    internal static Utf8String NonModulePath(Utf8String path) => !CompilerPath.IsAbsolute(path) && !Relative(path) ? Utf8Literals.CurrentDirectoryPrefix + path : path;

    internal static Utf8String FromPaths(
        Utf8String relativeToBase,
        IReadOnlyList<KeyValuePair<Utf8String, Utf8String[]>> paths,
        IReadOnlyList<ModuleSpecifierEnding> endings,
        Utf8String baseDirectory,
        CompilerOptions options,
        IFileSystem fileSystem,
        Utf8String currentDirectory,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var comparison = fileSystem.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var mapping in paths)
            foreach (Utf8String value in mapping.Value)
            {
                cancellation.ThrowIfCancellationRequested();
                Utf8String normalized = CompilerPath.Normalize(value);
                Utf8String pattern = RelativeIfSameVolume(normalized, baseDirectory, fileSystem.CaseSensitive);
                if (pattern.Length == 0)
                    pattern = normalized;
                int star = pattern.IndexOf((byte)'*');
                var candidates = endings.Select(
                    e => (Ending: e, Value: ProcessEnding(relativeToBase, [e], options, fileSystem, currentDirectory))).ToList();
                if (Extension(pattern).Length != 0)
                    candidates.Add((ModuleSpecifierEnding.JavaScript, relativeToBase));
                if (star >= 0)
                {
                    Utf8String prefix = pattern[..star], suffix = pattern[(star + 1)..];
                    foreach (var candidate in candidates)
                        if (candidate.Value.Length >= prefix.Length + suffix.Length && candidate.Value.StartsWith(prefix, comparison)
                            && candidate.Value.EndsWith(suffix, comparison) && Valid(candidate))
                        {
                            Utf8String matched = candidate.Value[prefix.Length..(candidate.Value.Length - suffix.Length)];
                            if (!Relative(matched))
                            {
                                int replacement = mapping.Key.IndexOf((byte)'*');
                                return replacement < 0
                                    ? mapping.Key
                                    : mapping.Key[..replacement] + matched + mapping.Key[(replacement + 1)..];
                            }
                        }
                }
                else if (candidates.Any(c => c.Ending != ModuleSpecifierEnding.Minimal && pattern == c.Value)
                    || candidates.Any(c => c.Ending == ModuleSpecifierEnding.Minimal && pattern == c.Value && Valid(c)))
                    return mapping.Key;
            }
        return Utf8String.Empty;
        bool Valid((ModuleSpecifierEnding Ending, Utf8String Value) candidate) => candidate.Ending != ModuleSpecifierEnding.Minimal
            || candidate.Value == ProcessEnding(relativeToBase, [candidate.Ending], options, fileSystem, currentDirectory);
    }
}

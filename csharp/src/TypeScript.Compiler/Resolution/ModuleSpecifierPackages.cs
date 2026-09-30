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

internal sealed partial class ModuleSpecifierPackages(IFileSystem fileSystem, CompilerOptions options, Utf8String currentDirectory,
    Utf8String commonSourceDirectory, IReadOnlyList<Utf8String>? contentMapperExtensions = null, SemanticVersion? compilerVersion = null)
{
    private readonly PackageJsonCache packages = new(fileSystem, currentDirectory);
    private readonly SemanticVersion version = compilerVersion ?? new(7, 1, 0, Utf8Literals.Dev);
    private StringComparison Comparison => fileSystem.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    internal Utf8String FromExports(Utf8String target, Utf8String directory, Utf8String name, JsonElement exports, IReadOnlyList<Utf8String> conditions,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (PackageJson.MapKind(exports) == PackageMapKind.Subpaths)
            foreach (var mapping in PackageJson.Properties(exports))
            {
                Utf8String subName = CompilerPath.Normalize(CompilerPath.Combine(name, JsonStrings.GetName(mapping)));
                var result = FromMap(
                    target,
                    directory,
                    subName,
                    mapping.Value,
                    conditions,
                    MatchMode(JsonStrings.GetName(mapping)),
                    false,
                    false,
                    cancellation);
                if (result.Length != 0)
                    return result;
            }
        return FromMap(target, directory, name, exports, conditions, PackageSpecifierMatch.Exact, false, false, cancellation);
    }

    internal Utf8String FromImports(Utf8String target, Utf8String sourceDirectory, ReferenceResolutionMode importMode, bool preferTypeScript,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (options.ResolvePackageJsonImports == false)
            return Utf8String.Empty;
        PackageJson? package = null;
        foreach (Utf8String directory in PackageJsonCache.Ancestors(CompilerPath.Resolve(currentDirectory, sourceDirectory)))
        {
            cancellation.ThrowIfCancellationRequested();
            if (packages.Get(directory).Contents is { } found)
            {
                package = found;
                break;
            }
        }
        if (package?.Get(Utf8Literals.Imports) is not { ValueKind: JsonValueKind.Object } imports)
            return Utf8String.Empty;
        var conditions = Conditions(importMode);
        foreach (var entry in PackageJson.Properties(imports))
        {
            Utf8String key = JsonStrings.GetName(entry);
            if (key == "#"u8 || key == "#/"u8 || !key.StartsWith((byte)'#'))
                continue;
            if (key.StartsWith("#/"u8, StringComparison.Ordinal) && ResolutionKind() == Utf8Literals.Node16Option)
                continue;
            Utf8String result = FromMap(
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
        return Utf8String.Empty;
    }

    internal Utf8String[] Conditions(ReferenceResolutionMode mode)
    {
        Utf8String resolution = ResolutionKind();
        return [mode == ReferenceResolutionMode.Import || mode == 0 && resolution == Utf8Literals.Bundler ? Utf8Literals.ImportKeyword : Utf8Literals.RequireKeyword,
            .. options.NoDtsResolution != true ? new Utf8String[]{ Utf8Literals.Types } : [],
            .. resolution != Utf8Literals.Bundler ? new Utf8String[]{ Utf8Literals.Node } : [], .. options.CustomConditions ?? []];
    }

    private Utf8String ResolutionKind() => options.EmitModuleResolutionKind switch
    {
        ModuleResolutionKind.Node16 => Utf8Literals.Node16Option,
        ModuleResolutionKind.NodeNext => Utf8Literals.Nodenext,
        _ => Utf8Literals.Bundler
    };

    private static PackageSpecifierMatch MatchMode(Utf8String key) => key.EndsWith((byte)'/') ? PackageSpecifierMatch.Directory
        : key.Contains((byte)'*') ? PackageSpecifierMatch.Pattern : PackageSpecifierMatch.Exact;

    internal Utf8String FromMap(Utf8String target, Utf8String directory, Utf8String name, JsonElement map, IReadOnlyList<Utf8String> conditions,
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
                        Utf8String key = JsonStrings.GetName(entries[i]);
                        if (key == Utf8Literals.Default || conditions.Contains(key)
                            || conditions.Contains(Utf8Literals.Types) && key.StartsWith("types@"u8, StringComparison.Ordinal)
                                && VersionRange.Parse(key[6..])?.Test(version) == true)
                            pending.Push(entries[i].Value);
                    }
                    break;
                case JsonValueKind.String:
                    Utf8String result = MatchTarget(target, directory, name, JsonStrings.GetString(value), mode, imports, preferTypeScript);
                    if (result.Length != 0)
                        return result;
                    break;
            }
        }
        return Utf8String.Empty;
    }

    private Utf8String MatchTarget(Utf8String target, Utf8String directory, Utf8String name, Utf8String value, PackageSpecifierMatch mode,
        bool imports, bool preferTypeScript)
    {
        Utf8String output = imports ? OutputFile(target, false) : Utf8String.Empty;
        Utf8String declaration = imports ? OutputFile(target, true) : Utf8String.Empty;
        Utf8String pattern = CompilerPath.Normalize(CompilerPath.Combine(directory, value));
        Utf8String extension = ModuleSpecifierPaths.Extension(target);
        Utf8String swapped = (extension == ".ts"u8 || extension == ".tsx"u8 || extension == ".d.ts"u8 || extension == ".mts"u8 || extension == ".d.mts"u8 || extension == ".cts"u8 || extension == ".d.cts"u8)
            ? ModuleSpecifierPaths.WithoutExtension(target) + ModuleSpecifierPaths.JavaScriptFileExtension(target, options) : Utf8String.Empty;
        bool typed = preferTypeScript && !CompilerPath.IsDeclarationFile(target)
            && (target.EndsWith(".ts"u8, StringComparison.Ordinal) || target.EndsWith(".tsx"u8, StringComparison.Ordinal)
            || target.EndsWith(".mts"u8, StringComparison.Ordinal) || target.EndsWith(".cts"u8, StringComparison.Ordinal));
        switch (mode)
        {
            case PackageSpecifierMatch.Exact:
                return swapped.Length != 0 && Equal(swapped, pattern) || Equal(target, pattern)
                    || output.Length != 0 && Equal(output, pattern) || declaration.Length != 0 && Equal(declaration, pattern) ? name : Utf8String.Empty;
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
                return Utf8String.Empty;
            case PackageSpecifierMatch.Pattern:
                int star = pattern.IndexOf((byte)'*');
                Utf8String prefix = star < 0 ? pattern : pattern[..star], suffix = star < 0 ? Utf8String.Empty : pattern[(star + 1)..];
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
                return Utf8String.Empty;
                bool Matches(Utf8String path) => path.Length >= prefix.Length + suffix.Length
                    && path.StartsWith(prefix, Comparison) && path.EndsWith(suffix, Comparison);
                Utf8String Substitute(Utf8String path)
                {
                    Utf8String replacement = path[prefix.Length..(path.Length - suffix.Length)];
                    int index = name.IndexOf((byte)'*');
                    return index < 0 ? name : name[..index] + replacement + name[(index + 1)..];
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        Utf8String DirectoryName(Utf8String path, bool includeValue) => includeValue
            ? CompilerPath.Normalize(CompilerPath.Combine(name, value, Relative(pattern, path)))
            : CompilerPath.Combine(name, Relative(pattern, path));
    }

    private bool Equal(Utf8String first, Utf8String second) => CompilerPath.Resolve(currentDirectory, first)
        .Equals(CompilerPath.Resolve(currentDirectory, second), Comparison);

    private bool Contains(Utf8String directory, Utf8String path) => CompilerPath.Contains(CompilerPath.Resolve(currentDirectory, directory),
            CompilerPath.Resolve(currentDirectory, path), fileSystem.CaseSensitive);

    private Utf8String Relative(Utf8String directory, Utf8String path) => CompilerPath.Relative(CompilerPath.Resolve(currentDirectory, directory),
            CompilerPath.Resolve(currentDirectory, path), fileSystem.CaseSensitive);

    internal Utf8String OutputFile(Utf8String source, bool declaration)
    {
        Utf8String directory = declaration
            ? options.DeclarationDir ?? options.OutDir ?? Utf8String.Empty
            : options.OutDir ?? Utf8String.Empty;
        Utf8String path = directory.Length == 0 ? source : CompilerPath.Resolve(CompilerPath.Resolve(currentDirectory, directory),
            Relative(commonSourceDirectory, source));
        if (!declaration)
        {
            Utf8String outputExtension = source.EndsWith(".json"u8, StringComparison.Ordinal) ? Utf8Literals.Json
                : options.Jsx == JsxEmit.Preserve
                    && (source.EndsWith(".jsx"u8, StringComparison.Ordinal) || source.EndsWith(".tsx"u8, StringComparison.Ordinal)) ? Utf8Literals.Jsx
                : source.EndsWith(".mts"u8, StringComparison.Ordinal) || source.EndsWith(".mjs"u8, StringComparison.Ordinal) ? Utf8Literals.Mjs
                : source.EndsWith(".cts"u8, StringComparison.Ordinal) || source.EndsWith(".cjs"u8, StringComparison.Ordinal) ? Utf8Literals.Cjs : Utf8Literals.Js;
            return ChangeExtension(path, outputExtension);
        }
        var mapped = contentMapperExtensions?.Where(e => path.EndsWith(
            e,
            StringComparison.Ordinal)).OrderByDescending(e => e.Length).FirstOrDefault();
        if (mapped is { } mappedExtension && !mappedExtension.IsEmpty)
            return path[..^mappedExtension.Length] + Utf8Literals.D + mappedExtension + Utf8Literals.Ts;
        Utf8String bare = ModuleSpecifierPaths.WithoutExtension(path);
        if (bare == path)
            bare = path[..(path.Length - CompilerPath.Extension(path).Length)];
        Utf8String extension = path.EndsWith(".mts"u8, StringComparison.Ordinal) || path.EndsWith(".mjs"u8, StringComparison.Ordinal) ? Utf8Literals.DMts
            : path.EndsWith(".cts"u8, StringComparison.Ordinal) || path.EndsWith(".cjs"u8, StringComparison.Ordinal) ? Utf8Literals.DCts
            : path.EndsWith(".ts"u8, StringComparison.Ordinal) || path.EndsWith(".tsx"u8, StringComparison.Ordinal)
                || path.EndsWith(".js"u8, StringComparison.Ordinal) || path.EndsWith(".jsx"u8, StringComparison.Ordinal) ? Utf8Literals.DTs
            : CompilerPath.Extension(path) is { Length: > 0 } other ? Utf8Literals.D + other + Utf8Literals.Ts : Utf8Literals.DTs;
        return bare + extension;
    }

    private static Utf8String ChangeExtension(Utf8String path, Utf8String extension)
    {
        Utf8String current = ModuleSpecifierPaths.Extension(path);
        return current.Length == 0 ? path : path[..^current.Length] + extension;
    }

    private static Utf8String ChangeFullExtension(Utf8String path, Utf8String extension)
    {
        if (CompilerPath.IsDeclarationFile(path))
        {
            int marker = path.LastIndexOf(".d."u8, StringComparison.Ordinal);
            if (marker >= 0)
                return path[..marker] + extension;
        }
        return ChangeExtension(path, extension);
    }
}

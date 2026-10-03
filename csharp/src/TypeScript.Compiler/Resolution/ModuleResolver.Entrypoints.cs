using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Resolution;

internal enum EntrypointEnding { Fixed, ExtensionChangeable, Changeable }

internal sealed record ResolvedEntrypoint(Utf8String OriginalFileName, Utf8String ResolvedFileName,
    Utf8String ModuleSpecifier, EntrypointEnding Ending, IReadOnlySet<Utf8String>? IncludeConditions = null,
    IReadOnlySet<Utf8String>? ExcludeConditions = null)
{
    internal Utf8String SymlinkOrRealPath => OriginalFileName.IsEmpty ? ResolvedFileName : OriginalFileName;

    internal Utf8String Format(CompilerOptions options, SourceFileNode source, ReferenceResolutionMode mode,
        Utf8String preference, CancellationToken cancellation, IReadOnlyList<ModuleSpecifierEnding>? allowedEndings = null)
    {
        if (Ending == EntrypointEnding.Fixed) return ModuleSpecifier;
        var preferred = allowedEndings is { Count: > 0 } ? allowedEndings[0]
            : ModuleSpecifierPaths.AllowedEndings(options, source, mode, preference: preference, cancellation: cancellation)[0];
        var extension = DeclarationExtension(ModuleSpecifier);
        bool declaration = !extension.IsEmpty;
        if (!declaration) extension = ModuleSpecifierPaths.Extension(ModuleSpecifier);
        bool typescript = extension == ".ts"u8 || extension == ".tsx"u8 || extension == ".mts"u8 || extension == ".cts"u8;
        bool javascript = extension == ".js"u8 || extension == ".jsx"u8 || extension == ".mjs"u8 || extension == ".cjs"u8;
        if (!declaration && !typescript && !javascript) return ModuleSpecifier;
        if (declaration && (Ending != EntrypointEnding.Changeable || extension != ".d.ts"u8
            || preferred is ModuleSpecifierEnding.JavaScript or ModuleSpecifierEnding.TypeScript))
            return ModuleSpecifier[..^extension.Length] + ModuleSpecifierPaths.DeclarationJavaScriptExtension(extension);
        if (typescript && preferred == ModuleSpecifierEnding.TypeScript) return ModuleSpecifier;
        if (typescript && (Ending != EntrypointEnding.Changeable || preferred == ModuleSpecifierEnding.JavaScript))
            return ModuleSpecifier[..^extension.Length] + ModuleSpecifierPaths.JavaScriptFileExtension(ModuleSpecifier, options);
        if (Ending != EntrypointEnding.Changeable || preferred is ModuleSpecifierEnding.JavaScript or ModuleSpecifierEnding.TypeScript) return ModuleSpecifier;
        var specifier = ModuleSpecifier[..^extension.Length];
        return preferred == ModuleSpecifierEnding.Minimal && specifier.EndsWith("/index"u8) ? specifier[..^6] : specifier;
    }

    internal static Utf8String DeclarationExtension(Utf8String path)
    {
        foreach (var extension in new Utf8String[] { ".d.ts"u8, ".d.mts"u8, ".d.cts"u8 }) if (path.EndsWith(extension)) return extension;
        if (!path.EndsWith(".ts"u8)) return default;
        var name = CompilerPath.BaseName(path);
        int marker = name.IndexOf(".d."u8);
        return marker < 0 ? default : name[marker..];
    }
}

public sealed partial class ModuleResolver
{
    internal IReadOnlyList<ResolvedEntrypoint> GetEntrypoints(PackageJsonEntry package, Utf8String packageName,
        bool directorySearch, CancellationToken cancellation = default) =>
        new Request(this, packageName, package.Directory, 0, false, cancellation).Entrypoints(package, packageName, directorySearch);

    private sealed partial class Request
    {
        internal IReadOnlyList<ResolvedEntrypoint> Entrypoints(PackageJsonEntry package, Utf8String packageName, bool directorySearch)
        {
            cancellation.ThrowIfCancellationRequested();
            extensions = Extensions.TypeScript | Extensions.Declaration;
            esm = false;
            exports = imports = true;
            List<ResolvedEntrypoint> result = [];
            Utf8String[] fileExtensions = [".ts"u8, ".tsx"u8, ".cts"u8, ".mts"u8, ".d.ts"u8, ".d.cts"u8, ".d.mts"u8];
            var exportMap = package.Contents?.Get(Utf8Literals.Exports) ?? default;
            if (exportMap.ValueKind != JsonValueKind.Undefined)
            {
                Stack<(Utf8String Subpath, JsonElement Target, HashSet<Utf8String>? Include, HashSet<Utf8String>? Exclude)> pending = [];
                if (exportMap.ValueKind == JsonValueKind.Object && PackageJson.MapKind(exportMap) == PackageMapKind.Subpaths)
                    foreach (var property in PackageJson.Properties(exportMap).Reverse()) pending.Push((JsonStrings.GetName(property), property.Value, null, null));
                else pending.Push(("."u8, exportMap, null, null));
                while (pending.TryPop(out var work))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (work.Target.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var element in work.Target.EnumerateArray().Reverse()) pending.Push((work.Subpath, element, work.Include, work.Exclude));
                    }
                    else if (work.Target.ValueKind == JsonValueKind.Object)
                    {
                        List<(Utf8String, JsonElement, HashSet<Utf8String>?, HashSet<Utf8String>?)> branches = [];
                        List<Utf8String> previous = [];
                        var excluded = work.Exclude;
                        foreach (var property in PackageJson.Properties(work.Target))
                        {
                            var condition = JsonStrings.GetName(property);
                            if (excluded?.Contains(condition) == true) continue;
                            bool always = condition == "default"u8 || condition == "types"u8
                                || condition.StartsWith("types@"u8) && VersionRange.Parse(condition[6..])?.Test(resolver.CompilerVersion) == true;
                            var included = work.Include;
                            if (!always)
                            {
                                included = included is null ? [] : new(included);
                                included.Add(condition);
                                excluded = excluded is null ? null : new(excluded);
                                if (previous.Count != 0) (excluded ??= []).UnionWith(previous);
                            }
                            previous.Add(condition);
                            branches.Add((work.Subpath, property.Value, included, excluded));
                            if (always) break;
                        }
                        for (int i = branches.Count - 1; i >= 0; i--) pending.Push(branches[i]);
                    }
                    else if (work.Target.ValueKind == JsonValueKind.String)
                    {
                        var target = JsonStrings.GetString(work.Target);
                        if (!target.StartsWith("./"u8)) continue;
                        int star = target.IndexOf((byte)'*');
                        if (star >= 0)
                        {
                            if (target.LastIndexOf((byte)'*') != star) continue;
                            var pattern = CompilerPath.Resolve(package.Directory, target);
                            int patternStar = pattern.IndexOf((byte)'*');
                            var leading = pattern[..patternStar];
                            var trailing = pattern[(patternStar + 1)..];
                            var include = target[..star] + "**/*"u8 + target[(star + 1)..];
                            var includeExtension = ModuleSpecifierPaths.Extension(include);
                            var declaration = ResolvedEntrypoint.DeclarationExtension(include);
                            if (!declaration.IsEmpty) include = include[..^declaration.Length] + ".*"u8;
                            else if (!includeExtension.IsEmpty) include = include[..^includeExtension.Length] + ".*"u8;
                            foreach (var file in FileMatcher.ReadDirectory(fs, package.Directory, resolver.cwd, fileExtensions,
                                includes: [include], cancellation: cancellation))
                            {
                                var match = Match(file);
                                if (match is null)
                                {
                                    var extension = ModuleSpecifierPaths.Extension(file);
                                    var full = ResolvedEntrypoint.DeclarationExtension(file);
                                    match = Match(file[..^(full.IsEmpty ? extension.Length : full.Length)] + ModuleSpecifierPaths.JavaScriptFileExtension(file, options));
                                }
                                if (match is not { } captured) continue;
                                int subpathStar = work.Subpath.IndexOf((byte)'*');
                                var subpath = subpathStar < 0 ? work.Subpath : work.Subpath[..subpathStar] + captured + work.Subpath[(subpathStar + 1)..];
                                Add(file, CompilerPath.Resolve(packageName, subpath), target.EndsWith((byte)'*') ? EntrypointEnding.ExtensionChangeable : EntrypointEnding.Fixed,
                                    work.Include, work.Exclude);
                            }
                            Utf8String? Match(Utf8String file)
                            {
                                var comparison = fs.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                                return file.Length >= leading.Length + trailing.Length && file.StartsWith(leading, comparison) && file.EndsWith(trailing, comparison)
                                    ? file.Substring(leading.Length, file.Length - leading.Length - trailing.Length) : null;
                            }
                        }
                        else if (!InvalidSegments(target[2..]) && PackageFile(extensions, CompilerPath.Resolve(package.Directory, target), target) is { } resolved)
                            Add(resolved.FileName, CompilerPath.Resolve(packageName, work.Subpath), EntrypointEnding.Fixed, work.Include, work.Exclude);
                    }
                }
            }
            else
            {
                var main = LoadDirectory(extensions, package.Directory, package.Contents);
                if (main is not null) Add(main.FileName, packageName, EntrypointEnding.Fixed);
                if (directorySearch)
                    foreach (var file in FileMatcher.ReadDirectory(fs, package.Directory, resolver.cwd, fileExtensions,
                        [Utf8Literals.NodeModules], ["**/*"u8], cancellation: cancellation))
                        if (main is null || !file.Equals(main.FileName, fs.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                            Add(file, CompilerPath.Resolve(packageName, CompilerPath.Relative(package.Directory, file, fs.CaseSensitive)), EntrypointEnding.Changeable);
            }
            return result;

            void Add(Utf8String file, Utf8String specifier, EntrypointEnding ending, HashSet<Utf8String>? include = null, HashSet<Utf8String>? exclude = null)
            {
                var real = fs.RealPath(file);
                result.Add(new(real == file ? default : file, real, specifier, ending, include, exclude));
            }
        }
    }
}

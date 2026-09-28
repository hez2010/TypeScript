using System.Runtime.CompilerServices;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Resolution;

public sealed record PackageId(string Name, string SubModuleName, string Version, string PeerDependencies = "")
{
    public override string ToString() => Name + (SubModuleName.Length == 0 ? "" : "/" + SubModuleName) + "@" + Version + PeerDependencies;
}

public readonly record struct ResolutionTrace(string Operation, string Path, string Detail = "");

public sealed record ResolvedModule(string FileName = "", string Extension = "", PackageId? PackageId = null,
    string OriginalPath = "", bool External = false, bool UsingTsExtension = false, bool UsingExtraExtension = false)
{
    public bool IsResolved => FileName.Length != 0;
    internal bool IsArbitraryExtension => IsResolved && !UsingExtraExtension
        && Extension is not (".ts" or ".tsx" or ".d.ts" or ".mts" or ".d.mts" or ".cts" or ".d.cts"
            or ".js" or ".jsx" or ".mjs" or ".cjs" or ".json");
    public bool Primary { get; init; }
    public string AlternateResult { get; init; } = "";
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];
    public IReadOnlyList<ResolutionTrace> Trace { get; init; } = [];
    public IReadOnlyList<string> AffectingLocations { get; init; } = [];
}

/// <summary>A resolver owns its options and one generation of resolution/package caches.</summary>
public sealed partial class ModuleResolver
{
    private readonly IFileSystem fs;
    private readonly CompilerOptions options = new();
    private readonly string cwd, configFile, typingsLocation;
    private readonly string[] extraExtensions;
    private readonly object gate = new();
    private readonly Dictionary<(string Name, string Directory, ReferenceResolutionMode Mode, bool Types, bool Inferred), ResolvedModule> cache = [];
    private long generation;
    public PackageJsonCache Packages { get; }
    public SemanticVersion CompilerVersion { get; }
    public string ResolutionKind { get; }

    public ModuleResolver(IFileSystem fileSystem, CompilerOptions compilerOptions, string currentDirectory,
        string configFile = "", string typingsLocation = "", IEnumerable<string>? extraExtensions = null,
        SemanticVersion? compilerVersion = null)
    {
        fs = fileSystem;
        options.Merge(compilerOptions);
        cwd = CompilerPath.Resolve("/", currentDirectory);
        this.configFile = configFile;
        this.typingsLocation = typingsLocation;
        this.extraExtensions = extraExtensions?.OrderByDescending(e => e.Length).ToArray() ?? [];
        CompilerVersion = compilerVersion ?? new(7, 1, 0, "dev");
        Packages = new(fs, cwd);
        ResolutionKind = options.EmitModuleResolutionKind switch
        {
            ModuleResolutionKind.Node16 => "node16",
            ModuleResolutionKind.NodeNext => "nodenext",
            _ => "bundler"
        };
    }

    public void Invalidate()
    {
        lock (gate)
        {
            generation++;
            cache.Clear();
            Packages.Invalidate();
        }
    }

    public ResolvedModule Resolve(string name, string containingFile, ReferenceResolutionMode mode = default,
        bool typeReference = false, CancellationToken cancellation = default) =>
        Parser.RunParse(ResolveAsync(name, containingFile, mode, typeReference, cancellation));

    public async ValueTask<ResolvedModule> ResolveAsync(string name, string containingFile, ReferenceResolutionMode mode = default,
        bool typeReference = false, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        string directory = CompilerPath.DirectoryName(CompilerPath.Resolve(cwd, containingFile));
        bool inferred = typeReference && containingFile.EndsWith("__inferred type names__.ts", StringComparison.Ordinal);
        var key = (name, fs.CaseSensitive ? directory : directory.ToLowerInvariant(), mode, typeReference, inferred);
        long version;
        lock (gate)
        {
            version = generation;
            if (options.TraceResolution != true && cache.TryGetValue(key, out var cached))
                return cached;
        }
        var request = new Request(this, name, directory, mode, typeReference, cancellation, inferredTypes: inferred);
        var result = await request.RunAsync().ConfigureAwait(false);
        if (typeReference)
            result = result with { Primary = request.Primary };
        result = result with
        {
            Diagnostics = request.diagnostics.ToArray(),
            Trace = request.trace.ToArray(),
            AffectingLocations = request.locations.Order(StringComparer.Ordinal).ToArray()
        };
        lock (gate)
            if (generation == version)
                cache[key] = result;
        return result;
    }

    public string[] TypeRoots() => options.TypeRoots ?? PackageJsonCache.Ancestors(
        configFile.Length == 0 ? cwd : CompilerPath.DirectoryName(configFile))
        .Select(d => CompilerPath.Combine(d, "node_modules/@types")).ToArray();

    public string[] AutomaticTypeDirectives()
    {
        string[] types = options.Types ?? [];
        if (!types.Contains("*"))
            return types;
        var matches = new List<string>();
        foreach (string root in TypeRoots())
            foreach (string name in fs.GetAccessibleEntries(root).Directories)
                if (!name.StartsWith('.')
                    && Packages.Get(CompilerPath.Combine(root, name)).Contents?.Get("typings").ValueKind != JsonValueKind.Null)
                    matches.Add(name);
        return types.SelectMany(t => t == "*" ? matches : (IEnumerable<string>)[t]).Distinct(StringComparer.Ordinal).ToArray();
    }

    public string? ResolvePackageDirectory(string name, string containingFile) =>
        ResolvePackageDirectoryInfo(name, containingFile)?.FileName;

    internal ResolvedModule? ResolvePackageDirectoryInfo(string name, string containingFile)
    {
        (string package, _) = PackageName(name);
        foreach (string directory in PackageJsonCache.Ancestors(CompilerPath.DirectoryName(CompilerPath.Resolve(cwd, containingFile))))
        {
            if (CompilerPath.BaseName(directory) == "node_modules")
                continue;
            string path = CompilerPath.Combine(directory, "node_modules", package);
            if (fs.DirectoryExists(path))
                return Result(path);
            path = CompilerPath.Combine(directory, "node_modules/@types", Mangle(package));
            if (fs.DirectoryExists(path))
                return Result(path);
        }
        return null;
        ResolvedModule Result(string path)
        {
            string real = options.PreserveSymlinks == true ? path : fs.RealPath(path);
            return new(real, OriginalPath: real == path ? "" : path);
        }
    }

    public static (string Name, string Subpath) PackageName(string name)
    {
        int slash = name.IndexOf('/');
        if (name.StartsWith('@') && slash >= 0)
            slash = name.IndexOf('/', slash + 1);
        return slash < 0 ? (name, "") : (name[..slash], name[(slash + 1)..]);
    }

    public static ResolvedModule ResolveConfig(IFileSystem fileSystem, string currentDirectory, string name, string containingFile) =>
        Parser.RunParse(ResolveConfigAsync(fileSystem, currentDirectory, name, containingFile));

    public static async ValueTask<ResolvedModule> ResolveConfigAsync(IFileSystem fileSystem, string currentDirectory, string name,
        string containingFile, CancellationToken cancellation = default)
    {
        var options = new CompilerOptions();
        options.SetString("moduleResolution", "nodenext");
        var resolver = new ModuleResolver(fileSystem, options, currentDirectory);
        var request = new Request(resolver, name, CompilerPath.DirectoryName(containingFile), ReferenceResolutionMode.Require,
            false, cancellation, configLookup: true);
        var result = await request.RunAsync().ConfigureAwait(false);
        return result with
        {
            Diagnostics = request.diagnostics.ToArray(),
            AffectingLocations = request.locations.Order(StringComparer.Ordinal).ToArray()
        };
    }

    public static string Mangle(string name)
    {
        int slash = name.IndexOf('/');
        return name.StartsWith('@') && slash >= 0 ? name[1..slash] + "__" + name[(slash + 1)..] : name;
    }

    public static bool Relative(string name) => name is "." or ".." || name.StartsWith("./", StringComparison.Ordinal)
        || name.StartsWith("../", StringComparison.Ordinal) || CompilerPath.IsAbsolute(name) && !CompilerPath.IsUrl(name);

    public static bool Relative(ReadOnlySpan<char> name) => name is "." or ".." || name.StartsWith("./", StringComparison.Ordinal)
        || name.StartsWith("../", StringComparison.Ordinal) || CompilerPath.EncodedRootLength(name) > 0;

    public static string Extension(string path)
    {
        foreach (string ext in new[] { ".d.ts", ".d.mts", ".d.cts", ".tsx", ".ts", ".jsx", ".js", ".json", ".mts", ".cts", ".mjs", ".cjs" })
            if (path.EndsWith(ext, StringComparison.Ordinal))
                return ext;
        return CompilerPath.Extension(path);
    }

    [Flags]
    private enum Extensions
    {
        None = 0,
        TypeScript = 1,
        JavaScript = 2,
        Declaration = 4,
        Json = 8
    }

    private sealed partial class Request
    {
        private readonly ModuleResolver resolver;
        private readonly CompilerOptions options;
        private readonly IFileSystem fs;
        private readonly string name, directory;
        private readonly ReferenceResolutionMode mode;
        private readonly bool types;
        private readonly CancellationToken cancellation;
        private readonly string[] conditions;
        private readonly HashSet<(string, string)> active;
        internal readonly List<ResolutionTrace> trace = [];
        internal readonly List<Diagnostic> diagnostics = [];
        internal readonly HashSet<string> locations = new(StringComparer.Ordinal);
        private Extensions extensions;
        private bool esm, exports, imports, fromConfig;
        private readonly bool configLookup;
        private readonly bool inferredTypes;
        internal bool Primary { get; private set; }

        internal Request(
            ModuleResolver resolver,
            string name,
            string directory,
            ReferenceResolutionMode mode,
            bool types,
            CancellationToken cancellation,
            HashSet<(string, string)>? active = null,
            bool configLookup = false,
            bool inferredTypes = false)
        {
            this.resolver = resolver;
            this.name = name;
            this.directory = directory;
            this.mode = mode;
            this.types = types;
            this.cancellation = cancellation;
            this.active = active ?? [];
            this.configLookup = configLookup;
            this.inferredTypes = inferredTypes;
            options = resolver.options;
            fs = resolver.fs;
            extensions = types ? Extensions.Declaration : options.NoDtsResolution == true
                ? Extensions.TypeScript | Extensions.JavaScript : Extensions.TypeScript | Extensions.JavaScript | Extensions.Declaration;
            if (!types && (options.ResolveJsonModule ?? (resolver.ResolutionKind == "bundler"
                || options.Module is ModuleKind.Node20 or ModuleKind.NodeNext)))
                extensions |= Extensions.Json;
            if (configLookup)
                extensions = Extensions.Json;
            esm = resolver.ResolutionKind != "bundler" && mode == ReferenceResolutionMode.Import;
            conditions = [mode == ReferenceResolutionMode.Import || mode == 0 && resolver.ResolutionKind == "bundler"
                ? "import"
                : "require",
                .. options.NoDtsResolution != true ? new[] { "types" } : [],
                .. resolver.ResolutionKind == "bundler" ? [] : new[] { "node" }, .. options.CustomConditions ?? []];
            // The pinned Node16/Next resolver always enables maps; only Bundler consults these switches.
            exports = resolver.ResolutionKind != "bundler" || options.ResolvePackageJsonExports != false;
            imports = resolver.ResolutionKind != "bundler" || options.ResolvePackageJsonImports != false;
        }

        private void Trace(string operation, string path, string detail = "")
        {
            if (options.TraceResolution == true)
                trace.Add(new(operation, path, detail));
        }

        private bool DirectoryExists(string path)
        {
            cancellation.ThrowIfCancellationRequested();
            locations.Add(path);
            bool exists = fs.DirectoryExists(path);
            Trace("directory", path, exists ? "exists" : "missing");
            return exists;
        }

        private PackageJson? Package(string path)
        {
            locations.Add(CompilerPath.Combine(path, "package.json"));
            var entry = resolver.Packages.Get(path);
            Trace("package", path, entry.Contents is null ? "missing" : entry.Contents.Parseable ? "parsed" : "invalid");
            return entry.Contents;
        }

        private PackageJson? Scope()
        {
            foreach (string path in PackageJsonCache.Ancestors(directory))
                if (Package(path) is { } package)
                    return package;
            return null;
        }

        internal async ValueTask<ResolvedModule> RunAsync()
        {
            await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
                ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
            cancellation.ThrowIfCancellationRequested();
            if (!active.Add((name, directory)))
            {
                Trace("cycle", directory, name);
                return new();
            }
            try
            {
                Trace("resolve", directory, name + ":" + string.Join(',', conditions));
                var result = types ? await TypeReferenceAsync().ConfigureAwait(false)
                    : await ModuleAsync().ConfigureAwait(false);
                result ??= new();
                if (!types && result.IsResolved && result.External && conditions.Contains("import") && exports
                    && !IsTypeScript(result.Extension) && !Relative(name))
                {
                    exports = false;
                    extensions &= Extensions.TypeScript | Extensions.Declaration;
                    int count = diagnostics.Count;
                    var alternate = await ModuleAsync().ConfigureAwait(false);
                    diagnostics.RemoveRange(count, diagnostics.Count - count);
                    if (alternate?.IsResolved == true && alternate.External)
                        result = result with { AlternateResult = alternate.FileName };
                }
                if (!types && resolver.typingsLocation.Length != 0 && !Relative(name)
                    && (!result.IsResolved || !IsTypeScript(result.Extension) && result.Extension != ".json"))
                {
                    var global = await ImmediateAsync(Extensions.Declaration, resolver.typingsLocation).ConfigureAwait(false);
                    if (global?.IsResolved == true)
                        result = global with { External = true };
                }
                if (result.IsResolved && options.PreserveSymlinks != true && (types || result.External && !Relative(name)))
                {
                    string real = CompilerPath.Normalize(fs.RealPath(result.FileName));
                    Trace("realpath", result.FileName, real);
                    if (!real.Equals(result.FileName, fs.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                        result = result with { OriginalPath = result.FileName, FileName = real };
                }
                Trace("result", result.FileName, result.PackageId?.ToString() ?? "");
                return result;
            }
            finally
            {
                active.Remove((name, directory));
            }
        }

        private async ValueTask<ResolvedModule?> ModuleAsync()
        {
            if (!Relative(name) && options.Paths is { } paths)
            {
                var mapped = Paths(extensions, name, options.PathsBasePath ?? resolver.cwd, paths,
                    candidate => RelativeLoad(extensions, candidate));
                if (mapped is not null)
                    return mapped;
            }
            if (Relative(name))
            {
                string candidate = CompilerPath.Resolve(directory, name);
                if (name.AsSpan(name.LastIndexOf('/') + 1) is "." or "..")
                    candidate = CompilerPath.EnsureTrailingSeparator(candidate);
                if (options.RootDirs is { Length: > 0 } roots)
                {
                    string? root = roots.Select(CompilerPath.EnsureTrailingSeparator)
                        .Where(r => candidate.StartsWith(r, StringComparison.Ordinal)).OrderByDescending(r => r.Length).FirstOrDefault();
                    if (root is not null)
                    {
                        var result = RelativeLoad(extensions, candidate);
                        if (result is not null)
                            return result;
                        foreach (string other in roots)
                            if (CompilerPath.EnsureTrailingSeparator(other) != root
                                && RelativeLoad(extensions, CompilerPath.Combine(other, candidate[root.Length..])) is { } redirected)
                                return redirected;
                    }
                }
                return RelativeLoad(extensions, candidate);
            }
            PackageJson? scope = null;
            if (imports && name.StartsWith('#'))
            {
                scope = Scope();
                if (name != "#" && !(name.StartsWith("#/", StringComparison.Ordinal) && resolver.ResolutionKind == "node16")
                    && scope?.Get("imports") is { ValueKind: JsonValueKind.Object } map)
                {
                    var result = await MapAsync(scope, map, name, extensions, true).ConfigureAwait(false);
                    if (result is not null)
                        return result;
                }
            }
            scope ??= Scope();
            if (scope?.Name is { } self
                && (name == self || name.StartsWith(self + "/", StringComparison.Ordinal))
                && Truthy(scope.Get("exports")))
            {
                string subpath = name == self ? "." : "." + name[self.Length..];
                bool allowJs = options.AllowJs ?? options.CheckJs == true;
                foreach (Extensions ext in allowJs && !directory.Contains("/node_modules/", StringComparison.Ordinal)
                    ? new[] { extensions } : new[]
                    {
                        extensions & (Extensions.TypeScript | Extensions.Declaration),
                        extensions & ~(Extensions.TypeScript | Extensions.Declaration)
                    })
                    if (await ExportsAsync(scope, subpath, ext).ConfigureAwait(false) is { } result)
                        return External(result);
            }
            if (name.Contains(':'))
                return null;
            if (await NearestAsync(extensions).ConfigureAwait(false) is { } found)
                return External(found);
            if ((extensions & Extensions.Declaration) != 0 && options.TypeRoots is { } typeRoots)
                return FromTypeRoots(typeRoots, true);
            return null;
        }

        private async ValueTask<ResolvedModule?> TypeReferenceAsync()
        {
            var primary = FromTypeRoots(resolver.TypeRoots(), options.TypeRoots is not null);
            if (primary is not null)
            {
                Primary = primary.IsResolved;
                return primary;
            }
            if (inferredTypes && options.TypeRoots is not null)
                return null;
            return Relative(name) ? RelativeLoad(Extensions.Declaration, CompilerPath.Resolve(directory, name))
                : await NearestAsync(Extensions.Declaration).ConfigureAwait(false);
        }

        private ResolvedModule? FromTypeRoots(string[] roots, bool fileLookup)
        {
            foreach (string root in roots)
            {
                if (!DirectoryExists(root))
                    continue;
                string candidate = CompilerPath.Combine(
                    root,
                    root.TrimEnd('/').EndsWith("/node_modules/@types", StringComparison.Ordinal) ? Mangle(name) : name);
                if (fileLookup && LoadFile(Extensions.Declaration, candidate) is { } file)
                    return WithPackage(file, PackageForFile(file.FileName));
                if (LoadDirectory(Extensions.Declaration, candidate, Package(candidate)) is { } found)
                    return found;
            }
            return null;
        }

        private async ValueTask<ResolvedModule?> NearestAsync(Extensions exts)
        {
            foreach (Extensions ext in new[]
            {
                exts & (Extensions.TypeScript | Extensions.Declaration),
                exts & ~(Extensions.TypeScript | Extensions.Declaration)
            })
                if (ext != 0)
                    foreach (string path in PackageJsonCache.Ancestors(directory))
                        if (CompilerPath.BaseName(path) != "node_modules"
                            && await ImmediateAsync(ext, path).ConfigureAwait(false) is { } result)
                            return result;
            return null;
        }

        private async ValueTask<ResolvedModule?> ImmediateAsync(Extensions ext, string directory)
        {
            string modules = CompilerPath.Combine(directory, "node_modules");
            if (!DirectoryExists(modules))
                return null;
            if (await NodeModulesAsync(ext, name, modules).ConfigureAwait(false) is { } result)
                return result;
            if ((ext & Extensions.Declaration) != 0 && DirectoryExists(CompilerPath.Combine(modules, "@types")))
                return await NodeModulesAsync(
                    Extensions.Declaration,
                    Mangle(name),
                    CompilerPath.Combine(modules, "@types")).ConfigureAwait(false);
            return null;
        }

        private async ValueTask<ResolvedModule?> NodeModulesAsync(Extensions ext, string module, string modules)
        {
            string candidate = CompilerPath.Resolve(modules, module).TrimEnd('/');
            (string package, string rest) = PackageName(module);
            string packageDirectory = package.Length == 0 ? candidate : CompilerPath.Combine(modules, package);
            var info = Package(candidate);
            PackageJson? root = null;
            if (rest.Length != 0 && info is not null)
            {
                if (exports)
                    root = Package(packageDirectory);
                if (root?.Get("exports").ValueKind is null or JsonValueKind.Undefined)
                {
                    if (LoadFile(ext, candidate) is { } file)
                        return file;
                    if (LoadDirectory(ext, candidate, info) is { } nested)
                        return WithPackage(nested, info);
                }
            }
            if (rest.Length != 0)
                info = root ?? Package(packageDirectory);
            ResolvedModule? Load(string target)
            {
                if ((rest.Length != 0 || !esm) && LoadFile(ext, target) is { } file)
                    return WithPackage(file, info);
                if (LoadDirectory(ext, target, info) is { } folder)
                    return WithPackage(folder, info);
                if (rest.Length == 0 && info is not null && info.Get("exports").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    && esm && LoadFile(ext, CompilerPath.Combine(target, "index.js")) is { } index)
                    return WithPackage(index, info);
                return null;
            }
            if (info is not null)
            {
                if (exports && Truthy(info.Get("exports")))
                    return await ExportsAsync(info, rest.Length == 0 ? "." : "./" + rest, ext).ConfigureAwait(false);
                if (rest.Length != 0 && info.VersionPaths(resolver.CompilerVersion) is { ValueKind: JsonValueKind.Object } paths
                    && CompilerOptions.ParsePaths(paths) is { } mappings
                    && Paths(ext, rest, packageDirectory, mappings, Load) is { } mapped)
                    return mapped;
            }
            return Load(candidate);
        }

        private static bool Truthy(JsonElement value) => value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null
            or JsonValueKind.False)
            && (value.ValueKind != JsonValueKind.String || JsonStrings.GetString(value).Length != 0)
            && (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double n) || n != 0);

        private static bool IsTypeScript(string ext) => ext is ".ts" or ".tsx" or ".mts" or ".cts" or ".d.ts" or ".d.mts" or ".d.cts";

        private static ResolvedModule External(ResolvedModule result) =>
            result with { External = result.FileName.Contains("/node_modules/", StringComparison.Ordinal) };
    }
}

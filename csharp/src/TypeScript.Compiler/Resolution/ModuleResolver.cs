using System.Runtime.CompilerServices;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Resolution;

public sealed record PackageId(Utf8String Name, Utf8String SubModuleName, Utf8String Version, Utf8String PeerDependencies = default)
{
    public Utf8String ToUtf8String() => Name + (SubModuleName.Length == 0 ? Utf8String.Empty : Utf8Literals.Slash + SubModuleName) + Utf8Literals.At + Version + PeerDependencies;
    public override string ToString() => ToUtf8String().ToString();
}

public readonly record struct ResolutionTrace(Utf8String Operation, Utf8String Path, Utf8String Detail = default);

public sealed record ResolvedModule(Utf8String FileName = default, Utf8String Extension = default, PackageId? PackageId = null,
    Utf8String OriginalPath = default, bool External = false, bool UsingTsExtension = false, bool UsingExtraExtension = false)
{
    public bool IsResolved => FileName.Length != 0;
    internal bool IsArbitraryExtension => IsResolved && !UsingExtraExtension
        && !(Extension == ".ts"u8 || Extension == ".tsx"u8 || Extension == ".d.ts"u8 || Extension == ".mts"u8 || Extension == ".d.mts"u8 || Extension == ".cts"u8 || Extension == ".d.cts"u8 || Extension == ".js"u8 || Extension == ".jsx"u8 || Extension == ".mjs"u8 || Extension == ".cjs"u8 || Extension == ".json"u8);
    public bool Primary { get; init; }
    public Utf8String AlternateResult { get; init; } = Utf8String.Empty;
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];
    public IReadOnlyList<ResolutionTrace> Trace { get; init; } = [];
    public IReadOnlyList<Utf8String> AffectingLocations { get; init; } = [];
}

/// <summary>A resolver owns its options and one generation of resolution/package caches.</summary>
public sealed partial class ModuleResolver
{
    private readonly IFileSystem fs;
    private readonly CompilerOptions options = new();
    private readonly Utf8String cwd, configFile, typingsLocation;
    private readonly Utf8String[] extraExtensions;
    private readonly object gate = new();
    private readonly Dictionary<(Utf8String Name, Utf8String Directory, ReferenceResolutionMode Mode, bool Types, bool Inferred), ResolvedModule> cache = [];
    private long generation;
    public PackageJsonCache Packages { get; }
    public SemanticVersion CompilerVersion { get; }
    public Utf8String ResolutionKind { get; }

    public ModuleResolver(IFileSystem fileSystem, CompilerOptions compilerOptions, Utf8String currentDirectory,
        Utf8String configFile = default, Utf8String typingsLocation = default, IEnumerable<Utf8String>? extraExtensions = null,
        SemanticVersion? compilerVersion = null)
    {
        fs = fileSystem;
        options.Merge(compilerOptions);
        cwd = CompilerPath.Resolve(Utf8Literals.Slash, currentDirectory);
        this.configFile = configFile;
        this.typingsLocation = typingsLocation;
        this.extraExtensions = extraExtensions?.OrderByDescending(e => e.Length).ToArray() ?? [];
        CompilerVersion = compilerVersion ?? new(7, 1, 0, Utf8Literals.Dev);
        Packages = new(fs, cwd);
        ResolutionKind = options.EmitModuleResolutionKind switch
        {
            ModuleResolutionKind.Node16 => Utf8Literals.Node16Option,
            ModuleResolutionKind.NodeNext => Utf8Literals.Nodenext,
            _ => Utf8Literals.Bundler
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

    public ResolvedModule Resolve(Utf8String name, Utf8String containingFile, ReferenceResolutionMode mode = default,
        bool typeReference = false, CancellationToken cancellation = default) =>
        Parser.RunParse(ResolveAsync(name, containingFile, mode, typeReference, cancellation));

    public async ValueTask<ResolvedModule> ResolveAsync(Utf8String name, Utf8String containingFile, ReferenceResolutionMode mode = default,
        bool typeReference = false, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Utf8String directory = CompilerPath.DirectoryName(CompilerPath.Resolve(cwd, containingFile));
        bool inferred = typeReference && containingFile.EndsWith("__inferred type names__.ts"u8, StringComparison.Ordinal);
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
            AffectingLocations = request.locations.Order(Utf8StringComparer.Ordinal).ToArray()
        };
        lock (gate)
            if (generation == version)
                cache[key] = result;
        return result;
    }

    public Utf8String[] TypeRoots() => options.TypeRoots ?? PackageJsonCache.Ancestors(
        configFile.Length == 0 ? cwd : CompilerPath.DirectoryName(configFile))
        .Select(d => CompilerPath.Combine(d, Utf8Literals.NodeModulesTypes)).ToArray();

    public Utf8String[] AutomaticTypeDirectives()
    {
        Utf8String[] types = options.Types ?? [];
        if (!types.Contains(Utf8Literals.Asterisk))
            return types;
        var matches = new List<Utf8String>();
        foreach (Utf8String root in TypeRoots())
            foreach (Utf8String name in fs.GetAccessibleEntries(root).Directories)
                if (!name.StartsWith((byte)'.')
                    && Packages.Get(CompilerPath.Combine(root, name)).Contents?.Get(Utf8Literals.Typings).ValueKind != JsonValueKind.Null)
                    matches.Add(name);
        return types.SelectMany(t => t == Utf8Literals.Asterisk ? matches : (IEnumerable<Utf8String>)[t]).Distinct(Utf8StringComparer.Ordinal).ToArray();
    }

    public Utf8String? ResolvePackageDirectory(Utf8String name, Utf8String containingFile) =>
        ResolvePackageDirectoryInfo(name, containingFile)?.FileName;

    internal ResolvedModule? ResolvePackageDirectoryInfo(Utf8String name, Utf8String containingFile)
    {
        (Utf8String package, _) = PackageName(name);
        foreach (Utf8String directory in PackageJsonCache.Ancestors(CompilerPath.DirectoryName(CompilerPath.Resolve(cwd, containingFile))))
        {
            if (CompilerPath.BaseName(directory) == Utf8Literals.NodeModules)
                continue;
            Utf8String path = CompilerPath.Combine(directory, Utf8Literals.NodeModules, package);
            if (fs.DirectoryExists(path))
                return Result(path);
            path = CompilerPath.Combine(directory, Utf8Literals.NodeModulesTypes, Mangle(package));
            if (fs.DirectoryExists(path))
                return Result(path);
        }
        return null;
        ResolvedModule Result(Utf8String path)
        {
            Utf8String real = options.PreserveSymlinks == true ? path : fs.RealPath(path);
            return new(real, OriginalPath: real == path ? Utf8String.Empty : path);
        }
    }

    public static (Utf8String Name, Utf8String Subpath) PackageName(Utf8String name)
    {
        int slash = name.IndexOf((byte)'/');
        if (name.StartsWith((byte)'@') && slash >= 0)
            slash = name.IndexOf((byte)'/', slash + 1);
        return slash < 0 ? (name, Utf8String.Empty) : (name[..slash], name[(slash + 1)..]);
    }

    public static ResolvedModule ResolveConfig(IFileSystem fileSystem, Utf8String currentDirectory, Utf8String name, Utf8String containingFile) =>
        Parser.RunParse(ResolveConfigAsync(fileSystem, currentDirectory, name, containingFile));

    public static async ValueTask<ResolvedModule> ResolveConfigAsync(IFileSystem fileSystem, Utf8String currentDirectory, Utf8String name,
        Utf8String containingFile, CancellationToken cancellation = default)
    {
        var options = new CompilerOptions();
        options.SetString(Utf8Literals.ModuleResolution, Utf8Literals.Nodenext);
        var resolver = new ModuleResolver(fileSystem, options, currentDirectory);
        var request = new Request(resolver, name, CompilerPath.DirectoryName(containingFile), ReferenceResolutionMode.Require,
            false, cancellation, configLookup: true);
        var result = await request.RunAsync().ConfigureAwait(false);
        return result with
        {
            Diagnostics = request.diagnostics.ToArray(),
            AffectingLocations = request.locations.Order(Utf8StringComparer.Ordinal).ToArray()
        };
    }

    public static Utf8String Mangle(Utf8String name)
    {
        int slash = name.IndexOf((byte)'/');
        return name.StartsWith((byte)'@') && slash >= 0 ? name[1..slash] + Utf8Literals.DoubleUnderscore + name[(slash + 1)..] : name;
    }

    public static bool Relative(Utf8String name) => name == "."u8 || name == ".."u8 || name.StartsWith("./"u8, StringComparison.Ordinal)
        || name.StartsWith("../"u8, StringComparison.Ordinal) || CompilerPath.IsAbsolute(name) && !CompilerPath.IsUrl(name);

    public static bool Relative(ReadOnlySpan<byte> name) => name.SequenceEqual("."u8) || name.SequenceEqual(".."u8) || name.StartsWith("./"u8, StringComparison.Ordinal)
        || name.StartsWith("../"u8, StringComparison.Ordinal) || CompilerPath.EncodedRootLength(name) > 0;

    public static Utf8String Extension(Utf8String path)
    {
        foreach (Utf8String ext in new Utf8String[] { Utf8Literals.DTs, Utf8Literals.DMts, Utf8Literals.DCts, Utf8Literals.Tsx, Utf8Literals.Ts, Utf8Literals.Jsx, Utf8Literals.Js, Utf8Literals.Json, Utf8Literals.Mts, Utf8Literals.Cts, Utf8Literals.Mjs, Utf8Literals.Cjs })
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
        private readonly Utf8String name, directory;
        private readonly ReferenceResolutionMode mode;
        private readonly bool types;
        private readonly CancellationToken cancellation;
        private readonly Utf8String[] conditions;
        private readonly HashSet<(Utf8String, Utf8String)> active;
        internal readonly List<ResolutionTrace> trace = [];
        internal readonly List<Diagnostic> diagnostics = [];
        internal readonly HashSet<Utf8String> locations = new(Utf8StringComparer.Ordinal);
        private Extensions extensions;
        private bool esm, exports, imports, fromConfig;
        private readonly bool configLookup;
        private readonly bool inferredTypes;
        internal bool Primary { get; private set; }

        internal Request(
            ModuleResolver resolver,
            Utf8String name,
            Utf8String directory,
            ReferenceResolutionMode mode,
            bool types,
            CancellationToken cancellation,
            HashSet<(Utf8String, Utf8String)>? active = null,
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
            if (!types && (options.ResolveJsonModule ?? resolver.ResolutionKind == Utf8Literals.Bundler
                || options.Module is ModuleKind.Node20 or ModuleKind.NodeNext))
                extensions |= Extensions.Json;
            if (configLookup)
                extensions = Extensions.Json;
            esm = resolver.ResolutionKind != Utf8Literals.Bundler && mode == ReferenceResolutionMode.Import;
            conditions = [mode == ReferenceResolutionMode.Import || mode == 0 && resolver.ResolutionKind == Utf8Literals.Bundler
                ? Utf8Literals.ImportKeyword
                : Utf8Literals.RequireKeyword,
                .. options.NoDtsResolution != true ? new Utf8String[]{ Utf8Literals.Types } : [],
                .. resolver.ResolutionKind == Utf8Literals.Bundler ? [] : new Utf8String[]{ Utf8Literals.Node }, .. options.CustomConditions ?? []];
            // The pinned Node16/Next resolver always enables maps; only Bundler consults these switches.
            exports = resolver.ResolutionKind != Utf8Literals.Bundler || options.ResolvePackageJsonExports != false;
            imports = resolver.ResolutionKind != Utf8Literals.Bundler || options.ResolvePackageJsonImports != false;
        }

        private void Trace(Utf8String operation, Utf8String path, Utf8String detail = default)
        {
            if (options.TraceResolution == true)
                trace.Add(new(operation, path, detail));
        }

        private bool DirectoryExists(Utf8String path)
        {
            cancellation.ThrowIfCancellationRequested();
            locations.Add(path);
            bool exists = fs.DirectoryExists(path);
            Trace(Utf8Literals.Directory, path, exists ? Utf8Literals.Exists : Utf8Literals.Missing);
            return exists;
        }

        private PackageJson? Package(Utf8String path)
        {
            locations.Add(CompilerPath.Combine(path, Utf8Literals.PackageJson));
            var entry = resolver.Packages.Get(path);
            Trace(Utf8Literals.Package, path, entry.Contents is null ? Utf8Literals.Missing : entry.Contents.Parseable ? Utf8Literals.Parsed : Utf8Literals.Invalid);
            return entry.Contents;
        }

        private PackageJson? Scope()
        {
            foreach (Utf8String path in PackageJsonCache.Ancestors(directory))
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
                Trace(Utf8Literals.Cycle, directory, name);
                return new();
            }
            try
            {
                Trace(Utf8Literals.Resolve, directory, name + Utf8Literals.Colon + Utf8String.Join((byte)',', conditions));
                var result = types ? await TypeReferenceAsync().ConfigureAwait(false)
                    : await ModuleAsync().ConfigureAwait(false);
                result ??= new();
                if (!types && result.IsResolved && result.External && conditions.Contains(Utf8Literals.ImportKeyword) && exports
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
                    && (!result.IsResolved || !IsTypeScript(result.Extension) && result.Extension != Utf8Literals.Json))
                {
                    var global = await ImmediateAsync(Extensions.Declaration, resolver.typingsLocation).ConfigureAwait(false);
                    if (global?.IsResolved == true)
                        result = global with { External = true };
                }
                if (result.IsResolved && options.PreserveSymlinks != true && (types || result.External && !Relative(name)))
                {
                    Utf8String real = CompilerPath.Normalize(fs.RealPath(result.FileName));
                    Trace(Utf8Literals.Realpath, result.FileName, real);
                    if (!real.Equals(result.FileName, fs.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                        result = result with { OriginalPath = result.FileName, FileName = real };
                }
                Trace(Utf8Literals.Result, result.FileName, result.PackageId?.ToUtf8String() ?? default);
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
                Utf8String candidate = CompilerPath.Resolve(directory, name);
                if (name.AsSpan(name.LastIndexOf((byte)'/') + 1) is var matchedText && (matchedText.SequenceEqual("."u8) || matchedText.SequenceEqual(".."u8)))
                    candidate = CompilerPath.EnsureTrailingSeparator(candidate);
                if (options.RootDirs is { Length: > 0 } roots)
                {
                    Utf8String root = roots.Select(CompilerPath.EnsureTrailingSeparator)
                        .Where(r => candidate.StartsWith(r, StringComparison.Ordinal)).OrderByDescending(r => r.Length).FirstOrDefault();
                    if (!root.IsEmpty)
                    {
                        var result = RelativeLoad(extensions, candidate);
                        if (result is not null)
                            return result;
                        foreach (Utf8String other in roots)
                            if (CompilerPath.EnsureTrailingSeparator(other) != root
                                && RelativeLoad(extensions, CompilerPath.Combine(other, candidate[root.Length..])) is { } redirected)
                                return redirected;
                    }
                }
                return RelativeLoad(extensions, candidate);
            }
            PackageJson? scope = null;
            if (imports && name.StartsWith((byte)'#'))
            {
                scope = Scope();
                if (name != Utf8Literals.Hash && !(name.StartsWith("#/"u8, StringComparison.Ordinal) && resolver.ResolutionKind == Utf8Literals.Node16Option)
                    && scope?.Get(Utf8Literals.Imports) is { ValueKind: JsonValueKind.Object } map)
                {
                    var result = await MapAsync(scope, map, name, extensions, true).ConfigureAwait(false);
                    if (result is not null)
                        return result;
                }
            }
            scope ??= Scope();
            if (scope?.Name is { } self
                && (name == self || name.StartsWith(self + "/"u8, StringComparison.Ordinal))
                && Truthy(scope.Get(Utf8Literals.Exports)))
            {
                Utf8String subpath = name == self ? Utf8Literals.Dot : Utf8Literals.Dot + name[self.Length..];
                bool allowJs = options.AllowJs ?? options.CheckJs == true;
                foreach (Extensions ext in allowJs && !directory.Contains("/node_modules/"u8, StringComparison.Ordinal)
                    ? new[] { extensions } : new[]
                    {
                        extensions & (Extensions.TypeScript | Extensions.Declaration),
                        extensions & ~(Extensions.TypeScript | Extensions.Declaration)
                    })
                    if (await ExportsAsync(scope, subpath, ext).ConfigureAwait(false) is { } result)
                        return External(result);
            }
            if (name.Contains((byte)':'))
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

        private ResolvedModule? FromTypeRoots(Utf8String[] roots, bool fileLookup)
        {
            foreach (Utf8String root in roots)
            {
                if (!DirectoryExists(root))
                    continue;
                Utf8String candidate = CompilerPath.Combine(
                    root,
                    root.TrimEnd((byte)'/').EndsWith("/node_modules/@types"u8, StringComparison.Ordinal) ? Mangle(name) : name);
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
                    foreach (Utf8String path in PackageJsonCache.Ancestors(directory))
                        if (CompilerPath.BaseName(path) != Utf8Literals.NodeModules
                            && await ImmediateAsync(ext, path).ConfigureAwait(false) is { } result)
                            return result;
            return null;
        }

        private async ValueTask<ResolvedModule?> ImmediateAsync(Extensions ext, Utf8String directory)
        {
            Utf8String modules = CompilerPath.Combine(directory, Utf8Literals.NodeModules);
            if (!DirectoryExists(modules))
                return null;
            if (await NodeModulesAsync(ext, name, modules).ConfigureAwait(false) is { } result)
                return result;
            if ((ext & Extensions.Declaration) != 0 && DirectoryExists(CompilerPath.Combine(modules, Utf8Literals.TypesScope)))
                return await NodeModulesAsync(
                    Extensions.Declaration,
                    Mangle(name),
                    CompilerPath.Combine(modules, Utf8Literals.TypesScope)).ConfigureAwait(false);
            return null;
        }

        private async ValueTask<ResolvedModule?> NodeModulesAsync(Extensions ext, Utf8String module, Utf8String modules)
        {
            Utf8String candidate = CompilerPath.Resolve(modules, module).TrimEnd((byte)'/');
            (Utf8String package, Utf8String rest) = PackageName(module);
            Utf8String packageDirectory = package.Length == 0 ? candidate : CompilerPath.Combine(modules, package);
            var info = Package(candidate);
            PackageJson? root = null;
            if (rest.Length != 0 && info is not null)
            {
                if (exports)
                    root = Package(packageDirectory);
                if (root?.Get(Utf8Literals.Exports).ValueKind is null or JsonValueKind.Undefined)
                {
                    if (LoadFile(ext, candidate) is { } file)
                        return file;
                    if (LoadDirectory(ext, candidate, info) is { } nested)
                        return WithPackage(nested, info);
                }
            }
            if (rest.Length != 0)
                info = root ?? Package(packageDirectory);
            ResolvedModule? Load(Utf8String target)
            {
                if ((rest.Length != 0 || !esm) && LoadFile(ext, target) is { } file)
                    return WithPackage(file, info);
                if (LoadDirectory(ext, target, info) is { } folder)
                    return WithPackage(folder, info);
                if (rest.Length == 0 && info is not null && info.Get(Utf8Literals.Exports).ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    && esm && LoadFile(ext, CompilerPath.Combine(target, Utf8Literals.IndexJs)) is { } index)
                    return WithPackage(index, info);
                return null;
            }
            if (info is not null)
            {
                if (exports && Truthy(info.Get(Utf8Literals.Exports)))
                    return await ExportsAsync(info, rest.Length == 0 ? Utf8Literals.Dot : Utf8Literals.CurrentDirectoryPrefix + rest, ext).ConfigureAwait(false);
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

        private static bool IsTypeScript(Utf8String ext) => ext == ".ts"u8 || ext == ".tsx"u8 || ext == ".mts"u8 || ext == ".cts"u8 || ext == ".d.ts"u8 || ext == ".d.mts"u8 || ext == ".d.cts"u8;

        private static ResolvedModule External(ResolvedModule result) =>
            result with { External = result.FileName.Contains("/node_modules/"u8, StringComparison.Ordinal) };
    }
}

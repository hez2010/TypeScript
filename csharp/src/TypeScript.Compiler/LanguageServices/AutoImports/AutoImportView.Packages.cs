using System.Text.Json;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Resolution;

namespace TypeScript.Compiler.LanguageServices;

internal sealed partial class AutoImportView
{
    private readonly Dictionary<Utf8String, List<ResolvedEntrypoint>> packageEntrypoints = [];
    private static readonly HashSet<Utf8String> recursivePackages =
    [
        "@material-ui/core"u8, "@material-ui/icons"u8, "@sap/cds"u8, "@testing-library/react-native"u8, "ajv"u8,
        "asap"u8, "async"u8, "aws-sdk"u8, "braintree-web"u8, "core-js"u8, "core-js-pure"u8, "crypto-js"u8,
        "cypress-mochawesome-reporter"u8, "dd-trace"u8, "dumi"u8, "dva"u8, "egg-mock"u8, "electron-log"u8,
        "es-abstract"u8, "es6-promise"u8, "eslint-config-taro"u8, "expo"u8, "expo-router"u8, "flow-remove-types"u8,
        "gatsby"u8, "glamor"u8, "gluegun"u8, "graphology-indices"u8, "graphology-traversal"u8, "graphology-utils"u8,
        "jest-expo"u8, "lodash"u8, "lodash-es"u8, "moment"u8, "mz"u8, "next"u8, "pdfjs-dist"u8, "protobufjs"u8,
        "react-app-polyfill"u8, "react-dev-utils"u8, "react-devtools-inline"u8, "recast"u8, "semver"u8,
        "stylelint-config-html"u8, "umi"u8, "web3-provider-engine"u8, "webpack"u8
    ];

    private async ValueTask<IReadOnlyList<Utf8String>> AddPackagesAsync(AutoImportIndex index)
    {
        var fs = FileSystem;
        var resolver = new ModuleResolver(fs, new(), program.CurrentDirectory);
        HashSet<Utf8String>? allowed = null;
        var directories = PackageJsonCache.Ancestors(CompilerPath.DirectoryName(File.FileName)).ToArray();
        foreach (var directory in directories)
            if (resolver.Packages.Get(directory).Contents is { Parseable: true } package)
            {
                allowed ??= [];
                foreach (var (name, _, field) in package.Dependencies())
                    if ((field == "dependencies"u8 || field == "peerDependencies"u8) && !name.IsEmpty && name != "@types/"u8 && !name.StartsWith((byte)'.'))
                        allowed.Add(UnmangleTypes(name));
            }
        var (resolvedPackages, deepImports) = DirectPackages(resolver);
        allowed?.UnionWith(resolvedPackages);
        HashSet<Utf8String> shadowed = [], extracted = [];
        List<Utf8String> packageRoots = [];
        Dictionary<Utf8String, List<Utf8String>> ambientFiles = [];
        List<(Utf8String Name, Utf8String[] Roots, IReadOnlyList<ResolvedEntrypoint> Entries, AutoImportExport[] Exports, Utf8String[] Unresolved)> extractions = [];
        foreach (var directory in directories)
        {
            cancellation.ThrowIfCancellationRequested();
            var modules = CompilerPath.Combine(directory, "node_modules"u8);
            var names = PackageNames(modules);
            foreach (var name in allowed ?? names)
            {
                if (shadowed.Contains(name)) continue;
                var package = resolver.Packages.Get(CompilerPath.Combine(modules, name));
                bool search = preferences.AutoImportEntrypointDirectorySearch == true || deepImports.Contains(name) || recursivePackages.Contains(name);
                var entries = GetEntries(package, search);
                if (entries.Count == 0 && !name.StartsWith("@types/"u8))
                {
                    var mangled = name.StartsWith((byte)'@') ? name[1..].Replace("/"u8, "__"u8) : name;
                    package = resolver.Packages.Get(CompilerPath.Combine(modules, "@types"u8, mangled));
                    entries = GetEntries(package, true);
                }
                if (entries.Count == 0) continue;
                var realDirectory = fs.RealPath(package.Directory);
                packageRoots.Add(realDirectory);
                if (!extracted.Add(Canonical(realDirectory))) continue;
                var roots = entries.Select(entry => cache?.ReferenceSources.GetValueOrDefault(Canonical(entry.ResolvedFileName)) is { IsEmpty: false } source ? source
                    : program.ProjectReferences.Outputs.TryGetValue(Canonical(entry.ResolvedFileName), out var output) ? output.Source : entry.SymlinkOrRealPath).Distinct().ToArray();
                var extraction = await Extract(name, roots, []);
                extractions.Add((name, roots, entries, extraction.Exports, extraction.Unresolved));

                IReadOnlyList<ResolvedEntrypoint> GetEntries(PackageJsonEntry info, bool recursive) => !info.DirectoryExists ? []
                    : resolver.GetEntrypoints(info, name, recursive, cancellation)
                        .Select(entry => entry with { ResolvedFileName = PackageRealPath(entry.SymlinkOrRealPath) })
                        .Where(entry => !ExcludedFile(entry.ResolvedFileName)).ToArray();
            }
            shadowed.UnionWith(names);
        }
        foreach (var extraction in extractions)
        {
            var additional = extraction.Unresolved.SelectMany(name => ambientFiles.GetValueOrDefault(name) ?? []).Except(extraction.Roots).Distinct().ToArray();
            var exports = additional.Length == 0 ? extraction.Exports : (await Extract(extraction.Name, extraction.Roots, additional)).Exports;
            foreach (var export in exports) index.Add(export);
            foreach (var entry in extraction.Entries)
            {
                var path = Canonical(entry.ResolvedFileName);
                if (!exports.Any(export => export.Path == path && CompilerPath.IsAbsolute(export.Id.Module))) continue;
                if (!packageEntrypoints.TryGetValue(path, out var entrypoints)) packageEntrypoints[path] = entrypoints = [];
                entrypoints.Add(entry);
            }
        }
        return packageRoots;

        async ValueTask<(AutoImportExport[] Exports, Utf8String[] Unresolved)> Extract(Utf8String name, Utf8String[] roots, Utf8String[] additional)
        {
            var options = new CompilerOptions();
            options.SetRaw("noLib"u8, "true"u8);
            options.SetRaw("noCheck"u8, "true"u8);
            options.SetRaw("types"u8, "[]"u8);
            var packageProgram = await CompilerProgram.CreateAsync(fs, program.CurrentDirectory, new(default, options, [.. roots, .. additional], [], [], []),
                cancellation: cancellation);
            var packageChecker = await packageProgram.CreateCheckerAsync(cancellation);
            var extractor = new AutoImportView(packageProgram, packageChecker, projection, preferences, cancellation, name);
            var packageIndex = new AutoImportIndex();
            foreach (var root in roots)
                if (packageProgram.GetFile(root) is { } file)
                {
                    await extractor.ExtractFileAsync(file, packageIndex);
                    foreach (var module in file.Syntax.AmbientModuleNames)
                    {
                        if (!ambientFiles.TryGetValue(module, out var declarations)) ambientFiles[module] = declarations = [];
                        if (!declarations.Contains(root)) declarations.Add(root);
                    }
                }
            var unresolved = packageProgram.SourceFiles.SelectMany(file => file.Resolutions)
                .Where(import => !import.Resolution.IsResolved && !ImportSorter.Relative(import.Specifier) && !CompilerPath.IsAbsolute(import.Specifier))
                .Select(import => import.Specifier).Distinct().ToArray();
            return (packageIndex.Search(default).ToArray(), unresolved);
        }

        HashSet<Utf8String> PackageNames(Utf8String directory)
        {
            HashSet<Utf8String> names = [];
            foreach (var name in fs.GetAccessibleEntries(directory).Directories)
                if (!name.IsEmpty && !name.StartsWith((byte)'.'))
                {
                    if (!name.StartsWith((byte)'@')) names.Add(name);
                    else foreach (var scoped in fs.GetAccessibleEntries(CompilerPath.Combine(directory, name)).Directories)
                        names.Add(UnmangleTypes(CompilerPath.Combine(name, CompilerPath.BaseName(scoped))));
                }
            return names;
        }
    }

    private (HashSet<Utf8String> Resolved, HashSet<Utf8String> Deep) DirectPackages(ModuleResolver resolver)
    {
        HashSet<Utf8String> resolved = [], deep = [];
        foreach (var entry in program.SourceFiles)
        {
            if (entry.Library || program.IsFromExternalLibrary(entry.Syntax) || entry.Syntax.FileName.Contains("/node_modules/"u8)) continue;
            foreach (var import in entry.Resolutions)
            {
                if (import.TypeReference || import.Augmentation || ImportSorter.Relative(import.Specifier) || CompilerPath.IsAbsolute(import.Specifier)) continue;
                if (!import.Resolution.IsResolved)
                {
                    if (checker.Symbols.Globals.GetValueOrDefault("\""u8 + import.Specifier + "\""u8) is { } ambient
                        && SemanticSyntax.Source(ambient.Declarations.FirstOrDefault()) is { } source)
                    {
                        var ambientPackage = PackageFromPath(source.FileName);
                        if (!ambientPackage.IsEmpty) resolved.Add(UnmangleTypes(ambientPackage));
                    }
                    continue;
                }
                if (!import.Resolution.External) continue;
                var package = resolver.Packages.Scope(CompilerPath.DirectoryName(import.Resolution.FileName));
                var name = import.Resolution.PackageId?.Name ?? package?.Name ?? default;
                if (name.IsEmpty) name = PackageFromPath(import.Resolution.FileName);
                if (name.IsEmpty) continue;
                resolved.Add(UnmangleTypes(name));
                if (!ModuleResolver.PackageName(import.Specifier).Subpath.IsEmpty && package is not null
                    && package.Get("exports"u8).ValueKind == JsonValueKind.Undefined) deep.Add(UnmangleTypes(name));
            }
        }
        foreach (var name in Options.Types ?? []) if (name != "*"u8) resolved.Add(UnmangleTypes(name));
        return (resolved, deep);
    }

    private static Utf8String PackageFromPath(Utf8String path)
    {
        int index = path.LastIndexOf("/node_modules/"u8);
        return index < 0 || index + 14 == path.Length || path[index + 14] == '.' ? default : ModuleResolver.PackageName(path[(index + 14)..]).Name;
    }

    private Utf8String PackageRealPath(Utf8String path)
    {
        var name = PackageFromPath(path);
        if (name.IsEmpty) return path;
        int end = path.LastIndexOf("/node_modules/"u8) + 14 + name.Length;
        return FileSystem.RealPath(path[..end]) + path[end..];
    }

    private static Utf8String UnmangleTypes(Utf8String name)
    {
        if (!name.StartsWith("@types/"u8)) return name;
        name = name[7..];
        int scope = name.IndexOf("__"u8);
        return scope < 0 ? name : "@"u8 + name[..scope] + "/"u8 + name[(scope + 2)..];
    }
}

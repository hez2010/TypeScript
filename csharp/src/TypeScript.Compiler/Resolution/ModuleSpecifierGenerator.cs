using TypeScript.Compiler.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Resolution;

internal readonly record struct ModuleSpecifierPath(string FileName, bool IsInNodeModules, bool IsRedirect);
internal enum ModuleSpecifierKind
{
    None,
    NodeModules,
    Paths,
    Redirect,
    Relative,
    Ambient
}
internal readonly record struct ModuleSpecifierResult(IReadOnlyList<string> Specifiers, ModuleSpecifierKind Kind);
internal sealed record ModuleSpecifierPreferences(string Relative = "shortest", string Ending = "", Func<string, bool>? Excluded = null);

internal interface IModuleSpecifierHost
{
    IFileSystem FileSystem { get; }
    string CurrentDirectory { get; }
    string ConfigFileName { get; }
    string CommonSourceDirectory { get; }
    string GlobalTypingsCache { get; }
    IReadOnlyList<string> ContentMapperExtensions { get; }

    string OriginalSourceFileName(SourceFileNode source);

    string ProjectOutput(string sourceFileName);

    IReadOnlyList<string> RedirectTargets(string target);

    IReadOnlyList<string> SymlinkDirectories(string realDirectory);

    ResolvedModule? ResolvedImport(SourceFileNode source, SyntaxNode import);

    ReferenceResolutionMode ResolutionMode(SourceFileNode source, SyntaxNode? import);
}

internal sealed partial class ModuleSpecifierGenerator(IModuleSpecifierHost host, CompilerOptions options)
{
    private readonly ModuleSpecifierPackages packages = new(host.FileSystem, options, host.CurrentDirectory,
        host.CommonSourceDirectory, host.ContentMapperExtensions);
    private readonly PackageJsonCache packageJson = new(host.FileSystem, host.CurrentDirectory);

    internal ModuleSpecifierResult ForFile(SourceFileNode source, string target, ModuleSpecifierPreferences? preferences = null,
        ReferenceResolutionMode overrideMode = 0, bool forAutoImport = false, CancellationToken cancellation = default) =>
        Select(
            AllPaths(host.OriginalSourceFileName(source), target, cancellation),
            source,
            preferences,
            overrideMode,
            forAutoImport,
            cancellation);

    internal ModuleSpecifierResult Select(IReadOnlyList<ModuleSpecifierPath> paths, SourceFileNode source,
        ModuleSpecifierPreferences? preferences = null, ReferenceResolutionMode overrideMode = 0, bool forAutoImport = false,
        CancellationToken cancellation = default)
    {
        preferences ??= new();
        cancellation.ThrowIfCancellationRequested();
        var defaultMode = host.ResolutionMode(source, null);
        var mode = overrideMode == 0 ? defaultMode : overrideMode;
        foreach (var path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            SyntaxNode? existing = null;
            foreach (var import in source.Imports)
                if (host.ResolvedImport(source, import) is { IsResolved: true } resolved && SamePath(resolved.FileName, path.FileName))
                {
                    existing = import;
                    break;
                }
            if (existing is null)
                continue;
            TextSlice text = existing is StringLiteralNode literal ? literal.Text : ((NoSubstitutionTemplateLiteralNode)existing).Text;
            if (preferences.Relative == "non-relative" && Relative(text.ToString()))
                continue;
            var existingMode = host.ResolutionMode(source, existing);
            if (existingMode != mode && existingMode != 0 && mode != 0)
                continue;
            if (text.Length != 0)
                return new(Array.AsReadOnly(new[] { text.ToString() }), ModuleSpecifierKind.None);
            break;
        }

        bool hasNodeModulesPath = paths.Any(p => p.IsInNodeModules);
        List<string> mapped = [], redirected = [], nodeModules = [], relative = [];
        foreach (var path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            string named = path.IsInNodeModules
                ? packages.FromNodeModules(path.FileName, source, defaultMode, overrideMode, preferences.Ending,
                    isRedirect: path.IsRedirect, globalTypingsCache: host.GlobalTypingsCache, cancellation: cancellation) : "";
            if (named.Length != 0 && !(forAutoImport && Excluded(named, preferences)))
            {
                nodeModules.Add(named);
                if (path.IsRedirect)
                    return Result(nodeModules, ModuleSpecifierKind.NodeModules);
            }
            string local = Local(path.FileName, source, preferences, mode, path.IsRedirect || named.Length != 0, cancellation);
            if (local.Length == 0 || forAutoImport && Excluded(local, preferences))
                continue;
            if (path.IsRedirect)
                redirected.Add(local);
            else if (!CompilerPath.IsAbsolute(local) && !Relative(local))
                (InNodeModules(local) ? relative : mapped).Add(local);
            else if (forAutoImport || !hasNodeModulesPath || path.IsInNodeModules)
                relative.Add(local);
        }
        return mapped.Count != 0 ? Result(mapped, ModuleSpecifierKind.Paths)
            : redirected.Count != 0 ? Result(redirected, ModuleSpecifierKind.Redirect)
            : nodeModules.Count != 0 ? Result(nodeModules, ModuleSpecifierKind.NodeModules)
            : Result(relative, ModuleSpecifierKind.Relative);
    }

    internal string Local(string target, SourceFileNode source, ModuleSpecifierPreferences preferences,
        ReferenceResolutionMode mode, bool pathsOnly = false, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        JsonElement? paths = options.Get("paths") is { ValueKind: JsonValueKind.Object } pathsObject ? pathsObject : null;
        if (pathsOnly && paths is null)
            return "";
        string directory = CompilerPath.DirectoryName(source.FileName);
        var endings = ModuleSpecifierPaths.AllowedEndings(
            options,
            source,
            host.ResolutionMode(source, null),
            mode,
            preferences.Ending,
            cancellation: cancellation);
        var roots = options.Strings("rootDirs");
        string relative = roots is { Length: > 0 }
            ? ModuleSpecifierPaths.FromRootDirectories(
                roots,
                target,
                directory,
                endings,
                options,
                host.FileSystem,
                host.CurrentDirectory,
                cancellation) : "";
        if (relative.Length == 0)
            relative = ModuleSpecifierPaths.ProcessEnding(ModuleSpecifierPaths.NonModulePath(CompilerPath.Relative(
                CompilerPath.Resolve(host.CurrentDirectory, directory),
                CompilerPath.Resolve(host.CurrentDirectory, target),
                host.FileSystem.CaseSensitive)),
                endings, options, host.FileSystem, host.CurrentDirectory, cancellation);
        if (paths is null && options.Boolean("resolvePackageJsonImports") == false || preferences.Relative == "relative")
            return pathsOnly ? "" : relative;

        string baseDirectory = CompilerPath.Resolve(host.CurrentDirectory,
            paths is { } pathMap && pathMap.EnumerateObject().Any() ? options.String("pathsBasePath") ?? "" : "");
        string relativeToBase = ModuleSpecifierPaths.RelativeIfSameVolume(target, baseDirectory, host.FileSystem.CaseSensitive);
        if (relativeToBase.Length == 0)
            return pathsOnly ? "" : relative;
        int ts = Array.IndexOf(endings, ModuleSpecifierEnding.TypeScript), js = Array.IndexOf(endings, ModuleSpecifierEnding.JavaScript);
        string imports = pathsOnly ? "" : packages.FromImports(target, directory, mode, ts >= 0 && ts < js, cancellation);
        string mapped = "";
        if ((pathsOnly || imports.Length == 0) && paths is { } mappings)
            mapped = ModuleSpecifierPaths.FromPaths(
                relativeToBase,
                mappings,
                endings,
                baseDirectory,
                options,
                host.FileSystem,
                host.CurrentDirectory,
                cancellation);
        if (pathsOnly)
            return mapped;
        string nonRelative = imports.Length != 0 ? imports : mapped;
        if (nonRelative.Length == 0)
            return relative;
        bool relativeExcluded = Excluded(relative, preferences), nonRelativeExcluded = Excluded(nonRelative, preferences);
        if (relativeExcluded != nonRelativeExcluded)
            return relativeExcluded ? nonRelative : relative;
        if (preferences.Relative == "non-relative" && !Relative(nonRelative))
            return nonRelative;
        if (preferences.Relative == "project-relative" && !Relative(nonRelative))
        {
            string projectDirectory = host.ConfigFileName.Length == 0 ? host.CurrentDirectory
                : CompilerPath.Resolve(host.CurrentDirectory, CompilerPath.DirectoryName(host.ConfigFileName));
            string sourceDirectory = CompilerPath.Resolve(host.CurrentDirectory, directory);
            string modulePath = CompilerPath.Resolve(projectDirectory, target);
            if (CompilerPath.Contains(projectDirectory, sourceDirectory, host.FileSystem.CaseSensitive)
                != CompilerPath.Contains(projectDirectory, modulePath, host.FileSystem.CaseSensitive))
                return nonRelative;
            string sourcePackage = NearestPackage(
                directory,
                cancellation), targetPackage = NearestPackage(CompilerPath.DirectoryName(modulePath), cancellation);
            if (sourcePackage.Length == 0
                ? targetPackage.Length != 0
                : targetPackage.Length == 0 || !SamePath(sourcePackage, targetPackage))
                return nonRelative;
            return relative;
        }
        return nonRelative.StartsWith("../", StringComparison.Ordinal)
            || nonRelative == ".."
            || Components(relative) < Components(nonRelative)
            ? relative : nonRelative;
    }

    private string NearestPackage(string directory, CancellationToken cancellation)
    {
        foreach (string candidate in PackageJsonCache.Ancestors(CompilerPath.Resolve(host.CurrentDirectory, directory)))
        {
            cancellation.ThrowIfCancellationRequested();
            if (packageJson.Get(candidate).Contents is not null)
                return candidate;
        }
        return "";
    }

    private bool SamePath(string left, string right)
    {
        left = CompilerPath.Resolve(host.CurrentDirectory, left);
        right = CompilerPath.Resolve(host.CurrentDirectory, right);
        return host.FileSystem.CaseSensitive ? left == right : Lower(left, fileName: true) == Lower(right, fileName: true);
    }

    private static int Components(string path) => path.AsSpan(path.StartsWith("./", StringComparison.Ordinal) ? 2 : 0).Count('/');

    private static bool Relative(string path) =>
        path is "." or ".." || path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith("../", StringComparison.Ordinal);

    private static bool InNodeModules(string path) => path.Contains("/node_modules/", StringComparison.Ordinal);

    private static bool Excluded(string specifier, ModuleSpecifierPreferences preferences) =>
        preferences.Excluded?.Invoke(specifier) == true;

    private static ModuleSpecifierResult Result(List<string> specifiers, ModuleSpecifierKind kind) =>
        new(Array.AsReadOnly(specifiers.ToArray()), kind);
}

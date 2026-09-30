using TypeScript.Compiler.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Resolution;

internal readonly record struct ModuleSpecifierPath(Utf8String FileName, bool IsInNodeModules, bool IsRedirect);
internal enum ModuleSpecifierKind
{
    None,
    NodeModules,
    Paths,
    Redirect,
    Relative,
    Ambient
}
internal readonly record struct ModuleSpecifierResult(IReadOnlyList<Utf8String> Specifiers, ModuleSpecifierKind Kind);
internal sealed record ModuleSpecifierPreferences(Utf8String Relative = default, Utf8String Ending = default, Func<Utf8String, bool>? Excluded = null);

internal interface IModuleSpecifierHost
{
    IFileSystem FileSystem { get; }
    Utf8String CurrentDirectory { get; }
    Utf8String ConfigFileName { get; }
    Utf8String CommonSourceDirectory { get; }
    Utf8String GlobalTypingsCache { get; }
    IReadOnlyList<Utf8String> ContentMapperExtensions { get; }

    Utf8String OriginalSourceFileName(SourceFileNode source);

    Utf8String ProjectOutput(Utf8String sourceFileName);

    IReadOnlyList<Utf8String> RedirectTargets(Utf8String target);

    IReadOnlyList<Utf8String> SymlinkDirectories(Utf8String realDirectory);

    ResolvedModule? ResolvedImport(SourceFileNode source, SyntaxNode import);

    ReferenceResolutionMode ResolutionMode(SourceFileNode source, SyntaxNode? import);
}

internal sealed partial class ModuleSpecifierGenerator(IModuleSpecifierHost host, CompilerOptions options)
{
    private readonly ModuleSpecifierPackages packages = new(host.FileSystem, options, host.CurrentDirectory,
        host.CommonSourceDirectory, host.ContentMapperExtensions);
    private readonly PackageJsonCache packageJson = new(host.FileSystem, host.CurrentDirectory);

    internal ModuleSpecifierResult ForFile(SourceFileNode source, Utf8String target, ModuleSpecifierPreferences? preferences = null,
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
            Utf8String text = existing is StringLiteralNode literal ? literal.Text : ((NoSubstitutionTemplateLiteralNode)existing).Text;
            if (preferences.Relative == Utf8Literals.NonRelative && Relative(text))
                continue;
            var existingMode = host.ResolutionMode(source, existing);
            if (existingMode != mode && existingMode != 0 && mode != 0)
                continue;
            if (text.Length != 0)
                return new(Array.AsReadOnly(new[] { text }), ModuleSpecifierKind.None);
            break;
        }

        bool hasNodeModulesPath = paths.Any(p => p.IsInNodeModules);
        List<Utf8String> mapped = [], redirected = [], nodeModules = [], relative = [];
        foreach (var path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            Utf8String named = path.IsInNodeModules
                ? packages.FromNodeModules(path.FileName, source, defaultMode, overrideMode, preferences.Ending,
                    isRedirect: path.IsRedirect, globalTypingsCache: host.GlobalTypingsCache, cancellation: cancellation) : Utf8String.Empty;
            if (named.Length != 0 && !(forAutoImport && Excluded(named, preferences)))
            {
                nodeModules.Add(named);
                if (path.IsRedirect)
                    return Result(nodeModules, ModuleSpecifierKind.NodeModules);
            }
            Utf8String local = Local(path.FileName, source, preferences, mode, path.IsRedirect || named.Length != 0, cancellation);
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

    internal Utf8String Local(Utf8String target, SourceFileNode source, ModuleSpecifierPreferences preferences,
        ReferenceResolutionMode mode, bool pathsOnly = false, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var paths = options.Paths;
        if (pathsOnly && paths is null)
            return Utf8String.Empty;
        Utf8String directory = CompilerPath.DirectoryName(source.FileName);
        var endings = ModuleSpecifierPaths.AllowedEndings(
            options,
            source,
            host.ResolutionMode(source, null),
            mode,
            preferences.Ending,
            cancellation: cancellation);
        var roots = options.RootDirs;
        Utf8String relative = roots is { Length: > 0 }
            ? ModuleSpecifierPaths.FromRootDirectories(
                roots,
                target,
                directory,
                endings,
                options,
                host.FileSystem,
                host.CurrentDirectory,
                cancellation) : Utf8String.Empty;
        if (relative.Length == 0)
            relative = ModuleSpecifierPaths.ProcessEnding(ModuleSpecifierPaths.NonModulePath(CompilerPath.Relative(
                CompilerPath.Resolve(host.CurrentDirectory, directory),
                CompilerPath.Resolve(host.CurrentDirectory, target),
                host.FileSystem.CaseSensitive)),
                endings, options, host.FileSystem, host.CurrentDirectory, cancellation);
        if (paths is null && options.ResolvePackageJsonImports == false || preferences.Relative == Utf8Literals.Relative)
            return pathsOnly ? Utf8String.Empty : relative;

        Utf8String baseDirectory = CompilerPath.Resolve(host.CurrentDirectory,
            paths is { Count: > 0 } ? options.PathsBasePath ?? Utf8String.Empty : Utf8String.Empty);
        Utf8String relativeToBase = ModuleSpecifierPaths.RelativeIfSameVolume(target, baseDirectory, host.FileSystem.CaseSensitive);
        if (relativeToBase.Length == 0)
            return pathsOnly ? Utf8String.Empty : relative;
        int ts = Array.IndexOf(endings, ModuleSpecifierEnding.TypeScript), js = Array.IndexOf(endings, ModuleSpecifierEnding.JavaScript);
        Utf8String imports = pathsOnly ? Utf8String.Empty : packages.FromImports(target, directory, mode, ts >= 0 && ts < js, cancellation);
        Utf8String mapped = default;
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
        Utf8String nonRelative = imports.Length != 0 ? imports : mapped;
        if (nonRelative.Length == 0)
            return relative;
        bool relativeExcluded = Excluded(relative, preferences), nonRelativeExcluded = Excluded(nonRelative, preferences);
        if (relativeExcluded != nonRelativeExcluded)
            return relativeExcluded ? nonRelative : relative;
        if (preferences.Relative == Utf8Literals.NonRelative && !Relative(nonRelative))
            return nonRelative;
        if (preferences.Relative == Utf8Literals.ProjectRelative && !Relative(nonRelative))
        {
            Utf8String projectDirectory = host.ConfigFileName.Length == 0 ? host.CurrentDirectory
                : CompilerPath.Resolve(host.CurrentDirectory, CompilerPath.DirectoryName(host.ConfigFileName));
            Utf8String sourceDirectory = CompilerPath.Resolve(host.CurrentDirectory, directory);
            Utf8String modulePath = CompilerPath.Resolve(projectDirectory, target);
            if (CompilerPath.Contains(projectDirectory, sourceDirectory, host.FileSystem.CaseSensitive)
                != CompilerPath.Contains(projectDirectory, modulePath, host.FileSystem.CaseSensitive))
                return nonRelative;
            Utf8String sourcePackage = NearestPackage(
                directory,
                cancellation), targetPackage = NearestPackage(CompilerPath.DirectoryName(modulePath), cancellation);
            if (sourcePackage.Length == 0
                ? targetPackage.Length != 0
                : targetPackage.Length == 0 || !SamePath(sourcePackage, targetPackage))
                return nonRelative;
            return relative;
        }
        return nonRelative.StartsWith("../"u8, StringComparison.Ordinal)
            || nonRelative == Utf8Literals.ParentDirectory
            || Components(relative) < Components(nonRelative)
            ? relative : nonRelative;
    }

    private Utf8String NearestPackage(Utf8String directory, CancellationToken cancellation)
    {
        foreach (Utf8String candidate in PackageJsonCache.Ancestors(CompilerPath.Resolve(host.CurrentDirectory, directory)))
        {
            cancellation.ThrowIfCancellationRequested();
            if (packageJson.Get(candidate).Contents is not null)
                return candidate;
        }
        return Utf8String.Empty;
    }

    private bool SamePath(Utf8String left, Utf8String right)
    {
        left = CompilerPath.Resolve(host.CurrentDirectory, left);
        right = CompilerPath.Resolve(host.CurrentDirectory, right);
        return host.FileSystem.CaseSensitive ? left == right : Lower(left, fileName: true) == Lower(right, fileName: true);
    }

    private static int Components(Utf8String path) => path.AsSpan(path.StartsWith("./"u8, StringComparison.Ordinal) ? 2 : 0).Count((byte)'/');

    private static bool Relative(Utf8String path) =>
        path == "."u8 || path == ".."u8 || path.StartsWith("./"u8, StringComparison.Ordinal) || path.StartsWith("../"u8, StringComparison.Ordinal);

    private static bool InNodeModules(Utf8String path) => path.Contains("/node_modules/"u8, StringComparison.Ordinal);

    private static bool Excluded(Utf8String specifier, ModuleSpecifierPreferences preferences) =>
        preferences.Excluded?.Invoke(specifier) == true;

    private static ModuleSpecifierResult Result(List<Utf8String> specifiers, ModuleSpecifierKind kind) =>
        new(Array.AsReadOnly(specifiers.ToArray()), kind);
}

using System.Text.Json;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Incremental;
using TypeScript.Compiler.Resolution;

namespace TypeScript.Compiler.Projects.TypeAcquisition;

public sealed record TypeAcquisitionOptions(bool? Enable = null, Utf8String[]? Include = null,
    Utf8String[]? Exclude = null, bool? DisableFilenameBasedTypeAcquisition = null);
public sealed record CachedTyping(Utf8String FileName, SemanticVersion Version);
public sealed record TypingsDiscoveryResult(Utf8String[] CachedFiles, Utf8String[] NewNames, Utf8String[] FilesToWatch);
public enum PackageNameValidationResult { Ok, Empty, TooLong, StartsWithDot, StartsWithUnderscore, NonUriSafeCharacters }
public readonly record struct PackageNameValidation(PackageNameValidationResult Result, Utf8String Name = default, bool IsScope = false);

public static class TypingsDiscovery
{
    internal static readonly Utf8String VersionTag = "ts"u8 + Utf8String.Join("."u8, BuildInfo.CompilerVersion.Split((byte)'.').Take(2));

    public static PackageNameValidation ValidatePackageName(Utf8String name) => Validate(name, true);

    private static PackageNameValidation Validate(Utf8String name, bool scoped)
    {
        if (name.IsEmpty) return new(PackageNameValidationResult.Empty);
        if (name.Length > 214) return new(PackageNameValidationResult.TooLong);
        if (name[0] == '.') return new(PackageNameValidationResult.StartsWithDot);
        if (name[0] == '_') return new(PackageNameValidationResult.StartsWithUnderscore);
        if (scoped && name[0] == '@' && name.IndexOf((byte)'/') is > 1 and var separator && separator < name.Length - 1
            && !name[(separator + 1)..].Contains((byte)'/'))
        {
            var scope = name[1..separator]; var package = name[(separator + 1)..];
            var scopeResult = Validate(scope, false);
            if (scopeResult.Result != PackageNameValidationResult.Ok) return scopeResult with { Name = scope, IsScope = true };
            var packageResult = Validate(package, false);
            return packageResult.Result == PackageNameValidationResult.Ok ? packageResult : packageResult with { Name = package };
        }
        foreach (byte value in name.Span)
            if (value is not (>= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~'))
                return new(PackageNameValidationResult.NonUriSafeCharacters);
        return new(PackageNameValidationResult.Ok);
    }

    public static Utf8String RemoveMinAndVersionNumbers(Utf8String name)
    {
        int end = name.Length, position = end;
        while (position > 0)
        {
            byte value = name[position - 1];
            if (value is >= (byte)'0' and <= (byte)'9')
                do { position--; } while (position > 0 && name[position - 1] is >= (byte)'0' and <= (byte)'9');
            else if (position > 4 && name[(position - 3)..position].Equals("min"u8, StringComparison.OrdinalIgnoreCase)) position -= 3;
            else break;
            if (position == 0 || name[position - 1] is not ((byte)'-' or (byte)'.')) break;
            end = --position;
        }
        return name[..end];
    }

    internal static bool IsCurrent(CachedTyping typing, IReadOnlyDictionary<Utf8String, Utf8String> versions)
    {
        var version = versions.GetValueOrDefault(VersionTag, versions.GetValueOrDefault("latest"u8));
        return (SemanticVersion.Parse(version) ?? throw new InvalidDataException($"Invalid typings version: {version}")).CompareTo(typing.Version) <= 0;
    }

    public static TypingsDiscoveryResult Discover(IFileSystem fs, TypeAcquisitionOptions acquisition, CompilerOptions options,
        IReadOnlyList<Utf8String> fileNames, Utf8String projectRoot, IEnumerable<Utf8String>? unresolvedImports = null,
        IReadOnlyDictionary<Utf8String, CachedTyping>? cache = null,
        IReadOnlyDictionary<Utf8String, IReadOnlyDictionary<Utf8String, Utf8String>>? registry = null,
        CancellationToken cancellation = default)
    {
        var inferred = new Dictionary<Utf8String, Utf8String>(Utf8StringComparer.Ordinal);
        var watch = new List<Utf8String>();
        var javaScript = fileNames.Where(IsJavaScript).ToArray();
        void Add(Utf8String name) => inferred.TryAdd(name, default);
        foreach (var name in acquisition.Include ?? []) Add(name);
        if (options.Types is null)
            foreach (var directory in javaScript.Select(CompilerPath.DirectoryName).Append(projectRoot).Distinct(Utf8StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                SearchManifest(directory, "bower.json"u8, "bower_components"u8);
                SearchManifest(directory, "package.json"u8, "node_modules"u8);
            }
        if (acquisition.DisableFilenameBasedTypeAcquisition != true)
        {
            foreach (var fileName in javaScript)
            {
                Utf8String name = CompilerPath.BaseName(fileName).ToLowerInvariant();
                name = name[..^CompilerPath.Extension(name).Length];
                if (TypingsMap.FileNames.TryGetValue(RemoveMinAndVersionNumbers(name), out var typing)) Add(typing);
            }
            if (javaScript.Any(name => name.EndsWith(".jsx"u8))) Add("react"u8);
        }
        foreach (var name in unresolvedImports ?? []) Add(NodeCoreModules.Contains(name) ? "node"u8 : name);
        foreach (var name in acquisition.Exclude ?? []) inferred.Remove(name);
        foreach (var (name, typing) in cache ?? new Dictionary<Utf8String, CachedTyping>())
            if (inferred.GetValueOrDefault(name).IsEmpty && registry?.TryGetValue(name, out var versions) == true && IsCurrent(typing, versions))
                inferred[name] = typing.FileName;
        return new(inferred.Values.Where(value => !value.IsEmpty).ToArray(), inferred.Where(pair => pair.Value.IsEmpty).Select(pair => pair.Key).ToArray(), watch.ToArray());

        void SearchManifest(Utf8String directory, Utf8String manifestName, Utf8String modulesName)
        {
            Utf8String manifestPath = CompilerPath.Combine(directory, manifestName);
            var dependencyNames = new List<Utf8String>();
            if (fs.ReadFile(manifestPath) is { } bytes)
            {
                watch.Add(manifestPath);
                var manifest = PackageJson.Parse(directory, bytes);
                foreach (var dependency in manifest.Dependencies()) { dependencyNames.Add(dependency.Name); Add(dependency.Name); }
            }
            Utf8String packages = CompilerPath.Combine(directory, modulesName);
            watch.Add(packages);
            if (!fs.DirectoryExists(packages)) return;
            IEnumerable<Utf8String> manifests;
            if (dependencyNames.Count != 0) manifests = dependencyNames.Select(name => CompilerPath.Combine(packages, name, manifestName));
            else manifests = FileMatcher.ReadDirectory(fs, packages, projectRoot, [".json"u8], depth: 3, cancellation: cancellation)
                .Where(path => CompilerPath.BaseName(path) == manifestName && IsTopLevelManifest(path, modulesName));
            foreach (var fileName in manifests)
            {
                cancellation.ThrowIfCancellationRequested();
                if (fs.ReadFile(fileName) is not { } contents) continue;
                var manifest = PackageJson.Parse(CompilerPath.DirectoryName(fileName), contents);
                if (!manifest.Parseable || manifest.Name is not { IsEmpty: false } name) continue;
                Utf8String ownTypes = manifest.String("types"u8) ?? default;
                if (ownTypes.IsEmpty) ownTypes = manifest.String("typings"u8) ?? default;
                if (ownTypes.IsEmpty) Add(name);
                else
                {
                    var path = CompilerPath.Resolve(CompilerPath.DirectoryName(fileName), ownTypes);
                    if (fs.FileExists(path)) inferred[name] = path;
                }
            }
        }
    }

    private static bool IsJavaScript(Utf8String path) => path.EndsWith(".js"u8) || path.EndsWith(".jsx"u8)
        || path.EndsWith(".mjs"u8) || path.EndsWith(".cjs"u8);
    private static bool IsTopLevelManifest(Utf8String path, Utf8String modulesName)
    {
        var package = CompilerPath.DirectoryName(path); var parent = CompilerPath.DirectoryName(package);
        if (CompilerPath.BaseName(parent).StartsWith("@"u8)) parent = CompilerPath.DirectoryName(parent);
        return CompilerPath.BaseName(parent).ToLowerInvariant() == modulesName;
    }
}

using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Resolution;

internal sealed partial class ModuleSpecifierPackages
{
    internal string FromNodeModules(string target, SourceFileNode source, ReferenceResolutionMode defaultMode,
        ReferenceResolutionMode overrideMode = 0, string preference = "", bool packageNameOnly = false,
        bool isRedirect = false, string globalTypingsCache = "", CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (NodeModuleParts(target) is not { } parts)
            return "";
        string specifier = target;
        bool packageRoot = false;
        if (!packageNameOnly)
        {
            var endings = ModuleSpecifierPaths.AllowedEndings(
                options,
                source,
                defaultMode,
                preference: preference,
                cancellation: cancellation);
            var attempt = DirectoryWithPackageJson(target, parts, overrideMode == 0 ? defaultMode : overrideMode, endings, cancellation);
            if (attempt.Blocked)
                return "";
            if (attempt.Exported)
                return attempt.FileName;
            packageRoot = attempt.Root.Length != 0;
            // The reference retries the same package root for each path component. The package cache
            // is stable for this naming request, so one attempt produces the same result.
            specifier = packageRoot ? attempt.Root
                : ModuleSpecifierPaths.ProcessEnding(attempt.FileName, endings, options, fileSystem, currentDirectory, cancellation);
        }
        if (isRedirect && !packageRoot)
            return "";
        string topLevel = specifier[..parts.NodeModules];
        if (!CompilerPath.DirectoryName(source.FileName).StartsWith(topLevel, Comparison)
            || globalTypingsCache.Length != 0 && globalTypingsCache.StartsWith(topLevel, Comparison))
            return "";
        return PackageNameFromTypes(specifier[(parts.PackageName + 1)..]);
    }

    private readonly record struct NodeModulePathParts(int NodeModules, int PackageName, int PackageRoot);

    private static NodeModulePathParts? NodeModuleParts(string path)
    {
        int nodeModules = 0, packageName = 0, packageRoot = 0, state = 0, end = 0;
        while (end >= 0)
        {
            int start = end;
            end = start < path.Length ? path.IndexOf('/', start + 1) : -1;
            switch (state)
            {
                case 0:
                    if (path.AsSpan(start).StartsWith("/node_modules/", StringComparison.Ordinal))
                    {
                        nodeModules = start;
                        packageName = end;
                        state = 1;
                    }
                    break;
                case 1:
                case 2:
                    if (state == 1 && start + 1 < path.Length && path[start + 1] == '@')
                        state = 2;
                    else
                    {
                        packageRoot = end;
                        state = 3;
                    }
                    break;
                case 3:
                    if (path.AsSpan(start).StartsWith("/node_modules/", StringComparison.Ordinal))
                        state = 1;
                    break;
            }
        }
        return state > 1 ? new(nodeModules, packageName, packageRoot) : null;
    }

    private static string PackageNameFromTypes(string name)
    {
        if (!name.StartsWith("@types/", StringComparison.Ordinal))
            return name;
        name = name[7..];
        int separator = name.IndexOf("__", StringComparison.Ordinal);
        return separator < 0 ? name : "@" + name[..separator] + "/" + name[(separator + 2)..];
    }

    private readonly record struct PackageDirectoryAttempt(string FileName, string Root = "", bool Blocked = false, bool Exported = false);

    private PackageDirectoryAttempt DirectoryWithPackageJson(string target, NodeModulePathParts parts,
        ReferenceResolutionMode mode, IReadOnlyList<ModuleSpecifierEnding> endings, CancellationToken cancellation)
    {
        string root = parts.PackageRoot < 0 ? target : target[..parts.PackageRoot];
        var package = packages.Get(root).Contents;
        if (package is null)
            return new(target, target[(parts.PackageRoot + 1)..] is "index.d.ts" or "index.js" or "index.ts" or "index.tsx" ? root : "");

        if (options.Boolean("resolvePackageJsonExports") != false
            && package.Get("exports") is { ValueKind: not JsonValueKind.Undefined } exports)
        {
            mode = ModuleSpecifierPaths.Extension(target) switch
            {
                ".cjs" or ".cts" or ".d.cts" => ReferenceResolutionMode.Require,
                ".mjs" or ".mts" or ".d.mts" => ReferenceResolutionMode.Import,
                _ => mode
            };
            string named = FromExports(
                target,
                root,
                PackageNameFromTypes(root[(parts.PackageName + 1)..]),
                exports,
                Conditions(mode),
                cancellation);
            return named.Length == 0 ? new(target, Blocked: true) : new(named, Exported: true);
        }

        string moduleFile = target;
        bool blockedByVersions = false;
        using var versionPaths = NamingVersionPaths(package);
        if (versionPaths is not null)
        {
            string mapped = ModuleSpecifierPaths.FromPaths(target[(root.Length + 1)..], versionPaths.RootElement, endings, root,
                options, fileSystem, currentDirectory, cancellation);
            blockedByVersions = mapped.Length == 0;
            if (!blockedByVersions)
                moduleFile = CompilerPath.Combine(root, mapped);
        }
        string main = package.String("typings") ?? package.String("types") ?? package.String("main") ?? "index.js";
        if (main.Length != 0 && !(blockedByVersions && MatchesVersionPath(versionPaths!.RootElement, main)))
        {
            string mainFile = CompilerPath.Resolve(root, main);
            if (SamePath(ModuleSpecifierPaths.WithoutExtension(mainFile), ModuleSpecifierPaths.WithoutExtension(moduleFile)))
                return new(moduleFile, root);
            if (package.Type != "module"
                && ModuleSpecifierPaths.Extension(moduleFile) is not (".mts" or ".d.mts" or ".mjs" or ".cts" or ".d.cts" or ".cjs")
                && moduleFile.StartsWith(mainFile, Comparison)
                && SamePath(CompilerPath.DirectoryName(moduleFile), CompilerPath.RemoveTrailingSeparator(mainFile))
                && ModuleSpecifierPaths.WithoutExtension(CompilerPath.BaseName(moduleFile)) == "index")
                return new(moduleFile, root);
        }
        return new(moduleFile);
    }

    private bool SamePath(string left, string right) => CompilerPath.Relative(
        CompilerPath.Resolve(currentDirectory, left), CompilerPath.Resolve(currentDirectory, right), fileSystem.CaseSensitive).Length == 0;

    private JsonDocument? NamingVersionPaths(PackageJson package)
    {
        var paths = package.VersionPaths(version);
        if (paths.ValueKind != JsonValueKind.Object)
            return null;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var entry in PackageJson.Properties(paths))
            {
                if (entry.Value.ValueKind != JsonValueKind.Array)
                    continue;
                writer.WriteStartArray(entry.Name);
                foreach (var item in entry.Value.EnumerateArray())
                    writer.WriteStringValue(item.ValueKind == JsonValueKind.String ? JsonStrings.GetString(item) : "");
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray());
    }

    private static bool MatchesVersionPath(JsonElement paths, string candidate)
    {
        foreach (var entry in PackageJson.Properties(paths))
        {
            string key = entry.Name;
            int star = key.IndexOf('*');
            if (star < 0 ? key == candidate
                : key.IndexOf('*', star + 1) < 0 && candidate.Length >= key.Length - 1
                    && candidate.StartsWith(key[..star], StringComparison.Ordinal)
                    && candidate.EndsWith(key[(star + 1)..], StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Resolution;

internal sealed partial class ModuleSpecifierPackages
{
    internal Utf8String FromNodeModules(Utf8String target, SourceFileNode source, ReferenceResolutionMode defaultMode,
        ReferenceResolutionMode overrideMode = 0, Utf8String preference = default, bool packageNameOnly = false,
        bool isRedirect = false, Utf8String globalTypingsCache = default, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (NodeModuleParts(target) is not { } parts)
            return Utf8String.Empty;
        Utf8String specifier = target;
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
                return Utf8String.Empty;
            if (attempt.Exported)
                return attempt.FileName;
            packageRoot = attempt.Root.Length != 0;
            // The reference retries the same package root for each path component. The package cache
            // is stable for this naming request, so one attempt produces the same result.
            specifier = packageRoot ? attempt.Root
                : ModuleSpecifierPaths.ProcessEnding(attempt.FileName, endings, options, fileSystem, currentDirectory, cancellation);
        }
        if (isRedirect && !packageRoot)
            return Utf8String.Empty;
        Utf8String topLevel = specifier[..parts.NodeModules];
        if (!CompilerPath.DirectoryName(source.FileName).StartsWith(topLevel, Comparison)
            || globalTypingsCache.Length != 0 && globalTypingsCache.StartsWith(topLevel, Comparison))
            return Utf8String.Empty;
        return PackageNameFromTypes(specifier[(parts.PackageName + 1)..]);
    }

    private readonly record struct NodeModulePathParts(int NodeModules, int PackageName, int PackageRoot);

    private static NodeModulePathParts? NodeModuleParts(Utf8String path)
    {
        int nodeModules = 0, packageName = 0, packageRoot = 0, state = 0, end = 0;
        while (end >= 0)
        {
            int start = end;
            end = start < path.Length ? path.IndexOf((byte)'/', start + 1) : -1;
            switch (state)
            {
                case 0:
                    if (path.AsSpan(start).StartsWith("/node_modules/"u8, StringComparison.Ordinal))
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
                    if (path.AsSpan(start).StartsWith("/node_modules/"u8, StringComparison.Ordinal))
                        state = 1;
                    break;
            }
        }
        return state > 1 ? new(nodeModules, packageName, packageRoot) : null;
    }

    private static Utf8String PackageNameFromTypes(Utf8String name)
    {
        if (!name.StartsWith("@types/"u8, StringComparison.Ordinal))
            return name;
        name = name[7..];
        int separator = name.IndexOf("__"u8, StringComparison.Ordinal);
        return separator < 0 ? name : Utf8Literals.At + name[..separator] + Utf8Literals.Slash + name[(separator + 2)..];
    }

    private readonly record struct PackageDirectoryAttempt(Utf8String FileName, Utf8String Root = default, bool Blocked = false, bool Exported = false);

    private PackageDirectoryAttempt DirectoryWithPackageJson(Utf8String target, NodeModulePathParts parts,
        ReferenceResolutionMode mode, IReadOnlyList<ModuleSpecifierEnding> endings, CancellationToken cancellation)
    {
        Utf8String root = parts.PackageRoot < 0 ? target : target[..parts.PackageRoot];
        var package = packages.Get(root).Contents;
        if (package is null)
            return new(target, (target[(parts.PackageRoot + 1)..] is var matchedText && (matchedText == "index.d.ts"u8 || matchedText == "index.js"u8 || matchedText == "index.ts"u8 || matchedText == "index.tsx"u8)) ? root : Utf8String.Empty);

        if (options.ResolvePackageJsonExports != false
            && package.Get(Utf8Literals.Exports) is { ValueKind: not JsonValueKind.Undefined } exports)
        {
            mode = ModuleSpecifierPaths.Extension(target) switch
            {
                var matchedText2 when matchedText2 == ".cjs"u8 || matchedText2 == ".cts"u8 || matchedText2 == ".d.cts"u8 => ReferenceResolutionMode.Require,
                var matchedText3 when matchedText3 == ".mjs"u8 || matchedText3 == ".mts"u8 || matchedText3 == ".d.mts"u8 => ReferenceResolutionMode.Import,
                _ => mode
            };
            Utf8String named = FromExports(
                target,
                root,
                PackageNameFromTypes(root[(parts.PackageName + 1)..]),
                exports,
                Conditions(mode),
                cancellation);
            return named.Length == 0 ? new(target, Blocked: true) : new(named, Exported: true);
        }

        Utf8String moduleFile = target;
        bool blockedByVersions = false;
        using var versionPaths = NamingVersionPaths(package);
        if (versionPaths is not null)
        {
            Utf8String mapped = ModuleSpecifierPaths.FromPaths(target[(root.Length + 1)..],
                CompilerOptions.ParsePaths(versionPaths.RootElement) ?? [], endings, root,
                options, fileSystem, currentDirectory, cancellation);
            blockedByVersions = mapped.Length == 0;
            if (!blockedByVersions)
                moduleFile = CompilerPath.Combine(root, mapped);
        }
        Utf8String main = package.String(Utf8Literals.Typings) ?? package.String(Utf8Literals.Types) ?? package.String(Utf8Literals.Main) ?? Utf8Literals.IndexJs;
        if (main.Length != 0 && !(blockedByVersions && MatchesVersionPath(versionPaths!.RootElement, main)))
        {
            Utf8String mainFile = CompilerPath.Resolve(root, main);
            if (SamePath(ModuleSpecifierPaths.WithoutExtension(mainFile), ModuleSpecifierPaths.WithoutExtension(moduleFile)))
                return new(moduleFile, root);
            if (package.Type != Utf8Literals.Module
                && !(ModuleSpecifierPaths.Extension(moduleFile) == ".mts"u8 || ModuleSpecifierPaths.Extension(moduleFile) == ".d.mts"u8 || ModuleSpecifierPaths.Extension(moduleFile) == ".mjs"u8 || ModuleSpecifierPaths.Extension(moduleFile) == ".cts"u8 || ModuleSpecifierPaths.Extension(moduleFile) == ".d.cts"u8 || ModuleSpecifierPaths.Extension(moduleFile) == ".cjs"u8)
                && moduleFile.StartsWith(mainFile, Comparison)
                && SamePath(CompilerPath.DirectoryName(moduleFile), CompilerPath.RemoveTrailingSeparator(mainFile))
                && ModuleSpecifierPaths.WithoutExtension(CompilerPath.BaseName(moduleFile)) == Utf8Literals.Index)
                return new(moduleFile, root);
        }
        return new(moduleFile);
    }

    private bool SamePath(Utf8String left, Utf8String right) => CompilerPath.Relative(
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
                    writer.WriteStringValue(item.ValueKind == JsonValueKind.String ? JsonStrings.GetString(item) : Utf8String.Empty);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray());
    }

    private static bool MatchesVersionPath(JsonElement paths, Utf8String candidate)
    {
        foreach (var entry in PackageJson.Properties(paths))
        {
            Utf8String key = JsonStrings.GetName(entry);
            int star = key.IndexOf((byte)'*');
            if (star < 0 ? key == candidate
                : key.IndexOf((byte)'*', star + 1) < 0 && candidate.Length >= key.Length - 1
                    && candidate.StartsWith(key[..star], StringComparison.Ordinal)
                    && candidate.EndsWith(key[(star + 1)..], StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

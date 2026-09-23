using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Resolution;

public sealed partial class ModuleResolver
{
    private sealed partial class Request
    {
        private ResolvedModule? RelativeLoad(Extensions ext, string candidate, bool considerPackage = true)
        {
            if (!CompilerPath.HasTrailingSeparator(candidate))
            {
                if (!DirectoryExists(CompilerPath.DirectoryName(candidate)))
                    return null;
                if (LoadFile(ext, candidate) is { } file)
                    return considerPackage ? WithPackage(file, PackageForFile(file.FileName)) : file;
            }
            if (!DirectoryExists(candidate) || esm)
                return null;
            return LoadDirectory(ext, candidate, considerPackage ? Package(candidate) : null);
        }

        private ResolvedModule? LoadFile(Extensions ext, string candidate, bool implicitExtensions = true)
        {
            if (CompilerPath.BaseName(candidate).Contains('.'))
            {
                string original = Extension(candidate);
                if (!IsTypeScript(original) && original is not (".js" or ".jsx" or ".mjs" or ".cjs" or ".json"))
                    original = resolver.extraExtensions.FirstOrDefault(e => candidate.EndsWith(e, StringComparison.Ordinal)) ?? original;
                Trace("extension", candidate, original);
                if (AddExtensions(ext, candidate[..^original.Length], original) is { } file)
                    return file;
            }
            return !esm && implicitExtensions ? AddExtensions(ext, candidate, "") : null;
        }

        private ResolvedModule? AddExtensions(Extensions ext, string stem, string original)
        {
            if (!DirectoryExists(CompilerPath.DirectoryName(stem)))
                return null;
            string[] candidates = original switch
            {
                ".mjs" or ".mts" or ".d.mts" => [".mts", ".d.mts", ".mjs"],
                ".cjs" or ".cts" or ".d.cts" => [".cts", ".d.cts", ".cjs"],
                ".json" => [".d.json.ts", ".json"],
                ".tsx" or ".jsx" => [".tsx", ".ts", ".d.ts", ".jsx", ".js"],
                ".ts" or ".d.ts" or ".js" or "" => [".ts", ".tsx", ".d.ts", ".js", ".jsx"],
                _ => []
            };
            if (configLookup && original is ".ts" or ".d.ts" or ".js" or "")
                candidates = [".json"];
            if (candidates.Length == 0)
            {
                if (resolver.extraExtensions.Contains(original) && TryFile(stem + original) is { } extra)
                    return new(extra, original, UsingExtraExtension: true);
                if ((ext & Extensions.Declaration) != 0 && !CompilerPath.IsDeclarationFile(stem + original))
                    candidates = [".d" + original + ".ts"];
            }
            foreach (string extension in candidates)
            {
                if ((ext & ExtensionKind(extension)) == 0)
                    continue;
                if (TryFile(stem + extension) is { } file)
                    return External(new(file, extension, UsingTsExtension: !fromConfig && IsTypeScript(original)
                        && ExtensionKind(extension) is Extensions.TypeScript or Extensions.Declaration));
            }
            return null;
        }

        private static Extensions ExtensionKind(string extension) => CompilerPath.IsDeclarationFile("f" + extension) ? Extensions.Declaration
            : extension is ".ts" or ".tsx" or ".mts" or ".cts" ? Extensions.TypeScript
            : extension == ".json" ? Extensions.Json : Extensions.JavaScript;

        private string? TryFile(string candidate)
        {
            string ext = Extension(candidate);
            if (!IsTypeScript(ext) && ext is not (".js" or ".jsx" or ".mjs" or ".cjs" or ".json"))
                ext = "";
            string[]? suffixes = options.Strings("moduleSuffixes");
            foreach (string suffix in suffixes is { Length: > 0 } ? suffixes : [""])
            {
                cancellation.ThrowIfCancellationRequested();
                string path = candidate[..^ext.Length] + suffix + ext;
                locations.Add(path);
                bool exists = fs.FileExists(path);
                Trace("file", path, exists ? "exists" : "missing");
                if (exists)
                    return path;
            }
            return null;
        }

        private ResolvedModule? LoadDirectory(Extensions ext, string candidate, PackageJson? package)
        {
            string? packageFile = null;
            if (package is not null && candidate.TrimEnd('/').Equals(package.Directory,
                fs.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            {
                if (configLookup)
                    packageFile = PathField(package, "tsconfig");
                else if ((ext & Extensions.Declaration) != 0)
                    packageFile = PathField(package, "typings") ?? PathField(package, "types");
                if (!configLookup)
                    packageFile ??= PathField(package, "main");
            }
            ResolvedModule? Load(string path)
            {
                if (PackageFile(ext, path, packageFile ?? "") is { } file)
                    return file;
                bool savedEsm = esm, savedConfig = fromConfig;
                fromConfig = true;
                if (package is not null && package.Type != "module")
                    esm = false;
                try
                {
                    return RelativeLoad(ext == Extensions.Declaration ? ext | Extensions.TypeScript : ext, path, false);
                }
                finally
                {
                    esm = savedEsm;
                    fromConfig = savedConfig;
                }
            }
            string index = CompilerPath.Combine(candidate, configLookup ? "tsconfig" : "index");
            if (package?.VersionPaths(resolver.CompilerVersion) is { ValueKind: JsonValueKind.Object } paths
                && (packageFile is null || CompilerPath.Contains(candidate, packageFile, true)))
            {
                string module = CompilerPath.Relative(candidate, packageFile ?? index, true);
                if (Paths(ext, module, candidate, paths, Load) is { } mapped)
                    return mapped;
            }
            if (packageFile is not null && Load(packageFile) is { } result)
                return result;
            return !esm && DirectoryExists(candidate) ? LoadFile(ext, index) : null;
        }

        private string? PathField(PackageJson package, string name)
        {
            if (package.String(name) is not { Length: > 0 } value)
            {
                Trace("field-unavailable", package.Directory, name + ":" + package.Get(name).ValueKind);
                return null;
            }
            string result = CompilerPath.Resolve(package.Directory, value);
            Trace("field", result, name + ":" + value);
            return result;
        }

        private ResolvedModule? PackageFile(Extensions ext, string candidate, string pattern)
        {
            string extension = Extension(candidate);
            Extensions kind = ExtensionKind(extension);
            if (kind is Extensions.TypeScript or Extensions.Declaration && (ext & kind) != 0)
                return TryFile(candidate) is { } file ? External(new(file, extension, UsingTsExtension: pattern.EndsWith('*'))) : null;
            bool saved = fromConfig;
            fromConfig = true;
            try
            {
                return LoadFile(ext, candidate, false);
            }
            finally
            {
                fromConfig = saved;
            }
        }

        private ResolvedModule? Paths(
            Extensions ext,
            string module,
            string directory,
            JsonElement paths,
            Func<string, ResolvedModule?> load)
        {
            JsonProperty? best = null;
            string star = "";
            int bestPrefix = -1;
            foreach (var entry in PackageJson.Properties(paths))
            {
                if (entry.Name == module)
                {
                    best = entry;
                    star = "";
                    break;
                }
                int index = entry.Name.IndexOf('*');
                if (index < 0 || entry.Name.IndexOf('*', index + 1) >= 0 || index <= bestPrefix)
                    continue;
                if (module.Length < entry.Name.Length - 1 || !module.StartsWith(entry.Name[..index], StringComparison.Ordinal)
                    || !module.EndsWith(entry.Name[(index + 1)..], StringComparison.Ordinal))
                    continue;
                best = entry;
                bestPrefix = index;
                star = module.Substring(index, module.Length - entry.Name.Length + 1);
            }
            if (best is not { Value.ValueKind: JsonValueKind.Array } matched)
                return null;
            Trace("paths", module, matched.Name);
            foreach (var value in matched.Value.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String)
                    continue;
                string substitution = JsonStrings.GetString(value);
                int index = substitution.IndexOf('*');
                string candidate = CompilerPath.Resolve(
                    directory,
                    index < 0 ? substitution : substitution[..index] + star + substitution[(index + 1)..]);
                string extension = Extension(substitution);
                Trace("substitution", candidate, substitution);
                if (extension.Length != 0 && TryFile(candidate) is { } direct)
                    return External(new(direct, extension));
                bool saved = fromConfig;
                fromConfig |= extension.Length != 0;
                try
                {
                    if (load(candidate) is { } result)
                        return result;
                }
                finally
                {
                    fromConfig = saved;
                }
            }
            return null;
        }

        private PackageJson? PackageForFile(string file)
        {
            int index = file.LastIndexOf("/node_modules/", StringComparison.Ordinal);
            if (index < 0)
                return null;
            (string package, _) = PackageName(file[(index + 14)..]);
            return Package(file[..(index + 14)] + package);
        }

        private ResolvedModule WithPackage(ResolvedModule file, PackageJson? package)
        {
            file = External(file);
            if (!file.IsResolved || package?.Name is not { } name || package.Version is not { } version)
                return file;
            string path = CompilerPath.Normalize(fs.RealPath(package.Directory));
            int index = path.LastIndexOf("/node_modules", StringComparison.Ordinal);
            var peers = new StringBuilder();
            if (index >= 0)
                foreach (var peer in package.Dependencies().Where(d => d.Field == "peerDependencies").OrderBy(
                    d => d.Name,
                    StringComparer.Ordinal))
                    if (Package(path[..(index + 13)] + "/" + peer.Name) is { } peerPackage)
                        peers.Append('+').Append(peer.Name).Append('@').Append(peerPackage.Version ?? "");
            string submodule = file.FileName.Length > package.Directory.Length ? file.FileName[(package.Directory.Length + 1)..] : "";
            return file with { PackageId = new(name, submodule, version, peers.ToString()) };
        }
    }
}

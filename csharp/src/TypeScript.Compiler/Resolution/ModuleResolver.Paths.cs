using TypeScript.Compiler.Text;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Resolution;

public sealed partial class ModuleResolver
{
    private sealed partial class Request
    {
        private ResolvedModule? RelativeLoad(Extensions ext, Utf8String candidate, bool considerPackage = true)
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

        private ResolvedModule? LoadFile(Extensions ext, Utf8String candidate, bool implicitExtensions = true)
        {
            if (CompilerPath.BaseName(candidate).Contains((byte)'.'))
            {
                Utf8String original = Extension(candidate);
                if (!IsTypeScript(original) && !(original == ".js"u8 || original == ".jsx"u8 || original == ".mjs"u8 || original == ".cjs"u8 || original == ".json"u8))
                    original = resolver.extraExtensions.FirstOrDefault(e => candidate.EndsWith(e, StringComparison.Ordinal), original);
                Trace(Utf8Literals.Extension, candidate, original);
                if (AddExtensions(ext, candidate[..^original.Length], original) is { } file)
                    return file;
            }
            return !esm && implicitExtensions ? AddExtensions(ext, candidate, Utf8String.Empty) : null;
        }

        private ResolvedModule? AddExtensions(Extensions ext, Utf8String stem, Utf8String original)
        {
            if (!DirectoryExists(CompilerPath.DirectoryName(stem)))
                return null;
            Utf8String[] candidates = original switch
            {
                _ when original == ".mjs"u8 || original == ".mts"u8 || original == ".d.mts"u8 => [Utf8Literals.Mts, Utf8Literals.DMts, Utf8Literals.Mjs],
                _ when original == ".cjs"u8 || original == ".cts"u8 || original == ".d.cts"u8 => [Utf8Literals.Cts, Utf8Literals.DCts, Utf8Literals.Cjs],
                _ when original == ".json"u8 => [Utf8Literals.DJsonTs, Utf8Literals.Json],
                _ when original == ".tsx"u8 || original == ".jsx"u8 => [Utf8Literals.Tsx, Utf8Literals.Ts, Utf8Literals.DTs, Utf8Literals.Jsx, Utf8Literals.Js],
                _ when original == ".ts"u8 || original == ".d.ts"u8 || original == ".js"u8 || original == ""u8 => [Utf8Literals.Ts, Utf8Literals.Tsx, Utf8Literals.DTs, Utf8Literals.Js, Utf8Literals.Jsx],
                _ => []
            };
            if (configLookup && (original == ".ts"u8 || original == ".d.ts"u8 || original == ".js"u8 || original == ""u8))
                candidates = [Utf8Literals.Json];
            if (candidates.Length == 0)
            {
                if (resolver.extraExtensions.Contains(original) && TryFile(stem + original) is { } extra)
                    return new(extra, original, UsingExtraExtension: true);
                if ((ext & Extensions.Declaration) != 0 && !CompilerPath.IsDeclarationFile(stem + original))
                    candidates = [Utf8Literals.D + original + Utf8Literals.Ts];
            }
            foreach (Utf8String extension in candidates)
            {
                if ((ext & ExtensionKind(extension)) == 0)
                    continue;
                if (TryFile(stem + extension) is { } file)
                    return External(new(file, extension, UsingTsExtension: !fromConfig && IsTypeScript(original)
                        && ExtensionKind(extension) is Extensions.TypeScript or Extensions.Declaration));
            }
            return null;
        }

        private static Extensions ExtensionKind(Utf8String extension) => CompilerPath.IsDeclarationFile(Utf8Literals.F + extension) ? Extensions.Declaration
            : (extension == ".ts"u8 || extension == ".tsx"u8 || extension == ".mts"u8 || extension == ".cts"u8) ? Extensions.TypeScript
            : extension == Utf8Literals.Json ? Extensions.Json : Extensions.JavaScript;

        private Utf8String? TryFile(Utf8String candidate)
        {
            Utf8String ext = Extension(candidate);
            if (!IsTypeScript(ext) && !(ext == ".js"u8 || ext == ".jsx"u8 || ext == ".mjs"u8 || ext == ".cjs"u8 || ext == ".json"u8))
                ext = Utf8String.Empty;
            Utf8String[]? suffixes = options.ModuleSuffixes;
            foreach (Utf8String suffix in suffixes is { Length: > 0 } ? suffixes : [Utf8String.Empty])
            {
                cancellation.ThrowIfCancellationRequested();
                Utf8String path = candidate[..^ext.Length] + suffix + ext;
                locations.Add(path);
                bool exists = fs.FileExists(path);
                Trace(Utf8Literals.File, path, exists ? Utf8Literals.Exists : Utf8Literals.Missing);
                if (exists)
                    return path;
            }
            return null;
        }

        private ResolvedModule? LoadDirectory(Extensions ext, Utf8String candidate, PackageJson? package)
        {
            Utf8String? packageFile = null;
            if (package is not null && candidate.TrimEnd((byte)'/').Equals(package.Directory,
                fs.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            {
                if (configLookup)
                    packageFile = PathField(package, Utf8Literals.Tsconfig);
                else if ((ext & Extensions.Declaration) != 0)
                    packageFile = PathField(package, Utf8Literals.Typings) ?? PathField(package, Utf8Literals.Types);
                if (!configLookup)
                    packageFile ??= PathField(package, Utf8Literals.Main);
            }
            ResolvedModule? Load(Utf8String path)
            {
                if (PackageFile(ext, path, packageFile ?? Utf8String.Empty) is { } file)
                    return file;
                bool savedEsm = esm, savedConfig = fromConfig;
                fromConfig = true;
                if (package is not null && package.Type != Utf8Literals.Module)
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
            Utf8String index = CompilerPath.Combine(candidate, configLookup ? Utf8Literals.Tsconfig : Utf8Literals.Index);
            if (package?.VersionPaths(resolver.CompilerVersion) is { ValueKind: JsonValueKind.Object } paths
                && (packageFile is null || CompilerPath.Contains(candidate, packageFile.Value, true))
                && CompilerOptions.ParsePaths(paths) is { } mappings)
            {
                Utf8String module = CompilerPath.Relative(candidate, packageFile ?? index, true);
                if (Paths(ext, module, candidate, mappings, Load) is { } mapped)
                    return mapped;
            }
            if (packageFile is not null && Load(packageFile.Value) is { } result)
                return result;
            return !esm && DirectoryExists(candidate) ? LoadFile(ext, index) : null;
        }

        private Utf8String? PathField(PackageJson package, Utf8String name)
        {
            if (package.String(name) is not { Length: > 0 } value)
            {
                Trace(Utf8Literals.FieldUnavailable, package.Directory, name + Utf8Literals.Colon + Utf8String.EnumName(package.Get(name).ValueKind));
                return null;
            }
            Utf8String result = CompilerPath.Resolve(package.Directory, value);
            Trace(Utf8Literals.Field, result, name + Utf8Literals.Colon + value);
            return result;
        }

        private ResolvedModule? PackageFile(Extensions ext, Utf8String candidate, Utf8String pattern)
        {
            Utf8String extension = Extension(candidate);
            Extensions kind = ExtensionKind(extension);
            if (kind is Extensions.TypeScript or Extensions.Declaration && (ext & kind) != 0)
                return TryFile(candidate) is { } file ? External(new(file, extension, UsingTsExtension: pattern.EndsWith((byte)'*'))) : null;
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
            Utf8String module,
            Utf8String directory,
            IReadOnlyList<KeyValuePair<Utf8String, Utf8String[]>> paths,
            Func<Utf8String, ResolvedModule?> load)
        {
            KeyValuePair<Utf8String, Utf8String[]>? best = null;
            Utf8String star = default;
            int bestPrefix = -1;
            foreach (var entry in paths)
            {
                if (entry.Key == module)
                {
                    best = entry;
                    star = Utf8String.Empty;
                    break;
                }
                int index = entry.Key.IndexOf((byte)'*');
                if (index < 0 || entry.Key.IndexOf((byte)'*', index + 1) >= 0 || index <= bestPrefix)
                    continue;
                if (module.Length < entry.Key.Length - 1 || !module.StartsWith(entry.Key[..index], StringComparison.Ordinal)
                    || !module.EndsWith(entry.Key[(index + 1)..], StringComparison.Ordinal))
                    continue;
                best = entry;
                bestPrefix = index;
                star = module.Substring(index, module.Length - entry.Key.Length + 1);
            }
            if (best is not { } matched)
                return null;
            Trace(Utf8Literals.Paths, module, matched.Key);
            foreach (Utf8String substitution in matched.Value)
            {
                int index = substitution.IndexOf((byte)'*');
                Utf8String candidate = CompilerPath.Resolve(
                    directory,
                    index < 0 ? substitution : substitution[..index] + star + substitution[(index + 1)..]);
                Utf8String extension = Extension(substitution);
                Trace(Utf8Literals.Substitution, candidate, substitution);
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

        private PackageJson? PackageForFile(Utf8String file)
        {
            int index = file.LastIndexOf("/node_modules/"u8, StringComparison.Ordinal);
            if (index < 0)
                return null;
            (Utf8String package, _) = PackageName(file[(index + 14)..]);
            return Package(file[..(index + 14)] + package);
        }

        private ResolvedModule WithPackage(ResolvedModule file, PackageJson? package)
        {
            file = External(file);
            if (!file.IsResolved || package?.Name is not { } name || package.Version is not { } version)
                return file;
            Utf8String path = CompilerPath.Normalize(fs.RealPath(package.Directory));
            int index = path.LastIndexOf("/node_modules"u8, StringComparison.Ordinal);
            var peers = new Utf8StringBuilder();
            if (index >= 0)
                foreach (var peer in package.Dependencies().Where(d => d.Field == Utf8Literals.PeerDependencies).OrderBy(
                    d => d.Name,
                    Utf8StringComparer.Ordinal))
                    if (Package(path[..(index + 13)] + Utf8Literals.Slash + peer.Name) is { } peerPackage)
                        peers.Append((byte)'+').Append(peer.Name).Append((byte)'@').Append(peerPackage.Version ?? ""u8);
            Utf8String submodule = file.FileName.Length > package.Directory.Length ? file.FileName[(package.Directory.Length + 1)..] : Utf8String.Empty;
            return file with { PackageId = new(name, submodule, version, peers.ToUtf8String()) };
        }
    }
}

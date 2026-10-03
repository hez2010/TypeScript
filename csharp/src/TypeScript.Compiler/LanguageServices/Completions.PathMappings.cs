using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class CompletionQuery
    {
        private static readonly SemanticVersion CompletionCompilerVersion = new(7, 1, 0, "dev"u8);

        private bool AddPathMappings(Dictionary<Utf8String, PathCompletion> result, Utf8String fragment, Utf8String directory, PathOptions pathOptions,
            IEnumerable<KeyValuePair<Utf8String, Utf8String[]>> mappings, bool exports = false, bool imports = false)
        {
            List<(IReadOnlyList<PathCompletion> Entries, bool Matched)> all = [];
            Utf8String? matchedPath = null;
            foreach (var (rawKey, patterns) in mappings)
            {
                cancellation.ThrowIfCancellationRequested();
                if (rawKey == "."u8 || patterns.Length == 0) continue;
                var key = rawKey.StartsWith("./"u8) ? rawKey[2..] : rawKey;
                if ((exports || imports) && key.EndsWith("/"u8)) key += "*"u8;
                int star = key.IndexOf((byte)'*');
                if (star >= 0 && key[(star + 1)..].Contains("*"u8)) continue;
                bool match = star < 0 ? key == fragment : fragment.Length >= key.Length - 1 && fragment.StartsWith(key[..star]) && fragment.EndsWith(key[(star + 1)..]);
                if (match && (matchedPath is null || Compare(key, matchedPath.Value) < 0))
                { matchedPath = key; all.RemoveAll(item => item.Matched); }
                if (star < 0 || matchedPath is null || Compare(key, matchedPath.Value) <= 0)
                    all.Add((MappedPaths(key, patterns, fragment, directory, pathOptions, exports, imports), match));
            }
            foreach (var (entries, _) in all) foreach (var entry in entries) AddPath(result, entry);
            return matchedPath is not null;

            int Compare(Utf8String a, Utf8String b) => exports || imports ? ModuleResolver.ComparePatternKeys(a, b)
                : (b.IndexOf((byte)'*') is var bi && bi >= 0 ? bi : b.Length).CompareTo(a.IndexOf((byte)'*') is var ai && ai >= 0 ? ai : a.Length);
        }

        private IReadOnlyList<PathCompletion> MappedPaths(Utf8String key, IReadOnlyList<Utf8String> patterns, Utf8String fragment, Utf8String directory,
            PathOptions pathOptions, bool exports, bool imports)
        {
            var fragmentDirectory = FragmentDirectory(fragment);
            if (!fragmentDirectory.IsEmpty) fragmentDirectory = CompilerPath.EnsureTrailingSeparator(fragmentDirectory);
            int star = key.IndexOf((byte)'*');
            if (star < 0) return NameOnly(key, 1, patterns.Count == 0 ? default : CompletionExtension(patterns[0]));
            var prefix = key[..star]; var suffix = key[(star + 1)..];
            Utf8String remaining, directoryPrefix;
            if (!fragment.StartsWith(prefix))
            {
                if (!prefix.StartsWith(fragment)) return [];
                if (key.EndsWith("/*"u8)) return NameOnly(prefix, 0, default);
                remaining = default; directoryPrefix = prefix[fragmentDirectory.Length..];
            }
            else
            {
                remaining = fragment[prefix.Length..];
                directoryPrefix = fragmentDirectory.StartsWith(prefix) ? default : prefix[fragmentDirectory.Length..];
            }
            List<PathCompletion> result = [];
            foreach (var pattern in patterns)
                foreach (var entry in PatternPaths(remaining, directory, pattern, pathOptions, exports, imports))
                    result.Add(entry with { Name = directoryPrefix + entry.Name + (entry.Kind == 1 ? suffix : Utf8String.Empty) });
            return result;

            IReadOnlyList<PathCompletion> NameOnly(Utf8String name, int kind, Utf8String extension)
            {
                if (!name.StartsWith(fragment)) return [];
                name = name.TrimEnd((byte)'/');
                if (!fragmentDirectory.IsEmpty && name.StartsWith(fragmentDirectory)) name = name[fragmentDirectory.Length..];
                return [new(name, kind, extension)];
            }
        }

        private IReadOnlyList<PathCompletion> PatternPaths(Utf8String fragment, Utf8String directory, Utf8String pattern, PathOptions pathOptions, bool exports, bool imports)
        {
            int star = pattern.IndexOf((byte)'*');
            if (star < 0 || pattern[(star + 1)..].Contains("*"u8)) return [];
            var prefix = pattern[..star]; var suffix = CompilerPath.Normalize(pattern[(star + 1)..]);
            var normalized = CompilerPath.Normalize(prefix);
            var prefixDirectory = CompilerPath.HasTrailingSeparator(prefix) ? normalized : CompilerPath.DirectoryName(normalized);
            var prefixBase = CompilerPath.HasTrailingSeparator(prefix) ? Utf8String.Empty : CompilerPath.BaseName(normalized);
            bool fragmentHasPath = fragment.Contains("/"u8);
            var expanded = fragmentHasPath ? CompilerPath.Combine(prefixDirectory, prefixBase + FragmentDirectory(fragment)) : prefixDirectory;
            var baseDirectory = CompilerPath.Normalize(CompilerPath.Combine(directory, expanded));
            List<Utf8String> roots = [baseDirectory];
            if (imports)
            {
                if (options.OutDir is { Length: > 0 } output) roots.Add(CompilerPath.Resolve(program.CommonSourceDirectory, CompilerPath.Relative(output, baseDirectory, FileSystem.CaseSensitive)));
                if (options.DeclarationDir is { Length: > 0 } declarations) roots.Add(CompilerPath.Resolve(program.CommonSourceDirectory, CompilerPath.Relative(declarations, baseDirectory, FileSystem.CaseSensitive)));
            }
            List<Utf8String> suffixes = [];
            if (!suffix.IsEmpty)
            {
                var extension = ModuleResolver.Extension("_"u8 + suffix);
                var declaration = suffix.EndsWith(".mjs"u8) || suffix.EndsWith(".mts"u8) ? (Utf8String)".d.mts"u8
                    : suffix.EndsWith(".cjs"u8) || suffix.EndsWith(".cts"u8) ? (Utf8String)".d.cts"u8
                    : suffix.EndsWith(".ts"u8) || suffix.EndsWith(".tsx"u8) || suffix.EndsWith(".js"u8) || suffix.EndsWith(".jsx"u8) ? (Utf8String)".d.ts"u8
                    : CompilerPath.Extension("_"u8 + suffix) is { IsEmpty: false } other ? ".d"u8 + other + ".ts"u8 : ".d.ts"u8;
                suffixes.Add(ChangeExtension(suffix, declaration));
                Utf8String[] inputs = extension == ".d.mts"u8 || extension == ".mjs"u8 || extension == ".mts"u8 ? [".mts"u8, ".mjs"u8]
                    : extension == ".d.cts"u8 || extension == ".cjs"u8 || extension == ".cts"u8 ? [".cts"u8, ".cjs"u8]
                    : extension.StartsWith(".d."u8) && extension.EndsWith(".ts"u8) && extension != ".d.ts"u8 ? ["."u8 + extension[3..^3]]
                    : [".tsx"u8, ".ts"u8, ".jsx"u8, ".js"u8];
                foreach (var input in inputs) suffixes.Add(ChangeExtension(suffix, input));
            }
            suffixes.Add(suffix);
            var includes = suffix.IsEmpty ? [(Utf8String)"./*"u8] : suffixes.Select(part => "**/*"u8 + part).ToArray();
            bool wildcard = (exports || imports) && pattern.EndsWith("/*"u8);
            List<PathCompletion> result = [];
            foreach (var root in roots)
            {
                cancellation.ThrowIfCancellationRequested();
                var completePrefix = fragmentHasPath ? root : CompilerPath.EnsureTrailingSeparator(root) + prefixBase;
                foreach (var file in FileMatcher.ReadDirectory(FileSystem, root, program.CurrentDirectory, pathOptions.Extensions, includes: includes, cancellation: cancellation))
                {
                    Utf8String trimmed = default;
                    var normalizedFile = CompilerPath.Normalize(file);
                    foreach (var ending in suffixes)
                        if (normalizedFile.StartsWith(completePrefix) && normalizedFile.EndsWith(ending) && normalizedFile.Length >= completePrefix.Length + ending.Length)
                        { trimmed = normalizedFile[completePrefix.Length..^(ending.Length)].TrimStart((byte)'/'); break; }
                    if (trimmed.IsEmpty) continue;
                    int slash = trimmed.IndexOf((byte)'/');
                    if (slash >= 0) result.Add(new(trimmed[..slash], 0));
                    else
                    {
                        var (name, extension) = PathFileName(trimmed, pathOptions, wildcard);
                        result.Add(new(name, 1, extension.IsEmpty ? CompletionExtension(file) : extension));
                    }
                }
            }
            if (suffix.IsEmpty)
                foreach (var root in roots)
                    foreach (var child in FileSystem.GetAccessibleEntries(root).Directories)
                        if (child != "node_modules"u8) result.Add(new(child, 0));
            return result;
        }

        private bool PackageMappedPaths(JsonElement map, Utf8String fragment, Utf8String directory, PathOptions pathOptions,
            Dictionary<Utf8String, PathCompletion> result, bool exports)
        {
            if (map.ValueKind != JsonValueKind.Object) return map.ValueKind != JsonValueKind.Undefined;
            var conditions = new ModuleSpecifierPackages(FileSystem, options, program.CurrentDirectory, program.CommonSourceDirectory).Conditions(pathOptions.Mode);
            List<KeyValuePair<Utf8String, Utf8String[]>> mappings = [];
            foreach (var entry in PackageJson.Properties(map))
            {
                var key = JsonStrings.GetName(entry);
                var target = FirstCondition(entry.Value, conditions);
                if (!target.IsEmpty)
                {
                    if (key.EndsWith("/"u8) && target.EndsWith("/"u8)) target += "*"u8;
                    mappings.Add(new(key, [target]));
                }
            }
            AddPathMappings(result, fragment, directory, pathOptions, mappings, exports, !exports);
            return true;
        }

        private Utf8String FirstCondition(JsonElement target, IReadOnlyList<Utf8String> conditions)
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (target.ValueKind == JsonValueKind.String) return JsonStrings.GetString(target);
                if (target.ValueKind != JsonValueKind.Object) return default;
                JsonElement next = default;
                foreach (var entry in PackageJson.Properties(target))
                {
                    var name = JsonStrings.GetName(entry);
                    if (name == "default"u8 || conditions.Contains(name) || conditions.Contains((Utf8String)"types"u8)
                        && name.StartsWith("types@"u8) && VersionRange.Parse(name[6..])?.Test(CompletionCompilerVersion) == true)
                    { next = entry.Value; break; }
                }
                target = next;
            }
        }

        private static IEnumerable<KeyValuePair<Utf8String, Utf8String[]>> JsonPathMappings(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) yield break;
            foreach (var entry in PackageJson.Properties(value))
                if (entry.Value.ValueKind == JsonValueKind.Array && entry.Value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
                    yield return new(JsonStrings.GetName(entry), entry.Value.EnumerateArray().Select(JsonStrings.GetString).ToArray());
        }

        private static Utf8String CompletionExtension(Utf8String name) => ModuleResolver.Extension("_"u8 + name) is { IsEmpty: false } extension ? extension : CompilerPath.Extension("_"u8 + name);
        private static Utf8String ChangeExtension(Utf8String name, Utf8String extension) => name[..^CompletionExtension(name).Length] + extension;
    }
}

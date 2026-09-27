using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Resolution;

public sealed partial class ModuleResolver
{
    public static int ComparePatternKeys(string a, string b)
    {
        int ai = a.IndexOf('*'), bi = b.IndexOf('*');
        int al = ai < 0 ? a.Length : ai + 1, bl = bi < 0 ? b.Length : bi + 1;
        if (al != bl)
            return bl.CompareTo(al);
        if (ai < 0)
            return bi < 0 ? 0 : 1;
        if (bi < 0)
            return -1;
        return b.Length.CompareTo(a.Length);
    }

    private sealed partial class Request
    {
        private ValueTask<ResolvedModule?> ExportsAsync(PackageJson scope, string subpath, Extensions ext)
        {
            JsonElement map = scope.Get("exports");
            if (subpath == ".")
            {
                JsonElement target = map.ValueKind is JsonValueKind.String or JsonValueKind.Array
                    || PackageJson.MapKind(map) == PackageMapKind.Conditions ? map
                    : map.ValueKind == JsonValueKind.Object && map.TryGetProperty(".", out var main) ? main : default;
                if (target.ValueKind != JsonValueKind.Undefined)
                    return TargetAsync(scope, target, "", false, ".", ext, false);
            }
            else if (PackageJson.MapKind(map) == PackageMapKind.Subpaths)
                return MapAsync(scope, map, subpath, ext, false);
            Trace("export-unavailable", scope.Directory, subpath);
            return new((ResolvedModule?)null);
        }

        private ValueTask<ResolvedModule?> MapAsync(PackageJson scope, JsonElement map, string name, Extensions ext, bool imports)
        {
            if (!name.EndsWith('/') && !name.Contains('*') && map.TryGetProperty(name, out var exact))
                return TargetAsync(scope, exact, "", false, name, ext, imports);
            foreach (var entry in PackageJson.Properties(map).Where(e => e.Name.Count(c => c == '*') == 1 || e.Name.EndsWith('/'))
                .OrderBy(e => e.Name, Comparer<string>.Create(ComparePatternKeys)))
            {
                string key = entry.Name;
                int star = key.IndexOf('*');
                if (star >= 0 && name.Length >= key.Length - 1 && name.StartsWith(key[..star], StringComparison.Ordinal)
                    && name.EndsWith(key[(star + 1)..], StringComparison.Ordinal))
                    return TargetAsync(scope, entry.Value, name.Substring(star, name.Length - key.Length + 1), true, key, ext, imports);
                if (star < 0 && name.StartsWith(key, StringComparison.Ordinal))
                    return TargetAsync(scope, entry.Value, name[key.Length..], false, key, ext, imports);
            }
            Trace("map-unavailable", scope.Directory, name);
            return new((ResolvedModule?)null);
        }

        private async ValueTask<ResolvedModule?> TargetAsync(PackageJson scope, JsonElement target, string subpath,
            bool pattern, string key, Extensions ext, bool imports)
        {
            // Explicit work stack handles arbitrarily nested condition objects and fallback arrays.
            var pending = new Stack<JsonElement>();
            pending.Push(target);
            while (pending.TryPop(out var item))
            {
                cancellation.ThrowIfCancellationRequested();
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var entries = PackageJson.Properties(item).ToArray();
                    for (int i = entries.Length - 1; i >= 0; i--)
                    {
                        string condition = entries[i].Name;
                        bool match = condition == "default" || conditions.Contains(condition)
                            || conditions.Contains("types") && condition.StartsWith("types@", StringComparison.Ordinal)
                                && VersionRange.Parse(condition[6..])?.Test(resolver.CompilerVersion) == true;
                        Trace("condition", condition, match ? "matched" : "skipped");
                        if (match)
                            pending.Push(entries[i].Value);
                    }
                    continue;
                }
                if (item.ValueKind == JsonValueKind.Array)
                {
                    for (int i = item.GetArrayLength() - 1; i >= 0; i--)
                        pending.Push(item[i]);
                    continue;
                }
                if (item.ValueKind == JsonValueKind.Null)
                {
                    Trace("blocked", scope.Directory, key);
                    return new();
                }
                if (item.ValueKind != JsonValueKind.String)
                    continue;
                string text = JsonStrings.GetString(item);
                if (!pattern && subpath.Length > 0 && !text.EndsWith('/'))
                    continue;
                string combined = pattern ? text.Replace("*", subpath, StringComparison.Ordinal) : text + subpath;
                if (!text.StartsWith("./", StringComparison.Ordinal))
                {
                    if (imports && !text.StartsWith("../", StringComparison.Ordinal) && !CompilerPath.IsAbsolute(text))
                    {
                        var request = new Request(resolver, combined, scope.Directory, mode, false, cancellation, active, configLookup);
                        var result = await request.RunAsync().ConfigureAwait(false);
                        trace.AddRange(request.trace);
                        diagnostics.AddRange(request.diagnostics);
                        locations.UnionWith(request.locations);
                        if (result.IsResolved)
                            return result;
                    }
                    continue;
                }
                if (InvalidSegments(text[2..]) || InvalidSegments(subpath))
                {
                    Trace("invalid-target", scope.Directory, combined);
                    continue;
                }
                string path = CompilerPath.Resolve(scope.Directory, combined);
                Trace(imports ? "imports" : "exports", path, key);
                if (InputFile(path, subpath, scope, imports) is { } source)
                    return WithPackage(source, scope);
                if (PackageFile(ext, path, text) is { } resolved)
                    return WithPackage(resolved, scope);
            }
            return null;
        }

        private static bool InvalidSegments(string path) => path.Split('/').Any(s => s is "." or ".." or "node_modules");

        private ResolvedModule? InputFile(string path, string entry, PackageJson package, bool imports)
        {
            if (configLookup)
                return null;
            string? outDir = options.String("outDir"), declarationDir = options.String("declarationDir");
            if (outDir is null && declarationDir is null || path.Contains("/node_modules/", StringComparison.Ordinal)
                || resolver.configFile.Length != 0 && !CompilerPath.Contains(package.Directory, resolver.configFile, fs.CaseSensitive))
                return null;
            string? root = options.String("rootDir") ?? (resolver.configFile.Length != 0
                ? CompilerPath.DirectoryName(resolver.configFile)
                : null);
            if (root is null)
            {
                diagnostics.Add(
                    new(
                    DiagnosticLocalization.GetMessage(
                        imports
                            ? DiagnosticCode.TheProjectRootIsAmbiguousButIsRequiredToResolveExportMapEntry0InFile1SupplyTheRootDirCompilerOptionToDisambiguate
                            : DiagnosticCode.TheProjectRootIsAmbiguousButIsRequiredToResolveImportMapEntry0InFile1SupplyTheRootDirCompilerOptionToDisambiguate),
                    0,
                    0,
                    [entry.Length == 0 ? "." : entry, CompilerPath.Combine(package.Directory, "package.json")]));
                return new();
            }
            foreach (string output in new[] { declarationDir, outDir }.OfType<string>().Distinct(StringComparer.Ordinal))
            {
                string folder = CompilerPath.Resolve(resolver.configFile.Length == 0 ? root : resolver.cwd, output);
                if (!CompilerPath.Contains(folder, path, fs.CaseSensitive))
                    continue;
                string input = CompilerPath.Resolve(root, CompilerPath.Relative(folder, path, fs.CaseSensitive));
                string extension = Extension(input);
                string[] candidates = extension switch
                {
                    ".mjs" or ".d.mts" => [".mts", ".mjs"],
                    ".cjs" or ".d.cts" => [".cts", ".cjs"],
                    ".js" or ".d.ts" => [".ts", ".tsx", ".js", ".jsx"],
                    ".json" => [".json"],
                    _ => []
                };
                foreach (string candidate in candidates)
                {
                    string file = input[..^extension.Length] + candidate;
                    if ((extensions & ExtensionKind(candidate)) != 0 && fs.FileExists(file)
                        && PackageFile(extensions, file, "") is { } result)
                        return result;
                }
            }
            return null;
        }
    }
}

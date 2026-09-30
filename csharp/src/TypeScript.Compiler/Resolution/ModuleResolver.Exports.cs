using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Resolution;

public sealed partial class ModuleResolver
{
    public static int ComparePatternKeys(Utf8String a, Utf8String b)
    {
        int ai = a.IndexOf((byte)'*'), bi = b.IndexOf((byte)'*');
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
        private ValueTask<ResolvedModule?> ExportsAsync(PackageJson scope, Utf8String subpath, Extensions ext)
        {
            JsonElement map = scope.Get(Utf8Literals.Exports);
            if (subpath == Utf8Literals.Dot)
            {
                JsonElement target = map.ValueKind is JsonValueKind.String or JsonValueKind.Array
                    || PackageJson.MapKind(map) == PackageMapKind.Conditions ? map
                    : map.ValueKind == JsonValueKind.Object && map.TryGetProperty("."u8, out var main) ? main : default;
                if (target.ValueKind != JsonValueKind.Undefined)
                    return TargetAsync(scope, target, Utf8String.Empty, false, Utf8Literals.Dot, ext, false);
            }
            else if (PackageJson.MapKind(map) == PackageMapKind.Subpaths)
                return MapAsync(scope, map, subpath, ext, false);
            Trace(Utf8Literals.ExportUnavailable, scope.Directory, subpath);
            return new((ResolvedModule?)null);
        }

        private ValueTask<ResolvedModule?> MapAsync(PackageJson scope, JsonElement map, Utf8String name, Extensions ext, bool imports)
        {
            if (!name.EndsWith((byte)'/') && !name.Contains((byte)'*') && map.TryGetProperty(name, out var exact))
                return TargetAsync(scope, exact, Utf8String.Empty, false, name, ext, imports);
            foreach (var entry in PackageJson.Properties(map).Where(e => e.Name.Count(c => c == '*') == 1 || e.Name.EndsWith('/'))
                .OrderBy(e => JsonStrings.GetName(e), Comparer<Utf8String>.Create(ComparePatternKeys)))
            {
                Utf8String key = JsonStrings.GetName(entry);
                int star = key.IndexOf((byte)'*');
                if (star >= 0 && name.Length >= key.Length - 1 && name.StartsWith(key[..star], StringComparison.Ordinal)
                    && name.EndsWith(key[(star + 1)..], StringComparison.Ordinal))
                    return TargetAsync(scope, entry.Value, name.Substring(star, name.Length - key.Length + 1), true, key, ext, imports);
                if (star < 0 && name.StartsWith(key, StringComparison.Ordinal))
                    return TargetAsync(scope, entry.Value, name[key.Length..], false, key, ext, imports);
            }
            Trace(Utf8Literals.MapUnavailable, scope.Directory, name);
            return new((ResolvedModule?)null);
        }

        private async ValueTask<ResolvedModule?> TargetAsync(PackageJson scope, JsonElement target, Utf8String subpath,
            bool pattern, Utf8String key, Extensions ext, bool imports)
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
                        Utf8String condition = JsonStrings.GetName(entries[i]);
                        bool match = condition == Utf8Literals.Default || conditions.Contains(condition)
                            || conditions.Contains(Utf8Literals.Types) && condition.StartsWith("types@"u8, StringComparison.Ordinal)
                                && VersionRange.Parse(condition[6..])?.Test(resolver.CompilerVersion) == true;
                        Trace(Utf8Literals.Condition, condition, match ? Utf8Literals.Matched : Utf8Literals.Skipped);
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
                    Trace(Utf8Literals.Blocked, scope.Directory, key);
                    return new();
                }
                if (item.ValueKind != JsonValueKind.String)
                    continue;
                Utf8String text = JsonStrings.GetString(item);
                if (!pattern && subpath.Length > 0 && !text.EndsWith((byte)'/'))
                    continue;
                Utf8String combined = pattern ? text.Replace("*"u8, subpath) : text + subpath;
                if (!text.StartsWith("./"u8, StringComparison.Ordinal))
                {
                    if (imports && !text.StartsWith("../"u8, StringComparison.Ordinal) && !CompilerPath.IsAbsolute(text))
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
                    Trace(Utf8Literals.InvalidTarget, scope.Directory, combined);
                    continue;
                }
                Utf8String path = CompilerPath.Resolve(scope.Directory, combined);
                Trace(imports ? Utf8Literals.Imports : Utf8Literals.Exports, path, key);
                if (InputFile(path, subpath, scope, imports) is { } source)
                    return WithPackage(source, scope);
                if (PackageFile(ext, path, text) is { } resolved)
                    return WithPackage(resolved, scope);
            }
            return null;
        }

        private static bool InvalidSegments(Utf8String path)
        {
            ReadOnlySpan<byte> text = path;
            foreach (Range range in text.Split((byte)'/'))
                if (text[range] is var matchedText && (matchedText.SequenceEqual("."u8) || matchedText.SequenceEqual(".."u8) || matchedText.SequenceEqual("node_modules"u8)))
                    return true;
            return false;
        }

        private ResolvedModule? InputFile(Utf8String path, Utf8String entry, PackageJson package, bool imports)
        {
            if (configLookup)
                return null;
            Utf8String? outDir = options.OutDir, declarationDir = options.DeclarationDir;
            if (outDir is null && declarationDir is null || path.Contains("/node_modules/"u8, StringComparison.Ordinal)
                || resolver.configFile.Length != 0 && !CompilerPath.Contains(package.Directory, resolver.configFile, fs.CaseSensitive))
                return null;
            Utf8String? root = options.RootDir ?? (resolver.configFile.Length != 0
                ? CompilerPath.DirectoryName(resolver.configFile)
                : (Utf8String?)null);
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
                    [entry.Length == 0 ? Utf8Literals.Dot : entry, CompilerPath.Combine(package.Directory, Utf8Literals.PackageJson)]));
                return new();
            }
            foreach (Utf8String output in new[] { declarationDir, outDir }.OfType<Utf8String>().Distinct(Utf8StringComparer.Ordinal))
            {
                Utf8String folder = CompilerPath.Resolve(resolver.configFile.Length == 0 ? root.Value : resolver.cwd, output);
                if (!CompilerPath.Contains(folder, path, fs.CaseSensitive))
                    continue;
                Utf8String input = CompilerPath.Resolve(root.Value, CompilerPath.Relative(folder, path, fs.CaseSensitive));
                Utf8String extension = Extension(input);
                Utf8String[] candidates = extension switch
                {
                    _ when extension == ".mjs"u8 || extension == ".d.mts"u8 => [Utf8Literals.Mts, Utf8Literals.Mjs],
                    _ when extension == ".cjs"u8 || extension == ".d.cts"u8 => [Utf8Literals.Cts, Utf8Literals.Cjs],
                    _ when extension == ".js"u8 || extension == ".d.ts"u8 => [Utf8Literals.Ts, Utf8Literals.Tsx, Utf8Literals.Js, Utf8Literals.Jsx],
                    _ when extension == ".json"u8 => [Utf8Literals.Json],
                    _ => []
                };
                foreach (Utf8String candidate in candidates)
                {
                    Utf8String file = input[..^extension.Length] + candidate;
                    if ((extensions & ExtensionKind(candidate)) != 0 && fs.FileExists(file)
                        && PackageFile(extensions, file, Utf8String.Empty) is { } result)
                        return result;
                }
            }
            return null;
        }
    }
}

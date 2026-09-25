using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Resolution;

internal sealed partial class ModuleSpecifierGenerator
{
    internal IReadOnlyList<ModuleSpecifierPath> EachPath(string importingFile, string target, bool preferSymlinks = true,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        string reference = host.ProjectOutput(CompilerPath.Resolve(host.CurrentDirectory, target));
        var names = new List<string>();
        if (reference.Length != 0)
            names.Add(reference);
        names.Add(target);
        names.AddRange(host.RedirectTargets(CompilerPath.Resolve(host.CurrentDirectory, target)));
        string[] targets = names.Select(f => CompilerPath.Resolve(host.CurrentDirectory, f)).ToArray();
        bool filterIgnored = !targets.All(Ignored);
        var result = new List<ModuleSpecifierPath>();
        if (!preferSymlinks)
            AddTargets();
        foreach (string directory in PackageJsonCache.Ancestors(
            CompilerPath.DirectoryName(CompilerPath.Resolve(host.CurrentDirectory, target))))
        {
            cancellation.ThrowIfCancellationRequested();
            var symlinks = host.SymlinkDirectories(directory);
            if (symlinks.Count != 0)
            {
                if (StartsWithDirectory(importingFile, directory))
                    break;
                foreach (string fileName in targets)
                {
                    if (!StartsWithDirectory(fileName, directory))
                        continue;
                    string relative = CompilerPath.Relative(directory, fileName, host.FileSystem.CaseSensitive);
                    foreach (string link in symlinks)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string file = CompilerPath.Resolve(link, relative);
                        result.Add(new(file, InNodeModules(file), fileName == reference));
                        filterIgnored = true;
                    }
                }
            }
            if (directory == host.GlobalTypingsCache)
                break;
        }
        if (preferSymlinks)
            AddTargets();
        return result.AsReadOnly();

        void AddTargets()
        {
            foreach (string file in targets)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!(filterIgnored && Ignored(file)))
                    result.Add(new(file, InNodeModules(file), file == reference));
            }
        }
    }

    internal IReadOnlyList<ModuleSpecifierPath> AllPaths(string importingFile, string target, CancellationToken cancellation = default)
    {
        var paths = new Dictionary<string, ModuleSpecifierPath>(StringComparer.Ordinal);
        foreach (var path in EachPath(importingFile, target, true, cancellation))
            paths[path.FileName] = path;
        var result = new List<ModuleSpecifierPath>();
        foreach (string directory in PackageJsonCache.Ancestors(CompilerPath.DirectoryName(importingFile)))
        {
            cancellation.ThrowIfCancellationRequested();
            if (paths.Count == 0)
                break;
            string prefix = CompilerPath.EnsureTrailingSeparator(directory);
            var nearby = paths.Values.Where(p => p.FileName.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            nearby.Sort(ComparePaths);
            result.AddRange(nearby);
            foreach (var path in nearby)
                paths.Remove(path.FileName);
        }
        var remaining = paths.Values.ToList();
        remaining.Sort(ComparePaths);
        result.AddRange(remaining);
        return result.AsReadOnly();
    }

    private int ComparePaths(ModuleSpecifierPath left, ModuleSpecifierPath right)
    {
        int result = right.IsRedirect.CompareTo(left.IsRedirect);
        if (result != 0)
            return result;
        result = left.FileName.AsSpan().Count('/').CompareTo(right.FileName.AsSpan().Count('/'));
        if (result != 0)
            return result;
        int aRoot = CompilerPath.RootLength(left.FileName), bRoot = CompilerPath.RootLength(right.FileName);
        result = CompareText(Lower(left.FileName[..aRoot]), Lower(right.FileName[..bRoot]));
        if (result != 0)
            return result;
        string a = left.FileName[aRoot..], b = right.FileName[bRoot..];
        return host.FileSystem.CaseSensitive ? CompareText(a, b) : CompareText(Lower(a), Lower(b));
    }

    private bool StartsWithDirectory(string file, string directory)
    {
        if (directory.Length == 0)
            return false;
        if (!host.FileSystem.CaseSensitive)
        {
            file = Lower(file, fileName: true);
            directory = Lower(directory, fileName: true);
        }
        directory = directory.TrimEnd('/', '\\');
        return file.StartsWith(directory + "/", StringComparison.Ordinal) || file.StartsWith(directory + "\\", StringComparison.Ordinal);
    }

    // Path identity preserves dotted I; path ordering uses ordinary simple lowercase.
    private static string Lower(string text, bool fileName = false) => !text.AsSpan().ContainsAnyExceptInRange(
        '\0',
        '\x7f') ? text.ToLowerInvariant()
        : string.Concat(GoUnicode.Runes(text).Select(c => char.ConvertFromUtf32(fileName && c == 0x130 ? c : GoUnicode.Lower(c))));

    private static int CompareText(string a, string b) => Wtf8.Encode(a).AsSpan().SequenceCompareTo(Wtf8.Encode(b));

    internal static string CanonicalFileName(string path, bool caseSensitive) => caseSensitive ? path : Lower(path, fileName: true);

    internal static bool Ignored(string path) => path.Contains("/node_modules/.", StringComparison.Ordinal)
            || path.Contains("/.git", StringComparison.Ordinal) || path.Contains(".#", StringComparison.Ordinal);
}

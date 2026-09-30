using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Resolution;

internal sealed partial class ModuleSpecifierGenerator
{
    internal IReadOnlyList<ModuleSpecifierPath> EachPath(Utf8String importingFile, Utf8String target, bool preferSymlinks = true,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Utf8String reference = host.ProjectOutput(CompilerPath.Resolve(host.CurrentDirectory, target));
        var names = new List<Utf8String>();
        if (reference.Length != 0)
            names.Add(reference);
        names.Add(target);
        names.AddRange(host.RedirectTargets(CompilerPath.Resolve(host.CurrentDirectory, target)));
        Utf8String[] targets = names.Select(f => CompilerPath.Resolve(host.CurrentDirectory, f)).ToArray();
        bool filterIgnored = !targets.All(Ignored);
        var result = new List<ModuleSpecifierPath>();
        if (!preferSymlinks)
            AddTargets();
        foreach (Utf8String directory in PackageJsonCache.Ancestors(
            CompilerPath.DirectoryName(CompilerPath.Resolve(host.CurrentDirectory, target))))
        {
            cancellation.ThrowIfCancellationRequested();
            var symlinks = host.SymlinkDirectories(directory);
            if (symlinks.Count != 0)
            {
                if (StartsWithDirectory(importingFile, directory))
                    break;
                foreach (Utf8String fileName in targets)
                {
                    if (!StartsWithDirectory(fileName, directory))
                        continue;
                    Utf8String relative = CompilerPath.Relative(directory, fileName, host.FileSystem.CaseSensitive);
                    foreach (Utf8String link in symlinks)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        Utf8String file = CompilerPath.Resolve(link, relative);
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
            foreach (Utf8String file in targets)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!(filterIgnored && Ignored(file)))
                    result.Add(new(file, InNodeModules(file), file == reference));
            }
        }
    }

    internal IReadOnlyList<ModuleSpecifierPath> AllPaths(Utf8String importingFile, Utf8String target, CancellationToken cancellation = default)
    {
        var paths = new Dictionary<Utf8String, ModuleSpecifierPath>(Utf8StringComparer.Ordinal);
        foreach (var path in EachPath(importingFile, target, true, cancellation))
            paths[path.FileName] = path;
        var result = new List<ModuleSpecifierPath>();
        foreach (Utf8String directory in PackageJsonCache.Ancestors(CompilerPath.DirectoryName(importingFile)))
        {
            cancellation.ThrowIfCancellationRequested();
            if (paths.Count == 0)
                break;
            Utf8String prefix = CompilerPath.EnsureTrailingSeparator(directory);
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
        result = left.FileName.AsSpan().Count((byte)'/').CompareTo(right.FileName.AsSpan().Count((byte)'/'));
        if (result != 0)
            return result;
        int aRoot = CompilerPath.RootLength(left.FileName), bRoot = CompilerPath.RootLength(right.FileName);
        result = CompareText(Lower(left.FileName[..aRoot]), Lower(right.FileName[..bRoot]));
        if (result != 0)
            return result;
        Utf8String a = left.FileName[aRoot..], b = right.FileName[bRoot..];
        return host.FileSystem.CaseSensitive ? CompareText(a, b) : CompareText(Lower(a), Lower(b));
    }

    private bool StartsWithDirectory(Utf8String file, Utf8String directory)
    {
        if (directory.Length == 0)
            return false;
        if (!host.FileSystem.CaseSensitive)
        {
            file = Lower(file, fileName: true);
            directory = Lower(directory, fileName: true);
        }
        directory = directory.TrimEnd((byte)'/', (byte)'\\');
        return file.StartsWith(directory + "/"u8, StringComparison.Ordinal) || file.StartsWith(directory + "\\"u8, StringComparison.Ordinal);
    }

    // Path identity preserves dotted I; path ordering uses ordinary simple lowercase.
    private static Utf8String Lower(Utf8String text, bool fileName = false) => !text.AsSpan().ContainsAnyExceptInRange(
        (byte)'\0',
        (byte)'\x7f') ? text.ToLowerInvariant()
        : Utf8String.Concat(GoUnicode.Runes(text).Select(c => Utf8String.FromCodePoint(fileName && c == 0x130 ? c : GoUnicode.Lower(c))));

    private static int CompareText(Utf8String a, Utf8String b) => a.Span.SequenceCompareTo(b.Span);

    internal static Utf8String CanonicalFileName(Utf8String path, bool caseSensitive) => caseSensitive ? path : Lower(path, fileName: true);

    internal static bool Ignored(Utf8String path) => path.Contains("/node_modules/."u8, StringComparison.Ordinal)
            || path.Contains("/.git"u8, StringComparison.Ordinal) || path.Contains(".#"u8, StringComparison.Ordinal);
}

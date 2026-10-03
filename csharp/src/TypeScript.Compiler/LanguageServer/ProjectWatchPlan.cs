using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;

namespace TypeScript.Compiler.LanguageServer;

internal sealed record ProjectWatchGroup(Utf8String Name, LspWatchPattern[] Patterns);

// Plans own strings only: slow client registrations must not retain project snapshots or checkers.
internal static class ProjectWatchPlan
{
    internal static ProjectWatchGroup[] Create(ProjectWorkspaceSnapshot snapshot, bool relativePatterns)
    {
        var options = snapshot.Host.Options;
        var workspace = options.CurrentDirectory;
        bool caseSensitive = snapshot.FileSystem.CaseSensitive;
        List<ProjectWatchGroup> groups = [];
        foreach (var config in snapshot.Configurations.Values)
            groups.Add(new("root files for "u8 + config.FileName, Config(config, workspace, caseSensitive)));
        foreach (var project in snapshot.Projects)
        {
            var directory = project.Kind == ProjectKind.Configured ? CompilerPath.DirectoryName(project.Configuration.FileName) : workspace;
            if (project.Resource is { } resource)
            {
                groups.Add(new("program files for "u8 + project.Id,
                    Resolution(resource.ObservedFiles, workspace, options.DefaultLibraryDirectory, directory, caseSensitive, relativePatterns)));
                groups.Add(new("content mapper configuration files for "u8 + project.Id,
                    ExactFiles(resource.MapperWatchedFiles, workspace, caseSensitive, relativePatterns)));
            }
            if (!options.TypingsLocation.IsEmpty)
                groups.Add(new("typings installer files for "u8 + project.Id,
                    Typings(project.TypingsFilesToWatch, options.TypingsLocation, workspace, caseSensitive, relativePatterns)));
        }
        groups.Add(new("auto-import"u8, Patterns(snapshot.WatchedNodeModules.Select(Recursive), [], relativePatterns)));
        return groups.ToArray();
    }

    internal static LspWatchPattern[] Resolution(IEnumerable<Utf8String> files, Utf8String workspace, Utf8String library,
        Utf8String currentDirectory, bool caseSensitive, bool relativePatterns)
    {
        workspace = Canonical(workspace); currentDirectory = Canonical(currentDirectory);
        library = Canonical(library.IsEmpty ? currentDirectory : library);
        HashSet<Utf8String> globs = [], external = [], seen = [];
        foreach (var path in files.Select(Canonical))
        {
            if (CompilerPath.IsDynamic(path) || !seen.Add(CompilerPath.DirectoryName(path))) continue;
            if (Contains(workspace, path)) globs.Add(Recursive(workspace));
            else if (Contains(currentDirectory, path)) globs.Add(Recursive(currentDirectory));
            else if (Contains(library, path)) globs.Add(Recursive(library));
            else if (path.IndexOf("/node_modules/"u8) is >= 0 and var index) globs.Add(Recursive(path[..(index + 13)]));
            else external.Add(CompilerPath.DirectoryName(path));
        }
        return Patterns(globs, CommonParents(external, true), relativePatterns);
        Utf8String Canonical(Utf8String path) => caseSensitive ? path : path.ToLowerInvariant();
        static bool Contains(Utf8String root, Utf8String path) => CompilerPath.Contains(root, path, true);
    }

    internal static LspWatchPattern[] Config(ParsedConfig config, Utf8String workspace, bool caseSensitive)
    {
        var directory = CompilerPath.DirectoryName(config.FileName);
        bool includeWorkspace = false, includeDirectory = false;
        List<Utf8String> external = [], globs = [];
        foreach (var path in config.WildcardDirectories.Keys) Include(path, path);
        foreach (var file in config.LiteralFiles) Include(file, CompilerPath.DirectoryName(file));
        if (includeWorkspace) globs.Add(Recursive(workspace));
        if (includeDirectory) globs.Add(Recursive(directory));
        foreach (var file in config.ExtendedConfigFiles)
            if (!includeWorkspace || !CompilerPath.Contains(workspace, file, caseSensitive)) globs.Add(file);
        globs.AddRange(CommonParents(external, caseSensitive).Select(Recursive));
        return Patterns(globs, [], false);
        void Include(Utf8String path, Utf8String outsideDirectory)
        {
            if (CompilerPath.Contains(workspace, path, caseSensitive)) includeWorkspace = true;
            else if (CompilerPath.Contains(directory, path, caseSensitive)) includeDirectory = true;
            else external.Add(outsideDirectory);
        }
    }

    internal static LspWatchPattern[] ExactFiles(IEnumerable<Utf8String> files, Utf8String workspace, bool caseSensitive, bool relativePatterns)
    {
        List<Utf8String> globs = [], external = [];
        foreach (var file in files)
            if (CompilerPath.Contains(workspace, file, caseSensitive)) globs.Add(file);
            else external.Add(CompilerPath.DirectoryName(file));
        return Patterns(globs, external, relativePatterns);
    }

    internal static LspWatchPattern[] Typings(IEnumerable<Utf8String> files, Utf8String typings, Utf8String workspace, bool caseSensitive, bool relativePatterns)
    {
        List<Utf8String> globs = [], external = [];
        foreach (var file in files)
            if (CompilerPath.Contains(typings, file, caseSensitive)) globs.Add(Recursive(typings));
            else if (CompilerPath.Contains(workspace, file, caseSensitive)) globs.Add(Recursive(workspace));
            else external.Add(CompilerPath.DirectoryName(file));
        return Patterns(globs, CommonParents(external, caseSensitive), relativePatterns);
    }

    private static LspWatchPattern[] Patterns(IEnumerable<Utf8String> inside, IEnumerable<Utf8String> outside, bool relativePatterns) =>
        inside.Distinct().Order().Select(glob => new LspWatchPattern(glob)).Concat(outside.Distinct().Order().Select(directory => relativePatterns
            ? new LspWatchPattern("**/*"u8, DocumentUris.FromFileName(directory)) : new LspWatchPattern(Recursive(directory)))).ToArray();

    internal static Utf8String Recursive(Utf8String directory) => (directory.EndsWith((byte)'/') ? directory[..^1] : directory) + "/**/*"u8;

    internal static Utf8String[] Components(Utf8String path)
    {
        path = CompilerPath.Normalize(path);
        int root = CompilerPath.RootLength(path);
        List<Utf8String> parts = [path[..root]];
        foreach (var part in path.AsSpan(root).Split((byte)'/'))
            if (part.Start.Value != part.End.Value) parts.Add(Utf8String.Copy(path.AsSpan(root)[part]));
        if (parts.Count <= 1) return parts.ToArray();
        int group = parts[0].StartsWith("//"u8) ? 2
            : (parts[0].Length == 3 && parts[0][1] == ':' ? parts[1].Equals("users"u8, StringComparison.OrdinalIgnoreCase)
                : parts[1] == "home"u8) ? Math.Min(3, parts.Count) : 1;
        var result = parts.Skip(group - 1).ToArray();
        result[0] = parts[0];
        for (int i = 1; i < group; i++) result[0] = CompilerPath.Combine(result[0], parts[i]);
        return result;
    }

    internal static Utf8String[] CommonParents(IEnumerable<Utf8String> directories, bool caseSensitive)
    {
        var paths = directories.ToArray();
        if (paths.Length == 1) return Components(paths[0]).Length >= 2 ? paths : [];
        var comparer = caseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
        var groups = paths.Select(Components).Where(parts => parts.Length >= 2).GroupBy(parts => parts[0], comparer);
        List<Utf8String> result = [];
        foreach (var roots in groups)
            foreach (var branch in roots.GroupBy(parts => parts[1], comparer))
            {
                var first = branch.First(); int count = first.Length;
                foreach (var parts in branch)
                {
                    count = Math.Min(count, parts.Length);
                    for (int index = 2; index < count; index++)
                        if (!comparer.Equals(first[index], parts[index])) { count = index; break; }
                }
                var path = first[0];
                for (int index = 1; index < count; index++) path = CompilerPath.Combine(path, first[index]);
                result.Add(path);
            }
        return result.Order().ToArray();
    }
}

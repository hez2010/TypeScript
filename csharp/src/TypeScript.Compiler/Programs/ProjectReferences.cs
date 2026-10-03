using System.Collections.Concurrent;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;

namespace TypeScript.Compiler.Programs;

public sealed record ProjectFileRedirect(Utf8String Source, Utf8String Output, ParsedConfig Project);

public sealed class ProjectReferences
{
    private readonly IFileSystem fs;
    private readonly bool preserveSymlinks;
    private readonly Dictionary<Utf8String, ParsedConfig> projects;
    private readonly Dictionary<Utf8String, ProjectFileRedirect> sources, outputs;
    private readonly Dictionary<Utf8String, Utf8String[]> references;
    private readonly ConcurrentDictionary<Utf8String, Utf8String> outputAliases;
    private readonly HashSet<Utf8String> declarationDirectories;
    public IReadOnlyDictionary<Utf8String, ParsedConfig> Projects => projects;
    public IReadOnlyDictionary<Utf8String, ProjectFileRedirect> Sources => sources;
    public IReadOnlyDictionary<Utf8String, ProjectFileRedirect> Outputs => outputs;
    public IReadOnlyDictionary<Utf8String, Utf8String[]> References => references;
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool UseSources { get; }

    public ProjectReferences(
        IFileSystem fileSystem,
        Utf8String currentDirectory,
        ParsedConfig root,
        bool useSources,
        CancellationToken cancellation = default)
    {
        fs = fileSystem;
        preserveSymlinks = root.Options.PreserveSymlinks == true;
        UseSources = useSources && root.Options.DisableSourceOfProjectReferenceRedirect != true;
        var comparer = fs.CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
        projects = new(comparer);
        sources = new(comparer);
        outputs = new(comparer);
        references = new(comparer);
        outputAliases = new(comparer);
        declarationDirectories = new(comparer);
        var errors = new List<Diagnostic>();
        var parser = new ConfigParser(fs, currentDirectory);
        var stack = new Stack<ParsedConfig>();
        stack.Push(root);
        var visited = new HashSet<Utf8String>(comparer);
        while (stack.TryPop(out var config))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(config.FileName))
                continue;
            projects[config.FileName] = config;
            var children = new List<ParsedConfig>();
            var paths = new List<Utf8String>();
            foreach (var reference in config.References)
            {
                Diagnostic ReferenceError(DiagnosticMessage message) => new(message, reference.SourceStart, reference.SourceLength, [reference.Path])
                { FileName = config.SourceFile is null ? null : config.FileName };
                Utf8String path = reference.Path;
                if (!path.EndsWith(".json"u8, StringComparison.OrdinalIgnoreCase))
                    path = CompilerPath.Combine(path, Utf8Literals.TsconfigJson);
                paths.Add(path);
                if (!fs.FileExists(path))
                {
                    errors.Add(ReferenceError(Messages.File_0_not_found));
                    continue;
                }
                var child = projects.GetValueOrDefault(path) ?? parser.Parse(path, cancellation: cancellation);
                if (config.FileNames.Length != 0)
                {
                    if (child.Options.Composite != true)
                        errors.Add(ReferenceError(Messages.Referenced_project_0_must_have_setting_composite_Colon_true));
                    if (child.Options.NoEmit == true)
                        errors.Add(ReferenceError(Messages.Referenced_project_0_may_not_disable_emit));
                }
                projects[path] = child;
                children.Add(child);
            }
            references[config.FileName] = paths.ToArray();
            if (config != root)
            {
                if ((config.Options.DeclarationDir ?? config.Options.OutDir) is { IsEmpty: false } declarationDirectory)
                    declarationDirectories.Add(declarationDirectory);
                Utf8String rootDir = config.Options.RootDir ?? (config.Options.Composite == true
                    ? CompilerPath.DirectoryName(config.FileName) : CommonDirectory(
                        config.FileNames.Where(f => !CompilerPath.IsDeclarationFile(f)),
                        fs.CaseSensitive));
                foreach (Utf8String source in config.FileNames)
                {
                    if (CompilerPath.IsDeclarationFile(source) || ModuleResolver.Extension(source) == Utf8Literals.Json)
                    {
                        sources[source] = new(source, default, config);
                        continue;
                    }
                    Utf8String ext = ModuleResolver.Extension(source);
                    Utf8String suffix = (ext == ".mts"u8 || ext == ".mjs"u8) ? Utf8Literals.DMts : (ext == ".cts"u8 || ext == ".cjs"u8) ? Utf8Literals.DCts : Utf8Literals.DTs;
                    Utf8String output = source[..^ext.Length] + suffix;
                    if ((config.Options.DeclarationDir ?? config.Options.OutDir) is { } folder)
                        output = CompilerPath.Resolve(folder, CompilerPath.Relative(rootDir, output, fs.CaseSensitive));
                    var redirect = new ProjectFileRedirect(source, output, config);
                    sources[source] = redirect;
                    outputs[output] = redirect;
                }
            }
            for (int i = children.Count - 1; i >= 0; i--)
                stack.Push(children[i]);
        }
        Diagnostics = errors.ToArray();
    }

    public ProjectFileRedirect? Find(Utf8String path) => sources.GetValueOrDefault(path) ?? outputs.GetValueOrDefault(path)
        ?? (preserveSymlinks && path.Contains("/node_modules/"u8, StringComparison.Ordinal)
            ? outputs.GetValueOrDefault(outputAliases.GetValueOrDefault(path, OutputRealPath(path))) : null);

    private Utf8String OutputRealPath(Utf8String path)
    {
        var real = CompilerPath.Normalize(fs.RealPath(path));
        if (!outputs.Comparer.Equals(path, real)) return real;
        int start = path.LastIndexOf("/node_modules/"u8);
        if (start < 0) return path;
        start += "/node_modules/"u8.Length;
        if (start == path.Length) return path;
        int end = path.IndexOf((byte)'/', start);
        if (end >= 0 && path[start] == '@') end = path.IndexOf((byte)'/', end + 1);
        if (end < 0) return path;
        var root = path[..end];
        if (!fs.DirectoryExists(root)) return path;
        var target = fs.RealPath(root);
        return outputs.Comparer.Equals(root, target) ? path : CompilerPath.Normalize(target + path[end..]);
    }

    public Utf8String Redirect(Utf8String path)
    {
        if (!UseSources)
            return sources.TryGetValue(path, out var source) && !source.Output.IsEmpty ? source.Output : path;
        return Find(path)?.Source ?? path;
    }

    public static Utf8String CommonDirectory(IEnumerable<Utf8String> files, bool sensitive)
    {
        Utf8String? common = null;
        foreach (Utf8String file in files)
        {
            Utf8String directory = CompilerPath.DirectoryName(file);
            if (common is null)
            {
                common = directory;
                continue;
            }
            while (!CompilerPath.Contains(common.Value, directory, sensitive))
            {
                Utf8String parent = CompilerPath.DirectoryName(common.Value);
                if (parent == common)
                    return Utf8String.Empty;
                common = parent;
            }
        }
        return common ?? Utf8String.Empty;
    }

    internal IFileSystem ResolutionFileSystem() => UseSources ? new OutputFileSystem(fs, this) : fs;

    private sealed class OutputFileSystem(IFileSystem fs, ProjectReferences references) : IFileSystem
    {
        public bool CaseSensitive => fs.CaseSensitive;

        public bool FileExists(Utf8String path)
        {
            if (fs.FileExists(path)) return true;
            if (!CompilerPath.IsDeclarationFile(path)) return false;
            var real = references.OutputRealPath(path);
            var redirect = references.outputs.GetValueOrDefault(path) ?? references.outputs.GetValueOrDefault(real);
            if (redirect is null || !fs.FileExists(redirect.Source)) return false;
            if (!references.outputs.Comparer.Equals(real, path)) references.outputAliases[path] = real;
            return true;
        }

        public bool DirectoryExists(Utf8String path) => fs.DirectoryExists(path)
                    || references.declarationDirectories.Any(directory =>
                        CompilerPath.Contains(directory, references.OutputRealPath(path), CaseSensitive)
                        || CompilerPath.Contains(references.OutputRealPath(path), directory, CaseSensitive));

        public byte[]? ReadFile(Utf8String path) => fs.ReadFile(path);

        public DirectoryEntries GetAccessibleEntries(Utf8String path) => fs.GetAccessibleEntries(path);

        public FileEntry? Stat(Utf8String path) => fs.Stat(path);

        public Utf8String RealPath(Utf8String path) => references.outputAliases.GetValueOrDefault(path, fs.RealPath(path));

        public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents) =>
            throw new NotSupportedException("Resolution filesystem is read only");

        public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents) =>
            throw new NotSupportedException("Resolution filesystem is read only");

        public void Remove(Utf8String path) => throw new NotSupportedException("Resolution filesystem is read only");

        public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc) =>
            throw new NotSupportedException("Resolution filesystem is read only");
    }
}

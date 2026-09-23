using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;

namespace TypeScript.Compiler.Programs;

public sealed record ProjectFileRedirect(string Source, string Output, ParsedConfig Project);

public sealed class ProjectReferences
{
    private readonly IFileSystem fs;
    private readonly bool preserveSymlinks;
    private readonly Dictionary<string, ParsedConfig> projects;
    private readonly Dictionary<string, ProjectFileRedirect> sources, outputs;
    private readonly Dictionary<string, string[]> references;
    public IReadOnlyDictionary<string, ParsedConfig> Projects => projects;
    public IReadOnlyDictionary<string, ProjectFileRedirect> Sources => sources;
    public IReadOnlyDictionary<string, ProjectFileRedirect> Outputs => outputs;
    public IReadOnlyDictionary<string, string[]> References => references;
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool UseSources { get; }

    public ProjectReferences(
        IFileSystem fileSystem,
        string currentDirectory,
        ParsedConfig root,
        bool useSources,
        CancellationToken cancellation = default)
    {
        fs = fileSystem;
        preserveSymlinks = root.Options.Boolean("preserveSymlinks") == true;
        UseSources = useSources && root.Options.Boolean("disableSourceOfProjectReferenceRedirect") != true;
        var comparer = fs.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        projects = new(comparer);
        sources = new(comparer);
        outputs = new(comparer);
        references = new(comparer);
        var errors = new List<Diagnostic>();
        var parser = new ConfigParser(fs, currentDirectory);
        var stack = new Stack<ParsedConfig>();
        stack.Push(root);
        var visited = new HashSet<string>(comparer);
        while (stack.TryPop(out var config))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(config.FileName))
                continue;
            projects[config.FileName] = config;
            var children = new List<ParsedConfig>();
            var paths = new List<string>();
            foreach (var reference in config.References)
            {
                string path = reference.Path;
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    path = CompilerPath.Combine(path, "tsconfig.json");
                paths.Add(path);
                if (!fs.FileExists(path))
                {
                    errors.Add(new(Messages.File_0_not_found, 0, 0, [path]));
                    continue;
                }
                var child = projects.GetValueOrDefault(path) ?? parser.Parse(path, cancellation: cancellation);
                if (config.FileNames.Length != 0)
                {
                    if (child.Options.Boolean("composite") != true)
                        errors.Add(new(Messages.Referenced_project_0_must_have_setting_composite_Colon_true, 0, 0, [reference.Path]));
                    if (child.Options.Boolean("noEmit") == true)
                        errors.Add(new(Messages.Referenced_project_0_may_not_disable_emit, 0, 0, [reference.Path]));
                }
                projects[path] = child;
                children.Add(child);
            }
            references[config.FileName] = paths.ToArray();
            if (config != root)
            {
                string rootDir = config.Options.String("rootDir") ?? (config.Options.Boolean("composite") == true
                    ? CompilerPath.DirectoryName(config.FileName) : CommonDirectory(
                        config.FileNames.Where(f => !CompilerPath.IsDeclarationFile(f)),
                        fs.CaseSensitive));
                foreach (string source in config.FileNames)
                {
                    if (CompilerPath.IsDeclarationFile(source) || ModuleResolver.Extension(source) == ".json")
                        continue;
                    string ext = ModuleResolver.Extension(source);
                    string suffix = ext is ".mts" or ".mjs" ? ".d.mts" : ext is ".cts" or ".cjs" ? ".d.cts" : ".d.ts";
                    string output = source[..^ext.Length] + suffix;
                    if ((config.Options.String("declarationDir") ?? config.Options.String("outDir")) is { } folder)
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

    public ProjectFileRedirect? Find(string path) => sources.GetValueOrDefault(path) ?? outputs.GetValueOrDefault(path)
        ?? (preserveSymlinks && path.Contains("/node_modules/", StringComparison.Ordinal)
            ? outputs.GetValueOrDefault(CompilerPath.Normalize(fs.RealPath(path))) : null);

    public string Redirect(string path)
    {
        if (!UseSources)
            return sources.TryGetValue(path, out var source) ? source.Output : path;
        return Find(path)?.Source ?? path;
    }

    public static string CommonDirectory(IEnumerable<string> files, bool sensitive)
    {
        string? common = null;
        foreach (string file in files)
        {
            string directory = CompilerPath.DirectoryName(file);
            if (common is null)
            {
                common = directory;
                continue;
            }
            while (!CompilerPath.Contains(common, directory, sensitive))
            {
                string parent = CompilerPath.DirectoryName(common);
                if (parent == common)
                    return "";
                common = parent;
            }
        }
        return common ?? "";
    }

    internal IFileSystem ResolutionFileSystem() => UseSources ? new OutputFileSystem(fs, this) : fs;

    private sealed class OutputFileSystem(IFileSystem fs, ProjectReferences references) : IFileSystem
    {
        public bool CaseSensitive => fs.CaseSensitive;

        public bool FileExists(string path) => fs.FileExists(path) || CompilerPath.IsDeclarationFile(path)
                    && (references.outputs.GetValueOrDefault(path) ?? references.outputs.GetValueOrDefault(CompilerPath.Normalize(fs.RealPath(path)))) is { } redirect
                    && fs.FileExists(redirect.Source);

        public bool DirectoryExists(string path) => fs.DirectoryExists(path)
                    || references.outputs.Values.Any(
                        r => CompilerPath.Contains(CompilerPath.Normalize(fs.RealPath(path)), r.Output, CaseSensitive)
                            && fs.FileExists(r.Source));

        public byte[]? ReadFile(string path) => fs.ReadFile(path);

        public DirectoryEntries GetAccessibleEntries(string path) => fs.GetAccessibleEntries(path);

        public FileEntry? Stat(string path) => fs.Stat(path);

        public string RealPath(string path) => fs.RealPath(path);

        public void WriteFile(string path, ReadOnlySpan<byte> contents) =>
            throw new NotSupportedException("Resolution filesystem is read only");

        public void AppendFile(string path, ReadOnlySpan<byte> contents) =>
            throw new NotSupportedException("Resolution filesystem is read only");

        public void Remove(string path) => throw new NotSupportedException("Resolution filesystem is read only");

        public void SetTimes(string path, DateTime accessTimeUtc, DateTime writeTimeUtc) =>
            throw new NotSupportedException("Resolution filesystem is read only");
    }
}

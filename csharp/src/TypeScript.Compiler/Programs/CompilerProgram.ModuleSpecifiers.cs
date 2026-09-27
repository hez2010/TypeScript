using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Programs;

public sealed partial class CompilerProgram
{
    private readonly object moduleSpecifierGate = new();
    private ProgramModuleSpecifierHost? moduleSpecifierHost;
    private ModuleSpecifierGenerator? moduleSpecifierGenerator;

    internal ModuleSpecifierResult GetModuleSpecifiers(SourceFileNode source, string target, ModuleSpecifierPreferences? preferences = null,
        ReferenceResolutionMode mode = 0, CancellationToken cancellation = default)
    {
        if (!ReferenceEquals(GetFile(source.FileName)?.Syntax, source))
            throw new ArgumentException("Source belongs to another program", nameof(source));
        lock (moduleSpecifierGate)
        {
            var host = ModuleSpecifierHost(cancellation);
            string original = ProjectReferences.Outputs.TryGetValue(CompilerPath.Resolve(CurrentDirectory, target), out var output)
                ? output.Source : target;
            return moduleSpecifierGenerator!.ForFile(source, original, preferences, mode, cancellation: cancellation);
        }
    }

    internal IReadOnlyList<ModuleSpecifierPath> GetModuleSpecifierPaths(
        SourceFileNode source,
        string target,
        CancellationToken cancellation = default)
    {
        if (!ReferenceEquals(GetFile(source.FileName)?.Syntax, source))
            throw new ArgumentException("Source belongs to another program", nameof(source));
        lock (moduleSpecifierGate)
        {
            var host = ModuleSpecifierHost(cancellation);
            return moduleSpecifierGenerator!.AllPaths(host.OriginalSourceFileName(source), target, cancellation);
        }
    }

    private ProgramModuleSpecifierHost ModuleSpecifierHost(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (moduleSpecifierHost is null)
        {
            var host = new ProgramModuleSpecifierHost(this, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var generator = new ModuleSpecifierGenerator(host, Configuration.Options);
            moduleSpecifierHost = host;
            moduleSpecifierGenerator = generator;
        }
        return moduleSpecifierHost;
    }

    internal bool SourceFileMayBeEmitted(SourceFileNode source)
    {
        var options = Configuration.Options;
        if (options.Boolean("noEmitForJsFiles") == true && source.ScriptKind is ScriptKind.JS or ScriptKind.JSX
            || source.IsDeclarationFile || externalLibraryFiles.Contains(source.FileName)
            || ProjectReferences.Sources.ContainsKey(source.FileName))
            return false;
        if (GetFile(source.FileName)?.Mapping is not null && options.Boolean("declaration") != true && options.Boolean("composite") != true)
            return false;
        if (source.ScriptKind != ScriptKind.JSON)
            return true;
        if (options.String("outDir") is not { Length: > 0 } outputDirectory)
            return false;
        string? root = options.String("rootDir") ?? (Configuration.FileName.Length == 0
            ? null
            : CompilerPath.DirectoryName(Configuration.FileName));
        if (root is not null)
        {
            string output = CompilerPath.Resolve(
                outputDirectory,
                CompilerPath.Relative(CompilerPath.Resolve(CurrentDirectory, root), source.FileName, fileSystem.CaseSensitive));
            if (CompilerPath.Relative(source.FileName, output, fileSystem.CaseSensitive).Length == 0)
                return false;
        }
        return true;
    }

    private sealed class ProgramModuleSpecifierHost : IModuleSpecifierHost
    {
        private readonly CompilerProgram program;
        private readonly Dictionary<string, IReadOnlyList<string>> redirects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<string>> links = new(StringComparer.Ordinal);
        public IFileSystem FileSystem => program.fileSystem;
        public string CurrentDirectory => program.CurrentDirectory;
        public string ConfigFileName => program.Configuration.FileName;
        public string CommonSourceDirectory => program.CommonSourceDirectory;
        public string GlobalTypingsCache => program.GlobalTypingsCache;
        public IReadOnlyList<string> ContentMapperExtensions { get; }

        internal ProgramModuleSpecifierHost(CompilerProgram program, CancellationToken cancellation)
        {
            this.program = program;
            ContentMapperExtensions = Array.AsReadOnly(
                program.Configuration.ContentMappers.SelectMany(m => m.Extensions).Distinct().ToArray());
            var redirectLists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var entry in program.Redirects)
            {
                cancellation.ThrowIfCancellationRequested();
                if (program.ProjectReferences.Find(entry.Key) is { } reference
                    && (Key(reference.Source) == Key(entry.Value) || Key(reference.Output) == Key(entry.Value)))
                    continue;
                string key = Key(entry.Value);
                if (!redirectLists.TryGetValue(key, out var list))
                    redirectLists[key] = list = [];
                list.Add(entry.Key);
            }
            foreach (var entry in redirectLists)
                redirects[entry.Key] = entry.Value.AsReadOnly();

            var linkLists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in program.files.Values)
                foreach (var resolution in file.Resolutions)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Process(resolution.Resolution.OriginalPath, resolution.Resolution.FileName);
                }
            var resolver = new ModuleResolver(
                FileSystem,
                program.Configuration.Options,
                CurrentDirectory,
                ConfigFileName,
                GlobalTypingsCache);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in program.SourceFiles)
            {
                cancellation.ThrowIfCancellationRequested();
                if (file.PackageDirectory.Length == 0
                    || !program.SourceFileMayBeEmitted(file.Syntax)
                    || !seen.Add(Key(file.PackageDirectory)))
                    continue;
                if (resolver.Packages.Get(file.PackageDirectory).Contents is not { } package)
                    continue;
                foreach (string dependency in package.RuntimeDependencies())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (known.Contains(Key(CompilerPath.Combine(file.PackageDirectory, "node_modules", dependency))))
                        continue;
                    if (!dependency.StartsWith("@types", StringComparison.Ordinal)
                        && known.Contains(
                            Key(CompilerPath.Combine(file.PackageDirectory, "node_modules/@types", ModuleResolver.Mangle(dependency)))))
                        continue;
                    if (resolver.ResolvePackageDirectoryInfo(
                        dependency,
                        CompilerPath.Combine(file.PackageDirectory, "package.json")) is { OriginalPath.Length: > 0 } resolved)
                        Process(
                            CompilerPath.Combine(resolved.OriginalPath, "package.json"),
                            CompilerPath.Combine(resolved.FileName, "package.json"));
                }
            }
            foreach (var entry in linkLists)
                links[entry.Key] = entry.Value.AsReadOnly();

            void Process(string original, string resolved)
            {
                if (original.Length == 0 || resolved.Length == 0)
                    return;
                string a = CompilerPath.Resolve(CurrentDirectory, resolved), b = CompilerPath.Resolve(CurrentDirectory, original);
                bool directory = false;
                while (a.Length > CompilerPath.RootLength(a) && b.Length > CompilerPath.RootLength(b))
                {
                    string aParent = CompilerPath.DirectoryName(a), bParent = CompilerPath.DirectoryName(b);
                    if (Boundary(CompilerPath.BaseName(aParent)) || Boundary(CompilerPath.BaseName(bParent))
                        || Canonical(CompilerPath.BaseName(a)) != Canonical(CompilerPath.BaseName(b)))
                        break;
                    a = aParent;
                    b = bParent;
                    directory = true;
                }
                if (!directory || ModuleSpecifierGenerator.Ignored(Key(b)) || !known.Add(Key(b)))
                    return;
                string key = Key(a);
                if (!linkLists.TryGetValue(key, out var list))
                    linkLists[key] = list = [];
                list.Add(b);
            }
            bool Boundary(string name) => name.Length != 0 && (Canonical(name) == "node_modules" || name.StartsWith('@'));
        }

        private string Canonical(string path) => ModuleSpecifierGenerator.CanonicalFileName(path, FileSystem.CaseSensitive);

        private string Key(string path) => Canonical(CompilerPath.Resolve(CurrentDirectory, path).TrimEnd('/'));

        public string OriginalSourceFileName(SourceFileNode source) =>
            program.ProjectReferences.Outputs.TryGetValue(source.FileName, out var reference) ? reference.Source : source.FileName;

        public string ProjectOutput(string sourceFileName) =>
            program.ProjectReferences.Sources.TryGetValue(sourceFileName, out var reference) ? reference.Output : "";

        public IReadOnlyList<string> RedirectTargets(string target) => redirects.GetValueOrDefault(Key(target)) ?? [];

        public IReadOnlyList<string> SymlinkDirectories(string realDirectory) => links.GetValueOrDefault(Key(realDirectory)) ?? [];

        public ReferenceResolutionMode ResolutionMode(SourceFileNode source, SyntaxNode? import) =>
            program.ResolutionModeForUsage(source, import);

        public ResolvedModule? ResolvedImport(SourceFileNode source, SyntaxNode import)
        {
            TextSlice text = import is StringLiteralNode literal ? literal.Text : ((NoSubstitutionTemplateLiteralNode)import).Text;
            var mode = ResolutionMode(source, import);
            return program.GetFile(source.FileName)!.Resolutions.FirstOrDefault(
                r => !r.TypeReference && r.Specifier == text && r.Mode == mode)?.Resolution;
        }
    }
}

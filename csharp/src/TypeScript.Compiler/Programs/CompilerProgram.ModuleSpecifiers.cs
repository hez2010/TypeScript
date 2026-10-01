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

    internal ModuleSpecifierResult GetModuleSpecifiers(SourceFileNode source, Utf8String target, ModuleSpecifierPreferences? preferences = null,
        ReferenceResolutionMode mode = 0, CancellationToken cancellation = default)
    {
        if (!ReferenceEquals(GetFile(source.FileName)?.Syntax, source))
            throw new ArgumentException("Source belongs to another program", nameof(source));
        lock (moduleSpecifierGate)
        {
            var host = ModuleSpecifierHost(cancellation);
            Utf8String original = ProjectReferences.Outputs.TryGetValue(CompilerPath.Resolve(CurrentDirectory, target), out var output)
                ? output.Source : target;
            return moduleSpecifierGenerator!.ForFile(source, original, preferences, mode, cancellation: cancellation);
        }
    }

    internal IReadOnlyList<ModuleSpecifierPath> GetModuleSpecifierPaths(
        SourceFileNode source,
        Utf8String target,
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

    internal bool SourceFileMayBeEmitted(SourceFileNode source, bool forceDeclarations = false, bool forceJavaScript = false)
    {
        var options = Configuration.Options;
        if (!forceJavaScript && options.NoEmitForJsFiles == true && source.ScriptKind is ScriptKind.JS or ScriptKind.JSX
            || source.IsDeclarationFile || externalLibraryFiles.Contains(source.FileName))
            return false;
        if (GetFile(source.FileName)?.Mapping is not null && !forceDeclarations && options.Declaration != true && options.Composite != true)
            return false;
        if (forceDeclarations || forceJavaScript) return true;
        if (ProjectReferences.Sources.ContainsKey(source.FileName)) return false;
        if (source.ScriptKind != ScriptKind.JSON)
            return true;
        if (options.OutDir is not { Length: > 0 } outputDirectory)
            return false;
        Utf8String? root = options.RootDir ?? (Configuration.FileName.Length == 0
            ? (Utf8String?)null
            : CompilerPath.DirectoryName(Configuration.FileName));
        if (root is not null)
        {
            Utf8String output = CompilerPath.Resolve(
                outputDirectory,
                CompilerPath.Relative(CompilerPath.Resolve(CurrentDirectory, root.Value), source.FileName, fileSystem.CaseSensitive));
            if (CompilerPath.Relative(source.FileName, output, fileSystem.CaseSensitive).Length == 0)
                return false;
        }
        return true;
    }

    private sealed class ProgramModuleSpecifierHost : IModuleSpecifierHost
    {
        private readonly CompilerProgram program;
        private readonly Dictionary<Utf8String, IReadOnlyList<Utf8String>> redirects = new(Utf8StringComparer.Ordinal);
        private readonly Dictionary<Utf8String, IReadOnlyList<Utf8String>> links = new(Utf8StringComparer.Ordinal);
        public IFileSystem FileSystem => program.fileSystem;
        public Utf8String CurrentDirectory => program.CurrentDirectory;
        public Utf8String ConfigFileName => program.Configuration.FileName;
        public Utf8String CommonSourceDirectory => program.CommonSourceDirectory;
        public Utf8String GlobalTypingsCache => program.GlobalTypingsCache;
        public IReadOnlyList<Utf8String> ContentMapperExtensions { get; }

        internal ProgramModuleSpecifierHost(CompilerProgram program, CancellationToken cancellation)
        {
            this.program = program;
            ContentMapperExtensions = Array.AsReadOnly(
                program.Configuration.ContentMappers.SelectMany(m => m.Extensions).Distinct().ToArray());
            var redirectLists = new Dictionary<Utf8String, List<Utf8String>>(Utf8StringComparer.Ordinal);
            foreach (var entry in program.Redirects)
            {
                cancellation.ThrowIfCancellationRequested();
                if (program.ProjectReferences.Find(entry.Key) is { } reference
                    && (Key(reference.Source) == Key(entry.Value) || Key(reference.Output) == Key(entry.Value)))
                    continue;
                Utf8String key = Key(entry.Value);
                if (!redirectLists.TryGetValue(key, out var list))
                    redirectLists[key] = list = [];
                list.Add(entry.Key);
            }
            foreach (var entry in redirectLists)
                redirects[entry.Key] = entry.Value.AsReadOnly();

            var linkLists = new Dictionary<Utf8String, List<Utf8String>>(Utf8StringComparer.Ordinal);
            var known = new HashSet<Utf8String>(Utf8StringComparer.Ordinal);
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
            var seen = new HashSet<Utf8String>(Utf8StringComparer.Ordinal);
            foreach (var file in program.SourceFiles)
            {
                cancellation.ThrowIfCancellationRequested();
                if (file.PackageDirectory.Length == 0
                    || !program.SourceFileMayBeEmitted(file.Syntax)
                    || !seen.Add(Key(file.PackageDirectory)))
                    continue;
                if (resolver.Packages.Get(file.PackageDirectory).Contents is not { } package)
                    continue;
                foreach (Utf8String dependency in package.RuntimeDependencies())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (known.Contains(Key(CompilerPath.Combine(file.PackageDirectory, Utf8Literals.NodeModules, dependency))))
                        continue;
                    if (!dependency.StartsWith("@types"u8, StringComparison.Ordinal)
                        && known.Contains(
                            Key(CompilerPath.Combine(file.PackageDirectory, Utf8Literals.NodeModulesTypes, ModuleResolver.Mangle(dependency)))))
                        continue;
                    if (resolver.ResolvePackageDirectoryInfo(
                        dependency,
                        CompilerPath.Combine(file.PackageDirectory, Utf8Literals.PackageJson)) is { OriginalPath.Length: > 0 } resolved)
                        Process(
                            CompilerPath.Combine(resolved.OriginalPath, Utf8Literals.PackageJson),
                            CompilerPath.Combine(resolved.FileName, Utf8Literals.PackageJson));
                }
            }
            foreach (var entry in linkLists)
                links[entry.Key] = entry.Value.AsReadOnly();

            void Process(Utf8String original, Utf8String resolved)
            {
                if (original.Length == 0 || resolved.Length == 0)
                    return;
                Utf8String a = CompilerPath.Resolve(CurrentDirectory, resolved), b = CompilerPath.Resolve(CurrentDirectory, original);
                bool directory = false;
                while (a.Length > CompilerPath.RootLength(a) && b.Length > CompilerPath.RootLength(b))
                {
                    Utf8String aParent = CompilerPath.DirectoryName(a), bParent = CompilerPath.DirectoryName(b);
                    if (Boundary(CompilerPath.BaseName(aParent)) || Boundary(CompilerPath.BaseName(bParent))
                        || Canonical(CompilerPath.BaseName(a)) != Canonical(CompilerPath.BaseName(b)))
                        break;
                    a = aParent;
                    b = bParent;
                    directory = true;
                }
                if (!directory || ModuleSpecifierGenerator.Ignored(Key(b)) || !known.Add(Key(b)))
                    return;
                Utf8String key = Key(a);
                if (!linkLists.TryGetValue(key, out var list))
                    linkLists[key] = list = [];
                list.Add(b);
            }
            bool Boundary(Utf8String name) => name.Length != 0 && (Canonical(name) == Utf8Literals.NodeModules || name.StartsWith((byte)'@'));
        }

        private Utf8String Canonical(Utf8String path) => ModuleSpecifierGenerator.CanonicalFileName(path, FileSystem.CaseSensitive);

        private Utf8String Key(Utf8String path) => Canonical(CompilerPath.Resolve(CurrentDirectory, path).TrimEnd((byte)'/'));

        public Utf8String OriginalSourceFileName(SourceFileNode source) =>
            program.ProjectReferences.Outputs.TryGetValue(source.FileName, out var reference) ? reference.Source : source.FileName;

        public Utf8String ProjectOutput(Utf8String sourceFileName) =>
            program.ProjectReferences.Sources.TryGetValue(sourceFileName, out var reference) ? reference.Output : Utf8String.Empty;

        public IReadOnlyList<Utf8String> RedirectTargets(Utf8String target) => redirects.GetValueOrDefault(Key(target)) ?? [];

        public IReadOnlyList<Utf8String> SymlinkDirectories(Utf8String realDirectory) => links.GetValueOrDefault(Key(realDirectory)) ?? [];

        public ReferenceResolutionMode ResolutionMode(SourceFileNode source, SyntaxNode? import) =>
            program.ResolutionModeForUsage(source, import);

        public ResolvedModule? ResolvedImport(SourceFileNode source, SyntaxNode import)
        {
            Utf8String text = import is StringLiteralNode literal ? literal.Text : ((NoSubstitutionTemplateLiteralNode)import).Text;
            var mode = ResolutionMode(source, import);
            return program.GetFile(source.FileName)!.Resolutions.FirstOrDefault(
                r => !r.TypeReference && r.Specifier == text && r.Mode == mode)?.Resolution;
        }
    }
}

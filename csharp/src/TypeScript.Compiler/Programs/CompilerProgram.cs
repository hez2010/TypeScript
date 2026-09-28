using System.Collections.Concurrent;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Programs;

public enum FileIncludeKind
{
    Root,
    Import,
    PathReference,
    TypeReference,
    Library,
    AutomaticType,
    ProjectReference,
    MapperSupplemental
}
public sealed record FileIncludeReason(FileIncludeKind Kind, string FileName, string ContainingFile = "", int Position = 0, int Length = 0);
public sealed record ModuleReference(
    string Specifier,
    ReferenceResolutionMode Mode,
    SyntaxNode? Node,
    ResolvedModule Resolution,
    bool TypeReference = false,
    bool Augmentation = false);
public sealed record ProgramFile(SourceFileNode Syntax, BoundSourceFile Binding, ParseOptions ParseOptions,
    ReferenceResolutionMode ImpliedFormat, string PackageDirectory, string PackageType, IReadOnlyList<ModuleReference> Resolutions,
    IReadOnlyList<string> Dependencies, bool Library = false)
{
    public MappedSourceFile? Mapping { get; init; }
}

/// <summary>A published program owns a deterministic graph; rebuilding never mutates an older program.</summary>
public sealed partial class CompilerProgram
{
    private readonly Dictionary<string, ProgramFile> files;
    private readonly IFileSystem fileSystem;
    private readonly HashSet<string> externalLibraryFiles;
    internal string CurrentDirectory { get; }
    internal string GlobalTypingsCache { get; }
    internal string ModuleResolutionKind { get; }
    public IReadOnlyList<ProgramFile> SourceFiles { get; }
    public IReadOnlyList<string> RootFileNames { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    internal IReadOnlyList<Diagnostic> IncludeDiagnostics { get; }
    public IReadOnlyList<string> MissingFiles { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<FileIncludeReason>> IncludeReasons { get; }
    public IReadOnlyDictionary<string, string> Redirects { get; }
    public ProjectReferences ProjectReferences { get; }
    public ParsedConfig Configuration { get; }
    public int ReusedSourceFiles { get; }
    public string CommonSourceDirectory { get; }
    internal bool UseCaseSensitiveFileNames => fileSystem.CaseSensitive;

    private CompilerProgram(Builder builder, ProgramFile[] ordered)
    {
        files = builder.files;
        fileSystem = builder.fs;
        CurrentDirectory = builder.CurrentDirectory;
        GlobalTypingsCache = builder.GlobalTypingsCache;
        externalLibraryFiles = builder.ExternalLibraryFiles.ToHashSet(files.Comparer);
        ModuleResolutionKind = builder.ResolutionKind;
        SourceFiles = Array.AsReadOnly(ordered);
        Configuration = builder.config;
        RootFileNames = Array.AsReadOnly(builder.config.FileNames.ToArray());
        ProjectReferences = builder.references;
        MissingFiles = builder.missing.ToArray();
        Redirects = builder.redirects.AsReadOnly();
        ReusedSourceFiles = builder.reused;
        IncludeReasons = builder.reasons.ToDictionary(
            p => p.Key,
            p => (IReadOnlyList<FileIncludeReason>)p.Value.AsReadOnly(),
            files.Comparer);
        string common = Configuration.Options.RootDir ?? (Configuration.FileName.Length != 0
            ? CompilerPath.DirectoryName(Configuration.FileName) : ProjectReferences.CommonDirectory(
                ordered.Where(f => SourceFileMayBeEmitted(f.Syntax)).Select(f => f.Syntax.FileName), fileSystem.CaseSensitive));
        if (common.Length == 0 && ordered.All(f => !SourceFileMayBeEmitted(f.Syntax)))
            common = CurrentDirectory;
        CommonSourceDirectory = common.Length == 0 ? "" : CompilerPath.EnsureTrailingSeparator(common);
        Diagnostics = builder.diagnostics.Concat(ModulePathOptionDiagnostics()).OrderBy(
            d => d.FileName ?? "", StringComparer.Ordinal).ThenBy(d => d.Start).ThenBy(d => d.Code).ToArray();
        IncludeDiagnostics = Array.AsReadOnly(builder.includeDiagnostics.ToArray());
    }

    public ProgramFile? GetFile(string path)
    {
        int remaining = Redirects.Count;
        while (Redirects.TryGetValue(path, out var target) && target != path)
        {
            if (remaining-- == 0)
                throw new InvalidOperationException("Cyclic file redirects");
            path = target;
        }
        return files.GetValueOrDefault(path);
    }

    internal bool FileExists(string path) => fileSystem.FileExists(path);

    public static ValueTask<CompilerProgram> CreateAsync(IFileSystem fileSystem, string currentDirectory, ParsedConfig config,
        CompilerProgram? previous = null, bool useProjectReferenceSources = false, int concurrency = 4,
        string? defaultLibraryDirectory = null, ContentMapperProject? mapperProject = null, CancellationToken cancellation = default,
        string globalTypingsCache = "") =>
        new Builder(
            fileSystem,
            currentDirectory,
            config,
            previous,
            useProjectReferenceSources,
            concurrency,
            defaultLibraryDirectory,
            mapperProject,
            cancellation, globalTypingsCache).Build();

    private sealed partial class Builder
    {
        internal readonly IFileSystem fs;
        private readonly IFileSystem resolutionFs;
        internal readonly ParsedConfig config;
        private readonly CompilerProgram? previous;
        private readonly string cwd, libraryDirectory;
        internal string CurrentDirectory => cwd;
        internal string GlobalTypingsCache { get; }
        internal IEnumerable<string> ExternalLibraryFiles => loadedDepth.Where(p => p.Value > 0).Select(p => p.Key);
        private readonly int concurrency;
        private readonly SemaphoreSlim parseSlots;
        private readonly CancellationToken cancellation;
        internal readonly ProjectReferences references;
        internal readonly Dictionary<string, ProgramFile> files;
        internal readonly Dictionary<string, string> redirects;
        internal readonly Dictionary<string, List<FileIncludeReason>> reasons;
        internal readonly List<Diagnostic> diagnostics = [];
        internal readonly List<Diagnostic> includeDiagnostics = [];
        private readonly List<(string Path, IReadOnlyList<Diagnostic> Diagnostics)> includeFailures = [];

        private void AddIncludeDiagnostic(Diagnostic diagnostic)
        {
            diagnostics.Add(diagnostic);
            includeDiagnostics.Add(diagnostic);
        }

        internal readonly List<string> missing = [];
        internal int reused;
        private readonly Dictionary<string, ModuleResolver> resolvers;
        private readonly Dictionary<string, PackageId> filePackages;
        private readonly List<string> roots = [];
        private readonly HashSet<string> seen;
        private readonly Dictionary<string, int> loadedDepth;
        private ContentMapperProject? mapperProject;
        private readonly ConcurrentDictionary<string, MappedSourceFile> supplemental;

        private sealed record ParsedSource(
            SourceFileNode? File,
            ParseOptions Options,
            ReferenceResolutionMode Format,
                    string PackageDirectory,
            string PackageType,
            MappedSourceFiles? Mapping = null,
            Diagnostic[]? Diagnostics = null,
            bool FailedLookup = false)
        {
            internal BoundSourceFile? Binding { get; set; }
        }

        internal Builder(
            IFileSystem fs,
            string cwd,
            ParsedConfig config,
            CompilerProgram? previous,
            bool useSources,
            int concurrency,
            string? libraryDirectory,
            ContentMapperProject? mapperProject,
            CancellationToken cancellation,
            string globalTypingsCache)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
            this.fs = fs;
            this.cwd = CompilerPath.Normalize(cwd);
            GlobalTypingsCache = globalTypingsCache;
            this.previous = previous;
            var ownedOptions = new CompilerOptions();
            ownedOptions.Merge(config.Options);
            this.config = config = config with
            {
                Options = ownedOptions,
                FileNames = config.FileNames.ToArray(),
                References = config.References.ToArray(),
                ContentMappers = config.ContentMappers.ToArray()
            };
            this.concurrency = concurrency;
            parseSlots = new(concurrency, concurrency);
            this.cancellation = cancellation;
            this.libraryDirectory = libraryDirectory ?? (fs is LibraryFileSystem libraries ? libraries.LibraryDirectory : "");
            var comparer = fs.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            files = new(comparer);
            redirects = new(comparer);
            reasons = new(comparer);
            resolvers = new(comparer);
            seen = new(comparer);
            loadedDepth = new(comparer);
            supplemental = new(comparer);
            this.mapperProject = mapperProject;
            filePackages = new(comparer);
            references = new(fs, cwd, config, useSources, cancellation);
            resolutionFs = references.ResolutionFileSystem();
            diagnostics.AddRange(config.Diagnostics);
            diagnostics.AddRange(references.Diagnostics);
        }

        internal string ResolutionKind => Resolver(config).ResolutionKind;

        private ModuleResolver Resolver(ParsedConfig project)
        {
            lock (resolvers)
            {
                if (!resolvers.TryGetValue(project.FileName, out var resolver))
                    resolvers[project.FileName] = resolver = new(
                        resolutionFs,
                        project.Options,
                        cwd,
                        project.FileName,
                        typingsLocation: GlobalTypingsCache,
                        extraExtensions: config.ContentMappers.SelectMany(m => m.Extensions));
                return resolver;
            }
        }

        internal async ValueTask<CompilerProgram> Build()
        {
            ContentMapperHost? ownedHost = null;
            try
            {
                if (mapperProject is null && config.ContentMappers.Length != 0)
                {
                    ownedHost = new(config.Options.Locale ?? "");
                    mapperProject = await ownedHost.GetProjectAsync(config, cancellation).ConfigureAwait(false);
                }
                return await BuildGraph().ConfigureAwait(false);
            }
            finally
            {
                if (ownedHost is not null)
                {
                    try
                    {
                        if (mapperProject is not null)
                            await mapperProject.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        await ownedHost.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }

        private async ValueTask<CompilerProgram> BuildGraph()
        {
            var pending = new Queue<(FileIncludeReason Reason, bool Library, int Depth, PackageId? Package)>();
            foreach (string root in config.FileNames)
            {
                string path = RootPath(root);
                roots.Add(path);
                pending.Enqueue((new(FileIncludeKind.Root, path), false, 0, null));
            }
            foreach (string type in Resolver(config).AutomaticTypeDirectives())
            {
                var result = await Resolver(config).ResolveAsync(
                    type,
                    CompilerPath.Combine(cwd, "__inferred type names__.ts"),
                    typeReference: true,
                    cancellation: cancellation).ConfigureAwait(false);
                if (result.IsResolved)
                {
                    roots.Add(result.FileName);
                    pending.Enqueue((new(FileIncludeKind.AutomaticType, result.FileName), false, 0, result.PackageId));
                }
                else
                    diagnostics.Add(new(Messages.Cannot_find_type_definition_file_for_0, 0, 0, [type]));
            }
            await LoadPending(pending).ConfigureAwait(false);
            if (config.FileNames.Length != 0 && config.Options.NoLib != true)
            {
                string[] libs = config.Options.Lib ?? [DefaultLibrary(config.Options)];
                foreach (string lib in libs)
                {
                    string path = await LibraryPath(lib).ConfigureAwait(false);
                    roots.Add(path);
                    pending.Enqueue((new(FileIncludeKind.Library, path), true, 0, null));
                }
                await LoadPending(pending).ConfigureAwait(false);
            }
            var order = new List<ProgramFile>();
            var visited = new HashSet<string>(files.Comparer);
            var packageOwners = new Dictionary<PackageId, string>();
            var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<(string Path, bool Exit, FileIncludeReason? Reason)>();
            for (int i = roots.Count - 1; i >= 0; i--)
                stack.Push((roots[i], false,
                reasons.GetValueOrDefault(roots[i])?.FirstOrDefault(r => r.ContainingFile.Length == 0) ?? new(
                    FileIncludeKind.Root,
                    roots[i])));
            while (stack.TryPop(out var entry))
            {
                string path = redirects.GetValueOrDefault(entry.Path, entry.Path);
                if (!files.TryGetValue(path, out var file))
                    continue;
                if (entry.Exit)
                {
                    order.Add(file);
                    continue;
                }
                if (fs.CaseSensitive && visited.Contains(path))
                    continue;
                if (spellings.TryGetValue(path, out string? spelling) && spelling != path
                    && (fs.CaseSensitive || config.Options.ForceConsistentCasingInFileNames != false)
                    && spelling[CompilerPath.RootLength(spelling)..] != path[CompilerPath.RootLength(path)..])
                {
                    var reason = entry.Reason;
                    bool rootAfterReference = string.IsNullOrEmpty(reason?.ContainingFile)
                        && reasons.GetValueOrDefault(path)?.Any(r => r.ContainingFile.Length != 0) == true;
                    AddIncludeDiagnostic(
                        new(
                        rootAfterReference ? Messages.Already_included_file_name_0_differs_from_file_name_1_only_in_casing
                            : Messages.File_name_0_differs_from_already_included_file_name_1_only_in_casing,
                        reason?.Position ?? 0, reason?.Length ?? 0, rootAfterReference ? [spelling, path] : [path, spelling])
                        { FileName = string.IsNullOrEmpty(reason?.ContainingFile) ? null : reason.ContainingFile });
                }
                else
                    spellings.TryAdd(path, path);
                if (!visited.Add(path))
                    continue;
                if (file.Syntax.FileName != path)
                {
                    var parseOptions = file.ParseOptions with { FileName = path };
                    var syntax = await Parser.ParseSourceFileAsync(parseOptions, file.Syntax.Source, cancellation).ConfigureAwait(false);
                    var nodeMap = file.Syntax.DescendantsAndSelf().Zip(syntax.DescendantsAndSelf()).ToDictionary(
                        p => p.First,
                        p => p.Second);
                    foreach (var pair in file.Syntax.Imports.Zip(syntax.Imports))
                        nodeMap[pair.First] = pair.Second;
                    var binding = await Binder.BindAsync(syntax, cancellation).ConfigureAwait(false);
                    if (ReferenceEquals(previous?.GetFile(path)?.Syntax, file.Syntax))
                        reused--;
                    file = file with
                    {
                        Syntax = syntax,
                        Binding = binding,
                        ParseOptions = parseOptions,
                        Resolutions = file.Resolutions.Select(
                            r => r with { Node = r.Node is null ? null : nodeMap.GetValueOrDefault(r.Node) }).ToArray(),
                        Mapping = file.Mapping is { } mapping
                            ? mapping with
                            {
                                Syntax = syntax,
                                VirtualFileName = path + mapping.VirtualFileName[file.Syntax.FileName.Length..]
                            }
                            : null
                    };
                    files[path] = file;
                }
                if (config.Options.DeduplicatePackages != false && filePackages.TryGetValue(path, out var package))
                {
                    if (packageOwners.TryGetValue(package, out string? owner) && owner != path)
                    {
                        redirects[path] = owner;
                        continue;
                    }
                    packageOwners[package] = path;
                }
                stack.Push((path, true, entry.Reason));
                for (int i = file.Dependencies.Count - 1; i >= 0; i--)
                {
                    string dependency = file.Dependencies[i];
                    stack.Push(
                        (dependency, false, reasons.GetValueOrDefault(dependency)?.FirstOrDefault(r => files.Comparer.Equals(
                            r.ContainingFile,
                            path))));
                }
            }
            var libraryNames = OptionDefinitions.All.First(o => o.Name == "lib").Values;
            int Priority(ProgramFile f)
            {
                string name = CompilerPath.BaseName(f.Syntax.FileName);
                if (name is "lib.d.ts" or "lib.es6.d.ts")
                    return 0;
                int index = name.StartsWith("lib.", StringComparison.Ordinal) && name.EndsWith(".d.ts", StringComparison.Ordinal)
                    ? Array.IndexOf(libraryNames, name[4..^5]) : -1;
                return index < 0 ? libraryNames.Length + 2 : index + 1;
            }
            var ordered = order.Where(f => f.Library).OrderBy(Priority).Concat(order.Where(f => !f.Library)).ToArray();
            foreach (var failure in includeFailures)
                foreach (var (path, includes) in reasons)
                    if (files.Comparer.Equals(redirects.GetValueOrDefault(path, path), failure.Path))
                        foreach (var reason in includes)
                            foreach (var diagnostic in failure.Diagnostics)
                                AddIncludeDiagnostic(diagnostic with
                                {
                                    Start = reason.Position,
                                    Length = reason.Length,
                                    Arguments = diagnostic.Code is DiagnosticCode.File0NotFound
                                        or DiagnosticCode.CouldNotResolveThePath0WithTheExtensionsColon1
                                        && reason.Kind == FileIncludeKind.PathReference
                                        && files.TryGetValue(reason.ContainingFile, out var containing)
                                        ? [containing.Syntax.Source.Text[containing.Syntax.Source.ToUtf16Position(reason.Position)
                                            ..containing.Syntax.Source.ToUtf16Position(reason.Position + reason.Length)].Replace('\\', '/'),
                                            .. diagnostic.Arguments.Skip(1)] : diagnostic.Arguments,
                                    FileName = reason.ContainingFile.Length == 0 ? null : reason.ContainingFile
                                });
            VerifyOutputPaths(ordered);
            return new(this, ordered);
        }

        private async ValueTask LoadPending(Queue<(FileIncludeReason Reason, bool Library, int Depth, PackageId? Package)> pending)
        {
            // Keep file-local parsing and binding busy while publishing the graph in discovery order.
            var scheduled = new Queue<(string Path, bool Library, ParsedConfig Project, ValueTask<ParsedSource> Task)>();
            try
            {
                while (pending.Count != 0 || scheduled.Count != 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    // Queue known files independently of publication. A slow
                    // earlier file must not leave free parser workers idle.
                    while (pending.Count != 0 && (concurrency > 1 || scheduled.Count == 0))
                    {
                        var item = pending.Dequeue();
                        string original = CompilerPath.Resolve(cwd, item.Reason.FileName), path = references.Redirect(original);
                        if (!reasons.TryGetValue(original, out var why))
                            reasons[original] = why = [];
                        if (!why.Contains(item.Reason))
                            why.Add(item.Reason);
                        if (path != original)
                            redirects[original] = path;
                        if (item.Package is { } package)
                            filePackages.TryAdd(path, package);
                        if (!seen.Add(path))
                        {
                            if (item.Depth < loadedDepth[path])
                            {
                                loadedDepth[path] = item.Depth;
                                if (files.TryGetValue(path, out var existing))
                                {
                                    var existingDependencies = existing.Dependencies.ToList();
                                    var projectOptions = (references.Find(path)?.Project ?? config).Options;
                                    foreach (var reference in existing.Resolutions.Where(r => r.Resolution.IsResolved && !r.Augmentation))
                                    {
                                        var resolution = reference.Resolution;
                                        if (projectOptions.NoResolve == true)
                                            continue;
                                        if (resolution.IsArbitraryExtension && !existing.Syntax.IsDeclarationFile
                                            && projectOptions.AllowArbitraryExtensions != true)
                                            continue;
                                        if (resolution.Extension is ".js" or ".jsx" or ".mjs" or ".cjs"
                                            && !(projectOptions.AllowJs ?? projectOptions.CheckJs == true))
                                            continue;
                                        if (resolution.Extension is ".jsx" or ".tsx" && projectOptions.Jsx == JsxEmit.None)
                                            continue;
                                        int depth = item.Depth + (resolution.External ? 1 : 0);
                                        bool externalJs = resolution.External && resolution.Extension is ".js" or ".jsx" or ".mjs" or ".cjs";
                                        if (externalJs && depth > (projectOptions.MaxNodeModuleJsDepth ?? 0))
                                        {
                                            if (!existingDependencies.Contains(resolution.FileName, files.Comparer))
                                                existingDependencies.Add(resolution.FileName);
                                            continue;
                                        }
                                        if (!existingDependencies.Contains(resolution.FileName, files.Comparer))
                                            existingDependencies.Add(resolution.FileName);
                                        pending.Enqueue(
                                            (new(
                                                reference.TypeReference ? FileIncludeKind.TypeReference : FileIncludeKind.Import,
                                                resolution.FileName,
                                                path,
                                                reference.Node?.Pos ?? 0), false, depth, resolution.PackageId));
                                    }
                                    files[path] = existing with { Dependencies = existingDependencies.ToArray() };
                                }
                            }
                            continue;
                        }
                        loadedDepth[path] = item.Depth;
                        var project = references.Find(path)?.Project ?? config;
                        scheduled.Enqueue((path, item.Library, project, ParseAndBind(path, project, item.Library)));
                    }
                    if (scheduled.Count == 0)
                        continue;
                    var entry = scheduled.Dequeue();
                    var parsed = await entry.Task.ConfigureAwait(false);
                    if (parsed.File is not { } syntax)
                    {
                        missing.Add(entry.Path);
                        includeFailures.Add((entry.Path, parsed.Diagnostics ?? [new(Messages.File_0_not_found, 0, 0, [entry.Path])]));
                        continue;
                    }
                    if (parsed.Diagnostics is not null)
                        diagnostics.AddRange(parsed.Diagnostics);
                    int currentDepth = loadedDepth[entry.Path];
                    var dependencies = new List<string>();
                    var resolutions = new List<ModuleReference>();
                    var resolver = Resolver(entry.Project);
                    var options = entry.Project.Options;
                    string resolutionFile = references.Find(entry.Path)?.Source ?? entry.Path;
                    void Include(
                        string path,
                        FileIncludeKind kind,
                        int pos,
                        bool lib = false,
                        int? depth = null,
                        PackageId? package = null,
                        int length = 0)
                    {
                        dependencies.Add(path);
                        pending.Enqueue((new(kind, path, entry.Path, pos, length), lib, depth ?? currentDepth, package));
                    }
                    if (options.NoResolve != true)
                    {
                        foreach (var reference in syntax.ReferencedFiles)
                        {
                            string path = RootPath(CompilerPath.Resolve(CompilerPath.DirectoryName(entry.Path), reference.FileName));
                            if (files.Comparer.Equals(path, entry.Path))
                                AddIncludeDiagnostic(
                                    new(
                                        Messages.A_file_cannot_have_a_reference_to_itself,
                                        reference.Pos,
                                        reference.End - reference.Pos,
                                        [])
                                    { FileName = entry.Path });
                            else
                                Include(path, FileIncludeKind.PathReference, reference.Pos, length: reference.End - reference.Pos);
                        }
                        foreach (var reference in syntax.TypeReferenceDirectives)
                        {
                            var mode = reference.ResolutionMode == 0
                                ? DefaultMode(entry.Path, options, parsed.Format, parsed.PackageType)
                                : reference.ResolutionMode;
                            var resolved = await resolver.ResolveAsync(
                                reference.FileName,
                                resolutionFile,
                                mode,
                                true,
                                cancellation).ConfigureAwait(false);
                            resolutions.Add(new(reference.FileName, mode, null, resolved, true));
                            if (resolved.IsResolved)
                                Include(
                                    resolved.FileName,
                                    FileIncludeKind.TypeReference,
                                    reference.Pos,
                                    package: resolved.PackageId,
                                    length: reference.End - reference.Pos);
                            else
                                AddIncludeDiagnostic(
                                    new(
                                        Messages.Cannot_find_type_definition_file_for_0,
                                        reference.Pos,
                                        reference.End - reference.Pos,
                                        [reference.FileName])
                                    { FileName = entry.Path });
                        }
                    }
                    if (options.NoLib != true)
                        foreach (var reference in syntax.LibReferenceDirectives)
                            Include(
                                await LibraryPath(reference.FileName).ConfigureAwait(false),
                                FileIncludeKind.Library,
                                reference.Pos,
                                true,
                                length: reference.End - reference.Pos);
                    var imports = new List<(SyntaxNode? Node, string Name)>();
                    bool javaScript = syntax.ScriptKind is ScriptKind.JS or ScriptKind.JSX;
                    if (options.ImportHelpers == true && (javaScript || !syntax.IsDeclarationFile
                        && (syntax.ExternalModuleIndicator is not null
                            || options.IsolatedModules == true
                            || options.VerbatimModuleSyntax == true)))
                        imports.Add((null, "tslib"));
                    string? Pragma(string name) =>
                        syntax.Pragmas.LastOrDefault(p => p.Name == name).Arguments?.GetValueOrDefault("factory").Value;
                    if ((javaScript || syntax.ScriptKind == ScriptKind.TSX) && Pragma("jsxruntime") != "classic"
                        && (options.Jsx is JsxEmit.ReactJSX or JsxEmit.ReactJSXDev || options.JsxImportSource is not null
                            || Pragma("jsximportsource") is not null || Pragma("jsxruntime") == "automatic"))
                    {
                        string jsx = Pragma("jsximportsource") ?? options.JsxImportSource ?? "react";
                        imports.Add((null, jsx + (options.Jsx == JsxEmit.ReactJSXDev ? "/jsx-dev-runtime" : "/jsx-runtime")));
                    }
                    imports.AddRange(syntax.Imports.Select(n => (Node: (SyntaxNode?)n, Name: ImportText(n).ToString())));
                    foreach (var import in imports)
                    {
                        if (import.Name.Length == 0)
                            continue;
                        var mode = UsageMode(import.Node, entry.Path, options, parsed.Format, parsed.PackageType);
                        var resolved = await resolver.ResolveAsync(
                            import.Name,
                            resolutionFile,
                            mode,
                            cancellation: cancellation).ConfigureAwait(false);
                        resolutions.Add(new(import.Name, mode, import.Node, resolved));
                        diagnostics.AddRange(resolved.Diagnostics);
                        if (!resolved.IsResolved || options.NoResolve == true)
                            continue;
                        if (resolved.IsArbitraryExtension
                            && !syntax.IsDeclarationFile
                            && options.AllowArbitraryExtensions != true)
                            continue;
                        if (syntax.ScriptKind is not (ScriptKind.JS or ScriptKind.JSX)
                            && (import.Node?.Flags & NodeFlags.JSDoc) != 0
                            && import.Node is not null)
                            continue;
                        bool js = resolved.Extension is ".js" or ".jsx" or ".mjs" or ".cjs";
                        int depth = currentDepth + (resolved.External ? 1 : 0);
                        if (js && !(options.AllowJs ?? options.CheckJs == true))
                            continue;
                        if (js && resolved.External && resolved.FileName.Contains("/node_modules/", StringComparison.Ordinal)
                            && depth > (options.MaxNodeModuleJsDepth ?? 0))
                        {
                            // An elided dependency can still be loaded through an explicit root or a shallower import.
                            dependencies.Add(resolved.FileName);
                            continue;
                        }
                        if (resolved.Extension is ".tsx" or ".jsx" && options.Jsx == JsxEmit.None)
                            continue;
                        Include(resolved.FileName, FileIncludeKind.Import, import.Node?.Pos ?? 0, depth: depth, package: resolved.PackageId,
                            length: import.Node is null ? 0 : import.Node.End - import.Node.Pos);
                    }
                    foreach (var augmentation in syntax.ModuleAugmentations.OfType<StringLiteralNode>())
                    {
                        var mode = UsageMode(augmentation, entry.Path, options, parsed.Format, parsed.PackageType);
                        var resolved = await resolver.ResolveAsync(
                            augmentation.Text.ToString(),
                            resolutionFile,
                            mode,
                            cancellation: cancellation).ConfigureAwait(false);
                        resolutions.Add(new(augmentation.Text.ToString(), mode, augmentation, resolved, Augmentation: true));
                        diagnostics.AddRange(resolved.Diagnostics);
                    }
                    if (parsed.Mapping is { } mappedFiles)
                        foreach (var mapped in mappedFiles.Supplemental)
                        {
                            supplemental[mapped.Syntax.FileName] = mapped;
                            Include(mapped.Syntax.FileName, FileIncludeKind.MapperSupplemental, 0);
                        }
                    files.Add(entry.Path, new(syntax, parsed.Binding!, parsed.Options, parsed.Format, parsed.PackageDirectory, parsed.PackageType,
                        resolutions.ToArray(), dependencies.ToArray(), entry.Library)
                    { Mapping = parsed.Mapping?.Canonical ?? supplemental.GetValueOrDefault(entry.Path) });
                }
            }
            finally
            {
                // Observe outstanding work before the builder (including its mapper host) is disposed.
                if (scheduled.Count != 0)
                    await Task.WhenAll(scheduled.Select(entry => entry.Task.AsTask())).ConfigureAwait(false);
            }
        }

        private async ValueTask<ParsedSource> ParseAndBind(string path, ParsedConfig project, bool library)
        {
            if (concurrency > 1)
                await parseSlots.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var parsed = await Parse(path, project, library).ConfigureAwait(false);
                if (parsed.File is { } syntax)
                    parsed.Binding = await Binder.BindAsync(syntax, cancellation).ConfigureAwait(false);
                return parsed;
            }
            finally
            {
                if (concurrency > 1)
                    parseSlots.Release();
            }
        }

        private async ValueTask<ParsedSource> Parse(
            string path, ParsedConfig project, bool library)
        {
            // Parallel execution may finish in any order; publication above always follows discovery order.
            var mapper = config.ContentMappers.FirstOrDefault(m => m.Extensions.Any(e => path.EndsWith(e, StringComparison.Ordinal)));
            if (concurrency > 1 && mapper is null)
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            cancellation.ThrowIfCancellationRequested();
            var resolver = Resolver(project);
            var scope = library ? null : resolver.Packages.Scope(CompilerPath.DirectoryName(path));
            string ext = ModuleResolver.Extension(path);
            bool explicitFormat = path.EndsWith(".mts", StringComparison.Ordinal) || path.EndsWith(".cts", StringComparison.Ordinal)
                || path.EndsWith(".mjs", StringComparison.Ordinal) || path.EndsWith(".cjs", StringComparison.Ordinal);
            string type = (!explicitFormat && resolver.ResolutionKind != "bundler"
                || path.Contains("/node_modules/", StringComparison.Ordinal))
                ? scope?.Type ?? ""
                : "";
            var format = ext is ".mts" or ".mjs" or ".d.mts" || ext is not (".cts" or ".cjs" or ".d.cts") && type == "module"
                ? ReferenceResolutionMode.Import : ReferenceResolutionMode.Require;
            if (ext == ".json")
                format = ReferenceResolutionMode.Unspecified;
            var options = project.Options;
            ModuleDetectionKind detection = options.EmitModuleDetectionKind;
            bool force = !CompilerPath.IsDeclarationFile(path) && (detection == ModuleDetectionKind.Force || detection == ModuleDetectionKind.Auto
                && (ext is ".mts" or ".cts" or ".mjs" or ".cjs"
                    || ImpliedMode(path, options, format, type) == ReferenceResolutionMode.Import));
            var parseOptions = new ParseOptions(path, ForceExternalModule: force,
                JsxExternalModule: detection == ModuleDetectionKind.Auto && options.Jsx is JsxEmit.ReactJSX or JsxEmit.ReactJSXDev);
            if (supplemental.TryGetValue(path, out var mappedSource))
                return new(mappedSource.Syntax, parseOptions, format, scope?.Directory ?? "", type);
            if (options.AllowNonTsExtensions != true && !SupportedSource(path, project))
            {
                bool js = ext is ".js" or ".jsx" or ".mjs" or ".cjs";
                var diagnostic = new Diagnostic(ext.Length == 0 ? Messages.Could_not_resolve_the_path_0_with_the_extensions_Colon_1
                    : js ? Messages.File_0_is_a_JavaScript_file_Did_you_mean_to_enable_the_allowJs_option
                    : Messages.File_0_has_an_unsupported_extension_The_only_supported_extensions_are_1, 0, 0,
                    js ? [path] : [path, SupportedExtensionsText(project)]);
                return new(null, parseOptions, format, scope?.Directory ?? "", type, Diagnostics: [diagnostic], FailedLookup: true);
            }
            SourceText? source = (fs as LibraryFileSystem)?.ReadBundledSource(path);
            byte[]? bytes = source is null ? fs.ReadFile(path) : null;
            if (source is null && bytes is null)
                return new(null, parseOptions, format, scope?.Directory ?? "", type);
            if (bytes is not null)
                bytes = SourceEncoding.DecodeOwnedBytes(bytes);
            if (mapper is not null)
                return await ParseMapped(
                    mapper,
                    parseOptions,
                    source ?? SourceText.FromOwnedBytes(bytes!),
                    format,
                    scope?.Directory ?? "",
                    type).ConfigureAwait(false);
            if (previous?.GetFile(path) is { } old && old.ParseOptions == parseOptions && old.Syntax.Source.Bytes.Span.SequenceEqual(source is null ? bytes : source.Bytes.Span))
            {
                Interlocked.Increment(ref reused);
                return new(old.Syntax, parseOptions, format, scope?.Directory ?? "", type);
            }
            var syntax = await Parser.ParseSourceFileAsync(parseOptions, source ?? SourceText.FromOwnedBytes(bytes!), cancellation).ConfigureAwait(false);
            return new(syntax, parseOptions, format, scope?.Directory ?? "", type);
        }
    }
}

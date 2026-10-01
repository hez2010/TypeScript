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
public sealed record FileIncludeReason(FileIncludeKind Kind, Utf8String FileName, Utf8String ContainingFile = default, int Position = 0, int Length = 0)
{
    public Utf8String Specifier { get; init; }
    public PackageId? PackageId { get; init; }
}
public sealed record ModuleReference(
    Utf8String Specifier,
    ReferenceResolutionMode Mode,
    SyntaxNode? Node,
    ResolvedModule Resolution,
    bool TypeReference = false,
    bool Augmentation = false);
public sealed record ProgramFile(SourceFileNode Syntax, BoundSourceFile Binding, ParseOptions ParseOptions,
    ReferenceResolutionMode ImpliedFormat, Utf8String PackageDirectory, Utf8String PackageType, IReadOnlyList<ModuleReference> Resolutions,
    IReadOnlyList<Utf8String> Dependencies, bool Library = false)
{
    public MappedSourceFile? Mapping { get; init; }
}

/// <summary>A published program owns a deterministic graph; rebuilding never mutates an older program.</summary>
public sealed partial class CompilerProgram
{
    private readonly Dictionary<Utf8String, ProgramFile> files;
    private readonly IFileSystem fileSystem;
    private readonly HashSet<Utf8String> externalLibraryFiles;
    internal Utf8String CurrentDirectory { get; }
    internal Utf8String DefaultLibraryDirectory { get; }
    internal Utf8String GlobalTypingsCache { get; }
    internal Utf8String ModuleResolutionKind { get; }
    public IReadOnlyList<ProgramFile> SourceFiles { get; }
    internal IReadOnlyList<ProgramFile> ExplanationFiles { get; }
    public IReadOnlyList<Diagnostic> ResolutionTrace { get; }
    public IReadOnlyList<Utf8String> RootFileNames { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    internal IReadOnlyList<Diagnostic> IncludeDiagnostics { get; }
    public IReadOnlyList<Utf8String> MissingFiles { get; }
    public IReadOnlyDictionary<Utf8String, IReadOnlyList<FileIncludeReason>> IncludeReasons { get; }
    public IReadOnlyDictionary<Utf8String, Utf8String> Redirects { get; }
    public ProjectReferences ProjectReferences { get; }
    public ParsedConfig Configuration { get; }
    public int ReusedSourceFiles { get; }
    public Utf8String CommonSourceDirectory { get; }
    public IReadOnlyList<Utf8String> PackageJsonLookupPaths { get; }
    internal IReadOnlyList<Utf8String> ExistingPackageJsons { get; }
    internal IReadOnlyList<Utf8String> MissingPackageJsons { get; }
    internal bool UseCaseSensitiveFileNames => fileSystem.CaseSensitive;

    private CompilerProgram(Builder builder, ProgramFile[] ordered)
    {
        files = builder.files;
        fileSystem = builder.fs;
        CurrentDirectory = builder.CurrentDirectory;
        DefaultLibraryDirectory = builder.LibraryDirectory;
        GlobalTypingsCache = builder.GlobalTypingsCache;
        externalLibraryFiles = builder.ExternalLibraryFiles.ToHashSet(files.Comparer);
        ModuleResolutionKind = builder.ResolutionKind;
        SourceFiles = Array.AsReadOnly(ordered);
        ExplanationFiles = builder.explanationFiles;
        ResolutionTrace = builder.resolutionTrace.ToArray();
        Configuration = builder.config;
        RootFileNames = Array.AsReadOnly(builder.config.FileNames.Select(path => CompilerPath.Resolve(CurrentDirectory, path)).ToArray());
        ProjectReferences = builder.references;
        MissingFiles = builder.missing.ToArray();
        Redirects = builder.redirects.AsReadOnly();
        ReusedSourceFiles = builder.reused;
        var packages = builder.PackageJsonEntries.Select(entry => (
            Path: entry.DirectoryExists ? fileSystem.RealPath(CompilerPath.Combine(entry.Directory, "package.json"u8))
                : CompilerPath.Combine(entry.Directory, "package.json"u8), Exists: entry.Contents is not null)).ToArray();
        PackageJsonLookupPaths = packages.Select(entry => entry.Path).Distinct(files.Comparer).Order(Utf8StringComparer.Ordinal).ToArray();
        ExistingPackageJsons = packages.Where(entry => entry.Exists).Select(entry => entry.Path).Distinct(files.Comparer).Order(Utf8StringComparer.Ordinal).ToArray();
        MissingPackageJsons = packages.Where(entry => !entry.Exists && entry.Path.Contains("/node_modules/"u8, StringComparison.Ordinal))
            .Select(entry => entry.Path).Distinct(files.Comparer).Order(Utf8StringComparer.Ordinal).ToArray();
        IncludeReasons = builder.reasons.ToDictionary(
            p => p.Key,
            p => (IReadOnlyList<FileIncludeReason>)p.Value.AsReadOnly(),
            files.Comparer);
        Utf8String common = Configuration.Options.RootDir ?? (Configuration.FileName.Length != 0
            ? CompilerPath.DirectoryName(Configuration.FileName) : ProjectReferences.CommonDirectory(
                ordered.Where(f => SourceFileMayBeEmitted(f.Syntax)).Select(f => f.Syntax.FileName), fileSystem.CaseSensitive));
        if (common.Length == 0 && ordered.All(f => !SourceFileMayBeEmitted(f.Syntax)))
            common = CurrentDirectory;
        CommonSourceDirectory = common.Length == 0 ? Utf8String.Empty : CompilerPath.EnsureTrailingSeparator(common);
        blockedEmitPaths = new(files.Comparer);
        foreach (var failure in builder.explainedFailures)
        {
            var explained = ExplainIncludeDiagnostic(failure.Diagnostic, failure.Path, failure.Reason);
            builder.diagnostics.Add(explained);
            builder.includeDiagnostics.Add(explained);
        }
        foreach (var failure in builder.includeFailures)
            foreach (var (path, reasons) in IncludeReasons)
                if (files.Comparer.Equals(Redirects.GetValueOrDefault(path, path), failure.Path))
                    foreach (var reason in reasons)
                        foreach (var diagnostic in failure.Diagnostics)
                        {
                            var arguments = diagnostic.Arguments;
                            if (diagnostic.Code is DiagnosticCode.File0NotFound or DiagnosticCode.CouldNotResolveThePath0WithTheExtensionsColon1)
                            {
                                if (reason.Kind == FileIncludeKind.Root && !reason.Specifier.IsEmpty)
                                    arguments = [reason.Specifier, .. arguments.Skip(1)];
                                else if (reason.Kind == FileIncludeKind.PathReference && GetFile(reason.ContainingFile) is { } containing)
                                    arguments = [containing.Syntax.Source.Text[reason.Position..(reason.Position + reason.Length)].Replace((byte)'\\', (byte)'/'), .. arguments.Skip(1)];
                            }
                            var explained = ExplainIncludeDiagnostic(diagnostic with { Arguments = arguments }, default, reason);
                            builder.diagnostics.Add(explained);
                            builder.includeDiagnostics.Add(explained);
                        }
        var compositeErrors = Configuration.Options.Composite == true ? SourceFiles
            .Where(file => SourceFileMayBeEmitted(file.Syntax) && !RootFileNames.Any(root => files.Comparer.Equals(CompilerPath.Resolve(CurrentDirectory, root), file.Syntax.FileName)))
            .Select(file => ExplainIncludeDiagnostic(new(Messages.File_0_is_not_listed_within_the_file_list_of_project_1_Projects_must_list_all_files_or_use_an_include_pattern,
                0, 0, [file.Syntax.FileName, Configuration.FileName]), file.Syntax.FileName)).ToArray() : [];
        builder.includeDiagnostics.AddRange(compositeErrors);
        var options = Configuration.Options;
        if ((options.OutDir is { IsEmpty: false } || options.RootDir is { IsEmpty: false }
            || options.SourceRoot is { IsEmpty: false } || options.MapRoot is { IsEmpty: false }
            || (options.Declaration == true || options.Composite == true) && options.DeclarationDir is { IsEmpty: false })
            && (options.RootDir ?? (Configuration.FileName.IsEmpty ? (Utf8String?)null
                : CompilerPath.DirectoryName(Configuration.FileName))) is { IsEmpty: false } rootDirectory)
            foreach (var file in SourceFiles)
                if (SourceFileMayBeEmitted(file.Syntax) && !CompilerPath.Contains(rootDirectory, file.Syntax.FileName, UseCaseSensitiveFileNames))
                {
                    var error = ExplainIncludeDiagnostic(new(Messages.File_0_is_not_under_rootDir_1_rootDir_is_expected_to_contain_all_source_files,
                        0, 0, [file.Syntax.FileName, rootDirectory]), file.Syntax.FileName);
                    builder.diagnostics.Add(error); builder.includeDiagnostics.Add(error);
                }
        Diagnostics = DiagnosticCollection.SortAndDeduplicate(builder.diagnostics
            .Where(diagnostic => diagnostic.FileName is null || !builder.includeDiagnostics.Contains(diagnostic))
            .Concat(CompilerOptionDiagnostics()).Concat(ModulePathOptionDiagnostics()).Concat(VerifyOutputPaths())
            .Concat(compositeErrors.Where(diagnostic => diagnostic.FileName is null)));
        IncludeDiagnostics = Array.AsReadOnly(builder.includeDiagnostics.ToArray());
    }

    public ProgramFile? GetFile(Utf8String path)
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

    internal bool FileExists(Utf8String path) => fileSystem.FileExists(path);

    public static ValueTask<CompilerProgram> CreateAsync(IFileSystem fileSystem, Utf8String currentDirectory, ParsedConfig config,
        CompilerProgram? previous = null, bool useProjectReferenceSources = false, int concurrency = 4,
        Utf8String? defaultLibraryDirectory = null, ContentMapperProject? mapperProject = null, CancellationToken cancellation = default,
        Utf8String globalTypingsCache = default, bool skipModuleResolution = false) =>
        new Builder(
            fileSystem,
            currentDirectory,
            config,
            previous,
            useProjectReferenceSources,
            concurrency,
            defaultLibraryDirectory,
            mapperProject,
            cancellation, globalTypingsCache, skipModuleResolution).Build();

    private sealed partial class Builder
    {
        internal readonly IFileSystem fs;
        private readonly IFileSystem resolutionFs;
        internal readonly ParsedConfig config;
        private readonly CompilerProgram? previous;
        private readonly Utf8String cwd, libraryDirectory;
        internal Utf8String CurrentDirectory => cwd;
        internal Utf8String LibraryDirectory => libraryDirectory;
        internal Utf8String GlobalTypingsCache { get; }
        internal IEnumerable<Utf8String> ExternalLibraryFiles => loadedDepth.Where(p => p.Value > 0).Select(p => p.Key);
        private readonly int concurrency;
        private readonly SemaphoreSlim parseSlots;
        private readonly CancellationToken cancellation;
        private readonly bool skipModuleResolution;
        internal readonly ProjectReferences references;
        internal readonly Dictionary<Utf8String, ProgramFile> files;
        internal readonly Dictionary<Utf8String, Utf8String> redirects;
        internal readonly Dictionary<Utf8String, List<FileIncludeReason>> reasons;
        internal readonly List<Diagnostic> diagnostics = [];
        internal readonly List<Diagnostic> includeDiagnostics = [];
        internal readonly List<(Utf8String Path, IReadOnlyList<Diagnostic> Diagnostics)> includeFailures = [];
        internal readonly List<(Diagnostic Diagnostic, Utf8String Path, FileIncludeReason? Reason)> explainedFailures = [];

        private void AddIncludeDiagnostic(Diagnostic diagnostic)
        {
            diagnostics.Add(diagnostic);
            includeDiagnostics.Add(diagnostic);
        }

        internal readonly List<Utf8String> missing = [];
        internal int reused;
        private readonly Dictionary<Utf8String, ModuleResolver> resolvers;
        internal IEnumerable<PackageJsonEntry> PackageJsonEntries => resolvers.Values.SelectMany(resolver => resolver.Packages.Snapshot());
        private readonly Dictionary<Utf8String, PackageId> filePackages;
        private readonly List<Utf8String> roots = [];
        private readonly HashSet<Utf8String> seen;
        private readonly Dictionary<Utf8String, int> loadedDepth;
        private ContentMapperProject? mapperProject;
        private readonly ConcurrentDictionary<Utf8String, MappedSourceFile> supplemental;

        private sealed record ParsedSource(
            SourceFileNode? File,
            ParseOptions Options,
            ReferenceResolutionMode Format,
                    Utf8String PackageDirectory,
            Utf8String PackageType,
            MappedSourceFiles? Mapping = null,
            Diagnostic[]? Diagnostics = null,
            bool FailedLookup = false)
        {
            internal BoundSourceFile? Binding { get; set; }
        }

        internal Builder(
            IFileSystem fs,
            Utf8String cwd,
            ParsedConfig config,
            CompilerProgram? previous,
            bool useSources,
            int concurrency,
            Utf8String? libraryDirectory,
            ContentMapperProject? mapperProject,
            CancellationToken cancellation,
            Utf8String globalTypingsCache, bool skipModuleResolution)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
            this.fs = fs;
            this.skipModuleResolution = skipModuleResolution;
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
            this.concurrency = config.Options.SingleThreaded == true ? 1 : concurrency;
            parseSlots = new(this.concurrency, this.concurrency);
            this.cancellation = cancellation;
            this.libraryDirectory = libraryDirectory ?? (fs is LibraryFileSystem libraries ? libraries.LibraryDirectory : Utf8String.Empty);
            var comparer = fs.CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
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

        internal Utf8String ResolutionKind => Resolver(config).ResolutionKind;
        internal readonly List<Diagnostic> resolutionTrace = [];
        internal ProgramFile[] explanationFiles = [];

        private ModuleResolver Resolver(ParsedConfig project)
        {
            lock (resolvers)
            {
                if (!resolvers.TryGetValue(project.FileName, out var resolver))
                {
                    var packages = project == config ? new PackageJsonCache(resolutionFs, cwd) : Resolver(config).Packages;
                    resolvers[project.FileName] = resolver = new(
                        resolutionFs,
                        project.Options,
                        cwd,
                        project.FileName,
                        typingsLocation: GlobalTypingsCache,
                        extraExtensions: config.ContentMappers.SelectMany(m => m.Extensions))
                    { RedirectConfig = project == config ? default : project.FileName, TraceOverride = config.Options.TraceResolution == true, Packages = packages };
                }
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
                    ownedHost = new(config.Options.Locale ?? Utf8String.Empty);
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
            if (config.FileNames.Length != 0 && config.Options.NoLib != true)
                foreach (Utf8String lib in config.Options.Lib ?? [DefaultLibrary(config.Options)])
                    await LibraryPath(lib).ConfigureAwait(false);
            var pending = new LinkedList<(FileIncludeReason Reason, bool Library, int Depth, PackageId? Package)>();
            foreach (Utf8String root in config.FileNames)
            {
                Utf8String path = RootPath(root);
                roots.Add(path);
                pending.AddLast((new(FileIncludeKind.Root, path) { Specifier = root }, false, 0, null));
            }
            foreach (Utf8String type in skipModuleResolution ? [] : Resolver(config).AutomaticTypeDirectives())
            {
                var result = await Resolver(config).ResolveAsync(
                    type,
                    CompilerPath.Combine(cwd, Utf8Literals.InferredTypeNamesTs),
                    typeReference: true,
                    cancellation: cancellation).ConfigureAwait(false);
                resolutionTrace.AddRange(result.TraceMessages);
                if (result.IsResolved)
                {
                    roots.Add(result.FileName);
                    pending.AddLast((new(FileIncludeKind.AutomaticType, result.FileName) { Specifier = type, PackageId = result.PackageId }, false, 0, result.PackageId));
                }
                else
                    diagnostics.Add(new(Messages.Cannot_find_type_definition_file_for_0, 0, 0, [type]));
            }
            await LoadPending(pending).ConfigureAwait(false);
            if (config.FileNames.Length != 0 && config.Options.NoLib != true)
            {
                Utf8String[] libs = config.Options.Lib ?? [DefaultLibrary(config.Options)];
                foreach (Utf8String lib in libs)
                {
                    Utf8String path = await LibraryPath(lib).ConfigureAwait(false);
                    roots.Add(path);
                    pending.AddLast((new(FileIncludeKind.Library, path) { Specifier = lib }, true, 0, null));
                }
                await LoadPending(pending).ConfigureAwait(false);
            }
            var order = new List<ProgramFile>();
            var explanationOrder = new List<ProgramFile>();
            var visited = new HashSet<Utf8String>(files.Comparer);
            var packageOwners = new Dictionary<PackageId, Utf8String>();
            var spellings = new Dictionary<Utf8String, Utf8String>(Utf8StringComparer.OrdinalIgnoreCase);
            var orderedReasons = new Dictionary<Utf8String, List<FileIncludeReason>>(files.Comparer);
            var stack = new Stack<(Utf8String Path, bool Exit, FileIncludeReason? Reason)>();
            for (int i = roots.Count - 1; i >= 0; i--)
                stack.Push((roots[i], false,
                reasons.GetValueOrDefault(roots[i])?.FirstOrDefault(r => r.ContainingFile.Length == 0 && r.FileName == roots[i]) ?? new(
                    FileIncludeKind.Root,
                    roots[i])));
            while (stack.TryPop(out var entry))
            {
                Utf8String path = redirects.GetValueOrDefault(entry.Path, entry.Path);
                if (!entry.Exit && entry.Reason is { } includeReason)
                {
                    if (!orderedReasons.TryGetValue(entry.Path, out var includes)) orderedReasons[entry.Path] = includes = [];
                    if (!includes.Contains(includeReason)) includes.Add(includeReason);
                }
                if (!files.TryGetValue(path, out var file))
                    continue;
                if (entry.Exit)
                {
                    order.Add(file);
                    explanationOrder.Add(file);
                    continue;
                }
                if (fs.CaseSensitive && visited.Contains(path))
                    continue;
                if (spellings.TryGetValue(path, out Utf8String spelling) && spelling != path
                    && (fs.CaseSensitive || config.Options.ForceConsistentCasingInFileNames != false)
                    && spelling[CompilerPath.RootLength(spelling)..] != path[CompilerPath.RootLength(path)..])
                {
                    var reason = entry.Reason;
                    bool rootAfterReference = Utf8String.IsNullOrEmpty(reason?.ContainingFile)
                        && reasons.Any(pair => pair.Key.Equals(path, StringComparison.OrdinalIgnoreCase) && pair.Value.Any(r => r.ContainingFile.Length != 0));
                    explainedFailures.Add((
                        new(
                        rootAfterReference ? Messages.Already_included_file_name_0_differs_from_file_name_1_only_in_casing
                            : Messages.File_name_0_differs_from_already_included_file_name_1_only_in_casing,
                        reason?.Position ?? 0, reason?.Length ?? 0, rootAfterReference ? [spelling, path] : [path, spelling])
                        { FileName = reason?.ContainingFile is { IsEmpty: false } containingFile ? containingFile : (Utf8String?)null }, path, reason));
                }
                else
                    spellings.TryAdd(path, path);
                if (!visited.Add(path))
                    continue;
                if (file.Syntax.FileName != path)
                {
                    Utf8String previousSpelling = file.Syntax.FileName;
                    foreach (var includes in reasons.Values.Concat(orderedReasons.Values))
                        for (int i = 0; i < includes.Count; i++)
                            if (includes[i].ContainingFile == previousSpelling)
                                includes[i] = includes[i] with { ContainingFile = path };
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
                foreach (var resolution in file.Resolutions.OrderByDescending(r => r.TypeReference))
                    resolutionTrace.AddRange(resolution.Resolution.TraceMessages);
                if (config.Options.DeduplicatePackages != false && filePackages.TryGetValue(path, out var package))
                {
                    if (packageOwners.TryGetValue(package, out Utf8String owner) && owner != path)
                    {
                        redirects[path] = owner;
                        explanationOrder.Add(file);
                        continue;
                    }
                    packageOwners[package] = path;
                }
                stack.Push((path, true, entry.Reason));
                var consumedReasons = new HashSet<FileIncludeReason>();
                for (int i = file.Dependencies.Count - 1; i >= 0; i--)
                {
                    Utf8String dependency = file.Dependencies[i];
                    var reason = reasons.GetValueOrDefault(dependency)?.LastOrDefault(r => r.FileName == dependency
                        && files.Comparer.Equals(r.ContainingFile, path) && !consumedReasons.Contains(r));
                    if (reason is not null) consumedReasons.Add(reason);
                    stack.Push((dependency, false, reason));
                }
            }
            var libraryNames = OptionDefinitions.All.First(o => o.Name == Utf8Literals.Lib).Values;
            int Priority(ProgramFile f)
            {
                Utf8String name = CompilerPath.BaseName(f.Syntax.FileName);
                if (name == "lib.d.ts"u8 || name == "lib.es6.d.ts"u8)
                    return 0;
                int index = name.StartsWith("lib."u8, StringComparison.Ordinal) && name.EndsWith(".d.ts"u8, StringComparison.Ordinal)
                    ? Array.IndexOf(libraryNames, name[4..^5]) : -1;
                return index < 0 ? libraryNames.Length + 2 : index + 1;
            }
            var ordered = order.Where(f => f.Library).OrderBy(Priority).Concat(order.Where(f => !f.Library)).ToArray();
            explanationFiles = explanationOrder.Where(f => f.Library).OrderBy(Priority).Concat(explanationOrder.Where(f => !f.Library)).ToArray();
            foreach (var (path, includes) in orderedReasons)
                if (reasons.TryGetValue(path, out var existing))
                {
                    includes.AddRange(existing.Where(reason => !includes.Contains(reason)));
                    reasons[path] = includes;
                }
            var remainingReasons = reasons.Where(pair => !orderedReasons.ContainsKey(pair.Key)).ToArray();
            reasons.Clear();
            foreach (var pair in orderedReasons) reasons.Add(pair.Key, pair.Value);
            foreach (var pair in remainingReasons) reasons.Add(pair.Key, pair.Value);
            if (mapperProject is not null)
                foreach (var diagnostic in mapperProject.Diagnostics)
                    diagnostics.Add(MapperOptionDiagnostic(diagnostic));
            foreach (var library in resolvedLibraries.OrderBy(pair => pair.Key, Utf8StringComparer.Ordinal))
                resolutionTrace.AddRange(library.Value.Trace);
            return new(this, ordered);
        }

        private async ValueTask LoadPending(LinkedList<(FileIncludeReason Reason, bool Library, int Depth, PackageId? Package)> pending)
        {
            // Keep file-local parsing and binding busy while publishing the graph in discovery order.
            var scheduled = new Queue<(Utf8String Path, bool Library, ParsedConfig Project, ValueTask<ParsedSource> Task)>();
            try
            {
                while (pending.Count != 0 || scheduled.Count != 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    // Queue known files independently of publication. A slow
                    // earlier file must not leave free parser workers idle.
                    while (pending.Count != 0 && (concurrency > 1 || scheduled.Count == 0))
                    {
                        var next = config.Options.SingleThreaded == true ? pending.Last! : pending.First!;
                        var item = next.Value;
                        pending.Remove(next);
                        Utf8String original = CompilerPath.Resolve(cwd, item.Reason.FileName), path = references.Redirect(original);
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
                                        if ((resolution.Extension == ".js"u8 || resolution.Extension == ".jsx"u8 || resolution.Extension == ".mjs"u8 || resolution.Extension == ".cjs"u8)
                                            && !(projectOptions.AllowJs ?? projectOptions.CheckJs == true))
                                            continue;
                                        if ((resolution.Extension == ".jsx"u8 || resolution.Extension == ".tsx"u8) && projectOptions.Jsx == JsxEmit.None)
                                            continue;
                                        int depth = item.Depth + (resolution.External ? 1 : 0);
                                        bool externalJs = resolution.External && (resolution.Extension == ".js"u8 || resolution.Extension == ".jsx"u8 || resolution.Extension == ".mjs"u8 || resolution.Extension == ".cjs"u8);
                                        if (externalJs && depth > (projectOptions.MaxNodeModuleJsDepth ?? 0))
                                        {
                                            if (!existingDependencies.Contains(resolution.FileName, files.Comparer))
                                                existingDependencies.Add(resolution.FileName);
                                            continue;
                                        }
                                        if (!existingDependencies.Contains(resolution.FileName, files.Comparer))
                                            existingDependencies.Add(resolution.FileName);
                                        pending.AddLast(
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
                        if (!references.UseSources && references.Outputs.ContainsKey(entry.Path)) continue;
                        includeFailures.Add((entry.Path, parsed.Diagnostics ?? [new(Messages.File_0_not_found, 0, 0, [entry.Path])]));
                        continue;
                    }
                    if (parsed.Diagnostics is not null)
                        diagnostics.AddRange(parsed.Diagnostics);
                    int currentDepth = loadedDepth[entry.Path];
                    var dependencies = new List<Utf8String>();
                    var resolutions = new List<ModuleReference>();
                    var resolver = Resolver(entry.Project);
                    var options = entry.Project.Options;
                    Utf8String resolutionFile = references.Find(entry.Path)?.Source ?? entry.Path;
                    void Include(
                        Utf8String path,
                        FileIncludeKind kind,
                        int pos,
                        bool lib = false,
                        int? depth = null,
                        PackageId? package = null,
                        int length = 0)
                    {
                        dependencies.Add(path);
                        pending.AddLast((new(kind, path, entry.Path, pos, length) { PackageId = package }, lib, depth ?? currentDepth, package));
                    }
                    if (!skipModuleResolution && options.NoResolve != true)
                    {
                        foreach (var reference in syntax.ReferencedFiles)
                        {
                            Utf8String path = RootPath(CompilerPath.Resolve(CompilerPath.DirectoryName(entry.Path), reference.FileName));
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
                    if (!skipModuleResolution && options.NoLib != true)
                        foreach (var reference in syntax.LibReferenceDirectives)
                        {
                            var definition = OptionDefinitions.All.First(option => option.Name == Utf8Literals.Lib);
                            if (!definition.Values.Contains(reference.FileName.ToLowerInvariant()))
                            {
                                var suggestion = await Semantics.SpellingSuggestions.FindAsync(reference.FileName, definition.Values,
                                    name => new((Utf8String?)name), Utf8StringComparer.Ordinal.Compare, cancellation: cancellation).ConfigureAwait(false);
                                AddIncludeDiagnostic(new(suggestion is null ? Messages.Cannot_find_lib_definition_for_0
                                    : Messages.Cannot_find_lib_definition_for_0_Did_you_mean_1, reference.Pos, reference.End - reference.Pos,
                                    suggestion is { } suggested ? [reference.FileName, suggested] : [reference.FileName]) { FileName = entry.Path });
                                continue;
                            }
                            Include(
                                await LibraryPath(reference.FileName).ConfigureAwait(false),
                                FileIncludeKind.Library,
                                reference.Pos,
                                true,
                                length: reference.End - reference.Pos);
                        }
                    var imports = new List<(SyntaxNode? Node, Utf8String Name)>();
                    bool javaScript = syntax.ScriptKind is ScriptKind.JS or ScriptKind.JSX;
                    if (options.ImportHelpers == true && (javaScript || !syntax.IsDeclarationFile
                        && (syntax.ExternalModuleIndicator is not null
                            || options.IsolatedModules == true
                            || options.VerbatimModuleSyntax == true)))
                        imports.Add((null, Utf8Literals.Tslib));
                    Utf8String? Pragma(Utf8String name) =>
                        syntax.Pragmas.LastOrDefault(p => p.Name == name).Arguments?.GetValueOrDefault(Utf8Literals.Factory).Value;
                    if ((javaScript || syntax.ScriptKind == ScriptKind.TSX) && Pragma(Utf8Literals.Jsxruntime) != Utf8Literals.Classic
                        && (options.Jsx is JsxEmit.ReactJSX or JsxEmit.ReactJSXDev || options.JsxImportSource is not null
                            || Pragma(Utf8Literals.Jsximportsource) is not null || Pragma(Utf8Literals.Jsxruntime) == Utf8Literals.Automatic))
                    {
                        Utf8String jsx = Pragma(Utf8Literals.Jsximportsource) ?? options.JsxImportSource ?? Utf8Literals.React;
                        imports.Add((null, jsx + (options.Jsx == JsxEmit.ReactJSXDev ? Utf8Literals.JsxDevRuntime : Utf8Literals.JsxRuntime)));
                    }
                    imports.AddRange(syntax.Imports.Select(n => (Node: (SyntaxNode?)n, Name: ImportText(n))));
                    foreach (var import in imports)
                    {
                        if (skipModuleResolution || import.Name.Length == 0)
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
                        bool js = resolved.Extension == ".js"u8 || resolved.Extension == ".jsx"u8 || resolved.Extension == ".mjs"u8 || resolved.Extension == ".cjs"u8;
                        int depth = currentDepth + (resolved.External ? 1 : 0);
                        if (js && !(options.AllowJs ?? options.CheckJs == true))
                            continue;
                        if (js && resolved.External && resolved.FileName.Contains("/node_modules/"u8, StringComparison.Ordinal)
                            && depth > (options.MaxNodeModuleJsDepth ?? 0))
                        {
                            // An elided dependency can still be loaded through an explicit root or a shallower import.
                            dependencies.Add(resolved.FileName);
                            continue;
                        }
                        if ((resolved.Extension == ".tsx"u8 || resolved.Extension == ".jsx"u8) && options.Jsx == JsxEmit.None)
                            continue;
                        Include(resolved.FileName, FileIncludeKind.Import, import.Node?.Pos ?? 0, depth: depth, package: resolved.PackageId,
                            length: import.Node is null ? 0 : import.Node.End - import.Node.Pos);
                    }
                    foreach (var augmentation in skipModuleResolution ? [] : syntax.ModuleAugmentations.OfType<StringLiteralNode>())
                    {
                        var mode = UsageMode(augmentation, entry.Path, options, parsed.Format, parsed.PackageType);
                        var resolved = await resolver.ResolveAsync(
                            augmentation.Text,
                            resolutionFile,
                            mode,
                            cancellation: cancellation).ConfigureAwait(false);
                        resolutions.Add(new(augmentation.Text, mode, augmentation, resolved, Augmentation: true));
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

        private async ValueTask<ParsedSource> ParseAndBind(Utf8String path, ParsedConfig project, bool library)
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
            Utf8String path, ParsedConfig project, bool library)
        {
            // Parallel execution may finish in any order; publication above always follows discovery order.
            var mapper = config.ContentMappers.FirstOrDefault(m => m.Extensions.Any(e => path.EndsWith(e, StringComparison.Ordinal)));
            if (concurrency > 1 && mapper is null)
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            cancellation.ThrowIfCancellationRequested();
            var resolver = Resolver(project);
            var scope = library || skipModuleResolution ? null : resolver.Packages.Scope(CompilerPath.DirectoryName(path));
            Utf8String ext = ModuleResolver.Extension(path);
            bool explicitFormat = path.EndsWith(".mts"u8, StringComparison.Ordinal) || path.EndsWith(".cts"u8, StringComparison.Ordinal)
                || path.EndsWith(".mjs"u8, StringComparison.Ordinal) || path.EndsWith(".cjs"u8, StringComparison.Ordinal);
            Utf8String type = (!explicitFormat && resolver.ResolutionKind != Utf8Literals.Bundler
                || path.Contains("/node_modules/"u8, StringComparison.Ordinal))
                ? scope?.Type ?? Utf8String.Empty
                : Utf8String.Empty;
            var format = ext == ".mts"u8 || ext == ".mjs"u8 || ext == ".d.mts"u8 || !(ext == ".cts"u8 || ext == ".cjs"u8 || ext == ".d.cts"u8) && type == Utf8Literals.Module
                ? ReferenceResolutionMode.Import : ReferenceResolutionMode.Require;
            if (!(ext == ".mts"u8 || ext == ".mjs"u8 || ext == ".d.mts"u8 || ext == ".cts"u8 || ext == ".cjs"u8 || ext == ".d.cts"u8
                || ext == ".ts"u8 || ext == ".tsx"u8 || ext == ".d.ts"u8 || ext == ".js"u8 || ext == ".jsx"u8))
                format = ReferenceResolutionMode.Unspecified;
            var options = project.Options;
            ModuleDetectionKind detection = options.EmitModuleDetectionKind;
            bool force = !CompilerPath.IsDeclarationFile(path) && (detection == ModuleDetectionKind.Force || detection == ModuleDetectionKind.Auto
                && (ext == ".mts"u8 || ext == ".cts"u8 || ext == ".mjs"u8 || ext == ".cjs"u8
                    || ImpliedMode(path, options, format, type) == ReferenceResolutionMode.Import));
            var parseOptions = new ParseOptions(path, ForceExternalModule: force,
                JsxExternalModule: detection == ModuleDetectionKind.Auto && options.Jsx is JsxEmit.ReactJSX or JsxEmit.ReactJSXDev);
            if (supplemental.TryGetValue(path, out var mappedSource))
                return new(mappedSource.Syntax, parseOptions, format, scope?.Directory ?? Utf8String.Empty, type);
            if (options.AllowNonTsExtensions != true && !SupportedSource(path, project))
            {
                bool js = ext == ".js"u8 || ext == ".jsx"u8 || ext == ".mjs"u8 || ext == ".cjs"u8;
                var diagnostic = new Diagnostic(ext.Length == 0 ? Messages.Could_not_resolve_the_path_0_with_the_extensions_Colon_1
                    : js ? Messages.File_0_is_a_JavaScript_file_Did_you_mean_to_enable_the_allowJs_option
                    : Messages.File_0_has_an_unsupported_extension_The_only_supported_extensions_are_1, 0, 0,
                    js ? [path] : [path, SupportedExtensionsText(project)]);
                return new(null, parseOptions, format, scope?.Directory ?? Utf8String.Empty, type, Diagnostics: [diagnostic], FailedLookup: true);
            }
            SourceText? source = (fs as LibraryFileSystem)?.ReadBundledSource(path);
            byte[]? bytes = source is null ? fs.ReadFile(path) : null;
            if (source is null && bytes is null)
                return new(null, parseOptions, format, scope?.Directory ?? Utf8String.Empty, type);
            if (bytes is not null)
                bytes = SourceEncoding.DecodeOwnedBytes(bytes);
            if (mapper is not null)
                return await ParseMapped(
                    mapper,
                    parseOptions,
                    source ?? SourceText.FromOwnedBytes(bytes!),
                    format,
                    scope?.Directory ?? Utf8String.Empty,
                    type).ConfigureAwait(false);
            if (previous?.GetFile(path) is { } old && old.ParseOptions == parseOptions && old.Syntax.Source.Bytes.Span.SequenceEqual(source is null ? bytes : source.Bytes.Span))
            {
                Interlocked.Increment(ref reused);
                return new(old.Syntax, parseOptions, format, scope?.Directory ?? Utf8String.Empty, type);
            }
            var syntax = await Parser.ParseSourceFileAsync(parseOptions, source ?? SourceText.FromOwnedBytes(bytes!), cancellation).ConfigureAwait(false);
            return new(syntax, parseOptions, format, scope?.Directory ?? Utf8String.Empty, type);
        }
    }
}

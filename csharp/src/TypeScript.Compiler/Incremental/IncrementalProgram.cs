using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Incremental;

/// <summary>Owns one build generation. Reuse copies state; cancellation never publishes into an older generation.</summary>
public sealed partial class IncrementalProgram
{
    private readonly IFileSystem fileSystem;
    private readonly Utf8StringComparer comparer;
    private readonly bool hashWithText;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IncrementalSnapshot snapshot;
    private CheckerPool? checkerPool;
    private IReadOnlyList<Diagnostic>? diagnostics;
    private IReadOnlyList<Diagnostic> globalDiagnostics = [];
    private readonly HashSet<Utf8String> checkedFiles;
    private readonly HashSet<Utf8String> affectedFiles;
    private bool hasChangedDeclaration;
    public CompilerProgram Program { get; }
    public Utf8String BuildInfoFileName { get; }
    public IReadOnlyCollection<Utf8String> CheckedFiles => checkedFiles;
    public IReadOnlyCollection<Utf8String> AffectedFiles => affectedFiles;
    public bool HasChangedDeclaration => hasChangedDeclaration;
    private CompilerOptions Options => Program.Configuration.Options;
    private bool CanUseState => IncrementalOptions.IsIncremental(Options) || Options.Build != true;

    private IncrementalProgram(CompilerProgram program, IFileSystem fileSystem, bool hashWithText, Utf8String[] mapperIdentities)
    {
        Program = program;
        this.fileSystem = fileSystem;
        this.hashWithText = hashWithText;
        comparer = fileSystem.CaseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
        snapshot = new(Options, comparer, mapperIdentities) { CheckPending = Options.NoCheck == true };
        BuildInfoFileName = IncrementalOptions.GetBuildInfoFileName(program.Configuration, program.CurrentDirectory, fileSystem.CaseSensitive);
        checkedFiles = new(comparer); affectedFiles = new(comparer);
    }

    public static async ValueTask<IncrementalProgram> CreateAsync(CompilerProgram program, IFileSystem fileSystem,
        IncrementalProgram? previous = null, IReadOnlyList<Utf8String>? mapperIdentities = null,
        Utf8String defaultLibraryDirectory = default, bool hashWithText = false, bool reuseBuildInfo = true, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var identities = (mapperIdentities ?? []).Order(Utf8StringComparer.Ordinal).ToArray();
        var result = new IncrementalProgram(program, fileSystem, hashWithText, identities);
        IncrementalSnapshot? old = null;
        if (previous is not null)
        {
            await previous.gate.WaitAsync(cancellation).ConfigureAwait(false);
            try { old = previous.snapshot.Copy(); }
            finally { previous.gate.Release(); }
        }
        else if (reuseBuildInfo && IncrementalOptions.IsIncremental(result.Options) && result.BuildInfoFileName.Length != 0
            && fileSystem.ReadFile(result.BuildInfoFileName) is { } bytes && BuildInfo.TryRead(bytes) is { IsIncremental: true, IsValidVersion: true } info)
        {
            if (defaultLibraryDirectory.Length == 0 && fileSystem is LibraryFileSystem libraries) defaultLibraryDirectory = libraries.LibraryDirectory;
            if (defaultLibraryDirectory.Length == 0) defaultLibraryDirectory = program.DefaultLibraryDirectory;
            try { old = IncrementalSnapshot.FromBuildInfo(info, result.BuildInfoFileName, defaultLibraryDirectory, result.comparer); }
            catch (InvalidDataException) { old = null; }
        }
        if (old is not null && !old.MapperIdentities.SequenceEqual(identities)) old = null;
        await result.InitializeAsync(old, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return result;
    }

    private async ValueTask InitializeAsync(IncrementalSnapshot? previous, CancellationToken cancellation)
    {
        if (!CanUseState) { snapshot.BuildInfoPending = true; return; }
        if (previous is not null)
        {
            snapshot.BuildInfoPending = previous.BuildInfoPending;
            snapshot.HasErrors = previous.HasErrors;
            snapshot.SemanticErrors = previous.SemanticErrors;
            snapshot.PackageJsons = previous.PackageJsons;
            snapshot.MissingPackageJsons = previous.MissingPackageJsons;
            snapshot.Changed.UnionWith(previous.Changed);
            foreach (var pending in previous.PendingEmit) snapshot.PendingEmit[pending.Key] = pending.Value;
            if (Options.Composite == true) snapshot.LatestChangedDeclaration = previous.LatestChangedDeclaration;
        }
        else snapshot.BuildInfoPending = IncrementalOptions.IsIncremental(Options);
        var checker = await Program.CreateCheckerAsync(cancellation).ConfigureAwait(false);
        bool reuseDiagnostics = previous is not null && !IncrementalOptions.HaveChanges(previous.Options, Options, OptionEffects.SemanticDiagnostics);
        bool reuseSignatures = Options.Composite == true && previous is not null
            && !IncrementalOptions.HaveChanges(previous.Options, Options, OptionEffects.DeclarationPath);
        foreach (var file in Program.SourceFiles)
        {
            cancellation.ThrowIfCancellationRequested();
            var path = file.Syntax.FileName;
            var version = BuildInfo.ComputeHash(file.Mapping is { } mapping
                ? mapping.Original.Text + "\0"u8 + mapping.TransformIdentity : file.Syntax.Source.Text, hashWithText);
            bool global = AffectsGlobalScope(file);
            var references = await ReferencesAsync(file, checker, cancellation).ConfigureAwait(false);
            if (references.Count != 0) snapshot.References[path] = references;
            Utf8String? signature = version;
            if (previous is not null)
            {
                signature = null;
                if (!previous.Files.TryGetValue(path, out var old)) AddChanged(path);
                else
                {
                    signature = old.Signature;
                    if (old.Version != version || old.AffectsGlobalScope != global || old.ImpliedNodeFormat != (int)file.ImpliedFormat
                        || !references.SetEquals(previous.References.GetValueOrDefault(path) ?? [])
                        || references.Any(reference => Program.GetFile(reference) is null && previous.Files.ContainsKey(reference))) AddChanged(path);
                }
                if (!snapshot.Changed.Contains(path))
                {
                    if (previous.EmitDiagnostics.TryGetValue(path, out var emitDiagnostics)) snapshot.EmitDiagnostics[path] = emitDiagnostics
                        .Select(diagnostic => diagnostic.FromBuildInfo ? diagnostic : Program.RepopulateDiagnostic(diagnostic)).ToArray();
                    if (reuseDiagnostics && (!file.Syntax.IsDeclarationFile || previous.Options.SkipLibCheck == Options.SkipLibCheck)
                        && (!file.Library || previous.Options.SkipDefaultLibCheck == Options.SkipDefaultLibCheck)
                        && previous.SemanticDiagnostics.TryGetValue(path, out var semantic)) snapshot.SemanticDiagnostics[path] = semantic
                            .Select(diagnostic => diagnostic.FromBuildInfo ? diagnostic : Program.RepopulateDiagnostic(diagnostic)).ToArray();
                }
                if (reuseSignatures && previous.EmitSignatures.TryGetValue(path, out var emitted))
                    snapshot.EmitSignatures[path] = emitted with { DifferentOptions = emitted.DifferentOptions ^ (previous.Options.DeclarationMap == true != (Options.DeclarationMap == true)) };
            }
            else snapshot.PendingEmit[path] = BuildInfo.GetEmitKind(Options);
            snapshot.Files[path] = new(version, signature, global, (int)file.ImpliedFormat);
        }
        if (previous is null) return;
        bool globalRemoved = previous.Files.Any(entry => entry.Value.AffectsGlobalScope
            && (!snapshot.Files.TryGetValue(entry.Key, out var file) || !file.AffectsGlobalScope));
        if (globalRemoved)
            foreach (var file in Program.SourceFiles.Where(file => !file.Library)) AddChanged(file.Syntax.FileName);
        if (previous.Files.Keys.Any(path => !snapshot.Files.ContainsKey(path))) snapshot.BuildInfoPending = true;
        if (!globalRemoved)
        {
            var pending = IncrementalOptions.HaveChanges(previous.Options, Options, OptionEffects.Emit)
                ? BuildInfo.GetEmitKind(Options) : BuildInfo.GetPendingEmitKind(BuildInfo.GetEmitKind(Options), BuildInfo.GetEmitKind(previous.Options));
            if (pending != FileEmitKind.None)
                foreach (var file in Program.SourceFiles.Where(file => !snapshot.Changed.Contains(file.Syntax.FileName))) AddPending(file.Syntax.FileName, pending);
        }
        if (snapshot.SemanticDiagnostics.Count != Program.SourceFiles.Count && previous.CheckPending != snapshot.CheckPending) snapshot.BuildInfoPending = true;
    }

    private static bool AffectsGlobalScope(ProgramFile file) => file.Syntax.ModuleAugmentations.Any(node => node.Parent is ModuleDeclarationNode { Keyword: SyntaxKind.GlobalKeyword })
        || !file.Binding.IsModule && file.Syntax.ScriptKind != ScriptKind.JSON
            && file.Syntax.Statements?.Any(node => node is not ModuleDeclarationNode { Name: StringLiteralNode }) == true;

    private async ValueTask<HashSet<Utf8String>> ReferencesAsync(ProgramFile file, Checker checker, CancellationToken cancellation)
    {
        var references = new HashSet<Utf8String>(comparer);
        void AddSymbol(Symbol? symbol)
        {
            if (symbol is null) return;
            foreach (var declaration in symbol.Declarations)
                if (SemanticSyntax.Source(declaration) is { } source && source != file.Syntax) references.Add(source.FileName);
        }
        foreach (var name in file.Syntax.Imports.Concat(file.Syntax.ModuleAugmentations).Where(node => node is StringLiteralNode))
            AddSymbol(await checker.GetSymbolAtLocationAsync(name, cancellation).ConfigureAwait(false));
        foreach (var reference in file.Syntax.ReferencedFiles)
        {
            var path = CompilerPath.Resolve(CompilerPath.DirectoryName(file.Syntax.FileName), reference.FileName);
            var redirect = Program.ProjectReferences.Redirect(CompilerPath.Resolve(Program.CurrentDirectory, reference.FileName));
            references.Add(redirect != CompilerPath.Resolve(Program.CurrentDirectory, reference.FileName) ? redirect : path);
        }
        foreach (var reference in file.Resolutions.Where(reference => reference.TypeReference && reference.Resolution.IsResolved))
            references.Add(Program.GetFile(reference.Resolution.FileName)?.Syntax.FileName ?? reference.Resolution.FileName);
        foreach (var symbol in checker.Symbols.Globals.Where(entry => entry.Key.StartsWith((byte)'"')).Select(entry => entry.Value)
            .Concat(checker.Symbols.PatternModules.Select(module => module.Symbol))) AddSymbol(symbol);
        return references;
    }

    private void AddChanged(Utf8String path) { snapshot.Changed.Add(path); snapshot.BuildInfoPending = true; }
    private void AddPending(Utf8String path, FileEmitKind kind)
    {
        snapshot.PendingEmit[path] = snapshot.PendingEmit.GetValueOrDefault(path) | kind;
        if ((kind & FileEmitKind.DeclarationErrors) != 0) snapshot.EmitDiagnostics.Remove(path);
        snapshot.BuildInfoPending = true;
    }
}

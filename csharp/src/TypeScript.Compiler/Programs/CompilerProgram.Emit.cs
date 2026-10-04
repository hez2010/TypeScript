using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Programs;

public sealed partial class CompilerProgram
{
    public ValueTask<EmitResult> EmitAsync(EmitOptions? emitOptions = null, CancellationToken cancellation = default)
        => EmitCoreAsync(emitOptions, null, cancellation);

    internal ValueTask<EmitResult> EmitWithCheckerAsync(EmitOptions emitOptions, Checker checker, CancellationToken cancellation)
        => EmitCoreAsync(emitOptions, checker, cancellation);

    private async ValueTask<EmitResult> EmitCoreAsync(EmitOptions? emitOptions, Checker? externalChecker, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        emitOptions ??= new();
        var targets = emitOptions.SourceFiles ?? SourceFiles.Select(file => file.Syntax).ToArray();
        foreach (var source in targets)
            if (!ReferenceEquals(GetFile(source.FileName)?.Syntax, source))
                throw new ArgumentException("Source file belongs to another program", nameof(emitOptions));
        var options = Configuration.Options;
        bool signature = emitOptions.Only == EmitOnly.BuilderSignature;
        if (!emitOptions.Force && !signature && options.NoEmit == true)
            return new(emitOptions.SourceFiles is not null, [], [], []);
        using var captured = CompilationCapture.Current?.Begin("emit"u8, "emit"u8);
        var checker = externalChecker ?? await CreateCheckerAsync(cancellation);
        if (!emitOptions.Force && !signature && options.NoEmitOnError == true)
        {
            var errors = await DiagnosticsBeforeEmitAsync(checker, targets, externalChecker is null, cancellation);
            if (errors.Count != 0) return new(true, errors, [], []);
        }
        bool forceDeclarations = signature || emitOptions.Force && emitOptions.Only == EmitOnly.Declarations;
        bool forceJavaScript = emitOptions.Force && emitOptions.Only == EmitOnly.JavaScript;
        bool skipped = false;
        List<Diagnostic> diagnostics = [];
        List<Utf8String> emitted = [];
        List<SourceMapEmitResult> maps = [];
        foreach (var source in targets)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!SourceFileMayBeEmitted(source, forceDeclarations, forceJavaScript)) continue;
            var paths = GetOutputPaths(source, forceDeclarations, forceJavaScript, emitOptions.Force && emitOptions.Only == EmitOnly.Declarations);
            var emitter = new FileEmitter(this, checker, source, emitOptions, cancellation);
            var result = await emitter.EmitAsync(paths);
            skipped |= result.EmitSkipped;
            diagnostics.AddRange(result.Diagnostics);
            emitted.AddRange(result.EmittedFiles);
            maps.AddRange(result.SourceMaps);
        }
        return new(skipped, diagnostics.ToArray(), emitted.ToArray(), maps.ToArray());
    }

    public ValueTask<IReadOnlyList<Diagnostic>> GetDeclarationDiagnosticsAsync(SourceFileNode? source = null,
        CancellationToken cancellation = default)
        => GetDeclarationDiagnosticsCoreAsync(source, null, cancellation);

    internal ValueTask<IReadOnlyList<Diagnostic>> GetDeclarationDiagnosticsWithCheckerAsync(SourceFileNode source,
        Checker checker, CancellationToken cancellation)
        => GetDeclarationDiagnosticsCoreAsync(source, checker, cancellation);

    private async ValueTask<IReadOnlyList<Diagnostic>> GetDeclarationDiagnosticsCoreAsync(SourceFileNode? source,
        Checker? externalChecker, CancellationToken cancellation)
    {
        if (source is not null && !ReferenceEquals(GetFile(source.FileName)?.Syntax, source))
            throw new ArgumentException("Source file belongs to another program", nameof(source));
        var checker = externalChecker ?? await CreateCheckerAsync(cancellation);
        List<Diagnostic> diagnostics = [];
        foreach (var file in source is null ? SourceFiles.Select(file => file.Syntax) : [source])
        {
            cancellation.ThrowIfCancellationRequested();
            if (!SourceFileMayBeEmitted(file) || file.ScriptKind == ScriptKind.JSON) continue;
            var transform = new DeclarationTransformer(new(), checker, Configuration.Options, cancellation);
            await transform.VisitAsync(file);
            diagnostics.AddRange(transform.Diagnostics);
        }
        return DiagnosticCollection.SortAndDeduplicate(diagnostics);
    }

    private async ValueTask<IReadOnlyList<Diagnostic>> DiagnosticsBeforeEmitAsync(Checker checker,
        IReadOnlyList<SourceFileNode> targets, bool includeGlobalDiagnostics, CancellationToken cancellation)
    {
        List<Diagnostic> result = [.. Configuration.Diagnostics];
        foreach (var source in targets)
            result.AddRange(DiagnosticCollection.SortAndDeduplicate(source.ParseDiagnostics.Concat(source.JSDiagnostics)
                .Select(diagnostic => diagnostic with { FileName = source.FileName })));
        if (result.Count != Configuration.Diagnostics.Length) return result.ToArray();
        result.AddRange(Diagnostics.Where(diagnostic => !Configuration.Diagnostics.Contains(diagnostic, DiagnosticEqualityComparer.Instance)));
        if (Configuration.Options.ListFilesOnly == true) return result.ToArray();
        if (includeGlobalDiagnostics) result.AddRange(DiagnosticCollection.SortAndDeduplicate(checker.DetailedDiagnosticsForFile(null)));
        if (result.Count != Configuration.Diagnostics.Length) return result.ToArray();
        foreach (var source in targets)
            if (!checker.SkipProgramFile(source)) await checker.CheckSourceFileAsync(source, cancellation);
        foreach (var source in targets) result.AddRange(DiagnosticCollection.SortAndDeduplicate(checker.DetailedDiagnosticsForProgramFile(source)));
        if (includeGlobalDiagnostics) result.AddRange(DiagnosticCollection.SortAndDeduplicate(checker.DetailedDiagnosticsForFile(null)));
        if (result.Count == Configuration.Diagnostics.Length && (Configuration.Options.Declaration == true || Configuration.Options.Composite == true))
            foreach (var source in targets)
            {
                if (!SourceFileMayBeEmitted(source) || source.ScriptKind == ScriptKind.JSON) continue;
                var transform = new DeclarationTransformer(new(), checker, Configuration.Options, cancellation);
                await transform.VisitAsync(source);
                result.AddRange(DiagnosticCollection.SortAndDeduplicate(transform.Diagnostics));
            }
        return result.ToArray();
    }

    private sealed partial class FileEmitter(CompilerProgram program, Checker checker, SourceFileNode source,
        EmitOptions emitOptions, CancellationToken cancellation)
    {
        private readonly CompilerOptions options = program.Configuration.Options;
        private readonly List<Diagnostic> diagnostics = [];
        private readonly List<Utf8String> emitted = [];
        private readonly List<SourceMapEmitResult> maps = [];
        private bool skipped;
        private Utf8String NewLine => options.NewLine == NewLineKind.CRLF ? "\r\n"u8 : "\n"u8;

        internal async ValueTask<EmitResult> EmitAsync(EmitOutputPaths paths)
        {
            if (emitOptions.Only is EmitOnly.All or EmitOnly.JavaScript && paths.JavaScript.Length != 0)
            {
                if (!emitOptions.Force && (options.NoEmit == true || program.IsEmitBlocked(paths.JavaScript))) skipped = true;
                else
                {
                    var context = new EmitContext();
                    var tree = await ScriptTransformer.TransformAsync(source, context, program, checker, cancellation);
                    var printer = new SyntaxPrinter(new()
                    {
                        RemoveComments = options.RemoveComments == true, NewLine = NewLine,
                        NoEmitHelpers = options.NoEmitHelpers == true, TargetYear = options.EmitTargetYear,
                        InlineSources = options.InlineSources == true
                    }, context);
                    await PrintAsync(tree, printer, paths.JavaScript, paths.SourceMap, options.SourceMap == true, options.InlineSourceMap == true);
                }
            }
            if (emitOptions.Only != EmitOnly.JavaScript && paths.Declaration.Length != 0)
            {
                var context = new EmitContext();
                var transform = new DeclarationTransformer(context, checker, options, cancellation, paths.Declaration);
                var tree = (SourceFileNode)(await transform.VisitAsync(source))!;
                AddSupplementalReferences(tree, paths.Declaration);
                diagnostics.AddRange(transform.Diagnostics);
                bool signature = emitOptions.Only == EmitOnly.BuilderSignature;
                if (!emitOptions.Force && !signature && (options.NoEmit == true || program.IsEmitBlocked(paths.Declaration) || transform.Diagnostics.Count != 0)) skipped = true;
                else
                {
                    var mapping = program.GetFile(source.FileName)?.Mapping;
                    var originalName = program.IncludeReasons.GetValueOrDefault(source.FileName)?
                        .FirstOrDefault(reason => reason.Kind == FileIncludeKind.MapperSupplemental)?.ContainingFile ?? source.FileName;
                    var printer = new SyntaxPrinter(new()
                    {
                        RemoveComments = options.RemoveComments == true, NewLine = NewLine, NoEmitHelpers = true,
                        TargetYear = options.EmitTargetYear, OnlyPrintJSDocStyle = true, OmitBraceSourceMapPositions = true,
                        MapSourcePosition = mapping is null ? null : (file, position) => file.FileName != source.FileName
                            ? new(file.FileName, file.Source, position) : mapping.Map.TryMapExactPosition(position, out int original)
                                ? new(originalName, mapping.Original, original) : null
                    }, context);
                    await PrintAsync(tree, printer, paths.Declaration, paths.DeclarationMap, !signature && options.DeclarationMap == true, false);
                }
            }
            return new(skipped, DiagnosticCollection.SortAndDeduplicate(diagnostics), emitted.ToArray(), maps.ToArray());
        }

        private void AddSupplementalReferences(SourceFileNode tree, Utf8String declarationPath)
        {
            bool force = emitOptions.Only == EmitOnly.BuilderSignature || emitOptions.Force && emitOptions.Only == EmitOnly.Declarations;
            List<FileReference>? references = null;
            foreach (var file in program.SourceFiles)
            {
                if (program.IncludeReasons.GetValueOrDefault(file.Syntax.FileName)?.Any(reason =>
                    reason.Kind == FileIncludeKind.MapperSupplemental && reason.ContainingFile == source.FileName) != true
                    || !program.SourceFileMayBeEmitted(file.Syntax, force)) continue;
                var path = program.GetOutputPaths(file.Syntax, force).Declaration;
                if (path.Length == 0) continue;
                references ??= [.. tree.ReferencedFiles];
                var relative = CompilerPath.Relative(
                    CompilerPath.DirectoryName(CompilerPath.Resolve(program.CurrentDirectory, declarationPath)),
                    CompilerPath.Resolve(program.CurrentDirectory, path), program.UseCaseSensitiveFileNames);
                if (!relative.StartsWith((byte)'.') && !CompilerPath.IsAbsolute(relative)) relative = "./"u8 + relative;
                references.Add(new(relative, -1, -1));
            }
            if (references is not null) tree.ReferencedFiles = references;
        }
    }
}

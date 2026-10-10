using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;

namespace TypeScript.Compiler.Incremental;

public sealed partial class IncrementalProgram
{
    public async ValueTask<EmitResult> EmitAsync(EmitOptions? options = null,
        Func<Utf8String, BuildInfo, CancellationToken, ValueTask>? writeBuildInfo = null, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        var previous = snapshot;
        snapshot = snapshot.Copy();
        try { return await EmitCoreAsync(options ?? new(), writeBuildInfo, cancellation).ConfigureAwait(false); }
        catch { snapshot = previous; checkerPool = null; diagnostics = null; throw; }
        finally { gate.Release(); }
    }

    private async ValueTask<EmitResult> EmitCoreAsync(EmitOptions options,
        Func<Utf8String, BuildInfo, CancellationToken, ValueTask>? writeBuildInfo, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        List<Diagnostic> errors = [];
        List<Utf8String> emitted = [];
        List<SourceMapEmitResult> maps = [];
        bool skipped = false;
        object writeGate = new();
        bool noOutput = !options.Force && options.Only != EmitOnly.BuilderSignature && Options.NoEmit == true;
        if (!noOutput && !options.Force && options.Only != EmitOnly.BuilderSignature && Options.NoEmitOnError == true)
        {
            errors.AddRange(await GetDiagnosticsCoreAsync(cancellation).ConfigureAwait(false));
            if (errors.Count == 0 && (Options.Declaration == true || Options.Composite == true))
                errors.AddRange(await GetDeclarationDiagnosticsCoreAsync(cancellation).ConfigureAwait(false));
            noOutput = skipped = errors.Count != 0;
        }
        if (!noOutput)
        {
            if (options.SourceFiles is not null || options.Force || options.Only == EmitOnly.BuilderSignature)
            {
                var result = await Program.EmitAsync(options with { WriteFile = Write }, cancellation).ConfigureAwait(false);
                return result;
            }
            if (CanUseState) await CollectAffectedFilesAsync(cancellation).ConfigureAwait(false);
            var work = new List<(ProgramFile File, Utf8String Path, FileEmitKind Pending)>();
            foreach (var file in Program.SourceFiles)
            {
                var path = file.Syntax.FileName;
                var pending = CanUseState ? snapshot.PendingEmit.GetValueOrDefault(path) : BuildInfo.GetEmitKind(Options);
                if (options.Only == EmitOnly.Declarations) pending &= FileEmitKind.AllDeclarations;
                if (pending == FileEmitKind.None)
                {
                    if (snapshot.EmitDiagnostics.TryGetValue(path, out var cached))
                    {
                        errors.AddRange(cached);
                        skipped = true;
                    }
                    continue;
                }
                if (!Program.SourceFileMayBeEmitted(file.Syntax))
                {
                    snapshot.PendingEmit.Remove(path); snapshot.BuildInfoPending = true;
                    continue;
                }
                work.Add((file, path, pending));
            }
            if (work.Count != 0)
            {
                var results = new EmitResult?[work.Count];
                // Measurement prototype (campaign 2026-10-06): the upstream loop emits every file
                // serially with one checker. Go queues one goroutine per file and borrows each
                // file's pooled checker (tsc/internal/compiler/program.go:1845-1880). This variant
                // does the same with the existing CheckerPool, and can be forced back to the
                // serial path with TSHARP_EMIT_CONCURRENCY=1 for an A/B measurement.
                int requested = int.TryParse(Environment.GetEnvironmentVariable("TSHARP_EMIT_CONCURRENCY"), out var configured) ? configured : 0;
                CheckerPool? pool = null;
                int workers = 1;
                if (requested != 1 && work.Count > 1 && Options.SingleThreaded != true)
                {
                    pool = await Program.CreateCheckerPoolAsync(false, cancellation).ConfigureAwait(false);
                    workers = Math.Max(1, Math.Min(requested > 1 ? requested : pool.Count, work.Count));
                }
                if (workers > 1)
                {
                    await Parallel.ForAsync(0, work.Count, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellation }, async (index, token) =>
                    {
                        var (file, _, pending) = work[index];
                        bool javascript = (pending & FileEmitKind.AllJavaScript) != 0;
                        bool declarations = (pending & FileEmitKind.AllDeclarations) != 0;
                        using var lease = await pool!.AcquireAsync(file.Syntax, token).ConfigureAwait(false);
                        results[index] = await Program.EmitWithCheckerAsync(new()
                        {
                            SourceFiles = [file.Syntax], WriteFile = Write,
                            Only = javascript && declarations ? EmitOnly.All : javascript ? EmitOnly.JavaScript : EmitOnly.Declarations
                        }, lease.Checker, token).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                else
                {
                    Checker? emitChecker = null;
                    for (int index = 0; index < work.Count; index++)
                    {
                        var (file, _, pending) = work[index];
                        bool javascript = (pending & FileEmitKind.AllJavaScript) != 0;
                        bool declarations = (pending & FileEmitKind.AllDeclarations) != 0;
                        emitChecker ??= await Program.CreateCheckerAsync(cancellation).ConfigureAwait(false);
                        results[index] = await Program.EmitWithCheckerAsync(new()
                        {
                            SourceFiles = [file.Syntax], WriteFile = Write,
                            Only = javascript && declarations ? EmitOnly.All : javascript ? EmitOnly.JavaScript : EmitOnly.Declarations
                        }, emitChecker, cancellation).ConfigureAwait(false);
                    }
                }
                for (int index = 0; index < work.Count; index++)
                {
                    var (_, path, pending) = work[index];
                    var result = results[index]!;
                    skipped |= result.EmitSkipped;
                    errors.AddRange(result.Diagnostics); emitted.AddRange(result.EmittedFiles); maps.AddRange(result.SourceMaps);
                    if (result.Diagnostics.Count == 0) snapshot.EmitDiagnostics.Remove(path);
                    else snapshot.EmitDiagnostics[path] = result.Diagnostics;
                    if (CanUseState)
                    {
                        var remaining = BuildInfo.GetPendingEmitKind(snapshot.PendingEmit.GetValueOrDefault(path), pending);
                        if (remaining == FileEmitKind.None) snapshot.PendingEmit.Remove(path); else snapshot.PendingEmit[path] = remaining;
                        snapshot.BuildInfoPending = true;
                    }
                }
            }
            foreach (var path in snapshot.PendingEmit.Keys.Where(path => Program.GetFile(path) is null).ToArray()) snapshot.PendingEmit.Remove(path);
        }
        if (options.SourceFiles is null && BuildInfoFileName.Length != 0 && !Program.IsEmitBlocked(BuildInfoFileName))
        {
            var info = CreateBuildInfo();
            byte[] bytes = info.ToJson();
            var packages = Program.Configuration.FileName.Length == 0 ? [] : Program.ExistingPackageJsons;
            var missingPackages = Program.Configuration.FileName.Length == 0 ? [] : Program.MissingPackageJsons;
            bool changed = snapshot.HasErrors != info.Errors || snapshot.SemanticErrors != info.SemanticErrors
                || !snapshot.PackageJsons.SequenceEqual(packages) || !snapshot.MissingPackageJsons.SequenceEqual(missingPackages);
            if (snapshot.BuildInfoPending || changed)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    if (writeBuildInfo is not null) await writeBuildInfo(BuildInfoFileName, info, cancellation).ConfigureAwait(false);
                    else fileSystem.WriteFile(BuildInfoFileName, bytes);
                    emitted.Add(BuildInfoFileName);
                    snapshot.BuildInfoPending = false;
                    snapshot.HasErrors = info.Errors;
                    snapshot.SemanticErrors = info.SemanticErrors;
                    snapshot.PackageJsons = packages.ToArray();
                    snapshot.MissingPackageJsons = missingPackages.ToArray();
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    skipped |= noOutput && Options.NoEmit == true;
                    errors.Add(new(Messages.Could_not_write_file_0_Colon_1, -1, 0, [BuildInfoFileName, Utf8String.FromString(error.Message)]));
                }
            }
        }
        return new(skipped, DiagnosticCollection.SortAndDeduplicate(errors), emitted, maps);

        async ValueTask Write(Utf8String path, Utf8String text, EmitWriteFileData data, CancellationToken token)
        {
            DateTime? originalTime = null;
            var source = data.SourceFile.FileName;
            if (CompilerPath.IsDeclarationFile(path) && (Options.Declaration == true || Options.Composite == true))
            {
                // Guarded because the parallel emitter calls this from several workers at once.
                lock (writeGate)
                {
                    if (CanUseState && snapshot.Files.TryGetValue(source, out var info))
                    {
                        if (info.Signature == info.Version)
                        {
                            var signature = BuildInfoDiagnostics.Signature(source, text, data.SourceMapUrlPosition, data.Diagnostics, hashWithText);
                            if (signature != info.Version) snapshot.Files[source] = info with { Signature = signature, Expanded = false };
                            snapshot.BuildInfoPending = true;
                        }
                        if (Options.Composite == true)
                        {
                            var signature = BuildInfo.ComputeHash(data.SourceMapUrlPosition >= 0 ? text[..data.SourceMapUrlPosition] : text, hashWithText);
                            if (snapshot.EmitSignatures.TryGetValue(source, out var previous) && previous.Text == signature)
                            {
                                if (!previous.DifferentOptions) { data.SkippedDeclarationWrite = true; return; }
                                if (Options.Build == true) originalTime = fileSystem.Stat(path)?.LastWriteTimeUtc;
                            }
                            else
                            {
                                snapshot.LatestChangedDeclaration = path;
                                hasChangedDeclaration = true;
                            }
                            snapshot.EmitSignatures[source] = new(signature);
                            snapshot.BuildInfoPending = true;
                        }
                    }
                }
            }
            if (options.WriteFile is not null) await options.WriteFile(path, text, data, token).ConfigureAwait(false);
            else fileSystem.WriteFile(path, text.Span);
            if (originalTime is { } time) fileSystem.SetTimes(path, time, time);
        }
    }

}

using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.Incremental;

public sealed partial class IncrementalProgram
{
    public async ValueTask<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        var previous = snapshot;
        snapshot = snapshot.Copy();
        try { return await GetDiagnosticsCoreAsync(cancellation).ConfigureAwait(false); }
        catch { snapshot = previous; checkerPool = null; diagnostics = null; throw; }
        finally { gate.Release(); }
    }

    private async ValueTask<IReadOnlyList<Diagnostic>> GetDiagnosticsCoreAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (diagnostics is not null) return diagnostics;
        List<Diagnostic> result = [.. Program.Configuration.Diagnostics];
        int initial = result.Count;
        foreach (var file in Program.SourceFiles)
            result.AddRange(file.Syntax.ParseDiagnostics.Concat(file.Syntax.JSDiagnostics).Select(diagnostic => diagnostic with { FileName = file.Syntax.FileName }));
        if (result.Count != initial)
            result.AddRange(Program.Diagnostics.Where(diagnostic => diagnostic.Code == Messages.The_content_mapper_0_failed_1_times_and_will_not_be_used.Code));
        if (result.Count == initial)
        {
            result.AddRange(Program.Diagnostics.Where(diagnostic => !Program.Configuration.Diagnostics.Contains(diagnostic, DiagnosticEqualityComparer.Instance)));
            if (Options.ListFilesOnly != true && Program.SourceFiles.Count != 0)
            {
                checkerPool ??= await Program.CreateCheckerPoolAsync(Options.SingleThreaded == true, cancellation).ConfigureAwait(false);
                using (var lease = await checkerPool.AcquireAsync(cancellation: cancellation).ConfigureAwait(false))
                    globalDiagnostics = lease.Checker.DetailedDiagnosticsForFile(null).ToArray();
                result.AddRange(globalDiagnostics);
                if (result.Count == initial && Options.NoCheck != true)
                {
                    if (CanUseState) await CollectAffectedFilesAsync(cancellation).ConfigureAwait(false);
                    var selected = Program.SourceFiles.Where(file => !snapshot.SemanticDiagnostics.ContainsKey(file.Syntax.FileName)).Select(file => file.Syntax).ToHashSet();
                    if (selected.Count != 0)
                    {
                        var current = await checkerPool.GetDiagnosticsAsync(cancellation, selected).ConfigureAwait(false);
                        var grouped = current.Semantic.ToLookup(diagnostic => diagnostic.FileName ?? default, comparer);
                        foreach (var file in selected)
                        {
                            snapshot.SemanticDiagnostics[file.FileName] = grouped[file.FileName]
                                .Where(diagnostic => !Program.IncludeDiagnostics.Contains(diagnostic)).ToArray();
                            checkedFiles.Add(file.FileName);
                        }
                        globalDiagnostics = current.Global;
                        snapshot.BuildInfoPending = true;
                    }
                    if (snapshot.SemanticDiagnostics.Count == Program.SourceFiles.Count) snapshot.CheckPending = false;
                    foreach (var file in Program.SourceFiles)
                    {
                        var semantic = snapshot.SemanticDiagnostics.GetValueOrDefault(file.Syntax.FileName) ?? [];
                        if (semantic.Any(diagnostic => diagnostic.FromBuildInfo))
                            snapshot.SemanticDiagnostics[file.Syntax.FileName] = semantic = semantic
                                .Select(diagnostic => diagnostic.FromBuildInfo ? Program.RepopulateDiagnostic(diagnostic) : diagnostic).ToArray();
                        result.AddRange(semantic.Where(diagnostic => Options.NoEmit != true || !diagnostic.SkippedOnNoEmit));
                        using var lease = await checkerPool.AcquireAsync(file.Syntax, cancellation).ConfigureAwait(false);
                        result.AddRange(lease.Checker.IncludeDiagnosticsForProgramFile(file.Syntax));
                    }
                    result.AddRange(globalDiagnostics);
                }
                if (Options.NoEmit == true && (Options.Declaration == true || Options.Composite == true) && result.Count == initial)
                    result.AddRange(await GetDeclarationDiagnosticsCoreAsync(cancellation).ConfigureAwait(false));
            }
        }
        cancellation.ThrowIfCancellationRequested();
        diagnostics = DiagnosticCollection.SortAndDeduplicate(result.Where(diagnostic => Options.NoEmit != true || !diagnostic.SkippedOnNoEmit));
        return diagnostics;
    }

    private async ValueTask<IReadOnlyList<Diagnostic>> GetDeclarationDiagnosticsCoreAsync(CancellationToken cancellation)
    {
        if (CanUseState) await CollectAffectedFilesAsync(cancellation).ConfigureAwait(false);
        var result = new List<Diagnostic>();
        Checker? declarationChecker = null;
        foreach (var file in Program.SourceFiles)
        {
            var path = file.Syntax.FileName;
            if (!Program.SourceFileMayBeEmitted(file.Syntax)) continue;
            if (!CanUseState || (snapshot.PendingEmit.GetValueOrDefault(path) & FileEmitKind.DeclarationErrors) != 0)
            {
                declarationChecker ??= await Program.CreateCheckerAsync(cancellation).ConfigureAwait(false);
                var current = await Program.GetDeclarationDiagnosticsWithCheckerAsync(file.Syntax, declarationChecker, cancellation).ConfigureAwait(false);
                if (current.Count != 0) snapshot.EmitDiagnostics[path] = current;
                else snapshot.EmitDiagnostics.Remove(path);
                if (CanUseState)
                {
                    var pending = snapshot.PendingEmit.GetValueOrDefault(path) & ~FileEmitKind.DeclarationErrors;
                    if (pending == 0) snapshot.PendingEmit.Remove(path); else snapshot.PendingEmit[path] = pending;
                    snapshot.BuildInfoPending = true;
                }
            }
            result.AddRange(snapshot.EmitDiagnostics.GetValueOrDefault(path) ?? []);
        }
        return DiagnosticCollection.SortAndDeduplicate(result);
    }
}

using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Incremental;

public sealed partial class IncrementalProgram
{
    private async ValueTask CollectAffectedFilesAsync(CancellationToken cancellation)
    {
        if (snapshot.Changed.Count == 0) return;
        var reverse = new Dictionary<Utf8String, HashSet<Utf8String>>(comparer);
        foreach (var (file, references) in snapshot.References)
            foreach (var reference in references)
            {
                if (!reverse.TryGetValue(reference, out var files)) reverse[reference] = files = new(comparer);
                files.Add(file);
            }
        var signatures = new Dictionary<Utf8String, Utf8String>(comparer);
        var affected = new HashSet<Utf8String>(comparer);
        var invalidate = new HashSet<Utf8String>(comparer);
        var pending = new Dictionary<Utf8String, FileEmitKind>(comparer);
        bool allFiles = false;

        async ValueTask<bool> UpdateSignature(ProgramFile file, bool useVersion = false)
        {
            var path = file.Syntax.FileName;
            if (signatures.ContainsKey(path)) return false;
            var info = snapshot.Files[path];
            Utf8String signature = default;
            if (!useVersion && !file.Syntax.IsDeclarationFile && file.Syntax.ScriptKind != ScriptKind.JSON)
                await Program.EmitAsync(new()
                {
                    SourceFiles = [file.Syntax], Only = EmitOnly.BuilderSignature,
                    WriteFile = (_, text, data, _) =>
                    {
                        signature = BuildInfoDiagnostics.Signature(path, text, data.SourceMapUrlPosition, data.Diagnostics, hashWithText);
                        return ValueTask.CompletedTask;
                    }
                }, cancellation).ConfigureAwait(false);
            if (signature.Length == 0) signature = info.Version;
            signatures[path] = signature;
            return signature != info.Signature;
        }
        bool ChangedSignature(Utf8String path) => signatures.TryGetValue(path, out var signature) && signature != snapshot.Files[path].Signature;
        IEnumerable<Utf8String> ReferencedBy(Utf8String path) => reverse.GetValueOrDefault(path) ?? [];
        void InvalidateLibraries()
        {
            foreach (var file in Program.SourceFiles.Where(file => file.Library)) invalidate.Add(file.Syntax.FileName);
        }

        foreach (var path in snapshot.Changed.Order(Utf8StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            if (Program.GetFile(path) is not { } file) continue;
            affected.Add(path);
            if (!await UpdateSignature(file).ConfigureAwait(false)) continue;
            if (snapshot.Files[path].AffectsGlobalScope)
            {
                allFiles = true;
                foreach (var other in Program.SourceFiles.Where(other => !other.Library)) affected.Add(other.Syntax.FileName);
            }
            else if (Options.IsolatedModules != true)
            {
                var seen = new HashSet<Utf8String>(comparer) { path };
                var queue = new Stack<Utf8String>(ReferencedBy(path));
                while (queue.TryPop(out var dependent))
                {
                    if (!seen.Add(dependent) || Program.GetFile(dependent) is not { } current) continue;
                    affected.Add(dependent);
                    if (await UpdateSignature(current).ConfigureAwait(false))
                        foreach (var next in ReferencedBy(dependent)) queue.Push(next);
                }
            }
        }

        async ValueTask HandleDeclaration(Utf8String path, bool invalidateJavaScript)
        {
            if (snapshot.Changed.Contains(path) || Program.GetFile(path) is not { } file) return;
            invalidate.Add(path);
            await UpdateSignature(file, true).ConfigureAwait(false);
            var kind = invalidateJavaScript ? BuildInfo.GetEmitKind(Options)
                : Options.Declaration == true || Options.Composite == true
                    ? Options.DeclarationMap == true ? FileEmitKind.AllDeclarations : FileEmitKind.Declarations : FileEmitKind.None;
            if (kind != 0) pending[path] = pending.GetValueOrDefault(path) | kind;
        }
        async ValueTask<bool> HandleGlobal(Utf8String path, bool invalidateJavaScript)
        {
            if (!snapshot.Files.TryGetValue(path, out var info) || !info.AffectsGlobalScope) return false;
            foreach (var file in Program.SourceFiles.Where(file => !file.Library)) await HandleDeclaration(file.Syntax.FileName, invalidateJavaScript).ConfigureAwait(false);
            InvalidateLibraries();
            return true;
        }
        var seenReferences = new Dictionary<Utf8String, bool>(comparer);
        foreach (var path in affected.Order(Utf8StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            invalidate.Add(path);
            pending[path] = BuildInfo.GetEmitKind(Options);
            if (allFiles)
            {
                InvalidateLibraries();
                if (Program.GetFile(path) is { } file) await UpdateSignature(file).ConfigureAwait(false);
                continue;
            }
            if (Options.AssumeChangesOnlyAffectDirectDependencies == true || !snapshot.Changed.Contains(path) || !ChangedSignature(path)) continue;
            if (Options.IsolatedModules == true)
            {
                var seen = new HashSet<Utf8String>(comparer) { path };
                var queue = new Stack<Utf8String>(ReferencedBy(path));
                while (queue.TryPop(out var dependent))
                {
                    if (!seen.Add(dependent) || !snapshot.Files.ContainsKey(dependent)) continue;
                    if (await HandleGlobal(dependent, false).ConfigureAwait(false)) break;
                    await HandleDeclaration(dependent, false).ConfigureAwait(false);
                    if (ChangedSignature(dependent)) foreach (var next in ReferencedBy(dependent)) queue.Push(next);
                }
            }
            bool invalidateJavaScript = Program.GetFile(path)?.Binding.Symbol?.Exports.Values
                .Any(symbol => (symbol.Flags & SymbolFlags.ConstEnum) != 0) == true;
            var dependentQueue = new Stack<Utf8String>();
            bool globalHandled = false;
            foreach (var dependent in ReferencedBy(path))
            {
                if (await HandleGlobal(dependent, invalidateJavaScript).ConfigureAwait(false)) { globalHandled = true; break; }
                foreach (var next in ReferencedBy(dependent)) dependentQueue.Push(next);
            }
            if (globalHandled) continue;
            while (dependentQueue.TryPop(out var dependent))
            {
                if (seenReferences.TryGetValue(dependent, out bool seen) && (seen || !invalidateJavaScript)) continue;
                seenReferences[dependent] = invalidateJavaScript;
                if (await HandleGlobal(dependent, invalidateJavaScript).ConfigureAwait(false)) break;
                await HandleDeclaration(dependent, invalidateJavaScript).ConfigureAwait(false);
                foreach (var next in ReferencedBy(dependent)) dependentQueue.Push(next);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        foreach (var (path, signature) in signatures) snapshot.Files[path] = snapshot.Files[path] with { Signature = signature, Expanded = false };
        foreach (var path in invalidate) snapshot.SemanticDiagnostics.Remove(path);
        foreach (var (path, kind) in pending) AddPending(path, kind);
        snapshot.Changed.Clear();
        snapshot.BuildInfoPending = true;
        affectedFiles.UnionWith(affected);
    }
}

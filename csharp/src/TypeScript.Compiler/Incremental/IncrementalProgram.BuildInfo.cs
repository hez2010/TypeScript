using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Incremental;

public sealed partial class IncrementalProgram
{
    public async ValueTask<BuildInfo> GetBuildInfoAsync(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try { cancellation.ThrowIfCancellationRequested(); return CreateBuildInfo(); }
        finally { gate.Release(); }
    }

    private BuildInfo CreateBuildInfo()
    {
        var directory = CompilerPath.DirectoryName(BuildInfoFileName.Length == 0
            ? CompilerPath.Combine(Program.CurrentDirectory, "tsconfig.tsbuildinfo"u8) : BuildInfoFileName);
        Utf8String Relative(Utf8String path) => IncrementalOptions.Relative(directory, path, fileSystem.CaseSensitive);
        var result = new BuildInfo
        {
            CheckPending = snapshot.CheckPending,
            ContentMapperIdentities = snapshot.MapperIdentities.Length == 0 ? null : snapshot.MapperIdentities.ToArray(),
            PackageJsons = Program.Configuration.FileName.Length == 0 || Program.ExistingPackageJsons.Count == 0 ? null : Program.ExistingPackageJsons.Select(Relative).ToArray(),
            MissingPackageJsons = Program.Configuration.FileName.Length == 0 || Program.MissingPackageJsons.Count == 0 ? null : Program.MissingPackageJsons.Select(Relative).ToArray()
        };
        bool incremental = IncrementalOptions.IsIncremental(Options);
        bool emitErrors = snapshot.EmitDiagnostics.Values.Any(values => values.Count != 0);
        result.Errors = emitErrors ? !incremental : Program.Configuration.Diagnostics.Length != 0 || Program.Diagnostics.Count != 0
            || globalDiagnostics.Count != 0 || Program.SourceFiles.Any(file => file.Syntax.ParseDiagnostics.Count != 0 || file.Syntax.JSDiagnostics.Count != 0)
            || diagnostics?.Any(diagnostic => Program.IncludeDiagnostics.Contains(diagnostic)) == true;
        result.SemanticErrors = !emitErrors && !result.Errors && !incremental && snapshot.SemanticDiagnostics.Values.Any(values => values.Count != 0);
        if (!incremental)
        {
            result.Root = Program.RootFileNames.Count == 0 ? null : Program.RootFileNames.Select(path => new BuildInfoRoot(0, Name: Relative(path))).ToArray();
            return result;
        }
        var ids = new Dictionary<Utf8String, int>(comparer);
        var names = new List<Utf8String>();
        int FileId(Utf8String path)
        {
            if (ids.TryGetValue(path, out int id)) return id;
            id = names.Count + 1;
            ids[path] = id;
            var canonical = fileSystem.CaseSensitive ? path : path.ToLowerInvariant();
            names.Add(Program.GetFile(path) is { Library: true } && CompilerPath.Contains(Program.DefaultLibraryDirectory, path, fileSystem.CaseSensitive)
                ? CompilerPath.BaseName(canonical) : Relative(canonical));
            return id;
        }
        var emitSignatures = new List<BuildInfoEmitSignature>();
        result.FileInfos = Program.SourceFiles.Select(file =>
        {
            var path = file.Syntax.FileName;
            var info = snapshot.Files[path];
            int id = FileId(path);
            if (Options.Composite == true && file.Syntax.ScriptKind != ScriptKind.JSON && Program.SourceFileMayBeEmitted(file.Syntax))
            {
                if (!snapshot.EmitSignatures.TryGetValue(path, out var signature)) emitSignatures.Add(new(id));
                else if (signature.DifferentOptions) emitSignatures.Add(signature.Text == info.Signature
                    ? new(id, Kind: EmitSignatureKind.DifferentMap) : new(id, signature.Text, EmitSignatureKind.DifferentOptions));
                else if (signature.Text != info.Signature) emitSignatures.Add(new(id, signature.Text, EmitSignatureKind.Text));
            }
            return info with { Expanded = false };
        }).ToArray();
        var roots = new List<BuildInfoRoot>();
        var resolvedRoots = new List<BuildInfoResolvedRoot>();
        foreach (var pair in Program.RootFileNames.Select(root => (Root: root, File: Program.GetFile(root)))
            .Where(pair => pair.File is not null).DistinctBy(pair => pair.File).OrderBy(pair => FileId(pair.File!.Syntax.FileName)))
        {
            int resolved = FileId(pair.File!.Syntax.FileName), original = FileId(pair.Root);
            if (roots.Count != 0 && (roots[^1].End == resolved - 1 || roots[^1].End == 0 && roots[^1].Start == resolved - 1))
                roots[^1] = roots[^1] with { End = resolved };
            else roots.Add(new(resolved));
            if (resolved != original) resolvedRoots.Add(new(resolved, original));
        }
        result.Root = roots.Count == 0 ? null : roots.ToArray();
        result.ResolvedRoot = resolvedRoots.Count == 0 ? null : resolvedRoots.ToArray();
        result.Options = IncrementalOptions.ToBuildInfo(Options, directory, fileSystem.CaseSensitive);
        var lists = new List<int[]>();
        var listIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var references = new List<BuildInfoReference>();
        foreach (var (file, dependencies) in snapshot.References.OrderBy(entry => entry.Key, Utf8StringComparer.Ordinal))
        {
            int[] items = dependencies.Select(FileId).Order().ToArray();
            string key = string.Join(',', items);
            if (!listIds.TryGetValue(key, out int listId))
            {
                lists.Add(items);
                listIds[key] = listId = lists.Count;
            }
            references.Add(new(FileId(file), listId));
        }
        result.FileIdsList = lists.Count == 0 ? null : lists.ToArray();
        result.ReferencedMap = references.Count == 0 ? null : references.ToArray();
        result.ChangeFileSet = snapshot.Changed.Count == 0 ? null : snapshot.Changed.Order(Utf8StringComparer.Ordinal).Select(FileId).ToArray();
        var semantic = new List<BuildInfoFileDiagnostics>();
        foreach (var file in Program.SourceFiles)
        {
            var path = file.Syntax.FileName;
            if (snapshot.SemanticDiagnostics.TryGetValue(path, out var values))
            {
                if (values.Count != 0) semantic.Add(new(FileId(path), BuildInfoDiagnostics.Encode(values, path, FileId)));
            }
            else if (!snapshot.Changed.Contains(path)) semantic.Add(new(FileId(path), null));
        }
        result.SemanticDiagnosticsPerFile = semantic.Count == 0 ? null : semantic.ToArray();
        result.EmitDiagnosticsPerFile = snapshot.EmitDiagnostics.Count == 0 ? null : snapshot.EmitDiagnostics
            .Where(entry => entry.Value.Count != 0).OrderBy(entry => entry.Key, Utf8StringComparer.Ordinal)
            .Select(entry => new BuildInfoFileDiagnostics(FileId(entry.Key), BuildInfoDiagnostics.Encode(entry.Value, entry.Key, FileId))).ToArray();
        if (result.EmitDiagnosticsPerFile is { Length: 0 }) result.EmitDiagnosticsPerFile = null;
        var fullEmit = BuildInfo.GetEmitKind(Options);
        var pending = snapshot.PendingEmit.Where(entry => Program.GetFile(entry.Key) is { } file && Program.SourceFileMayBeEmitted(file.Syntax))
            .OrderBy(entry => entry.Key, Utf8StringComparer.Ordinal)
            .Select(entry => new BuildInfoPendingEmit(FileId(entry.Key), entry.Value == fullEmit ? FileEmitKind.None : entry.Value)).ToArray();
        result.AffectedFilesPendingEmit = pending.Length == 0 ? null : pending;
        result.LatestChangedDtsFile = snapshot.LatestChangedDeclaration.Length == 0 ? default : Relative(snapshot.LatestChangedDeclaration);
        result.EmitSignatures = emitSignatures.Count == 0 ? null : emitSignatures.ToArray();
        result.FileNames = names.Count == 0 ? null : names.ToArray();
        return result;
    }
}

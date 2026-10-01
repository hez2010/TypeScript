using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Incremental;

internal readonly record struct DeclarationSignature(Utf8String Text, bool DifferentOptions = false);

internal sealed class IncrementalSnapshot
{
    internal CompilerOptions Options { get; }
    internal Dictionary<Utf8String, BuildInfoFileInfo> Files { get; }
    internal Dictionary<Utf8String, HashSet<Utf8String>> References { get; }
    internal Dictionary<Utf8String, IReadOnlyList<Diagnostic>> SemanticDiagnostics { get; }
    internal Dictionary<Utf8String, IReadOnlyList<Diagnostic>> EmitDiagnostics { get; }
    internal Dictionary<Utf8String, DeclarationSignature> EmitSignatures { get; }
    internal Dictionary<Utf8String, FileEmitKind> PendingEmit { get; }
    internal HashSet<Utf8String> Changed { get; }
    internal Utf8String[] MapperIdentities { get; }
    internal Utf8String LatestChangedDeclaration { get; set; }
    internal bool CheckPending { get; set; }
    internal bool BuildInfoPending { get; set; }
    internal bool HasErrors { get; set; }
    internal bool SemanticErrors { get; set; }
    internal Utf8String[] PackageJsons { get; set; } = [];
    internal Utf8String[] MissingPackageJsons { get; set; } = [];

    internal IncrementalSnapshot(CompilerOptions options, Utf8StringComparer comparer, Utf8String[] mapperIdentities)
    {
        Options = options;
        Files = new(comparer); References = new(comparer); SemanticDiagnostics = new(comparer);
        EmitDiagnostics = new(comparer); EmitSignatures = new(comparer); PendingEmit = new(comparer); Changed = new(comparer);
        MapperIdentities = mapperIdentities;
    }

    internal IncrementalSnapshot Copy()
    {
        var copy = new IncrementalSnapshot(Options, (Utf8StringComparer)Files.Comparer, MapperIdentities)
        {
            LatestChangedDeclaration = LatestChangedDeclaration, CheckPending = CheckPending,
            BuildInfoPending = BuildInfoPending, HasErrors = HasErrors, SemanticErrors = SemanticErrors,
            PackageJsons = PackageJsons, MissingPackageJsons = MissingPackageJsons
        };
        foreach (var entry in Files) copy.Files.Add(entry.Key, entry.Value);
        foreach (var entry in References) copy.References.Add(entry.Key, entry.Value);
        foreach (var entry in SemanticDiagnostics) copy.SemanticDiagnostics.Add(entry.Key, entry.Value);
        foreach (var entry in EmitDiagnostics) copy.EmitDiagnostics.Add(entry.Key, entry.Value);
        foreach (var entry in EmitSignatures) copy.EmitSignatures.Add(entry.Key, entry.Value);
        foreach (var entry in PendingEmit) copy.PendingEmit.Add(entry.Key, entry.Value);
        copy.Changed.UnionWith(Changed);
        return copy;
    }

    internal static IncrementalSnapshot FromBuildInfo(BuildInfo info, Utf8String buildInfoPath, Utf8String libraryDirectory,
        Utf8StringComparer comparer)
    {
        var directory = CompilerPath.DirectoryName(buildInfoPath);
        var snapshot = new IncrementalSnapshot(IncrementalOptions.FromBuildInfo(info, directory), comparer, info.ContentMapperIdentities ?? [])
        {
            CheckPending = info.CheckPending, HasErrors = info.Errors, SemanticErrors = info.SemanticErrors,
            LatestChangedDeclaration = info.LatestChangedDtsFile.Length != 0 ? CompilerPath.Resolve(directory, info.LatestChangedDtsFile) : default,
            PackageJsons = (info.PackageJsons ?? []).Select(path => CompilerPath.Resolve(directory, path)).ToArray(),
            MissingPackageJsons = (info.MissingPackageJsons ?? []).Select(path => CompilerPath.Resolve(directory, path)).ToArray()
        };
        var paths = (info.FileNames ?? []).Select(name => CompilerPath.Resolve(
            !CompilerPath.IsAbsolute(name) && !name.StartsWith((byte)'.') ? libraryDirectory : directory, name)).ToArray();
        if (paths.Distinct(comparer).Count() != paths.Length) throw new InvalidDataException("Duplicate build-info file identities");
        Utf8String File(int id) => paths[id - 1];
        for (int i = 0; i < (info.FileInfos?.Length ?? 0); i++)
        {
            var file = info.FileInfos![i];
            snapshot.Files.Add(paths[i], file);
            if (snapshot.Options.Composite == true && file.Signature is { IsEmpty: false } signature)
                snapshot.EmitSignatures[paths[i]] = new(signature);
        }
        foreach (var signature in info.EmitSignatures ?? [])
        {
            var path = File(signature.FileId);
            if (signature.Kind == EmitSignatureKind.Missing) snapshot.EmitSignatures.Remove(path);
            else snapshot.EmitSignatures[path] = new(signature.Kind == EmitSignatureKind.DifferentMap
                ? snapshot.Files.GetValueOrDefault(path).Signature ?? default : signature.Signature,
                signature.Kind is EmitSignatureKind.DifferentMap or EmitSignatureKind.DifferentOptions);
        }
        foreach (var entry in info.ReferencedMap ?? [])
            snapshot.References[File(entry.FileId)] = new(info.FileIdsList![entry.FileIdListId - 1].Select(File), comparer);
        foreach (int id in info.ChangeFileSet ?? []) snapshot.Changed.Add(File(id));
        foreach (var path in snapshot.Files.Keys)
            if (!snapshot.Changed.Contains(path)) snapshot.SemanticDiagnostics[path] = [];
        foreach (var entry in info.SemanticDiagnosticsPerFile ?? [])
        {
            var path = File(entry.FileId);
            if (entry.Diagnostics is { } diagnostics && BuildInfoDiagnostics.Decode(diagnostics, path, File) is { } decoded)
                snapshot.SemanticDiagnostics[path] = decoded;
            else snapshot.SemanticDiagnostics.Remove(path);
        }
        foreach (var entry in info.EmitDiagnosticsPerFile ?? [])
            if (entry.Diagnostics is { } diagnostics && BuildInfoDiagnostics.Decode(diagnostics, File(entry.FileId), File) is { } decoded)
                snapshot.EmitDiagnostics[File(entry.FileId)] = decoded;
        foreach (var entry in info.AffectedFilesPendingEmit ?? [])
            snapshot.PendingEmit[File(entry.FileId)] = entry.Kind == FileEmitKind.None ? BuildInfo.GetEmitKind(snapshot.Options) : entry.Kind;
        return snapshot;
    }
}

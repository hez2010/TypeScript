using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Projects.TypeAcquisition;

public sealed class TypingsInfo : IEquatable<TypingsInfo>
{
    public TypeAcquisitionOptions Acquisition { get; }
    public bool AllowJavaScript { get; }
    public IReadOnlyList<Utf8String> UnresolvedImports { get; }

    public TypingsInfo(TypeAcquisitionOptions acquisition, bool allowJavaScript, IEnumerable<Utf8String> unresolvedImports)
    {
        Acquisition = acquisition with { Include = acquisition.Include?.ToArray(), Exclude = acquisition.Exclude?.ToArray() };
        AllowJavaScript = allowJavaScript;
        UnresolvedImports = Array.AsReadOnly(unresolvedImports.Distinct(Utf8StringComparer.Ordinal).Order(Utf8StringComparer.Ordinal)
            .Select(value => Utf8String.Copy(value.Span)).ToArray());
    }

    public bool Equals(TypingsInfo? other) => other is not null && AllowJavaScript == other.AllowJavaScript
        && Acquisition.Enable == other.Acquisition.Enable
        && Acquisition.DisableFilenameBasedTypeAcquisition == other.Acquisition.DisableFilenameBasedTypeAcquisition
        && (Acquisition.Include ?? []).SequenceEqual(other.Acquisition.Include ?? [])
        && (Acquisition.Exclude ?? []).SequenceEqual(other.Acquisition.Exclude ?? [])
        && UnresolvedImports.SequenceEqual(other.UnresolvedImports);
    public override bool Equals(object? other) => other is TypingsInfo info && Equals(info);
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(AllowJavaScript); hash.Add(Acquisition.Enable); hash.Add(Acquisition.DisableFilenameBasedTypeAcquisition);
        foreach (var value in Acquisition.Include ?? []) hash.Add(value);
        foreach (var value in Acquisition.Exclude ?? []) hash.Add(value);
        foreach (var value in UnresolvedImports) hash.Add(value);
        return hash.ToHashCode();
    }

    internal static TypingsInfo Capture(ProjectSnapshot project)
    {
        var options = project.Configuration.Options;
        var unresolved = project.Program?.SourceFiles.SelectMany(file => file.Resolutions)
            .Where(reference => !reference.TypeReference && (!reference.Resolution.IsResolved
                    || DocumentSnapshot.InferKind(reference.Resolution.FileName) is not (ScriptKind.TS or ScriptKind.TSX or ScriptKind.JSON))
                && !IsRelative(reference.Specifier)).Select(reference => reference.Specifier) ?? [];
        return new(project.GetTypeAcquisition(), options.AllowJs ?? options.CheckJs == true, unresolved);
    }

    private static bool IsRelative(Utf8String name) => name == "."u8 || name == ".."u8 || name.StartsWith("./"u8) || name.StartsWith("../"u8)
        || CompilerPath.EncodedRootLength(name) > 0;
}

public sealed record TypingsStateChange(Utf8String ProjectId, TypingsInfo Info, TypingsInstallResult Result)
{
    internal object? ProjectIdentity { get; init; }
}

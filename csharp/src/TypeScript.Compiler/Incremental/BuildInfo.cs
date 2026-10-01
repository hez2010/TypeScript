using System.IO.Hashing;
using System.Text.Encodings.Web;
using System.Text.Json;
using TypeScript.Compiler.Configuration;

namespace TypeScript.Compiler.Incremental;

public readonly record struct BuildInfoRoot(int Start, int End = 0, Utf8String Name = default);
public readonly record struct BuildInfoFileInfo(Utf8String Version, Utf8String? Signature,
    bool AffectsGlobalScope = false, int ImpliedNodeFormat = 1)
{
    internal bool Expanded { get; init; }
}
public readonly record struct BuildInfoReference(int FileId, int FileIdListId);
public readonly record struct BuildInfoFileDiagnostics(int FileId, JsonElement? Diagnostics);
public readonly record struct BuildInfoPendingEmit(int FileId, FileEmitKind Kind = FileEmitKind.None);
public readonly record struct BuildInfoResolvedRoot(int Resolved, int Root);
public enum EmitSignatureKind { Missing, Text, DifferentMap, DifferentOptions }
public readonly record struct BuildInfoEmitSignature(int FileId, Utf8String Signature = default,
    EmitSignatureKind Kind = EmitSignatureKind.Missing);

[Flags]
public enum FileEmitKind
{
    None = 0, JavaScript = 1, JavaScriptMap = 2, JavaScriptInlineMap = 4,
    DeclarationErrors = 8, Declaration = 16, DeclarationMap = 32,
    Declarations = DeclarationErrors | Declaration,
    AllJavaScript = JavaScript | JavaScriptMap | JavaScriptInlineMap,
    AllDeclarationOutput = Declaration | DeclarationMap,
    AllDeclarations = Declarations | DeclarationMap,
    All = AllJavaScript | AllDeclarations
}

/// <summary>The persisted build format shared with the pinned compiler, including byte diagnostic positions.</summary>
public sealed class BuildInfo
{
    public static Utf8String CompilerVersion => "7.1.0-dev"u8;
    public Utf8String Version { get; set; } = CompilerVersion;
    public bool Errors { get; set; }
    public bool CheckPending { get; set; }
    public BuildInfoRoot[]? Root { get; set; }
    public Utf8String[]? PackageJsons { get; set; }
    public Utf8String[]? MissingPackageJsons { get; set; }
    public Utf8String[]? ContentMapperIdentities { get; set; }
    public Utf8String[]? FileNames { get; set; }
    public BuildInfoFileInfo[]? FileInfos { get; set; }
    public int[][]? FileIdsList { get; set; }
    public JsonElement? Options { get; set; }
    public BuildInfoReference[]? ReferencedMap { get; set; }
    public BuildInfoFileDiagnostics[]? SemanticDiagnosticsPerFile { get; set; }
    public BuildInfoFileDiagnostics[]? EmitDiagnosticsPerFile { get; set; }
    public int[]? ChangeFileSet { get; set; }
    public BuildInfoPendingEmit[]? AffectedFilesPendingEmit { get; set; }
    public Utf8String LatestChangedDtsFile { get; set; }
    public BuildInfoEmitSignature[]? EmitSignatures { get; set; }
    public BuildInfoResolvedRoot[]? ResolvedRoot { get; set; }
    public bool SemanticErrors { get; set; }
    public bool IsIncremental => FileNames is { Length: > 0 };
    public bool IsValidVersion => Version == CompilerVersion;

    public static Utf8String ComputeHash(Utf8String text, bool includeText = false)
    {
        Utf8String hash = Utf8String.FromString(Convert.ToHexStringLower(XxHash128.Hash(text.Span)));
        return includeText ? hash + "-"u8 + text : hash;
    }

    public static FileEmitKind GetEmitKind(CompilerOptions options)
    {
        var kind = FileEmitKind.JavaScript;
        if (options.SourceMap == true) kind |= FileEmitKind.JavaScriptMap;
        if (options.InlineSourceMap == true) kind |= FileEmitKind.JavaScriptInlineMap;
        if (options.Declaration == true || options.Composite == true) kind |= FileEmitKind.Declarations;
        if (options.DeclarationMap == true) kind |= FileEmitKind.DeclarationMap;
        return options.EmitDeclarationOnly == true ? kind & FileEmitKind.AllDeclarations : kind;
    }

    public static FileEmitKind GetPendingEmitKind(FileEmitKind current, FileEmitKind previous)
    {
        if (current == previous) return FileEmitKind.None;
        if (current == 0 || previous == 0) return current;
        var difference = current ^ previous;
        var result = FileEmitKind.None;
        if ((difference & FileEmitKind.AllJavaScript) != 0) result |= current & FileEmitKind.AllJavaScript;
        if ((difference & FileEmitKind.DeclarationErrors) != 0) result |= current & FileEmitKind.AllDeclarations;
        if ((difference & FileEmitKind.AllDeclarationOutput) != 0) result |= current & FileEmitKind.AllDeclarationOutput;
        return result;
    }

    public byte[] ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            Text("version", Version);
            Flag("errors", Errors);
            Flag("checkPending", CheckPending);
            Array("root", Root, root =>
            {
                if (root.Start == 0) JsonStrings.WriteString(writer, root.Name);
                else if (root.End == 0) writer.WriteNumberValue(root.Start);
                else Pair(root.Start, root.End);
            });
            Strings("packageJsons", PackageJsons);
            Strings("missingPackageJsons", MissingPackageJsons);
            Strings("contentMapperIdentities", ContentMapperIdentities);
            Strings("fileNames", FileNames);
            Array("fileInfos", FileInfos, info =>
            {
                if (!info.Expanded && info.Signature == info.Version && !info.AffectsGlobalScope && info.ImpliedNodeFormat == 1)
                    JsonStrings.WriteString(writer, info.Version);
                else
                {
                    writer.WriteStartObject();
                    Text("version", info.Version);
                    if (info.Signature is null) Flag("noSignature", true);
                    else if (info.Signature != info.Version) Text("signature", info.Signature.Value);
                    Flag("affectsGlobalScope", info.AffectsGlobalScope);
                    if (info.ImpliedNodeFormat != 0) writer.WriteNumber("impliedNodeFormat", info.ImpliedNodeFormat);
                    writer.WriteEndObject();
                }
            });
            Array("fileIdsList", FileIdsList, ids =>
            {
                writer.WriteStartArray();
                foreach (int id in ids) writer.WriteNumberValue(id);
                writer.WriteEndArray();
            });
            if (Options is { } options) { writer.WritePropertyName("options"); JsonStrings.WriteValue(writer, options); }
            Array("referencedMap", ReferencedMap, reference => Pair(reference.FileId, reference.FileIdListId));
            Array("semanticDiagnosticsPerFile", SemanticDiagnosticsPerFile, Diagnostics);
            Array("emitDiagnosticsPerFile", EmitDiagnosticsPerFile, Diagnostics);
            Array("changeFileSet", ChangeFileSet, writer.WriteNumberValue);
            Array("affectedFilesPendingEmit", AffectedFilesPendingEmit, pending =>
            {
                if (pending.Kind == FileEmitKind.None) writer.WriteNumberValue(pending.FileId);
                else
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(pending.FileId);
                    if (pending.Kind != FileEmitKind.Declarations) writer.WriteNumberValue((int)pending.Kind);
                    writer.WriteEndArray();
                }
            });
            Text("latestChangedDtsFile", LatestChangedDtsFile);
            Array("emitSignatures", EmitSignatures, signature =>
            {
                if (signature.Kind == EmitSignatureKind.Missing) writer.WriteNumberValue(signature.FileId);
                else
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(signature.FileId);
                    if (signature.Kind == EmitSignatureKind.Text) JsonStrings.WriteString(writer, signature.Signature);
                    else
                    {
                        writer.WriteStartArray();
                        if (signature.Kind == EmitSignatureKind.DifferentOptions) JsonStrings.WriteString(writer, signature.Signature);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                }
            });
            Array("resolvedRoot", ResolvedRoot, root => Pair(root.Resolved, root.Root));
            Flag("semanticErrors", SemanticErrors);
            writer.WriteEndObject();

            void Text(string name, Utf8String value)
            {
                if (value.Length == 0) return;
                writer.WritePropertyName(name);
                JsonStrings.WriteString(writer, value);
            }
            void Flag(string name, bool value) { if (value) writer.WriteBoolean(name, true); }
            void Pair(int first, int second)
            {
                writer.WriteStartArray(); writer.WriteNumberValue(first); writer.WriteNumberValue(second); writer.WriteEndArray();
            }
            void Array<T>(string name, T[]? values, Action<T> write)
            {
                if (values is null) return;
                writer.WriteStartArray(name);
                foreach (var value in values) write(value);
                writer.WriteEndArray();
            }
            void Strings(string name, Utf8String[]? values) => Array(name, values, value => JsonStrings.WriteString(writer, value));
            void Diagnostics(BuildInfoFileDiagnostics value)
            {
                if (value.Diagnostics is not { } diagnostics) writer.WriteNumberValue(value.FileId);
                else
                {
                    writer.WriteStartArray(); writer.WriteNumberValue(value.FileId);
                    JsonStrings.WriteValue(writer, diagnostics); writer.WriteEndArray();
                }
            }
        }
        return stream.ToArray();
    }

    /// <summary>Invalid or truncated state is a cache miss, never a compiler failure.</summary>
    public static BuildInfo? TryRead(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var info = new BuildInfo
            {
                Version = Text(root, "version"), Errors = Flag(root, "errors"), CheckPending = Flag(root, "checkPending"),
                SemanticErrors = Flag(root, "semanticErrors"), LatestChangedDtsFile = Text(root, "latestChangedDtsFile"),
                PackageJsons = Strings("packageJsons"), MissingPackageJsons = Strings("missingPackageJsons"),
                ContentMapperIdentities = Strings("contentMapperIdentities"), FileNames = Strings("fileNames"),
                Root = Array("root", value => value.ValueKind switch
                {
                    JsonValueKind.String => new BuildInfoRoot(0, Name: JsonStrings.GetString(value)),
                    JsonValueKind.Number => new BuildInfoRoot(value.GetInt32()),
                    _ => PairRoot(value)
                }),
                FileInfos = Array("fileInfos", value =>
                {
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        var version = JsonStrings.GetString(value);
                        return new BuildInfoFileInfo(version, version);
                    }
                    var text = Text(value, "version");
                    var signature = Text(value, "signature");
                    return new BuildInfoFileInfo(text, Flag(value, "noSignature") ? null : signature.Length == 0 ? text : signature,
                        Flag(value, "affectsGlobalScope"), value.TryGetProperty("impliedNodeFormat", out var format) ? format.GetInt32() : 0) { Expanded = true };
                }),
                FileIdsList = Array("fileIdsList", value => value.EnumerateArray().Select(v => v.GetInt32()).ToArray()),
                ReferencedMap = Array("referencedMap", value => { Tuple(value, 2); return new BuildInfoReference(value[0].GetInt32(), value[1].GetInt32()); }),
                SemanticDiagnosticsPerFile = Array("semanticDiagnosticsPerFile", Diagnostics),
                EmitDiagnosticsPerFile = Array("emitDiagnosticsPerFile", Diagnostics),
                ChangeFileSet = Array("changeFileSet", value => value.GetInt32()),
                AffectedFilesPendingEmit = Array("affectedFilesPendingEmit", value =>
                {
                    if (value.ValueKind == JsonValueKind.Number) return new BuildInfoPendingEmit(value.GetInt32());
                    Tuple(value, 1, 2);
                    return new BuildInfoPendingEmit(value[0].GetInt32(), value.GetArrayLength() == 1
                        ? FileEmitKind.Declarations : (FileEmitKind)value[1].GetInt32());
                }),
                EmitSignatures = Array("emitSignatures", value =>
                {
                    if (value.ValueKind == JsonValueKind.Number) return new BuildInfoEmitSignature(value.GetInt32());
                    Tuple(value, 2);
                    int id = value[0].GetInt32();
                    if (value[1].ValueKind == JsonValueKind.String) return new BuildInfoEmitSignature(id, JsonStrings.GetString(value[1]), EmitSignatureKind.Text);
                    Tuple(value[1], 0, 1);
                    return value[1].GetArrayLength() == 0 ? new BuildInfoEmitSignature(id, Kind: EmitSignatureKind.DifferentMap)
                        : new BuildInfoEmitSignature(id, JsonStrings.GetString(value[1][0]), EmitSignatureKind.DifferentOptions);
                }),
                ResolvedRoot = Array("resolvedRoot", value => { Tuple(value, 2); return new BuildInfoResolvedRoot(value[0].GetInt32(), value[1].GetInt32()); })
            };
            if (root.TryGetProperty("options", out var options) && options.ValueKind != JsonValueKind.Null)
            {
                if (options.ValueKind != JsonValueKind.Object) return null;
                info.Options = options.Clone();
            }
            return info.Validate() ? info : null;

            T[]? Array<T>(string name, Func<JsonElement, T> parse) => root.TryGetProperty(name, out var value)
                && value.ValueKind != JsonValueKind.Null ? value.EnumerateArray().Select(parse).ToArray() : null;
            Utf8String[]? Strings(string name) => Array(name, JsonStrings.GetString);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    private bool Validate()
    {
        int count = FileNames?.Length ?? 0;
        bool Id(int id) => id > 0 && id <= count;
        if (FileInfos is { } infos && infos.Length > count) return false;
        if (Root is { } roots && roots.Any(root => root.Start == 0 ? IsIncremental || root.Name.IsEmpty
            : !Id(root.Start) || root.End != 0 && (root.End < root.Start || !Id(root.End)))) return false;
        if (FileIdsList?.Any(ids => ids.Any(id => !Id(id))) == true) return false;
        if (ReferencedMap?.Any(entry => !Id(entry.FileId) || entry.FileIdListId < 1 || entry.FileIdListId > (FileIdsList?.Length ?? 0)) == true) return false;
        if (SemanticDiagnosticsPerFile?.Any(entry => !Id(entry.FileId)) == true || EmitDiagnosticsPerFile?.Any(entry => !Id(entry.FileId)) == true) return false;
        if (ChangeFileSet?.Any(id => !Id(id)) == true) return false;
        if (AffectedFilesPendingEmit?.Any(entry => !Id(entry.FileId) || (entry.Kind & ~FileEmitKind.All) != 0) == true) return false;
        if (EmitSignatures?.Any(entry => !Id(entry.FileId)) == true) return false;
        return ResolvedRoot?.Any(entry => !Id(entry.Root) || !Id(entry.Resolved)) != true;
    }

    private static Utf8String Text(JsonElement value, string name) => value.TryGetProperty(name, out var text)
        && text.ValueKind != JsonValueKind.Null ? JsonStrings.GetString(text) : default;
    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var flag)
        && flag.ValueKind != JsonValueKind.Null && flag.GetBoolean();
    private static void Tuple(JsonElement value, int minimum, int maximum = -1)
    {
        int length = value.GetArrayLength();
        if (length < minimum || length > (maximum < 0 ? minimum : maximum)) throw new JsonException("Invalid build-info tuple");
    }
    private static BuildInfoRoot PairRoot(JsonElement value)
    {
        Tuple(value, 2);
        return new(value[0].GetInt32(), value[1].GetInt32());
    }
    private static BuildInfoFileDiagnostics Diagnostics(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number) return new(value.GetInt32(), null);
        Tuple(value, 2);
        if (value[1].ValueKind is not JsonValueKind.Array and not JsonValueKind.Null) throw new JsonException("Invalid build-info diagnostics");
        return new(value[0].GetInt32(), value[1].Clone());
    }
}

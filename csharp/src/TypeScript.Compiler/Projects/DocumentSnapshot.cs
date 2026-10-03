using System.IO.Hashing;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Projects;

/// <summary>An immutable source version shared by project snapshots and active requests.</summary>
public sealed class DocumentSnapshot
{
    private readonly Lazy<DocumentLineMap> lineMap;
    private readonly Lazy<SourceText> source;
    public Utf8String FileName { get; }
    public Utf8String Text { get; }
    public int Version { get; }
    public ScriptKind Kind { get; }
    public bool IsOverlay { get; }
    public bool MatchesDiskText { get; }
    public UInt128 Hash { get; }
    public DocumentLineMap LineMap => lineMap.Value;

    public DocumentSnapshot(Utf8String fileName, Utf8String text, int version = 0,
        ScriptKind kind = ScriptKind.Unknown, bool isOverlay = false, bool matchesDiskText = true)
    {
        FileName = fileName;
        Text = text;
        Version = version;
        Kind = kind == ScriptKind.Unknown ? InferKind(fileName) : kind;
        IsOverlay = isOverlay;
        MatchesDiskText = matchesDiskText;
        Hash = XxHash128.HashToUInt128(text.Span);
        lineMap = new(() => new(Text));
        source = new(() => new(Text));
    }

    internal SourceText Source => source.Value;

    public DocumentSnapshot WithDiskMatch(bool matches) =>
        matches == MatchesDiskText ? this : new(FileName, Text, Version, Kind, IsOverlay, matches);

    internal static ScriptKind InferKind(Utf8String fileName) => CompilerPath.Extension(fileName).ToLowerInvariant() switch
    {
        var extension when extension == ".js"u8 || extension == ".mjs"u8 || extension == ".cjs"u8 => ScriptKind.JS,
        var extension when extension == ".ts"u8 || extension == ".mts"u8 || extension == ".cts"u8 => ScriptKind.TS,
        var extension when extension == ".jsx"u8 => ScriptKind.JSX,
        var extension when extension == ".tsx"u8 => ScriptKind.TSX,
        var extension when extension == ".json"u8 => ScriptKind.JSON,
        _ => ScriptKind.Unknown
    };

    public DocumentSnapshot Apply(IReadOnlyList<DocumentEdit> edits, int version, PositionEncoding encoding)
    {
        if (edits.Count == 0) return this;
        DocumentSnapshot current = this;
        foreach (var edit in edits)
        {
            Utf8String text = edit.Text;
            if (edit.Range is { } range)
            {
                int start = current.LineMap.ToOffset(range.Start, encoding);
                int end = current.LineMap.ToOffset(range.End, encoding);
                if (end < start) throw new ArgumentException("The edit range ends before it starts", nameof(edits));
                text = Utf8String.Concat(current.Text.Span[..start], text.Span, current.Text.Span[end..]);
            }
            current = new(FileName, text, version, Kind, true, false);
        }
        return current;
    }
}

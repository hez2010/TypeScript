using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Mapping;

public sealed record MappedDiagnosticDirective(int OriginalStart, int OriginalEnd, int VirtualStart, int VirtualEnd,
    bool Expect, string Source, DiagnosticCode UnusedCode = DiagnosticCode.None, string UnusedMessage = "");
public sealed record MapperOutput(SourceText Text, string Extension, SpanMap Mappings, IReadOnlyList<MappedDiagnosticDirective> Directives);
public sealed record MapperResult(MapperOutput Canonical, IReadOnlyList<MapperOutput> Supplemental, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record MappedSourceFile(SourceFileNode Syntax, SourceText Original, SpanMap Map, string VirtualFileName,
    string MapperIdentity, string TransformIdentity, IReadOnlyList<MappedDiagnosticDirective> Directives)
{
    public DiagnosticPresentation Present(Diagnostic diagnostic, string? locale = null)
    {
        int start = diagnostic.Start, end = checked(start + diagnostic.Length);
        if (diagnostic.Source is not null)
            return new(Original, start, end - start, false, diagnostic.Format(locale));
        var span = Map.VirtualToOriginalSpan(start, end);
        TextSlice[] arguments = diagnostic.Arguments;
        if (Map.AliasForVirtualSpan(start, end) is { } alias)
        {
            string virtualName = Wtf8.DecodeString(Syntax.Source.Bytes.Span[alias.VirtualStart..alias.VirtualEnd]);
            string originalName = Wtf8.DecodeString(Original.Bytes.Span[alias.OriginalStart..alias.OriginalEnd]);
            arguments = arguments.Select(arg => arg == virtualName ? originalName : arg).ToArray();
        }
        string message = diagnostic.Message.Format(locale, arguments);
        return span.Fidelity == MappingFidelity.None ? new(Syntax.Source, start, end - start, true, message)
            : new(Original, span.Start, span.End - span.Start, false, message);
    }

    public IReadOnlyList<Diagnostic> ApplyDiagnosticDirectives(IEnumerable<Diagnostic> diagnostics)
    {
        bool[] used = new bool[Directives.Count];
        var result = new List<Diagnostic>();
        foreach (var diagnostic in diagnostics)
        {
            bool suppressed = false;
            if (diagnostic.Source is null)
                for (int i = 0; i < Directives.Count; i++)
                    if (diagnostic.Start >= Directives[i].VirtualStart && diagnostic.Start < Directives[i].VirtualEnd)
                    {
                        used[i] = suppressed = true;
                        break;
                    }
            if (!suppressed)
                result.Add(diagnostic);
        }
        for (int i = 0; i < used.Length; i++)
        {
            var directive = Directives[i];
            if (!used[i] && directive.Expect)
                result.Add(
                    new(
                    new(directive.UnusedCode, DiagnosticCategory.Error, "", directive.UnusedMessage),
                    directive.OriginalStart, directive.OriginalEnd - directive.OriginalStart, [])
                    { FileName = Syntax.FileName, Source = directive.Source });
        }
        return result;
    }
}
public readonly record struct DiagnosticPresentation(SourceText Text, int Start, int Length, bool Synthesized, string Message);
public sealed record MappedSourceFiles(MappedSourceFile Canonical, IReadOnlyList<MappedSourceFile> Supplemental);

internal static class MapperOutputDecoder
{
    internal static bool SupportedExtension(string extension) =>
        extension is ".ts" or ".tsx" or ".mts" or ".cts" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".json";

    internal static MapperResult Decode(JsonElement result, SourceText original, string encoding, string source)
    {
        var canonical = DecodeOutput(result, original, encoding, source);
        var supplemental = new List<MapperOutput>();
        if (result.TryGetProperty("supplemental", out var outputs))
            foreach (var output in outputs.EnumerateArray())
                supplemental.Add(DecodeOutput(output, original, encoding, source));
        var diagnostics = new List<Diagnostic>();
        if (result.TryGetProperty("diagnostics", out var errors))
            foreach (var error in errors.EnumerateArray())
            {
                int start = error.TryGetProperty("start", out var offset) ? offset.GetInt32() : 0;
                int length = error.TryGetProperty("length", out var rawLength) ? rawLength.GetInt32() : 0;
                if (length < 0)
                    throw new InvalidDataException("Negative mapper diagnostic length");
                int low = Position(original, encoding, start), high = Position(original, encoding, checked(start + length));
                int code = error.TryGetProperty("code", out var rawCode) ? rawCode.GetInt32() : 0;
                string message = error.TryGetProperty("messageText", out var text) ? JsonStrings.GetString(text) : "";
                diagnostics.Add(new(new((DiagnosticCode)code, DiagnosticCategory.Error, source + code, message),
                    low, high - low, [])
                { Source = source });
            }
        return new(canonical, supplemental.ToArray(), diagnostics.ToArray());
    }

    private static MapperOutput DecodeOutput(JsonElement output, SourceText original, string encoding, string source)
    {
        string extension = JsonStrings.GetString(output.GetProperty("extension"));
        if (!SupportedExtension(extension))
            throw new InvalidDataException("Unsupported mapper virtual extension: " + extension);
        var text = new SourceText(output.TryGetProperty("text", out var rawText) ? JsonStrings.GetString(rawText) : "");
        var raw = SpanMap.Read(output.TryGetProperty("mappings", out var mappings) ? mappings : default);
        var map = new SpanMap(raw.Segments.Select(s => s with
        {
            VirtualStart = Position(text, encoding, s.VirtualStart),
            VirtualEnd = Position(text, encoding, s.VirtualEnd),
            OriginalStart = Position(original, encoding, s.OriginalStart),
            OriginalEnd = Position(original, encoding, s.OriginalEnd)
        }));
        map.Validate(text.Bytes.Span, original.Bytes.Span);
        var directives = new List<MappedDiagnosticDirective>();
        if (output.TryGetProperty("diagnosticDirectives", out var rawDirectives) && rawDirectives.ValueKind != JsonValueKind.Null)
        {
            JsonElement unused = rawDirectives.TryGetProperty("unusedExpectDirectiveDiagnostics", out var values) ? values : default;
            if (rawDirectives.TryGetProperty("directives", out var tuples))
                foreach (var tuple in tuples.EnumerateArray())
                {
                    if (tuple.GetArrayLength() is not (5 or 6))
                        throw new InvalidDataException("Invalid diagnostic directive tuple length");
                    int policy = tuple[4].GetInt32();
                    if (policy is not (0 or 1))
                        throw new InvalidDataException("Invalid diagnostic directive policy");
                    int start = Position(text, encoding, tuple[2].GetInt32()), end = Position(text, encoding, tuple[3].GetInt32());
                    if (end < start)
                        throw new InvalidDataException("Reversed diagnostic directive range");
                    int originalStart = 0, originalEnd = 0;
                    try
                    {
                        int offset = tuple[0].GetInt32(), length = tuple[1].GetInt32();
                        if (length < 0)
                            throw new InvalidDataException("Negative directive length");
                        originalStart = Position(original, encoding, offset);
                        originalEnd = Position(original, encoding, checked(offset + length));
                    }
                    catch (Exception e) when (policy == 0 && e is InvalidDataException or OverflowException)
                    {
                        originalStart = originalEnd = 0;
                    }
                    DiagnosticCode code = DiagnosticCode.None;
                    string message = "";
                    if (policy == 1)
                    {
                        int count = unused.ValueKind == JsonValueKind.Array ? unused.GetArrayLength() : 0;
                        int index = tuple.GetArrayLength() == 6 ? tuple[5].GetInt32() : count == 1 ? 0 : -1;
                        if (index < 0 || index >= count)
                            throw new InvalidDataException("Missing unused-expect diagnostic");
                        code = (DiagnosticCode)unused[index].GetProperty("code").GetInt32();
                        message = JsonStrings.GetString(unused[index].GetProperty("messageText"));
                    }
                    directives.Add(new(originalStart, originalEnd, start, end, policy == 1, source, code, message));
                }
            int previousEnd = 0;
            foreach (var directive in directives.OrderBy(d => d.VirtualStart))
            {
                if (directive.VirtualStart < previousEnd)
                    throw new InvalidDataException("Overlapping diagnostic directives");
                previousEnd = directive.VirtualEnd;
            }
        }
        return new(text, extension, map, directives.ToArray());
    }

    private static int Position(SourceText text, string encoding, int position)
    {
        int length = encoding == "utf-16" ? text.Length : text.Bytes.Length;
        if (position < 0 || position > length)
            throw new InvalidDataException("Mapper position is outside the source text");
        int result = encoding == "utf-16" ? text.ToBytePosition(position) : position;
        if (result < text.Bytes.Length && (text.Bytes.Span[result] & 0xC0) == 0x80)
            throw new InvalidDataException("Mapper position splits a Unicode code point");
        return result;
    }

    internal static async ValueTask<MappedSourceFiles> Parse(MapperResult result, ParseOptions options, SourceText original,
        string identity, string transformIdentity, CancellationToken cancellation)
    {
        async ValueTask<MappedSourceFile> File(MapperOutput output, string fileName)
        {
            string virtualName = fileName == options.FileName ? fileName + output.Extension : fileName;
            var script = output.Extension is ".js" or ".mjs" or ".cjs" ? ScriptKind.JS : output.Extension == ".jsx" ? ScriptKind.JSX
                : output.Extension == ".tsx" ? ScriptKind.TSX : output.Extension == ".json" ? ScriptKind.JSON : ScriptKind.TS;
            var parse = options with
            {
                FileName = fileName,
                ScriptKind = script,
                ForceExternalModule = options.ForceExternalModule || output.Extension is ".mts" or ".cts" or ".mjs" or ".cjs"
            };
            var syntax = await Parser.ParseSourceFileAsync(parse, output.Text, cancellation).ConfigureAwait(false);
            return new(syntax, original, output.Mappings, virtualName, identity, transformIdentity, output.Directives);
        }
        var canonical = await File(result.Canonical, options.FileName).ConfigureAwait(false);
        canonical.Syntax.ParseDiagnostics =
            [
                .. canonical.Syntax.ParseDiagnostics,
                .. result.Diagnostics.Select(d => d with { FileName = options.FileName })
            ];
        var supplemental = new List<MappedSourceFile>();
        for (int i = 0; i < result.Supplemental.Count; i++)
            supplemental.Add(
                await File(result.Supplemental[i], options.FileName + "." + i + result.Supplemental[i].Extension).ConfigureAwait(false));
        return new(canonical, supplemental.ToArray());
    }
}

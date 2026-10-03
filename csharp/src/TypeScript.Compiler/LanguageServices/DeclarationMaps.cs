using System.Buffers;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.LanguageServices;

internal sealed class DeclarationMaps(CompilerProgram program, CancellationToken cancellation)
{
    internal readonly record struct Position(Utf8String FileName, int Offset);
    private sealed record Entry(int Generated, Utf8String Source, int Original, Utf8String GeneratedFile);
    private readonly Dictionary<Utf8String, SourceText?> sources = new(program.UseCaseSensitiveFileNames ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Utf8String, Entry[]?> maps = new(program.UseCaseSensitiveFileNames ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase);

    internal (Utf8String FileName, DocumentRange Range)? MapRange(Utf8String fileName, int start, int end, PositionEncoding encoding)
    {
        if (SourcePosition(fileName, start) is not { } first) return null;
        var last = SourcePosition(fileName, end);
        int mappedEnd = last is { } value && value.FileName == first.FileName && value.Offset >= first.Offset ? value.Offset : first.Offset + end - start;
        return (first.FileName, new DocumentLineMap(Read(first.FileName)!.Text).ToRange(first.Offset, mappedEnd, encoding));
    }

    internal Position? SourcePosition(Utf8String fileName, int position)
    {
        Position? result = null;
        HashSet<(Utf8String, int)> seen = [];
        while (CompilerPath.IsDeclarationFile(fileName) && seen.Add((fileName, position)))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!maps.TryGetValue(fileName, out var entries)) maps[fileName] = entries = Load(fileName);
            if (entries is null) break;
            int low = 0, high = entries.Length;
            while (low < high) { int mid = low + (high - low) / 2; if (entries[mid].Generated < position) low = mid + 1; else high = mid; }
            if (low == entries.Length || entries[low] is not { Original: >= 0, Source.IsEmpty: false } mapping) break;
            result = new(mapping.Source, mapping.Original); fileName = mapping.Source; position = mapping.Original;
        }
        return result is { } target && Read(target.FileName) is not null ? target : null;
    }

    internal Position? GeneratedPosition(Utf8String fileName, int position)
    {
        if (CompilerPath.IsDeclarationFile(fileName) || program.GetFile(fileName) is null
            || program.ProjectReferences.UseSources && program.ProjectReferences.Sources.ContainsKey(fileName)) return null;
        var generated = program.DeclarationOutputPath(fileName);
        if (!maps.TryGetValue(generated, out var entries)) maps[generated] = entries = Load(generated);
        var match = entries?.Where(entry => entry.Source.Equals(fileName, program.UseCaseSensitiveFileNames ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
            && entry.Original >= position).OrderBy(entry => entry.Original).FirstOrDefault();
        return match is { Generated: >= 0 } && Read(match.GeneratedFile) is not null ? new(match.GeneratedFile, match.Generated) : null;
    }

    internal SourceText? Read(Utf8String fileName)
    {
        cancellation.ThrowIfCancellationRequested();
        if (sources.TryGetValue(fileName, out var text)) return text;
        text = (program.FileSystem as ICompilerSourceProvider)?.ReadSource(fileName)?.Text;
        if (text is null && program.FileSystem.ReadFile(fileName) is { } bytes) text = new(SourceEncoding.Decode(bytes));
        sources[fileName] = text;
        return text;
    }

    private Entry[]? Load(Utf8String fileName)
    {
        var source = Read(fileName);
        Utf8String url = default;
        if (source is not null)
        {
            for (int i = source.LineStarts.Length - 1; i >= 0; i--)
            {
                var line = source.Text[source.LineStarts[i]..(i + 1 < source.LineStarts.Length ? source.LineStarts[i + 1] : source.Length)].ToString().TrimStart().TrimEnd('\r', '\n', '\u2028', '\u2029');
                if (line.Length == 0) continue;
                if (!(line.StartsWith("//# ", StringComparison.Ordinal) || line.StartsWith("//@ ", StringComparison.Ordinal))) break;
                if (line.AsSpan(4).StartsWith("sourceMappingURL=", StringComparison.Ordinal)) { url = Utf8String.FromString(line[21..].TrimEnd()); break; }
            }
        }
        if (url.StartsWith("data:"u8))
        {
            var data = url.ToString();
            const string prefix = "data:application/json;";
            if (data.StartsWith(prefix, StringComparison.Ordinal))
            {
                data = data[prefix.Length..];
                if (data.StartsWith("charset=", StringComparison.Ordinal)) data = data[8..].StartsWith("utf-8;", StringComparison.OrdinalIgnoreCase) ? data[14..] : "";
                if (data.StartsWith("base64,", StringComparison.Ordinal) && data.Length > 7)
                    try { return Parse(new(Convert.FromBase64String(data[7..])), fileName); } catch (FormatException) { }
            }
            url = default;
        }
        foreach (var candidate in url.IsEmpty ? new[] { fileName + ".map"u8 } : [url, fileName + ".map"u8])
        {
            var path = CompilerPath.Resolve(CompilerPath.DirectoryName(fileName), candidate);
            if (Read(path) is { } contents) return Parse(contents.Text, path);
        }
        return null;
    }

    private Entry[]? Parse(Utf8String text, Utf8String path)
    {
        try
        {
            using var document = JsonDocument.Parse(text.Span.ToArray());
            var root = document.RootElement;
            if (Get(root, "version").ValueKind != JsonValueKind.Number || Get(root, "version").GetInt32() != 3
                || Get(root, "sources") is not { ValueKind: JsonValueKind.Array } sourceNames || sourceNames.GetArrayLength() == 0
                || Text(root, "file") is not { IsEmpty: false } generated || Text(root, "mappings") is not { IsEmpty: false } mappings
                || Get(root, "sourcesContent") is { ValueKind: JsonValueKind.Array } contents && contents.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Null)) return null;
            var directory = CompilerPath.DirectoryName(path);
            var generatedFile = CompilerPath.Resolve(directory, generated);
            var generatedText = Read(generatedFile);
            var sourceRoot = CompilerPath.Resolve(directory, Text(root, "sourceRoot"));
            var paths = sourceNames.EnumerateArray().Select(value => CompilerPath.Resolve(sourceRoot, JsonStrings.GetString(value))).ToArray();
            List<Entry> entries = [];
            int line = 0, column = 0, sourceIndex = 0, sourceLine = 0, sourceColumn = 0, name = 0, index = 0;
            while (index < mappings.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                if (mappings[index] == ';') { line++; column = 0; index++; continue; }
                if (mappings[index] == ',') { index++; continue; }
                column = checked(column + Value()); if (column < 0) return null;
                Utf8String source = default; int original = -1;
                if (!End())
                {
                    sourceIndex = checked(sourceIndex + Value()); if (sourceIndex < 0 || sourceIndex >= paths.Length || End()) return null;
                    sourceLine = checked(sourceLine + Value()); if (sourceLine < 0 || End()) return null;
                    sourceColumn = checked(sourceColumn + Value()); if (sourceColumn < 0) return null;
                    source = paths[sourceIndex]; original = Offset(Read(source), sourceLine, sourceColumn);
                    if (!End()) { name = checked(name + Value()); if (name < 0 || !End()) return null; }
                }
                entries.Add(new(Offset(generatedText, line, column), source, original, generatedFile));
            }
            return entries.OrderBy(entry => entry.Generated).Distinct().ToArray();
            bool End() => index == mappings.Length || mappings[index] is (byte)',' or (byte)';';
            int Value()
            {
                uint value = 0; int shift = 0, digit;
                do
                {
                    if (index == mappings.Length || (digit = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"u8.IndexOf(mappings[index++])) < 0 || shift >= 32) throw new FormatException();
                    value |= (uint)(digit & 31) << shift; shift += 5;
                } while ((digit & 32) != 0);
                return (value & 1) == 0 ? (int)(value >> 1) : -(int)(value >> 1);
            }
        }
        catch (Exception error) when (error is JsonException or FormatException or OverflowException or InvalidOperationException) { return null; }
    }

    private static int Offset(SourceText? text, int line, int column)
    {
        if (text is null) return -1;
        line = Math.Clamp(line, 0, text.LineStarts.Length - 1);
        int offset = text.LineStarts[line], end = line + 1 < text.LineStarts.Length ? text.LineStarts[line + 1] : text.Length;
        int units = 0;
        while (offset < end && units < column)
        {
            if (Rune.DecodeFromUtf8(text.Text.Span[offset..], out var rune, out int width) != OperationStatus.Done) { width = 1; rune = Rune.ReplacementChar; }
            units += rune.Utf16SequenceLength; offset += width;
        }
        return offset;
    }
    private static JsonElement Get(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : default;
    private static Utf8String Text(JsonElement value, string name) => Get(value, name) is { ValueKind: JsonValueKind.String } text ? JsonStrings.GetString(text) : default;
}

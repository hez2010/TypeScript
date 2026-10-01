using System.Buffers;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

public sealed record SourceMap(Utf8String File, Utf8String SourceRoot, IReadOnlyList<Utf8String> Sources,
    IReadOnlyList<Utf8String> Names, Utf8String Mappings, IReadOnlyList<Utf8String?>? SourcesContent = null)
{
    public Utf8String ToJson()
    {
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version"u8, 3);
            String("file"u8, File);
            String("sourceRoot"u8, SourceRoot);
            writer.WriteStartArray("sources"u8);
            foreach (var source in Sources)
                StringValue(source);
            writer.WriteEndArray();
            writer.WriteStartArray("names"u8);
            foreach (var name in Names)
                StringValue(name);
            writer.WriteEndArray();
            String("mappings"u8, Mappings);
            if (SourcesContent is { Count: > 0 } contents)
            {
                writer.WriteStartArray("sourcesContent"u8);
                foreach (var content in contents)
                    if (content is { } value)
                        StringValue(value);
                    else
                        writer.WriteNullValue();
                writer.WriteEndArray();
            }
            writer.WriteEndObject();

            void String(ReadOnlySpan<byte> property, Utf8String value)
            {
                writer.WritePropertyName(property);
                StringValue(value);
            }

            void StringValue(Utf8String value)
            {
                var encoded = new Utf8StringBuilder();
                encoded.Append((byte)'"');
                var remaining = value.Span;
                while (!remaining.IsEmpty)
                {
                    if (System.Text.Rune.DecodeFromUtf8(remaining, out var rune, out int width) != OperationStatus.Done)
                    {
                        encoded.Append("\\ufffd"u8);
                        remaining = remaining[1..];
                        continue;
                    }
                    switch (rune.Value)
                    {
                        case '"': encoded.Append("\\\""u8); break;
                        case '\\': encoded.Append("\\\\"u8); break;
                        case '\n': encoded.Append("\\n"u8); break;
                        case '\r': encoded.Append("\\r"u8); break;
                        case '\t': encoded.Append("\\t"u8); break;
                        case '\b': encoded.Append("\\b"u8); break;
                        case '\f': encoded.Append("\\f"u8); break;
                        case < 32:
                            encoded.Append("\\u"u8);
                            for (int shift = 12; shift >= 0; shift -= 4) encoded.Append("0123456789abcdef"u8[(rune.Value >> shift) & 15]);
                            break;
                        default: encoded.Append(remaining[..width]); break;
                    }
                    remaining = remaining[width..];
                }
                encoded.Append((byte)'"');
                writer.WriteRawValue(encoded.WrittenSpan, skipInputValidation: true);
            }
        }
        return Utf8String.Copy(bytes.WrittenSpan);
    }

    public Utf8String ToDataUrl() => "data:application/json;base64,"u8 + Utf8String.FromString(Convert.ToBase64String(ToJson().Span));
}

/// <summary>Builds a version-3 source map. Generated and source columns count UTF-16 code units.</summary>
public sealed class SourceMapGenerator(Utf8String file, Utf8String sourceRoot, Utf8String sourcesDirectory, bool caseSensitive = true,
    Utf8String currentDirectory = default)
{
    private readonly List<Utf8String> rawSources = [], sources = [], names = [];
    private readonly List<Utf8String?> contents = [];
    private readonly Dictionary<Utf8String, int> sourceIndices = [], nameIndices = [];
    private readonly Utf8StringBuilder mappings = new();
    private bool hasLast, hasPending, pendingSource, pendingName;
    private int lastLine, lastColumn, lastSource, lastSourceLine, lastSourceColumn, lastName;
    private int line, column, source, sourceLine, sourceColumn, name;

    public IReadOnlyList<Utf8String> Sources => rawSources.AsReadOnly();

    public int AddSource(Utf8String path)
    {
        Utf8String directory = currentDirectory.Length == 0 ? "/"u8 : currentDirectory;
        var relative = CompilerPath.Relative(CompilerPath.Resolve(directory, sourcesDirectory), CompilerPath.Resolve(directory, path), caseSensitive);
        if (CompilerPath.RootLength(relative) != 0 && !relative.Span.Contains("://"u8, StringComparison.Ordinal))
            relative = (relative.Span[0] == '/' ? "file://"u8 : "file:///"u8) + relative;
        if (sourceIndices.TryGetValue(relative, out int index))
            return index;
        index = sources.Count;
        sourceIndices.Add(relative, index);
        sources.Add(relative);
        rawSources.Add(path);
        return index;
    }

    public void SetSourceContent(int index, Utf8String? content)
    {
        if ((uint)index >= sources.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        while (contents.Count <= index)
            contents.Add(null);
        contents[index] = content;
    }

    public int AddName(Utf8String value)
    {
        if (nameIndices.TryGetValue(value, out int index))
            return index;
        index = names.Count;
        names.Add(value);
        nameIndices.Add(value, index);
        return index;
    }

    public void AddGeneratedMapping(int generatedLine, int generatedColumn)
    {
        ValidateGenerated(generatedLine, generatedColumn);
        Add(generatedLine, generatedColumn, -1, -1, -1, -1);
        pendingSource = pendingName = false;
    }

    public void AddSourceMapping(int generatedLine, int generatedColumn, int sourceIndex, int originalLine, int originalColumn, int nameIndex = -1)
    {
        ValidateGenerated(generatedLine, generatedColumn);
        if ((uint)sourceIndex >= sources.Count)
            throw new ArgumentOutOfRangeException(nameof(sourceIndex));
        ArgumentOutOfRangeException.ThrowIfNegative(originalLine);
        ArgumentOutOfRangeException.ThrowIfNegative(originalColumn);
        if (nameIndex < -1 || nameIndex >= names.Count)
            throw new ArgumentOutOfRangeException(nameof(nameIndex));
        if (hasPending && line == generatedLine && column == generatedColumn && !pendingSource)
            return;
        Add(generatedLine, generatedColumn, sourceIndex, originalLine, originalColumn, nameIndex);
    }

    private void ValidateGenerated(int generatedLine, int generatedColumn)
    {
        if (generatedLine < line)
            throw new ArgumentOutOfRangeException(nameof(generatedLine), "Generated lines cannot backtrack");
        ArgumentOutOfRangeException.ThrowIfNegative(generatedColumn);
    }

    private void Add(int generatedLine, int generatedColumn, int sourceIndex, int originalLine, int originalColumn, int nameIndex)
    {
        bool backtracking = sourceIndex >= 0 && source == sourceIndex
            && (sourceLine > originalLine || sourceLine == originalLine && sourceColumn > originalColumn);
        if (!hasPending || line != generatedLine || column != generatedColumn || backtracking)
        {
            Commit();
            line = generatedLine;
            column = generatedColumn;
            pendingSource = pendingName = false;
            hasPending = true;
        }
        if (sourceIndex >= 0)
        {
            source = sourceIndex;
            sourceLine = originalLine;
            sourceColumn = originalColumn;
            pendingSource = true;
            if (nameIndex >= 0)
            {
                name = nameIndex;
                pendingName = true;
            }
        }
    }

    private void Commit()
    {
        if (!hasPending || hasLast && lastLine == line && lastColumn == column && lastSource == source
            && lastSourceLine == sourceLine && lastSourceColumn == sourceColumn && lastName == name)
            return;
        if (lastLine < line)
        {
            mappings.Append((byte)';', line - lastLine);
            lastLine = line;
            lastColumn = 0;
        }
        else if (hasLast)
            mappings.Append((byte)',');
        Vlq(column - lastColumn);
        lastColumn = column;
        if (pendingSource)
        {
            Vlq(source - lastSource);
            lastSource = source;
            Vlq(sourceLine - lastSourceLine);
            lastSourceLine = sourceLine;
            Vlq(sourceColumn - lastSourceColumn);
            lastSourceColumn = sourceColumn;
            if (pendingName)
            {
                Vlq(name - lastName);
                lastName = name;
            }
        }
        hasLast = true;
    }

    private void Vlq(int value)
    {
        ReadOnlySpan<byte> alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"u8;
        ulong remaining = value < 0 ? ((ulong)-(long)value << 1) | 1 : (ulong)value << 1;
        do
        {
            int digit = (int)(remaining & 31);
            remaining >>= 5;
            mappings.Append(alphabet[digit | (remaining != 0 ? 32 : 0)]);
        } while (remaining != 0);
    }

    public SourceMap ToSourceMap()
    {
        Commit();
        return new(file, sourceRoot, sources.ToArray(), names.ToArray(), mappings.ToUtf8String(), contents.Count == 0 ? null : contents.ToArray());
    }
}

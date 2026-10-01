using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Incremental;

internal static class BuildInfoDiagnostics
{
    internal static JsonElement Encode(IReadOnlyList<Diagnostic> diagnostics, Utf8String owner, Func<Utf8String, int> fileId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var diagnostic in diagnostics) Write(diagnostic);
            writer.WriteEndArray();

            void Write(Diagnostic diagnostic)
            {
                writer.WriteStartObject();
                if (diagnostic.FileName is not { IsEmpty: false } file) writer.WriteBoolean("noFile", true);
                else if (file != owner) writer.WriteNumber("file", fileId(file));
                Number("pos", diagnostic.Start); Number("end", diagnostic.Start + diagnostic.Length);
                Number("code", (int)diagnostic.Code); Number("category", (int)diagnostic.Message.Category);
                if (diagnostic.Source is { } source) Text("source", source);
                if (diagnostic.Message.Key.Length != 0) Text("messageKey", diagnostic.Message.Key);
                else Text("messageText", diagnostic.Message.Text);
                if (diagnostic.Arguments.Length != 0)
                {
                    writer.WriteStartArray("messageArgs");
                    foreach (var argument in diagnostic.Arguments) JsonStrings.WriteString(writer, argument);
                    writer.WriteEndArray();
                }
                Children("messageChain", diagnostic.MessageChain);
                Children("relatedInformation", diagnostic.RelatedInformation);
                if (diagnostic.Message.ReportsUnnecessary) writer.WriteBoolean("reportsUnnecessary", true);
                if (diagnostic.Message.ReportsDeprecated) writer.WriteBoolean("reportsDeprecated", true);
                if (diagnostic.SkippedOnNoEmit) writer.WriteBoolean("skippedOnNoEmit", true);
                if (diagnostic.Repopulation is { Kind: not 0 } repopulate)
                {
                    writer.WriteStartObject("repopulateInfo"); Number("kind", repopulate.Kind);
                    Text("moduleReference", repopulate.ModuleReference); Number("mode", repopulate.Mode); Text("packageName", repopulate.PackageName);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            void Number(string name, int value) { if (value != 0) writer.WriteNumber(name, value); }
            void Text(string name, Utf8String value)
            {
                if (value.Length == 0) return;
                writer.WritePropertyName(name); JsonStrings.WriteString(writer, value);
            }
            void Children(string name, IReadOnlyList<Diagnostic> values)
            {
                if (values.Count == 0) return;
                writer.WriteStartArray(name); foreach (var value in values) Write(value); writer.WriteEndArray();
            }
        }
        return JsonStrings.Parse(stream);
    }

    internal static IReadOnlyList<Diagnostic>? Decode(JsonElement values, Utf8String owner, Func<int, Utf8String> fileName)
    {
        if (values.ValueKind == JsonValueKind.Null) return [];
        try { return values.EnumerateArray().Select(value => Read(value) with { FromBuildInfo = true }).ToArray(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or FormatException or OverflowException)
        { return null; }

        Diagnostic Read(JsonElement value)
        {
            DiagnosticRepopulation repopulation = default;
            if (value.TryGetProperty("repopulateInfo", out var repopulate) && repopulate.ValueKind != JsonValueKind.Null)
            {
                int kind = Number(repopulate, "kind");
                if (kind is not (1 or 2)) throw new JsonException("Unknown diagnostic repopulation kind");
                repopulation = new(kind, Text(repopulate, "moduleReference"), Number(repopulate, "mode"), Text(repopulate, "packageName"));
            }
            var code = (DiagnosticCode)Number(value, "code");
            var key = Text(value, "messageKey");
            var message = key.Length != 0 ? DiagnosticLocalization.GetMessage(code)
                : new DiagnosticMessage(code, (DiagnosticCategory)Number(value, "category"), default, Text(value, "messageText"),
                    Flag(value, "reportsUnnecessary"), Flag(value, "reportsDeprecated"));
            int start = Number(value, "pos");
            return new(message, start, Number(value, "end") - start,
                value.TryGetProperty("messageArgs", out var arguments) ? arguments.EnumerateArray().Select(JsonStrings.GetString).ToArray() : [])
            {
                FileName = Flag(value, "noFile") ? null : Number(value, "file") is > 0 and var id ? fileName(id) : owner,
                Source = Text(value, "source") is { IsEmpty: false } source ? source : null,
                MessageChain = Children(value, "messageChain"), RelatedInformation = Children(value, "relatedInformation"),
                SkippedOnNoEmit = Flag(value, "skippedOnNoEmit"), Repopulation = repopulation
            };
        }
        Diagnostic[] Children(JsonElement value, string name) => value.TryGetProperty(name, out var children)
            ? children.EnumerateArray().Select(Read).ToArray() : [];
        static int Number(JsonElement value, string name) => value.TryGetProperty(name, out var number) ? number.GetInt32() : 0;
        static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var flag) && flag.GetBoolean();
        static Utf8String Text(JsonElement value, string name) => value.TryGetProperty(name, out var text) ? JsonStrings.GetString(text) : default;
    }

    internal static Utf8String Signature(Utf8String owner, Utf8String text, int mapPosition, IReadOnlyList<Diagnostic>? diagnostics, bool includeText)
    {
        var builder = new Utf8StringBuilder(mapPosition >= 0 ? text[..mapPosition] : text);
        if (diagnostics is not null)
        {
            var pending = new Stack<Diagnostic>(diagnostics.Reverse());
            while (pending.TryPop(out var diagnostic))
            {
                builder.Append((byte)'\n');
                if (diagnostic.FileName is { } file)
                {
                    if (file != owner) builder.Append(IncrementalOptions.Relative(CompilerPath.DirectoryName(owner), file, true));
                    builder.Append(Utf8String.FromString($"({diagnostic.Start},{diagnostic.Length}): "));
                }
                builder.Append(diagnostic.Message.Category switch
                {
                    DiagnosticCategory.Warning => "warning"u8, DiagnosticCategory.Error => "error"u8,
                    DiagnosticCategory.Suggestion => "suggestion"u8, _ => "message"u8
                });
                builder.Append(Utf8String.FromString($"{(int)diagnostic.Code}: ")).Append(diagnostic.Message.Key).Append((byte)'\n');
                foreach (var argument in diagnostic.Arguments) builder.Append(argument).Append((byte)'\n');
                for (int i = diagnostic.RelatedInformation.Count - 1; i >= 0; i--) pending.Push(diagnostic.RelatedInformation[i]);
                for (int i = diagnostic.MessageChain.Count - 1; i >= 0; i--) pending.Push(diagnostic.MessageChain[i]);
            }
        }
        return BuildInfo.ComputeHash(builder.ToUtf8String(), includeText);
    }
}

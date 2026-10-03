using System.Text.Json;
using TypeScript.Compiler.Api;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class ApiSessionTests
{
    internal static async Task LinesAsync()
    {
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            using var input = JsonDocument.Parse(line);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output)) await RunAsync(input.RootElement, writer);
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        }
    }
    private static async Task RunAsync(JsonElement input, Utf8JsonWriter writer)
    {
        Utf8String cwd = input.TryGetProperty("cwd", out var directory) ? JsonStrings.GetString(directory) : (Utf8String)"/"u8;
        bool sensitive = !input.TryGetProperty("caseSensitive", out var sensitivity) || sensitivity.GetBoolean();
        var files = input.GetProperty("files").EnumerateObject().Select(property =>
            KeyValuePair.Create(JsonStrings.GetName(property), JsonStrings.GetString(property.Value).Span.ToArray()));
        var fs = new LibraryFileSystem(new MemoryFileSystem(files, sensitive, cwd));
        await using var editor = input.TryGetProperty("lsp", out var lsp) && lsp.GetBoolean() ? new ProjectSession(fs, new() { CurrentDirectory = cwd }) : null;
        var sessions = new Dictionary<string, ApiSession>();
        var values = new Dictionary<string, JsonElement>();
        writer.WriteStartArray();
        try
        {
        foreach (var request in input.GetProperty("requests").EnumerateArray())
        {
            var method = JsonStrings.GetString(request.GetProperty("method"));
            using var buffer = new MemoryStream();
            using (var parameters = new Utf8JsonWriter(buffer)) Substitute(parameters, request.GetProperty("params"), values);
            using var parsed = JsonDocument.Parse(buffer.ToArray());
            if (method == "$editor"u8)
            {
                var change = parsed.RootElement;
                var kind = change.GetProperty("kind").GetString() switch { "open" => FileChangeKind.Open, "close" => FileChangeKind.Close, _ => FileChangeKind.Change };
                var content = change.TryGetProperty("text", out var text) ? JsonStrings.GetString(text) : default;
                editor!.Notify(new(kind, JsonStrings.GetString(change.GetProperty("fileName")), change.TryGetProperty("version", out var version) ? version.GetInt32() : 0,
                    content, ScriptKind.TS, kind == FileChangeKind.Change ? [new DocumentEdit(content)] : null));
                writer.WriteStartObject(); writer.WriteNull("result"); writer.WriteEndObject(); continue;
            }
            string client = request.TryGetProperty("session", out var named) ? named.GetString()! : "";
            if (!sessions.TryGetValue(client, out var session)) sessions.Add(client, session = editor is null ? new ApiSession(fs, new() { CurrentDirectory = cwd }) : new ApiSession(editor));
            if (method == "$close"u8)
            {
                await session.DisposeAsync(); writer.WriteStartObject(); writer.WriteNull("result"); writer.WriteEndObject(); continue;
            }
            try
            {
                var result = await session.HandleRequestAsync(method, parsed.RootElement);
                if (request.TryGetProperty("save", out var save))
                {
                    using var document = JsonDocument.Parse(result.Data);
                    values.Add(save.GetString()!, document.RootElement.Clone());
                }
                writer.WriteStartObject(); writer.WritePropertyName("result"); writer.WriteRawValue(result.Data.Span); writer.WriteEndObject();
            }
            catch (RpcException error)
            {
                writer.WriteStartObject(); writer.WriteString("error", error.Message); writer.WriteEndObject();
            }
        }
        }
        finally { foreach (var session in sessions.Values) await session.DisposeAsync(); }
        writer.WriteEndArray();
    }
    private static void Substitute(Utf8JsonWriter writer, JsonElement element, Dictionary<string, JsonElement> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("$ref", out var reference))
            {
                var segments = reference.GetString()!.Split('.'); var value = values[segments[0]];
                foreach (var segment in segments.Skip(1)) value = value.ValueKind == JsonValueKind.Array ? value[int.Parse(segment)] : value.GetProperty(segment);
                JsonStrings.WriteValue(writer, value); return;
            }
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject()) { JsonStrings.WriteName(writer, JsonStrings.GetName(property)); Substitute(writer, property.Value, values); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Substitute(writer, item, values); writer.WriteEndArray();
        }
        else JsonStrings.WriteValue(writer, element);
    }
}

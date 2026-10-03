using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class NavigationTests
{
    internal static async Task LinesAsync()
    {
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            using var input = JsonDocument.Parse(line);
            var root = input.RootElement;
            var file = await Parser.ParseSourceFileAsync(new(JsonStrings.GetString(root.GetProperty("file")),
                (ScriptKind)root.GetProperty("kind").GetInt32()), new(JsonStrings.GetString(root.GetProperty("text"))));
            var positions = root.TryGetProperty("positions", out var supplied) ? supplied.EnumerateArray().Select(p => p.GetInt32()).ToArray()
                : Enumerable.Range(0, file.Source.Length + 1).ToArray();
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartArray();
                foreach (int position in positions)
                {
                    writer.WriteStartArray();
                    for (int method = 0; method < 6; method++)
                    {
                        async ValueTask<SyntaxNode?> Get() => method switch
                        {
                            0 => await SyntaxNavigation.GetTokenAtPositionAsync(file, position),
                            1 => await SyntaxNavigation.GetTouchingPropertyNameAsync(file, position),
                            2 => await SyntaxNavigation.GetTouchingTokenAsync(file, position),
                            3 => await SyntaxNavigation.FindPrecedingTokenAsync(file, position),
                            4 => await SyntaxNavigation.FindPrecedingTokenAsync(file, position, excludeJSDoc: true),
                            _ => await SyntaxNavigation.FindNextTokenAsync(await SyntaxNavigation.GetTokenAtPositionAsync(file, position), file, file),
                        };
                        try
                        {
                            var node = await Get();
                            if (node is null) { writer.WriteNullValue(); continue; }
                            writer.WriteStartArray(); writer.WriteNumberValue((uint)node.Kind); writer.WriteNumberValue(node.Pos); writer.WriteNumberValue(node.End);
                            writer.WriteNumberValue((uint)(node.Parent?.Kind ?? 0)); writer.WriteNumberValue(node.Parent?.Pos ?? -1); writer.WriteNumberValue(node.Parent?.End ?? -1);
                            writer.WriteNumberValue(await SyntaxNavigation.GetStartAsync(node, file));
                            writer.WriteNumberValue(await SyntaxNavigation.GetStartAsync(node, file, true));
                            writer.WriteBooleanValue(ReferenceEquals(node, await Get())); writer.WriteEndArray();
                        }
                        catch (InvalidOperationException error)
                        { writer.WriteStartObject(); writer.WriteString("error", error.Message); writer.WriteEndObject(); }
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        }
    }
}

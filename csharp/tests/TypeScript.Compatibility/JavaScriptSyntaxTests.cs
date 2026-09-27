using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class JavaScriptSyntaxTests
{
    public static void Run(string repository)
    {
        using JsonDocument fixtures = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(repository, "csharp/tests/fixtures/parser/javascript-diagnostics.json")));
        int cases = 0, exact = 0, diagnostics = 0;
        foreach (JsonElement fixture in fixtures.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = fixture.GetProperty("name").GetString()!;
            var source = new SourceText(fixture.GetProperty("text").GetString()!);
            SourceFileNode file = Parser.ParseSourceFile(new(fixture.GetProperty("fileName").GetString()!), source);
            if ((file.ParseDiagnostics.Count != 0) != fixture.GetProperty("parseErrors").GetBoolean())
                throw new InvalidDataException($"{name}: source rejection changed.");
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                foreach (var diagnostic in file.JSDiagnostics.OrderBy(d => d.Code).ThenBy(d => d.Start).ThenBy(d => d.Length))
                {
                    if (diagnostic.FileName != file.FileName
                        || diagnostic.Start < 0
                        || diagnostic.Length < 0
                        || diagnostic.Start + diagnostic.Length > source.Bytes.Length
                        || diagnostic.RelatedInformation.Any(
                            d => d.FileName != file.FileName || d.Start < 0 || d.Start + d.Length > source.Bytes.Length))
                        throw new InvalidDataException($"{name}: JavaScript diagnostic source ownership or byte range.");
                    SyntaxTests.WriteJavaScriptDiagnostic(writer, source, diagnostic);
                    diagnostics++;
                }
                writer.WriteEndArray();
            }
            using JsonDocument actual = JsonDocument.Parse(buffer.ToArray());
            JsonElement expected = fixture.GetProperty("goJavaScriptDiagnostics");
            if (fixture.TryGetProperty("candidateJavaScriptDiagnostics", out JsonElement candidate))
            {
                expected = candidate;
                JsonElement rejection = fixture.GetProperty("requiredParseDiagnostic");
                if (!file.ParseDiagnostics.Any(d => (int)d.Code == rejection[0].GetInt32()
                    && source.ToUtf16Position(d.Start) == rejection[1].GetInt32()
                    && source.ToUtf16Position(d.Start + d.Length) - source.ToUtf16Position(d.Start) == rejection[2].GetInt32()))
                    throw new InvalidDataException($"{name}: documented recovery must still reject the original offending token.");
            }
            else
                exact++;
            if (!JsonElement.DeepEquals(expected, actual.RootElement))
                throw new InvalidDataException(
                    $"{name}: JavaScript diagnostics, arguments or related information differ. Expected {expected.GetRawText()}, actual {actual.RootElement.GetRawText()}.");
            cases++;
        }
        Console.WriteLine(
            $"JavaScript syntax: {cases} cases, {diagnostics} diagnostics; {exact} exact pinned-Go results, {cases - exact} documented malformed-source recovery case.");
    }
}

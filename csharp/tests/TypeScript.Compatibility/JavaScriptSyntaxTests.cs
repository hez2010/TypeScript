using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class JavaScriptSyntaxTests
{
    public static void Run(Utf8String repository)
    {
        using JsonDocument fixtures = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(repository.ToString(), "csharp/tests/fixtures/parser/javascript-diagnostics.json")));
        int cases = 0, exact = 0, diagnostics = 0;
        foreach (JsonElement fixture in fixtures.RootElement.GetProperty("cases"u8).EnumerateArray())
        {
            Utf8String name = TypeScript.Compiler.Configuration.JsonStrings.GetString(fixture.GetProperty("name"u8))!;
            var source = new SourceText(JsonStrings.GetString(fixture.GetProperty("text"u8))!);
            // The checked-in diagnostic fixture predates the byte-offset API.
            var bytePositions = new List<int> { 0 };
            for (int offset = 0; offset < source.Length;)
            {
                int point = Wtf8.Decode(source.Bytes.Span[offset..], out int width);
                if (point > 0xFFFF)
                    bytePositions.Add(offset);
                offset += width;
                bytePositions.Add(offset);
            }
            int BytePosition(int position) => position < 0 ? position : bytePositions[position];
            bool SameDiagnostics(JsonElement expected, JsonElement actual) => expected.GetArrayLength() == actual.GetArrayLength()
                && expected.EnumerateArray().Zip(actual.EnumerateArray()).All(pair => SameDiagnostic(pair.First, pair.Second));
            bool SameDiagnostic(JsonElement expected, JsonElement actual)
            {
                int start = expected[1].GetInt32();
                return expected[0].GetInt32() == actual[0].GetInt32()
                    && BytePosition(start) == actual[1].GetInt32()
                    && BytePosition(start + expected[2].GetInt32()) - BytePosition(start) == actual[2].GetInt32()
                    && JsonElement.DeepEquals(expected[3], actual[3])
                    && SameDiagnostics(expected[4], actual[4]);
            }
            SourceFileNode file = Parser.ParseSourceFile(new(JsonStrings.GetString(fixture.GetProperty("fileName"u8))!), source);
            if ((file.ParseDiagnostics.Count != 0) != fixture.GetProperty("parseErrors"u8).GetBoolean())
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
            JsonElement expected = fixture.GetProperty("goJavaScriptDiagnostics"u8);
            if (!SameDiagnostics(expected, actual.RootElement)
                && fixture.TryGetProperty("candidateJavaScriptDiagnostics"u8, out JsonElement candidate))
            {
                expected = candidate;
                JsonElement rejection = fixture.GetProperty("requiredParseDiagnostic"u8);
                if (!file.ParseDiagnostics.Any(d => (int)d.Code == rejection[0].GetInt32()
                    && d.Start == BytePosition(rejection[1].GetInt32())
                    && d.Start + d.Length == BytePosition(rejection[1].GetInt32() + rejection[2].GetInt32())))
                    throw new InvalidDataException($"{name}: documented recovery must still reject the original offending token.");
            }
            else
                exact++;
            if (!SameDiagnostics(expected, actual.RootElement))
                throw new InvalidDataException(
                    $"{name}: JavaScript diagnostics, arguments or related information differ. Expected {JsonStrings.Raw(expected)}, actual {JsonStrings.Raw(actual.RootElement)}.");
            cases++;
        }
        Console.WriteLine(
            $"JavaScript syntax: {cases} cases, {diagnostics} diagnostics; {exact} exact pinned-Go results, {cases - exact} documented malformed-source recovery case.");
    }
}

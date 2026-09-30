using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class SyntaxTests
{
    public static void ScanLines()
    {
        while (Console.ReadLine() is { } line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement request = document.RootElement;
            Utf8String name = TypeScript.Compiler.Configuration.JsonStrings.GetString(request.GetProperty("name"u8))!;
            byte[] bytes = request.TryGetProperty("path"u8, out var path)
                ? File.ReadAllBytes((JsonStrings.GetString(path)!).ToString())
                : request.GetProperty("text"u8).GetBytesFromBase64();
            bool Bool(Utf8String key) => request.TryGetProperty(key, out var p) && p.GetBoolean();
            Utf8String mode = request.TryGetProperty("mode"u8, out var modeElement) ? JsonStrings.GetString(modeElement)! : ""u8;
            if (mode == "units"u8)
            {
                TestSource units = TestDirectives.Parse(SourceEncoding.DecodeBytes(bytes), name);
                using var payload = new MemoryStream();
                using (var writer = new Utf8JsonWriter(payload))
                {
                    void Text(Utf8String value) => writer.WriteBase64StringValue(value.Span.ToArray());
                    void Pairs(IReadOnlyDictionary<Utf8String, Utf8String> values)
                    {
                        writer.WriteStartArray();
                        foreach (var pair in values.OrderBy(v => v.Key, Utf8StringComparer.Ordinal))
                        {
                            writer.WriteStartArray();
                            Text(pair.Key);
                            Text(pair.Value);
                            writer.WriteEndArray();
                        }
                        writer.WriteEndArray();
                    }
                    writer.WriteStartArray();
                    writer.WriteStartArray();
                    foreach (var unit in units.Units)
                    {
                        writer.WriteStartArray();
                        Text(unit.Name);
                        writer.WriteBase64StringValue(unit.Source.Bytes.Span);
                        Pairs(unit.Options);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                    Pairs(units.Symlinks);
                    Text(units.CurrentDirectory);
                    Pairs(units.Options);
                    writer.WriteEndArray();
                }
                using var result = new MemoryStream();
                using (var writer = new Utf8JsonWriter(result))
                {
                    writer.WriteStartObject();
                    writer.WriteString("name"u8, name);
                    writer.WriteNumber("tokens"u8, units.Units.Length);
                    writer.WriteNumber("diagnostics"u8, 0);
                    writer.WriteString(
                        "hash"u8,
                        Convert.ToHexStringLower(SHA256.HashData(payload.GetBuffer().AsSpan(0, (int)payload.Length))));
                    if (Bool("details"u8))
                    {
                        writer.WritePropertyName("details"u8);
                        writer.WriteRawValue(payload.GetBuffer().AsSpan(0, (int)payload.Length));
                    }
                    writer.WriteEndObject();
                }
                Console.WriteLine(Encoding.UTF8.GetString(result.ToArray()));
                continue;
            }
            var scanner = new Scanner(new SourceText(bytes), !Bool("trivia"u8), Bool("jsx"u8));
            bool hasFileName = request.TryGetProperty("fileName"u8, out var fileName);
            SourceFileNode? file = mode == "parse"u8
                ? Parser.ParseSourceFile(
                    new(
                        hasFileName ? CompilerPath.Resolve("/fixtures"u8, JsonStrings.GetString(fileName)!) : name,
                        hasFileName ? ScriptKind.Unknown : Bool("jsx"u8) ? ScriptKind.TSX : ScriptKind.TS),
                    scanner.Source)
                : null;
            using var buffer = new MemoryStream();
            int tokens = 0;
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                writer.WriteStartArray();
                if (file is not null)
                {
                    var visited = new HashSet<SyntaxNode>(ReferenceEqualityComparer.Instance);
                    Func<int, int> position = static value => value;
                    foreach (SyntaxNode node in file.DescendantsAndSelf())
                    {
                        if (!visited.Add(node))
                            throw new InvalidDataException($"{name}: duplicate or cyclic AST node {node.Kind} at {node.Pos}.");
                        if (!(node.Pos == -1 && node.End == -1)
                            && (node.Pos < 0 || node.End < node.Pos || node.End > file.Source.Bytes.Length))
                            throw new InvalidDataException($"{name}: invalid byte range [{node.Pos}, {node.End}) for {node.Kind}.");
                        for (int childIndex = 0; childIndex < node.ChildCount; childIndex++)
                            if (!ReferenceEquals(node.GetChild(childIndex).Parent, node))
                                throw new InvalidDataException($"{name}: {node.Kind} child {childIndex} has the wrong parent.");
                        writer.WriteStartArray();
                        writer.WriteNumberValue((int)node.Kind);
                        writer.WriteNumberValue(node.Pos);
                        writer.WriteNumberValue(node.End);
                        writer.WriteNumberValue((uint)node.Flags);
                        Utf8String value = node switch
                        {
                            IdentifierNode n => n.Text,
                            PrivateIdentifierNode n => n.Text,
                            StringLiteralNode n => n.Text,
                            NumericLiteralNode n => n.Text,
                            BigIntLiteralNode n => n.Text,
                            RegularExpressionLiteralNode n => n.Text,
                            NoSubstitutionTemplateLiteralNode n => n.Text,
                            TemplateHeadNode n => n.Text,
                            TemplateMiddleNode n => n.Text,
                            TemplateTailNode n => n.Text,
                            JsxTextNode n => n.Text,
                            _ => ""u8,
                        };
                        writer.WriteBase64StringValue(value.Span.ToArray());
                        writer.WriteNumberValue(node.ChildCount);
                        AstScalarProperties.Write(writer, node);
                        AstScalarProperties.WriteLists(writer, node, position);
                        writer.WriteEndArray();
                        tokens++;
                    }
                }
                else
                    while (true)
                    {
                        SyntaxKind kind = mode switch
                        {
                            _ when mode == "jsdoc"u8 => scanner.ScanJSDocToken(),
                            _ when mode == "jsx"u8 => scanner.ScanJsxToken(),
                            _ => scanner.Scan()
                        };
                        if (mode == "greater"u8)
                            kind = scanner.RescanGreaterThanToken();
                        if (mode == "regex"u8 && kind is SyntaxKind.SlashToken or SyntaxKind.SlashEqualsToken)
                            kind = scanner.RescanSlashToken();
                        writer.WriteStartArray();
                        writer.WriteNumberValue((int)kind);
                        writer.WriteNumberValue(scanner.FullStart);
                        writer.WriteNumberValue(scanner.TokenStart);
                        writer.WriteNumberValue(scanner.Position);
                        writer.WriteNumberValue((int)scanner.Flags);
                        bool hasValue = kind is SyntaxKind.Identifier or SyntaxKind.PrivateIdentifier
                            || kind is >= SyntaxKind.FirstKeyword and <= SyntaxKind.LastKeyword
                            || kind is >= SyntaxKind.NumericLiteral and <= SyntaxKind.TemplateTail;
                        writer.WriteBase64StringValue(hasValue ? scanner.Value.Span.ToArray() : []);
                        writer.WriteEndArray();
                        tokens++;
                        if (kind == SyntaxKind.EndOfFile)
                            break;
                    }
                writer.WriteEndArray();
                writer.WriteStartArray();
                foreach (var error in file?.ParseDiagnostics ?? scanner.Diagnostics)
                {
                    int start = file is null ? error.Start : error.Start;
                    int length = file is null ? error.Length : (error.Start + error.Length) - start;
                    writer.WriteStartArray();
                    writer.WriteNumberValue((int)error.Code);
                    writer.WriteNumberValue(start);
                    writer.WriteNumberValue(length);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteStartArray();
                if (file is not null)
                    foreach (var error in file.JSDiagnostics.OrderBy(d => d.Code).ThenBy(d => d.Start).ThenBy(d => d.Length))
                    {
                        int start = error.Start;
                        int length = (error.Start + error.Length) - start;
                        writer.WriteStartArray();
                        writer.WriteNumberValue((int)error.Code);
                        writer.WriteNumberValue(start);
                        writer.WriteNumberValue(length);
                        writer.WriteEndArray();
                    }
                writer.WriteEndArray();
                writer.WriteEndArray();
            }
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                writer.WriteString("name"u8, name);
                writer.WriteNumber("tokens"u8, tokens);
                writer.WriteNumber("diagnostics"u8, file?.ParseDiagnostics.Count ?? scanner.Diagnostics.Count);
                writer.WriteNumber("jsDiagnostics"u8, file?.JSDiagnostics.Count ?? 0);
                writer.WriteString("hash"u8, Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))));
                if (Bool("details"u8))
                {
                    writer.WritePropertyName("details"u8);
                    writer.WriteRawValue(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
                }
                if (Bool("jsDetails"u8) && file is not null)
                {
                    writer.WriteStartArray("jsDetails"u8);
                    foreach (var error in file.JSDiagnostics.OrderBy(d => d.Code).ThenBy(d => d.Start).ThenBy(d => d.Length))
                        WriteJavaScriptDiagnostic(writer, file.Source, error);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(output.GetBuffer().AsSpan(0, (int)output.Length)));
        }
    }

    internal static void WriteJavaScriptDiagnostic(
        Utf8JsonWriter writer,
        SourceText source,
        TypeScript.Compiler.Diagnostics.Diagnostic diagnostic)
    {
        int start = diagnostic.Start;
        writer.WriteStartArray();
        writer.WriteNumberValue((int)diagnostic.Code);
        writer.WriteNumberValue(start);
        writer.WriteNumberValue((diagnostic.Start + diagnostic.Length) - start);
        writer.WriteStartArray();
        foreach (Utf8String argument in diagnostic.Arguments)
            writer.WriteStringValue(argument.Span);
        writer.WriteEndArray();
        writer.WriteStartArray();
        foreach (var related in diagnostic.RelatedInformation)
            WriteJavaScriptDiagnostic(writer, source, related);
        writer.WriteEndArray();
        writer.WriteEndArray();
    }
}

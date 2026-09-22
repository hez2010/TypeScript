using System.Globalization;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Configuration;

internal sealed class ConfigSyntax
{
    public SourceFileNode Source { get; }
    public JsonElement Root { get; }
    private readonly SyntaxNode? root;
    public ConfigSyntax(string fileName, byte[] bytes, List<Diagnostic> errors, CancellationToken cancellation)
    {
        Source = Parser.ParseSourceFile(new(fileName, ScriptKind.JSON), new SourceText(SourceEncoding.DecodeBytes(bytes)), cancellation);
        errors.AddRange(Source.ParseDiagnostics.Select(d => d with { FileName = fileName }));
        void ConversionError(DiagnosticMessage message, SyntaxNode? node)
        {
            // Parser recovery already explains malformed syntax. Report conversion-only
            // errors on otherwise parsed JSON rather than cascading at synthetic nodes.
            if (Source.ParseDiagnostics.Count == 0) errors.Add(Diagnostic(message, node));
        }
        root = Source.Statements?.FirstOrDefault() is ExpressionStatementNode statement ? statement.Expression : null;
        if (root is not null && root is not ObjectLiteralExpressionNode)
        { errors.Add(Diagnostic(Messages.The_root_value_of_a_0_file_must_be_an_object, root, [CompilerPath.BaseName(fileName)])); root = null; }
        using var buffer = new MemoryStream();
        var namePatches = new List<(int Start, int End, string Name)>();
        using (var writer = new Utf8JsonWriter(buffer, new() { MaxDepth = int.MaxValue }))
        {
            var pending = new Stack<(SyntaxNode? Node, string? Name, byte Operation)>();
            if (root is null) { writer.WriteStartObject(); writer.WriteEndObject(); }
            else pending.Push((root, null, 0));
            while (pending.TryPop(out var item))
            {
                cancellation.ThrowIfCancellationRequested();
                if (item.Operation == 1) { writer.WriteEndObject(); continue; }
                if (item.Operation == 2) { writer.WriteEndArray(); continue; }
                if (item.Name is not null) JsonStrings.WriteName(writer, item.Name, namePatches);
                switch (item.Node)
                {
                    case ObjectLiteralExpressionNode obj:
                        writer.WriteStartObject(); pending.Push((null, null, 1));
                        if (obj.Properties is not null)
                            for (int i = obj.Properties.Count - 1; i >= 0; i--)
                                if (obj.Properties[i] is PropertyAssignmentNode property)
                                {
                                    string? name = property.Name switch { StringLiteralNode text => text.Text, IdentifierNode identifier => identifier.Text, NumericLiteralNode number => number.Text, _ => null };
                                    if (name is null) { ConversionError(Messages.Property_assignment_expected, property); continue; }
                                    CheckDoubleQuoted(property.Name);
                                    pending.Push((property.Initializer, name, 0));
                                }
                                else ConversionError(Messages.Property_assignment_expected, obj.Properties[i]);
                        break;
                    case ArrayLiteralExpressionNode array:
                        writer.WriteStartArray(); pending.Push((null, null, 2));
                        if (array.Elements is not null) for (int i = array.Elements.Count - 1; i >= 0; i--) pending.Push((array.Elements[i], null, 0));
                        break;
                    case StringLiteralNode text: CheckDoubleQuoted(text); JsonStrings.WriteString(writer, text.Text); break;
                    case NumericLiteralNode number: WriteNumber(number, false); break;
                    case PrefixUnaryExpressionNode { Operator: SyntaxKind.MinusToken, Operand: NumericLiteralNode number }: WriteNumber(number, true); break;
                    case { Kind: SyntaxKind.TrueKeyword }: writer.WriteBooleanValue(true); break;
                    case { Kind: SyntaxKind.FalseKeyword }: writer.WriteBooleanValue(false); break;
                    case { Kind: SyntaxKind.NullKeyword } or null: writer.WriteNullValue(); break;
                    default: ConversionError(Messages.Property_value_can_only_be_string_literal_numeric_literal_true_false_null_object_literal_or_array_literal, item.Node); writer.WriteNullValue(); break;
                }
            }
            void WriteNumber(NumericLiteralNode number, bool negative)
            {
                ReadOnlySpan<byte> raw = Source.Source.Bytes.Span[Start(number)..number.End];
                bool jsonNumber;
                try
                {
                    var reader = new Utf8JsonReader(raw);
                    jsonNumber = reader.Read() && reader.TokenType == JsonTokenType.Number && reader.BytesConsumed == raw.Length;
                }
                catch (JsonException) { jsonNumber = false; }
                if (jsonNumber)
                {
                    if (negative) writer.WriteRawValue("-" + System.Text.Encoding.UTF8.GetString(raw), skipInputValidation: true);
                    else writer.WriteRawValue(raw, skipInputValidation: true);
                }
                else if (double.TryParse(number.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && !double.IsNaN(value))
                {
                    // Non-JSON forms such as hexadecimal literals use the scanner's
                    // canonical numeric value. Infinity still needs a valid JSON spelling.
                    if (double.IsInfinity(value)) writer.WriteRawValue(negative ? "-1e309" : "1e309", skipInputValidation: true);
                    else writer.WriteNumberValue(negative ? -value : value);
                }
                else { ConversionError(Messages.Property_value_can_only_be_string_literal_numeric_literal_true_false_null_object_literal_or_array_literal, number); writer.WriteNullValue(); }
            }
        }
        Root = JsonStrings.Parse(buffer, namePatches);
        void CheckDoubleQuoted(SyntaxNode? node)
        {
            if (node is null) return;
            int start = Start(node);
            if (start >= Source.Source.Bytes.Length || Source.Source.Bytes.Span[start] != '"') ConversionError(Messages.String_literal_with_double_quotes_expected, node);
        }
    }
    public SyntaxNode? Value(params ReadOnlySpan<string> keys)
    {
        SyntaxNode? node = root;
        foreach (string key in keys)
            node = node is ObjectLiteralExpressionNode obj ? obj.Properties?.OfType<PropertyAssignmentNode>().LastOrDefault(p => p.Name is StringLiteralNode text && text.Text == key || p.Name is IdentifierNode identifier && identifier.Text == key)?.Initializer : null;
        return node;
    }
    public SyntaxNode? PropertyName(string section, string name)
    {
        SyntaxNode? node = section.Length == 0 ? root : Value(section);
        return node is ObjectLiteralExpressionNode obj ? obj.Properties?.OfType<PropertyAssignmentNode>().LastOrDefault(p => p.Name is StringLiteralNode text && text.Text == name || p.Name is IdentifierNode identifier && identifier.Text == name)?.Name : null;
    }
    private int Start(SyntaxNode node)
    {
        var scanner = new Scanner(Source.Source); scanner.ResetPosition(Source.Source.ToUtf16Position(Math.Max(0, node.Pos))); scanner.Scan();
        return Source.Source.ToBytePosition(scanner.TokenStart);
    }
    public Diagnostic Diagnostic(DiagnosticMessage message, SyntaxNode? node, params string[] arguments)
    {
        int start = node is null ? 0 : Start(node);
        return new(message, start, node is null ? 0 : Math.Max(0, node.End - start), arguments) { FileName = Source.FileName };
    }
}

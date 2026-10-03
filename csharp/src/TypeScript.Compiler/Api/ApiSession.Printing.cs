using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compiler.Api;

public sealed partial class ApiSession
{
    private static RpcResponse PrintNode(JsonElement parameters, CancellationToken cancellation)
    {
        var node = DecodeFactoryNode(parameters, cancellation);
        var printer = new SyntaxPrinter(new()
        {
            PreserveSourceNewlines = ApiJson.Boolean(parameters, "preserveSourceNewlines"u8),
            NeverAsciiEscape = ApiJson.Boolean(parameters, "neverAsciiEscape"u8),
            TerminateUnterminatedLiterals = ApiJson.Boolean(parameters, "terminateUnterminatedLiterals"u8),
        });
        return RpcResponse.String(printer.Print(node, cancellation: cancellation));
    }

    private async ValueTask<RpcResponse> FormatNodeForInsertionAsync(JsonElement parameters, CancellationToken cancellation)
    {
        await using var pin = Pin(ApiJson.UInt64(parameters, "snapshot"u8));
        var program = pin.Data.Program(ApiJson.String(parameters, "project"u8));
        var document = ApiJson.Get(parameters, "file"u8);
        var file = program.GetFile(Document(document))?.Syntax
            ?? throw new ApiException($"source file not found: {(document.ValueKind == JsonValueKind.Object ? ApiJson.String(document, "uri"u8) : ApiJson.String(document))}");
        var node = DecodeFactoryNode(parameters, cancellation);
        return RpcResponse.String(await SourceFormatter.FormatNodeForInsertionAsync(node, file, ApiJson.Int32(parameters, "position"u8),
            pin.Data.Snapshot.UserPreferences.FormatCodeSettings, cancellation).ConfigureAwait(false));
    }

    private static SyntaxNode DecodeFactoryNode(JsonElement parameters, CancellationToken cancellation)
    {
        var bytes = DecodeBase64(ApiJson.String(parameters, "data"u8));
        if (bytes.Length < AstPacket.HeaderSize) throw new ApiException($"failed to decode AST: data too short for header: {bytes.Length} bytes");
        if (bytes[3] != AstPacket.FormatVersion) throw new ApiException($"failed to decode AST: unsupported protocol version {bytes[3]} (expected {AstPacket.FormatVersion})");
        DecodedAst ast;
        try { ast = AstDecoder.DecodeForPrinting(bytes, cancellation); }
        catch (InvalidDataException error) { throw new ApiException($"failed to decode AST: {error.Message}"); }
        // The API prints factory nodes. The wire decoder's parent links are useful to
        // consumers, but the reference API's factory does not attach them for printing.
        foreach (var node in ast.Index.Nodes) if (node is not null) node.Parent = null;
        return ast.Root;
    }

    private static byte[] DecodeBase64(Utf8String value)
    {
        byte[] output = new byte[Base64.GetMaxDecodedFromUtf8Length(value.Length)];
        if (Base64.DecodeFromUtf8(value.Span, output, out _, out int written) == OperationStatus.Done
            && !value.Span.ContainsAny(" \t\v\f"u8)) return output.AsSpan(0, written).ToArray();
        // Match the protocol's error byte offset, including CR/LF inside padded input.
        int offset = 0; written = 0;
        while (offset < value.Length)
        {
            uint bits = 0; int digits = 4;
            for (int digit = 0; digit < 4; digit++)
            {
                if (offset == value.Length)
                {
                    if (digit == 0) return output.AsSpan(0, written).ToArray();
                    throw Invalid(offset - digit);
                }
                byte ch = value[offset++];
                int decoded = ch is >= (byte)'A' and <= (byte)'Z' ? ch - 'A' : ch is >= (byte)'a' and <= (byte)'z' ? ch - 'a' + 26
                    : ch is >= (byte)'0' and <= (byte)'9' ? ch - '0' + 52 : ch == '+' ? 62 : ch == '/' ? 63 : -1;
                if (decoded >= 0) { bits |= (uint)decoded << (18 - digit * 6); continue; }
                if (ch is (byte)'\r' or (byte)'\n') { digit--; continue; }
                if (ch != '=' || digit < 2) throw Invalid(offset - 1);
                if (digit == 2)
                {
                    SkipLines();
                    if (offset == value.Length) throw Invalid(offset);
                    if (value[offset] != '=') throw Invalid(offset - 1);
                    offset++;
                }
                SkipLines();
                if (offset < value.Length) throw Invalid(offset);
                digits = digit;
                break;
            }
            output[written++] = (byte)(bits >> 16);
            if (digits >= 3) output[written++] = (byte)(bits >> 8);
            if (digits == 4) output[written++] = (byte)bits;
        }
        return output.AsSpan(0, written).ToArray();
        void SkipLines() { while (offset < value.Length && value[offset] is (byte)'\r' or (byte)'\n') offset++; }
        static ApiException Invalid(int index) => new($"invalid base64 data: illegal base64 data at input byte {index}");
    }
}

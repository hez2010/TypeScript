using System.Text.Json;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compatibility;

internal static class LspProtocolTests
{
    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            var item = input.RootElement;
            using var value = item.GetProperty("raw").GetString() is { Length: > 0 } raw
                ? JsonDocument.Parse(raw, new() { MaxDepth = int.MaxValue }) : null;
            string? error = null;
            try
            {
                if (item.TryGetProperty("method", out var method)) LspProtocol.ValidateParams(Utf8String.FromString(method.GetString()!), value?.RootElement ?? default);
                else LspProtocol.ValidateType(item.GetProperty("type").GetString()!, value!.RootElement);
            }
            catch (RpcException ex) { error = ex.Message; }
            var result = RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); writer.WriteBoolean("accepted"u8, error is null);
                if (error is not null) writer.WriteString("error"u8, error);
                writer.WriteEndObject();
            });
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(result.Data.Span));
        }
    }
}

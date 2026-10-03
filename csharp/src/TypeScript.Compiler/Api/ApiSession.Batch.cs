using TypeScript.Compiler.Protocol;
using System.Text.Json;

namespace TypeScript.Compiler.Api;

public sealed partial class ApiSession
{
    private async ValueTask<RpcResponse> BatchAsync(JsonElement parameters, CancellationToken cancellation)
    {
        var token = ApiJson.String(parameters, "continuationToken"u8);
        ReadOnlyMemory<byte>[] responses;
        if (!token.IsEmpty)
        {
            lock (sync)
            {
                if (!batchPages.Remove(token, out responses!)) throw new ApiException("invalid batch continuation token");
            }
        }
        else
        {
            var requests = ApiJson.Array(parameters, "requests"u8); responses = new ReadOnlyMemory<byte>[requests.Length];
            for (int index = 0; index < requests.Length; index++)
            {
                var method = ApiJson.String(requests[index], "method"u8);
                RpcResponse response = RpcResponse.Null; Utf8String error = default;
                try
                {
                    if (method == "batchRequests"u8) throw new ApiException("batchRequests cannot be nested", true);
                    response = await HandleRequestAsync(method, ApiJson.Get(requests[index], "params"u8), cancellation).ConfigureAwait(false);
                }
                catch (Exception failure) { error = Utf8String.FromString(failure is RpcException or OperationCanceledException
                    ? failure.Message : $"panic: {failure}"); }
                responses[index] = RpcResponse.Json(writer =>
                {
                    writer.WriteStartObject(); ApiJson.String(writer, "method"u8, method); writer.WritePropertyName("result"u8);
                    if (response.IsBinary && IsAstMethod(method))
                    {
                        if (response.Data.IsEmpty) writer.WriteNullValue();
                        else { writer.WriteStartObject(); writer.WriteBase64String("data"u8, response.Data.Span); writer.WriteEndObject(); }
                    }
                    else writer.WriteRawValue(response.Data.Span);
                    if (!error.IsEmpty) ApiJson.String(writer, "error"u8, error);
                    writer.WriteEndObject();
                }).Data;
            }
        }
        int limit = ApiJson.Int32(parameters, "maxResponseBytesPerPage"u8); if (limit <= 0) limit = 300_000_000;
        long length = "{\"responses\":[]}"u8.Length; int count = 0;
        foreach (var response in responses)
        {
            long addition = response.Length + (count > 0 ? 1 : 0);
            if (count > 0 && length + addition > limit) break;
            length += addition; count++;
        }
        Utf8String next = default;
        if (count != responses.Length)
        {
            next = Id + "-"u8 + Interlocked.Increment(ref nextPage);
            int extra = ",\"continuationToken\":\"\""u8.Length + next.Length;
            while (count > 1 && length + extra > limit) { length -= responses[count - 1].Length + 1; count--; }
            lock (sync) batchPages.Add(next, responses[count..]);
        }
        return RpcResponse.Json(writer =>
        {
            writer.WriteStartObject(); writer.WritePropertyName("responses"u8); writer.WriteStartArray();
            for (int index = 0; index < count; index++) writer.WriteRawValue(responses[index].Span);
            writer.WriteEndArray(); if (!next.IsEmpty) ApiJson.String(writer, "continuationToken"u8, next); writer.WriteEndObject();
        });
    }
    private static bool IsAstMethod(Utf8String method) => method == "createSourceFile"u8 || method == "createSourceFileFromFile"u8
        || method == "getSourceFile"u8 || method == "getConfigSourceFile"u8 || method == "typeToTypeNode"u8 || method == "signatureToSignatureDeclaration"u8;
}

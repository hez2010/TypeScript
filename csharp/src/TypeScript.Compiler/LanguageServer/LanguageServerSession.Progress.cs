using TypeScript.Compiler.Protocol;
using static TypeScript.Compiler.LanguageServer.LspJson;

namespace TypeScript.Compiler.LanguageServer;

internal sealed partial class LanguageServerSession
{
    private ProjectLoadingProgress? progress;

    private ValueTask SendProgressAsync(LoadingProgressNotification notification)
    {
        var parameters = RpcResponse.Json(writer =>
        {
            writer.WriteStartObject(); String(writer, "token"u8, notification.Token);
            if (notification.Kind != "create"u8)
            {
                writer.WriteStartObject("value"u8); String(writer, "kind"u8, notification.Kind);
                if (notification.Kind == "begin"u8) String(writer, "title"u8, notification.Title);
                if (notification.Kind != "end"u8) String(writer, "message"u8, notification.Message);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }).Data;
        return notification.Kind == "create"u8
            ? connection!.CallWithoutResponseAsync("window/workDoneProgress/create"u8, parameters, apiLifetime.Token)
            : connection!.NotifyAsync("$/progress"u8, parameters, apiLifetime.Token);
    }
}

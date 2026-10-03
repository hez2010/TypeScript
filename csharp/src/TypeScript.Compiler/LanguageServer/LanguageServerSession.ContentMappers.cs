using System.Text.Json;
using System.Threading.Channels;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;
using static TypeScript.Compiler.LanguageServer.LspJson;

namespace TypeScript.Compiler.LanguageServer;

internal sealed partial class LanguageServerSession
{
    private readonly Channel<(ulong Snapshot, Utf8String[] Extensions, TaskCompletionSource? Completion)> contentMapperUpdates =
        Channel.CreateUnbounded<(ulong, Utf8String[], TaskCompletionSource?)>(new() { SingleReader = true, AllowSynchronousContinuations = false });
    private Task contentMapperRegistrar = Task.CompletedTask;

    private void StartContentMapperRegistrations()
    {
        projects!.SnapshotChanged += (_, snapshot) => QueueContentMapperRegistration(snapshot);
        contentMapperRegistrar = RegisterContentMappersAsync();
    }

    private void QueueContentMapperRegistration(ProjectWorkspaceSnapshot snapshot, TaskCompletionSource? completion = null)
    {
        var extensions = snapshot.Configurations.Values.SelectMany(config => config.ContentMappers).Concat(snapshot.InferredContentMappers)
            .SelectMany(mapper => mapper.Extensions).Distinct().Order().ToArray();
        if (!contentMapperUpdates.Writer.TryWrite((snapshot.Id, extensions, completion))) completion?.TrySetCanceled(apiLifetime.Token);
    }

    private async ValueTask<RpcResponse> SetContentMapperContributionsAsync(JsonElement parameters, CancellationToken cancellation)
    {
        var mappers = ContentMapperContributions.Parse(Get(parameters, "contributions"u8));
        if (!Boolean(initializationOptions, "runExternalCode"u8)) return RpcResponse.Null;
        CancelScheduledSnapshotUpdate();
        var documents = Array(Get(parameters, "openDocuments"u8)).Select(document => DocumentUris.ToFileName(String(document, "uri"u8))).ToArray();
        await using var snapshot = await projects!.SetContentMapperContributionsAsync(mappers, documents, cancellation).ConfigureAwait(false);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueContentMapperRegistration(snapshot.Snapshot, completion);
        await completion.Task.WaitAsync(cancellation).ConfigureAwait(false);
        return RpcResponse.Null;
    }

    private async Task RegisterContentMappersAsync()
    {
        ulong registeredSnapshot = 0;
        Utf8String[] registeredExtensions = [];
        bool registered = false;
        try
        {
            await foreach (var update in contentMapperUpdates.Reader.ReadAllAsync(apiLifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    if (update.Snapshot <= registeredSnapshot) continue;
                    if (!update.Extensions.SequenceEqual(registeredExtensions)
                        && Boolean(Get(Get(capabilities, "textDocument"u8), "synchronization"u8), "dynamicRegistration"u8))
                    {
                        if (registered)
                        {
                            await CallClientAsync("client/unregisterCapability"u8, ContentMapperRegistrations([], unregister: true).Data, apiLifetime.Token).ConfigureAwait(false);
                            registered = false;
                        }
                        if (update.Extensions.Length > 0)
                        {
                            await CallClientAsync("client/registerCapability"u8, ContentMapperRegistrations(update.Extensions).Data, apiLifetime.Token).ConfigureAwait(false);
                            registered = true;
                        }
                    }
                    registeredExtensions = update.Extensions; registeredSnapshot = update.Snapshot;
                }
                catch (RpcException) { } // Retain the previous revision so the next update retries.
                catch (IOException) { }
                finally { update.Completion?.TrySetResult(); }
            }
        }
        catch (OperationCanceledException) when (apiLifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (apiLifetime.IsCancellationRequested) { }
        finally
        {
            while (contentMapperUpdates.Reader.TryRead(out var update)) update.Completion?.TrySetCanceled(apiLifetime.Token);
        }
    }

    private static readonly (Utf8String Id, Utf8String Method, Utf8String Capability)[] contentMapperRegistrations =
    [
        new("did-open"u8, "didOpen"u8, "synchronization"u8), new("did-change"u8, "didChange"u8, "synchronization"u8),
        new("did-close"u8, "didClose"u8, "synchronization"u8), new("diagnostic"u8, "diagnostic"u8, "diagnostic"u8),
        new("hover"u8, "hover"u8, "hover"u8), new("signature-help"u8, "signatureHelp"u8, "signatureHelp"u8),
        new("definition"u8, "definition"u8, "definition"u8), new("type-definition"u8, "typeDefinition"u8, "typeDefinition"u8),
        new("implementation"u8, "implementation"u8, "implementation"u8), new("references"u8, "references"u8, "references"u8),
        new("document-highlight"u8, "documentHighlight"u8, "documentHighlight"u8), new("completion"u8, "completion"u8, "completion"u8),
        new("rename"u8, "rename"u8, "rename"u8), new("semantic-tokens"u8, "semanticTokens"u8, "semanticTokens"u8),
        new("document-symbol"u8, "documentSymbol"u8, "documentSymbol"u8), new("folding-range"u8, "foldingRange"u8, "foldingRange"u8),
        new("selection-range"u8, "selectionRange"u8, "selectionRange"u8), new("inlay-hint"u8, "inlayHint"u8, "inlayHint"u8),
        new("code-lens"u8, "codeLens"u8, "codeLens"u8), new("code-action"u8, "codeAction"u8, "codeAction"u8),
        new("formatting"u8, "formatting"u8, "formatting"u8), new("range-formatting"u8, "rangeFormatting"u8, "rangeFormatting"u8),
        new("on-type-formatting"u8, "onTypeFormatting"u8, "onTypeFormatting"u8), new("linked-editing"u8, "linkedEditingRange"u8, "linkedEditingRange"u8),
        new("call-hierarchy"u8, "prepareCallHierarchy"u8, "callHierarchy"u8), new("will-rename-files"u8, "willRenameFiles"u8, "fileOperations"u8),
    ];

    private RpcResponse ContentMapperRegistrations(Utf8String[] extensions, bool unregister = false) => RpcResponse.Json(writer =>
    {
        writer.WriteStartObject(); writer.WriteStartArray(unregister ? "unregisterations"u8 : "registrations"u8);
        foreach (var (id, method, capability) in contentMapperRegistrations)
        {
            bool workspace = capability == "fileOperations"u8;
            var supported = Get(Get(capabilities, workspace ? "workspace"u8 : "textDocument"u8), capability.Span);
            if (!Boolean(supported, "dynamicRegistration"u8) || workspace && !Boolean(supported, "willRename"u8)) continue;
            writer.WriteStartObject(); String(writer, "id"u8, "content-mapper-"u8 + id);
            String(writer, "method"u8, (workspace ? (Utf8String)"workspace/"u8 : (Utf8String)"textDocument/"u8) + method);
            if (!unregister)
            {
                writer.WriteStartObject("registerOptions"u8);
                if (workspace)
                {
                    writer.WriteStartArray("filters"u8);
                    foreach (var extension in extensions)
                    {
                        writer.WriteStartObject(); writer.WriteString("scheme"u8, "file"u8); writer.WriteStartObject("pattern"u8);
                        String(writer, "glob"u8, "**/*"u8 + extension); writer.WriteEndObject(); writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WriteStartArray("documentSelector"u8);
                    foreach (var extension in extensions)
                    { writer.WriteStartObject(); String(writer, "pattern"u8, "**/*"u8 + extension); writer.WriteEndObject(); }
                    writer.WriteEndArray();
                }
                if (method == "didChange"u8) writer.WriteNumber("syncKind"u8, 2);
                if (method == "diagnostic"u8)
                {
                    writer.WriteString("identifier"u8, "typescript"u8); writer.WriteBoolean("interFileDependencies"u8, true); writer.WriteBoolean("workspaceDiagnostics"u8, false);
                }
                if (method == "signatureHelp"u8)
                {
                    Strings("triggerCharacters"u8, ["("u8, ","u8, "<"u8]); Strings("retriggerCharacters"u8, [")"u8]);
                }
                if (method == "completion"u8)
                {
                    writer.WriteBoolean("resolveProvider"u8, true); Strings("triggerCharacters"u8, ["."u8, "\""u8, "'"u8, "`"u8, "/"u8, "@"u8, "<"u8, "#"u8, " "u8, "*"u8]);
                    writer.WriteStartObject("completionItem"u8); writer.WriteBoolean("labelDetailsSupport"u8, true); writer.WriteEndObject();
                }
                if (method == "rename"u8) writer.WriteBoolean("prepareProvider"u8, true);
                if (method == "semanticTokens"u8)
                {
                    var legend = SemanticTokens.Negotiate(SemanticCapabilities());
                    writer.WriteStartObject("legend"u8); Strings("tokenTypes"u8, legend.TokenTypes); Strings("tokenModifiers"u8, legend.TokenModifiers);
                    writer.WriteEndObject(); writer.WriteBoolean("full"u8, true); writer.WriteBoolean("range"u8, true);
                }
                if (method == "codeLens"u8) writer.WriteBoolean("resolveProvider"u8, true);
                if (method == "codeAction"u8) Strings("codeActionKinds"u8, ["quickfix"u8, "source.organizeImports.ts"u8, "source.removeUnusedImports.ts"u8, "source.sortImports.ts"u8, "source.fixAll.ts"u8]);
                if (method == "onTypeFormatting"u8)
                { writer.WriteString("firstTriggerCharacter"u8, "{"u8); Strings("moreTriggerCharacter"u8, ["}"u8, ";"u8, "\n"u8]); }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject();
        void Strings(ReadOnlySpan<byte> name, IEnumerable<Utf8String> values)
        { writer.WriteStartArray(name); foreach (var value in values) JsonStrings.WriteString(writer, value); writer.WriteEndArray(); }
    });
}

using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Incremental;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Syntax;
using static TypeScript.Compiler.LanguageServer.LspJson;

namespace TypeScript.Compiler.LanguageServer;

/// <summary>One editor connection owns its project session and negotiated document coordinates.</summary>
internal sealed partial class LanguageServerSession(IFileSystem fileSystem, LanguageServerOptions options) : IRpcHandler, IAsyncDisposable
{
    private RpcConnection? connection;
    private ProjectSession? projects;
    private JsonElement initializeParams, capabilities, initializationOptions;
    private PositionEncoding encoding = PositionEncoding.Utf16;
    private int state;
    internal void Connect(RpcConnection value) { connection = value; logPublisher = PublishLogsAsync(); }

    public async ValueTask<RpcResponse> HandleRequestAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var document = parameters.IsEmpty ? null : JsonDocument.Parse(parameters, new() { MaxDepth = int.MaxValue });
            var input = document?.RootElement ?? default;
            if (method == "initialize"u8) { LspProtocol.ValidateParams(method, input); return Initialize(input); }
            if (state < 2) throw new RpcException(-32002, "ServerNotInitialized"u8);
            if (state == 3) throw new RpcException(-32600, "InvalidRequest"u8);
            LspProtocol.ValidateParams(method, input);
            if (method == "shutdown"u8)
            {
                state = 3;
                await StopApiSessionsAsync().ConfigureAwait(false);
                if (projects is not null) await projects.DisposeAsync().ConfigureAwait(false);
                return RpcResponse.Null;
            }
            if (method == "custom/initializeAPISession"u8) return InitializeApiSession(input);
            if (DeveloperRequest(method, input) is { } developerResponse) return developerResponse;
            if (method == "custom/projectInfo"u8)
            {
                var name = DocumentUris.ToFileName(String(Get(input, "textDocument"u8), "uri"u8));
                await using var snapshot = await projects!.GetSnapshotAsync([name], cancellation).ConfigureAwait(false);
                var owner = snapshot.Snapshot.GetDefaultProject(name);
                return RpcResponse.Json(writer =>
                {
                    writer.WriteStartObject(); String(writer, "configFilePath"u8, owner?.Kind == ProjectKind.Configured ? owner.Configuration.FileName : default); writer.WriteEndObject();
                });
            }
            if (method == "custom/setContentMapperContributions"u8) return await SetContentMapperContributionsAsync(input, cancellation);
            if (method == "completionItem/resolve"u8)
            {
                var item = ReadCompletion(input);
                var itemFileName = item.Data!.FileName;
                await using var snapshot = await projects!.GetSnapshotAsync([itemFileName], cancellation).ConfigureAwait(false);
                var owner = snapshot.Snapshot.GetDefaultProject(itemFileName);
                if (owner?.Program?.GetFile(itemFileName) is not { } itemFile)
                    throw new RpcException(-32603, "file not found: "u8 + itemFileName);
                try
                {
                    return Completion(await new LanguageServiceDocument(owner.Program, itemFile, encoding).ResolveCompletionItemAsync(owner, item,
                        CompletionOptions(capabilities), snapshot.Snapshot.UserPreferences, cancellation));
                }
                catch (ArgumentException error) { throw new RpcException(-32603, Utf8String.FromString(error.Message), error); }
            }
            if (method == "codeLens/resolve"u8)
            {
                var lens = ReadCodeLens(input);
                var lensFile = DocumentUris.ToFileName(lens.Data.Uri);
                await using var snapshot = await projects!.GetSnapshotAsync([lensFile], cancellation).ConfigureAwait(false);
                if (snapshot.Snapshot.GetDefaultProject(lensFile)?.Program?.GetFile(lensFile) is null)
                    throw new RpcException(-32801, "ContentModified"u8);
                var command = Get(initializationOptions, "codeLensShowLocationsCommandName"u8);
                return CodeLens(await CrossProjectReferences.ResolveCodeLensAsync(projects, snapshot.Snapshot, lens,
                    command.ValueKind == JsonValueKind.String ? String(command) : null, encoding, cancellation));
            }
            if (method == "callHierarchy/incomingCalls"u8 || method == "callHierarchy/outgoingCalls"u8)
            {
                var item = Get(input, "item"u8);
                var itemFileName = DocumentUris.ToFileName(String(item, "uri"u8));
                var itemPosition = Position(Get(Get(item, "selectionRange"u8), "start"u8));
                await using var snapshot = await projects!.GetSnapshotAsync([itemFileName], cancellation).ConfigureAwait(false);
                var owner = snapshot.Snapshot.GetDefaultProject(itemFileName);
                if (owner?.Program?.GetFile(itemFileName) is not { } itemFile) return RpcResponse.Null;
                bool incoming = method == "callHierarchy/incomingCalls"u8;
                return CallHierarchyCalls(incoming
                    ? await CrossProjectReferences.IncomingCallsAsync(projects, snapshot.Snapshot, itemFileName, itemPosition, encoding, cancellation)
                    : await new LanguageServiceDocument(owner.Program, itemFile, encoding).GetOutgoingCallsAsync(owner, itemPosition, cancellation), incoming);
            }
            if (method == "workspace/willRenameFiles"u8)
                return WorkspaceEdit(await CrossProjectReferences.RenameFilesAsync(projects!, Array(Get(input, "files"u8))
                    .Select(file => new RenameFileChange(String(file, "oldUri"u8), String(file, "newUri"u8))).ToArray(), RenameOptions(capabilities), encoding, false, cancellation));
            if (method == "workspace/symbol"u8)
            {
                var uri = String(Get(input, "textDocument"u8), "uri"u8);
                await using var current = await projects!.GetSnapshotAsync(cancellation: cancellation).ConfigureAwait(false);
                bool scoped = !uri.IsEmpty && current.Snapshot.UserPreferences.WorkspaceSymbolsScope == "currentProject"u8;
                await using var workspace = scoped ? await projects.GetSnapshotAsync([DocumentUris.ToFileName(uri)], cancellation).ConfigureAwait(false)
                    : await projects.GetWorkspaceSnapshotAsync(cancellation).ConfigureAwait(false);
                var candidates = workspace.Snapshot.Projects.Where(project => project.Kind != ProjectKind.Synthetic && project.Program is not null
                    && (!scoped || project.ContainsFile(workspace.Snapshot.Host.Path(DocumentUris.ToFileName(uri)))));
                return WorkspaceSymbols(SyntaxLanguageService.GetWorkspaceSymbols(candidates.Select(project => project.Program!), String(input, "query"u8),
                    workspace.Snapshot.UserPreferences, encoding, cancellation));
            }
            bool semantic = method == "textDocument/semanticTokens/full"u8 || method == "textDocument/semanticTokens/range"u8;
            bool formatting = method == "textDocument/formatting"u8 || method == "textDocument/rangeFormatting"u8 || method == "textDocument/onTypeFormatting"u8;
            if (!semantic && !formatting && method != "textDocument/selectionRange"u8 && method != "textDocument/linkedEditingRange"u8
                && method != "textDocument/foldingRange"u8 && method != "textDocument/documentSymbol"u8 && method != "textDocument/hover"u8 && method != "textDocument/codeLens"u8 && method != "textDocument/inlayHint"u8 && method != "textDocument/_vs_onAutoInsert"u8 && method != "textDocument/diagnostic"u8
                && method != "textDocument/codeAction"u8 && method != "textDocument/completion"u8 && method != "textDocument/signatureHelp"u8 && method != "textDocument/prepareCallHierarchy"u8 && method != "textDocument/definition"u8 && method != "textDocument/typeDefinition"u8
                && method != "custom/textDocument/sourceDefinition"u8 && method != "textDocument/references"u8 && method != "textDocument/implementation"u8 && method != "textDocument/_vs_references"u8 && method != "textDocument/documentHighlight"u8 && method != "custom/textDocument/multiDocumentHighlight"u8 && method != "textDocument/rename"u8 && method != "textDocument/prepareRename"u8)
                throw new RpcException(-32601, "MethodNotFound"u8);
            var fileName = DocumentUris.ToFileName(String(Get(input, method == "textDocument/_vs_onAutoInsert"u8 ? "_vs_textDocument"u8 : "textDocument"u8), "uri"u8));
            await using var lease = await projects!.GetSnapshotAsync([fileName], cancellation).ConfigureAwait(false);
            var project = lease.Snapshot.GetDefaultProject(fileName);
            var file = project?.Program?.GetFile(lease.Snapshot.Host.Path(fileName));
            if (file is null) return method == "textDocument/diagnostic"u8
                ? DocumentDiagnostics([], Boolean(capabilities, "_vs_supportsVisualStudioExtensions"u8)) : RpcResponse.Null;
            var service = new LanguageServiceDocument(project!.Program!, file, encoding);
            if (method == "textDocument/codeAction"u8)
                return CodeActions(await service.GetCodeActionsAsync(project, CodeActionContext(Get(input, "context"u8)),
                    lease.Snapshot.UserPreferences with { Locale = DiagnosticLocale(lease.Snapshot) }, cancellation));
            if (method == "textDocument/completion"u8)
            {
                var trigger = Get(Get(input, "context"u8), "triggerCharacter"u8);
                return Completions(await service.GetCompletionsAsync(project, Position(Get(input, "position"u8)), CompletionOptions(capabilities),
                    lease.Snapshot.UserPreferences with { Locale = DiagnosticLocale(lease.Snapshot) }, trigger.ValueKind == JsonValueKind.String ? String(trigger) : null, cancellation));
            }
            if (method == "textDocument/diagnostic"u8)
                return await DocumentDiagnosticsAsync(service, project, lease.Snapshot, cancellation).ConfigureAwait(false);
            if (method == "textDocument/_vs_onAutoInsert"u8)
                return AutoInsertion(await service.GetAutoInsertionAsync(Position(Get(input, "_vs_position"u8)), String(input, "_vs_ch"u8), lease.Snapshot.UserPreferences, cancellation));
            if (method == "textDocument/inlayHint"u8)
                return InlayHints(await service.GetInlayHintsAsync(project, Range(Get(input, "range"u8)), lease.Snapshot.UserPreferences, cancellation));
            if (method == "textDocument/codeLens"u8)
                return CodeLenses(await service.GetCodeLensesAsync(project, lease.Snapshot.UserPreferences.CodeLens, cancellation));
            if (method == "textDocument/prepareCallHierarchy"u8)
                return CallHierarchyItems(await service.PrepareCallHierarchyAsync(project, Position(Get(input, "position"u8)), cancellation));
            if (method == "textDocument/prepareRename"u8)
                return PrepareRename(await service.GetRenameInfoAsync(project, Position(Get(input, "position"u8)), preferences: lease.Snapshot.UserPreferences,
                    capabilities: RenameOptions(capabilities), cancellation: cancellation));
            if (method == "textDocument/rename"u8)
                return WorkspaceEdit(await CrossProjectReferences.RenameAsync(projects, lease.Snapshot, fileName, Position(Get(input, "position"u8)),
                    String(input, "newName"u8), RenameOptions(capabilities), encoding, cancellation));
            if (method == "textDocument/documentHighlight"u8 || method == "custom/textDocument/multiDocumentHighlight"u8)
                return DocumentHighlights(await service.GetDocumentHighlightsAsync(project, Position(Get(input, "position"u8)),
                    Array(Get(input, "filesToSearch"u8)).Select(value => DocumentUris.ToFileName(String(value))).ToArray(), cancellation),
                    method == "textDocument/documentHighlight"u8 ? String(Get(input, "textDocument"u8), "uri"u8) : null);
            if (method == "textDocument/_vs_references"u8)
                return VisualStudioReferences(await CrossProjectReferences.VisualStudioReferencesAsync(projects, lease.Snapshot, fileName, Position(Get(input, "position"u8)),
                    Boolean(capabilities, "_vs_supportsVisualStudioExtensions"u8), encoding, cancellation));
            if (method == "textDocument/references"u8)
                return References(await CrossProjectReferences.ReferencesAsync(projects, lease.Snapshot, fileName, Position(Get(input, "position"u8)),
                    Boolean(Get(input, "context"u8), "includeDeclaration"u8), encoding, cancellation));
            if (method == "textDocument/implementation"u8)
            {
                bool links = Boolean(Get(Get(capabilities, "textDocument"u8), "implementation"u8), "linkSupport"u8);
                return Definitions(await CrossProjectReferences.ImplementationsAsync(projects, lease.Snapshot, fileName, Position(Get(input, "position"u8)), links, encoding, cancellation), links);
            }
            if (method == "custom/textDocument/sourceDefinition"u8 || method == "textDocument/definition"u8 && lease.Snapshot.UserPreferences.PreferGoToSourceDefinition)
                return Definitions(await service.GetSourceDefinitionsAsync(project, Position(Get(input, "position"u8)), cancellation),
                    Boolean(Get(Get(capabilities, "textDocument"u8), "definition"u8), "linkSupport"u8));
            if (method == "textDocument/definition"u8 || method == "textDocument/typeDefinition"u8)
                return Definitions(await service.GetDefinitionsAsync(project, Position(Get(input, "position"u8)), method == "textDocument/typeDefinition"u8, cancellation),
                    Boolean(Get(Get(capabilities, "textDocument"u8), method == "textDocument/typeDefinition"u8 ? "typeDefinition"u8 : "definition"u8), "linkSupport"u8));
            if (method == "textDocument/signatureHelp"u8)
                return SignatureHelp(await service.GetSignatureHelpAsync(project, Position(Get(input, "position"u8)),
                    SignatureHelpOptions(input, capabilities), cancellation).ConfigureAwait(false));
            if (method == "textDocument/hover"u8)
                return Hover(await service.GetHoverAsync(project, Position(Get(input, "position"u8)),
                    HoverOptions(input, capabilities, lease.Snapshot.UserPreferences.MaximumHoverLength), cancellation).ConfigureAwait(false));
            if (method == "textDocument/documentSymbol"u8)
                return DocumentSymbols(await service.GetDocumentSymbolsAsync(cancellation).ConfigureAwait(false),
                    Boolean(Get(Get(capabilities, "textDocument"u8), "documentSymbol"u8), "hierarchicalDocumentSymbolSupport"u8), String(Get(input, "textDocument"u8), "uri"u8));
            if (formatting)
            {
                var preferences = lease.Snapshot.UserPreferences;
                if (preferences.EnableFormatting == false) return RpcResponse.Null;
                var settings = Get(input, "options"u8);
                var format = preferences.FormatCodeSettings with
                {
                    TabSize = Integer(settings, "tabSize"u8), IndentSize = Integer(settings, "tabSize"u8), ConvertTabsToSpaces = Boolean(settings, "insertSpaces"u8),
                };
                if (Get(settings, "trimTrailingWhitespace"u8) is { ValueKind: JsonValueKind.True or JsonValueKind.False } trim)
                    format = format with { TrimTrailingWhitespace = trim.GetBoolean() };
                return TextEdits(method == "textDocument/onTypeFormatting"u8
                    ? await service.GetFormattingEditsAfterKeystrokeAsync(Position(Get(input, "position"u8)), String(input, "ch"u8), format, cancellation).ConfigureAwait(false)
                    : await service.GetFormattingEditsAsync(format, method == "textDocument/rangeFormatting"u8 ? Range(Get(input, "range"u8)) : null, cancellation).ConfigureAwait(false));
            }
            if (semantic)
                return SemanticTokenResult(await service.GetSemanticTokensAsync(project, SemanticCapabilities(),
                    method == "textDocument/semanticTokens/range"u8 ? Range(Get(input, "range"u8)) : null, cancellation).ConfigureAwait(false));
            if (method == "textDocument/selectionRange"u8)
                return SelectionRanges(await service.GetSelectionRangesAsync(Array(Get(input, "positions"u8)).Select(Position).ToArray(), cancellation).ConfigureAwait(false));
            if (method == "textDocument/linkedEditingRange"u8)
                return LinkedEditingRanges(await service.GetLinkedEditingRangesAsync(Position(Get(input, "position"u8)), cancellation).ConfigureAwait(false));
            var folding = Get(Get(capabilities, "textDocument"u8), "foldingRange"u8);
            return FoldingRanges(await service.GetFoldingRangesAsync(Boolean(folding, "lineFoldingOnly"u8), Boolean(Get(folding, "foldingRange"u8), "collapsedText"u8), cancellation).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw new RpcException(-32800, "RequestCancelled"u8); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new RpcException(-32602, "InvalidParams"u8, error); }
        catch (Exception error) when (error is not RpcException and not OperationCanceledException)
        { ReportRequestFailure(method, error); throw; }
        finally { PublishGlobalDiagnostics(); }
    }

    private RpcResponse Initialize(JsonElement input)
    {
        if (state != 0) throw new RpcException(-32600, "InvalidRequest"u8);
        initializeParams = input.Clone();
        capabilities = Get(initializeParams, "capabilities"u8);
        initializationOptions = Get(initializeParams, "initializationOptions"u8);
        if (Get(initializationOptions, "logVerbosity"u8) is { ValueKind: JsonValueKind.Number } level && level.TryGetInt32(out int verbosity) && verbosity is >= 0 and <= 5)
            logger.SetVerbosity(verbosity);
        if (Array(Get(Get(capabilities, "general"u8), "positionEncodings"u8)).Any(value => String(value) == "utf-8"u8)) encoding = PositionEncoding.Utf8;
        state = 1;
        return RpcResponse.Json(writer =>
        {
            writer.WriteStartObject(); writer.WriteStartObject("serverInfo"u8); writer.WriteString("name"u8, "typescript"u8);
            String(writer, "version"u8, BuildInfo.CompilerVersion); writer.WriteEndObject();
            writer.WriteStartObject("capabilities"u8); writer.WriteString("positionEncoding"u8, encoding == PositionEncoding.Utf8 ? "utf-8"u8 : "utf-16"u8);
            writer.WriteStartObject("textDocumentSync"u8); writer.WriteBoolean("openClose"u8, true); writer.WriteNumber("change"u8, 2);
            writer.WriteBoolean("save"u8, true); writer.WriteEndObject();
            writer.WriteBoolean("selectionRangeProvider"u8, true); writer.WriteBoolean("linkedEditingRangeProvider"u8, true); writer.WriteBoolean("foldingRangeProvider"u8, true);
            writer.WriteBoolean("documentFormattingProvider"u8, true); writer.WriteBoolean("documentRangeFormattingProvider"u8, true);
            writer.WriteBoolean("documentSymbolProvider"u8, true);
            writer.WriteBoolean("workspaceSymbolProvider"u8, true);
            writer.WriteBoolean("hoverProvider"u8, true);
            writer.WriteStartObject("renameProvider"u8); writer.WriteBoolean("prepareProvider"u8, true); writer.WriteEndObject();
            writer.WriteStartObject("workspace"u8); writer.WriteStartObject("fileOperations"u8); writer.WriteStartObject("willRename"u8);
            writer.WriteStartArray("filters"u8); writer.WriteStartObject(); writer.WriteString("scheme"u8, "file"u8);
            writer.WriteStartObject("pattern"u8); writer.WriteString("glob"u8, "**/*.{ts,tsx,js,jsx,cts,cjs,mts,mjs,json}"u8);
            writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
            writer.WriteBoolean("definitionProvider"u8, true); writer.WriteBoolean("typeDefinitionProvider"u8, true);
            writer.WriteBoolean("documentHighlightProvider"u8, true); writer.WriteBoolean("referencesProvider"u8, true); writer.WriteBoolean("_vs_referencesProvider"u8, true); writer.WriteBoolean("implementationProvider"u8, true);
            writer.WriteBoolean("callHierarchyProvider"u8, true);
            writer.WriteStartObject("codeLensProvider"u8); writer.WriteBoolean("resolveProvider"u8, true); writer.WriteEndObject();
            writer.WriteBoolean("inlayHintProvider"u8, true);
            writer.WriteStartObject("completionProvider"u8); writer.WriteBoolean("resolveProvider"u8, true);
            writer.WriteStartArray("triggerCharacters"u8);
            foreach (var trigger in new Utf8String[] { "."u8, "\""u8, "'"u8, "`"u8, "/"u8, "@"u8, "<"u8, "#"u8, " "u8, "*"u8 }) JsonStrings.WriteString(writer, trigger);
            writer.WriteEndArray(); writer.WriteStartObject("completionItem"u8); writer.WriteBoolean("labelDetailsSupport"u8, true); writer.WriteEndObject(); writer.WriteEndObject();
            writer.WriteStartObject("codeActionProvider"u8); writer.WriteStartArray("codeActionKinds"u8);
            foreach (var kind in new Utf8String[] { "quickfix"u8, "source.organizeImports.ts"u8, "source.removeUnusedImports.ts"u8, "source.sortImports.ts"u8, "source.fixAll.ts"u8 }) JsonStrings.WriteString(writer, kind);
            writer.WriteEndArray(); writer.WriteEndObject();
            writer.WriteStartObject("diagnosticProvider"u8); writer.WriteString("identifier"u8, "typescript"u8); writer.WriteBoolean("interFileDependencies"u8, true); writer.WriteBoolean("workspaceDiagnostics"u8, false); writer.WriteEndObject();
            writer.WriteStartObject("_vs_onAutoInsertProvider"u8); writer.WriteStartArray("_vs_triggerCharacters"u8); writer.WriteStringValue(">"u8); writer.WriteEndArray(); writer.WriteEndObject();
            writer.WriteStartObject("experimental"u8);
            writer.WriteBoolean("customSourceDefinitionProvider"u8, true); writer.WriteBoolean("customMultiDocumentHighlightProvider"u8, true);
            writer.WriteEndObject();
            writer.WriteStartObject("signatureHelpProvider"u8);
            writer.WriteStartArray("triggerCharacters"u8); writer.WriteStringValue("("u8); writer.WriteStringValue(","u8); writer.WriteStringValue("<"u8); writer.WriteEndArray();
            writer.WriteStartArray("retriggerCharacters"u8); writer.WriteStringValue(")"u8); writer.WriteEndArray(); writer.WriteEndObject();
            writer.WriteStartObject("documentOnTypeFormattingProvider"u8); writer.WriteString("firstTriggerCharacter"u8, "{"u8);
            writer.WriteStartArray("moreTriggerCharacter"u8); writer.WriteStringValue("}"u8); writer.WriteStringValue(";"u8); writer.WriteStringValue("\n"u8); writer.WriteEndArray(); writer.WriteEndObject();
            var legend = SemanticTokens.Negotiate(SemanticCapabilities());
            writer.WriteStartObject("semanticTokensProvider"u8); writer.WriteStartObject("legend"u8);
            writer.WriteStartArray("tokenTypes"u8); foreach (var type in legend.TokenTypes) JsonStrings.WriteString(writer, type); writer.WriteEndArray();
            writer.WriteStartArray("tokenModifiers"u8); foreach (var modifier in legend.TokenModifiers) JsonStrings.WriteString(writer, modifier); writer.WriteEndArray();
            writer.WriteEndObject(); writer.WriteBoolean("full"u8, true); writer.WriteBoolean("range"u8, true); writer.WriteEndObject();
            writer.WriteEndObject(); writer.WriteEndObject();
        });
    }

    private SemanticTokenLegend SemanticCapabilities()
    {
        var semantic = Get(Get(capabilities, "textDocument"u8), "semanticTokens"u8);
        return new(Array(Get(semantic, "tokenTypes"u8)).Select(String).ToArray(), Array(Get(semantic, "tokenModifiers"u8)).Select(String).ToArray());
    }

    public async ValueTask HandleNotificationAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation)
    {
        try { await HandleNotificationCoreAsync(method, parameters, cancellation).ConfigureAwait(false); }
        catch (Exception error) when (error is RpcException or JsonException or InvalidOperationException or FormatException or OverflowException)
        { logger.Log(1, "error handling notification: "u8 + Utf8String.FromString(error.Message)); }
        catch (Exception error) when (error is not OperationCanceledException)
        { ReportRequestFailure(method, error); throw; }
    }

    private async ValueTask HandleNotificationCoreAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        LspProtocol.ValidateParams(method, parameters);
        if (method == "exit"u8) { connection!.Stop(); return; }
        if (method == "initialized"u8)
        {
            if (state != 1) return;
            var cwd = options.CurrentDirectory;
            var workspace = Get(capabilities, "workspace"u8);
            var folders = Array(Get(initializeParams, "workspaceFolders"u8)).ToArray();
            if (Boolean(workspace, "workspaceFolders"u8) && folders.Length == 1)
                cwd = DocumentUris.ToFileName(String(folders[0], "uri"u8));
            else if (String(initializeParams, "rootUri"u8) is { IsEmpty: false } uri) cwd = DocumentUris.ToFileName(uri);
            else if (String(initializeParams, "rootPath"u8) is { IsEmpty: false } path) cwd = path;
            if (!CompilerPath.IsAbsolute(cwd)) cwd = options.CurrentDirectory;
            if (Boolean(Get(capabilities, "window"u8), "workDoneProgress"u8))
                progress = new(SendProgressAsync, String(initializeParams, "locale"u8), options.ProgressDelay, options.TimeProvider, apiLifetime.Token);
            projects = new(fileSystem, new()
            {
                CurrentDirectory = cwd, DefaultLibraryDirectory = options.DefaultLibraryDirectory, TypingsLocation = options.TypingsLocation,
                PositionEncoding = encoding, RunExternalCode = Boolean(initializationOptions, "runExternalCode"u8),
                StartMapperProcess = options.StartMapperProcess,
                MapperLog = logger.MapperMessage,
                Progress = progress is null ? null : progress.EnqueueAsync,
                TrackFileWatches = SupportsFileWatching,
            });
            StartDiagnostics();
            StartContentMapperRegistrations();
            StartFileWatching();
            StartTelemetry();
            if (options.InferredCompilerOptions is { } inferred) projects.SetInferredOptions(inferred);
            projects.DiagnosticsRefreshRequested += ScheduleDiagnosticsRefresh;
            projects.CodeLensRefreshRequested += RefreshCodeLenses;
            projects.InlayHintsRefreshRequested += RefreshInlayHints;
            var preferences = new UserPreferences { Locale = String(initializeParams, "locale"u8) }.WithConfig(Get(initializationOptions, "userPreferences"u8));
            if (Boolean(workspace, "configuration"u8))
            {
                var result = await CallClientAsync("workspace/configuration"u8,
                    "{\"items\":[{\"section\":\"js/ts\"},{\"section\":\"typescript\"},{\"section\":\"javascript\"},{\"section\":\"editor\"}]}"u8.ToArray(), cancellation).ConfigureAwait(false);
                using var config = JsonDocument.Parse(result); preferences = UserPreferences.ReadConfiguration(config.RootElement);
            }
            projects.Configure(preferences);
            await CallClientAsync("client/registerCapability"u8,
                "{\"registrations\":[{\"id\":\"typescript-config-watch-id\",\"method\":\"workspace/didChangeConfiguration\",\"registerOptions\":{\"section\":[\"js/ts\",\"typescript\",\"javascript\",\"editor\"]}}]}"u8.ToArray(), cancellation).ConfigureAwait(false);
            state = 2;
            return;
        }
        if (state != 2) return;
        if (method == "$/setTrace"u8) return;
        if (method == "custom/setLogVerbosity"u8)
        {
            int verbosity = Integer(parameters, "verbosity"u8);
            if (verbosity is < 0 or > 5) throw new RpcException(-32602, "InvalidParams: invalid log verbosity "u8 + Utf8String.Format(verbosity));
            logger.SetVerbosity(verbosity); return;
        }
        if (method == "workspace/didChangeConfiguration"u8)
        {
            if (Get(parameters, "settings"u8) is { ValueKind: JsonValueKind.Object } settings) projects!.Configure(UserPreferences.ReadConfiguration(settings));
            return;
        }
        if (method == "workspace/didChangeWatchedFiles"u8)
        { await WatchedFilesChangedAsync(parameters, cancellation).ConfigureAwait(false); return; }
        if (method != "textDocument/didOpen"u8 && method != "textDocument/didChange"u8 && method != "textDocument/didClose"u8 && method != "textDocument/didSave"u8) return;
        var textDocument = Get(parameters, "textDocument"u8);
        var fileName = DocumentUris.ToFileName(String(textDocument, "uri"u8));
        int version = Integer(textDocument, "version"u8);
        if (method == "textDocument/didOpen"u8)
        {
            var language = String(textDocument, "languageId"u8);
            var kind = language == "typescript"u8 ? ScriptKind.TS : language == "typescriptreact"u8 ? ScriptKind.TSX
                : language == "javascript"u8 ? ScriptKind.JS : language == "javascriptreact"u8 ? ScriptKind.JSX : ScriptKind.Unknown;
            CancelScheduledSnapshotUpdate();
            projects!.Notify(new(FileChangeKind.Open, fileName, version, String(textDocument, "text"u8), kind));
            await using var opened = await projects.GetSnapshotAsync([fileName], cancellation).ConfigureAwait(false);
        }
        else if (method == "textDocument/didChange"u8)
        {
            var edits = Array(Get(parameters, "contentChanges"u8)).Select(change => new DocumentEdit(String(change, "text"u8),
                Get(change, "range"u8) is { ValueKind: JsonValueKind.Object } range ? Range(range) : null)).ToArray();
            projects!.Notify(new(FileChangeKind.Change, fileName, version, Edits: edits));
            await using var current = projects.AcquireCurrentSnapshot();
            if (current is not null && IsMappedFile(current.Snapshot, fileName)) ScheduleDiagnosticsRefresh(TimeSpan.Zero);
            else CancelScheduledDiagnosticsRefresh();
        }
        else if (method == "textDocument/didClose"u8)
        { projects!.Notify(new(FileChangeKind.Close, fileName)); ScheduleSnapshotUpdate(); }
        else if (method == "textDocument/didSave"u8) projects!.Notify(new(FileChangeKind.Save, fileName));
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopApiSessionsAsync().ConfigureAwait(false); }
        finally
        {
            if (projects is not null) await projects.DisposeAsync().ConfigureAwait(false);
            apiLifetime.Dispose(); state = 3;
        }
    }
}

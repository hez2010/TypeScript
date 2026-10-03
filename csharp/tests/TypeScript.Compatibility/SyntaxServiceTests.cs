using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Api;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compatibility;

internal static class SyntaxServiceTests
{
    internal static async Task LinesAsync()
    {
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line, new() { MaxDepth = int.MaxValue });
            var input = document.RootElement;
            var parameters = input.GetProperty("params");
            if (input.GetProperty("method").GetString() == "workspace/symbol")
            { await WorkspaceAsync(input, parameters); continue; }
            if (input.GetProperty("method").GetString() == "workspace/willRenameFiles")
            {
                try { await FileRenameAsync(input, parameters); }
                catch (Exception error)
                {
                    var failed = TypeScript.Compiler.Protocol.RpcResponse.Json(writer =>
                    { writer.WriteStartObject(); writer.WriteString("error", error.ToString()); writer.WriteEndObject(); });
                    Console.WriteLine(System.Text.Encoding.UTF8.GetString(failed.Data.Span));
                }
                continue;
            }
            var fileName = input.GetProperty("method").GetString() == "completionItem/resolve" ? JsonStrings.GetString(parameters.GetProperty("data").GetProperty("fileName")) : DocumentUris.ToFileName(JsonStrings.GetString((parameters.TryGetProperty("data", out var lensData) ? lensData : parameters.TryGetProperty("item", out var hierarchyItem) ? hierarchyItem : parameters.GetProperty(parameters.TryGetProperty("_vs_textDocument", out _) ? "_vs_textDocument" : "textDocument")).GetProperty("uri")));
            if (input.GetProperty("method").GetString() is "textDocument/semanticTokens/full" or "textDocument/semanticTokens/range" or "textDocument/hover" or "textDocument/signatureHelp" or "textDocument/definition" or "textDocument/typeDefinition" or "custom/textDocument/sourceDefinition" or "textDocument/references" or "textDocument/documentHighlight" or "custom/textDocument/multiDocumentHighlight" or "textDocument/_vs_references" or "textDocument/implementation" or "textDocument/rename" or "textDocument/prepareRename" or "textDocument/prepareCallHierarchy" or "callHierarchy/incomingCalls" or "callHierarchy/outgoingCalls" or "textDocument/codeLens" or "codeLens/resolve" or "textDocument/inlayHint" or "textDocument/_vs_onAutoInsert" or "textDocument/codeAction" or "textDocument/diagnostic" or "textDocument/completion" or "completionItem/resolve")
            {
                try { await SemanticAsync(input, parameters, fileName); }
                catch (Exception error)
                {
                    using var failed = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(failed)) { writer.WriteStartObject(); if (error is TypeScript.Compiler.Protocol.RpcException rpc)
                    { writer.WriteStartObject("error"); writer.WriteNumber("code", rpc.Code); writer.WriteString("message", rpc.Message); writer.WriteEndObject(); }
                    else writer.WriteString("error", error.ToString()); writer.WriteEndObject(); }
                    Console.WriteLine(System.Text.Encoding.UTF8.GetString(failed.ToArray()));
                }
                continue;
            }
            var text = JsonStrings.GetString(input.GetProperty("files").GetProperty(fileName));
            var file = await Parser.ParseSourceFileAsync(new(fileName, DocumentSnapshot.InferKind(fileName)), new(text));
            var encoding = input.TryGetProperty("encoding", out var supplied) && supplied.GetString() == "utf-16" ? PositionEncoding.Utf16 : PositionEncoding.Utf8;
            LanguageServiceDocument service = new(file, encoding);
            if (input.TryGetProperty("projections", out var projections))
            {
                List<DocumentProjection> mapped = [];
                foreach (var projection in projections.EnumerateArray())
                {
                    var name = JsonStrings.GetString(projection.GetProperty("fileName"));
                    var syntax = await Parser.ParseSourceFileAsync(new(name, (ScriptKind)projection.GetProperty("scriptKind").GetInt32()),
                        new(JsonStrings.GetString(projection.GetProperty("text"))));
                    var map = new SpanMap(projection.GetProperty("segments").EnumerateArray().Select(segment =>
                        new MappingSegment(segment[0].GetInt32(), segment[1].GetInt32(), segment[2].GetInt32(), segment[3].GetInt32(),
                            (MappingKind)segment[4].GetInt32(), (MappingFeature)segment[5].GetInt32())));
                    mapped.Add(new(syntax, new(syntax, new(JsonStrings.GetString(projection.GetProperty("original"))), map, name, "test"u8, "test"u8, []), encoding));
                }
                service = new(mapped.ToArray());
            }
            static DocumentPosition Position(JsonElement value) => new(value.GetProperty("line").GetUInt32(), value.GetProperty("character").GetUInt32());
            if (input.GetProperty("method").GetString() == "textDocument/documentSymbol")
            {
                var capabilities = input.GetProperty("symbolCapabilities");
                var response = LspJson.DocumentSymbols(await service.GetDocumentSymbolsAsync(), LspJson.Boolean(capabilities, "hierarchicalDocumentSymbolSupport"u8),
                    JsonStrings.GetString(parameters.GetProperty("textDocument").GetProperty("uri")));
                Console.WriteLine(System.Text.Encoding.UTF8.GetString(response.Data.Span));
                continue;
            }
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output, new() { MaxDepth = int.MaxValue }))
            {
                if (input.GetProperty("method").GetString() == "textDocument/selectionRange")
                {
                    var results = await service.GetSelectionRangesAsync(parameters.GetProperty("positions").EnumerateArray().Select(Position).ToArray());
                    if (results is null) writer.WriteNullValue();
                    else
                    {
                        writer.WriteStartArray();
                        foreach (var result in results)
                        {
                            int depth = 0;
                            for (SelectionRange? current = result; current is not null; current = current.Parent)
                            {
                                writer.WriteStartObject(); depth++;
                                writer.WritePropertyName("range"); Range(writer, current.Range);
                                if (current.Parent is not null) writer.WritePropertyName("parent");
                            }
                            while (depth-- != 0) writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                }
                else if (input.GetProperty("method").GetString() == "textDocument/foldingRange")
                {
                    var capabilities = input.GetProperty("foldingCapabilities");
                    bool lineOnly = capabilities.TryGetProperty("lineFoldingOnly", out var lineCapability) && lineCapability.GetBoolean();
                    bool collapsed = capabilities.TryGetProperty("foldingRange", out var foldingCapability)
                        && foldingCapability.TryGetProperty("collapsedText", out var collapsedCapability) && collapsedCapability.GetBoolean();
                    var result = await service.GetFoldingRangesAsync(lineOnly, collapsed);
                    writer.WriteStartArray();
                    foreach (var range in result)
                    {
                        writer.WriteStartObject(); writer.WriteNumber("startLine", range.StartLine); writer.WriteNumber("startCharacter", range.StartCharacter);
                        writer.WriteNumber("endLine", range.EndLine); writer.WriteNumber("endCharacter", range.EndCharacter);
                        if (range.Kind is { } kind) writer.WriteString("kind", kind);
                        if (range.CollapsedText is { } banner) { writer.WritePropertyName("collapsedText"); JsonStrings.WriteString(writer, banner); }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    var result = await service.GetLinkedEditingRangesAsync(Position(parameters.GetProperty("position")));
                    if (result is null) writer.WriteNullValue();
                    else
                    {
                        writer.WriteStartObject(); writer.WriteStartArray("ranges");
                        foreach (var range in result.Ranges) Range(writer, range);
                        writer.WriteEndArray(); writer.WriteString("wordPattern", result.WordPattern); writer.WriteEndObject();
                    }
                }
            }
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        }
    }

    private static async Task WorkspaceAsync(JsonElement input, JsonElement parameters)
    {
        var files = input.GetProperty("files").EnumerateObject().Select(property => KeyValuePair.Create(JsonStrings.GetName(property), JsonStrings.GetString(property.Value).Span.ToArray()));
        var fs = new LibraryFileSystem(new MemoryFileSystem(files, true));
        var cwd = JsonStrings.GetString(input.GetProperty("cwd"));
        bool mapped = input.TryGetProperty("projections", out var projections);
        var projected = mapped ? projections.EnumerateArray().ToDictionary(projection => JsonStrings.GetString(projection.GetProperty("fileName"))) : [];
        IFileSystem source = fs;
        if (mapped) source = new SnapshotFileSystem(new OverlayFileSystem(fs, cwd, projected.Select(pair => new DocumentSnapshot(pair.Key,
            JsonStrings.GetString(pair.Value.GetProperty("text")), kind: (ScriptKind)pair.Value.GetProperty("scriptKind").GetInt32()))), cwd);
        await using var host = new ProjectSnapshotHost(source, new() { CurrentDirectory = cwd, DefaultLibraryDirectory = fs.LibraryDirectory });
        await using var snapshot = await host.CreateAsync(new()
        {
            CreatePrograms = input.GetProperty("programs").EnumerateArray().Select(program =>
            {
                var options = ApiJson.CompilerOptions(program.GetProperty("compilerOptions"));
                var roots = ApiJson.Strings(program, "rootFiles"u8);
                if (mapped)
                {
                    using var enabled = JsonDocument.Parse("true"); options.Set("allowNonTsExtensions"u8, enabled.RootElement);
                    roots = roots.Concat(projected.Keys).Distinct().ToArray();
                }
                return new CreateProgramRequest(roots, options);
            }).ToArray(),
        });
        var candidates = SyntaxLanguageService.WorkspaceSymbolFiles(snapshot.CreatedPrograms.Select(project => project.Program!),
            new() { ExcludeLibrarySymbolsInNavTo = input.GetProperty("excludeLibrarySymbols").GetBoolean() }, default);
        if (mapped) candidates = candidates.Select(item =>
        {
            if (!projected.TryGetValue(item.File.Syntax.FileName, out var projection)) return item;
            var map = new SpanMap(projection.GetProperty("segments").EnumerateArray().Select(segment =>
                new MappingSegment(segment[0].GetInt32(), segment[1].GetInt32(), segment[2].GetInt32(), segment[3].GetInt32(), (MappingKind)segment[4].GetInt32(), (MappingFeature)segment[5].GetInt32())));
            return (item.Path, item.File with { Mapping = new(item.File.Syntax, new(JsonStrings.GetString(projection.GetProperty("original"))), map, item.File.Syntax.FileName, "test"u8, "test"u8, []) },
                JsonStrings.GetString(projection.GetProperty("originalName")));
        });
        var result = SyntaxLanguageService.WorkspaceSymbols(candidates, JsonStrings.GetString(parameters.GetProperty("query")),
            input.GetProperty("encoding").GetString() == "utf-16" ? PositionEncoding.Utf16 : PositionEncoding.Utf8, default);
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(LspJson.WorkspaceSymbols(result).Data.Span));
    }

    private static async Task<bool> DefinitionsAsync(LanguageServiceDocument service, ProjectSnapshot project, JsonElement input, JsonElement parameters)
    {
        var method = input.GetProperty("method").GetString();
        if (method == "textDocument/codeAction")
        {
            using var codeActionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var prefs = new UserPreferences().WithConfig(LspJson.Get(input, "preferences"u8));
            var response = LspJson.CodeActions(await service.GetCodeActionsAsync(project, LspJson.CodeActionContext(LspJson.Get(parameters, "context"u8)), prefs, codeActionDeadline.Token));
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(response.Data.Span)); return true;
        }
        if (method is "textDocument/completion" or "completionItem/resolve")
        {
            using var completionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var prefs = new UserPreferences().WithConfig(LspJson.Get(input, "preferences"u8));
            var caps = LspJson.Get(input, "capabilities"u8);
            var trigger = LspJson.Get(LspJson.Get(parameters, "context"u8), "triggerCharacter"u8);
            if (input.TryGetProperty("priorCompletions", out var history))
                foreach (var previous in history.EnumerateArray())
                {
                    var request = previous.GetProperty("params");
                    var fileName = DocumentUris.ToFileName(JsonStrings.GetString(request.GetProperty("textDocument").GetProperty("uri")));
                    var previousService = request.GetProperty("textDocument").GetProperty("uri").GetString() == parameters.GetProperty("textDocument").GetProperty("uri").GetString()
                        ? service : new LanguageServiceDocument(project.Program!, project.Program!.GetFile(fileName)!,
                        input.GetProperty("encoding").GetString() == "utf-16" ? PositionEncoding.Utf16 : PositionEncoding.Utf8);
                    var previousTrigger = LspJson.Get(LspJson.Get(request, "context"u8), "triggerCharacter"u8);
                    await previousService.GetCompletionsAsync(project, LspJson.Position(request.GetProperty("position")),
                        LspJson.CompletionOptions(previous.GetProperty("capabilities")), new UserPreferences().WithConfig(previous.GetProperty("preferences")),
                        previousTrigger.ValueKind == JsonValueKind.String ? JsonStrings.GetString(previousTrigger) : null, completionDeadline.Token);
                }
            var response = method == "completionItem/resolve"
                ? LspJson.Completion(await service.ResolveCompletionItemAsync(project, LspJson.ReadCompletion(parameters), LspJson.CompletionOptions(caps), prefs, completionDeadline.Token))
                : LspJson.Completions(await service.GetCompletionsAsync(project, LspJson.Position(parameters.GetProperty("position")),
                LspJson.CompletionOptions(caps), prefs, trigger.ValueKind == JsonValueKind.String ? JsonStrings.GetString(trigger) : null, completionDeadline.Token));
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(response.Data.Span)); return true;
        }
        if (method == "textDocument/diagnostic")
        {
            using var diagnosticDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var prefs = new UserPreferences().WithConfig(LspJson.Get(input, "preferences"u8));
            var caps = LspJson.Get(input, "capabilities"u8);
            var response = LspJson.DocumentDiagnostics(await service.GetDiagnosticsAsync(project, prefs, LspJson.DiagnosticOptions(caps), diagnosticDeadline.Token), LspJson.Boolean(caps, "_vs_supportsVisualStudioExtensions"u8));
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(response.Data.Span)); return true;
        }
        if (method == "textDocument/_vs_onAutoInsert")
        {
            var prefs = new UserPreferences().WithConfig(LspJson.Get(input, "preferences"u8));
            var response = LspJson.AutoInsertion(await service.GetAutoInsertionAsync(LspJson.Position(parameters.GetProperty("_vs_position")), LspJson.String(parameters, "_vs_ch"u8), prefs));
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(response.Data.Span)); return true;
        }
        if (method == "textDocument/inlayHint")
        {
            using var inlayDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var prefs = new UserPreferences().WithConfig(LspJson.Get(input, "preferences"u8));
            var response = LspJson.InlayHints(await service.GetInlayHintsAsync(project, LspJson.Range(parameters.GetProperty("range")), prefs, inlayDeadline.Token));
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(response.Data.Span)); return true;
        }
        if (method is "textDocument/codeLens" or "codeLens/resolve")
        {
            using var lensDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var prefs = new UserPreferences().WithConfig(LspJson.Get(input, "preferences"u8));
            var command = LspJson.Get(input, "codeLensShowLocationsCommandName"u8);
            var response = method == "textDocument/codeLens"
                ? LspJson.CodeLenses(await service.GetCodeLensesAsync(project, prefs.CodeLens, lensDeadline.Token))
                : LspJson.CodeLens(await service.ResolveCodeLensAsync(project, LspJson.ReadCodeLens(parameters),
                    command.ValueKind == JsonValueKind.String ? JsonStrings.GetString(command) : null, lensDeadline.Token));
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(response.Data.Span)); return true;
        }
        if (method is "textDocument/prepareCallHierarchy" or "callHierarchy/incomingCalls" or "callHierarchy/outgoingCalls")
        {
            using var hierarchyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var point = HierarchyPosition(parameters);
            var response = method == "textDocument/prepareCallHierarchy"
                ? LspJson.CallHierarchyItems(await service.PrepareCallHierarchyAsync(project, point, hierarchyDeadline.Token))
                : LspJson.CallHierarchyCalls(method == "callHierarchy/incomingCalls"
                    ? await service.GetIncomingCallsAsync(project, point, hierarchyDeadline.Token)
                    : await service.GetOutgoingCallsAsync(project, point, hierarchyDeadline.Token), method == "callHierarchy/incomingCalls");
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(response.Data.Span)); return true;
        }
        if (method is "textDocument/rename" or "textDocument/prepareRename")
        {
            using var renameDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var preferences = UserPreferences.ReadConfiguration(default).WithConfig(LspJson.Get(input, "preferences"u8));
            var renameCapabilities = LspJson.RenameOptions(LspJson.Get(input, "capabilities"u8));
            var point = LspJson.Position(parameters.GetProperty("position"));
            var response = method == "textDocument/prepareRename"
                ? LspJson.PrepareRename(await service.GetRenameInfoAsync(project, point, preferences: preferences, capabilities: renameCapabilities, cancellation: renameDeadline.Token))
                : LspJson.WorkspaceEdit(await service.GetRenameEditsAsync(project, point, LspJson.String(parameters, "newName"u8), preferences, renameCapabilities, renameDeadline.Token));
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(response.Data.Span)); return true;
        }
        if (method is "textDocument/documentHighlight" or "custom/textDocument/multiDocumentHighlight")
        {
            using var highlightsDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var found = await service.GetDocumentHighlightsAsync(project, LspJson.Position(parameters.GetProperty("position")),
                ApiJson.Strings(parameters, "filesToSearch"u8).Select(DocumentUris.ToFileName).ToArray(), highlightsDeadline.Token);
            var response = LspJson.DocumentHighlights(found, method == "textDocument/documentHighlight" ? LspJson.String(LspJson.Get(parameters, "textDocument"u8), "uri"u8) : null);
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(response.Data.Span)); return true;
        }
        if (method is "textDocument/references" or "textDocument/_vs_references" or "textDocument/implementation")
        {
            using var referenceDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var position = LspJson.Position(parameters.GetProperty("position"));
            bool links = LspJson.Boolean(LspJson.Get(LspJson.Get(input.GetProperty("capabilities"), "textDocument"u8), "implementation"u8), "linkSupport"u8);
            var response = method == "textDocument/_vs_references"
                ? LspJson.VisualStudioReferences(await service.GetVisualStudioReferencesAsync(project, position, LspJson.Boolean(input.GetProperty("capabilities"), "_vs_supportsVisualStudioExtensions"u8), referenceDeadline.Token))
                : method == "textDocument/references"
                ? LspJson.References(await service.GetReferencesAsync(project, position, LspJson.Boolean(parameters.GetProperty("context"), "includeDeclaration"u8), referenceDeadline.Token))
                : LspJson.Definitions(await service.GetImplementationsAsync(project, position, links, referenceDeadline.Token), links);
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(response.Data.Span));
            return true;
        }
        if (method is not ("textDocument/definition" or "textDocument/typeDefinition" or "custom/textDocument/sourceDefinition")) return false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        bool types = method == "textDocument/typeDefinition";
        var capabilities = LspJson.Get(LspJson.Get(input.GetProperty("capabilities"), "textDocument"u8), types ? "typeDefinition"u8 : "definition"u8);
        bool source = method == "custom/textDocument/sourceDefinition" || !types && UserPreferences.ReadConfiguration(default).WithConfig(LspJson.Get(input, "preferences"u8)).PreferGoToSourceDefinition;
        var result = source ? await service.GetSourceDefinitionsAsync(project, LspJson.Position(parameters.GetProperty("position")), deadline.Token)
            : await service.GetDefinitionsAsync(project, LspJson.Position(parameters.GetProperty("position")), types, deadline.Token);
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(LspJson.Definitions(result, LspJson.Boolean(capabilities, "linkSupport"u8)).Data.Span));
        return true;
    }

    private static async Task SemanticAsync(JsonElement input, JsonElement parameters, Utf8String fileName)
    {
        if (input.GetProperty("method").GetString() is "textDocument/references" or "textDocument/_vs_references" or "textDocument/implementation" or "textDocument/rename" or "textDocument/prepareRename" or "textDocument/prepareCallHierarchy" or "callHierarchy/incomingCalls" or "callHierarchy/outgoingCalls" or "textDocument/codeLens" or "codeLens/resolve" or "textDocument/codeAction" or "textDocument/diagnostic" or "textDocument/completion" or "completionItem/resolve"
            && !input.TryGetProperty("mappedFiles", out _) && !input.TryGetProperty("projections", out _))
        { await ReferenceSessionAsync(input, parameters, fileName); return; }
        if (input.GetProperty("method").GetString() is "textDocument/hover" or "textDocument/signatureHelp" or "textDocument/definition" or "textDocument/typeDefinition" or "custom/textDocument/sourceDefinition" or "textDocument/references" or "textDocument/documentHighlight" or "custom/textDocument/multiDocumentHighlight" or "textDocument/_vs_references" or "textDocument/implementation" or "textDocument/rename" or "textDocument/prepareRename" or "textDocument/prepareCallHierarchy" or "callHierarchy/incomingCalls" or "callHierarchy/outgoingCalls" or "textDocument/codeLens" or "codeLens/resolve" or "textDocument/inlayHint" or "textDocument/_vs_onAutoInsert" or "textDocument/codeAction" or "textDocument/diagnostic" or "textDocument/completion" or "completionItem/resolve" && input.TryGetProperty("mappedFiles", out _))
        { await MappedHoverAsync(input, parameters, fileName); return; }
        var files = input.GetProperty("files").EnumerateObject().Select(property => KeyValuePair.Create(JsonStrings.GetName(property), JsonStrings.GetString(property.Value).Span.ToArray()));
        var fs = new LibraryFileSystem(new MemoryFileSystem(files, true));
        var cwd = JsonStrings.GetString(input.GetProperty("cwd"));
        var options = ApiJson.CompilerOptions(input.GetProperty("compilerOptions"));
        var roots = ApiJson.Strings(input, "rootFiles"u8);
        IFileSystem source = fs;
        bool mapped = input.TryGetProperty("projections", out var projections);
        var allProjections = input.TryGetProperty("mappedFiles", out var allMapped) ? allMapped : projections;
        if (allProjections.ValueKind == JsonValueKind.Array)
        {
            // Rebuild the captured virtual program, retaining its source names and script kinds.
            var documents = allProjections.EnumerateArray().Select(projection => new DocumentSnapshot(JsonStrings.GetString(projection.GetProperty("fileName")),
                JsonStrings.GetString(projection.GetProperty("text")), kind: (ScriptKind)projection.GetProperty("scriptKind").GetInt32())).ToArray();
            source = new SnapshotFileSystem(new OverlayFileSystem(fs, cwd, documents), cwd);
            using var enabled = JsonDocument.Parse("true"); options.Set("allowNonTsExtensions"u8, enabled.RootElement);
            roots = roots.Concat(documents.Select(document => document.FileName)).Distinct().ToArray();
        }
        await using var host = new ProjectSnapshotHost(source, new() { CurrentDirectory = cwd, DefaultLibraryDirectory = fs.LibraryDirectory });
        await using var snapshot = await host.CreateAsync(new()
        {
            CreatePrograms = [new(roots, options)],
        });
        var project = snapshot.CreatedPrograms[0];
        var file = project.Program!.GetFile(fileName) ?? throw new InvalidOperationException($"Missing semantic input {fileName}");
        var service = new LanguageServiceDocument(project.Program, file, input.GetProperty("encoding").GetString() == "utf-16" ? PositionEncoding.Utf16 : PositionEncoding.Utf8);
        if (allProjections.ValueKind == JsonValueKind.Array)
        {
            List<DocumentProjection> projected = [];
            Dictionary<SourceFileNode, DocumentProjection> related = [];
            foreach (var projection in allProjections.EnumerateArray())
            {
                var name = JsonStrings.GetString(projection.GetProperty("fileName"));
                var syntax = project.Program.GetFile(name)!.Syntax;
                var map = new SpanMap(projection.GetProperty("segments").EnumerateArray().Select(segment =>
                    new MappingSegment(segment[0].GetInt32(), segment[1].GetInt32(), segment[2].GetInt32(), segment[3].GetInt32(), (MappingKind)segment[4].GetInt32(), (MappingFeature)segment[5].GetInt32())));
                var item = new DocumentProjection(syntax, new(syntax, new(JsonStrings.GetString(projection.GetProperty("original"))), map, name, "test"u8, "test"u8, []),
                    input.GetProperty("encoding").GetString() == "utf-16" ? PositionEncoding.Utf16 : PositionEncoding.Utf8,
                    projection.TryGetProperty("originalName", out var originalName) ? JsonStrings.GetString(originalName) : fileName);
                related[syntax] = item;
            }
            if (mapped) foreach (var projection in projections.EnumerateArray()) projected.Add(related[project.Program.GetFile(JsonStrings.GetString(projection.GetProperty("fileName")))!.Syntax]);
            else projected.Add(new(file.Syntax, file.Mapping, input.GetProperty("encoding").GetString() == "utf-16" ? PositionEncoding.Utf16 : PositionEncoding.Utf8));
            service = new(projected.ToArray(), related);
        }
        if (await DefinitionsAsync(service, project, input, parameters)) return;
        if (input.GetProperty("method").GetString() == "textDocument/signatureHelp")
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var help = await service.GetSignatureHelpAsync(project, LspJson.Position(parameters.GetProperty("position")),
                LspJson.SignatureHelpOptions(parameters, input.GetProperty("capabilities")), deadline.Token);
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(LspJson.SignatureHelp(help).Data.Span));
            return;
        }
        if (input.GetProperty("method").GetString() == "textDocument/hover")
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var hover = await service.GetHoverAsync(project, LspJson.Position(parameters.GetProperty("position")),
                LspJson.HoverOptions(parameters, input.GetProperty("capabilities"), LspJson.Integer(input, "maximumHoverLength"u8)), deadline.Token);
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(LspJson.Hover(hover).Data.Span));
            return;
        }
        var capabilities = input.GetProperty("semanticCapabilities");
        var data = await service.GetSemanticTokensAsync(project, new(ApiJson.Strings(capabilities, "tokenTypes"u8), ApiJson.Strings(capabilities, "tokenModifiers"u8)),
            parameters.TryGetProperty("range", out var range) ? LspJson.Range(range) : null);
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(LspJson.SemanticTokenResult(data).Data.Span));
    }

    private static DocumentPosition HierarchyPosition(JsonElement parameters) => LspJson.Position(parameters.TryGetProperty("item", out var item)
        ? item.GetProperty("selectionRange").GetProperty("start") : parameters.GetProperty("position"));

    private static async Task ReferenceSessionAsync(JsonElement input, JsonElement parameters, Utf8String fileName)
    {
        var files = input.GetProperty("files").EnumerateObject().ToDictionary(JsonStrings.GetName, property => JsonStrings.GetString(property.Value));
        var fs = new LibraryFileSystem(LspTests.CreateFileSystem(input));
        var encoding = input.GetProperty("encoding").GetString() == "utf-16" ? PositionEncoding.Utf16 : PositionEncoding.Utf8;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = new ProjectSession(fs, new() { CurrentDirectory = JsonStrings.GetString(input.GetProperty("cwd")), DefaultLibraryDirectory = fs.LibraryDirectory, PositionEncoding = encoding });
        session.SetInferredOptions(ApiJson.CompilerOptions(input.TryGetProperty("inferredOptions", out var inferred) ? inferred : input.GetProperty("compilerOptions")));
        session.Configure(UserPreferences.ReadConfiguration(default).WithConfig(LspJson.Get(input, "preferences"u8)));
        foreach (var name in ApiJson.Strings(input, "openFiles"u8))
            if (files.TryGetValue(name, out var text)) session.Notify(new(FileChangeKind.Open, name, 1, text, DocumentSnapshot.InferKind(name)));
        await using var snapshot = await session.GetSnapshotAsync([fileName], deadline.Token);
        var position = input.GetProperty("method").GetString() is "textDocument/codeLens" or "codeLens/resolve" or "textDocument/codeAction" or "textDocument/diagnostic" or "textDocument/completion" or "completionItem/resolve" ? default : HierarchyPosition(parameters);
        bool links = LspJson.Boolean(LspJson.Get(LspJson.Get(input.GetProperty("capabilities"), "textDocument"u8), "implementation"u8), "linkSupport"u8);
        var service = new LanguageServiceDocument(snapshot.Snapshot.GetDefaultProject(fileName)!.Program!, snapshot.Snapshot.GetDefaultProject(fileName)!.Program!.GetFile(fileName)!, encoding);
        var method = input.GetProperty("method").GetString();
        if (method is "textDocument/completion" or "completionItem/resolve" or "textDocument/codeAction")
        { await DefinitionsAsync(service, snapshot.Snapshot.GetDefaultProject(fileName)!, input, parameters); return; }
        var result = method == "textDocument/diagnostic"
            ? LspJson.DocumentDiagnostics(await service.GetDiagnosticsAsync(snapshot.Snapshot.GetDefaultProject(fileName)!, snapshot.Snapshot.UserPreferences, LspJson.DiagnosticOptions(input.GetProperty("capabilities")), deadline.Token), LspJson.Boolean(input.GetProperty("capabilities"), "_vs_supportsVisualStudioExtensions"u8))
            : method == "textDocument/codeLens"
            ? LspJson.CodeLenses(await service.GetCodeLensesAsync(snapshot.Snapshot.GetDefaultProject(fileName)!, snapshot.Snapshot.UserPreferences.CodeLens, deadline.Token))
            : method == "codeLens/resolve"
            ? LspJson.CodeLens(await CrossProjectReferences.ResolveCodeLensAsync(session, snapshot.Snapshot, LspJson.ReadCodeLens(parameters),
                LspJson.Get(input, "codeLensShowLocationsCommandName"u8) is { ValueKind: JsonValueKind.String } command ? JsonStrings.GetString(command) : null, encoding, deadline.Token))
            : method == "callHierarchy/incomingCalls"
            ? LspJson.CallHierarchyCalls(await CrossProjectReferences.IncomingCallsAsync(session, snapshot.Snapshot, fileName, position, encoding, deadline.Token), true)
            : method == "callHierarchy/outgoingCalls"
            ? LspJson.CallHierarchyCalls(await service.GetOutgoingCallsAsync(snapshot.Snapshot.GetDefaultProject(fileName)!, position, deadline.Token), false)
            : method == "textDocument/prepareCallHierarchy"
            ? LspJson.CallHierarchyItems(await service.PrepareCallHierarchyAsync(snapshot.Snapshot.GetDefaultProject(fileName)!, position, deadline.Token))
            : method == "textDocument/rename"
            ? LspJson.WorkspaceEdit(await CrossProjectReferences.RenameAsync(session, snapshot.Snapshot, fileName, position, LspJson.String(parameters, "newName"u8), LspJson.RenameOptions(input.GetProperty("capabilities")), encoding, deadline.Token))
            : input.GetProperty("method").GetString() == "textDocument/prepareRename"
            ? LspJson.PrepareRename(await service.GetRenameInfoAsync(snapshot.Snapshot.GetDefaultProject(fileName)!, position, preferences: snapshot.Snapshot.UserPreferences, capabilities: LspJson.RenameOptions(input.GetProperty("capabilities")), cancellation: deadline.Token))
            : input.GetProperty("method").GetString() == "textDocument/_vs_references"
            ? LspJson.VisualStudioReferences(await CrossProjectReferences.VisualStudioReferencesAsync(session, snapshot.Snapshot, fileName, position,
                LspJson.Boolean(input.GetProperty("capabilities"), "_vs_supportsVisualStudioExtensions"u8), encoding, deadline.Token))
            : input.GetProperty("method").GetString() == "textDocument/references"
            ? LspJson.References(await CrossProjectReferences.ReferencesAsync(session, snapshot.Snapshot, fileName, position,
                LspJson.Boolean(parameters.GetProperty("context"), "includeDeclaration"u8), encoding, deadline.Token))
            : LspJson.Definitions(await CrossProjectReferences.ImplementationsAsync(session, snapshot.Snapshot, fileName, position, links, encoding, deadline.Token), links);
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(result.Data.Span));
    }

    private static async Task FileRenameAsync(JsonElement input, JsonElement parameters)
    {
        var encoding = input.GetProperty("encoding").GetString() == "utf-16" ? PositionEncoding.Utf16 : PositionEncoding.Utf8;
        var cwd = JsonStrings.GetString(input.GetProperty("cwd"));
        var preferences = UserPreferences.ReadConfiguration(default).WithConfig(input.GetProperty("preferences"));
        var capabilities = LspJson.RenameOptions(input.GetProperty("capabilities"));
        var files = parameters.GetProperty("files").EnumerateArray().Select(file => new RenameFileChange(LspJson.String(file, "oldUri"u8), LspJson.String(file, "newUri"u8))).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        WorkspaceEdit? edits;
        if (input.TryGetProperty("mappedFiles", out _))
        {
            var (fs, mapper) = CapturedMapper(input);
            await using var mapperHost = new ContentMapperHost();
            List<WorkspaceDocumentChange> changes = [];
            foreach (var captured in input.GetProperty("programs").EnumerateArray())
            {
                var id = LspJson.String(captured, "projectId"u8);
                var config = fs.FileExists(id) ? new ConfigParser(fs, cwd).Parse(id, cancellation: deadline.Token)
                    : new ParsedConfig(default, new(), [], [], [], []);
                var options = ApiJson.CompilerOptions(captured.GetProperty("compilerOptions"));
                using var enabled = JsonDocument.Parse("true"); options.Set("runExternalCode"u8, enabled.RootElement);
                config = config with { Options = options, FileNames = ApiJson.Strings(captured, "rootFiles"u8), ContentMappers = [mapper] };
                var mapping = await mapperHost.GetProjectAsync(config, deadline.Token);
                ProjectProgram? resource = null;
                try
                {
                    var program = await CompilerProgram.CreateAsync(fs, config.FileName.IsEmpty ? cwd : CompilerPath.DirectoryName(config.FileName), config,
                        useProjectReferenceSources: true, defaultLibraryDirectory: fs.LibraryDirectory, mapperProject: mapping, cancellation: deadline.Token);
                    resource = new(program, mapping, new());
                    var project = new ProjectSnapshot(id, config.FileName.IsEmpty ? ProjectKind.Inferred : ProjectKind.Configured, config, resource, dirty: false);
                    var service = new LanguageServiceDocument(program, program.SourceFiles[0], encoding);
                    foreach (var file in files)
                        changes.AddRange(await service.GetEditsForFileRenameAsync(project, DocumentUris.ToFileName(file.OldUri), DocumentUris.ToFileName(file.NewUri), preferences, deadline.Token));
                }
                finally { if (resource is not null) await resource.ReleaseAsync(); else await mapping.DisposeAsync(); }
            }
            edits = CrossProjectReferences.CombineFileRenames(changes, files, capabilities, false);
        }
        else
        {
            var fs = new LibraryFileSystem(LspTests.CreateFileSystem(input));
            await using var session = new ProjectSession(fs, new() { CurrentDirectory = cwd, DefaultLibraryDirectory = fs.LibraryDirectory, PositionEncoding = encoding });
            session.SetInferredOptions(ApiJson.CompilerOptions(LspJson.Get(input, "inferredOptions"u8)));
            session.Configure(preferences);
            foreach (var name in ApiJson.Strings(input, "openFiles"u8))
                session.Notify(new(FileChangeKind.Open, name, 1, JsonStrings.GetString(input.GetProperty("files").GetProperty(name)), DocumentSnapshot.InferKind(name)));
            edits = await CrossProjectReferences.RenameFilesAsync(session, files, capabilities, encoding, false, deadline.Token);
        }
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(LspJson.WorkspaceEdit(edits).Data.Span));
    }

    private static int SupplementalIndex(Utf8String name, Utf8String original)
    {
        var suffix = name[(original.Length + 1)..]; int dot = suffix.IndexOf("."u8);
        return int.Parse(suffix[..dot].Span, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (LibraryFileSystem FileSystem, ContentMapper Mapper) CapturedMapper(JsonElement input)
    {
        var files = input.GetProperty("files").EnumerateObject().ToDictionary(JsonStrings.GetName, property => JsonStrings.GetString(property.Value).Span.ToArray());
        var groups = input.GetProperty("mappedFiles").EnumerateArray().GroupBy(projection => JsonStrings.GetString(projection.GetProperty("originalName"))).ToArray();
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteStartObject("results");
            foreach (var group in groups)
            {
                var canonical = group.Single(projection => JsonStrings.GetString(projection.GetProperty("fileName")) == group.Key);
                files[group.Key] = JsonStrings.GetString(canonical.GetProperty("original")).Span.ToArray();
                writer.WritePropertyName(group.Key.Span); writer.WriteStartObject(); WriteProjection(canonical);
                writer.WriteStartArray("supplemental");
                foreach (var supplemental in group.Where(projection => JsonStrings.GetString(projection.GetProperty("fileName")) != group.Key)
                    .OrderBy(projection => SupplementalIndex(JsonStrings.GetString(projection.GetProperty("fileName")), group.Key)))
                {
                    files.Remove(JsonStrings.GetString(supplemental.GetProperty("fileName")));
                    writer.WriteStartObject(); WriteProjection(supplemental); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndObject(); writer.WriteEndObject();
            void WriteProjection(JsonElement projection)
            {
                writer.WritePropertyName("text"); JsonStrings.WriteString(writer, JsonStrings.GetString(projection.GetProperty("text")));
                if (LspJson.Boolean(projection, "transformFailed"u8)) writer.WriteBoolean("transformFailed", true);
                if (projection.TryGetProperty("externalDiagnostics", out var mapperDiagnostics)) { writer.WritePropertyName("diagnostics"); mapperDiagnostics.WriteTo(writer); }
                if (projection.TryGetProperty("diagnosticDirectives", out var mapperDirectives)) { writer.WritePropertyName("diagnosticDirectives"); mapperDirectives.WriteTo(writer); }
                writer.WriteString("extension", (ScriptKind)projection.GetProperty("scriptKind").GetInt32() switch
                { ScriptKind.JS => ".js", ScriptKind.JSX => ".jsx", ScriptKind.TSX => ".tsx", ScriptKind.JSON => ".json", _ => ".ts" });
                writer.WriteStartArray("mappings");
                foreach (var segment in projection.GetProperty("segments").EnumerateArray())
                {
                    writer.WriteStartArray(); writer.WriteNumberValue(segment[0].GetInt32()); writer.WriteNumberValue(segment[1].GetInt32() - segment[0].GetInt32());
                    writer.WriteNumberValue(segment[2].GetInt32()); writer.WriteNumberValue(segment[3].GetInt32() - segment[2].GetInt32());
                    writer.WriteNumberValue(segment[4].GetInt32()); writer.WriteNumberValue(segment[5].GetInt32()); writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
        }
        using var mapperOptions = JsonDocument.Parse(output.ToArray());
        using var empty = JsonDocument.Parse("[]");
        var repository = Utf8String.FromString(Path.GetFullPath("."));
        var identity = LspJson.String(groups[0].First(), "mapper"u8);
        int separator = identity.LastIndexOf((byte)'@');
        Utf8String mapperName = identity.IsEmpty ? "captured"u8 : separator > 0 ? identity[..separator] : identity;
        var mapperVersion = separator > 0 ? identity[(separator + 1)..] : Utf8String.Empty;
        var diagnosticSource = groups.SelectMany(group => group).Select(projection => LspJson.String(projection, "diagnosticSource"u8)).FirstOrDefault(value => !value.IsEmpty);
        if (diagnosticSource.IsEmpty) diagnosticSource = "fixture"u8;
        ContentMapper mapper = new(mapperName, groups.Select(group => Utf8String.FromString(Path.GetExtension(group.Key.ToString()))).Distinct().ToArray(),
            mapperOptions.RootElement.Clone(), repository, mapperName, mapperVersion,
            ["node"u8, Utf8String.FromString(Path.GetFullPath("csharp/tests/fixtures/mappers/mapper.mjs")), "utf-8"u8, diagnosticSource], empty.RootElement.Clone(), false);
        var fs = new LibraryFileSystem(LspTests.CreateFileSystem(input, files));
        return (fs, mapper);
    }

    private static async Task MappedHoverAsync(JsonElement input, JsonElement parameters, Utf8String fileName)
    {
        var (fs, mapper) = CapturedMapper(input);
        var groups = input.GetProperty("mappedFiles").EnumerateArray().GroupBy(projection => JsonStrings.GetString(projection.GetProperty("originalName"))).ToArray();
        var cwd = JsonStrings.GetString(input.GetProperty("cwd"));
        var options = ApiJson.CompilerOptions(input.GetProperty("compilerOptions"));
        using var enabled = JsonDocument.Parse("true"); options.Set("runExternalCode"u8, enabled.RootElement);
        await using var host = new ProjectSnapshotHost(fs, new() { CurrentDirectory = cwd, DefaultLibraryDirectory = fs.LibraryDirectory, RunExternalCode = true });
        await using var snapshot = await host.CreateAsync(new()
        { CreatePrograms = [new(ApiJson.Strings(input, "rootFiles"u8), options)], InferredContentMappers = [mapper] });
        var project = snapshot.CreatedPrograms[0];
        if (input.TryGetProperty("projectId", out var projectId))
            project = new ProjectSnapshot(JsonStrings.GetString(projectId), project.Kind, project.Configuration, project.Resource, project.ProgramLastUpdate, project.IsDirty);
        foreach (var group in groups)
        {
            if (project.Program!.GetFile(group.Key)?.Mapping is null)
                throw new InvalidOperationException($"Captured mapper did not transform {group.Key}: {string.Join("\n", project.Program.Diagnostics.Select(diagnostic => diagnostic.Format()))}");
            if (project.Program.GetFile(group.Key)!.SupplementalSourceFiles.Count != group.Count() - 1)
                throw new InvalidOperationException($"Captured mapper lost supplemental files for {group.Key}");
            foreach (var projection in group)
            {
                var name = JsonStrings.GetString(projection.GetProperty("fileName"));
                var actual = project.Program.GetFile(name);
                if (actual?.Syntax.Source.Text != JsonStrings.GetString(projection.GetProperty("text")) || actual.Mapping is null
                    || actual.Mapping.Original.Text != JsonStrings.GetString(projection.GetProperty("original")))
                    throw new InvalidOperationException($"Captured mapper source changed for {name}");
            }
        }
        var file = project.Program!.GetFile(fileName)!;
        var service = new LanguageServiceDocument(project.Program, file, input.GetProperty("encoding").GetString() == "utf-16" ? PositionEncoding.Utf16 : PositionEncoding.Utf8);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (await DefinitionsAsync(service, project, input, parameters)) return;
        if (input.GetProperty("method").GetString() == "textDocument/signatureHelp")
        {
            var help = await service.GetSignatureHelpAsync(project, LspJson.Position(parameters.GetProperty("position")),
                LspJson.SignatureHelpOptions(parameters, input.GetProperty("capabilities")), deadline.Token);
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(LspJson.SignatureHelp(help).Data.Span));
            return;
        }
        var hover = await service.GetHoverAsync(project, LspJson.Position(parameters.GetProperty("position")),
            LspJson.HoverOptions(parameters, input.GetProperty("capabilities"), LspJson.Integer(input, "maximumHoverLength"u8)), deadline.Token);
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(LspJson.Hover(hover).Data.Span));
    }

    private static void Range(Utf8JsonWriter writer, DocumentRange range)
    {
        writer.WriteStartObject(); writer.WriteStartObject("start"); writer.WriteNumber("line", range.Start.Line);
        writer.WriteNumber("character", range.Start.Character); writer.WriteEndObject(); writer.WriteStartObject("end");
        writer.WriteNumber("line", range.End.Line); writer.WriteNumber("character", range.End.Character); writer.WriteEndObject(); writer.WriteEndObject();
    }

    internal static async Task<int> SafetyAsync()
    {
        int assertions = 0;
        void Check(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
        const int nesting = 12000;
        Utf8String text = Utf8String.FromString("const x = " + new string('(', nesting) + "1" + new string(')', nesting) + ";");
        var file = await Parser.ParseSourceFileAsync(new("/index.ts"u8, ScriptKind.TS), new(text));
        var range = await SyntaxLanguageService.GetSelectionRangeAsync(file, 10 + nesting);
        int depth = 1;
        Check(range.Range == new DocumentRange(new(0, 10 + nesting), new(0, 11 + nesting)), "Innermost selection must retain the token");
        while (range.Parent is { } parent) { range = parent; depth++; }
        Check(depth == SyntaxLanguageService.MaximumSelectionDepth, "Deep selection must be bounded");
        Check(range.Range == new DocumentRange(new(0, 0), new(0, (uint)text.Length)), "Outermost selection must retain the file");
        file = await Parser.ParseSourceFileAsync(new("/mapped.ts"u8, ScriptKind.TS), new("type M = { -readonly [K in keyof any]-?: any };"u8));
        var nodes = file.DescendantsAndSelf().Select(node => (Node: node, node.Parent)).ToArray();
        for (int position = 0; position <= file.Source.Length; position++)
            await SyntaxLanguageService.GetSelectionRangeAsync(file, position);
        Check(nodes.All(pair => ReferenceEquals(pair.Node.Parent, pair.Parent)), "Selection grouping must not reparent shared source nodes");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await SyntaxLanguageService.GetSelectionRangeAsync(file, 1, cancellation: cancelled.Token); throw new InvalidOperationException("Expected cancellation"); }
        catch (OperationCanceledException) { assertions++; }
        const int symbolDepth = 2048;
        var nested = await Parser.ParseSourceFileAsync(new("/nested.ts"u8),
            new(Utf8String.FromString(string.Concat(Enumerable.Repeat("function f() {", symbolDepth)) + "const 名=1;" + new string('}', symbolDepth))));
        var original = nested.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        var symbols = await SyntaxLanguageService.GetDocumentSymbolsAsync(nested);
        int count = 0;
        for (var children = symbols; children.Length != 0; children = children[0].Children)
        { if (children.Length != 1) throw new InvalidOperationException("Unexpected nested outline"); count++; }
        Check(count == symbolDepth + 1, "Deep document symbols retain every declaration");
        using var flat = JsonDocument.Parse(LspJson.DocumentSymbols(symbols, false, "file:///nested.ts"u8).Data);
        Check(flat.RootElement.GetArrayLength() == symbolDepth + 1 && flat.RootElement[1].GetProperty("containerName").GetString() == "f", "Flat symbols retain immediate containers");
        using var hierarchical = JsonDocument.Parse(LspJson.DocumentSymbols(symbols, true, "file:///nested.ts"u8).Data, new() { MaxDepth = int.MaxValue });
        Check(hierarchical.RootElement.GetArrayLength() == 1, "Deep hierarchical symbols serialize without recursion");
        Check(original.All(entry => entry.Node.Parent == entry.Parent && entry.Node.Pos == entry.Pos && entry.Node.End == entry.End && entry.Node.Flags == entry.Flags), "Document symbols preserve the source tree");
        try { await SyntaxLanguageService.GetDocumentSymbolsAsync(file, cancellation: cancelled.Token); throw new InvalidOperationException("Expected symbol cancellation"); }
        catch (OperationCanceledException) { assertions++; }
        var binding = await Binder.BindAsync(nested);
        var nestedFile = new ProgramFile(nested, binding, new(nested.FileName), default, default, default, [], []);
        var searchFiles = new[] { (nested.FileName, nestedFile, nested.FileName) };
        var workspace = SyntaxLanguageService.WorkspaceSymbols(searchFiles, "名"u8, PositionEncoding.Utf8, default);
        Check(workspace.Length == 1 && workspace[0].ContainerName == "f"u8, "Deep workspace search retains names and containers");
        Check(SyntaxLanguageService.WorkspaceSymbols(searchFiles, ""u8, PositionEncoding.Utf8, default).Length == 256, "Workspace search enforces its result limit");
        Check(original.All(entry => entry.Node.Parent == entry.Parent && entry.Node.Pos == entry.Pos && entry.Node.End == entry.End && entry.Node.Flags == entry.Flags), "Workspace symbols preserve source positions and parents");
        try { SyntaxLanguageService.GetWorkspaceSymbols([], ""u8, cancellation: cancelled.Token); throw new InvalidOperationException("Expected workspace cancellation"); }
        catch (OperationCanceledException) { assertions++; }
        return assertions;
    }
}

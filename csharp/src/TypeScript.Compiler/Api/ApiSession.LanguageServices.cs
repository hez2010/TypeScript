using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compiler.Api;

public sealed partial class ApiSession
{
    private async ValueTask<RpcResponse?> LanguageServiceRequestAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation)
    {
        if (method != "getCompletionsAtPosition"u8 && method != "getImportAdderEdits"u8
            && method != "getReferencedSymbolsForNode"u8 && method != "getSignatureUsages"u8) return null;
        await using var pin = Pin(ApiJson.UInt64(parameters, "snapshot"u8));
        var data = pin.Data;
        var projectId = ApiJson.String(parameters, "project"u8);
        var program = data.Program(projectId);
        var project = data.Project(projectId);
        if (method == "getCompletionsAtPosition"u8 || method == "getImportAdderEdits"u8)
        {
            var document = ApiJson.Get(parameters, "file"u8);
            var file = program.GetFile(Document(document, program));
            if (file is null)
            {
                if (method == "getCompletionsAtPosition"u8) return RpcResponse.Null;
                throw new ApiException($"source file not found: {(document.ValueKind == JsonValueKind.Object ? ApiJson.String(document, "uri"u8) : ApiJson.String(document))}");
            }
            if (method == "getCompletionsAtPosition"u8)
            {
                Utf8String? trigger = ApiJson.Get(parameters, "triggerCharacter"u8).ValueKind == JsonValueKind.String
                    ? ApiJson.String(parameters, "triggerCharacter"u8) : null;
                CompletionList? list;
                try
                {
                    list = await new LanguageServiceDocument(program, file).GetCompletionsAtPositionAsync(project,
                        (int)Math.Min(ApiJson.UInt32(parameters, "position"u8), int.MaxValue), data.Snapshot.UserPreferences,
                        trigger, ApiJson.Boolean(parameters, "includeSymbol"u8), cancellation);
                }
                catch (AutoImportsRequiredException error) { throw new RpcException(-32603, Utf8String.FromString(error.Message), error); }
                if (list is null) return RpcResponse.Null;
                var entries = await ResponseArrayAsync(list.Items, async item =>
                {
                    var symbol = item.Symbol is null ? (RpcResponse?)null : await data.SymbolResponseAsync(item.Symbol, projectId, cancellation);
                    return RpcResponse.Json(writer =>
                    {
                        writer.WriteStartObject(); ApiJson.String(writer, "name"u8, item.Label);
                        if (item.Kind is { } kind && kind != 0) writer.WriteNumber("kind"u8, kind);
                        if (item.SortText is { } sort) ApiJson.String(writer, "sortText"u8, sort);
                        if (item.InsertText is { } insert) ApiJson.String(writer, "insertText"u8, insert);
                        if (item.FilterText is { } filter) ApiJson.String(writer, "filterText"u8, filter);
                        if (item.Detail is { } detail) ApiJson.String(writer, "detail"u8, detail);
                        if (item.LabelDetails is { } label)
                        {
                            writer.WriteStartObject("labelDetails"u8);
                            if (label.Detail is { } suffix) ApiJson.String(writer, "detail"u8, suffix);
                            if (label.Description is { } description) ApiJson.String(writer, "description"u8, description);
                            writer.WriteEndObject();
                        }
                        if (symbol is { } response) { writer.WritePropertyName("symbol"u8); writer.WriteRawValue(response.Data.Span); }
                        writer.WriteEndObject();
                    });
                });
                return RpcResponse.Json(writer =>
                {
                    writer.WriteStartObject(); writer.WriteBoolean("isIncomplete"u8, list.IsIncomplete);
                    writer.WritePropertyName("entries"u8); writer.WriteRawValue(entries.Data.Span); writer.WriteEndObject();
                });
            }
            using var request = new ProjectRequest(cancellation);
            using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request, file.Syntax);
            var projection = new DocumentProjection(file.Syntax, file.Mapping, PositionEncoding.Utf8);
            var adder = new AutoImportView(program, lease.Checker, projection, data.Snapshot.UserPreferences, cancellation, cache: project.AutoImports).CreateAdder();
            int index = 0;
            foreach (var action in ApiJson.Array(parameters, "actions"u8))
            {
                cancellation.ThrowIfCancellationRequested();
                var kind = ApiJson.String(action, "kind"u8);
                if (kind != "importSymbol"u8) throw new ApiException($"unknown import adder action kind \"{kind}\"");
                ulong id = ApiJson.UInt64(action, "symbol"u8);
                if (id == 0) throw new ApiException($"import adder action {index} missing symbol");
                var symbol = data.Symbol(id).Symbol;
                // Auto-import preparation selects the file's editor project; synthetic programs have no registry bucket.
                if (project.Kind != ProjectKind.Synthetic)
                    await adder.AddExportAsync(symbol, ApiJson.Get(action, "isValidTypeOnlyUseSite"u8).ValueKind != JsonValueKind.False);
                index++;
            }
            var edits = adder.HasFixes ? await adder.EditsAsync() : [];
            return RpcResponse.Json(writer =>
            {
                writer.WriteStartArray();
                foreach (var edit in edits)
                {
                    var (start, end) = projection.OriginalRange(edit.Range);
                    writer.WriteStartObject(); writer.WriteNumber("pos"u8, start); writer.WriteNumber("end"u8, end);
                    ApiJson.String(writer, "newText"u8, edit.NewText); writer.WriteEndObject();
                }
                writer.WriteEndArray();
            });
        }
        bool usages = method == "getSignatureUsages"u8;
        var node = await data.ResolveNodeAsync(program, ApiJson.String(parameters, usages ? "signatureDecl"u8 : "node"u8), cancellation);
        if (usages && node.DeclarationName is not IdentifierNode) return RpcResponse.Null;
        if (usages) node = node.DeclarationName!;
        using var referenceRequest = new ProjectRequest(cancellation);
        using var referenceLease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, referenceRequest);
        var groups = await referenceLease.Checker.GetReferenceGroupsAsync(node, usages ? node.Pos : ApiJson.Int32(parameters, "position"u8),
            program.SourceFiles.Select(file => file.Syntax).ToArray(), new(ReferenceUse.References), cancellation);
        if (usages)
        {
            var names = groups.SelectMany(group => group.Symbol?.Declarations ?? []).Select(declaration => declaration.DeclarationName).ToHashSet();
            var references = groups.SelectMany(group => group.Entries).Where(entry => entry.Node is not null && !names.Contains(entry.Node)).ToArray();
            if (references.Length == 0) return RpcResponse.Null;
            return await ResponseArrayAsync(references, async entry =>
            {
                var reference = entry.Node!;
                var called = reference.Parent is PropertyAccessExpressionNode access && access.Name == reference ? access : reference;
                var name = await data.NodeHandleAsync(reference, cancellation);
                var call = called.Parent is CallExpressionNode invocation && invocation.Expression == called ? await data.NodeHandleAsync(invocation, cancellation) : default;
                return RpcResponse.Json(writer =>
                {
                    writer.WriteStartObject(); ApiJson.String(writer, "name"u8, name);
                    if (!call.IsEmpty) ApiJson.String(writer, "call"u8, call); writer.WriteEndObject();
                });
            });
        }
        var definitions = groups.Where(group => group.Node is not null || group.Symbol?.Declarations.Count > 0).ToArray();
        if (definitions.Length == 0) return RpcResponse.Null;
        return await ResponseArrayAsync(definitions, async group =>
        {
            var definition = await data.NodeHandleAsync(group.Node ?? group.Symbol!.Declarations[0], cancellation);
            var references = new List<Utf8String>();
            foreach (var entry in group.Entries) if (entry.Node is { } reference) references.Add(await data.NodeHandleAsync(reference, cancellation));
            var symbol = group.Symbol is null ? (RpcResponse?)null : await data.SymbolResponseAsync(group.Symbol, projectId, cancellation);
            return RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); ApiJson.String(writer, "definition"u8, definition);
                if (symbol is { } response) { writer.WritePropertyName("symbol"u8); writer.WriteRawValue(response.Data.Span); }
                ApiJson.Strings(writer, "references"u8, references); writer.WriteEndObject();
            });
        });
    }
}

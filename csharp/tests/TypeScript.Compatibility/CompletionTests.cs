using System.Runtime.CompilerServices;
using System.Text.Json;
using TypeScript.Compiler;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compatibility;

internal static partial class CompletionTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        const int depth = 2048;
        var text = Utf8String.FromString("const 字='😀';\r\n/** */\r\nfunction 方法($名前:string) {" + new string('{', depth) + "return $名前;" + new string('}', depth) + "}");
        var file = await Parser.ParseSourceFileAsync(new("/deep.ts"u8), new(text));
        var original = file.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        foreach (var encoding in new[] { PositionEncoding.Utf8, PositionEncoding.Utf16 })
        {
            var service = new LanguageServiceDocument(file, encoding);
            var position = new DocumentPosition(1, 3);
            var result = await service.GetJSDocTemplateCompletionsAsync(position, new(true, true, true), cancellation: deadline.Token);
            Check(result is { Items: [{ TextEdit.NewText.Span: var snippet }] } && snippet.SequenceEqual("/**\n * $0\n * @param \\$名前 ${1}\n * @returns ${2}\n */"u8), "Deep return traversal and snippet escaping preserve Unicode parameter names");
            Check(result!.Items[0].Data?.Position == text.IndexOf("/**"u8) + 3, "Completion identity retains the virtual byte position");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.GetJSDocTemplateCompletionsAsync(position, new(true, true, true), cancellation: deadline.Token).AsTask()));
            Check(concurrent.All(item => item!.Items[0].TextEdit == result.Items[0].TextEdit), "Concurrent completion queries share no mutable output");
            try { await service.GetJSDocTemplateCompletionsAsync(position, cancellation: canceled.Token); Check(false, "Canceled completion must throw"); }
            catch (OperationCanceledException) { checks++; }
            Check(await service.GetJSDocTemplateCompletionsAsync(position, preferences: new() { EnableJSDocCompletions = false }) is null, "Disabled JSDoc templates are not offered");
            Check((await service.GetJSDocTemplateCompletionsAsync(position, preferences: new() { GenerateReturnInDocTemplate = false }))!.Items[0].TextEdit!.NewText.IndexOf("@returns"u8) < 0, "Return template preference controls the return tag");
            foreach (var kind in new[] { MappingKind.Verbatim, MappingKind.Atom, MappingKind.Alias })
            {
                var mapping = new MappedSourceFile(file, new(text), new([new(0, text.Length, 0, text.Length, kind)]), file.FileName, "test"u8, "test"u8, []);
                Check((await new LanguageServiceDocument([new(file, mapping, encoding)]).GetJSDocTemplateCompletionsAsync(position) is not null) == (kind == MappingKind.Verbatim), "Completion edits require exact cursor mapping");
            }
            var disabled = new MappedSourceFile(file, new(text), new([new(0, text.Length, 0, text.Length, MappingKind.Verbatim, MappingFeature.All & ~MappingFeature.Completion)]), file.FileName, "test"u8, "test"u8, []);
            Check(await new LanguageServiceDocument([new(file, disabled, encoding)]).GetJSDocTemplateCompletionsAsync(position) is null, "Feature-disabled mappings suppress completion");
            var projected = await new LanguageServiceDocument([new(file, disabled, encoding), new(file, null, encoding)]).GetJSDocTemplateCompletionsAsync(position);
            Check(projected!.Items[0].Data?.SupplementalFileIndex == 0, "The first enabled supplemental projection retains its resolve identity");
            Check((await new LanguageServiceDocument([new(file, null, encoding), new(file, null, encoding)]).GetJSDocTemplateCompletionsAsync(position))!.Items[0].Data?.SupplementalFileIndex is null, "Completion uses the first exact projection when several projections match");
        }
        Check(original.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags), "Completion preserves the shared source tree");
        using var legacy = JsonDocument.Parse("{\"suggest\":{\"completeJSDocs\":false}}");
        using var stable = JsonDocument.Parse("{\"unstable\":{\"completeJSDocs\":false,\"generateReturnInDocTemplate\":false},\"suggest\":{\"completeJSDocs\":false,\"jsdoc\":{\"enabled\":true,\"generateReturns\":true}}}");
        Check(new UserPreferences().WithConfig(legacy.RootElement).EnableJSDocCompletions == false, "Legacy JSDoc configuration remains supported");
        Check(new UserPreferences().WithConfig(stable.RootElement) is { EnableJSDocCompletions: true, GenerateReturnInDocTemplate: true }, "Stable JSDoc configuration overrides unstable and fallback settings");
        var (released, retained) = await ReleasedAsync();
        for (int i = 0; i < 3 && released.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!released.IsAlive, "Returned completion data does not retain its disposed project"); GC.KeepAlive(retained);
        return checks + await TagSafetyAsync() + await SemanticSafetyAsync() + await AutoImportSafetyAsync() + await SpecifierRegexTests.SafetyAsync();
    }

    private static async Task<int> SemanticSafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Utf8String text = "const emoji='😀';\nimport {  } from \"./m\";\ninterface Shape { 値: number; optional?: string; $方法($引数:string):void; }\nconst shape: Shape = {  };\nclass C implements Shape { public  }\nconst mode: \"開\" | \"閉\" = \"\";\nshape."u8;
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/a.ts"u8] = text.Span.ToArray(), ["/m.ts"u8] = "export const exported=1;"u8.ToArray(),
        }, true);
        await using var session = new ProjectSession(fs, new() { MaxCheckers = 1 });
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8); session.SetInferredOptions(options);
        session.Notify(new(FileChangeKind.Open, "/a.ts"u8, 1, text));
        await using var snapshot = await session.GetSnapshotAsync(["/a.ts"u8], deadline.Token);
        var project = snapshot.Snapshot.GetDefaultProject("/a.ts"u8)!;
        var file = project.Program!.GetFile("/a.ts"u8)!;
        var original = file.Syntax.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        int[] offsets = [text.IndexOf("{  }"u8) + 1, text.LastIndexOf("{  }"u8) + 1, text.IndexOf("\"\";"u8) + 1, text.Length];
        foreach (var encoding in new[] { PositionEncoding.Utf8, PositionEncoding.Utf16 })
        {
            var projection = new DocumentProjection(file.Syntax, null, encoding);
            var service = new LanguageServiceDocument(project.Program, file, encoding);
            var points = offsets.Select(offset => projection.ToRange(offset, offset).Range.Start).ToArray();
            var results = await Task.WhenAll(points.Select(point => service.GetCompletionsAsync(project, point, cancellation: deadline.Token).AsTask()));
            Check(results[0]!.Items.Any(item => item.Label == "exported"u8), "Module completion does not reacquire the checker query gate");
            Check(results[1]!.Items.Any(item => item.Label == "値"u8) && results[1]!.Items.Any(item => item.Label == "optional?"u8), "Contextual object completion releases its checker query gate");
            Check(results[2]!.Items.Select(item => item.Label).ToHashSet().SetEquals(new Utf8String[] { "開"u8, "閉"u8 }), "Contextual string completions retain Unicode values");
            var snippetPreferences = new UserPreferences { IncludeCompletionsWithObjectLiteralMethodSnippets = true };
            var snippets = await service.GetCompletionsAsync(project, points[1], new(Snippets: true, LabelDetails: true), snippetPreferences, cancellation: deadline.Token);
            var method = snippets!.Items.Single(item => item.Data?.Source == "ObjectLiteralMethodSnippet/"u8);
            Check(method.InsertText == "\\$方法(\\$引数) {\n    $0\n},"u8 && method.LabelDetails?.Detail == "($引数)"u8, "Snippet printing escapes source dollars and preserves the body tab stop");
            var concurrentSnippets = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.GetCompletionsAsync(project, points[1], new(Snippets: true, LabelDetails: true), snippetPreferences, cancellation: deadline.Token).AsTask()));
            Check(concurrentSnippets.All(list => list!.Items.Single(item => item.Data?.Source == "ObjectLiteralMethodSnippet/"u8).InsertText == method.InsertText), "Snippet type construction and printing release a single checker between concurrent queries");
            var resolvedMethod = await service.ResolveCompletionItemAsync(project, method, new(Snippets: true, LabelDetails: true), snippetPreferences, deadline.Token);
            Check(resolvedMethod.Detail is { } methodDetail && methodDetail.Contains("$方法"u8) && resolvedMethod.Data == method.Data, "Method snippet resolution preserves identity and semantic details");
            int classOffset = text.IndexOf("public  }"u8) + 7;
            var classPoint = projection.ToRange(classOffset, classOffset).Range.Start;
            var classPreferences = new UserPreferences { IncludeCompletionsWithClassMemberSnippets = true };
            var classSnippets = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.GetCompletionsAsync(project, classPoint, new(Snippets: true), classPreferences, cancellation: deadline.Token).AsTask()));
            Check(classSnippets.All(list => list!.Items.Single(item => item.Label == "$方法"u8).InsertText == "public \\$方法(\\$引数: string): void {\n    $0\n}"u8), "Class snippets preserve type annotations and body tab stops with concurrent checker leases");
            var classMethod = classSnippets[0]!.Items.Single(item => item.Label == "$方法"u8);
            Check(classMethod.AdditionalTextEdits is [{ NewText.IsEmpty: true }] && classMethod.Data?.Source == "ClassMemberSnippet/"u8, "Class snippets map erased modifiers and retain their resolve source");
            var resolvedClassMethod = await service.ResolveCompletionItemAsync(project, classMethod, new(Snippets: true), classPreferences, deadline.Token);
            Check(resolvedClassMethod.Detail is { } classDetail && classDetail.Contains("$方法"u8) && resolvedClassMethod.Data == classMethod.Data, "Class snippet resolution preserves semantic details and identity");
            var member = results[3]!.Items.Single(item => item.Label == "値"u8);
            var resolved = await service.ResolveCompletionItemAsync(project, member, cancellation: deadline.Token);
            Check(resolved.Detail is { } detail && detail.Contains("number"u8) && resolved.Data == member.Data, "Member resolve retains typed details and byte identity");
            var repeated = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => service.GetCompletionsAsync(project, points[index % points.Length], cancellation: deadline.Token).AsTask()));
            Check(repeated.All(result => result is { Items.Length: > 0 }), "Concurrent semantic completion contexts release a single available checker");
            try { await service.GetCompletionsAsync(project, points[1], cancellation: canceled.Token); Check(false, "Canceled semantic completion must throw"); }
            catch (OperationCanceledException) { checks++; }
            Check((await service.GetCompletionsAsync(project, points[0], cancellation: deadline.Token))!.Items.Any(item => item.Label == "exported"u8), "Cancellation does not strand the next completion query");
        }
        Check(original.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags), "Semantic completions preserve shared source trees");
        return checks;
    }

    private static async Task<int> TagSafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Utf8String text = "const 字='😀';\r\n/**\r\n * @\r\n */\r\nexport function 方法(名前:string,数=3) {}"u8;
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = text.Span.ToArray() }, true);
        await using var session = new ProjectSession(fs, new() { MaxCheckers = 1 });
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8); session.SetInferredOptions(options);
        session.Notify(new(FileChangeKind.Open, "/a.ts"u8, 1, text));
        await using var snapshot = await session.GetSnapshotAsync(["/a.ts"u8], deadline.Token);
        var project = snapshot.Snapshot.GetDefaultProject("/a.ts"u8)!;
        var file = project.Program!.GetFile("/a.ts"u8)!;
        var original = file.Syntax.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        foreach (var encoding in new[] { PositionEncoding.Utf8, PositionEncoding.Utf16 })
        {
            var service = new LanguageServiceDocument(project.Program, file, encoding);
            var caps = new CompletionCapabilities(CommitCharacters: true, DefaultCommitCharacters: true);
            var result = await service.GetCompletionsAsync(project, new(2, 4), caps, cancellation: deadline.Token);
            Check(result is { Items.Length: 86, ItemDefaults.CommitCharacters.Length: 3 }, "Tags share completion-list commit characters");
            Check(result!.Items[^1].Label == "param [数=3] "u8 && result.Items[^2].Label == "param 名前 "u8, "Typed parameter annotations preserve Unicode and default expressions");
            Check(result.Items.All(item => item.Data is { SupplementalFileIndex: null } && item.Data.Position == text.IndexOf("@"u8) + 1 && item.CommitCharacters is null), "Tag identity uses byte offsets independently of negotiated coordinates");
            var resolved = await service.ResolveCompletionItemAsync(project, result.Items[^1], caps, cancellation: deadline.Token);
            Check(resolved.Detail == resolved.Label && resolved.Data == result.Items[^1].Data, "Resolve returns annotation details with stable source identity");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.ResolveCompletionItemAsync(project, result.Items[0], caps, cancellation: deadline.Token).AsTask()));
            Check(concurrent.All(item => item.Detail == "abstract"u8), "Concurrent resolutions serialize through one checker without retaining a lease");
            try { await service.ResolveCompletionItemAsync(project, result.Items[0], caps, cancellation: canceled.Token); Check(false, "Canceled resolve must throw"); }
            catch (OperationCanceledException) { checks++; }
            try { await service.ResolveCompletionItemAsync(project, result.Items[0] with { Data = result.Items[0].Data! with { SupplementalFileIndex = -1 } }, cancellation: deadline.Token); Check(false, "Invalid supplemental identity must fail"); }
            catch (ArgumentException) { checks++; }
            Check((await service.ResolveCompletionItemAsync(project, result.Items[0] with { Detail = "retained"u8 }, cancellation: deadline.Token)).Detail == "retained"u8, "Resolve preserves a client's existing detail");
        }
        Check(original.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags), "Tag completion and resolve preserve the program tree");
        return checks;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference, CompletionList)> ReleasedAsync()
    {
        Utf8String text = "/**\n * @\n */\nexport function f(値:number) {return 値;}"u8;
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = text.Span.ToArray() }, true);
        await using var session = new ProjectSession(fs);
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8); session.SetInferredOptions(options);
        session.Notify(new(FileChangeKind.Open, "/a.ts"u8, 1, text));
        await using var snapshot = await session.GetSnapshotAsync(["/a.ts"u8]);
        var project = snapshot.Snapshot.GetDefaultProject("/a.ts"u8)!;
        var service = new LanguageServiceDocument(project.Program!, project.Program!.GetFile("/a.ts"u8)!);
        var list = await service.GetCompletionsAsync(project, new(1, 4));
        list!.Items[0] = await service.ResolveCompletionItemAsync(project, list.Items[0]);
        return (new(project.Program), list ?? throw new InvalidOperationException("Retention fixture must produce completions"));
    }

    internal static async Task<int> LspSafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var encoding in new[] { "utf-8", "utf-16" })
        {
            var (serverStream, clientStream) = RpcTests.DuplexStream.Pair();
            var running = LanguageServer.RunAsync(new MemoryFileSystem([], true), serverStream, serverStream, new() { CurrentDirectory = "/"u8 }, deadline.Token);
            await using var peer = new RpcConnection(clientStream, clientStream, new Client());
            var clientRun = peer.RunAsync(deadline.Token);
            async Task<JsonElement> Request(Utf8String method, string parameters)
            {
                using var json = JsonDocument.Parse(await peer.CallAsync(method, System.Text.Encoding.UTF8.GetBytes(parameters), deadline.Token));
                return json.RootElement.Clone();
            }
            await Request("initialize"u8, "{\"processId\":null,\"rootUri\":\"file:///\",\"locale\":\"ja\",\"capabilities\":{\"general\":{\"positionEncodings\":[\"" + encoding + "\"]},\"textDocument\":{\"completion\":{\"completionItem\":{\"snippetSupport\":true,\"insertReplaceSupport\":true,\"commitCharactersSupport\":true}}}}}");
            await peer.NotifyAsync("initialized"u8, "{}"u8.ToArray(), deadline.Token);
            await peer.NotifyAsync("textDocument/didOpen"u8, "{\"textDocument\":{\"uri\":\"file:///a.ts\",\"languageId\":\"typescript\",\"version\":1,\"text\":\"/** */\\nfunction 方法(値:number){return 値;}\"}}"u8.ToArray(), deadline.Token);
            const string query = "{\"textDocument\":{\"uri\":\"file:///a.ts\"},\"position\":{\"line\":0,\"character\":3},\"context\":{\"triggerKind\":2,\"triggerCharacter\":\"*\"}}";
            var item = (await Request("textDocument/completion"u8, query)).GetProperty("items")[0];
            Check(item.GetProperty("detail").GetString() == TypeScript.Compiler.Diagnostics.Messages.JSDoc_comment.Format("ja"u8).ToString(), "Completion details use the editor locale");
            Check(item.GetProperty("textEdit").GetProperty("newText").GetString() == "/**\n * $0\n * @param 値 ${1}\n * @returns ${2}\n */", "LSP template snippets preserve Unicode parameters and returns");
            await peer.NotifyAsync("workspace/didChangeConfiguration"u8, "{\"settings\":{\"js/ts\":{\"suggest\":{\"jsdoc\":{\"enabled\":false}}}}}"u8.ToArray(), deadline.Token);
            Check((await Request("textDocument/completion"u8, query)).GetProperty("items").EnumerateArray().All(item => item.GetProperty("label").GetString() != "/** */"), "Configuration changes suppress JSDoc templates");
            await peer.NotifyAsync("workspace/didChangeConfiguration"u8, "{\"settings\":{\"js/ts\":{\"suggest\":{\"jsdoc\":{\"enabled\":true,\"generateReturns\":false}}}}}"u8.ToArray(), deadline.Token);
            Check(!(await Request("textDocument/completion"u8, query)).GetProperty("items")[0].GetProperty("textEdit").GetProperty("newText").GetString()!.Contains("@returns", StringComparison.Ordinal), "Configuration changes update template generation");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Request("textDocument/completion"u8, query)));
            Check(concurrent.All(value => value.GetProperty("items").GetArrayLength() == 1), "Concurrent LSP completions complete independently");
            await peer.NotifyAsync("textDocument/didChange"u8, "{\"textDocument\":{\"uri\":\"file:///a.ts\",\"version\":2},\"contentChanges\":[{\"text\":\"/**\\n * @\\n */\\nfunction 方法(値:number){}\"}]}"u8.ToArray(), deadline.Token);
            var tags = await Request("textDocument/completion"u8, "{\"textDocument\":{\"uri\":\"file:///a.ts\"},\"position\":{\"line\":1,\"character\":4}}");
            var tag = tags.GetProperty("items")[0];
            Check(tags.GetProperty("items").GetArrayLength() == 85 && tag.GetProperty("commitCharacters").GetArrayLength() == 3, "Document changes refresh tag completions and per-item defaults");
            var resolved = await Request("completionItem/resolve"u8, tag.GetRawText());
            Check(resolved.GetProperty("detail").GetString() == "abstract" && resolved.GetProperty("data").GetProperty("position").GetInt32() == 8, "LSP resolve uses recorded byte positions");
            var resolutions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Request("completionItem/resolve"u8, tag.GetRawText())));
            Check(resolutions.All(value => value.GetProperty("detail").GetString() == "abstract"), "Concurrent LSP resolutions preserve item identity");
            try { await Request("completionItem/resolve"u8, "{\"label\":\"abstract\"}"); Check(false, "Missing completion identity must fail"); }
            catch (IOException error) when (error.InnerException is RpcException rpc)
            { Check(rpc.Code == -32603, "Missing completion identity returns the reference protocol error"); }
            await Request("shutdown"u8, ""); await peer.NotifyAsync("exit"u8, ReadOnlyMemory<byte>.Empty, deadline.Token);
            await running.WaitAsync(deadline.Token); peer.Stop(); await clientRun.WaitAsync(deadline.Token);
            Check(true, "Shutdown releases completion session state");
        }
        return checks;
    }

    private sealed class Client : IRpcHandler
    {
        public ValueTask<RpcResponse> HandleRequestAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation) => new(RpcResponse.Null);
        public ValueTask HandleNotificationAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation) => ValueTask.CompletedTask;
    }
}

using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class CheckerNodeBuilderTests
{
    internal sealed class Tracker : INodeBuilderSymbolTracker
    {
        internal List<string> Events { get; } = [];
        internal Func<Symbol, bool>? OnTrack { get; init; }

        public bool TrackSymbol(Symbol symbol, SyntaxNode? enclosingDeclaration, SymbolFlags meaning)
        {
            // The Go JSON encoder replaces its invalid UTF-8 internal-name prefix.
            string name = SemanticSyntax.Name(symbol.ValueDeclaration) is PrivateIdentifierNode privateName ? privateName.Text
                : symbol.Name.Replace(Symbol.InternalPrefix, "\uFFFD", StringComparison.Ordinal);
            Events.Add($"symbol:{name}:{enclosingDeclaration?.Pos ?? -1}:{(uint)meaning}");
            return OnTrack?.Invoke(symbol) ?? false;
        }

        public void ReportInaccessibleThisError() => Events.Add("this");

        public void ReportPrivateInBaseOfClassExpression(string propertyName) => Events.Add("private:" + propertyName);

        public void ReportInaccessibleUniqueSymbolError() => Events.Add("unique");

        public void ReportCyclicStructureError() => Events.Add("cycle");

        public void ReportLikelyUnsafeImportRequiredError(string specifier, string symbolName) =>
            Events.Add($"unsafe:{specifier}:{symbolName}");

        public void ReportTruncationError() => Events.Add("truncation");

        public void ReportNonlocalAugmentation(SourceFileNode containingFile, Symbol parentSymbol, Symbol augmentingSymbol)
                    => Events.Add($"augmentation:{containingFile.FileName}:{parentSymbol.Name}:{augmentingSymbol.Name}");

        public void ReportNonSerializableProperty(string propertyName) => Events.Add("property:" + propertyName);

        public void ReportInferenceFallback(SyntaxNode node) => Events.Add($"inference:{node.Pos}:{(int)node.Kind}");

        public void PushErrorFallbackNode(SyntaxNode node) => Events.Add($"push:{node.Pos}:{(int)node.Kind}");

        public void PopErrorFallbackNode() => Events.Add("pop");
    }

    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Node builder tracking assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        var files = new Dictionary<string, byte[]>
        {
            ["/project/main.ts"] = Wtf8.Encode(
                "interface Object{}interface Function{}interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}interface A{value:string}declare let show:{a:A;f:<T extends A>(x:T)=>T;雪:'雪'};declare let empty:[];"),
            ["/project/long.ts"] = Wtf8.Encode(
                "declare let long:{value:'" + new string(
                    'x',
                    1_000_010) + "';a:number;b:number;c:number;d:number;e:number;f:number;g:number}")
        };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project",
            new("/project/tsconfig.json", options, files.Keys.ToArray(), [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts")!.Syntax;
        var declarations = source.DescendantsAndSelf().OfType<VariableDeclarationNode>().ToArray();
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var type = await checker.GetTypeFromTypeNodeAsync(declarations[0].Type!);
        const NodeBuilderFlags flags = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation;
        string first = await checker.SerializeTypeSyntaxAsync(type, source, flags);
        Check(checker.SerializedTypeSyntaxCount > 0);
        int count = checker.SerializedTypeSyntaxCount;
        var tracker = new Tracker();
        Check(await checker.SerializeTypeSyntaxAsync(type, source, flags, tracker: tracker) == first);
        Check(tracker.Events.Any(e => e.StartsWith("symbol:A:", StringComparison.Ordinal)));
        Check(tracker.Events.All(e => !e.StartsWith("symbol:T:", StringComparison.Ordinal)));
        Check(checker.SerializedTypeSyntaxCount == count);
        try
        {
            await checker.SerializeTypeSyntaxAsync(type, source, flags, tracker: new Tracker
            { OnTrack = _ => throw new InvalidOperationException("callback") });
            throw new InvalidOperationException("Missing callback failure");
        }
        catch (InvalidOperationException error) when (error.Message == "callback") { }
        Check(checker.SerializedTypeSyntaxCount == count && checker.TypeSyntaxScopeCount == 0);
        Check(await checker.SerializeTypeSyntaxAsync(type, source, flags) == first);
        var second = await program.CreateCheckerAsync();
        var secondType = await second.GetTypeFromTypeNodeAsync(declarations[0].Type!);
        try
        {
            await second.SerializeTypeSyntaxAsync(secondType, source, flags, tracker: new Tracker
            { OnTrack = _ => throw new InvalidOperationException("callback") });
            throw new InvalidOperationException("Missing callback failure");
        }
        catch (InvalidOperationException error) when (error.Message == "callback") { }
        Check(second.SerializedTypeSyntaxCount == 0 && second.TypeSyntaxScopeCount == 0);
        using var stop = new CancellationTokenSource();
        try
        {
            await second.SerializeTypeSyntaxAsync(secondType, source, flags, stop.Token, new Tracker
            {
                OnTrack = _ =>
            {
                stop.Cancel();
                stop.Token.ThrowIfCancellationRequested();
                return false;
            }
            });
            throw new InvalidOperationException("Missing cancellation");
        }
        catch (OperationCanceledException) { }
        Check(second.SerializedTypeSyntaxCount == 0 && second.TypeSyntaxScopeCount == 0);
        Check(await second.SerializeTypeSyntaxAsync(secondType, source, flags, tracker: new Tracker { OnTrack = _ => true }) == first);
        Check(second.SerializedTypeSyntaxCount == 0);
        Check(await second.SerializeTypeSyntaxAsync(secondType, source, flags) == first);
        Check(second.SerializedTypeSyntaxCount > 0);
        var empty = await checker.GetTypeFromTypeNodeAsync(declarations[1].Type!);
        Check(await checker.SerializeTypeSyntaxAsync(empty, source, NodeBuilderFlags.None) == "");
        Check(await checker.SerializeTypeSyntaxAsync(empty, source, NodeBuilderFlags.AllowEmptyTuple) == "[]");
        var longFile = program.GetFile("/project/long.ts")!.Syntax;
        var longType = await checker.GetTypeFromTypeNodeAsync(
            longFile.DescendantsAndSelf().OfType<VariableDeclarationNode>().Single().Type!);
        var truncation = new Tracker();
        await checker.SerializeTypeSyntaxAsync(longType, longFile, flags, tracker: truncation);
        Check(truncation.Events.Count(e => e == "truncation") == 1);
        Check(snapshot.All(n => n.Node.Parent == n.Parent && n.Node.Pos == n.Pos && n.Node.End == n.End && n.Node.Flags == n.Flags));
        return checks;
    }

    internal static async Task WriteAsync(Checker checker, SourceFileNode file, NodeBuilderFlags[] flags, Utf8JsonWriter writer,
        NodeBuilderInternalFlags[]? internalFlags = null)
    {
        bool includeInternal = internalFlags is { Length: > 0 };
        if (!includeInternal)
            internalFlags = [NodeBuilderInternalFlags.None];
        writer.WriteStartObject();
        writer.WriteStartArray("tracking");
        foreach (var declaration in file.DescendantsAndSelf().OfType<VariableDeclarationNode>())
        {
            if (declaration.Name is not IdentifierNode name || !name.Text.StartsWith("show", StringComparison.Ordinal))
                continue;
            var type = declaration.Type is { } annotation ? await checker.GetTypeFromTypeNodeAsync(annotation)
                : await checker.Values.GetAsync(checker.Symbols.Declaration(declaration)!);
            SyntaxNode?[] scopes = [null, file, declaration];
            for (int scope = 0; scope < scopes.Length; scope++)
                foreach (var flag in flags)
                    foreach (var internalFlag in internalFlags!)
                        foreach (string operation in includeInternal && declaration.Type is not null
                            ? new[] { "type", "declaration", "annotation" }
                            : includeInternal && declaration.Initializer is ArrowFunctionNode or FunctionExpressionNode
                                ? ["type", "declaration", "expression", "return", "parameters", "signature"] : ["type", "declaration"])
                        {
                            var tracker = new Tracker();
                            string text = operation switch
                            {
                                "type" => await checker.SerializeTypeSyntaxAsync(
                                    type,
                                    scopes[scope],
                                    flag,
                                    tracker: tracker,
                                    internalFlags: internalFlag),
                                "annotation" => await checker.SerializeJsTypeForEmitAsync(
                                    declaration.Type!,
                                    scopes[scope],
                                    flag,
                                    tracker: tracker,
                                    internalFlags: internalFlag),
                                "expression" => await checker.SerializeExpressionTypeForEmitAsync(
                                    declaration.Initializer!,
                                    scopes[scope],
                                    flag,
                                    tracker: tracker,
                                    internalFlags: internalFlag),
                                "return" => await checker.SerializeReturnTypeForEmitAsync(
                                    declaration.Initializer!,
                                    scopes[scope],
                                    flag,
                                    tracker: tracker,
                                    internalFlags: internalFlag),
                                "parameters" => string.Join(
                                    ", ",
                                    await checker.SerializeTypeParametersForEmitAsync(
                                        declaration.Initializer!,
                                        scopes[scope],
                                        flag,
                                        tracker: tracker,
                                        internalFlags: internalFlag)),
                                "signature" => await checker.SerializeSignatureSyntaxAsync(
                                    await checker.Signatures.FromDeclarationAsync(declaration.Initializer!),
                                    TypeScript.Compiler.Syntax.SyntaxKind.FunctionType,
                                    scopes[scope],
                                    flag,
                                    tracker: tracker,
                                    internalFlags: internalFlag),
                                _ => await checker.SerializeDeclarationTypeForEmitAsync(
                                    declaration,
                                    scopes[scope],
                                    flag,
                                    tracker: tracker,
                                    internalFlags: internalFlag)
                            };
                            writer.WriteStartArray();
                            writer.WriteStringValue(name.Text);
                            writer.WriteStringValue(operation);
                            writer.WriteNumberValue(scope);
                            writer.WriteNumberValue((uint)flag);
                            writer.WriteStringValue(text);
                            writer.WriteStartArray();
                            foreach (var entry in tracker.Events)
                                writer.WriteStringValue(entry);
                            writer.WriteEndArray();
                            if (includeInternal)
                                writer.WriteNumberValue((uint)internalFlag);
                            writer.WriteEndArray();
                        }
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

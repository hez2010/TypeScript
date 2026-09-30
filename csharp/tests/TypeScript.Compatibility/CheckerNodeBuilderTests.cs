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
        internal List<Utf8String> Events { get; } = [];
        internal Func<Symbol, bool>? OnTrack { get; init; }

        public bool TrackSymbol(Symbol symbol, SyntaxNode? enclosingDeclaration, SymbolFlags meaning)
        {
            // The Go JSON encoder replaces its invalid UTF-8 internal-name prefix.
            Utf8String name = SemanticSyntax.Name(symbol.ValueDeclaration) is PrivateIdentifierNode privateName ? Utf8String.Format(privateName.Text)
                : symbol.Name.Replace(Symbol.InternalPrefix, "\uFFFD"u8);
            Events.Add(Utf8String.ConcatMany("symbol:"u8, name, ":"u8, Utf8String.Format(enclosingDeclaration?.Pos ?? -1), ":"u8, Utf8String.Format((uint)meaning)));
            return OnTrack?.Invoke(symbol) ?? false;
        }

        public void ReportInaccessibleThisError() => Events.Add("this"u8);

        public void ReportPrivateInBaseOfClassExpression(Utf8String propertyName) => Events.Add(Utf8String.Copy("private:"u8) + propertyName);

        public void ReportInaccessibleUniqueSymbolError() => Events.Add("unique"u8);

        public void ReportCyclicStructureError() => Events.Add("cycle"u8);

        public void ReportLikelyUnsafeImportRequiredError(Utf8String specifier, Utf8String symbolName) =>
            Events.Add(Utf8String.ConcatMany("unsafe:"u8, specifier, ":"u8, symbolName));

        public void ReportTruncationError() => Events.Add("truncation"u8);

        public void ReportNonlocalAugmentation(SourceFileNode containingFile, Symbol parentSymbol, Symbol augmentingSymbol)
                    => Events.Add(Utf8String.ConcatMany("augmentation:"u8, containingFile.FileName, ":"u8, parentSymbol.Name, ":"u8, augmentingSymbol.Name));

        public void ReportNonSerializableProperty(Utf8String propertyName) => Events.Add(Utf8String.Copy("property:"u8) + propertyName);

        public void ReportInferenceFallback(SyntaxNode node) => Events.Add(Utf8String.ConcatMany("inference:"u8, Utf8String.Format(node.Pos), ":"u8, Utf8String.Format((int)node.Kind)));

        public void PushErrorFallbackNode(SyntaxNode node) => Events.Add(Utf8String.ConcatMany("push:"u8, Utf8String.Format(node.Pos), ":"u8, Utf8String.Format((int)node.Kind)));

        public void PopErrorFallbackNode() => Events.Add("pop"u8);
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
        options.SetRaw("noLib"u8, "true"u8);
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode(
                "interface Object{}interface Function{}interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}interface A{value:string}declare let show:{a:A;f:<T extends A>(x:T)=>T;雪:'雪'};declare let empty:[];"),
            ["/project/long.ts"u8] = (Utf8String.Copy("declare let long:{value:'"u8) + new Utf8String(
                    'x',
                    1_000_010) + "';a:number;b:number;c:number;d:number;e:number;f:number;g:number}"u8).Span.ToArray()
        };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
            new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        var declarations = source.DescendantsAndSelf().OfType<VariableDeclarationNode>().ToArray();
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var type = await checker.GetTypeFromTypeNodeAsync(declarations[0].Type!);
        const NodeBuilderFlags flags = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation;
        Utf8String first = await checker.SerializeTypeSyntaxAsync(type, source, flags);
        Check(checker.SerializedTypeSyntaxCount > 0);
        int count = checker.SerializedTypeSyntaxCount;
        var tracker = new Tracker();
        Check(await checker.SerializeTypeSyntaxAsync(type, source, flags, tracker: tracker) == first);
        Check(tracker.Events.Any(e => e.StartsWith("symbol:A:"u8, StringComparison.Ordinal)));
        Check(tracker.Events.All(e => !e.StartsWith("symbol:T:"u8, StringComparison.Ordinal)));
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
        Check(await checker.SerializeTypeSyntaxAsync(empty, source, NodeBuilderFlags.None) == ""u8);
        Check(await checker.SerializeTypeSyntaxAsync(empty, source, NodeBuilderFlags.AllowEmptyTuple) == "[]"u8);
        var longFile = program.GetFile("/project/long.ts"u8)!.Syntax;
        var longType = await checker.GetTypeFromTypeNodeAsync(
            longFile.DescendantsAndSelf().OfType<VariableDeclarationNode>().Single().Type!);
        var truncation = new Tracker();
        await checker.SerializeTypeSyntaxAsync(longType, longFile, flags, tracker: truncation);
        Check(truncation.Events.Count(e => e == "truncation"u8) == 1);
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
        writer.WriteStartArray("tracking"u8);
        foreach (var declaration in file.DescendantsAndSelf().OfType<VariableDeclarationNode>())
        {
            if (declaration.Name is not IdentifierNode name || !name.Text.Span.StartsWith("show"u8, StringComparison.Ordinal))
                continue;
            var type = declaration.Type is { } annotation ? await checker.GetTypeFromTypeNodeAsync(annotation)
                : await checker.Values.GetAsync(checker.Symbols.Declaration(declaration)!);
            SyntaxNode?[] scopes = [null, file, declaration];
            for (int scope = 0; scope < scopes.Length; scope++)
                foreach (var flag in flags)
                    foreach (var internalFlag in internalFlags!)
                        foreach (Utf8String operation in includeInternal && declaration.Type is not null
                            ? new Utf8String[] { "type"u8, "declaration"u8, "annotation"u8 }
                            : includeInternal && declaration.Initializer is ArrowFunctionNode or FunctionExpressionNode
                                ? ["type"u8, "declaration"u8, "expression"u8, "return"u8, "parameters"u8, "signature"u8] : ["type"u8, "declaration"u8])
                        {
                            var tracker = new Tracker();
                            Utf8String text = operation switch
                            {
                                _ when operation == "type"u8 => await checker.SerializeTypeSyntaxAsync(
                                    type,
                                    scopes[scope],
                                    flag,
                                    tracker: tracker,
                                    internalFlags: internalFlag),
                                _ when operation == "annotation"u8 => await checker.SerializeJsTypeForEmitAsync(
                                    declaration.Type!,
                                    scopes[scope],
                                    flag,
                                    tracker: tracker,
                                    internalFlags: internalFlag),
                                _ when operation == "expression"u8 => await checker.SerializeExpressionTypeForEmitAsync(
                                    declaration.Initializer!,
                                    scopes[scope],
                                    flag,
                                    tracker: tracker,
                                    internalFlags: internalFlag),
                                _ when operation == "return"u8 => await checker.SerializeReturnTypeForEmitAsync(
                                    declaration.Initializer!,
                                    scopes[scope],
                                    flag,
                                    tracker: tracker,
                                    internalFlags: internalFlag),
                                _ when operation == "parameters"u8 => Utf8String.Join(
                                    ", "u8,
                                    await checker.SerializeTypeParametersForEmitAsync(
                                        declaration.Initializer!,
                                        scopes[scope],
                                        flag,
                                        tracker: tracker,
                                        internalFlags: internalFlag)),
                                _ when operation == "signature"u8 => await checker.SerializeSignatureSyntaxAsync(
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
                            writer.WriteStringValue(name.Text.Span);
                            writer.WriteStringValue(operation);
                            writer.WriteNumberValue(scope);
                            writer.WriteNumberValue((uint)flag);
                            writer.WriteStringValue(text.Span);
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

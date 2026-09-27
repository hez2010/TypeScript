using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerContextQueryTests
{
    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Context query assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        const string source = "declare function color<T extends 'red'|'blue'>(value:T):T; color('red'); color('missing'); "
            + "declare function callback<T>(value:T,func:(x:T)=>T):T; callback('red',x=>x); const tuple:[number,string]=[1,'x'];";
        const string library = "interface Object{} interface Function{} interface CallableFunction extends Function{} "
            + "interface NewableFunction extends Function{} interface IArguments{} interface String{} interface Number{} interface Boolean{} "
            + "interface RegExp{} interface Array<T>{length:number;[n:number]:T;} interface ReadonlyArray<T>{readonly length:number;readonly [n:number]:T;}";
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source), ["/project/lib.d.ts"] = Wtf8.Encode(library) }), "/project",
            new("/project/tsconfig.json", options, ["/project/lib.d.ts", "/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var nodes = program.GetFile("/project/main.ts")!.Syntax.DescendantsAndSelf().ToArray();
        var calls = nodes.OfType<CallExpressionNode>().ToArray();
        var call = calls[0];
        var argument = call.Arguments![0];
        var signature = await checker.GetResolvedSignatureAsync(call);
        Check(await checker.GetReturnTypeOfSignatureAsync(signature) is LiteralType { Value: TextSlice { Span: "red" } });
        Check(await checker.GetContextualTypeAsync(argument) is LiteralType { Value: TextSlice { Span: "red" } });
        var blocked = await checker.GetContextualTypeAsync(argument, ContextFlags.IgnoreNodeInferences);
        Check(blocked is UnionType union && union.Types.Count == 2
            && union.Types.All(t => t is LiteralType { Value: TextSlice { Span: "red" or "blue" } }));
        Check(await checker.GetResolvedSignatureAsync(call) == signature);
        Check(checker.SkippedInferenceNodes.Count == 0 && !checker.InferencePartiallyBlocked && checker.ApparentArgumentCount is null);
        var arrow = nodes.OfType<ArrowFunctionNode>().Single();
        await checker.GetResolvedSignatureAsync(calls[2]);
        var arrowSignature = checker.Links.Signatures.Get(arrow).ResolvedSignature;
        var arrowValue = checker.Links.Values.Get(checker.Symbols.Declaration(arrow)!).ResolvedType;
        await checker.GetContextualTypeAsync(arrow.Body!, ContextFlags.IgnoreNodeInferences);
        Check(checker.Links.Signatures.Get(arrow).ResolvedSignature == arrowSignature);
        Check(checker.Links.Values.Get(checker.Symbols.Declaration(arrow)!).ResolvedType == arrowValue);
        checker.SkippedInferenceNodes.Add(arrow);
        await checker.GetContextualTypeAsync(argument, ContextFlags.IgnoreNodeInferences);
        Check(checker.SkippedInferenceNodes.SetEquals([arrow]));
        checker.SkippedInferenceNodes.Remove(arrow);
        checker.BeforeExpressionFinish = () => throw new InvalidOperationException("context callback");
        try
        {
            await checker.GetContextualTypeAsync(argument, ContextFlags.IgnoreNodeInferences);
            throw new InvalidOperationException("Context callback was not reached");
        }
        catch (InvalidOperationException error) when (error.Message == "context callback")
        {
            checks++;
        }
        finally
        {
            checker.BeforeExpressionFinish = null;
        }
        Check(checker.Links.Signatures.Get(call).ResolvedSignature == signature && checker.SkippedInferenceNodes.Count == 0);
        using var cancellation = new CancellationTokenSource();
        checker.BeforeExpressionFinish = () =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };
        try
        {
            await checker.GetContextualTypeAsync(argument, ContextFlags.IgnoreNodeInferences, cancellation.Token);
            throw new InvalidOperationException("Cancelled contextual query completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        finally
        {
            checker.BeforeExpressionFinish = null;
        }
        Check(!checker.InferencePartiallyBlocked && checker.ApparentArgumentCount is null && checker.SkippedInferenceNodes.Count == 0);
        Check(checker.Links.Signatures.Get(call).ResolvedSignature == signature);
        Check(checker.Contexts.ContextDepth == 0 && checker.Contexts.InferenceDepth == 0 && checker.CallResolution.ResolutionDepth == 0);
        var help = await checker.GetResolvedSignatureForSignatureHelpAsync(call, 2);
        Check(help.Candidates.Count == 1 && checker.ApparentArgumentCount is null);
        Check(checker.Links.Signatures.Get(call).ResolvedSignature == signature);
        var firstFailure = await checker.GetResolvedSignatureForSignatureHelpAsync(calls[1], 1);
        var secondFailure = await checker.GetResolvedSignatureForSignatureHelpAsync(calls[1], 1);
        Check(firstFailure.Signature != secondFailure.Signature);
        var candidates = await checker.GetCandidateSignaturesForStringLiteralCompletionsAsync(calls[1], calls[1].Arguments![0]);
        Check(candidates.Count == 2 && candidates[0] != candidates[1]);
        var fresh = await program.CreateCheckerAsync();
        int reports = 0;
        fresh.BeforeCallDiagnostics = _ => reports++;
        await fresh.GetContextualTypeAsync(calls[1].Arguments![0], ContextFlags.IgnoreNodeInferences);
        Check(reports == 0 && !fresh.Diagnostics.Contains(DiagnosticCode.ArgumentOfType0IsNotAssignableToParameterOfType1));
        await fresh.GetResolvedSignatureAsync(calls[1]);
        Check(reports != 0 && fresh.Diagnostics.Contains(DiagnosticCode.ArgumentOfType0IsNotAssignableToParameterOfType1));
        var array = nodes.OfType<ArrayLiteralExpressionNode>().Single();
        var contextual = await checker.GetContextualTypeAsync(array);
        Check(
            await checker.GetContextualTypeForArrayLiteralAtPositionAsync(
                contextual,
                array,
                array.Elements![0].Pos) == checker.Context.NumberType);
        Check(
            await checker.GetContextualTypeForArrayLiteralAtPositionAsync(
                contextual,
                array,
                array.Elements[1].Pos) == checker.Context.StringType);
        try
        {
            await checker.GetReturnTypeOfSignatureAsync(await fresh.GetResolvedSignatureAsync(call));
            throw new InvalidOperationException("Foreign signature accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Check(await checker.GetResolvedSignatureAsync(call) == signature);
        return checks;
    }

    internal static async Task WriteAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, Checker checker,
        Func<Type?, int> typeId, Func<Symbol?, int> symbolId, Func<SyntaxNode?, int> nodeId)
    {
        var signatures = new List<Signature>();
        var ids = new Dictionary<Signature, int>();
        int SignatureId(Signature? signature)
        {
            if (signature is null)
                return 0;
            if (!ids.TryGetValue(signature, out int id))
            {
                ids.Add(signature, id = signatures.Count + 1);
                signatures.Add(signature);
            }
            return id;
        }
        void SignatureIds(IReadOnlyList<Signature> values)
        {
            writer.WriteStartArray();
            foreach (var value in values)
                writer.WriteNumberValue(SignatureId(value));
            writer.WriteEndArray();
        }
        void Start(int operation, SyntaxNode node)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(operation);
            writer.WriteNumberValue(nodeId(node));
        }
        writer.WriteStartArray("contextQueries");
        foreach (var node in nodes)
        {
            if (SemanticSyntax.Source(node)?.FileName.StartsWith("/project/main.", StringComparison.Ordinal) != true)
                continue;
            if (QuerySyntax.Expression(node))
                foreach (var flags in new ContextFlags[] { 0, ContextFlags.Signature, ContextFlags.NoConstraints,
                    ContextFlags.IgnoreNodeInferences, ContextFlags.SkipBindingPatterns, ContextFlags.NoConstraints | ContextFlags.IgnoreNodeInferences })
                {
                    Start(0, node);
                    writer.WriteNumberValue((uint)flags);
                    writer.WriteNumberValue(typeId(await checker.GetContextualTypeAsync(node, flags)));
                    writer.WriteEndArray();
                }
            if (node.Parent is ObjectLiteralExpressionNode)
            {
                Start(1, node);
                writer.WriteNumberValue(typeId(await checker.GetContextualTypeForObjectLiteralElementAsync(node)));
                writer.WriteEndArray();
            }
            if (node is ArrayLiteralExpressionNode array)
            {
                var contextual = await checker.GetContextualTypeAsync(array);
                for (int i = 0; i <= array.Elements!.Count; i++)
                {
                    Start(2, node);
                    writer.WriteNumberValue(i);
                    writer.WriteNumberValue(typeId(await checker.GetContextualTypeForArrayLiteralAtPositionAsync(contextual, array,
                        i == array.Elements.Count ? array.End : array.Elements[i].Pos)));
                    writer.WriteEndArray();
                }
            }
            if (node is JsxAttributeNode or JsxSpreadAttributeNode)
            {
                Start(3, node);
                writer.WriteNumberValue(typeId(await checker.GetContextualTypeForJsxAttributeAsync(node)));
                writer.WriteEndArray();
            }
            if (!Checker.QueryCallLike(node))
                continue;
            var signature = await checker.GetResolvedSignatureAsync(node);
            Start(4, node);
            writer.WriteNumberValue(SignatureId(signature));
            writer.WriteNumberValue(typeId(await checker.GetReturnTypeOfSignatureAsync(signature)));
            writer.WriteEndArray();
            int count = CallArguments.List(node)?.Count ?? 1;
            for (int i = 0; i <= count; i++)
            {
                Start(5, node);
                writer.WriteNumberValue(i);
                writer.WriteNumberValue(typeId(await checker.GetContextualTypeForArgumentAtIndexAsync(node, i)));
                writer.WriteEndArray();
            }
            foreach (int argumentCount in new[] { 0, count, count + 1 })
            {
                var help = await checker.GetResolvedSignatureForSignatureHelpAsync(node, argumentCount);
                Start(6, node);
                writer.WriteNumberValue(argumentCount);
                writer.WriteNumberValue(SignatureId(help.Signature));
                SignatureIds(help.Candidates);
                writer.WriteEndArray();
            }
            foreach (var argument in (IEnumerable<SyntaxNode>?)CallArguments.List(node) ?? [])
                if (argument is StringLiteralNode)
                {
                    Start(7, node);
                    writer.WriteNumberValue(nodeId(argument));
                    SignatureIds(await checker.GetCandidateSignaturesForStringLiteralCompletionsAsync(node, argument));
                    writer.WriteEndArray();
                }
        }
        writer.WriteEndArray();
        writer.WriteStartArray("querySignatureGraph");
        for (int i = 0; i < signatures.Count; i++)
        {
            var signature = signatures[i];
            writer.WriteStartArray();
            writer.WriteNumberValue((uint)signature.Flags);
            writer.WriteNumberValue(nodeId(signature.Declaration));
            writer.WriteNumberValue(signature.MinArgumentCount);
            writer.WriteNumberValue(signature.ResolvedMinArgumentCount);
            writer.WriteStartArray();
            foreach (var parameter in signature.TypeParameters)
                writer.WriteNumberValue(typeId(parameter));
            writer.WriteEndArray();
            writer.WriteNumberValue(symbolId(signature.ThisParameter));
            writer.WriteStartArray();
            foreach (var parameter in signature.Parameters)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(symbolId(parameter));
                writer.WriteNumberValue(typeId(await checker.Values.GetAsync(parameter)));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteNumberValue(typeId(await checker.GetReturnTypeOfSignatureAsync(signature)));
            writer.WriteNumberValue(SignatureId(signature.Target));
            if (signature.Composite is { } composite)
            {
                writer.WriteBooleanValue(composite.IsUnion);
                SignatureIds(composite.Signatures);
            }
            else
            {
                writer.WriteNullValue();
                writer.WriteNullValue();
            }
            if (signature.ResolvedTypePredicate is { } predicate)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue((int)predicate.Kind);
                writer.WriteNumberValue(predicate.ParameterIndex);
                writer.WriteStringValue(predicate.ParameterName.Span);
                writer.WriteNumberValue(typeId(predicate.Type));
                writer.WriteEndArray();
            }
            else
                writer.WriteNullValue();
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}

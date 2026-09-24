using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using Checking = TypeScript.Compiler.Checking;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : ICallArgumentHost, ICallSignatureHost, ICallResolutionHost, ICallExpressionHost, ILiteralElaborationHost
{
    internal GenericExpressions GenericExpressions { get; }
    internal CallArguments CallArguments { get; }
    internal CallSignatures CallSignatures { get; }
    internal CallInference CallInference { get; }
    internal CallResolution CallResolution { get; }
    internal CallExpressions Calls { get; }
    internal ConstructorAccess ConstructorAccess { get; }
    internal BestMatchingTypes BestMatchingTypes { get; }
    internal LiteralElaboration LiteralElaboration { get; }
    internal HashSet<SyntaxNode> MissingAwaitHints { get; } = [];
    private int? relationDiagnosticHead;
    internal Action<SyntaxNode>? BeforeCallDiagnostics { get; set; }

    public void LiteralRelationError(SyntaxNode node, int code) => Error(node, relationDiagnosticHead ?? code);

    public async ValueTask MissingAwaitInfoAsync(
        SyntaxNode node,
        Type source,
        Type target,
        RelationKind relation,
        CancellationToken cancellation)
    {
        if (await Awaited.OfPromiseAsync(target, cancellation: cancellation) is not null)
            return;
        if (await Awaited.OfPromiseAsync(source, cancellation: cancellation) is { } awaited
            && await Relations.RelatedAsync(awaited, target, relation, cancellation))
            MissingAwaitHints.Add(node);
    }

    internal async ValueTask<Type?> ContextualCallArgumentAsync(SyntaxNode call, SyntaxNode argument, CancellationToken cancellation)
    {
        var arguments = await CallArguments.EffectiveAsync(call, cancellation);
        int index = arguments.ToList().IndexOf(argument);
        if (index < 0)
            return null;
        var signature = links.Signatures.Get(call).ResolvedSignature == CallSignatures.Resolving
            ? CallSignatures.Resolving : await CallResolution.GetAsync(call, cancellation: cancellation);
        int restIndex = signature.Parameters.Count - 1;
        return signature.HasRestParameter && index >= restIndex
            ? await Indexed.GetAsync(
                await Values.GetAsync(signature.Parameters[restIndex], cancellation),
                context.GetNumberLiteralType(index - restIndex),
                Checking.AccessFlags.Contextual, cancellation: cancellation) : await Parameters.AtAsync(signature, index, cancellation);
    }

    public async ValueTask<Type?> NumberIndexAsync(Type type, CancellationToken cancellation) =>
        (await IndexesAsync(type, cancellation)).FirstOrDefault(i => i.KeyType == context.NumberType)?.ValueType;

    public async ValueTask<IReadOnlyList<SyntaxNode>> SpecialArgumentsAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (node is TaggedTemplateExpressionNode tagged)
        {
            var type = await program.Globals.GetAsync("TemplateStringsArray", 0, true, cancellation);
            var result = new List<SyntaxNode> { Checking.CallArguments.Synthetic(tagged.Template!, type) };
            if (tagged.Template is TemplateExpressionNode template)
                result.AddRange(template.TemplateSpans!.OfType<TemplateSpanNode>().Select(s => s.Expression!));
            return result;
        }
        if (node is BinaryExpressionNode binary)
            return [binary.Left!];
        throw new InvalidOperationException("Checker requires JSX/decorator effective arguments");
    }

    public ValueTask<bool> SpecialArityAsync(
        SyntaxNode node,
        IReadOnlyList<SyntaxNode> arguments,
        Signature signature,
        CancellationToken cancellation) =>
        throw new InvalidOperationException("Checker requires JSX/decorator arity rules");

    public ValueTask<bool> ConstructorAccessibleAsync(SyntaxNode node, IReadOnlyList<Signature> signatures, CancellationToken cancellation)
        => ConstructorAccess.CheckAsync(node, signatures, cancellation);

    public async ValueTask<Signature> SpecialCallAsync(
        SyntaxNode node,
        List<Signature>? candidates,
        CheckMode mode,
        CancellationToken cancellation)
    {
        if (node is BinaryExpressionNode binary)
        {
            var type = await Expressions.CheckAsync(binary.Right!, cancellation: cancellation);
            if ((type.Flags & TypeFlags.Any) == 0)
            {
                if (await HasInstanceMethodAsync(type, cancellation) is { } method)
                {
                    var apparent = await Views.ApparentAsync(method, cancellation);
                    if (apparent == context.ErrorType)
                        return await CallResolution.UntypedAsync(node, true, cancellation);
                    var calls = await SignaturesAsync(apparent, false, cancellation);
                    var constructors = await SignaturesAsync(apparent, true, cancellation);
                    if ((method.Flags & TypeFlags.Any) != 0 || (apparent.Flags & TypeFlags.Any) != 0 && method is TypeParameter
                        || calls.Count == 0 && constructors.Count == 0 && apparent is not UnionType
                            && ((await Views.ReducedAsync(apparent, cancellation)).Flags & TypeFlags.Never) == 0
                            && await AssignableAsync(method, GlobalFunction, cancellation))
                        return await CallResolution.UntypedAsync(node, false, cancellation);
                    if (calls.Count != 0)
                        return await CallResolution.OverloadAsync(node, calls, candidates, mode, cancellation: cancellation);
                }
                else if ((await SignaturesAsync(type, false, cancellation)).Count == 0
                    && (await SignaturesAsync(type, true, cancellation)).Count == 0
                    && !await Relations.RelatedAsync(type, GlobalFunction, RelationKind.Subtype, cancellation))
                {
                    Error(binary.Right!, 2359);
                    return await CallResolution.UntypedAsync(node, true, cancellation);
                }
            }
            return CallSignatures.Any;
        }
        if (node is TaggedTemplateExpressionNode tagged)
        {
            var type = await Expressions.CheckAsync(tagged.Tag!, cancellation: cancellation);
            var apparent = await Views.ApparentAsync(type, cancellation);
            if (apparent == context.ErrorType || (apparent.Flags & TypeFlags.Any) != 0 && apparent.Alias is not null)
                return await CallResolution.UntypedAsync(node, true, cancellation);
            var calls = await SignaturesAsync(apparent, false, cancellation);
            var constructors = await SignaturesAsync(apparent, true, cancellation);
            if ((type.Flags & TypeFlags.Any) != 0 || (apparent.Flags & TypeFlags.Any) != 0 && type is TypeParameter
                || calls.Count == 0 && constructors.Count == 0 && apparent is not UnionType
                    && ((await Views.ReducedAsync(
                        apparent,
                        cancellation)).Flags & TypeFlags.Never) == 0 && await AssignableAsync(type, GlobalFunction, cancellation))
                return await CallResolution.UntypedAsync(node, false, cancellation);
            if (calls.Count != 0)
                return await CallResolution.OverloadAsync(node, calls, candidates, mode, cancellation: cancellation);
            if (node.Parent is ArrayLiteralExpressionNode)
                Error(tagged.Tag!, 2796);
            else
                await InvocationErrorAsync(tagged.Tag!, apparent, false, cancellation);
            return await CallResolution.UntypedAsync(node, true, cancellation);
        }
        if (node is CallExpressionNode { Expression.Kind: SyntaxKind.SuperKeyword } call)
        {
            var type = await ThisExpressions.SuperAsync(call.Expression!, cancellation);
            if ((type.Flags & TypeFlags.Any) != 0)
            {
                foreach (var argument in call.Arguments!)
                    await Expressions.CheckAsync(argument, cancellation: cancellation);
                return CallSignatures.Any;
            }
            var containing = DeclarationOrder.Ancestor(node, SemanticSyntax.ClassLike);
            if (type != context.ErrorType && containing is not null
                && ClassBases.BaseNode(
                    (InterfaceType)await Declared.GetAsync(program.Symbols.Declaration(containing)!, cancellation)) is { } baseNode)
                return await CallResolution.OverloadAsync(
                    node,
                    await ClassBases.ConstructorsAsync(type, baseNode, cancellation),
                    candidates,
                    mode,
                    cancellation: cancellation);
            return await CallResolution.UntypedAsync(node, false, cancellation);
        }
        throw new InvalidOperationException("Checker requires import/tagged-template/decorator/JSX/instanceof call resolution");
    }

    public async ValueTask InvocationErrorAsync(SyntaxNode node, Type type, bool construct, CancellationToken cancellation)
    {
        var awaited = await Awaited.GetAsync(type, cancellation: cancellation);
        if (awaited is not null)
            await SignaturesAsync(awaited, construct, cancellation);
        if (type is UnionType union)
        {
            bool callable = false, missing = false;
            foreach (var part in union.Types)
            {
                if ((await SignaturesAsync(part, construct, cancellation)).Count != 0)
                    callable = true;
                else
                    missing = true;
                if (callable && missing)
                    break;
            }
        }
        Error(node, construct ? 2351 : 2349);
    }

    public async ValueTask<bool> ArgumentRelatedAsync(Type source, Type target, RelationKind relation, SyntaxNode? errorNode,
        SyntaxNode expression, int code, CancellationToken cancellation)
    {
        if (errorNode is null)
            return await Relations.RelatedAsync(source, target, relation, cancellation);
        int? previous = relationDiagnosticHead;
        if (code is 2769 or 2860)
            relationDiagnosticHead = code;
        try
        {
            return await RelationDiagnostics.CheckAsync(source, target, relation, errorNode, expression, code, cancellation);
        }
        finally
        {
            relationDiagnosticHead = previous;
        }
    }

    public ValueTask CallGrammarAsync(SyntaxNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (node is TaggedTemplateExpressionNode tag && (tag.QuestionDotToken is not null || (tag.Flags & NodeFlags.OptionalChain) != 0))
        {
            if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
                Error(tag.Template!, 1358);
            return ValueTask.CompletedTask;
        }
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0 && Checking.CallArguments.TypeNodes(node) is { } types)
        {
            if (types.HasTrailingComma)
                Error(node, 1009);
            else if (types.Count == 0)
                Error(node, 1099);
        }
        return ValueTask.CompletedTask;
    }

    public void DeprecatedSignature(SyntaxNode node, Signature signature)
    {
        if (signature.Declaration is { } declaration && program.Deprecations.Declaration(declaration))
            ExpressionSuggestion(node, 6387);
    }

    public async ValueTask<Type?> SpecialCallResultAsync(SyntaxNode node, Type result, CancellationToken cancellation)
    {
        if ((node.Flags & NodeFlags.JavaScriptFile) != 0 && CommonJsRequire(node, cancellation))
        {
            var specifier = ((CallExpressionNode)node).Arguments![0];
            var module = await program.ExternalModuleAsync(specifier, specifier, null, cancellation).ConfigureAwait(false);
            var target = module is null
                ? null
                : await program.AliasTargets.ExternalModuleAsync(module, false, cancellation).ConfigureAwait(false);
            return target is null ? context.AnyType : await Values.GetAsync(target, cancellation).ConfigureAwait(false);
        }
        if ((result.Flags & TypeFlags.ESSymbolLike) != 0 && node is CallExpressionNode call)
        {
            var target = call.Expression is PropertyAccessExpressionNode { Name: IdentifierNode { Text: "for" } } property
                ? property.Expression
                : call.Expression;
            if (target is IdentifierNode { Text: "Symbol" } identifier
                && program.Symbols.Lookup(program.Symbols.Globals, "Symbol", SymbolFlags.Value) is { } global
                && program.Symbols.NameResolver(cancellation).Resolve(identifier, "Symbol", SymbolFlags.Value) == global)
            {
                var declaration = node.Parent;
                while (declaration is ParenthesizedExpressionNode)
                    declaration = declaration.Parent;
                return Nodes.UniqueSymbol(declaration);
            }
        }
        return null;
    }

    private bool CommonJsRequire(SyntaxNode node, CancellationToken cancellation)
    {
        if (node is not CallExpressionNode { Expression: IdentifierNode { Text: "require" } name, Arguments.Count: 1 } call
            || call.Arguments[0] is not (StringLiteralNode or NoSubstitutionTemplateLiteralNode))
            return false;
        var symbol = program.Symbols.NameResolver(cancellation).Resolve(name, name.Text, SymbolFlags.Value);
        if (symbol == program.Symbols.RequireSymbol)
            return true;
        if (symbol is null || (symbol.Flags & SymbolFlags.Alias) != 0)
            return false;
        var declaration = (symbol.Flags & SymbolFlags.Function) != 0 ? symbol.Declarations.FirstOrDefault(d => d is FunctionDeclarationNode)
            : (symbol.Flags & SymbolFlags.Variable) != 0 ? symbol.Declarations.FirstOrDefault(d => d is VariableDeclarationNode) : null;
        return declaration is not null && (declaration.Flags & NodeFlags.Ambient) != 0;
    }

    public async ValueTask CheckAssertionCallAsync(CallExpressionNode node, CancellationToken cancellation)
    {
        SyntaxNode target = node.Expression!;
        while (target is PropertyAccessExpressionNode or ParenthesizedExpressionNode)
            target = target is PropertyAccessExpressionNode property
                ? property.Expression!
                : ((ParenthesizedExpressionNode)target).Expression!;
        if (target is not IdentifierNode
            && target.Kind is not SyntaxKind.ThisKeyword and not SyntaxKind.SuperKeyword and not SyntaxKind.MetaProperty)
            Error(node.Expression!, 2776);
        else if (await FlowEffects.GetAsync(node, cancellation) is null)
        {
            Error(node.Expression!, 2775);
            await ExplicitValues.DottedAsync(node.Expression!, true, cancellation);
        }
    }

    public async ValueTask ReportCallErrorsAsync(
        CallResolution.State state,
        IReadOnlyList<Signature> original,
        CancellationToken cancellation)
    {
        BeforeCallDiagnostics?.Invoke(state.Node);
        cancellation.ThrowIfCancellationRequested();
        if (state.ArgumentErrors.Count != 0)
        {
            await CallResolution.ApplicableAsync(state.Node, state.Arguments, state.ArgumentErrors[^1], RelationKind.Assignable, 0,
                true, state.ArgumentErrors.Count > 1 ? 2769 : state.Node is BinaryExpressionNode ? 2860 : 2345, cancellation);
            return;
        }
        if (state.ArgumentArityError is { } arity)
        {
            await ArityAsync([arity]);
            return;
        }
        if (state.TypeArgumentError is { } typeError)
        {
            await CallSignatures.TypeArgumentsAsync(typeError, state.TypeArguments, true, cancellation);
            return;
        }
        var correct = original.Where(s => Checking.CallSignatures.TypeArity(s, state.TypeArguments)).ToArray();
        if (correct.Length == 0)
        {
            Error(state.Node, 2558);
            return;
        }
        await ArityAsync(correct);

        async ValueTask ArityAsync(IReadOnlyList<Signature> signatures)
        {
            int spread = Checking.CallArguments.SpreadIndex(state.Arguments);
            if (spread >= 0)
            {
                Error(state.Arguments[spread], 2556);
                return;
            }
            int minimum = int.MaxValue, maximum = 0;
            bool rest = false;
            foreach (var signature in signatures)
            {
                minimum = Math.Min(minimum, await Parameters.MinimumAsync(signature, cancellation: cancellation));
                maximum = Math.Max(maximum, await Parameters.CountAsync(signature, cancellation));
                rest |= await Parameters.HasRestAsync(signature, cancellation);
            }
            Error(state.Node, minimum < state.Arguments.Count && state.Arguments.Count < maximum ? 2575 : rest ? 2555 : 2554);
        }
    }
}

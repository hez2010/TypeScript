using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
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

    public async ValueTask LiteralRelationErrorAsync(SyntaxNode node, int code, Type source, Type target, CancellationToken cancellation)
    {
        if (relationDiagnosticHead is { } head)
            RelationError(node, head);
        else
            RelationError(node, code, await TypeDisplay.GetAsync(source, cancellation), await TypeDisplay.GetAsync(target, cancellation));
    }

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
        return await ContextualArgumentAtIndexAsync(call, index, cancellation);
    }

    private async ValueTask<Type?> ContextualArgumentAtIndexAsync(SyntaxNode call, int index, CancellationToken cancellation)
    {
        if (call is CallExpressionNode import && IsImportCall(import))
            return index == 0 ? context.StringType : index == 1
                ? importCallOptionsType ?? await program.Globals.GetAsync("ImportCallOptions", 0, false, cancellation) : context.AnyType;
        var signature = links.Signatures.Get(call).ResolvedSignature == CallSignatures.Resolving
            ? CallSignatures.Resolving : await CallResolution.GetAsync(call, cancellation: cancellation);
        if (JsxOpening(call) && index == 0)
            return await JsxPropsAsync(signature, call, cancellation);
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
        if (node is JsxOpeningFragmentNode)
            return [Checking.CallArguments.Synthetic(node, JsxObject(null, [], fresh: true))];
        if (JsxAttributes(node) is { } attributes)
            return [attributes];
        if (node is DecoratorNode decorator && await DecoratorSignatureAsync(decorator, cancellation) is { } signature)
        {
            var arguments = new List<SyntaxNode>();
            foreach (var parameter in signature.Parameters)
                arguments.Add(Checking.CallArguments.Synthetic(decorator.Expression!, await Values.GetAsync(parameter, cancellation)));
            return arguments;
        }
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

    public async ValueTask<bool> SpecialArityAsync(
        SyntaxNode node,
        IReadOnlyList<SyntaxNode> arguments,
        Signature signature,
        CancellationToken cancellation)
    {
        if (JsxOpening(node))
            return true;
        if (node is not DecoratorNode decorator)
            throw new InvalidOperationException("Checker requires JSX arity rules");
        int count = await DecoratorArgumentCountAsync(decorator, signature, cancellation);
        if (!await Parameters.HasRestAsync(signature, cancellation) && count > await Parameters.CountAsync(signature, cancellation))
            return false;
        int minimum = await Parameters.MinimumAsync(signature, cancellation: cancellation);
        for (int i = count; i < minimum; i++)
        {
            var type = await Parameters.AtAsync(signature, i, cancellation);
            if (!(type is UnionType union ? union.Types : [type]).Any(t => (t.Flags & TypeFlags.Void) != 0))
                return false;
        }
        return true;
    }

    public ValueTask<bool> ConstructorAccessibleAsync(SyntaxNode node, IReadOnlyList<Signature> signatures, CancellationToken cancellation)
        => ConstructorAccess.CheckAsync(node, signatures, cancellation);

    public async ValueTask<Signature> SpecialCallAsync(
        SyntaxNode node,
        List<Signature>? candidates,
        CheckMode mode,
        CancellationToken cancellation)
    {
        if (JsxOpening(node))
            return await ResolveJsxAsync(node, candidates, mode, cancellation);
        if (node is CallExpressionNode importCall && IsImportCall(importCall))
            return await CallResolution.UntypedAsync(node, false, cancellation);
        if (node is DecoratorNode decorator)
            return await ResolveDecoratorAsync(decorator, candidates, mode, cancellation);
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
        bool missingAwait = awaited is not null && (await SignaturesAsync(awaited, construct, cancellation)).Count != 0;
        var location = node is PropertyAccessExpressionNode propertyAccess && node.Parent is CallExpressionNode
            ? propertyAccess.Name!
            : node;
        Diagnostic? detail = null;
        async ValueTask<Diagnostic> Detail(int code, Type value) => CheckerDiagnostic.Create(location,
            DiagnosticLocalization.GetMessage(code), await TypeDisplay.GetAsync(value, cancellation));
        if (type is UnionType union)
        {
            bool callable = false;
            foreach (var part in union.Types)
            {
                if ((await SignaturesAsync(part, construct, cancellation)).Count != 0)
                    callable = true;
                else if (detail is null)
                {
                    var constituent = await Detail(construct ? 2761 : 2757, part);
                    detail = await Detail(construct ? 2760 : 2756, type) with { MessageChain = [constituent] };
                }
                if (callable && detail is not null)
                    break;
            }
            if (!callable)
                detail = await Detail(construct ? 2759 : 2755, type);
            detail ??= await Detail(construct ? 2762 : 2758, type);
        }
        else
            detail = await Detail(construct ? 2761 : 2757, type);
        int code = construct ? 2351 : 2349;
        if (node.Parent is CallExpressionNode { Arguments.Count: 0 }
            && links.SymbolNodes.TryGet(node)?.ResolvedSymbol is { } resolved && (resolved.Flags & SymbolFlags.GetAccessor) != 0)
            code = 6234;
        var related = new List<Diagnostic>();
        if (missingAwait)
            related.Add(CheckerDiagnostic.Create(node, Messages.Did_you_forget_to_use_await));
        if (type.Symbol is { } symbol && links.ExportTypes.TryGet(symbol) is { OriginatingImport: { } import, Target: { } target }
            && import is not CallExpressionNode && (await SignaturesAsync(
                await Values.GetAsync(target, cancellation),
                construct,
                cancellation)).Count != 0)
            related.Add(CheckerDiagnostic.Create(import,
                Messages.Type_originates_at_this_import_A_namespace_style_import_cannot_be_called_or_constructed_and_will_cause_a_failure_at_runtime_Consider_using_a_default_import_or_import_require_here_instead));
        Error(location, CheckerDiagnostic.Create(location, DiagnosticLocalization.GetMessage(code)) with
        { MessageChain = [detail], RelatedInformation = related });
    }

    public async ValueTask<bool> ArgumentRelatedAsync(Type source, Type target, RelationKind relation, SyntaxNode? errorNode,
        SyntaxNode expression, int code, CancellationToken cancellation)
    {
        if (errorNode is null)
            return await Relations.RelatedAsync(source, target, relation, cancellation);
        int? previous = relationDiagnosticHead;
        var previousOutput = relationDiagnosticOutput;
        relationDiagnosticOutput = callDiagnosticOutput;
        if (code is 2769 or 2860 or >= 1238 and <= 1241)
            relationDiagnosticHead = code;
        try
        {
            return await RelationDiagnostics.CheckAsync(source, target, relation, errorNode, expression, code, cancellation);
        }
        finally
        {
            relationDiagnosticHead = previous;
            relationDiagnosticOutput = previousOutput;
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
                EmptyTypeListError(node, types, 1099);
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
            var previous = explicitAnnotationError;
            explicitAnnotationError = node.Expression;
            try
            {
                await ExplicitValues.DottedAsync(node.Expression!, true, cancellation);
            }
            finally
            {
                explicitAnnotationError = previous;
            }
        }
    }

    public async ValueTask ReportCallErrorsAsync(
        CallResolution.State state,
        IReadOnlyList<Signature> original,
        CancellationToken cancellation)
    {
        BeforeCallDiagnostics?.Invoke(state.Node);
        cancellation.ThrowIfCancellationRequested();
        if (state.Node is DecoratorNode decorator)
        {
            int? previous = relationDiagnosticHead;
            var previousOutput = callDiagnosticOutput;
            var output = new List<(SyntaxNode Node, Diagnostic Diagnostic)>();
            relationDiagnosticHead = null;
            callDiagnosticOutput = output;
            try
            {
                if (state.ArgumentErrors.Count != 0)
                    await CallResolution.ApplicableAsync(state.Node, state.Arguments, state.ArgumentErrors[^1], RelationKind.Assignable, 0,
                        true, 2345, cancellation);
                else
                    await ReportArgumentArityAsync(state, state.ArgumentArityError is { } arityError ? [arityError] : original,
                        cancellation, DecoratorHead(decorator));
            }
            finally
            {
                relationDiagnosticHead = previous;
                callDiagnosticOutput = previousOutput;
            }
            foreach (var (_, detail) in output)
                Error(decorator, detail with
                {
                    Message = DiagnosticLocalization.GetMessage(DecoratorHead(decorator)),
                    Arguments = [],
                    MessageChain = [detail with { RelatedInformation = [] }]
                });
            return;
        }
        if (state.ArgumentErrors.Count != 0)
        {
            await ReportOverloadFailureAsync(state, cancellation);
            return;
        }
        if (state.ArgumentArityError is { } arity)
        {
            await ReportArgumentArityAsync(state, [arity], cancellation);
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
            ReportTypeArgumentArity(state, original);
            return;
        }
        await ReportArgumentArityAsync(state, correct, cancellation);
    }
}

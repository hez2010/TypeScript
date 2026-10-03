using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using ModuleOptionKind = TypeScript.Compiler.Configuration.ModuleKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IFunctionContextHost, IFunctionBodyHost, IFunctionExpressionHost, IFunctionDeclarationHost, IFunctionWideningHost, IFunctionThisHost, IAwaitExpressionHost
{
    internal FunctionContexts FunctionContexts { get; }
    internal FunctionBodies FunctionBodies { get; }
    internal FunctionExpressions Functions { get; }
    internal FunctionDeclarations FunctionDeclarations { get; }
    internal FunctionWidening FunctionWidening { get; }
    internal FunctionThis FunctionThis { get; }
    internal AwaitExpressions AwaitExpressions { get; }
    internal TypeReferenceChecks TypeReferenceChecks { get; }
    internal IndexDeclarationChecks IndexDeclarationChecks { get; }
    public Type GlobalThisMarker => program.Globals.Types[Utf8Literals.ThisType];

    public bool ExportsReceiver(SyntaxNode node) => node is IdentifierNode identifier
        && program.Symbols.Binding(node)?.CommonJSModuleIndicator is not null
            && (program.ReferenceSymbols.Resolve(identifier).Flags & SymbolFlags.ModuleExports) != 0;

    public void ExpressionSuggestion(SyntaxNode node, DiagnosticCode code)
        => ExpressionSuggestion(node, CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code)));

    private void ExpressionSuggestion(SyntaxNode node, Diagnostic diagnostic)
    {
        if (suggestionLocations.Add((node, diagnostic.Code)))
        {
            Suggestions.Add(diagnostic.Code);
            suggestionDiagnostics.Add(diagnostic with { Message = diagnostic.Message with { Category = DiagnosticCategory.Suggestion } });
        }
    }

    public ValueTask<bool> CheckConstraintAsync(Type source, Type target, SyntaxNode node, CancellationToken cancellation)
        =>
            RelationDiagnostics.CheckAsync(
                source,
                target,
                RelationKind.Assignable,
                node,
                null,
                DiagnosticCode.Type0DoesNotSatisfyTheConstraint1,
                cancellation);

    public async ValueTask<Type> CheckedFunctionTypeAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var previous = CurrentSourceNode;
        try
        {
            return await CheckedFunctionTypeWorkerAsync(node, cancellation);
        }
        finally
        {
            CurrentSourceNode = previous;
        }
    }

    private async ValueTask<Type> CheckedFunctionTypeWorkerAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var pending = new Stack<(SyntaxNode Node, bool Visited)>();
        pending.Push((node, false));
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            if (item.Visited)
            {
                CurrentSourceNode = item.Node;
                if (item.Node is TypeReferenceNode reference)
                {
                    InstantiationGrammar(reference, reference.TypeArguments);
                    await TypeReferenceChecks.CheckAsync(reference, cancellation);
                    await DeprecatedTypeAsync(reference, cancellation);
                }
                else if (item.Node.Kind == SyntaxKind.ThisType
                    && !(item.Node.Parent is TypePredicateNode thisPredicate && thisPredicate.ParameterName == item.Node))
                    await Nodes.FromNodeAsync(item.Node, cancellation);
                else if (item.Node is IndexedAccessTypeNode indexed)
                    await IndexValidation.CheckAsync(await Nodes.FromNodeAsync(indexed, cancellation), indexed, cancellation);
                else if (item.Node is TypeOperatorNode operation)
                    TypeOperatorGrammar(operation);
                else if (item.Node is MappedTypeNode mapped)
                    await CheckMappedTypeAsync(mapped, cancellation);
                else if (item.Node is TemplateLiteralTypeNode template)
                    await CheckTemplateTypeAsync(template, cancellation);
                else if (item.Node is TupleTypeNode tuple)
                    await TupleTypeGrammarAsync(tuple, cancellation);
                else if (item.Node is NamedTupleMemberNode member)
                    NamedTupleMemberGrammar(member);
                else if (item.Node is JSDocNullableTypeNode or JSDocNonNullableTypeNode or JSDocTypeLiteralNode
                    || item.Node.Kind == SyntaxKind.JSDocAllType)
                    await JsDocTypeGrammarAsync(item.Node, cancellation);
                else if (item.Node is ImportTypeNode import)
                {
                    ImportAttributeValues(import.Attributes);
                    InstantiationGrammar(import, import.TypeArguments);
                    await ImportTypeAsync(import, cancellation);
                    if (!import.IsTypeOf)
                        await TypeReferenceChecks.CheckAsync(import, cancellation);
                    await DeprecatedTypeAsync(import, cancellation);
                    await CheckImportAttributesAsync(import, import.Attributes, cancellation);
                }
                else if (item.Node is TypeLiteralNode literal)
                    await IndexDeclarationChecks.TypeLiteralAsync(literal, cancellation);
                else if (item.Node is IndexSignatureDeclarationNode index)
                    await CheckIndexSignatureSourceAsync(index, cancellation);
                else if (item.Node is TypePredicateNode predicate)
                    await CheckTypePredicateAsync(predicate, cancellation);
                if (item.Node is InferTypeNode)
                    await CheckInferTypeAsync((InferTypeNode)item.Node, cancellation);
                else if (item.Node is FunctionTypeNode or ConstructorTypeNode or MethodSignatureDeclarationNode
                    or CallSignatureDeclarationNode or ConstructSignatureDeclarationNode)
                {
                    await FunctionDeclarations.GrammarAsync(item.Node, cancellation).ConfigureAwait(false);
                    await FunctionDeclarations.CheckAsync(item.Node, cancellation).ConfigureAwait(false);
                    if (item.Node is MethodSignatureDeclarationNode)
                    {
                        await CheckMethodNameAsync(item.Node, cancellation);
                        await CheckFunctionOverloadsAsync(item.Node, cancellation);
                    }
                }
            }
            else if (item.Node is PropertySignatureDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode)
                await CheckSourceElementAsync(item.Node, cancellation);
            else
            {
                pending.Push((item.Node, true));
                for (int i = item.Node.ChildCount - 1; i >= 0; i--)
                {
                    var child = item.Node.GetChild(i);
                    if (item.Node is ImportTypeNode import && child != import.Argument)
                        continue;
                    if (item.Node is not MappedTypeNode mapped || mapped.Members?.Contains(child) != true)
                        pending.Push((child, false));
                }
            }
        }
        CurrentSourceNode = node;
        return await Nodes.FromNodeAsync(node, cancellation);
    }

    public void TopLevelAwait(SyntaxNode node)
        =>
            TopLevelAwait(
                node,
                DiagnosticCode.XAwaitExpressionsAreOnlyAllowedAtTheTopLevelOfAFileWhenThatFileIsAModuleButThisFileHasNoImportsOrExportsConsiderAddingAnEmptyExportToMakeThisFileAModule,
                DiagnosticCode.TopLevelAwaitExpressionsAreOnlyAllowedWhenTheModuleOptionIsSetToEs2022EsnextSystemNode16Node18Node20NodenextOrPreserveAndTheTargetOptionIsSetToEs2017OrHigher);

    private bool TopLevelAwait(SyntaxNode node, DiagnosticCode moduleRequired, DiagnosticCode invalidMode)
    {
        var file = SemanticSyntax.Source(node)!;
        var options = program.Symbols.Program.Configuration.Options;
        bool invalid = program.Symbols.Binding(file)?.IsModule != true
            && options.EmitModuleDetectionKind != ModuleDetectionKind.Force;
        if (invalid)
            ErrorOnFirstToken(node, moduleRequired);
        ModuleOptionKind module = options.EmitModule;
        bool nodeModule = module is ModuleOptionKind.Node16 or ModuleOptionKind.Node18 or ModuleOptionKind.Node20 or ModuleOptionKind.NodeNext;
        if (nodeModule && program.Symbols.Program.SourceFiles.First(f => f.Syntax == file).ImpliedFormat == ReferenceResolutionMode.Require)
        {
            ErrorOnFirstToken(node, DiagnosticCode.TheCurrentFileIsACommonJSModuleAndCannotUseAwaitAtTheTopLevel);
            invalid = true;
        }
        else if (TargetYear < 2017 || !nodeModule && module is not (ModuleOptionKind.ES2022 or ModuleOptionKind.ESNext
            or ModuleOptionKind.Preserve or ModuleOptionKind.System))
        {
            ErrorOnFirstToken(node, invalidMode);
            invalid = true;
        }
        return invalid;
    }

    internal HashSet<SyntaxNode> UnusedIdentifierScopes { get; } = [];
    internal List<BindingElementNode> RenamedBindingElements { get; } = [];
    internal Action<SyntaxNode>? BeforeFunctionDeclaration { get; set; }
    public Type AnyReadonlyArray => program.Globals.AnyReadonlyArrayType!;

    public async ValueTask<Type> CachedExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (mode != 0)
            return await Expressions.CheckAsync(node, mode, cancellation);
        var data = links.TypeNodes.Get(node);
        if (data.ResolvedType is { } type)
            return type;
        type = await FlowTypes.StableAsync((Expressions, node, cancellation),
            static state => state.Expressions.CheckAsync(state.node, cancellation: state.cancellation), cancellation);
        cancellation.ThrowIfCancellationRequested();
        Functions.RecordExpressionCache(node, type);
        return data.ResolvedType = type;
    }

    public async ValueTask<Type> PromiseResultAsync(SyntaxNode node, Type type, bool reportMissing, CancellationToken cancellation)
    {
        var target = await program.Globals.GetAsync(Utf8Literals.Promise, 1, true, cancellation);
        Type result = context.UnknownType;
        if (target != context.EmptyGenericType)
        {
            type = await Awaited.GetAsync(
                await Awaited.UnwrapAsync(type, cancellation),
                false,
                cancellation: cancellation) ?? context.UnknownType;
            result = context.CreateTypeReference((InterfaceType)target, [type]);
        }
        if (reportMissing)
        {
            if (result == context.UnknownType)
            {
                Error(
                    node,
                    DiagnosticCode.AnAsyncFunctionOrMethodMustReturnAPromiseMakeSureYouHaveADeclarationForPromiseOrIncludeES2015InYourLibOption);
                return context.ErrorType;
            }
            if (program.Symbols.Lookup(program.Symbols.Globals, Utf8Literals.Promise, SymbolFlags.Value) is null)
                Error(
                    node,
                    DiagnosticCode.AnAsyncFunctionOrMethodInES5RequiresThePromiseConstructorMakeSureYouHaveADeclarationForThePromiseConstructorOrIncludeES2015InYourLibOption);
        }
        return result;
    }

    public async ValueTask<Type?> UnwrapReturnAsync(SyntaxNode node, Type type, CancellationToken cancellation)
    {
        if (SemanticSyntax.Generator(node))
        {
            var result = (await Iterators.GeneratorAsync(
                type,
                SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword),
                cancellation)).Return;
            if (result is null)
                return context.ErrorType;
            return SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword)
                ? await Awaited.GetAsync(await Awaited.UnwrapAsync(result, cancellation), false, cancellation: cancellation) : result;
        }
        return SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword)
            ? await Awaited.GetAsync(type, false, cancellation: cancellation) ?? context.ErrorType : type;
    }

    public async ValueTask<Type?> InvokedParameterAsync(
        ParameterDeclarationNode node,
        CallExpressionNode call,
        CancellationToken cancellation)
    {
        var arguments = await CallArguments.EffectiveAsync(call, cancellation);
        int index = ((IFunctionSignature)node.Parent!).Parameters!.IndexOf(node);
        if (node.DotDotDotToken is not null)
            return await CallArguments.SpreadAsync(arguments, index, context.AnyType, cancellation: cancellation);
        var data = links.Signatures.Get(call);
        var cached = data.ResolvedSignature;
        data.ResolvedSignature = CallSignatures.Any;
        try
        {
            return index < arguments.Count ? await Widening.LiteralAsync(
                await Expressions.CheckAsync(arguments[index], cancellation: cancellation),
                cancellation)
                : node.Initializer is not null ? null : context.UndefinedWideningType;
        }
        finally
        {
            if (data.ResolvedSignature == CallSignatures.Any)
                data.ResolvedSignature = cached;
        }
    }

    public async ValueTask<Type?> GeneratorContextReturnAsync(SyntaxNode node, Type type, CancellationToken cancellation) =>
        await Generators.ContextReturnAsync(type, SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword), cancellation);

    public ValueTask<(IReadOnlyList<Type> Yield, IReadOnlyList<Type> Next)> YieldTypesAsync(
        SyntaxNode node,
        CheckMode mode,
        CancellationToken cancellation) =>
        Generators.AggregateAsync(node, mode, cancellation);

    public async ValueTask<Type> GeneratorResultAsync(SyntaxNode node, Type yield, Type result, Type? next, CancellationToken cancellation) =>
        await Generators.CreateAsync(yield, result, next ?? await Generators.ContextualAsync(node, IterationTypeKind.Next, cancellation),
            SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword), cancellation);

    public ValueTask<Type> WidenIterationAsync(SyntaxNode node, Type type, Type? contextual, int kind, CancellationToken cancellation) =>
        Generators.WidenAsync(
            type,
            contextual,
            (IterationTypeKind)kind,
            SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword),
            cancellation);

    public ValueTask ReportReturnWideningAsync(SyntaxNode node, Type type, WideningKind kind, CancellationToken cancellation) =>
        FunctionWidening.ReportAsync(node, type, kind, cancellation);

    public async ValueTask<Type?> ContextualIterationAsync(SyntaxNode node, Type type, WideningKind kind, CancellationToken cancellation) =>
        (await Iterators.GeneratorAsync(type, SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword), cancellation)).Get(
            kind == WideningKind.GeneratorYield
                ? IterationTypeKind.Yield
                : kind == WideningKind.GeneratorNext ? IterationTypeKind.Next : IterationTypeKind.Return);

    public ValueTask CheckFunctionDeclarationAsync(SyntaxNode node, CancellationToken cancellation)
    {
        BeforeFunctionDeclaration?.Invoke(node);
        cancellation.ThrowIfCancellationRequested();
        return FunctionDeclarations.CheckAsync(node, cancellation);
    }

    public async ValueTask FunctionGrammarAsync(SyntaxNode node, CancellationToken cancellation)
    {
        await FunctionDeclarations.GrammarAsync(node, cancellation);
    }

    private void GeneratorGrammar(SyntaxNode node)
    {
        var star = node switch
        {
            FunctionDeclarationNode declaration => declaration.AsteriskToken,
            FunctionExpressionNode expression => expression.AsteriskToken,
            MethodDeclarationNode method => method.AsteriskToken,
            _ => null
        };
        if (star is null || SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        if ((node.Flags & NodeFlags.Ambient) != 0)
            Error(star, DiagnosticCode.GeneratorsAreNotAllowedInAnAmbientContext);
        else if (SemanticSyntax.Body(node) is null)
            Error(star, DiagnosticCode.AnOverloadSignatureCannotBeDeclaredAsAGenerator);
    }

    public void RegisterUnused(SyntaxNode node) => UnusedIdentifierScopes.Add(node);

    public ValueTask FunctionNameAsync(SyntaxNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        CheckDeclarationName(node);
        return ValueTask.CompletedTask;
    }

    public async ValueTask SignatureEnvironmentAsync(SyntaxNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        await CheckUnmatchedDocumentationParametersAsync(node, cancellation).ConfigureAwait(false);
        if (SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword))
        {
            if (SemanticSyntax.Generator(node) && TargetYear < 2018)
                await ExternalHelpersAsync(node, [Utf8Literals.Await, Utf8Literals.AsyncGenerator], cancellation);
            else if (!SemanticSyntax.Generator(node) && TargetYear < 2017)
                await ExternalHelpersAsync(node, [Utf8Literals.Awaiter], cancellation);
        }
    }

    public async ValueTask CheckFunctionReturnAsync(SyntaxNode node, SyntaxNode annotation, Type type, CancellationToken cancellation)
    {
        if (SemanticSyntax.Generator(node))
        {
            if (SemanticSyntax.Body(node) is null)
                return;
            if (type == context.VoidType)
                Error(annotation, DiagnosticCode.AGeneratorCannotHaveAVoidTypeAnnotation);
            else
                await Generators.AssignableReturnAsync(
                    type,
                    SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword),
                    cancellation,
                    annotation);
            return;
        }
        if (!SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword) || type == context.ErrorType
            || (type.Flags & TypeFlags.Any) != 0 && type.Alias is not null)
            return;
        var promise = await program.Globals.GetAsync(Utf8Literals.Promise, 1, true, cancellation);
        if (promise != context.EmptyGenericType && !(type is TypeReference reference && reference.Target == promise))
        {
            var awaited = await Awaited.GetAsync(type, false, cancellation: cancellation) ?? context.VoidType;
            Error(
                annotation,
                DiagnosticCode.TheReturnTypeOfAnAsyncFunctionOrMethodMustBeTheGlobalPromiseTTypeDidYouMeanToWritePromise0,
                await TypeDisplay.GetAsync(awaited, cancellation));
            return;
        }
        await Awaited.GetAsync(
            type,
            false,
            node,
            DiagnosticCode.TheReturnTypeOfAnAsyncFunctionMustEitherBeAValidPromiseOrMustNotContainACallableThenMember,
            cancellation);
    }

    public ValueTask ParameterEnvironmentAsync(ParameterDeclarationNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (node.Modifiers is { Count: > 0 })
        {
            ClassMemberModifiers(node);
            if (ParameterProperty(node))
            {
                if (ErasableSyntaxOnly && (node.Flags & NodeFlags.JavaScriptFile) == 0)
                    Error(node, DiagnosticCode.ThisSyntaxIsNotAllowedWhenErasableSyntaxOnlyIsEnabled);
                if (node.Parent is ConstructorDeclarationNode && node.Name is IdentifierNode { Text.Span: var matchedText } && matchedText.SequenceEqual("constructor"u8))
                    Error(node.Name, DiagnosticCode.XConstructorCannotBeUsedAsAParameterPropertyName);
                if (node.Parent is not ConstructorDeclarationNode { Body: not null })
                    Error(node, DiagnosticCode.AParameterPropertyIsOnlyAllowedInAConstructorImplementation);
                if (node.Name is BindingPatternNode)
                    Error(node, DiagnosticCode.AParameterPropertyMayNotBeDeclaredUsingABindingPattern);
                if (node.DotDotDotToken is not null)
                    Error(node, DiagnosticCode.AParameterPropertyCannotBeDeclaredUsingARestParameter);
            }
        }
        CheckDeclarationName(node);
        return CheckDecoratorsAsync(node, cancellation);
    }

    public async ValueTask<bool> BindingEnvironmentAsync(BindingElementNode node, CancellationToken cancellation)
    {
        if (node.DotDotDotToken is not null && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
        {
            var elements = ((BindingPatternNode)node.Parent!).Elements!;
            if (elements[^1] != node)
                Error(node, DiagnosticCode.ARestElementMustBeLastInADestructuringPattern);
            else
            {
                DestructuringTrailingComma(elements, node);
                if (node.PropertyName is not null)
                    Error(node.Name!, DiagnosticCode.ARestElementCannotHaveAPropertyName);
                else if (node.Initializer is not null)
                {
                    var (start, end) = CheckerDiagnostic.TokenRange(SemanticSyntax.Source(node)!, node.Name!.End);
                    Error(node, CheckerDiagnostic.Create(node, Messages.A_rest_element_cannot_have_an_initializer) with
                    { Start = start, Length = end - start });
                }
            }
        }
        if (node.PropertyName is PrivateIdentifierNode && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            Error(node.PropertyName, DiagnosticCode.PrivateIdentifiersCannotBeUsedInDestructuringPatterns);
        if (node.PropertyName is not null && node.Name is IdentifierNode
            && SemanticSyntax.RootDeclaration(node) is ParameterDeclarationNode
            && SemanticSyntax.Body(SemanticSyntax.RootDeclaration(node).Parent!) is null)
        {
            RenamedBindingElements.Add(node);
            return true;
        }
        if (node.DotDotDotToken is not null && node.Parent!.Kind == SyntaxKind.ObjectBindingPattern && TargetYear < 2018)
            await ExternalHelpersAsync(node, [Utf8Literals.Rest], cancellation);
        if (node.PropertyName is ComputedPropertyNameNode computed)
            await ObjectLiterals.ComputedAsync(computed, cancellation);
        return false;
    }

    public async ValueTask<bool> VariableAliasAsync(SyntaxNode node, Symbol symbol, CancellationToken cancellation)
    {
        var declaration = node is BindingElementNode ? node.Parent?.Parent : node;
        if ((symbol.Flags & SymbolFlags.Alias) == 0 || (node.Flags & NodeFlags.JavaScriptFile) == 0
            || declaration is not VariableDeclarationNode { Type: null, Initializer: CallExpressionNode call }
            || !SemanticSyntax.RequireCall(call)
            || call.Arguments is not { Count: 1 } arguments
            || arguments[0] is not (StringLiteralNode or NoSubstitutionTemplateLiteralNode)
            || SemanticSyntax.HasModifier(declaration.Parent!.Parent!, SyntaxKind.ExportKeyword))
            return false;
        await CheckAliasSourceAsync(node, cancellation);
        return true;
    }

    public async ValueTask NonNullBindingAsync(Type type, SyntaxNode node, CancellationToken cancellation)
    {
        var nonNull = await ExpressionChecks.NonNullAsync(type, node, cancellation);
        if ((nonNull.Flags & TypeFlags.Void) != 0)
            Error(node, DiagnosticCode.ObjectIsPossiblyUndefined);
    }

    public ValueTask<bool> FunctionModifiersAsync(SyntaxNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        GeneratorGrammar(node);
        return ValueTask.FromResult(DeclarationModifiers(node));
    }

    public ValueTask TypeParameterModifiersAsync(TypeParameterDeclarationNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        TypeParameterGrammar(node);
        return ValueTask.CompletedTask;
    }
}

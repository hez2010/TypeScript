using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IFlowTypeHost, IFlowReferenceHost, IFlowNarrowingHost, IFlowEffectHost, IExplicitValueHost
{
    internal FlowTypes FlowTypes { get; }
    internal FlowReferences FlowReferences { get; }
    internal FlowNarrowing FlowNarrowing { get; }
    internal FlowEffects FlowEffects { get; }
    internal ExplicitValueTypes ExplicitValues { get; }
    internal AssignmentMarks Assignments { get; }
    internal Action<SyntaxNode>? BeforeFlowExpression { get; set; }

    public FlowNode? FlowOf(SyntaxNode node) => Bindings.SyntheticFlow(node) ?? program.Symbols.Binding(node)?.Get(node)?.Flow;

    public Symbol UnknownSymbol => program.Symbols.UnknownSymbol;

    public Symbol ResolveReference(SyntaxNode node, CancellationToken cancellation)
    {
        if (node is IdentifierNode identifier)
            return program.ReferenceSymbols.Resolve(identifier, cancellation);
        if (node is not PrivateIdentifierNode name)
            throw new ArgumentException("Expected a reference name", nameof(node));
        for (var owner = PrivateAccess.ContainingClass(node); owner is not null; owner = PrivateAccess.ContainingClass(owner))
        {
            cancellation.ThrowIfCancellationRequested();
            var symbol = program.Symbols.Declaration(owner)!;
            string key = TypeScript.Compiler.Checking.PrivateAccess.Name(symbol, name.Text);
            if ((symbol.Members.GetValueOrDefault(key) ?? symbol.Exports.GetValueOrDefault(key)) is { } found)
                return found;
        }
        return program.Symbols.UnknownSymbol;
    }

    public Symbol? DeclarationSymbol(SyntaxNode node) => program.Symbols.Declaration(node);

    public Symbol? ExportedSymbol(Symbol symbol) => program.Symbols.ExportedValue(symbol);

    public ValueTask<bool> MatchesAsync(SyntaxNode source, SyntaxNode target, CancellationToken cancellation) =>
        FlowReferences.MatchesAsync(source, target, cancellation);

    public ValueTask<bool> ContainsAsync(SyntaxNode source, SyntaxNode target, bool optionalChain, CancellationToken cancellation) =>
        FlowReferences.ContainsAsync(source, target, optionalChain, cancellation);

    public ValueTask<string?> ReferenceKeyAsync(FlowState state, CancellationToken cancellation) =>
        FlowReferences.KeyAsync(state, cancellation);

    public ValueTask<Type> NarrowAsync(FlowState state, Type type, SyntaxNode expression, bool assumeTrue, CancellationToken cancellation) =>
        FlowNarrowing.NarrowAsync(state, type, expression, assumeTrue, cancellation);

    public ValueTask<Type> NarrowAssertionAsync(FlowState state, Type type, SyntaxNode expression, CancellationToken cancellation) =>
        FlowNarrowing.AssertionAsync(state, type, expression, cancellation);

    public ValueTask<Signature?> EffectsAsync(SyntaxNode node, CancellationToken cancellation) => FlowEffects.GetAsync(node, cancellation);

    public ValueTask<TypePredicate?> PredicateAsync(Signature signature, CancellationToken cancellation) =>
        Signatures.PredicateAsync(signature, cancellation);

    public ValueTask<Type> ReturnTypeAsync(Signature signature, CancellationToken cancellation) =>
        Signatures.ReturnAsync(signature, cancellation);

    public void FlowControlError(SyntaxNode reference) => Error(reference, 2563);

    public ValueTask<Type> ArrayAsync(Type element, CancellationToken cancellation) =>
        ValueTask.FromResult<Type>(context.CreateTypeReference((InterfaceType)Instantiation.ArrayTarget(false), [element]));

    public bool ConstLike(SyntaxNode node) => AssignmentMarks.ConstLike(node);

    public async ValueTask<Type> ExpressionAsync(SyntaxNode node, CancellationToken cancellation)
    {
        BeforeFlowExpression?.Invoke(node);
        cancellation.ThrowIfCancellationRequested();
        return await FlowTypes.ExpressionAsync(node, () => Expressions.CheckAsync(node, CheckMode.TypeOnly, cancellation), cancellation);
    }

    public async ValueTask<Type> CachedFlowExpressionAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var data = links.TypeNodes.Get(node);
        if (data.ResolvedType is { } cached)
            return cached;
        var result = await FlowTypes.StableAsync(() => ExpressionAsync(node, cancellation), cancellation);
        cancellation.ThrowIfCancellationRequested();
        return data.ResolvedType = result;
    }

    public ValueTask<Type> ContextFreeExpressionAsync(SyntaxNode node, CancellationToken cancellation)
        => Contexts.ContextFreeAsync(node, cancellation);

    public ValueTask<Type> RegularObjectLiteralAsync(Type type, CancellationToken cancellation)
        => ObjectLiterals.RegularAsync(type, cancellation);

    public async ValueTask<Type> InitialOrAssignedAsync(SyntaxNode node, SyntaxNode reference, CancellationToken cancellation)
    {
        Type type;
        if (node is VariableDeclarationNode variable)
        {
            if (variable.Initializer is { } initializer)
                type = links.TypeNodes.Get(initializer).ResolvedType ?? await ExpressionAsync(initializer, cancellation);
            else if (variable.Parent?.Parent?.Kind == SyntaxKind.ForInStatement)
                type = context.StringType;
            else if (variable.Parent?.Parent?.Kind == SyntaxKind.ForOfStatement)
                type = await ForOfElementAsync((ForInOrOfStatementNode)variable.Parent.Parent, cancellation);
            else
                type = context.ErrorType;
        }
        else if (node is BindingElementNode binding)
            type = await Bindings.InitialAsync(binding, cancellation);
        else
            type = await AssignedTypeAsync(node, cancellation);
        return await ReferenceNarrowing.GetAsync(type, reference, 0, cancellation);
    }

    public ValueTask<Type?> DottedTypeAsync(SyntaxNode node, CancellationToken cancellation) =>
        ExplicitValues.DottedAsync(node, cancellation: cancellation);

    public async ValueTask<Type?> ExplicitThisAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var container = MissingNamePrefixes.ThisContainer(node, false, false);
        if (container is IFunctionSignature
            && (await Signatures.FromDeclarationAsync(container, cancellation).ConfigureAwait(false)).ThisParameter is { } parameter)
            return await ExplicitValues.SymbolAsync(parameter, cancellation: cancellation).ConfigureAwait(false);
        if (SemanticSyntax.ClassLike(container.Parent))
        {
            var symbol = program.Symbols.Declaration(container.Parent!)!;
            return SemanticSyntax.IsStatic(container) ? await Values.GetAsync(symbol, cancellation).ConfigureAwait(false)
                : ((InterfaceType)await Declared.GetAsync(symbol, cancellation).ConfigureAwait(false)).ThisType;
        }
        return null;
    }

    public ValueTask<Type> SuperAsync(SyntaxNode node, CancellationToken cancellation) => ThisExpressions.SuperAsync(node, cancellation);

    public ValueTask<Type?> IteratedTypeAsync(ForInOrOfStatementNode node, Type expression, CancellationToken cancellation) =>
        Iteration.TryAsync(node.AwaitModifier is null ? IterationUse.ForOf : IterationUse.ForAwaitOf,
            expression, context.UndefinedType, cancellation: cancellation);

    public string PrivatePropertyName(Symbol symbol, PrivateIdentifierNode name) => PrivateAccess.Name(symbol, name.Text);

    private SyntaxNode? explicitAnnotationError;
    internal Dictionary<SyntaxNode, List<(Symbol Symbol, SyntaxNode Declaration)>> AssertionRelatedDeclarations { get; } = [];

    public void MissingExplicitAnnotation(Symbol symbol, SyntaxNode declaration)
    {
        var target = explicitAnnotationError ?? throw new InvalidOperationException("Missing assertion diagnostic context");
        if (!AssertionRelatedDeclarations.TryGetValue(target, out var related))
            AssertionRelatedDeclarations[target] = related = [];
        if (!related.Contains((symbol, declaration)))
            related.Add((symbol, declaration));
    }

    public async ValueTask<Type> NonNullExpressionAsync(SyntaxNode node, CancellationToken cancellation)
        => await NonNullAsync(await ExpressionAsync(node, cancellation), node, cancellation);

    public ValueTask<IReadOnlyList<Signature>> CallSignaturesAsync(Type type, CancellationToken cancellation) =>
        SignaturesAsync(type, false, cancellation);

    public async ValueTask<Type> OptionalCallTargetAsync(SyntaxNode node, CancellationToken cancellation) =>
        await Optional.ReceiverAsync(
            await Expressions.CheckAsync(node, cancellation: cancellation),
            node,
            cancellation);

    public async ValueTask<Type?> HasInstanceMethodAsync(Type type, CancellationToken cancellation)
    {
        string name = await KnownSymbolNameAsync("hasInstance", cancellation);
        if (await AllAssignableKindAsync(type, TypeFlags.NonPrimitive, cancellation)
            && await Properties.PropertyAsync(type, name, cancellation: cancellation) is { } property)
        {
            var method = await Values.GetAsync(property, cancellation);
            if ((await SignaturesAsync(method, false, cancellation)).Count != 0)
                return method;
        }
        return null;
    }

    public async ValueTask<Signature?> ResolvedCallAsync(SyntaxNode node, CancellationToken cancellation) =>
        await CallResolution.GetAsync(node, cancellation: cancellation);

    public async ValueTask<bool> ConstantOrUnassignedAsync(Symbol symbol, CancellationToken cancellation)
        =>
            AssignmentMarks.Constant(symbol)
                || Assignments.ParameterOrMutableLocal(symbol) && !await Assignments.AssignedAsync(symbol, cancellation);

    public async ValueTask<SyntaxNode?> AliasedConditionAsync(
        SyntaxNode reference,
        IdentifierNode expression,
        CancellationToken cancellation)
    {
        var symbol = program.ReferenceSymbols.Resolve(expression, cancellation);
        if (AssignmentMarks.Constant(symbol)
            && symbol.ValueDeclaration is VariableDeclarationNode { Type: null, Initializer: { } initializer })
        {
            if (await ConstantReferenceAsync(reference, cancellation))
                return initializer;
        }
        return null;
    }

    public async ValueTask<bool> ConstantReferenceAsync(SyntaxNode node, CancellationToken cancellation)
    {
        while (node is PropertyAccessExpressionNode or ElementAccessExpressionNode)
        {
            cancellation.ThrowIfCancellationRequested();
            if (links.SymbolNodes.TryGet(node)?.ResolvedSymbol is not { } property || !IsReadonly(property))
                return false;
            node = FlowReferences.Receiver(node)!;
        }
        if (node.Kind == SyntaxKind.ThisKeyword)
            return true;
        if (node is IdentifierNode identifier && !FlowReferences.ThisInQuery(identifier))
        {
            var symbol = program.ReferenceSymbols.Resolve(identifier, cancellation);
            return await ConstantOrUnassignedAsync(symbol, cancellation) || symbol.ValueDeclaration is FunctionExpressionNode;
        }
        if (node is BindingPatternNode)
        {
            var declaration = node.Parent!;
            while (declaration is BindingElementNode)
                declaration = declaration.Parent!.Parent!;
            if (declaration is ParameterDeclarationNode || declaration is VariableDeclarationNode { Parent: CatchClauseNode })
                return !await Assignments.SomeAsync(declaration, cancellation);
            return declaration is VariableDeclarationNode && AssignmentMarks.ConstLike(declaration);
        }
        return false;
    }

    public ValueTask<string?> AccessNameAsync(SyntaxNode node, CancellationToken cancellation) => AccessNames.GetAsync(node, cancellation);

    public async ValueTask<Type?> FlowPropertyTypeAsync(Type type, string name, bool includeIndex, CancellationToken cancellation)
    {
        if (await Properties.PropertyAsync(type, name, cancellation: cancellation) is { } property)
            return await Values.GetAsync(property, cancellation);
        if (includeIndex && await ApplicableIndexAsync(type, name, cancellation) is { } index)
            return context.StrictNullChecks
                ? await Algebra.UnionAsync([index.ValueType, context.UndefinedOrMissingType], cancellation: cancellation)
                : index.ValueType;
        return null;
    }

    public async ValueTask<Type> ConstructorNarrowAsync(
        Type type,
        SyntaxKind op,
        SyntaxNode expression,
        bool assumeTrue,
        CancellationToken cancellation)
    {
        if (assumeTrue && op is not (SyntaxKind.EqualsEqualsToken or SyntaxKind.EqualsEqualsEqualsToken)
            || !assumeTrue && op is not (SyntaxKind.ExclamationEqualsToken or SyntaxKind.ExclamationEqualsEqualsToken))
            return type;
        var constructor = await ExpressionAsync(expression, cancellation).ConfigureAwait(false);
        bool callable = (constructor.Flags & TypeFlags.Object) != 0 && (await SignaturesAsync(constructor, false, cancellation)).Count != 0
            || (await SignaturesAsync(constructor, true, cancellation)).Count != 0;
        if (!callable && (constructor.Flags & TypeFlags.TypeVariable) != 0
            && await Instantiation.Constraints.BaseConstraintAsync(constructor, cancellation) is { } constraint)
            callable = await Composites.MixinAsync(await SignaturesAsync(constraint, true, cancellation), cancellation);
        if (!callable || await Properties.PropertyAsync(constructor, "prototype", cancellation: cancellation) is not { } prototype)
            return type;
        var candidate = await Values.GetAsync(prototype, cancellation).ConfigureAwait(false);
        if ((candidate.Flags & TypeFlags.Any) != 0 || candidate == GlobalObject || candidate == GlobalFunction)
            return type;
        if ((type.Flags & TypeFlags.Any) != 0)
            return candidate;
        return await Algebra.FilterAsync(type, async part =>
            (part.Flags & TypeFlags.Object) != 0 && (part.ObjectFlags & ObjectFlags.Class) != 0
                || (candidate.Flags & TypeFlags.Object) != 0 && (candidate.ObjectFlags & ObjectFlags.Class) != 0
                ? part.Symbol == candidate.Symbol
                : await Relations.RelatedAsync(part, candidate, RelationKind.Subtype, cancellation), cancellation);
    }

    public ValueTask<Type> OtherBinaryAsync(
        FlowState state,
        Type type,
        BinaryExpressionNode expression,
        bool assumeTrue,
        CancellationToken cancellation)
            => NarrowRelationalKeywordAsync(state, type, expression, assumeTrue, cancellation);

    public ValueTask<Type> NarrowPredicateAsync(
        FlowState state,
        Type type,
        TypePredicate predicate,
        SyntaxNode call,
        CancellationToken cancellation)
            => FlowNarrowing.PredicateNarrowAsync(state, type, predicate, call, true, cancellation);

    public ValueTask<Type> NarrowSwitchAsync(FlowState state, Type type, FlowNode clause, CancellationToken cancellation)
            => FlowNarrowing.SwitchAsync(state, type, clause, cancellation);

    public ValueTask<bool> ExhaustiveAsync(SyntaxNode statement, CancellationToken cancellation)
            => FlowNarrowing.ExhaustiveAsync((SwitchStatementNode)statement, cancellation);
}

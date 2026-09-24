using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal sealed partial class ProgramTypeHost : IFlowTypeHost, IFlowReferenceHost, IFlowNarrowingHost, IFlowEffectHost, IExplicitValueHost
{
    internal FlowTypes FlowTypes { get; }
    internal FlowReferences FlowReferences { get; }
    internal FlowNarrowing FlowNarrowing { get; }
    internal FlowEffects FlowEffects { get; }
    internal ExplicitValueTypes ExplicitValues { get; }
    internal AssignmentMarks Assignments { get; }
    internal Action<SyntaxNode>? BeforeFlowExpression { get; set; }

    public FlowNode? FlowOf(SyntaxNode node) => program.Symbols.Binding(node)?.Get(node)?.Flow;

    public Symbol UnknownSymbol => program.Symbols.UnknownSymbol;

    public Symbol ResolveReference(SyntaxNode node, CancellationToken cancellation) => node is IdentifierNode identifier
            ? program.ReferenceSymbols.Resolve(
                identifier,
                cancellation) : throw new InvalidOperationException("Probe requires private reference resolution");

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
    {
        if (node is IdentifierNode or NumericLiteralNode or StringLiteralNode or BigIntLiteralNode or NoSubstitutionTemplateLiteralNode
            || node.Kind is SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword or SyntaxKind.NullKeyword)
            return ExpressionAsync(node, cancellation);
        throw new InvalidOperationException("Probe requires context-free expression typing");
    }

    public ValueTask<Type> RegularObjectLiteralAsync(Type type, CancellationToken cancellation)
        => (type.ObjectFlags & ObjectFlags.FreshLiteral) == 0 ? ValueTask.FromResult(type)
            : throw new InvalidOperationException("Probe requires regular object-literal types");

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
                throw new InvalidOperationException("Probe requires iteration element types");
            else
                type = context.ErrorType;
        }
        else if (node is BindingElementNode)
            throw new InvalidOperationException("Probe requires binding initializer types");
        else if (node.Parent is BinaryExpressionNode binary)
        {
            if (binary.Parent is ArrayLiteralExpressionNode or PropertyAssignmentNode)
                throw new InvalidOperationException("Probe requires destructuring assignment defaults");
            type = await ExpressionAsync(binary.Right!, cancellation);
        }
        else if (node.Parent?.Kind == SyntaxKind.ForInStatement)
            type = context.StringType;
        else if (node.Parent is DeleteExpressionNode)
            type = context.UndefinedType;
        else
            throw new InvalidOperationException("Probe requires assigned/destructured value types");
        return await ReferenceNarrowing.GetAsync(type, reference, 0, cancellation);
    }

    public ValueTask<Type?> DottedTypeAsync(SyntaxNode node, CancellationToken cancellation) =>
        ExplicitValues.DottedAsync(node, cancellation: cancellation);

    public ValueTask<Type?> ExplicitThisAsync(SyntaxNode node, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires explicit this type");

    public ValueTask<Type> SuperAsync(SyntaxNode node, CancellationToken cancellation) => ThisExpressions.SuperAsync(node, cancellation);

    public ValueTask<Type?> IteratedTypeAsync(ForInOrOfStatementNode node, Type expression, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires iteration type");

    public string PrivatePropertyName(Symbol symbol, PrivateIdentifierNode name) => PrivateAccess.Name(symbol, name.Text);

    public void MissingExplicitAnnotation(Symbol symbol, SyntaxNode declaration) =>
        throw new InvalidOperationException("Probe requires explicit annotation related information");

    public async ValueTask<Type> NonNullExpressionAsync(SyntaxNode node, CancellationToken cancellation)
        => await NonNullAsync(await ExpressionAsync(node, cancellation), node, cancellation);

    public ValueTask<IReadOnlyList<Signature>> CallSignaturesAsync(Type type, CancellationToken cancellation) =>
        SignaturesAsync(type, false, cancellation);

    public ValueTask<Type> OptionalCallTargetAsync(SyntaxNode node, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires optional call target typing");

    public ValueTask<Type?> HasInstanceMethodAsync(Type type, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires Symbol.hasInstance lookup");

    public ValueTask<Signature?> ResolvedCallAsync(SyntaxNode node, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires call overload selection");

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
            if (reference.Kind == SyntaxKind.ThisKeyword)
                return initializer;
            if (reference is IdentifierNode identifier && !FlowReferences.ThisInQuery(identifier))
            {
                var target = program.ReferenceSymbols.Resolve(identifier, cancellation);
                if (await ConstantOrUnassignedAsync(target, cancellation) || target.ValueDeclaration is FunctionExpressionNode)
                    return initializer;
                return null;
            }
            throw new InvalidOperationException("Probe requires readonly or binding-pattern reference analysis");
        }
        return null;
    }

    public ValueTask<string?> AccessNameAsync(SyntaxNode node, CancellationToken cancellation) => AccessNames.GetAsync(node, cancellation);

    public async ValueTask<Type?> FlowPropertyTypeAsync(Type type, string name, bool includeIndex, CancellationToken cancellation)
    {
        if (await Properties.PropertyAsync(type, name, cancellation: cancellation) is { } property)
            return await Values.GetAsync(property, cancellation);
        if (includeIndex && await ApplicableIndexAsync(type, context.GetStringLiteralType(name), cancellation) is { } index)
            return context.StrictNullChecks
                ? await Algebra.UnionAsync([index.ValueType, context.UndefinedOrMissingType], cancellation: cancellation)
                : index.ValueType;
        return null;
    }

    public ValueTask<Type> ConstructorNarrowAsync(
        Type type,
        SyntaxKind op,
        SyntaxNode expression,
        bool assumeTrue,
        CancellationToken cancellation)
            => throw new InvalidOperationException("Probe requires constructor identity narrowing");

    public ValueTask<Type> OtherBinaryAsync(
        FlowState state,
        Type type,
        BinaryExpressionNode expression,
        bool assumeTrue,
        CancellationToken cancellation)
            => throw new InvalidOperationException("Probe requires in/instanceof narrowing");

    public ValueTask<Type> NarrowPredicateAsync(
        FlowState state,
        Type type,
        TypePredicate predicate,
        SyntaxNode call,
        CancellationToken cancellation)
            => FlowNarrowing.PredicateNarrowAsync(state, type, predicate, call, true, cancellation);

    public ValueTask<bool> DerivedAsync(Type source, Type target, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires derived-type narrowing");

    public ValueTask<Type> NarrowSwitchAsync(FlowState state, Type type, FlowNode clause, CancellationToken cancellation)
            => FlowNarrowing.SwitchAsync(state, type, clause, cancellation);

    public ValueTask<bool> ExhaustiveAsync(SyntaxNode statement, CancellationToken cancellation)
            => FlowNarrowing.ExhaustiveAsync((SwitchStatementNode)statement, cancellation);
}

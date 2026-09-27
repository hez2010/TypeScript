using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IFlowNarrowingHost
{
    Type GlobalFunction { get; }
    Type GlobalObject { get; }

    ValueTask<bool> DerivedAsync(Type source, Type target, CancellationToken cancellation);

    ValueTask<Signature?> EffectsAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<TypePredicate?> PredicateAsync(Signature signature, CancellationToken cancellation);

    ValueTask<TextSlice?> AccessNameAsync(SyntaxNode node, CancellationToken cancellation);

    Symbol ResolveReference(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type?> FlowPropertyTypeAsync(Type type, TextSlice name, bool includeIndex, CancellationToken cancellation);

    ValueTask<Type> ConstructorNarrowAsync(Type type, SyntaxKind op, SyntaxNode expression, bool assumeTrue, CancellationToken cancellation);

    ValueTask<Type> ExpressionAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> CachedFlowExpressionAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<SyntaxNode?> AliasedConditionAsync(SyntaxNode reference, IdentifierNode expression, CancellationToken cancellation);

    ValueTask<Type> OtherBinaryAsync(
        FlowState state,
        Type type,
        BinaryExpressionNode expression,
        bool assumeTrue,
        CancellationToken cancellation);
}

internal sealed partial class FlowNarrowing(TypeContext context, TypeAlgebra algebra, TypeFactQueries facts, TypeRelations relations,
    TypeConstraints constraints, TypeViews views, TypePredicates predicates, CheckerSymbols symbols, FlowReferences references,
    DiscriminantRelations discriminants, FlowTypes flows, IFlowNarrowingHost host)
{
    private int inlineLevel;
    internal int InlineLevel => inlineLevel;

    internal async ValueTask<Type> NarrowAsync(
        FlowState state,
        Type type,
        SyntaxNode expression,
        bool assumeTrue,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (OptionalRoot(expression) || expression.Parent is BinaryExpressionNode
            {
                OperatorToken.Kind: SyntaxKind.QuestionQuestionToken
            or SyntaxKind.QuestionQuestionEqualsToken
            } coalesce
            && coalesce.Left == expression)
            return await OptionalityAsync(state, type, expression, assumeTrue, cancellation).ConfigureAwait(false);
        switch (expression)
        {
            case IdentifierNode identifier:
                if (!await references.MatchesAsync(state.Reference, expression, cancellation).ConfigureAwait(false) && inlineLevel < 5
                    && await host.AliasedConditionAsync(state.Reference, identifier, cancellation).ConfigureAwait(false) is { } initializer)
                {
                    inlineLevel++;
                    try
                    {
                        return await NarrowAsync(state, type, initializer, assumeTrue, cancellation).ConfigureAwait(false);
                    }
                    finally
                    {
                        inlineLevel--;
                    }
                }
                return await TruthinessAsync(state, type, expression, assumeTrue, cancellation).ConfigureAwait(false);
            case PropertyAccessExpressionNode or ElementAccessExpressionNode or { Kind: SyntaxKind.ThisKeyword or SyntaxKind.SuperKeyword }:
                return await TruthinessAsync(state, type, expression, assumeTrue, cancellation).ConfigureAwait(false);
            case CallExpressionNode call:
                return await CallAsync(state, type, call, assumeTrue, cancellation).ConfigureAwait(false);
            case ParenthesizedExpressionNode or NonNullExpressionNode or SatisfiesExpressionNode:
                return await NarrowAsync(state, type, FlowReferences.Receiver(expression)!, assumeTrue, cancellation).ConfigureAwait(false);
            case BinaryExpressionNode binary:
                return await BinaryAsync(state, type, binary, assumeTrue, cancellation).ConfigureAwait(false);
            case PrefixUnaryExpressionNode { Operator: SyntaxKind.ExclamationToken } prefix:
                return await NarrowAsync(state, type, prefix.Operand!, !assumeTrue, cancellation).ConfigureAwait(false);
            default:
                return type;
        }
    }

    private async ValueTask<Type> OptionalityAsync(
        FlowState state,
        Type type,
        SyntaxNode expression,
        bool present,
        CancellationToken cancellation)
    {
        var include = present ? TypeFacts.NEUndefinedOrNull : TypeFacts.EQUndefinedOrNull;
        return await references.MatchesAsync(state.Reference, expression, cancellation).ConfigureAwait(false)
            ? await facts.AdjustAsync(type, include, cancellation).ConfigureAwait(false)
            : await DiscriminantAsync(
                state,
                type,
                expression,
                t => facts.FilterAsync(t, include, cancellation),
                cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> TruthinessAsync(
        FlowState state,
        Type type,
        SyntaxNode expression,
        bool assumeTrue,
        CancellationToken cancellation)
    {
        var include = assumeTrue ? TypeFacts.Truthy : TypeFacts.Falsy;
        if (await references.MatchesAsync(state.Reference, expression, cancellation).ConfigureAwait(false))
            return await facts.AdjustAsync(type, include, cancellation).ConfigureAwait(false);
        if (context.StrictNullChecks
            && assumeTrue
            && await references.ContainsAsync(expression, state.Reference, true, cancellation).ConfigureAwait(false))
            type = await facts.AdjustAsync(type, TypeFacts.NEUndefinedOrNull, cancellation).ConfigureAwait(false);
        return await DiscriminantAsync(
            state,
            type,
            expression,
            t => facts.FilterAsync(t, include, cancellation),
            cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> BinaryAsync(
        FlowState state,
        Type type,
        BinaryExpressionNode node,
        bool assumeTrue,
        CancellationToken cancellation)
    {
        var op = node.OperatorToken!.Kind;
        switch (op)
        {
            case SyntaxKind.EqualsToken or SyntaxKind.BarBarEqualsToken or SyntaxKind.AmpersandAmpersandEqualsToken
                or SyntaxKind.QuestionQuestionEqualsToken:
                return await TruthinessAsync(
                    state,
                    await NarrowAsync(state, type, node.Right!, assumeTrue, cancellation).ConfigureAwait(false),
                    node.Left!, assumeTrue, cancellation).ConfigureAwait(false);
            case SyntaxKind.EqualsEqualsToken or SyntaxKind.ExclamationEqualsToken or SyntaxKind.EqualsEqualsEqualsToken
                or SyntaxKind.ExclamationEqualsEqualsToken:
                var left = FlowReferences.Candidate(node.Left!);
                var right = FlowReferences.Candidate(node.Right!);
                if (left is TypeOfExpressionNode leftTypeOf && StringLike(right) is { } rightText)
                    return await TypeofAsync(state, type, leftTypeOf, op, rightText, assumeTrue, cancellation).ConfigureAwait(false);
                if (right is TypeOfExpressionNode rightTypeOf && StringLike(left) is { } leftText)
                    return await TypeofAsync(state, type, rightTypeOf, op, leftText, assumeTrue, cancellation).ConfigureAwait(false);
                if (await references.MatchesAsync(state.Reference, left, cancellation).ConfigureAwait(false))
                    return await EqualityAsync(type, op, right, assumeTrue, cancellation).ConfigureAwait(false);
                if (await references.MatchesAsync(state.Reference, right, cancellation).ConfigureAwait(false))
                    return await EqualityAsync(type, op, left, assumeTrue, cancellation).ConfigureAwait(false);
                return await UnmatchedEqualityAsync(state, type, left, right, op, assumeTrue, cancellation).ConfigureAwait(false);
            case SyntaxKind.InstanceOfKeyword or SyntaxKind.InKeyword:
                return await host.OtherBinaryAsync(state, type, node, assumeTrue, cancellation).ConfigureAwait(false);
            case SyntaxKind.CommaToken:
                return await NarrowAsync(state, type, node.Right!, assumeTrue, cancellation).ConfigureAwait(false);
            case SyntaxKind.AmpersandAmpersandToken or SyntaxKind.BarBarToken:
                if (assumeTrue == (op == SyntaxKind.AmpersandAmpersandToken))
                    return await NarrowAsync(
                        state,
                        await NarrowAsync(state, type, node.Left!, assumeTrue, cancellation).ConfigureAwait(false),
                        node.Right!, assumeTrue, cancellation).ConfigureAwait(false);
                return await algebra.UnionAsync([await NarrowAsync(state, type, node.Left!, assumeTrue, cancellation).ConfigureAwait(false),
                    await NarrowAsync(state, type, node.Right!, assumeTrue, cancellation).ConfigureAwait(false)],
                    cancellation: cancellation).ConfigureAwait(false);
            default:
                return type;
        }
    }

    internal async ValueTask<Type> AssertionAsync(
        FlowState state,
        Type type,
        SyntaxNode expression,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        while (expression is ParenthesizedExpressionNode parentheses)
            expression = parentheses.Expression!;
        if (expression.Kind == SyntaxKind.FalseKeyword)
            return context.UnreachableNeverType;
        if (expression is BinaryExpressionNode binary)
        {
            if (binary.OperatorToken!.Kind == SyntaxKind.AmpersandAmpersandToken)
                return await AssertionAsync(
                    state,
                    await AssertionAsync(state, type, binary.Left!, cancellation).ConfigureAwait(false),
                    binary.Right!,
                    cancellation).ConfigureAwait(false);
            if (binary.OperatorToken.Kind == SyntaxKind.BarBarToken)
                return await algebra.UnionAsync([await AssertionAsync(state, type, binary.Left!, cancellation).ConfigureAwait(false),
                    await AssertionAsync(state, type, binary.Right!, cancellation).ConfigureAwait(false)],
                    cancellation: cancellation).ConfigureAwait(false);
        }
        return await NarrowAsync(state, type, expression, true, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> TypeofAsync(FlowState state, Type type, TypeOfExpressionNode expression, SyntaxKind op,
        TextSlice literal, bool assumeTrue, CancellationToken cancellation)
    {
        if (op is SyntaxKind.ExclamationEqualsToken or SyntaxKind.ExclamationEqualsEqualsToken)
            assumeTrue = !assumeTrue;
        var target = FlowReferences.Candidate(expression.Expression!);
        if (await references.MatchesAsync(state.Reference, target, cancellation).ConfigureAwait(false))
            return await TypeNameAsync(type, literal, assumeTrue, cancellation).ConfigureAwait(false);
        if (context.StrictNullChecks && await references.ContainsAsync(target, state.Reference, true, cancellation).ConfigureAwait(false)
            && assumeTrue == (literal != "undefined"))
            type = await facts.AdjustAsync(type, TypeFacts.NEUndefinedOrNull, cancellation).ConfigureAwait(false);
        return await DiscriminantAsync(
            state,
            type,
            target,
            t => TypeNameAsync(t, literal, assumeTrue, cancellation),
            cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> TypeNameAsync(Type type, TextSlice name, bool assumeTrue, CancellationToken cancellation = default)
    {
        if (!assumeTrue)
            return await facts.AdjustAsync(type, name.Span switch
            {
                "string" => TypeFacts.TypeofNEString,
                "number" => TypeFacts.TypeofNENumber,
                "bigint" => TypeFacts.TypeofNEBigInt,
                "boolean" => TypeFacts.TypeofNEBoolean,
                "symbol" => TypeFacts.TypeofNESymbol,
                "undefined" => TypeFacts.NEUndefined,
                "object" => TypeFacts.TypeofNEObject,
                "function" => TypeFacts.TypeofNEFunction,
                _ => TypeFacts.TypeofNEHostObject
            }, cancellation).ConfigureAwait(false);
        if (name.Span is "object" or "function" && (type.Flags & TypeFlags.Any) != 0)
            return type;
        if (name == "object")
            return await algebra.UnionAsync(
                [await TypeFactsAsync(type, context.NonPrimitiveType, TypeFacts.TypeofEQObject, cancellation).ConfigureAwait(false),
                await TypeFactsAsync(type, context.NullType, TypeFacts.EQNull, cancellation).ConfigureAwait(false)],
                cancellation: cancellation).ConfigureAwait(false);
        var (implied, include) = name.Span switch
        {
            "string" => (context.StringType, TypeFacts.TypeofEQString),
            "number" => (context.NumberType, TypeFacts.TypeofEQNumber),
            "bigint" => (context.BigIntType, TypeFacts.TypeofEQBigInt),
            "boolean" => (context.BooleanType, TypeFacts.TypeofEQBoolean),
            "symbol" => (context.ESSymbolType, TypeFacts.TypeofEQSymbol),
            "undefined" => (context.UndefinedType, TypeFacts.EQUndefined),
            "function" => (host.GlobalFunction, TypeFacts.TypeofEQFunction),
            _ => (context.NonPrimitiveType, TypeFacts.TypeofEQHostObject)
        };
        return await TypeFactsAsync(type, implied, include, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> TypeFactsAsync(Type type, Type implied, TypeFacts include, CancellationToken cancellation)
        => await algebra.MapAsync(type, async part =>
        {
            if (await relations.RelatedAsync(part, implied, RelationKind.StrictSubtype, cancellation).ConfigureAwait(false))
                return await facts.GetAsync(part, include, cancellation).ConfigureAwait(false) != 0 ? part : context.NeverType;
            if (await relations.RelatedAsync(implied, part, RelationKind.Subtype, cancellation).ConfigureAwait(false))
                return implied;
            return await facts.GetAsync(part, include, cancellation).ConfigureAwait(false) != 0
                ? await algebra.IntersectionAsync([part, implied], cancellation: cancellation).ConfigureAwait(false) : context.NeverType;
        }, cancellation: cancellation).ConfigureAwait(false) ?? context.NeverType;

    private static TextSlice? StringLike(SyntaxNode node) => node switch
    { StringLiteralNode literal => literal.Text, NoSubstitutionTemplateLiteralNode literal => literal.Text, _ => (TextSlice?)null };

    private static bool OptionalRoot(SyntaxNode node) => node.Parent switch
    {
        PropertyAccessExpressionNode parent => parent.QuestionDotToken is not null && parent.Expression == node,
        ElementAccessExpressionNode parent => parent.QuestionDotToken is not null && parent.Expression == node,
        CallExpressionNode parent => parent.QuestionDotToken is not null && parent.Expression == node,
        _ => false
    };
}

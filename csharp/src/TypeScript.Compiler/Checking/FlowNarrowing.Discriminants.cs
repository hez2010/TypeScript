using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class FlowNarrowing
{
    private async ValueTask<SyntaxNode?> DiscriminantAccessAsync(
        FlowState state,
        Type type,
        SyntaxNode expression,
        CancellationToken cancellation)
    {
        if (state.Declared is not UnionType && type is not UnionType)
            return null;
        var access = await CandidateAccessAsync(state, expression, cancellation).ConfigureAwait(false);
        if (access is null || await host.AccessNameAsync(access, cancellation).ConfigureAwait(false) is not { } name)
            return null;
        if (state.Declared is UnionType && await flows.SubsetAsync(type, state.Declared, cancellation).ConfigureAwait(false))
            type = state.Declared;
        return type is UnionType union && await discriminants.PropertyAsync(union, name, cancellation).ConfigureAwait(false)
            ? access
            : null;
    }

    private async ValueTask<SyntaxNode?> CandidateAccessAsync(FlowState state, SyntaxNode expression, CancellationToken cancellation)
    {
        if (state.Reference is BindingPatternNode or FunctionExpressionNode or ArrowFunctionNode
            or MethodDeclarationNode { Parent: ObjectLiteralExpressionNode })
        {
            if (expression is IdentifierNode)
            {
                var declaration = symbols.ExportedValue(host.ResolveReference(expression, cancellation))?.ValueDeclaration;
                if (declaration is BindingElementNode or ParameterDeclarationNode && state.Reference == declaration.Parent
                    && ((IInitializedNode)declaration).Initializer is null && (declaration switch
                    { BindingElementNode b => b.DotDotDotToken, ParameterDeclarationNode p => p.DotDotDotToken, _ => null }) is null)
                    return declaration;
            }
        }
        else if (expression is PropertyAccessExpressionNode or ElementAccessExpressionNode)
        {
            if (await references.MatchesAsync(state.Reference, FlowReferences.Receiver(expression)!, cancellation).ConfigureAwait(false))
                return expression;
        }
        else if (expression is IdentifierNode)
        {
            var symbol = host.ResolveReference(expression, cancellation);
            if (AssignmentMarks.Constant(symbol))
            {
                var declaration = symbol.ValueDeclaration!;
                var initializer = CandidateInitializer(declaration);
                if (initializer is PropertyAccessExpressionNode or ElementAccessExpressionNode
                    && await references.MatchesAsync(
                        state.Reference,
                        FlowReferences.Receiver(initializer)!,
                        cancellation).ConfigureAwait(false))
                    return initializer;
                if (declaration is BindingElementNode { Initializer: null })
                {
                    initializer = CandidateInitializer(declaration.Parent!.Parent!);
                    if (initializer is IdentifierNode or PropertyAccessExpressionNode or ElementAccessExpressionNode
                        && await references.MatchesAsync(state.Reference, initializer, cancellation).ConfigureAwait(false))
                        return declaration;
                }
            }
        }
        return null;
    }

    private static SyntaxNode? CandidateInitializer(SyntaxNode declaration)
    {
        var initializer = declaration is VariableDeclarationNode { Type: null } variable ? variable.Initializer : null;
        while (initializer is ParenthesizedExpressionNode parentheses)
            initializer = parentheses.Expression;
        return initializer;
    }

    private async ValueTask<Type> DiscriminantAsync(FlowState state, Type type, SyntaxNode expression,
        Func<Type, ValueTask<Type>> narrow, CancellationToken cancellation)
        => await DiscriminantAccessAsync(state, type, expression, cancellation).ConfigureAwait(false) is { } access
            ? await PropertyAsync(type, access, narrow, cancellation).ConfigureAwait(false) : type;

    private async ValueTask<Type> PropertyAsync(
        Type type,
        SyntaxNode access,
        Func<Type, ValueTask<Type>> narrow,
        CancellationToken cancellation)
    {
        if (await host.AccessNameAsync(access, cancellation).ConfigureAwait(false) is not { } name)
            return type;
        bool optional = (access.Flags & NodeFlags.OptionalChain) != 0;
        bool removeNullable = context.StrictNullChecks && (optional || access is PropertyAccessExpressionNode or ElementAccessExpressionNode
            && FlowReferences.Receiver(access) is NonNullExpressionNode) && predicates.Maybe(type, TypeFlags.Nullable, cancellation);
        var nonNull = removeNullable
            ? await facts.FilterAsync(type, TypeFacts.NEUndefinedOrNull, cancellation).ConfigureAwait(false)
            : type;
        var property = await host.FlowPropertyTypeAsync(nonNull, name, false, cancellation).ConfigureAwait(false);
        if (property is null)
            return type;
        if (removeNullable && optional)
            property = await algebra.UnionAsync([property, context.UndefinedType], cancellation: cancellation).ConfigureAwait(false);
        var narrowed = await narrow(property).ConfigureAwait(false);
        return await algebra.FilterAsync(type, async part =>
        {
            var discriminant = await host.FlowPropertyTypeAsync(
                part,
                name,
                true,
                cancellation).ConfigureAwait(false) ?? context.UnknownType;
            return (discriminant.Flags & TypeFlags.Never) == 0 && (narrowed.Flags & TypeFlags.Never) == 0
                && await ComparableAsync(narrowed, discriminant, cancellation).ConfigureAwait(false);
        }, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> UnmatchedEqualityAsync(FlowState state, Type type, SyntaxNode left, SyntaxNode right,
        SyntaxKind op, bool assumeTrue, CancellationToken cancellation)
    {
        if (context.StrictNullChecks)
        {
            if (await references.ContainsAsync(left, state.Reference, true, cancellation).ConfigureAwait(false))
                type = await OptionalContainmentAsync(type, op, right, assumeTrue, cancellation).ConfigureAwait(false);
            else if (await references.ContainsAsync(right, state.Reference, true, cancellation).ConfigureAwait(false))
                type = await OptionalContainmentAsync(type, op, left, assumeTrue, cancellation).ConfigureAwait(false);
        }
        if (await DiscriminantAccessAsync(state, type, left, cancellation).ConfigureAwait(false) is { } leftAccess)
            return await PropertyEqualityAsync(type, leftAccess, op, right, assumeTrue, cancellation).ConfigureAwait(false);
        if (await DiscriminantAccessAsync(state, type, right, cancellation).ConfigureAwait(false) is { } rightAccess)
            return await PropertyEqualityAsync(type, rightAccess, op, left, assumeTrue, cancellation).ConfigureAwait(false);
        if (await ConstructorReferenceAsync(state, left, cancellation).ConfigureAwait(false))
            return await host.ConstructorNarrowAsync(type, op, right, assumeTrue, cancellation).ConfigureAwait(false);
        if (await ConstructorReferenceAsync(state, right, cancellation).ConfigureAwait(false))
            return await host.ConstructorNarrowAsync(type, op, left, assumeTrue, cancellation).ConfigureAwait(false);
        if (right.Kind is SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword
            && left is not (PropertyAccessExpressionNode or ElementAccessExpressionNode))
            return await NarrowAsync(
                state,
                type,
                left,
                (right.Kind == SyntaxKind.TrueKeyword) == (op is SyntaxKind.EqualsEqualsToken or SyntaxKind.EqualsEqualsEqualsToken) == assumeTrue,
                cancellation).ConfigureAwait(false);
        if (left.Kind is SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword
            && right is not (PropertyAccessExpressionNode or ElementAccessExpressionNode))
            return await NarrowAsync(
                state,
                type,
                right,
                (left.Kind == SyntaxKind.TrueKeyword) == (op is SyntaxKind.EqualsEqualsToken or SyntaxKind.EqualsEqualsEqualsToken) == assumeTrue,
                cancellation).ConfigureAwait(false);
        return type;
    }

    private async ValueTask<bool> ConstructorReferenceAsync(FlowState state, SyntaxNode expression, CancellationToken cancellation)
    {
        string? name = expression switch
        {
            PropertyAccessExpressionNode property => SyntaxNameText.Get(property.Name),
            ElementAccessExpressionNode element => StringLike(element.ArgumentExpression!),
            _ => null
        };
        return name == "constructor"
            && await references.MatchesAsync(state.Reference, FlowReferences.Receiver(expression)!, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> OptionalContainmentAsync(
        Type type,
        SyntaxKind op,
        SyntaxNode value,
        bool assumeTrue,
        CancellationToken cancellation)
    {
        bool equals = op is SyntaxKind.EqualsEqualsToken or SyntaxKind.EqualsEqualsEqualsToken;
        var nullable = op is SyntaxKind.EqualsEqualsToken or SyntaxKind.ExclamationEqualsToken ? TypeFlags.Nullable : TypeFlags.Undefined;
        var valueType = await host.ExpressionAsync(value, cancellation).ConfigureAwait(false);
        var types = valueType is UnionType union ? union.Types : (IReadOnlyList<Type>)[valueType];
        bool remove = equals != assumeTrue && types.All(t => (t.Flags & nullable) != 0)
            || equals == assumeTrue && types.All(t => (t.Flags & (TypeFlags.AnyOrUnknown | nullable)) == 0);
        return remove ? await facts.AdjustAsync(type, TypeFacts.NEUndefinedOrNull, cancellation).ConfigureAwait(false) : type;
    }

    private async ValueTask<Type> PropertyEqualityAsync(
        Type type,
        SyntaxNode access,
        SyntaxKind op,
        SyntaxNode value,
        bool assumeTrue,
        CancellationToken cancellation)
    {
        if (op is SyntaxKind.EqualsEqualsEqualsToken or SyntaxKind.ExclamationEqualsEqualsToken && type is UnionType union)
        {
            string key = await discriminants.KeyAsync(union, cancellation).ConfigureAwait(false);
            if (key.Length != 0 && await host.AccessNameAsync(access, cancellation).ConfigureAwait(false) == key)
            {
                var candidateKey = await algebra.RegularTypeAsync(
                    await host.ExpressionAsync(value, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false);
                var candidate = union.ConstituentMap!.GetValueOrDefault(candidateKey);
                if (candidate is not null && candidate != context.UnknownType)
                {
                    if (assumeTrue == (op == SyntaxKind.EqualsEqualsEqualsToken))
                        return candidate;
                    return await host.FlowPropertyTypeAsync(candidate, key, false, cancellation).ConfigureAwait(false) is { IsUnit: true }
                        ? Remove(type, candidate) : type;
                }
            }
        }
        return await PropertyAsync(
            type,
            access,
            t => EqualityAsync(t, op, value, assumeTrue, cancellation),
            cancellation).ConfigureAwait(false);
    }
}

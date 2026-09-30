using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class FlowNarrowing
{
    private readonly Dictionary<(Type Type, Type Candidate, bool True, bool Derived), Type> narrowedTypes = [];

    internal async ValueTask<Type> NarrowedAsync(
        Type type,
        Type candidate,
        bool assumeTrue,
        bool checkDerived = false,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        context.RequireOwned(candidate);
        var key = (type, candidate, assumeTrue, checkDerived);
        if (type is UnionType && narrowedTypes.TryGetValue(key, out var cached))
            return cached;
        var result = await WorkerAsync(type, candidate, assumeTrue, checkDerived, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (type is UnionType)
            narrowedTypes[key] = result;
        return result;
    }

    private async ValueTask<Type> WorkerAsync(Type type, Type candidate, bool assumeTrue, bool derived, CancellationToken cancellation)
    {
        if (!assumeTrue)
        {
            if (type == candidate)
                return context.NeverType;
            if (derived)
                return await algebra.FilterAsync(
                    type,
                    async t => !await host.DerivedAsync(t, candidate, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false);
            if ((type.Flags & TypeFlags.Unknown) != 0)
                type = context.UnknownUnionType;
            var trueType = await NarrowedAsync(type, candidate, true, false, cancellation).ConfigureAwait(false);
            var filtered = await algebra.FilterAsync(
                type,
                async t => !await flows.SubsetAsync(t, trueType, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
            return filtered == context.UnknownUnionType ? context.UnknownType : filtered;
        }
        if ((type.Flags & TypeFlags.AnyOrUnknown) != 0 || type == candidate)
            return candidate;
        Utf8String key = type is UnionType union ? await discriminants.KeyAsync(union, cancellation).ConfigureAwait(false) : Utf8String.Empty;
        var narrowed = await algebra.MapAsync(candidate, async next =>
        {
            var matching = key.Length != 0
                ? await discriminants.MatchAsync((UnionType)type, next, cancellation).ConfigureAwait(false) ?? type
                : type;
            var direct = await algebra.MapAsync(matching, async part =>
            {
                if (derived)
                {
                    if (await host.DerivedAsync(part, next, cancellation).ConfigureAwait(false))
                        return part;
                    if (await host.DerivedAsync(next, part, cancellation).ConfigureAwait(false))
                        return next;
                }
                else
                {
                    if (await relations.RelatedAsync(part, next, RelationKind.StrictSubtype, cancellation).ConfigureAwait(false))
                        return part;
                    if (await relations.RelatedAsync(next, part, RelationKind.StrictSubtype, cancellation).ConfigureAwait(false))
                        return next;
                    if (await relations.RelatedAsync(part, next, RelationKind.Subtype, cancellation).ConfigureAwait(false))
                        return part;
                    if (await relations.RelatedAsync(next, part, RelationKind.Subtype, cancellation).ConfigureAwait(false))
                        return next;
                }
                return context.NeverType;
            }, cancellation: cancellation).ConfigureAwait(false) ?? context.NeverType;
            if ((direct.Flags & TypeFlags.Never) == 0)
                return direct;
            return await algebra.MapAsync(type, async part =>
            {
                if (predicates.Maybe(part, TypeFlags.Instantiable, cancellation))
                {
                    var constraint = await constraints.BaseConstraintAsync(part, cancellation).ConfigureAwait(false);
                    if (constraint is null || (derived ? await host.DerivedAsync(next, constraint, cancellation).ConfigureAwait(false)
                        : await relations.RelatedAsync(next, constraint, RelationKind.Subtype, cancellation).ConfigureAwait(false)))
                        return await algebra.IntersectionAsync([part, next], cancellation: cancellation).ConfigureAwait(false);
                }
                return context.NeverType;
            }, cancellation: cancellation).ConfigureAwait(false) ?? context.NeverType;
        }, cancellation: cancellation).ConfigureAwait(false) ?? context.NeverType;
        if ((narrowed.Flags & TypeFlags.Never) == 0)
            return narrowed;
        if (await relations.RelatedAsync(candidate, type, RelationKind.Subtype, cancellation).ConfigureAwait(false))
            return candidate;
        if (await relations.RelatedAsync(type, candidate, RelationKind.Assignable, cancellation).ConfigureAwait(false))
            return type;
        if (await relations.RelatedAsync(candidate, type, RelationKind.Assignable, cancellation).ConfigureAwait(false))
            return candidate;
        return await algebra.IntersectionAsync([type, candidate], cancellation: cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> PredicateNarrowAsync(FlowState state, Type type, TypePredicate predicate, SyntaxNode call,
        bool assumeTrue, CancellationToken cancellation = default)
    {
        if (predicate.Type is not { } asserted
            || (type.Flags & TypeFlags.Any) != 0 && (asserted == host.GlobalObject || asserted == host.GlobalFunction))
            return type;
        var argument = PredicateArgument(predicate, call);
        if (argument is null)
            return type;
        if (await references.MatchesAsync(state.Reference, argument, cancellation).ConfigureAwait(false))
            return await NarrowedAsync(type, asserted, assumeTrue, cancellation: cancellation).ConfigureAwait(false);
        if (context.StrictNullChecks && await references.ContainsAsync(argument, state.Reference, true, cancellation).ConfigureAwait(false))
        {
            bool removeNullable;
            if (assumeTrue)
                removeNullable = await facts.GetAsync(asserted, TypeFacts.EQUndefined, cancellation).ConfigureAwait(false) == 0;
            else
            {
                removeNullable = true;
                foreach (var part in asserted is UnionType union ? union.Types : (IReadOnlyList<Type>)[asserted])
                    if (await facts.GetAsync(part, TypeFacts.IsUndefinedOrNull, cancellation).ConfigureAwait(false) == 0)
                    {
                        removeNullable = false;
                        break;
                    }
            }
            if (removeNullable)
                type = await facts.AdjustAsync(type, TypeFacts.NEUndefinedOrNull, cancellation).ConfigureAwait(false);
        }
        return await DiscriminantAsync(
            state,
            type,
            argument,
            t => NarrowedAsync(t, asserted, assumeTrue, cancellation: cancellation),
            cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> CallAsync(
        FlowState state,
        Type type,
        CallExpressionNode call,
        bool assumeTrue,
        CancellationToken cancellation)
    {
        bool matching = false;
        foreach (var argument in call.Arguments!)
            if (await references.MatchesAsync(state.Reference, argument, cancellation).ConfigureAwait(false)
                || await references.ContainsAsync(state.Reference, argument, false, cancellation).ConfigureAwait(false)
                || await references.ContainsAsync(argument, state.Reference, true, cancellation).ConfigureAwait(false))
            {
                matching = true;
                break;
            }
        if (!matching && call.Expression is PropertyAccessExpressionNode access)
            matching = await references.MatchesAsync(state.Reference, access.Expression!, cancellation).ConfigureAwait(false)
                || await references.ContainsAsync(state.Reference, access.Expression!, false, cancellation).ConfigureAwait(false);
        if (matching && (assumeTrue || (call.Flags & NodeFlags.OptionalChain) == 0)
            && await host.EffectsAsync(call, cancellation).ConfigureAwait(false) is { } signature
            && await host.PredicateAsync(
                signature,
                cancellation).ConfigureAwait(false) is { Kind: TypePredicateKind.This or TypePredicateKind.Identifier } predicate)
            return await PredicateNarrowAsync(state, type, predicate, call, assumeTrue, cancellation).ConfigureAwait(false);
        if ((type == context.MissingType || type is UnionType union && union.Types.Contains(context.MissingType))
            && state.Reference is PropertyAccessExpressionNode or ElementAccessExpressionNode
            && call.Expression is PropertyAccessExpressionNode property
            && await references.MatchesAsync(
                FlowReferences.Receiver(state.Reference)!,
                FlowReferences.Candidate(property.Expression!),
                cancellation).ConfigureAwait(false)
            && property.Name is IdentifierNode { Text.Span: var matchedText } && matchedText.SequenceEqual("hasOwnProperty"u8) && call.Arguments!.Count == 1 && await host.AccessNameAsync(
                state.Reference,
                cancellation).ConfigureAwait(false) is { } name
            && StringLike(call.Arguments[0]) == name)
            return await facts.FilterAsync(
                type,
                assumeTrue ? TypeFacts.NEUndefined : TypeFacts.EQUndefined,
                cancellation).ConfigureAwait(false);
        return type;
    }

    private static SyntaxNode? PredicateArgument(TypePredicate predicate, SyntaxNode call)
    {
        if (predicate.Kind is TypePredicateKind.Identifier or TypePredicateKind.AssertsIdentifier)
            return call is CallExpressionNode { Arguments: var arguments }
                && predicate.ParameterIndex >= 0
                && predicate.ParameterIndex < arguments!.Count
                ? arguments[predicate.ParameterIndex] : null;
        var expression = FlowReferences.Receiver(call);
        while (expression is ParenthesizedExpressionNode parent)
            expression = parent.Expression;
        if (expression is not (PropertyAccessExpressionNode or ElementAccessExpressionNode))
            return null;
        expression = FlowReferences.Receiver(expression);
        while (expression is ParenthesizedExpressionNode parent)
            expression = parent.Expression;
        return expression;
    }
}

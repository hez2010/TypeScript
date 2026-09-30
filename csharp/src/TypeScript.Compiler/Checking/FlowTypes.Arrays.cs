using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class FlowTypes
{
    private readonly Dictionary<Type, EvolvingArrayType> evolvingArrays = [];

    internal EvolvingArrayType Evolving(Type element)
    {
        context.RequireOwned(element);
        if (evolvingArrays.TryGetValue(element, out var cached))
            return cached;
        var result = (EvolvingArrayType)context.NewObjectType(ObjectFlags.EvolvingArray);
        result.ElementType = element;
        evolvingArrays.Add(element, result);
        return result;
    }

    internal async ValueTask<Type> FinalizeAsync(Type type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type is not EvolvingArrayType array)
            return type;
        if (array.FinalArrayType is not null)
            return array.FinalArrayType;
        var element = array.ElementType!;
        var result = (element.Flags & TypeFlags.Never) != 0 ? host.AutoArray : await host.ArrayAsync(
            element is UnionType union
                ? await algebra.UnionAsync(union.Types, UnionReduction.Subtype, cancellation: cancellation).ConfigureAwait(false)
                : element,
            cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return array.FinalArrayType = result;
    }

    internal async ValueTask<bool> EvolvingTargetAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        var root = FlowReferences.Root(node);
        var parent = root.Parent;
        bool property = parent is PropertyAccessExpressionNode access && (SyntaxNameText.Get(access.Name) == Utf8Literals.Length
            || access.Parent is CallExpressionNode && access.Name is IdentifierNode { Text.Span: var matchedText } && (matchedText.SequenceEqual("push"u8) || matchedText.SequenceEqual("unshift"u8)));
        // Evaluate the element case independently, as in the reference.
        bool element = parent is ElementAccessExpressionNode index && index.Expression == root
            && index.Parent is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.EqualsToken } assignment
            && assignment.Left == index && ReferenceSyntax.AssignmentTarget(assignment) is null
            && await predicates.AssignableAsync(await host.ExpressionAsync(index.ArgumentExpression!, cancellation).ConfigureAwait(false),
                TypeFlags.NumberLike, cancellation: cancellation).ConfigureAwait(false);
        return property || element;
    }

    private async ValueTask<EvolvingArrayType> AddElementAsync(EvolvingArrayType array, SyntaxNode node, CancellationToken cancellation)
    {
        var element = await host.RegularObjectLiteralAsync(await widening.LiteralBaseAsync(
            await host.ContextFreeExpressionAsync(node, cancellation).ConfigureAwait(false),
            cancellation).ConfigureAwait(false),
            cancellation).ConfigureAwait(false);
        return await SubsetAsync(element, array.ElementType!, cancellation).ConfigureAwait(false) ? array
            : Evolving(await algebra.UnionAsync([array.ElementType!, element], cancellation: cancellation).ConfigureAwait(false));
    }

    private async ValueTask<FlowType?> MutationAsync(FlowState state, FlowNode flow, CancellationToken cancellation)
    {
        if (state.Declared != context.AutoType && state.Declared != host.AutoArray)
            return null;
        var node = flow.Node!;
        var expression = node is CallExpressionNode call
            ? FlowReferences.Receiver(call.Expression!)
            : FlowReferences.Receiver(((BinaryExpressionNode)node).Left!);
        if (!await host.MatchesAsync(state.Reference, FlowReferences.Candidate(expression!), cancellation).ConfigureAwait(false))
            return null;
        var antecedent = await AtAsync(state, flow.Antecedent!, cancellation).ConfigureAwait(false);
        if (antecedent.Type is not EvolvingArrayType evolved)
            return antecedent;
        if (node is CallExpressionNode mutation)
        {
            foreach (var argument in mutation.Arguments!)
                evolved = await AddElementAsync(evolved, argument, cancellation).ConfigureAwait(false);
        }
        else
        {
            var assignment = (BinaryExpressionNode)node;
            var index = (ElementAccessExpressionNode)assignment.Left!;
            var indexType = await host.ContextFreeExpressionAsync(index.ArgumentExpression!, cancellation).ConfigureAwait(false);
            if (await predicates.AssignableAsync(indexType, TypeFlags.NumberLike, cancellation: cancellation).ConfigureAwait(false))
                evolved = await AddElementAsync(evolved, assignment.Right!, cancellation).ConfigureAwait(false);
        }
        return Result(evolved, antecedent.Incomplete);
    }
}

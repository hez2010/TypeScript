using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;

namespace TypeScript.Compiler.Checking;

internal interface IInferredConstraintHost
{
    ValueTask<IReadOnlyList<TypeParameter>> ReferenceParametersAsync(TypeReferenceNode node, CancellationToken cancellation);

    ValueTask<Type> EffectiveArgumentAsync(
        TypeReferenceNode node,
        IReadOnlyList<TypeParameter> parameters,
        int index,
        CancellationToken cancellation);

    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<TypeParameter> ParameterAsync(TypeParameterDeclarationNode node, CancellationToken cancellation);
}

internal sealed class InferredConstraints(TypeContext context, TypeAlgebra algebra, TypeConstraints constraints,
    TypeInstantiation instantiation, TupleTypes tuples, IInferredConstraintHost host)
{
    internal async ValueTask<Type?> GetAsync(TypeParameter parameter, bool omitReferences = false, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(parameter);
        var inferences = new List<Type>();
        foreach (var declaration in parameter.Symbol?.Declarations ?? [])
        {
            if (declaration.Parent is not InferTypeNode infer)
                continue;
            SyntaxNode child = infer;
            var parent = child.Parent;
            while (parent is ParenthesizedTypeNode)
            {
                cancellation.ThrowIfCancellationRequested();
                child = parent;
                parent = parent.Parent;
            }
            if (parent is TypeReferenceNode reference && !omitReferences)
            {
                var parameters = await host.ReferenceParametersAsync(reference, cancellation).ConfigureAwait(false);
                int index = reference.TypeArguments?.ToList().IndexOf(child) ?? -1;
                if (index >= 0 && index < parameters.Count
                    && await constraints.ParameterConstraintAsync(parameters[index], cancellation).ConfigureAwait(false) is { } bound)
                {
                    var mapper = TypeMapper.FunctionAsync(async (type, token) =>
                    {
                        for (int i = 0; i < parameters.Count; i++)
                            if (type == parameters[i])
                                return await host.EffectiveArgumentAsync(reference, parameters, i, token).ConfigureAwait(false);
                        return type;
                    });
                    var constraint = await instantiation.InstantiateAsync(bound, mapper, cancellation: cancellation).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Inferred constraint instantiation returned no type");
                    if (constraint != parameter)
                        inferences.Add(constraint);
                }
            }
            else if (parent is ParameterDeclarationNode { DotDotDotToken: not null } or RestTypeNode
                or NamedTupleMemberNode { DotDotDotToken: not null })
                inferences.Add(await tuples.ArrayAsync(context.UnknownType, cancellation: cancellation).ConfigureAwait(false));
            else if (parent is TemplateLiteralTypeSpanNode)
                inferences.Add(context.StringType);
            else if (parent is TypeParameterDeclarationNode { Parent: MappedTypeNode })
                inferences.Add(context.StringNumberSymbolType);
            else if (parent is MappedTypeNode { Type: { } body, Parent: ConditionalTypeNode conditional } mapping
                && SkipParentheses(body) == infer && conditional.ExtendsType == mapping
                && conditional.CheckType is MappedTypeNode { Type: { } checkBody, TypeParameter: { } mappedParameter })
            {
                var type = await host.TypeFromNodeAsync(checkBody, cancellation).ConfigureAwait(false);
                var mappedVariable = await host.ParameterAsync(mappedParameter, cancellation).ConfigureAwait(false);
                var target = mappedParameter.Constraint is null ? context.StringNumberSymbolType
                    : await host.TypeFromNodeAsync(mappedParameter.Constraint, cancellation).ConfigureAwait(false);
                inferences.Add(
                    await instantiation.InstantiateAsync(
                        type,
                        TypeMapper.Create([mappedVariable], [target]),
                        cancellation: cancellation).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Mapped inferred constraint returned no type"));
            }
        }
        return inferences.Count == 0 ? null : await algebra.IntersectionAsync(inferences, cancellation: cancellation).ConfigureAwait(false);
    }

    private static SyntaxNode SkipParentheses(SyntaxNode node)
    {
        while (node is ParenthesizedTypeNode parentheses)
            node = parentheses.Type!;
        return node;
    }
}

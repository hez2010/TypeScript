using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed class TypeReferenceChecks(TypeContext context, CheckerLinks links, TypeReferences references,
    DeclaredTypes declared, TypeParameterScopes scopes, TypeConstraints constraints, TypeInstantiation instantiation,
    TypeRelations relations, Action<SyntaxNode, int> error)
{
    internal async ValueTask CheckAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        var typeArguments = TypeReferences.Arguments(node);
        var type = await references.FromNodeAsync(node, cancellation).ConfigureAwait(false);
        if (type == context.ErrorType
            || (type.Flags & TypeFlags.Any) != 0 && type.Alias is not null
            || typeArguments is not { Count: > 0 })
            return;
        var symbol = await references.SymbolAsync(node, cancellation).ConfigureAwait(false);
        IReadOnlyList<TypeParameter> parameters;
        if ((symbol.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) != 0)
        {
            var target = await scopes.ClassOrInterfaceAsync(symbol, cancellation).ConfigureAwait(false);
            parameters = target.AllTypeParameters.Skip(target.OuterTypeParameterCount)
                .Take(
                    target.AllTypeParameters.Count - target.OuterTypeParameterCount - (target.ThisType is null
                        ? 0
                        : 1)).Cast<TypeParameter>().ToArray();
        }
        else if ((symbol.Flags & SymbolFlags.TypeAlias) != 0)
        {
            await declared.GetAsync(symbol, cancellation).ConfigureAwait(false);
            parameters = links.TypeAliases.Get(symbol).TypeParameters ?? [];
        }
        else
            return;
        IReadOnlyList<Type>? arguments = null;
        TypeMapper? mapper = null;
        bool success = true;
        for (int i = 0; i < parameters.Count; i++)
        {
            var constraint = await constraints.ConstraintAsync(parameters[i], cancellation).ConfigureAwait(false);
            if (constraint is null)
                continue;
            if (arguments is null)
            {
                arguments = await references.EffectiveArgumentsAsync(node, parameters, cancellation).ConfigureAwait(false);
                mapper = TypeMapper.Create(parameters.Cast<Type>().ToArray(), arguments.ToArray());
            }
            if (success && !await relations.RelatedAsync(arguments[i], (await instantiation.InstantiateAsync(constraint, mapper,
                cancellation: cancellation).ConfigureAwait(false))!, RelationKind.Assignable, cancellation).ConfigureAwait(false))
            {
                if (i < typeArguments.Count)
                    error(typeArguments[i], 2344);
                success = false;
            }
        }
    }
}

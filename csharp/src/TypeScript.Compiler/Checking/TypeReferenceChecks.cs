using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface IConstraintCheckHost
{
    ValueTask<bool> CheckConstraintAsync(Type source, Type target, SyntaxNode node, CancellationToken cancellation);
}

internal sealed class TypeReferenceChecks(TypeContext context, CheckerLinks links, TypeReferences references,
    DeclaredTypes declared, TypeParameterScopes scopes, TypeConstraints constraints, TypeInstantiation instantiation,
    TypeRelations relations, IConstraintCheckHost host)
{
    internal async ValueTask CheckAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        var typeArguments = TypeReferences.Arguments(node);
        var type = await references.FromNodeAsync(node, cancellation).ConfigureAwait(false);
        if (type == context.ErrorType
            || (type.Flags & TypeFlags.Any) != 0 && type.Alias is not null
            || typeArguments is not { Count: > 0 })
            return;
        var symbol = links.SymbolNodes.TryGet(node)?.ResolvedSymbol;
        if (symbol is null)
            return;
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
            if (!success)
                continue;
            var target = (await instantiation.InstantiateAsync(constraint, mapper, cancellation: cancellation).ConfigureAwait(false))!;
            if (!await relations.RelatedAsync(arguments[i], target, RelationKind.Assignable, cancellation).ConfigureAwait(false))
            {
                if (i < typeArguments.Count)
                    await host.CheckConstraintAsync(arguments[i], target, typeArguments[i], cancellation).ConfigureAwait(false);
                success = false;
            }
        }
    }
}

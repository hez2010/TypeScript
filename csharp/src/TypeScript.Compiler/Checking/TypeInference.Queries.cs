using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class TypeInference
{
    internal async ValueTask<bool> ConstVariableAsync(Type type, CancellationToken cancellation = default)
    {
        context.RequireOwned(type);
        var pending = new Stack<(Type Type, int Depth)>();
        pending.Push((type, 0));
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            if (item.Depth >= 5)
                continue;
            switch (item.Type)
            {
                case TypeParameter parameter:
                    if (parameter.Symbol?.Declarations.Any(d => SemanticSyntax.HasModifier(d, SyntaxKind.ConstKeyword)) == true)
                        return true;
                    break;
                case UnionOrIntersectionType composite:
                    for (int i = composite.Types.Count - 1; i >= 0; i--)
                        pending.Push((composite.Types[i], item.Depth));
                    break;
                case IndexedAccessType access:
                    pending.Push((access.ObjectType, item.Depth + 1));
                    break;
                case ConditionalType conditional:
                    pending.Push(
                        (await constraints.ConditionalConstraintAsync(conditional, cancellation).ConfigureAwait(false), item.Depth + 1));
                    break;
                case SubstitutionType substitution:
                    pending.Push((substitution.BaseType, item.Depth));
                    break;
                case MappedType mapping:
                    if (await mapped.HomomorphicVariableAsync(mapping, cancellation).ConfigureAwait(false) is { } variable)
                        pending.Push((variable, item.Depth));
                    break;
                case TypeReference { Target: TupleType tuple } reference when TypeConstraints.IsGenericTuple(reference):
                    var args = await host.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false);
                    for (int i = tuple.ElementInfos.Count - 1; i >= 0; i--)
                        if ((tuple.ElementInfos[i].Flags & ElementFlags.Variadic) != 0)
                            pending.Push((args[i], item.Depth));
                    break;
            }
        }
        return false;
    }

    internal async ValueTask<bool> DefinitelyUnrelatedAsync(Type source, Type target, CancellationToken cancellation = default)
    {
        context.RequireOwned(source);
        context.RequireOwned(target);
        if (source is TypeReference { Target: TupleType left } && target is TypeReference { Target: TupleType right })
            return (right.CombinedFlags & ElementFlags.Variadic) == 0 && right.MinLength > left.MinLength
                || (right.CombinedFlags & ElementFlags.Variable) == 0
                    && ((left.CombinedFlags & ElementFlags.Variable) != 0 || right.FixedLength < left.FixedLength);
        return await UnmatchedAsync(source, target, true, cancellation).ConfigureAwait(false)
            && await UnmatchedAsync(target, source, false, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<bool> UnmatchedAsync(Type source, Type target, bool discriminants, CancellationToken cancellation)
    {
        foreach (var property in await host.PropertiesAsync(target, cancellation).ConfigureAwait(false))
        {
            if (property.ValueDeclaration is INamedNode { Name: PrivateIdentifierNode }
                && SemanticSyntax.HasModifier(property.ValueDeclaration, SyntaxKind.StaticKeyword))
                continue;
            if ((property.Flags & SymbolFlags.Optional) != 0 || (property.CheckFlags & CheckFlags.Partial) != 0)
                continue;
            var sourceProperty = await host.PropertyAsync(source, property.Name, cancellation).ConfigureAwait(false);
            if (sourceProperty is null)
                return true;
            if (discriminants)
            {
                var targetType = await host.SymbolTypeAsync(property, cancellation).ConfigureAwait(false);
                if ((targetType.Flags & TypeFlags.Unit) != 0)
                {
                    var sourceType = await host.SymbolTypeAsync(sourceProperty, cancellation).ConfigureAwait(false);
                    if ((sourceType.Flags & TypeFlags.Any) == 0
                        && await algebra.RegularTypeAsync(
                            sourceType,
                            cancellation).ConfigureAwait(false) != await algebra.RegularTypeAsync(
                                targetType,
                                cancellation).ConfigureAwait(false))
                        return true;
                }
            }
        }
        return false;
    }
}

using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface IIndexedAccessValidationHost
{
    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    CheckFlags AccessFlags(Symbol symbol, bool writing);

    ValueTask AccessErrorAsync(
        SyntaxNode node,
        int code,
        CancellationToken cancellation,
        Type? type = null,
        Symbol? symbol = null,
        Type? index = null,
        Symbol? related = null);
}

internal sealed class IndexedAccessValidation(TypeContext context, TypeKeys keys, MappedTypes mapped, MappedMembers members,
    IndexSignatures indexes, TypeRelations relations, TypeViews views, TypeProperties properties, IIndexedAccessValidationHost host)
{
    internal async ValueTask<Type> CheckAsync(Type type, SyntaxNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (type is not IndexedAccessType access)
            return type;
        var objectType = access.ObjectType;
        var indexType = access.IndexType;
        var objectIndex = objectType is MappedType mapping && await mapped.IsGenericAsync(mapping, cancellation).ConfigureAwait(false)
            && await members.NameKindAsync(mapping, cancellation).ConfigureAwait(false) == MappedTypeNameTypeKind.Remapping
                ? await keys.MappedAsync(mapping, cancellation: cancellation).ConfigureAwait(false)
                : await keys.GetAsync(objectType, cancellation: cancellation).ConfigureAwait(false);
        bool numberIndex = (await host.IndexesAsync(
            objectType,
            cancellation).ConfigureAwait(false)).Any(i => i.KeyType == context.NumberType);
        bool valid = true;
        foreach (var part in indexType is UnionType union ? union.Types : (IReadOnlyList<Type>)[indexType])
            if (!await relations.RelatedAsync(part, objectIndex, RelationKind.Assignable, cancellation).ConfigureAwait(false)
                && !(numberIndex && await indexes.ApplicableTypeAsync(part, context.NumberType, cancellation).ConfigureAwait(false)))
            {
                valid = false;
                break;
            }
        if (valid)
        {
            if (node is ElementAccessExpressionNode && ReferenceSyntax.AssignmentTarget(node) is not null
                && objectType is MappedType mappedObject && (MappedTypes.Modifiers(mappedObject) & MappedTypeModifiers.IncludeReadonly) != 0)
                await host.AccessErrorAsync(node, 2542, cancellation, objectType).ConfigureAwait(false);
            return type;
        }
        if (((await mapped.GenericFlagsAsync(objectType, cancellation).ConfigureAwait(false)) & ObjectFlags.IsGenericObjectType) != 0
            && (indexType.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0)
        {
            string name = MappedMembers.PropertyName(indexType);
            var apparent = await views.ApparentAsync(objectType, cancellation).ConfigureAwait(false);
            foreach (var part in apparent is UnionOrIntersectionType composite ? composite.Types : (IReadOnlyList<Type>)[apparent])
                if (await properties.PropertyAsync(part, name, cancellation: cancellation).ConfigureAwait(false) is { } property)
                {
                    if ((host.AccessFlags(property, false) & (CheckFlags.ContainsPrivate | CheckFlags.ContainsProtected)) != 0)
                    {
                        await host.AccessErrorAsync(node, 4105, cancellation, symbol: property).ConfigureAwait(false);
                        return context.ErrorType;
                    }
                    break;
                }
        }
        await host.AccessErrorAsync(node, 2536, cancellation, objectType, index: indexType).ConfigureAwait(false);
        return context.ErrorType;
    }
}

using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IArrayLiteralHost
{
    ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<bool> ArrayLikeAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> MutableArrayLikeAsync(Type type, CancellationToken cancellation);

    ValueTask<Type?> IteratedContextAsync(Type type, CancellationToken cancellation);

    ValueTask<Type> SpreadElementAsync(Type type, SyntaxNode node, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    bool ContextSensitive(SyntaxNode node);
}

internal sealed class ArrayLiterals(TypeContext context, TypeAlgebra algebra, ExpressionContexts contexts,
    TypeProperties properties, SymbolTypes values, MappedTypes mapped, TupleTypes tuples, IndexedTypes indexed, IArrayLiteralHost host)
{
    private readonly Dictionary<TypeReference, TypeReference> literals = [];

    internal ValueTask<Type> CheckAsync(ArrayLiteralExpressionNode node, CheckMode mode = 0, CancellationToken cancellation = default)
        => contexts.CachedAsync(node, async () =>
        {
            var elements = node.Elements!;
            var types = new Type[elements.Count];
            var infos = new TupleElementInfo[elements.Count];
            bool destructuring = ReferenceSyntax.AssignmentTarget(node) is not null;
            bool constContext = await contexts.ConstAsync(node, cancellation).ConfigureAwait(false);
            var contextual = await contexts.ApparentAsync(node, cancellation: cancellation).ConfigureAwait(false);
            SyntaxNode? parent = node.Parent;
            while (parent is ParenthesizedExpressionNode)
                parent = parent.Parent;
            bool tupleContext = parent is SpreadElementNode { Parent: CallExpressionNode or NewExpressionNode };
            if (!tupleContext && contextual is not null)
                foreach (var part in contextual is UnionType union ? union.Types : [contextual])
                    if (await TupleLikeAsync(part, cancellation).ConfigureAwait(false)
                        || part is MappedType mapping && await mapped.IsGenericAsync(mapping, cancellation).ConfigureAwait(false)
                            && await mapped.NameAsync(mapping, cancellation).ConfigureAwait(false) is null
                            && await mapped.HomomorphicVariableAsync(
                                (MappedType?)mapping.Target ?? mapping,
                                cancellation).ConfigureAwait(false) is not null)
                    {
                        tupleContext = true;
                        break;
                    }
            bool omitted = false;
            for (int i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                cancellation.ThrowIfCancellationRequested();
                if (element is SpreadElementNode spread)
                {
                    var spreadType = await host.CheckExpressionAsync(spread.Expression!, mode, cancellation).ConfigureAwait(false);
                    if (await host.ArrayLikeAsync(spreadType, cancellation).ConfigureAwait(false))
                    {
                        types[i] = spreadType;
                        infos[i] = new(ElementFlags.Variadic);
                    }
                    else
                    {
                        types[i] = destructuring
                            ? (await host.IndexesAsync(
                                spreadType,
                                cancellation).ConfigureAwait(false)).FirstOrDefault(info => info.KeyType == context.NumberType)?.ValueType
                                ?? await host.IteratedContextAsync(spreadType, cancellation).ConfigureAwait(false) ?? context.UnknownType
                            : await host.SpreadElementAsync(spreadType, spread.Expression!, cancellation).ConfigureAwait(false);
                        infos[i] = new(ElementFlags.Rest);
                    }
                }
                else if (context.ExactOptionalPropertyTypes && element.Kind == SyntaxKind.OmittedExpression)
                {
                    omitted = true;
                    types[i] = context.UndefinedOrMissingType;
                    infos[i] = new(ElementFlags.Optional);
                }
                else
                {
                    var type = await contexts.MutableAsync(element, mode, cancellation).ConfigureAwait(false);
                    types[i] = omitted && context.StrictNullChecks
                        ? await algebra.UnionAsync(
                            [type, context.UndefinedOrMissingType],
                            cancellation: cancellation).ConfigureAwait(false) : type;
                    infos[i] = new(omitted ? ElementFlags.Optional : ElementFlags.Required);
                    if (tupleContext && (mode & CheckMode.Inferential) != 0 && (mode & CheckMode.SkipContextSensitive) == 0
                        && host.ContextSensitive(element))
                        contexts.InferenceFor(node)!.IntraExpressionSites.Add((element, type));
                }
            }
            if (destructuring)
                return await tuples.CreateAsync(types, infos, cancellation: cancellation).ConfigureAwait(false);
            if ((mode & CheckMode.ForceTuple) != 0 || constContext || tupleContext)
            {
                bool mutable = false;
                if (constContext && contextual is not null)
                    foreach (var part in contextual is UnionType union ? union.Types : [contextual])
                        if (await host.MutableArrayLikeAsync(part, cancellation).ConfigureAwait(false))
                        {
                            mutable = true;
                            break;
                        }
                return Literal(await tuples.CreateAsync(types, infos, constContext && !mutable, cancellation).ConfigureAwait(false));
            }
            for (int i = 0; i < types.Length; i++)
                if ((infos[i].Flags & ElementFlags.Variadic) != 0)
                    types[i] = await indexed.TryGetAsync(
                        types[i],
                        context.NumberType,
                        cancellation: cancellation).ConfigureAwait(false) ?? context.AnyType;
            var elementType = types.Length != 0
                ? await algebra.UnionAsync(types, UnionReduction.Subtype, cancellation: cancellation).ConfigureAwait(false)
                : context.StrictNullChecks ? context.ImplicitNeverType : context.UndefinedWideningType;
            return Literal(await tuples.ArrayAsync(elementType, constContext, cancellation).ConfigureAwait(false));
        }, cancellation);

    internal Type Literal(Type type)
    {
        if (type is not TypeReference reference)
            return type;
        if (!literals.TryGetValue(reference, out var result))
        {
            result = context.CloneTypeReference(reference);
            result.ObjectFlags |= ObjectFlags.ArrayLiteral | ObjectFlags.ContainsObjectOrArrayLiteral;
            literals[reference] = result;
        }
        return result;
    }

    internal async ValueTask<bool> TupleLikeAsync(Type type, CancellationToken cancellation = default)
    {
        if (type is TypeReference { Target: TupleType }
            || await properties.PropertyAsync(type, Utf8Literals.Zero, cancellation: cancellation).ConfigureAwait(false) is not null)
            return true;
        if (await host.ArrayLikeAsync(type, cancellation).ConfigureAwait(false)
            && await properties.PropertyAsync(type, Utf8Literals.Length, cancellation: cancellation).ConfigureAwait(false) is { } length)
        {
            var lengthType = await values.GetAsync(length, cancellation).ConfigureAwait(false);
            return (lengthType is UnionType union ? union.Types : [lengthType]).All(t => (t.Flags & TypeFlags.NumberLiteral) != 0);
        }
        return false;
    }

}

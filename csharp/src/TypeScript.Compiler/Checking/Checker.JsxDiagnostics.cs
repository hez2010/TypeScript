using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask<bool> ElaborateJsxAsync(
        JsxAttributesNode attributes,
        Type source,
        Type target,
        RelationKind relation,
        CancellationToken cancellation)
    {
        bool reported = false;
        foreach (var attribute in attributes.Properties!.OfType<JsxAttributeNode>())
        {
            string name = JsxName(attribute.Name!);
            if (!name.Contains('-'))
                reported |= await LiteralElaboration.ElementAsync(source, target, relation,
                attribute.Name!, attribute.Initializer, context.GetStringLiteralType(name), null, cancellation);
        }
        if (attributes.Parent is not JsxOpeningElementNode { Parent: JsxElementNode element })
            return reported;
        string nameOfChildren = await JsxPropertyNameAsync("ElementChildrenAttribute", attributes, cancellation) ?? "children";
        var nameType = context.GetStringLiteralType(nameOfChildren);
        var childrenTarget = await Indexed.GetAsync(target, nameType, cancellation: cancellation);
        var children = JsxSemanticChildren(element);
        if (children.Count == 0)
            return reported;
        var iterable = await program.Globals.GetAsync("Iterable", 3, false, cancellation);
        var anyIterable = iterable == context.EmptyGenericType
            ? null
            : context.CreateTypeReference((InterfaceType)iterable, [context.AnyType, context.AnyType, context.AnyType]);
        async ValueTask<bool> Iterable(Type type) => anyIterable is not null ? await AssignableAsync(type, anyIterable, cancellation)
            : IsArray(type) || await ArrayLiterals.TupleLikeAsync(type, cancellation);
        var arrayParts = await Algebra.FilterAsync(childrenTarget, Iterable, cancellation);
        var otherParts = await Algebra.FilterAsync(childrenTarget, async type => !await Iterable(type), cancellation);
        if (children.Count > 1)
        {
            if (arrayParts != context.NeverType)
            {
                var childTypes = await JsxChildrenAsync(element, 0, cancellation);
                var tuple = await Instantiation.Tuples.CreateAsync(
                    childTypes,
                    childTypes.Select(_ => new TupleElementInfo(ElementFlags.Required)).ToArray(),
                    cancellation: cancellation);
                var indexedParts = await Algebra.FilterAsync(
                    arrayParts,
                    async type => IsArray(type) || await ArrayLiterals.TupleLikeAsync(type, cancellation),
                    cancellation);
                var iteratedParts = await Algebra.FilterAsync(
                    arrayParts,
                    async type => !IsArray(type) && !await ArrayLiterals.TupleLikeAsync(type, cancellation),
                    cancellation);
                var iterated = iteratedParts == context.NeverType
                    ? null
                    : await Iteration.TryAsync(IterationUse.ForOf, iteratedParts, context.UndefinedType, cancellation: cancellation);
                for (int i = 0; i < children.Count; i++)
                {
                    var key = context.GetNumberLiteralType(i);
                    Type? expected = iterated;
                    var indexed = indexedParts == context.NeverType
                        ? null
                        : await Indexed.TryGetAsync(indexedParts, key, cancellation: cancellation);
                    if (indexed is not null && indexed is not IndexedAccessType)
                        expected = expected is null ? indexed : await Algebra.UnionAsync([expected, indexed], cancellation: cancellation);
                    var actual = await Indexed.TryGetAsync(tuple, key, cancellation: cancellation);
                    if (expected is null || actual is null || await Relations.RelatedAsync(actual, expected, relation, cancellation))
                        continue;
                    var child = children[i];
                    var expression = child is JsxExpressionNode jsx ? jsx.Expression : child is JsxTextNode ? null : child;
                    if (expression is not null
                        && await RelationDiagnostics.ElaborateAsync(expression, actual, expected, relation, null, cancellation))
                    {
                        reported = true;
                        continue;
                    }
                    var specific = expression is null ? actual : await Contexts.WithAsync(expression, actual,
                        () => Contexts.MutableAsync(expression, CheckMode.Contextual, cancellation), cancellation);
                    if (child is JsxTextNode)
                        RelationError(child, 2747, CheckerDiagnostic.DeclarationName(element.OpeningElement!.TagName!),
                            nameOfChildren, await TypeDisplay.GetAsync(expected, cancellation));
                    else if (context.ExactOptionalPropertyTypes && Predicates.Maybe(specific, TypeFlags.Undefined, cancellation)
                        && (expected == context.MissingType || expected is UnionType union && union.Types.Contains(context.MissingType)))
                        RelationError(child, 2375);
                    else
                        await ReportRelationAsync(specific, expected, relation, child, null, cancellation);
                    reported = true;
                }
            }
            else if (!await Relations.RelatedAsync(
                await Indexed.GetAsync(source, nameType, cancellation: cancellation),
                childrenTarget,
                relation,
                cancellation))
            {
                RelationError(
                    element.OpeningElement!.TagName!,
                    2746,
                    nameOfChildren,
                    await TypeDisplay.GetAsync(childrenTarget, cancellation));
                reported = true;
            }
        }
        else if (otherParts != context.NeverType)
        {
            var child = children[0];
            var expression = child is JsxExpressionNode jsx ? jsx.Expression : child is JsxTextNode ? null : child;
            reported |= await LiteralElaboration.ElementAsync(
                source,
                target,
                relation,
                child,
                expression,
                nameType,
                child is JsxTextNode ? 2747 : null,
                cancellation);
        }
        else if (!await Relations.RelatedAsync(
            await Indexed.GetAsync(source, nameType, cancellation: cancellation),
            childrenTarget,
            relation,
            cancellation))
        {
            RelationError(element.OpeningElement!.TagName!, 2745, nameOfChildren, await TypeDisplay.GetAsync(childrenTarget, cancellation));
            reported = true;
        }
        return reported;
    }
}

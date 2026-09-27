using System.Globalization;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface ILiteralElaborationHost
{
    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);

    ValueTask<Type> PromiseResultAsync(SyntaxNode node, Type type, bool reportMissing, CancellationToken cancellation);

    ValueTask<bool> ReportRelationAsync(
        Type source,
        Type target,
        RelationKind kind,
        SyntaxNode? node,
        DiagnosticCode? headCode,
        CancellationToken cancellation);

    ValueTask LiteralRelationErrorAsync(SyntaxNode node, DiagnosticCode code, Type source, Type target, CancellationToken cancellation);

    ValueTask ExpectedPropertyInfoAsync(SyntaxNode node, Type target, Type key, Symbol? property, CancellationToken cancellation);

    void ExpectedReturnInfo(ArrowFunctionNode node, Type target, bool suggestAsync);
}

internal sealed class LiteralElaboration(TypeContext context, CheckerSymbols symbols, TypeAlgebra algebra, TypeRelations relations,
    RelationDiagnostics diagnostics, IndexedTypes indexed, TypeKeys keys, TypeProperties properties, SymbolTypes values,
    TypePredicates predicates, BestMatchingTypes best, ExpressionContexts contexts, ArrayLiterals arrays,
    GenericExpressions generic, Signatures signatures, ILiteralElaborationHost host)
{
    internal async ValueTask<bool> CheckAsync(
        SyntaxNode node,
        Type source,
        Type target,
        RelationKind kind,
        CancellationToken cancellation = default)
    {
        if (node is ObjectLiteralExpressionNode literal)
        {
            if ((target.Flags & (TypeFlags.Primitive | TypeFlags.Never)) != 0)
                return false;
            bool reported = false;
            foreach (var property in literal.Properties!)
            {
                if (property is SpreadAssignmentNode)
                    continue;
                var name = await keys.PropertyAsync(
                    symbols.Declaration(property)!,
                    TypeFlags.StringOrNumberLiteralOrUnique,
                    false,
                    cancellation).ConfigureAwait(false);
                if ((name.Flags & TypeFlags.Never) != 0)
                    continue;
                var declarationName = ((INamedNode)property).Name!;
                var next = (property as PropertyAssignmentNode)?.Initializer;
                DiagnosticCode? code = declarationName is ComputedPropertyNameNode computed
                    && computed.Expression is not StringLiteralNode and not NumericLiteralNode
                    ? DiagnosticCode.TypeOfComputedPropertySValueIs0WhichIsNotAssignableToType1
                    : null;
                reported = await ElementAsync(source, target, kind, declarationName, next, name, code, cancellation).ConfigureAwait(false)
                    || reported;
            }
            return reported;
        }
        if (node is ArrayLiteralExpressionNode array)
        {
            if ((target.Flags & (TypeFlags.Primitive | TypeFlags.Never)) != 0)
                return false;
            if (!await arrays.TupleLikeAsync(source, cancellation).ConfigureAwait(false))
            {
                source = await contexts.WithAsync(
                    node,
                    target,
                    () => arrays.CheckAsync(array, CheckMode.Contextual | CheckMode.ForceTuple, cancellation),
                    cancellation).ConfigureAwait(false);
                if (!await arrays.TupleLikeAsync(source, cancellation).ConfigureAwait(false))
                    return false;
            }
            bool reported = false;
            for (int i = 0; i < array.Elements!.Count; i++)
            {
                var element = array.Elements[i];
                if (element.Kind == SyntaxKind.OmittedExpression || await arrays.TupleLikeAsync(target, cancellation).ConfigureAwait(false)
                    && await properties.PropertyAsync(
                        target,
                        i.ToString(CultureInfo.InvariantCulture),
                        cancellation: cancellation).ConfigureAwait(false) is null)
                    continue;
                var check = CallResolution.EffectiveNode(element);
                reported = await ElementAsync(
                    source,
                    target,
                    kind,
                    check,
                    check,
                    context.GetNumberLiteralType(i),
                    null,
                    cancellation).ConfigureAwait(false)
                    || reported;
            }
            return reported;
        }
        if (node is ArrowFunctionNode arrow)
        {
            if (arrow.Body is BlockNode || arrow.Parameters!.Any(p => p is ITypedNode { Type: not null }))
                return false;
            var signature = await generic.SingleAsync(source, cancellation: cancellation).ConfigureAwait(false);
            if (signature is null)
                return false;
            var targets = await host.SignaturesAsync(target, false, cancellation).ConfigureAwait(false);
            if (targets.Count == 0)
                return false;
            var sourceReturn = await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false);
            var returns = new List<Type>();
            foreach (var item in targets)
                returns.Add(await signatures.ReturnAsync(item, cancellation).ConfigureAwait(false));
            var targetReturn = await algebra.UnionAsync(returns, cancellation: cancellation).ConfigureAwait(false);
            if (await relations.RelatedAsync(sourceReturn, targetReturn, kind, cancellation).ConfigureAwait(false))
                return false;
            if (await diagnostics.ElaborateAsync(arrow.Body, sourceReturn, targetReturn, kind, null, cancellation).ConfigureAwait(false))
                return true;
            if (!await host.ReportRelationAsync(sourceReturn, targetReturn, kind, arrow.Body, null, cancellation).ConfigureAwait(false))
            {
                bool suggestAsync = false;
                if (!SemanticSyntax.HasModifier(arrow, SyntaxKind.AsyncKeyword)
                    && await properties.PropertyAsync(sourceReturn, "then", cancellation: cancellation).ConfigureAwait(false) is null)
                    suggestAsync = await relations.RelatedAsync(
                        await host.PromiseResultAsync(node, sourceReturn, false, cancellation).ConfigureAwait(false),
                        targetReturn,
                        kind,
                        cancellation).ConfigureAwait(false);
                host.ExpectedReturnInfo(arrow, target, suggestAsync);
                return true;
            }
        }
        return false;
    }

    internal async ValueTask<bool> ElementAsync(Type source, Type target, RelationKind kind, SyntaxNode property, SyntaxNode? next,
        Type key, DiagnosticCode? code, CancellationToken cancellation)
    {
        var targetType = await indexed.TryGetAsync(target, key, cancellation: cancellation).ConfigureAwait(false);
        if (targetType is null
            && target is UnionType union
            && await best.GetAsync(source, union, cancellation).ConfigureAwait(false) is { } match)
            targetType = await indexed.TryGetAsync(match, key, cancellation: cancellation).ConfigureAwait(false);
        if (targetType is null || targetType is IndexedAccessType)
            return false;
        var sourceType = await indexed.TryGetAsync(source, key, cancellation: cancellation).ConfigureAwait(false);
        if (sourceType is null || await relations.RelatedAsync(sourceType, targetType, kind, cancellation).ConfigureAwait(false))
            return false;
        if (next is not null
            && await diagnostics.ElaborateAsync(next, sourceType, targetType, kind, null, cancellation).ConfigureAwait(false))
            return true;
        var specific = next is null ? sourceType
            : await contexts.WithAsync(
                next,
                sourceType,
                () => contexts.MutableAsync(next, CheckMode.Contextual, cancellation),
                cancellation).ConfigureAwait(false);
        if (context.ExactOptionalPropertyTypes && predicates.Maybe(specific, TypeFlags.Undefined, cancellation)
            && (targetType == context.MissingType
                || targetType is UnionType targetUnion && targetUnion.Types.Contains(context.MissingType)))
        {
            await host.LiteralRelationErrorAsync(
                property,
                DiagnosticCode.Type0IsNotAssignableToType1WithExactOptionalPropertyTypesColonTrueConsiderAddingUndefinedToTheTypeOfTheTarget,
                specific,
                targetType,
                cancellation).ConfigureAwait(false);
            await host.ExpectedPropertyInfoAsync(property, target, key,
                await properties.PropertyAsync(target, MappedMembers.PropertyName(key), cancellation: cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
            return true;
        }
        string name = MappedMembers.PropertyName(key);
        var targetProperty = await properties.PropertyAsync(target, name, cancellation: cancellation).ConfigureAwait(false);
        var sourceProperty = await properties.PropertyAsync(source, name, cancellation: cancellation).ConfigureAwait(false);
        bool targetOptional = targetProperty is not null && (targetProperty.Flags & SymbolFlags.Optional) != 0;
        bool sourceOptional = sourceProperty is not null && (sourceProperty.Flags & SymbolFlags.Optional) != 0;
        targetType = values.NonMissing(targetType, targetOptional);
        sourceType = values.NonMissing(sourceType, targetOptional && sourceOptional);
        bool related = await host.ReportRelationAsync(specific, targetType, kind, property, code, cancellation).ConfigureAwait(false);
        if (related && specific != sourceType)
            related = await host.ReportRelationAsync(sourceType, targetType, kind, property, code, cancellation).ConfigureAwait(false);
        if (!related)
            await host.ExpectedPropertyInfoAsync(property, target, key, targetProperty, cancellation).ConfigureAwait(false);
        return !related;
    }
}

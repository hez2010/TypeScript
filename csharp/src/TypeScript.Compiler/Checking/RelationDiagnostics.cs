using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IRelationDiagnosticHost
{
    bool NoCheck { get; }

    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);

    ValueTask<bool> ReportRelationAsync(
        Type source,
        Type target,
        RelationKind kind,
        SyntaxNode? node,
        DiagnosticCode? headCode,
        CancellationToken cancellation);

    void CallOrConstructHint(SyntaxNode node, bool construct);

    ValueTask<bool> ElaborateComplexAsync(SyntaxNode node, Type source, Type target, RelationKind kind, CancellationToken cancellation);
}

internal sealed class RelationDiagnostics(TypeContext context, TypeRelations relations, Signatures signatures,
    TypeProperties properties, SymbolTypes values, TypePredicates predicates, IRelationDiagnosticHost host)
{
    internal async ValueTask<IReadOnlyList<Symbol>> ExactOptionalPropertiesAsync(
        Type source,
        Type target,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        if (source is TypeReference { Target: TupleType } && target is TypeReference { Target: TupleType })
            return [];
        List<Symbol> result = [];
        foreach (var property in await properties.GetAsync(target, cancellation).ConfigureAwait(false))
        {
            var sourceProperty = await properties.PropertyAsync(source, property.Name, cancellation: cancellation).ConfigureAwait(false);
            var sourceType = sourceProperty is null ? null : await values.GetAsync(sourceProperty, cancellation).ConfigureAwait(false);
            var targetType = await values.GetAsync(property, cancellation).ConfigureAwait(false);
            if (sourceType is not null && predicates.Maybe(sourceType, TypeFlags.Undefined, cancellation)
                && (targetType == context.MissingType || targetType is UnionType union && union.Types.Contains(context.MissingType)))
                result.Add(property);
        }
        return result;
    }

    internal async ValueTask<bool> CheckAsync(Type source, Type target, RelationKind kind, SyntaxNode? errorNode,
        SyntaxNode? expression, DiagnosticCode? headCode = null, CancellationToken cancellation = default)
    {
        if (await relations.RelatedAsync(source, target, kind, cancellation).ConfigureAwait(false))
            return true;
        if (errorNode is not null && !await ElaborateAsync(expression, source, target, kind, headCode, cancellation).ConfigureAwait(false))
            return await host.ReportRelationAsync(source, target, kind, errorNode, headCode, cancellation).ConfigureAwait(false);
        return false;
    }

    internal async ValueTask<bool> ElaborateAsync(
        SyntaxNode? node,
        Type source,
        Type target,
        RelationKind kind,
        DiagnosticCode? headCode,
        CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (node is null || GenericConditional(target) || host.NoCheck)
            return false;
        if (await CallOrConstructAsync(node, source, target, kind, true, headCode, cancellation).ConfigureAwait(false)
            || await CallOrConstructAsync(node, source, target, kind, false, headCode, cancellation).ConfigureAwait(false))
            return true;
        switch (node)
        {
            case AsExpressionNode assertion when SemanticSyntax.ConstAssertion(assertion):
                return await ElaborateAsync(assertion.Expression, source, target, kind, headCode, cancellation).ConfigureAwait(false);
            case JsxExpressionNode jsx:
                return await ElaborateAsync(jsx.Expression, source, target, kind, headCode, cancellation).ConfigureAwait(false);
            case ParenthesizedExpressionNode parentheses:
                return await ElaborateAsync(parentheses.Expression, source, target, kind, headCode, cancellation).ConfigureAwait(false);
            case BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.EqualsToken or SyntaxKind.CommaToken } binary:
                return await ElaborateAsync(binary.Right, source, target, kind, headCode, cancellation).ConfigureAwait(false);
            case ObjectLiteralExpressionNode or ArrayLiteralExpressionNode or ArrowFunctionNode or JsxAttributesNode:
                return await host.ElaborateComplexAsync(node, source, target, kind, cancellation).ConfigureAwait(false);
            default:
                return false;
        }
    }

    private async ValueTask<bool> CallOrConstructAsync(SyntaxNode node, Type source, Type target, RelationKind kind,
        bool construct, DiagnosticCode? headCode, CancellationToken cancellation)
    {
        foreach (var signature in await host.SignaturesAsync(source, construct, cancellation).ConfigureAwait(false))
        {
            var returned = await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false);
            if ((returned.Flags & (TypeFlags.Any | TypeFlags.Never)) != 0
                || !await host.ReportRelationAsync(returned, target, kind, null, null, cancellation).ConfigureAwait(false))
                continue;
            if (!await host.ReportRelationAsync(source, target, kind, node, headCode, cancellation).ConfigureAwait(false))
            {
                host.CallOrConstructHint(node, construct);
                return true;
            }
            break;
        }
        return false;
    }

    private static bool GenericConditional(Type type)
    {
        var pending = new Stack<Type>();
        pending.Push(type);
        while (pending.TryPop(out var part))
            if ((part.Flags & TypeFlags.Conditional) != 0)
                return true;
            else if (part is IntersectionType intersection)
                foreach (var item in intersection.Types)
                    pending.Push(item);
        return false;
    }
}

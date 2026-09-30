using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.Checking;

internal interface IElementAccessErrorHost
{
    bool NoImplicitAny { get; }

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> StaticPropertyAsync(Utf8String name, Type type, CancellationToken cancellation);

    ValueTask<Utf8String?> PropertySuggestionAsync(Utf8String name, Type type, CancellationToken cancellation);

    ValueTask<Utf8String?> IndexSuggestionAsync(Type type, ElementAccessExpressionNode node, Type index, CancellationToken cancellation);

    ValueTask InvalidIndexAsync(
        SyntaxNode node,
        Type objectType,
        Type indexType,
        DiagnosticCode code,
        CancellationToken cancellation,
        Type? fullIndex = null,
        Utf8String? suggestion = null);
}

internal sealed class ElementAccessErrors(TypeContext context, TypeAlgebra algebra, CheckerSymbols symbols, TypeProperties properties,
    SymbolTypes values, IElementAccessErrorHost host)
{
    internal async ValueTask<Type?> MissingAsync(Type original, Type type, Type index, Type fullIndex,
        ElementAccessExpressionNode node, Utf8String? name, AccessFlags flags, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if ((type.ObjectFlags & ObjectFlags.ObjectLiteral) != 0)
        {
            if (host.NoImplicitAny && (index.Flags & TypeFlags.StringOrNumberLiteral) != 0)
            {
                await host.InvalidIndexAsync(
                    node,
                    type,
                    index,
                    DiagnosticCode.Property0DoesNotExistOnType1,
                    cancellation).ConfigureAwait(false);
                return context.UndefinedType;
            }
            if ((index.Flags & (TypeFlags.Number | TypeFlags.String)) != 0)
            {
                var types = new List<Type>();
                foreach (var property in await properties.GetAsync(type, cancellation).ConfigureAwait(false))
                    types.Add(await values.GetAsync(property, cancellation).ConfigureAwait(false));
                types.Add(context.UndefinedType);
                return await algebra.UnionAsync(types, cancellation: cancellation).ConfigureAwait(false);
            }
        }
        if (type.Symbol == symbols.GlobalThisSymbol && name is not null
            && symbols.GlobalThisSymbol.Exports.GetValueOrDefault(name.Value) is { Flags: var globalFlags } && (globalFlags & SymbolFlags.BlockScoped) != 0)
            await host.InvalidIndexAsync(
                node,
                type,
                index,
                DiagnosticCode.Property0DoesNotExistOnType1,
                cancellation).ConfigureAwait(false);
        else if (host.NoImplicitAny && (flags & AccessFlags.SuppressNoImplicitAnyError) == 0)
        {
            if (name is not null && await host.StaticPropertyAsync(name.Value, type, cancellation).ConfigureAwait(false))
                await host.InvalidIndexAsync(
                    node,
                    type,
                    index,
                    DiagnosticCode.Property0DoesNotExistOnType1DidYouMeanToAccessTheStaticMember2Instead,
                    cancellation).ConfigureAwait(false);
            else if ((await host.IndexesAsync(type, cancellation).ConfigureAwait(false)).Any(i => i.KeyType == context.NumberType))
                await host.InvalidIndexAsync(
                    node.ArgumentExpression!,
                    type,
                    index,
                    DiagnosticCode.ElementImplicitlyHasAnAnyTypeBecauseIndexExpressionIsNotOfTypeNumber,
                    cancellation).ConfigureAwait(false);
            else
            {
                Utf8String? suggestion = name is not null
                    ? await host.PropertySuggestionAsync(name.Value, type, cancellation).ConfigureAwait(false)
                    : null;
                if (suggestion is { IsEmpty: false })
                    await host.InvalidIndexAsync(
                        node.ArgumentExpression!,
                        type,
                        index,
                        DiagnosticCode.Property0DoesNotExistOnType1DidYouMean2,
                        cancellation,
                        suggestion: suggestion).ConfigureAwait(false);
                else if (await host.IndexSuggestionAsync(
                    type,
                    node,
                    index,
                    cancellation).ConfigureAwait(false) is { Length: > 0 } indexSuggestion)
                    await host.InvalidIndexAsync(
                        node,
                        type,
                        index,
                        DiagnosticCode.ElementImplicitlyHasAnAnyTypeBecauseType0HasNoIndexSignatureDidYouMeanToCall1,
                        cancellation,
                        suggestion: indexSuggestion).ConfigureAwait(false);
                else
                    await host.InvalidIndexAsync(
                        node,
                        type,
                        index,
                        DiagnosticCode.ElementImplicitlyHasAnAnyTypeBecauseExpressionOfType0CanTBeUsedToIndexType1,
                        cancellation,
                        fullIndex).ConfigureAwait(false);
            }
        }
        return null;
    }
}

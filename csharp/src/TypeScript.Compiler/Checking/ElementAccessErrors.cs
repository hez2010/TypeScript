using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface IElementAccessErrorHost
{
    bool NoImplicitAny { get; }

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> StaticPropertyAsync(string name, Type type, CancellationToken cancellation);

    ValueTask<string?> PropertySuggestionAsync(string name, Type type, CancellationToken cancellation);

    ValueTask<string?> IndexSuggestionAsync(Type type, ElementAccessExpressionNode node, Type index, CancellationToken cancellation);

    ValueTask InvalidIndexAsync(
        SyntaxNode node,
        Type objectType,
        Type indexType,
        int code,
        CancellationToken cancellation,
        Type? fullIndex = null,
        string? suggestion = null);
}

internal sealed class ElementAccessErrors(TypeContext context, TypeAlgebra algebra, CheckerSymbols symbols, TypeProperties properties,
    SymbolTypes values, IElementAccessErrorHost host)
{
    internal async ValueTask<Type?> MissingAsync(Type original, Type type, Type index, Type fullIndex,
        ElementAccessExpressionNode node, string? name, AccessFlags flags, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if ((type.ObjectFlags & ObjectFlags.ObjectLiteral) != 0)
        {
            if (host.NoImplicitAny && (index.Flags & TypeFlags.StringOrNumberLiteral) != 0)
            {
                await host.InvalidIndexAsync(node, type, index, 2339, cancellation).ConfigureAwait(false);
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
            && symbols.GlobalThisSymbol.Exports.GetValueOrDefault(name) is { Flags: var globalFlags } && (globalFlags & SymbolFlags.BlockScoped) != 0)
            await host.InvalidIndexAsync(node, type, index, 2339, cancellation).ConfigureAwait(false);
        else if (host.NoImplicitAny && (flags & AccessFlags.SuppressNoImplicitAnyError) == 0)
        {
            if (name is not null && await host.StaticPropertyAsync(name, type, cancellation).ConfigureAwait(false))
                await host.InvalidIndexAsync(node, type, index, 2576, cancellation).ConfigureAwait(false);
            else if ((await host.IndexesAsync(type, cancellation).ConfigureAwait(false)).Any(i => i.KeyType == context.NumberType))
                await host.InvalidIndexAsync(node.ArgumentExpression!, type, index, 7015, cancellation).ConfigureAwait(false);
            else
            {
                string? suggestion = name is not null
                    ? await host.PropertySuggestionAsync(name, type, cancellation).ConfigureAwait(false)
                    : null;
                if (!string.IsNullOrEmpty(suggestion))
                    await host.InvalidIndexAsync(
                        node.ArgumentExpression!,
                        type,
                        index,
                        2551,
                        cancellation,
                        suggestion: suggestion).ConfigureAwait(false);
                else if (await host.IndexSuggestionAsync(
                    type,
                    node,
                    index,
                    cancellation).ConfigureAwait(false) is { Length: > 0 } indexSuggestion)
                    await host.InvalidIndexAsync(node, type, index, 7052, cancellation, suggestion: indexSuggestion).ConfigureAwait(false);
                else
                    await host.InvalidIndexAsync(node, type, index, 7053, cancellation, fullIndex).ConfigureAwait(false);
            }
        }
        return null;
    }
}

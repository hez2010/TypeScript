using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Semantics;

namespace TypeScript.Compiler.Checking;

internal interface IIndexSignatureHost
{
    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<bool> LateIndexAsync(SyntaxNode declaration, CancellationToken cancellation);

    ValueTask<Type> ComputedKeyAsync(SyntaxNode declaration, CancellationToken cancellation);

    ValueTask<bool> AssignableAsync(Type source, Type target, CancellationToken cancellation);

    ValueTask<bool> SymbolNameAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<bool> NumericNameAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<Type> SymbolTypeAsync(Symbol symbol, CancellationToken cancellation);
}

internal sealed class IndexSignatures(
    TypeContext context,
    CheckerSymbols environment,
    TypeAlgebra algebra,
    MappedMembers mapped,
    IIndexSignatureHost host)
{
    internal async ValueTask<IndexInfo?> ApplicableAsync(
        IReadOnlyList<IndexInfo> indexes,
        Type key,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(key);
        IndexInfo? strings = null;
        var applicable = new List<IndexInfo>();
        foreach (var index in indexes)
            if (index.KeyType == context.StringType)
                strings = index;
            else if (await ApplicableTypeAsync(key, index.KeyType, cancellation).ConfigureAwait(false))
                applicable.Add(index);
        if (applicable.Count == 0)
            return strings is not null && await ApplicableTypeAsync(key, context.StringType, cancellation).ConfigureAwait(false)
                ? strings
                : null;
        if (applicable.Count == 1)
            return applicable[0];
        return context.NewIndexInfo(context.UnknownType,
            await algebra.IntersectionAsync(
                applicable.Select(i => i.ValueType).ToArray(),
                cancellation: cancellation).ConfigureAwait(false),
            applicable.All(i => i.IsReadonly));
    }

    internal async ValueTask<bool> ApplicableTypeAsync(Type source, Type target, CancellationToken cancellation = default)
        => await host.AssignableAsync(source, target, cancellation).ConfigureAwait(false)
            || target == context.StringType && await host.AssignableAsync(source, context.NumberType, cancellation).ConfigureAwait(false)
            || target == context.NumberType
                && (source == context.NumericStringType || source is LiteralType { Value: TextSlice text } && NumericName(text));

    internal static bool NumericName(TextSlice text) => TokenFacts.NumberText(JsNumber.FromString(text)) == text;

    internal async ValueTask<IReadOnlyList<IndexInfo>> ResolveAsync(
        Symbol index,
        IReadOnlyList<Symbol> siblings,
        CancellationToken cancellation = default)
    {
        var result = new List<IndexInfo>();
        bool strings = false, numbers = false, symbols = false;
        bool readonlyStrings = true, readonlyNumbers = true, readonlySymbols = true;
        var properties = new List<Symbol>();
        foreach (var declaration in index.Declarations)
        {
            cancellation.ThrowIfCancellationRequested();
            if (declaration is IndexSignatureDeclarationNode { Parameters.Count: 1 } signature)
            {
                if (((ITypedNode)signature.Parameters[0]).Type is not { } keyNode)
                    continue;
                var value = signature.Type is { } valueNode
                    ? await host.TypeFromNodeAsync(valueNode, cancellation).ConfigureAwait(false)
                    : context.AnyType;
                var keys = await host.TypeFromNodeAsync(keyNode, cancellation).ConfigureAwait(false);
                foreach (var key in keys is UnionType union ? union.Types : [keys])
                    if (await mapped.ValidIndexKeyAsync(key, cancellation).ConfigureAwait(false) && !result.Any(i => i.KeyType == key))
                        result.Add(
                            context.NewIndexInfo(
                                key,
                                value,
                                SemanticSyntax.HasModifier(declaration, SyntaxKind.ReadonlyKeyword),
                                declaration));
            }
            else if (await host.LateIndexAsync(declaration, cancellation).ConfigureAwait(false))
            {
                var key = await host.ComputedKeyAsync(declaration, cancellation).ConfigureAwait(false);
                if (result.Any(i => i.KeyType == key))
                    continue;
                if (await host.AssignableAsync(key, context.StringNumberSymbolType, cancellation).ConfigureAwait(false))
                {
                    bool readOnly = SemanticSyntax.HasModifier(declaration, SyntaxKind.ReadonlyKeyword);
                    if (await host.AssignableAsync(key, context.NumberType, cancellation).ConfigureAwait(false))
                    {
                        numbers = true;
                        readonlyNumbers &= readOnly;
                    }
                    else if (await host.AssignableAsync(key, context.ESSymbolType, cancellation).ConfigureAwait(false))
                    {
                        symbols = true;
                        readonlySymbols &= readOnly;
                    }
                    else
                    {
                        strings = true;
                        readonlyStrings &= readOnly;
                    }
                    properties.Add(environment.Binding(declaration)?.Get(declaration)?.Symbol
                        ?? throw new InvalidOperationException("Late index declaration has no symbol"));
                }
            }
        }
        if (strings || numbers || symbols)
        {
            properties.AddRange(siblings.Where(s => s != index));
            foreach (var (include, key, readOnly) in new[] { (strings, context.StringType, readonlyStrings),
                (numbers, context.NumberType, readonlyNumbers), (symbols, context.ESSymbolType, readonlySymbols) })
                if (include && !result.Any(i => i.KeyType == key))
                    result.Add(await ObjectIndexAsync(properties, key, readOnly, cancellation).ConfigureAwait(false));
        }
        return result.AsReadOnly();
    }

    internal async ValueTask<IndexInfo> ObjectIndexAsync(
        IReadOnlyList<Symbol> properties,
        Type key,
        bool readOnly,
        CancellationToken cancellation = default)
    {
        var types = new List<Type>();
        var declarations = new List<SyntaxNode>();
        foreach (var property in properties)
        {
            bool include = key == context.StringType && !await host.SymbolNameAsync(property, cancellation).ConfigureAwait(false)
                || key == context.NumberType && await host.NumericNameAsync(property, cancellation).ConfigureAwait(false)
                || key == context.ESSymbolType && await host.SymbolNameAsync(property, cancellation).ConfigureAwait(false);
            if (!include)
                continue;
            types.Add(await host.SymbolTypeAsync(property, cancellation).ConfigureAwait(false));
            if (property.Declarations.Length != 0 && (property.Declarations[0] as INamedNode)?.Name is ComputedPropertyNameNode)
                declarations.Add(property.Declarations[0]);
        }
        var value = types.Count == 0
            ? context.UndefinedType
            : await algebra.UnionAsync(types, UnionReduction.Subtype, cancellation: cancellation).ConfigureAwait(false);
        return context.NewIndexInfo(key, value, readOnly, components: declarations.ToArray());
    }

}

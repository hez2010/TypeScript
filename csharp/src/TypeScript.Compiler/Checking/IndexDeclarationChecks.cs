using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IIndexDeclarationHost
{
    ValueTask<bool> BindableNameAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> ComputedKeyAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask IndexPropertyErrorAsync(SyntaxNode node, Symbol property, Type value, IndexInfo index, CancellationToken cancellation);

    ValueTask IndexSignatureErrorAsync(SyntaxNode node, IndexInfo source, IndexInfo target, CancellationToken cancellation);

    ValueTask DuplicateIndexErrorAsync(SyntaxNode node, Type type, CancellationToken cancellation);

    void DuplicatePropertyError(SyntaxNode node, Symbol symbol);
}

internal sealed class IndexDeclarationChecks(CheckerSymbols symbols, TypeNodes nodes, StructuredMembers members,
    TypeProperties properties, SymbolTypes values, IndexSignatures indexes, BaseTypes bases, TypeRelations relations,
    Func<Symbol, CancellationToken, ValueTask<Type>> propertyName, IIndexDeclarationHost host)
{
    internal async ValueTask TypeLiteralAsync(TypeLiteralNode node, CancellationToken cancellation = default)
    {
        var type = (ObjectType)await nodes.FromNodeAsync(node, cancellation).ConfigureAwait(false);
        await CheckAsync(type, false, cancellation).ConfigureAwait(false);
        await DuplicateIndexesAsync(node, cancellation).ConfigureAwait(false);
        DuplicateProperties(node.Members!, cancellation);
    }

    internal void DuplicateProperties(NodeList members, CancellationToken cancellation = default)
    {
        var names = new Dictionary<TextSlice, int>(TextSliceComparer.Ordinal);
        foreach (var member in members)
        {
            cancellation.ThrowIfCancellationRequested();
            if (symbols.Declaration(member) is not { Declarations.Count: > 1 } symbol)
                continue;
            int kind = member is PropertySignatureDeclarationNode
                ? 1
                : member is GetAccessorDeclarationNode or SetAccessorDeclarationNode ? 2 : 0;
            if (kind == 0)
                continue;
            int state = names.GetValueOrDefault(symbol.Name);
            if (state == 0)
                names[symbol.Name] = kind;
            else if (state == 1 || state == 2 && kind != 2)
            {
                foreach (var duplicate in members)
                    if (symbols.Declaration(duplicate)?.Name == symbol.Name && duplicate is INamedNode { Name: { } name })
                        host.DuplicatePropertyError(name, symbol);
                names[symbol.Name] = 3;
            }
        }
    }

    internal async ValueTask CheckAsync(StructuredType type, bool isStatic, CancellationToken cancellation = default)
    {
        var resolved = await members.ResolveAsync(type, cancellation).ConfigureAwait(false);
        if (resolved.IndexInfos.Count == 0)
            return;
        foreach (var property in resolved.Properties ?? [])
        {
            if (isStatic && (property.Flags & SymbolFlags.Prototype) != 0)
                continue;
            var name = await propertyName(property, cancellation).ConfigureAwait(false);
            var value = values.NonMissing(
                await values.GetAsync(property, cancellation).ConfigureAwait(false),
                (property.Flags & SymbolFlags.Optional) != 0);
            await CheckPropertyAsync(property, name, value).ConfigureAwait(false);
        }
        if (type.Symbol?.ValueDeclaration is ClassDeclarationNode or ClassExpressionNode)
            foreach (var member in PropertyInitialization.Members(type.Symbol.ValueDeclaration))
                if (SemanticSyntax.IsStatic(member) == isStatic && !await host.BindableNameAsync(member, cancellation).ConfigureAwait(false)
                    && symbols.Declaration(member) is { } symbol)
                    await CheckPropertyAsync(symbol, await host.ComputedKeyAsync(member, cancellation).ConfigureAwait(false),
                        values.NonMissing(
                            await values.GetAsync(symbol, cancellation).ConfigureAwait(false),
                            (symbol.Flags & SymbolFlags.Optional) != 0))
                        .ConfigureAwait(false);

        async ValueTask CheckPropertyAsync(Symbol property, Type name, Type value)
        {
            var declaration = property.ValueDeclaration;
            if (declaration is INamedNode { Name: PrivateIdentifierNode })
                return;
            foreach (var index in resolved.IndexInfos)
            {
                if (!await indexes.ApplicableTypeAsync(name, index.KeyType, cancellation).ConfigureAwait(false))
                    continue;
                var location = symbols.Parent(property) == type.Symbol ? declaration : null;
                location ??= LocalIndex(type, index);
                if (location is null && type is InterfaceType { ObjectFlags: var flags } iface && (flags & ObjectFlags.Interface) != 0)
                {
                    bool inheritedTogether = false;
                    foreach (var baseType in await bases.GetAsync(iface, cancellation).ConfigureAwait(false))
                        if (await properties.PropertyAsync(
                            baseType,
                            property.Name,
                            cancellation: cancellation).ConfigureAwait(false) is not null
                            && baseType is StructuredType structured && (await members.ResolveAsync(
                                structured,
                                cancellation).ConfigureAwait(false)).IndexInfos.Any(i => i.KeyType == index.KeyType))
                        {
                            inheritedTogether = true;
                            break;
                        }
                    if (!inheritedTogether)
                        location = type.Symbol?.Declarations.OfType<InterfaceDeclarationNode>().FirstOrDefault();
                }
                if (location is not null
                    && !await relations.RelatedAsync(value, index.ValueType, RelationKind.Assignable, cancellation).ConfigureAwait(false))
                    await host.IndexPropertyErrorAsync(location, property, value, index, cancellation).ConfigureAwait(false);
            }
        }
        foreach (var check in resolved.IndexInfos)
            foreach (var index in resolved.IndexInfos)
            {
                if (check == index || !await indexes.ApplicableTypeAsync(check.KeyType, index.KeyType, cancellation).ConfigureAwait(false))
                    continue;
                var location = LocalIndex(type, check) ?? LocalIndex(type, index);
                if (location is null && type is InterfaceType { ObjectFlags: var flags } iface && (flags & ObjectFlags.Interface) != 0)
                {
                    bool inheritedTogether = false;
                    foreach (var baseType in await bases.GetAsync(iface, cancellation).ConfigureAwait(false))
                        if (baseType is StructuredType structured)
                        {
                            var baseIndexes = (await members.ResolveAsync(structured, cancellation).ConfigureAwait(false)).IndexInfos;
                            if (baseIndexes.Any(i => i.KeyType == check.KeyType) && baseIndexes.Any(i => i.KeyType == index.KeyType))
                            {
                                inheritedTogether = true;
                                break;
                            }
                        }
                    if (!inheritedTogether)
                        location = type.Symbol?.Declarations.OfType<InterfaceDeclarationNode>().FirstOrDefault();
                }
                if (location is not null
                    && !await relations.RelatedAsync(
                        check.ValueType,
                        index.ValueType,
                        RelationKind.Assignable,
                        cancellation).ConfigureAwait(false))
                    await host.IndexSignatureErrorAsync(location, check, index, cancellation).ConfigureAwait(false);
            }
    }

    private SyntaxNode? LocalIndex(Type type, IndexInfo index) => index.Declaration is { } declaration
        && symbols.Declaration(declaration) is { } symbol && symbols.Parent(symbol) == type.Symbol ? declaration : null;

    internal async ValueTask DuplicateIndexesAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        var symbol = symbols.Declaration(node);
        if (symbol?.Members.GetValueOrDefault(Symbol.InternalPrefix + "index") is not { Declarations.Count: > 1 } index)
            return;
        var groups = new Dictionary<Type, List<SyntaxNode>>();
        foreach (var declaration in index.Declarations)
            if (declaration is IndexSignatureDeclarationNode { Parameters.Count: 1 } signature
                && signature.Parameters[0] is ITypedNode { Type: { } annotation })
            {
                var type = await nodes.FromNodeAsync(annotation, cancellation).ConfigureAwait(false);
                foreach (var part in type is UnionType union ? union.Types : [type])
                {
                    if (!groups.TryGetValue(part, out var declarations))
                        groups.Add(part, declarations = []);
                    declarations.Add(declaration);
                }
            }
        foreach (var (type, declarations) in groups)
            if (declarations.Count > 1)
                foreach (var declaration in declarations)
                    await host.DuplicateIndexErrorAsync(declaration, type, cancellation).ConfigureAwait(false);
    }
}

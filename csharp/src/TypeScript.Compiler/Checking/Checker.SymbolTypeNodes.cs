using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask<string> GetSymbolTypeReferenceAsync(Symbol symbol, SyntaxNode? enclosing, SymbolFlags meaning,
        IReadOnlyList<SyntaxNode>? typeArguments = null, bool externalAliasesOnly = false, bool aliasesOutsideScope = false,
        bool forbidIndexedAccess = false, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(enclosing, cancellation).ConfigureAwait(false);
        var flags = (externalAliasesOnly ? SymbolFormatFlags.UseOnlyExternalAliasing : 0)
            | (aliasesOutsideScope ? SymbolFormatFlags.UseAliasDefinedOutsideCurrentScope : 0);
        var arguments = typeArguments is null ? null : new NodeList(typeArguments.ToArray());
        return await ChainOperationAsync(() => ContainerOperationAsync(async () =>
        {
            var node = await SymbolTypeNodeAsync(symbol, meaning, arguments, new(enclosing, flags), forbidIndexedAccess, cancellation);
            return PrintDiagnosticNode(
                node,
                enclosing is SourceFileNode,
                cancellation,
                enclosing is null ? null : SemanticSyntax.Source(enclosing));
        }, cancellation), cancellation);
    }

    private async ValueTask<SyntaxNode> SymbolTypeNodeAsync(Symbol symbol, SymbolFlags meaning, NodeList? arguments,
        SymbolDisplayContext state, bool forbidIndexed, CancellationToken cancellation)
    {
        var factory = new NodeFactory();
        forbidIndexed |= state.ForbidIndexedAccess;
        List<Symbol> chain = state.Enclosing is null && !state.FullyQualified || (symbol.Flags & SymbolFlags.TypeParameter) != 0 ? [symbol]
            : (await DisplaySymbolChainAsync(symbol, meaning, true, state, cancellation,
                (state.Flags & SymbolFormatFlags.UseAliasDefinedOutsideCurrentScope) == 0))!;
        bool typeOf = meaning == SymbolFlags.Value;
        if (chain[0].Declarations.Any(NonGlobalExternalModule))
        {
            SyntaxNode? qualifier = chain.Count > 1
                ? await TypeAccessFromChainAsync(chain, chain.Count - 1, 1, arguments, state, forbidIndexed, factory, cancellation) : null;
            var importArguments = arguments ?? await QualifiedTypeArgumentsAsync(chain, 0, state, cancellation);
            ReferenceResolutionMode mode = 0;
            var contextFile = state.Enclosing is null ? null : SemanticSyntax.Source(state.Enclosing);
            var targetFile = chain[0].Declarations.OfType<SourceFileNode>().FirstOrDefault();
            if (program.Symbols.Program.ModuleResolutionKind is "node16" or "nodenext"
                && targetFile is not null && contextFile is not null
                && program.Symbols.Program.ResolutionModeForUsage(targetFile, null) == ReferenceResolutionMode.Import
                && program.Symbols.Program.ResolutionModeForUsage(contextFile, null) != ReferenceResolutionMode.Import)
                mode = ReferenceResolutionMode.Import;
            string specifier = await DisplayModuleSpecifierAsync(chain[0], state, cancellation, mode);
            var attributes = await TypeImportAttributesAsync(chain[0], specifier, mode, state, factory, cancellation);
            var argument = factory.NewLiteralTypeNode(factory.NewStringLiteral(specifier, state.StringLiteralFlags));
            state.Length?.Add(specifier, 10);
            if (qualifier is null or IdentifierNode or QualifiedNameNode)
                return factory.NewImportTypeNode(typeOf, argument, attributes, qualifier, importArguments);
            if (qualifier is not IndexedAccessTypeNode indexed)
                throw new InvalidOperationException("Unexpected module type qualification");
            while (indexed.ObjectType is IndexedAccessTypeNode parent)
                indexed = parent;
            if (indexed.ObjectType is not TypeReferenceNode reference)
                throw new InvalidOperationException("Indexed module qualification has no type reference");
            return factory.NewIndexedAccessTypeNode(
                factory.NewImportTypeNode(typeOf, argument, attributes, reference.TypeName, importArguments),
                indexed.IndexType);
        }
        var name = await TypeAccessFromChainAsync(chain, chain.Count - 1, 0, arguments, state, forbidIndexed, factory, cancellation);
        if (name is IndexedAccessTypeNode)
            return name;
        if (name is ExpressionWithTypeArgumentsNode expression && typeOf)
            return factory.NewTypeQueryNode(CloneTypeName(expression.Expression!, factory), expression.TypeArguments);
        if (name is not (IdentifierNode or QualifiedNameNode))
            return name;
        return typeOf ? factory.NewTypeQueryNode(name, null) : factory.NewTypeReferenceNode(name, arguments);
    }

    private async ValueTask<SyntaxNode> TypeAccessFromChainAsync(IReadOnlyList<Symbol> chain, int index, int stopper,
        NodeList? arguments, SymbolDisplayContext state, bool forbidIndexed, NodeFactory factory, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var symbol = chain[index];
        var typeParameterNodes = index == chain.Count - 1
            ? arguments
            : await QualifiedTypeArgumentsAsync(chain, index, state, cancellation);
        var parent = index > 0 ? chain[index - 1] : null;
        string name = index == 0 ? DisplayNameAsWritten(symbol, state, true, cancellation) : "";
        if (index == 0 && !state.ExpressionNames)
            state.Length?.Add(name, 1);
        if (index > 0 && parent is not null)
        {
            var exports = await ExportsAsync(parent, cancellation);
            if (symbol.Name != "export=" && !LateName(symbol.Name) && exports.GetValueOrDefault(symbol.Name) is { } direct
                && await SameSymbolReferenceAsync(direct, symbol, cancellation))
                name = symbol.Name;
            else
            {
                var matches = new Dictionary<Symbol, string>();
                foreach (var entry in exports)
                    if (entry.Key != "export=" && !LateName(entry.Key) && await SameSymbolReferenceAsync(entry.Value, symbol, cancellation))
                        matches[entry.Value] = entry.Key;
                if (matches.Count != 0)
                {
                    var sorted = matches.Keys.ToList();
                    sorted.Sort(Algebra.Order.CompareSymbols);
                    name = matches[sorted[0]];
                }
            }
        }
        if (name.Length == 0)
        {
            var declaredName = symbol.Declarations.Select(DisplayDeclarationName).FirstOrDefault(n => n is not null);
            if (declaredName is ComputedPropertyNameNode { Expression: IdentifierNode or QualifiedNameNode } computed)
            {
                var left = await TypeAccessFromChainAsync(
                    chain,
                    index - 1,
                    stopper,
                    arguments,
                    state,
                    forbidIndexed,
                    factory,
                    cancellation);
                return left is IdentifierNode or QualifiedNameNode
                    ? factory.NewIndexedAccessTypeNode(
                        factory.NewParenthesizedTypeNode(factory.NewTypeQueryNode(left, null)),
                        factory.NewTypeQueryNode(computed.Expression, null))
                    : left;
            }
            name = DisplayNameAsWritten(symbol, state, false, cancellation);
        }
        state.Length?.Add(name, 1);
        if (!forbidIndexed && parent is not null && (await MembersAsync(parent, cancellation)).GetValueOrDefault(symbol.Name) is { } member
            && await SameSymbolReferenceAsync(member, symbol, cancellation))
        {
            var left = await TypeAccessFromChainAsync(chain, index - 1, stopper, arguments, state, forbidIndexed, factory, cancellation);
            return factory.NewIndexedAccessTypeNode(
                left is IndexedAccessTypeNode ? left : factory.NewTypeReferenceNode(left, typeParameterNodes),
                factory.NewLiteralTypeNode(factory.NewStringLiteral(name, state.StringLiteralFlags)));
        }
        var identifier = factory.NewIdentifier(name);
        if (index <= stopper)
            return identifier;
        var lhs = await TypeAccessFromChainAsync(chain, index - 1, stopper, arguments, state, forbidIndexed, factory, cancellation);
        var typeArguments = typeParameterNodes;
        if (!state.InstantiationExpressions || lhs is IdentifierNode or QualifiedNameNode && typeArguments is not { Count: > 0 })
            return factory.NewQualifiedName(lhs, identifier);
        SyntaxNode access = factory.NewPropertyAccessExpression(TypeNameExpression(lhs, factory), null, identifier, NodeFlags.None);
        return typeArguments is { Count: > 0 } ? factory.NewExpressionWithTypeArguments(access, typeArguments) : access;
    }

    private static SyntaxNode TypeNameExpression(SyntaxNode node, NodeFactory factory)
    {
        if (node is not QualifiedNameNode)
            return node;
        var parts = new Stack<SyntaxNode>();
        while (node is QualifiedNameNode qualified)
        {
            parts.Push(qualified.Right!);
            node = qualified.Left!;
        }
        foreach (var part in parts)
            node = factory.NewPropertyAccessExpression(node, null, part, NodeFlags.None);
        return node;
    }

    private static SyntaxNode CloneTypeName(SyntaxNode node, NodeFactory factory)
    {
        var clone = node.DeepClone<SyntaxNode>(factory);
        foreach (var child in clone.DescendantsAndSelf())
            child.Parent = null;
        return clone;
    }

    private async ValueTask<ImportAttributesNode?> TypeImportAttributesAsync(Symbol symbol, string specifier,
        ReferenceResolutionMode mode, SymbolDisplayContext state, NodeFactory factory, CancellationToken cancellation)
    {
        Type attributes = await ModuleImportAttributesAsync(symbol, cancellation);
        if (state.Enclosing is not null && DisplayOriginalSpecifier(state.Enclosing) is { } original
            && await ModuleSpecifierAttributesAsync(original, cancellation) is { } supplied)
        {
            var usageMode = program.Symbols.Program.ResolutionModeForUsage(SemanticSyntax.Source(state.Enclosing)!, original);
            var resolved = await ResolveImportModuleAsync(state.Enclosing, factory.NewStringLiteral(specifier, TokenFlags.None), supplied,
                cancellation, ignoreErrors: true, resolutionMode: usageMode);
            if (resolved is not null && program.Symbols.Merger.GetMergedSymbol(resolved) == program.Symbols.Merger.GetMergedSymbol(symbol))
                attributes = supplied;
        }
        var entries = new List<SyntaxNode>();
        if (mode != 0)
        {
            entries.Add(factory.NewImportAttribute(factory.NewStringLiteral("resolution-mode", state.StringLiteralFlags),
                factory.NewStringLiteral(mode == ReferenceResolutionMode.Import ? "import" : "require", state.StringLiteralFlags)));
            state.Length?.Add("resolution-mode", (mode == ReferenceResolutionMode.Import ? 6 : 7) + 6);
        }
        var properties = (await PropertiesAsync(attributes, cancellation)).ToList();
        properties.Sort((a, b) => TypeOrder.CompareText(a.Name, b.Name));
        foreach (var property in properties)
            if (await Values.GetAsync(property, cancellation) is LiteralType { Value: string value })
            {
                entries.Add(factory.NewImportAttribute(IdentifierName(property.Name) ? factory.NewIdentifier(property.Name)
                    : factory.NewStringLiteral(property.Name, state.StringLiteralFlags),
                    factory.NewStringLiteral(value, state.StringLiteralFlags)));
                state.Length?.Add(property.Name, IdentifierName(property.Name) ? 4 : 6);
                state.Length?.Add(value);
            }
        if (entries.Count != 0)
            state.Length?.Add(16 + 2 * (entries.Count - 1));
        return entries.Count == 0 ? null : factory.NewImportAttributes(SyntaxKind.WithKeyword, new(entries.ToArray()), false);
    }

    private static bool LateName(string name) => name.StartsWith(Symbol.InternalPrefix + "@", StringComparison.Ordinal);
}

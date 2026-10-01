using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask<SyntaxNode> SymbolExpressionSyntaxAsync(Symbol symbol, SymbolDisplayContext scope, TypeSyntaxContext state,
        CancellationToken cancellation)
    {
        var chain = (await DisplaySymbolChainAsync(symbol, SymbolFlags.Value, true, scope, cancellation))!;
        SyntaxNode? expression = null;
        for (int i = 0; i < chain.Count; i++)
        {
            var part = chain[i];
            var name = DisplayNameAsWritten(part, scope, i == 0, cancellation);
            if (Quoted(name) && part.Declarations.Any(NonGlobalExternalModule))
                expression = state.Factory.NewStringLiteral(await DisplayModuleSpecifierAsync(part, scope, cancellation), TokenFlags.None);
            else if (expression is null || IdentifierName(name))
            {
                var identifier = state.Factory.NewIdentifier(name);
                state.NoAsciiEscape.Add(identifier);
                expression = expression is null ? identifier : state.Factory.NewPropertyAccessExpression(expression, null, identifier, NodeFlags.None);
            }
            else
            {
                if (name.Span.StartsWith((byte)'[')) name = name[1..^1];
                var key = Quoted(name) ? (SyntaxNode)state.Factory.NewStringLiteral(UnquoteSymbolText(name), TokenFlags.None)
                    : state.Factory.NewNumericLiteral(name, TokenFlags.None);
                expression = state.Factory.NewElementAccessExpression(expression, null, key, NodeFlags.None);
            }
        }
        return expression!;
    }

    private async ValueTask TrackComputedNameAsync(SyntaxNode expression, TypeSyntaxContext state, bool existing, CancellationToken cancellation)
    {
        var first = expression;
        while (first is QualifiedNameNode or PropertyAccessExpressionNode)
            first = first is QualifiedNameNode qualified ? qualified.Left! : ((PropertyAccessExpressionNode)first).Expression!;
        if (first is not IdentifierNode identifier)
            return;
        var resolver = program.Symbols.NameResolver(cancellation);
        var meaning = SymbolFlags.Value | SymbolFlags.ExportValue;
        var symbol = resolver.Resolve(state.Symbols.Enclosing, identifier.Text, meaning, isUse: true)
            ?? resolver.Resolve(identifier, identifier.Text, meaning, isUse: true);
        if (symbol is not null)
            await TrackTypeSymbolAsync(symbol, state.Symbols.Enclosing, existing ? meaning : SymbolFlags.Value, state, cancellation);
    }

    internal async ValueTask<Utf8String> GetSymbolTypeReferenceAsync(Symbol symbol, SyntaxNode? enclosing, SymbolFlags meaning,
        IReadOnlyList<SyntaxNode>? typeArguments = null, bool externalAliasesOnly = false, bool aliasesOutsideScope = false,
        bool forbidIndexedAccess = false, CancellationToken cancellation = default, INodeBuilderSymbolTracker? tracker = null,
        NodeBuilderInternalFlags internalFlags = NodeBuilderInternalFlags.None)
    {
        using var query = await EnterQueryAsync(enclosing, cancellation).ConfigureAwait(false);
        var flags = (externalAliasesOnly ? SymbolFormatFlags.UseOnlyExternalAliasing : 0)
            | (aliasesOutsideScope ? SymbolFormatFlags.UseAliasDefinedOutsideCurrentScope : 0);
        var arguments = typeArguments is null ? null : new NodeList(typeArguments.ToArray());
        return await ChainOperationAsync(() => ContainerOperationAsync(async () =>
        {
            var types = tracker is null && internalFlags == NodeBuilderInternalFlags.None
                ? null
                : new TypeSyntaxContext(
                    enclosing,
                    aliasesOutsideScope,
                    externalAliasesOnly,
                    tracker: tracker,
                    internalFlags: internalFlags);
            var node = await SymbolTypeNodeAsync(
                symbol,
                meaning,
                arguments,
                types?.Symbols ?? new(enclosing, flags),
                forbidIndexedAccess,
                cancellation);
            if (types is not null && !FinishTypeSyntax(types))
                return Utf8String.Empty;
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
        var factory = new NodeFactory { OnCreate = node => node.Flags |= NodeFlags.Synthesized };
        if (state.Types is { } tracking)
            await TrackTypeSymbolAsync(symbol, state.Enclosing, meaning, tracking, cancellation);
        forbidIndexed |= state.ForbidIndexedAccess;
        List<Symbol> chain = state.Enclosing is null && !state.FullyQualified || (symbol.Flags & SymbolFlags.TypeParameter) != 0
            || state.Types is { InternalFlags: var internalFlags }
                && (internalFlags & NodeBuilderInternalFlags.DoNotIncludeSymbolChain) != 0 ? [symbol]
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
            if ((program.Symbols.Program.ModuleResolutionKind == "node16"u8 || program.Symbols.Program.ModuleResolutionKind == "nodenext"u8)
                && targetFile is not null && contextFile is not null
                && program.Symbols.Program.ResolutionModeForUsage(targetFile, null) == ReferenceResolutionMode.Import
                && program.Symbols.Program.ResolutionModeForUsage(contextFile, null) != ReferenceResolutionMode.Import)
                mode = ReferenceResolutionMode.Import;
            Utf8String specifier = await DisplayModuleSpecifierAsync(chain[0], state, cancellation, mode);
            if (state.Types is { } types && (types.Flags & NodeBuilderFlags.AllowNodeModulesRelativePaths) == 0
                && specifier.Span.Contains("/node_modules/"u8, StringComparison.Ordinal))
            {
                Utf8String original = specifier;
                if ((program.Symbols.Program.ModuleResolutionKind == "node16"u8 || program.Symbols.Program.ModuleResolutionKind == "nodenext"u8) && contextFile is not null)
                {
                    var swapped = program.Symbols.Program.ResolutionModeForUsage(contextFile, null) == ReferenceResolutionMode.Import
                        ? ReferenceResolutionMode.Require : ReferenceResolutionMode.Import;
                    var alternate = await DisplayModuleSpecifierAsync(chain[0], state, cancellation, swapped);
                    if (!alternate.Span.Contains("/node_modules/"u8, StringComparison.Ordinal))
                    {
                        specifier = alternate;
                        mode = swapped;
                    }
                }
                if (mode == 0)
                {
                    types.EncounteredError = true;
                    types.Tracker.ReportLikelyUnsafeImportRequiredError(original, symbol.Name);
                }
            }
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
        Utf8String name = index == 0 ? DisplayNameAsWritten(symbol, state, true, cancellation) : Utf8String.Empty;
        if (index == 0 && !state.ExpressionNames)
            state.Length?.Add(name, 1);
        if (index > 0 && parent is not null)
        {
            var exports = await ExportsAsync(parent, cancellation);
            if (symbol.Name != Utf8Literals.ExportEquals && !LateName(symbol.Name) && exports.GetValueOrDefault(symbol.Name) is { } direct
                && await SameSymbolReferenceAsync(direct, symbol, cancellation))
                name = symbol.Name;
            else
            {
                var matches = new Dictionary<Symbol, Utf8String>();
                foreach (var entry in exports)
                    if (entry.Key != Utf8Literals.ExportEquals && !LateName(entry.Key) && await SameSymbolReferenceAsync(entry.Value, symbol, cancellation))
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

    private async ValueTask<ImportAttributesNode?> TypeImportAttributesAsync(Symbol symbol, Utf8String specifier,
        ReferenceResolutionMode mode, SymbolDisplayContext state, NodeFactory factory, CancellationToken cancellation)
    {
        Type attributes = await ModuleImportAttributesAsync(symbol, cancellation);
        if (state.Enclosing is not null && DisplayOriginalSpecifier(state.Enclosing) is { } original
            && await ModuleSpecifierAttributesAsync(original, cancellation) is { } supplied)
        {
            var resolved = await ResolveImportModuleAsync(state.Enclosing, factory.NewStringLiteral(specifier, TokenFlags.None), supplied,
                cancellation, ignoreErrors: true);
            if (resolved is not null && program.Symbols.Merger.GetMergedSymbol(resolved) == program.Symbols.Merger.GetMergedSymbol(symbol))
                attributes = supplied;
        }
        var entries = new List<SyntaxNode>();
        if (mode != 0)
        {
            entries.Add(factory.NewImportAttribute(factory.NewStringLiteral(Utf8Literals.ResolutionMode, state.StringLiteralFlags),
                factory.NewStringLiteral(mode == ReferenceResolutionMode.Import ? Utf8Literals.ImportKeyword : Utf8Literals.RequireKeyword, state.StringLiteralFlags)));
            state.Length?.Add(Utf8Literals.ResolutionMode, (mode == ReferenceResolutionMode.Import ? 6 : 7) + 6);
        }
        var properties = (await PropertiesAsync(attributes, cancellation)).ToList();
        properties.Sort((a, b) => TypeOrder.CompareText(a.Name, b.Name));
        foreach (var property in properties)
            if (await Values.GetAsync(property, cancellation) is LiteralType { Value: Utf8String value })
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

    private static bool LateName(Utf8String name) => name.Span.StartsWith(Symbol.InternalUnique, StringComparison.Ordinal);
}

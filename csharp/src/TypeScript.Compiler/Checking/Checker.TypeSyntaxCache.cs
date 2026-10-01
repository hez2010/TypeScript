using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly record struct SerializedTypeKey(SyntaxNode Enclosing, Type Type, NodeBuilderFlags Flags,
        NodeBuilderInternalFlags InternalFlags);

    private readonly record struct TrackedTypeSymbol(Symbol Symbol, SyntaxNode? Enclosing, SymbolFlags Meaning);

    private sealed record SerializedType(SyntaxNode Node, IReadOnlyDictionary<SyntaxNode, (bool NoAscii, bool SingleLine, SyntaxNode? CommentSource)> Printing,
            TrackedTypeSymbol[] Symbols, long Length, bool Truncated);

    private readonly Dictionary<SerializedTypeKey, SerializedType> serializedTypeSyntax = [];
    internal int SerializedTypeSyntaxCount => serializedTypeSyntax.Count;

    private async ValueTask<SyntaxNode> CachedTypeSyntaxAsync(Type type, TypeSyntaxContext state,
        Func<ValueTask<SyntaxNode>> build, CancellationToken cancellation)
    {
        var enclosing = state.Symbols.Enclosing;
        var flags = state.Flags | (state.Symbols.FullyQualified ? NodeBuilderFlags.UseFullyQualifiedType : 0)
            | ((state.Symbols.Flags & SymbolFormatFlags.UseAliasDefinedOutsideCurrentScope) != 0
                ? NodeBuilderFlags.UseAliasDefinedOutsideCurrentScope : 0)
            | ((state.Symbols.Flags & SymbolFormatFlags.UseOnlyExternalAliasing) != 0 ? NodeBuilderFlags.UseOnlyExternalAliasing : 0);
        var key = new SerializedTypeKey(enclosing!, type, flags, state.InternalFlags);
        if (enclosing is not null && (state.PendingTypes.TryGetValue(key, out var cached) || serializedTypeSyntax.TryGetValue(key, out cached)))
        {
            foreach (var symbol in cached.Symbols)
                await TrackTypeSymbolAsync(symbol.Symbol, symbol.Enclosing, symbol.Meaning, state, cancellation);
            state.Length.Add(cached.Length);
            state.Length.WasTruncated |= cached.Truncated;
            var clone = cached.Node.DeepClone<SyntaxNode>(state.Factory);
            using var originals = cached.Node.DescendantsAndSelf().GetEnumerator();
            foreach (var node in clone.DescendantsAndSelf())
            {
                cancellation.ThrowIfCancellationRequested();
                originals.MoveNext();
                var original = originals.Current;
                node.Parent = original.Parent is null ? null : node.Parent ?? original.Parent;
                var printing = cached.Printing.GetValueOrDefault(original);
                if (printing.NoAscii)
                    state.NoAsciiEscape.Add(node);
                if (printing.SingleLine)
                    state.SingleLine.Add(node);
                if (printing.CommentSource is { } source)
                    state.CommentSources[node] = source;
            }
            return clone;
        }
        (bool Constructor, Symbol? Symbol, SyntaxNode? Node)? identity = type switch
        {
            TypeReference { Node: { } node } => (false, null, node),
            ConditionalType conditional => (false, null, conditional.Root.Node),
            { Symbol: { } symbol } => ((type.ObjectFlags & ObjectFlags.Anonymous) != 0
                && (symbol.Flags & SymbolFlags.Class) != 0, symbol, null),
            _ => null
        };
        int depth = identity is { } id ? state.SymbolDepth.GetValueOrDefault(id) : 0;
        // Instantiations share their declaration identity even when their type objects differ.
        // Match the reference node builder's maximum expansion of that identity.
        if (identity is { } recursive)
        {
            if (depth > 10)
                return ElidedTypeSyntax(state);
            state.SymbolDepth[recursive] = depth + 1;
        }
        var previous = state.TrackedSymbols;
        state.TrackedSymbols = [];
        long length = state.Length.Value;
        try
        {
            var node = await build();
            if (enclosing is not null && !state.ReportedDiagnostic && !state.EncounteredError)
            {
                var pending = new Stack<SyntaxNode>();
                pending.Push(node);
                while (pending.TryPop(out var child))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!state.PendingPrinting.TryAdd(child, (state.NoAsciiEscape.Contains(child), state.SingleLine.Contains(child),
                        state.CommentSources.GetValueOrDefault(child))))
                        continue;
                    for (int i = 0; i < child.ChildCount; i++)
                        pending.Push(child.GetChild(i));
                }
                state.PendingTypes[key] = new(node, state.PendingPrinting,
                    state.TrackedSymbols.ToArray(), state.Length.Value - length, state.Length.WasTruncated);
            }
            return node;
        }
        finally
        {
            state.TrackedSymbols = previous;
            if (identity is { } restored)
                state.SymbolDepth[restored] = depth;
        }
    }

    private sealed class NodeBuilderTracker(TypeSyntaxContext state, INodeBuilderSymbolTracker? inner) : INodeBuilderSymbolTracker
    {
        public bool NeedsSymbolAccessibility => inner?.NeedsSymbolAccessibility == true;

        public bool TrackAccessibleSymbol(Symbol symbol, SyntaxNode? enclosingDeclaration, SymbolFlags meaning,
            SymbolAccessibilityResult accessibility)
        {
            if (inner?.TrackAccessibleSymbol(symbol, enclosingDeclaration, meaning, accessibility) == true)
            {
                state.DiagnosticCount++;
                return true;
            }
            if ((symbol.Flags & SymbolFlags.TypeParameter) == 0)
                state.TrackedSymbols.Add(new(symbol, enclosingDeclaration, meaning));
            return false;
        }

        public bool TrackSymbol(Symbol symbol, SyntaxNode? enclosingDeclaration, SymbolFlags meaning)
        {
            if (inner?.TrackSymbol(symbol, enclosingDeclaration, meaning) == true)
            {
                state.DiagnosticCount++;
                return true;
            }
            if ((symbol.Flags & SymbolFlags.TypeParameter) == 0)
                state.TrackedSymbols.Add(new(symbol, enclosingDeclaration, meaning));
            return false;
        }

        public void ReportInaccessibleThisError()
        {
            state.DiagnosticCount++;
            inner?.ReportInaccessibleThisError();
        }

        public void ReportPrivateInBaseOfClassExpression(Utf8String propertyName)
        {
            state.DiagnosticCount++;
            inner?.ReportPrivateInBaseOfClassExpression(propertyName);
        }

        public void ReportInaccessibleUniqueSymbolError()
        {
            state.DiagnosticCount++;
            inner?.ReportInaccessibleUniqueSymbolError();
        }

        public void ReportCyclicStructureError()
        {
            state.DiagnosticCount++;
            inner?.ReportCyclicStructureError();
        }

        public void ReportLikelyUnsafeImportRequiredError(Utf8String specifier, Utf8String symbolName)
        {
            state.DiagnosticCount++;
            inner?.ReportLikelyUnsafeImportRequiredError(specifier, symbolName);
        }

        public void ReportTruncationError()
        {
            state.DiagnosticCount++;
            inner?.ReportTruncationError();
        }

        public void ReportNonlocalAugmentation(SourceFileNode containingFile, Symbol parentSymbol, Symbol augmentingSymbol)
        {
            state.DiagnosticCount++;
            inner?.ReportNonlocalAugmentation(containingFile, parentSymbol, augmentingSymbol);
        }

        public void ReportNonSerializableProperty(Utf8String propertyName)
        {
            state.DiagnosticCount++;
            inner?.ReportNonSerializableProperty(propertyName);
        }

        public void ReportInferenceFallback(SyntaxNode node) => inner?.ReportInferenceFallback(node);

        public void PushErrorFallbackNode(SyntaxNode node) => inner?.PushErrorFallbackNode(node);

        public void PopErrorFallbackNode() => inner?.PopErrorFallbackNode();
    }
}

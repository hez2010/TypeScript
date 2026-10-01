using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal ValueTask<SyntaxNode?> CreateExpandoTypeForEmitAsync(SyntaxNode declaration, SyntaxNode enclosing,
        Symbol host, Symbol property, Utf8String namespaceName, Utf8String localName, EmitContext emission,
        NodeBuilderFlags flags, INodeBuilderSymbolTracker tracker, NodeBuilderInternalFlags internalFlags,
        CancellationToken cancellation = default) =>
        EmitSyntaxQueryAsync<SyntaxNode?>(declaration, enclosing, flags | NodeBuilderFlags.MultilineObjectLiterals, async state =>
        {
            var scope = state.Factory.NewModuleDeclaration(null, SyntaxKind.NamespaceKeyword, state.Factory.NewIdentifier(namespaceName), null,
                state.Factory.NewModuleBlock(new([])));
            scope.Flags |= NodeFlags.Synthesized;
            scope.Parent = enclosing;
            scope.BindingId = program.Symbols.Binding(enclosing)!.Id;
            scope.BindingSymbol = host;
            typeSyntaxScopes.Add(scope, new Dictionary<Utf8String, Symbol> { [localName] = property });
            state.Symbols.Enclosing = scope;
            try
            {
                return CopyEmitSyntax(await DeclarationTypeForEmitAsync(declaration, scope, state, cancellation), state, emission, cancellation);
            }
            finally
            {
                state.Symbols.Enclosing = enclosing;
                typeSyntaxScopes.Remove(scope);
                scope.ClearBindingState();
            }
        }, null, cancellation, tracker, internalFlags);

    internal ValueTask<SyntaxNode?> CreateExpressionTypeForEmitAsync(SyntaxNode expression, SyntaxNode? enclosing,
        EmitContext emission, NodeBuilderFlags flags, INodeBuilderSymbolTracker? tracker,
        NodeBuilderInternalFlags internalFlags, CancellationToken cancellation = default) =>
        EmitSyntaxQueryAsync<SyntaxNode?>(expression, enclosing, flags | NodeBuilderFlags.MultilineObjectLiterals, async state =>
            CopyEmitSyntax(await ExpressionTypeForEmitAsync(expression, enclosing, state, cancellation), state, emission, cancellation),
            null, cancellation, tracker, internalFlags);

    internal ValueTask<SyntaxNode?> CreateDeclarationTypeForEmitAsync(SyntaxNode declaration, SyntaxNode? enclosing,
        EmitContext emission, NodeBuilderFlags flags, INodeBuilderSymbolTracker? tracker,
        NodeBuilderInternalFlags internalFlags, CancellationToken cancellation = default) =>
        EmitSyntaxQueryAsync<SyntaxNode?>(declaration, enclosing, flags | NodeBuilderFlags.MultilineObjectLiterals, async state =>
            CopyEmitSyntax(await DeclarationTypeForEmitAsync(declaration, enclosing, state, cancellation), state, emission, cancellation),
            null, cancellation, tracker, internalFlags);

    internal ValueTask<SyntaxNode?> CreateReturnTypeForEmitAsync(SyntaxNode declaration, SyntaxNode? enclosing,
        EmitContext emission, NodeBuilderFlags flags, INodeBuilderSymbolTracker? tracker,
        NodeBuilderInternalFlags internalFlags, CancellationToken cancellation = default) =>
        EmitSyntaxQueryAsync<SyntaxNode?>(declaration, enclosing, flags, async state =>
            CopyEmitSyntax(await ReturnTypeForEmitAsync(declaration, enclosing, state, cancellation), state, emission, cancellation),
            null, cancellation, tracker, internalFlags);

    internal ValueTask<NodeList?> CreateTypeParametersForEmitAsync(SyntaxNode declaration, SyntaxNode? enclosing,
        EmitContext emission, NodeBuilderFlags flags, INodeBuilderSymbolTracker? tracker,
        NodeBuilderInternalFlags internalFlags, CancellationToken cancellation = default) =>
        EmitSyntaxQueryAsync<NodeList?>(declaration, enclosing, flags, async state =>
        {
            var nodes = await TypeParametersForEmitAsync(declaration, state, cancellation);
            return nodes.Count == 0 ? null : new(nodes.Select(node => CopyEmitSyntax(node, state, emission, cancellation)!).ToArray());
        }, null, cancellation, tracker, internalFlags);

    internal ValueTask<SyntaxNode?> CreateLiteralConstForEmitAsync(SyntaxNode declaration, EmitContext emission,
        CancellationToken cancellation = default) =>
        EmitSyntaxQueryAsync<SyntaxNode?>(declaration, declaration, NodeBuilderFlags.None, async state =>
            CopyEmitSyntax(await LiteralConstForEmitAsync(declaration, state, cancellation), state, emission, cancellation),
            null, cancellation);

    internal ValueTask<SyntaxNode?> CreateJsTypeForEmitAsync(SyntaxNode annotation, SyntaxNode? enclosing,
        EmitContext emission, NodeBuilderFlags flags, INodeBuilderSymbolTracker? tracker,
        NodeBuilderInternalFlags internalFlags, CancellationToken cancellation = default) =>
        EmitSyntaxQueryAsync<SyntaxNode?>(annotation, enclosing, flags, async state =>
            CopyEmitSyntax(await RecoverAnnotationSyntaxAsync(annotation, state, cancellation), state, emission, cancellation),
            null, cancellation, tracker, internalFlags);

    private static SyntaxNode? CopyEmitSyntax(SyntaxNode? node, TypeSyntaxContext state, EmitContext emission,
        CancellationToken cancellation)
    {
        if (node is null)
            return null;
        // Returned syntax must not expose nodes retained by the node-builder cache.
        var copy = node.DeepClone<SyntaxNode>(emission.Factory);
        using var originals = node.DescendantsAndSelf().GetEnumerator();
        foreach (var child in copy.DescendantsAndSelf())
        {
            cancellation.ThrowIfCancellationRequested();
            originals.MoveNext();
            var original = originals.Current;
            child.Flags |= NodeFlags.Synthesized;
            if (state.NoAsciiEscape.Contains(original))
                emission.AddFlags(child, EmitFlags.NoAsciiEscaping);
            if (state.SingleLine.Contains(original))
                emission.AddFlags(child, EmitFlags.SingleLine);
            if (state.Elided.Contains(original))
                emission.AddLeadingComment(child, new(SyntaxKind.MultiLineCommentTrivia, "elided"u8));
            if (state.CommentSources.TryGetValue(original, out var commentSource)
                && SemanticSyntax.Source(commentSource) == SemanticSyntax.Source(state.Symbols.Enclosing))
                emission.SetCommentRange(child, new(commentSource.Pos, commentSource.End));
        }
        return copy;
    }
}

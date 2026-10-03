using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Emission;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal const NodeBuilderFlags AnnotationFlags = NodeBuilderFlags.MultilineObjectLiterals | NodeBuilderFlags.WriteClassExpressionAsTypeLiteral
        | NodeBuilderFlags.UseTypeOfFunction | NodeBuilderFlags.UseStructuralFallback | NodeBuilderFlags.AllowEmptyTuple
        | NodeBuilderFlags.GenerateNamesForShadowedTypeParams | NodeBuilderFlags.NoTruncation;

    internal ValueTask<SyntaxNode?> InferAnnotationAsync(SyntaxNode node, bool widened, Type? variableType,
        EmitContext emitContext, Dictionary<SyntaxNode, Symbol> symbols, CancellationToken cancellation)
        => VisibilityQueryAsync(node, () => ChainOperationAsync(() => ContainerOperationAsync<SyntaxNode?>(async () =>
    {
        var enclosing = node;
        while (!enclosing.IsDeclarationNode && enclosing.Parent is { } parent) enclosing = parent;
        Type type;
        if (node.Kind is K.FunctionExpression or K.ArrowFunction or K.MethodDeclaration or K.GetAccessor or K.SetAccessor or K.FunctionDeclaration or K.Constructor)
        {
            var signature = await Signatures.FromDeclarationAsync(node, cancellation);
            if (await Signatures.PredicateAsync(signature, cancellation) is { } predicate)
            {
                if (predicate.Type is null) return null;
                var state = new TypeSyntaxContext(enclosing, false, flags: AnnotationFlags
                    | ((predicate.Type.Flags & TypeFlags.UniqueESSymbol) != 0 ? NodeBuilderFlags.AllowUniqueESSymbolType : 0));
                var result = await PredicateTypeSyntaxAsync(predicate, state, cancellation);
                return FinishTypeSyntax(state) ? result : null;
            }
            type = await Signatures.ReturnAsync(signature, cancellation);
        }
        else type = await TypeAtLocationAsync(QuerySyntax.Reparsed(node), cancellation);
        if (widened)
        {
            type = variableType ?? type;
            var wide = await Widening.LiteralAsync(type, cancellation);
            if (await AssignableAsync(wide, type, cancellation)) return null;
            type = wide;
        }
        var flags = AnnotationFlags;
        if ((node is VariableDeclarationNode || node is PropertyDeclarationNode
            && (SemanticSyntax.HasModifier(node, K.StaticKeyword) || SemanticSyntax.HasModifier(node, K.ReadonlyKeyword)))
            && (type.Flags & TypeFlags.UniqueESSymbol) != 0) flags |= NodeBuilderFlags.AllowUniqueESSymbolType;
        if (node is ParameterDeclarationNode parameter && node.BindingSymbol is not null
            && await ParameterRequiresImplicitUndefinedAsync(parameter, enclosing, cancellation))
            type = await Algebra.UnionAsync([context.UndefinedType, type], UnionReduction.None, cancellation: cancellation);
        return await MinimizedAnnotationWorkerAsync(type, enclosing, flags, emitContext, symbols, cancellation);
    }, cancellation), cancellation), cancellation);

    internal ValueTask<SyntaxNode?> MinimizedAnnotationAsync(Type type, SyntaxNode enclosing, NodeBuilderFlags flags,
        EmitContext emitContext, Dictionary<SyntaxNode, Symbol> symbols, CancellationToken cancellation)
        => VisibilityQueryAsync(enclosing, () => ChainOperationAsync(() => ContainerOperationAsync(
            () => MinimizedAnnotationWorkerAsync(type, enclosing, flags, emitContext, symbols, cancellation), cancellation), cancellation), cancellation);

    private async ValueTask<SyntaxNode?> MinimizedAnnotationWorkerAsync(Type type, SyntaxNode enclosing, NodeBuilderFlags flags,
        EmitContext emitContext, Dictionary<SyntaxNode, Symbol> symbols, CancellationToken cancellation)
    {
        context.RequireOwned(type);
        var state = new TypeSyntaxContext(enclosing, false, flags: flags, internalFlags: NodeBuilderInternalFlags.WriteComputedProps) { DisplaySymbols = symbols };
        var built = await TypeSyntaxAsync(type, state, cancellation);
        // These service APIs return syntax without the node builder's private printer metadata.
        var result = FinishTypeSyntax(state) ? built : null;
        if (result is TypeReferenceNode { TypeArguments.Count: > 0 } syntax && type is TypeReference { Target: InterfaceType target } reference)
        {
            var arguments = await References.TypeArgumentsAsync(reference, cancellation);
            var parameters = target.AllTypeParameters.OfType<TypeParameter>().Where(parameter => !parameter.IsThisType).ToArray();
            for (int cutoff = 0; cutoff < arguments.Count; cutoff++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (cutoff < target.OuterTypeParameterCount || cutoff >= parameters.Length
                    || parameters[cutoff].Symbol?.Declarations.Any(declaration => declaration is TypeParameterDeclarationNode { DefaultType: not null }) != true) continue;
                var filled = await Instantiation.Constraints.FillMissingArgumentsAsync(arguments.Take(cutoff).ToArray(), parameters, false,
                    IdenticalAsync, cancellation);
                if (!filled.SequenceEqual(arguments)) continue;
                if (cutoff < syntax.TypeArguments.Count)
                {
                    var updated = emitContext.Clone(syntax);
                    updated.TypeArguments = cutoff == 0 ? null : new([.. syntax.TypeArguments.Take(cutoff)]);
                    result = updated;
                }
                break;
            }
        }
        return result;
    }
}

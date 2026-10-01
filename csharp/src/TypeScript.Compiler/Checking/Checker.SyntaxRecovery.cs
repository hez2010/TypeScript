using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal ValueTask<Utf8String> SerializeJsTypeForEmitAsync(SyntaxNode annotation, SyntaxNode? enclosing,
        NodeBuilderFlags flags = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation, CancellationToken cancellation = default,
        INodeBuilderSymbolTracker? tracker = null,
        NodeBuilderInternalFlags internalFlags = NodeBuilderInternalFlags.None) =>
        EmitSyntaxQueryAsync(annotation, enclosing, flags, async state =>
        {
            return PrintEmitSyntax(await RecoverAnnotationSyntaxAsync(annotation, state, cancellation), enclosing, state, cancellation);
        }, Utf8String.Empty, cancellation, tracker, internalFlags);

    private async ValueTask<SyntaxNode> RecoverAnnotationSyntaxAsync(
        SyntaxNode annotation,
        TypeSyntaxContext state,
        CancellationToken cancellation)
    {
        long length = state.Length.Value;
        var node = await RecoverTypeSyntaxAsync(annotation, state, cancellation);
        state.Length.Value = length;
        AddReusedSyntaxLength(annotation, state);
        return node;
    }

    private async ValueTask<SyntaxNode> RecoverTypeSyntaxAsync(SyntaxNode node, TypeSyntaxContext state, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var f = state.Factory;
        if (node is ComputedPropertyNameNode { Expression: { } computed }
            && TypeScript.Compiler.Semantics.ConstantEvaluator.EntityName(computed))
            await TrackComputedNameAsync(computed, state, true, cancellation);
        switch (node)
        {
            case TypeLiteralNode literal:
                var members = new List<SyntaxNode>();
                foreach (var member in literal.Members!)
                {
                    if (SemanticSyntax.Name(member) is ComputedPropertyNameNode { Expression: { } computedExpression }
                        && TypeScript.Compiler.Semantics.ConstantEvaluator.EntityName(computedExpression)
                        && ((await ExpressionTypeForQueryAsync(computedExpression, cancellation)).Flags & TypeFlags.Any) == 0
                        && state.Symbols.Enclosing is { } destination
                        && (await EntityNameVisibilityAsync(computedExpression, destination, cancellation, computeAliases: false)).Accessibility
                            != SymbolAccessibility.Accessible)
                        return await Fallback();
                    if (SemanticSyntax.Name(member) is ComputedPropertyNameNode { Expression: { } key } computedName
                        && key is not (StringLiteralNode or NumericLiteralNode)
                        && !await LateMembers.BindableAsync(member, cancellation))
                    {
                        if ((state.InternalFlags & NodeBuilderInternalFlags.AllowUnresolvedNames) == 0
                            || !TypeScript.Compiler.Semantics.ConstantEvaluator.EntityName(key)
                            || ((await ComputedNameAsync(computedName, cancellation)).Flags & TypeFlags.Any) == 0)
                            continue;
                    }
                    members.Add(await Visit(member));
                }
                var objectNode = f.NewTypeLiteralNode(new(members.ToArray()));
                if (SemanticSyntax.Source(node) == SemanticSyntax.Source(state.Symbols.Enclosing))
                    (objectNode.Pos, objectNode.End) = (node.Pos, node.End);
                if ((state.Flags & NodeBuilderFlags.MultilineObjectLiterals) == 0)
                    state.SingleLine.Add(objectNode);
                return objectNode;
            case JSDocTypeExpressionNode expression:
                return await Visit(expression.Type!);
            case JSDocNonNullableTypeNode nonNullable:
                return await Visit(nonNullable.Type!);
            case JSDocNullableTypeNode nullable:
                return f.NewUnionTypeNode(new([await Visit(nullable.Type!), f.NewLiteralTypeNode(f.NewKeywordExpression(K.NullKeyword))]));
            case JSDocOptionalTypeNode optional:
                return f.NewUnionTypeNode(new([await Visit(optional.Type!), f.NewKeywordTypeNode(K.UndefinedKeyword)]));
            case JSDocVariadicTypeNode variadic:
                return f.NewArrayTypeNode(ParenthesizeType(await Visit(variadic.Type!), f, postfix: true));
            case JSDocTypeLiteralNode literal:
                var properties = new List<SyntaxNode>();
                foreach (var tag in literal.JSDocPropertyTags.OfType<JSDocParameterOrPropertyTagNode>())
                {
                    var name = tag.Name is QualifiedNameNode qualified ? qualified.Right! : tag.Name!;
                    properties.Add(f.NewPropertySignatureDeclaration(null, CloneSyntaxBindingName(name, state),
                        tag.IsBracketed || tag.TypeExpression is JSDocOptionalTypeNode ? f.NewToken(K.QuestionToken) : null,
                        tag.TypeExpression is null ? null : await Visit(tag.TypeExpression), null));
                }
                var result = f.NewTypeLiteralNode(new(properties.ToArray()));
                if ((state.Flags & NodeBuilderFlags.MultilineObjectLiterals) == 0)
                    state.SingleLine.Add(result);
                return result;
            case TypeReferenceNode reference:
                return (await RecoverTypeReferenceSyntaxAsync(reference, state, cancellation))!;
            case IndexedAccessTypeNode:
            case TypeOperatorNode { Operator: K.KeyOfKeyword }:
                return await ReuseSimpleTypeSyntaxAsync(node, state, cancellation) ?? await Fallback();
            case TypeQueryNode query:
                return await ReuseTypeQuerySyntaxAsync(query, state, cancellation, countLength: false) ?? await Fallback();
            case ImportTypeNode import:
                if (import.Argument is not LiteralTypeNode { Literal: StringLiteralNode specifier }
                    || import.Attributes?.Token == K.AssertKeyword)
                    return await Fallback();
                var argument = CloneSyntaxBindingName(import.Argument, state, typeAnnotation: true);
                var typeArguments = new List<SyntaxNode>();
                if (import.TypeArguments is { } importArguments)
                    foreach (var typeArgument in importArguments)
                        typeArguments.Add(await Visit(typeArgument));
                if (SemanticSyntax.Source(import) != SemanticSyntax.Source(state.Symbols.Enclosing))
                {
                    await Nodes.FromNodeAsync(import, cancellation);
                    Symbol? importModule = null;
                    if (links.SymbolNodes.TryGet(import)?.ResolvedSymbol is { } importedSymbol && importedSymbol != UnknownSymbol)
                    {
                        var meaning = import.IsTypeOf ? SymbolFlags.Value : SymbolFlags.Type;
                        if ((await SymbolAccessibilityAsync(importedSymbol, state.Symbols.Enclosing, meaning, false, true, cancellation)).Accessibility
                            == SymbolAccessibility.Accessible)
                        {
                            var chain = await DisplaySymbolChainAsync(importedSymbol, meaning, true, state.Symbols, cancellation, yieldModule: true);
                            if (chain is { Count: > 0 } && chain[0].Declarations.Any(NonGlobalExternalModule)) importModule = chain[0];
                        }
                    }
                    if (importModule is null && await ExternalModuleFileForEmitAsync(import, cancellation) is { } targetFile)
                        importModule = program.Symbols.Declaration(targetFile);
                    if (importModule is not null)
                    {
                        var moduleName = await DisplayModuleSpecifierAsync(importModule, state.Symbols, cancellation);
                        if (moduleName.Span.Contains("/node_modules/"u8, StringComparison.Ordinal))
                        { state.EncounteredError = true; state.Tracker.ReportLikelyUnsafeImportRequiredError(moduleName, Utf8String.Empty); }
                        argument = f.NewLiteralTypeNode(f.NewStringLiteral(moduleName, TokenFlags.None));
                    }
                }
                return f.NewImportTypeNode(import.IsTypeOf, argument,
                    import.Attributes is null ? null : (ImportAttributesNode)CloneSyntaxBindingName(import.Attributes, state),
                    import.Qualifier is null ? null : CloneSyntaxBindingName(import.Qualifier, state),
                    import.TypeArguments is null ? null : new(typeArguments.ToArray()));
            case TypeOperatorNode { Operator: K.UniqueKeyword }:
                return await ReuseTypeAnnotationSyntaxAsync(node, state, cancellation) ?? await Fallback();
            case TypeParameterDeclarationNode parameter:
                if (program.Symbols.Declaration(parameter) is { } parameterSymbol)
                    state.Tracker.TrackSymbol(parameterSymbol, state.Symbols.Enclosing, SymbolFlags.Type);
                var parameterName = program.Symbols.Declaration(parameter) is { } symbol
                    ? f.NewIdentifier(TypeSyntaxParameterName(program.Scopes.Parameter(symbol), state, cancellation))
                    : CloneSyntaxBindingName(parameter.Name!, state);
                return f.NewTypeParameterDeclaration(
                    parameter.Modifiers is null ? null : new(parameter.Modifiers.Select(m => f.NewToken(m.Kind)).ToArray()),
                    (IdentifierNode)parameterName, parameter.Constraint is null ? null : await Visit(parameter.Constraint),
                    parameter.Expression is null ? null : await Visit(parameter.Expression),
                    parameter.DefaultType is null ? null : await Visit(parameter.DefaultType));
        }
        if (node.Kind == K.JSDocAllType)
            return f.NewKeywordTypeNode(K.AnyKeyword);
        var copies = new Dictionary<SyntaxNode, SyntaxNode>();
        if (node is ConditionalTypeNode conditional)
        {
            copies[conditional.CheckType!] = await Visit(conditional.CheckType!);
            var inferred = program.Symbols.Binding(node)?.Get(node)?.Locals.Values
                .Where(s => (s.Flags & SymbolFlags.TypeParameter) != 0).Select(program.Scopes.Parameter).ToArray() ?? [];
            using (state.ParameterNames?.EnterScope())
            using (EnterGeneratedParameterScope(node, inferred, state, cancellation))
            {
                copies[conditional.ExtendsType!] = await Visit(conditional.ExtendsType!);
                copies[conditional.TrueType!] = await Visit(conditional.TrueType!);
            }
            copies[conditional.FalseType!] = await Visit(conditional.FalseType!);
        }
        else
        {
            bool introducesScope = Signatures.FunctionLike(node) || node is MappedTypeNode;
            using var names = introducesScope ? state.ParameterNames?.EnterScope() : null;
            using var qualifiedNames = introducesScope ? state.QualifiedNames.EnterScope() : null;
            var signature = Signatures.FunctionLike(node) ? await Signatures.FromDeclarationAsync(node, cancellation) : null;
            using var values = signature is null ? null : EnterValueParameterScope(node, signature.Parameters, null, state, cancellation);
            IReadOnlyList<TypeParameter> parameters = node is MappedTypeNode mapped
                ? [program.Scopes.Parameter(program.Symbols.Declaration(mapped.TypeParameter!)!)] : signature?.TypeParameters ?? [];
            using var scope = introducesScope ? EnterGeneratedParameterScope(node, parameters, state, cancellation) : null;
            for (int i = 0; i < node.ChildCount; i++)
            {
                var child = node.GetChild(i);
                copies[child] = await Visit(child);
            }
        }
        var clone = node.ShallowClone();
        clone.Parent = null;
        clone.RewriteChildren(copies);
        if (SemanticSyntax.Source(node) != SemanticSyntax.Source(state.Symbols.Enclosing))
            clone.Pos = clone.End = -1;
        if (clone is StringLiteralNode text)
            text.TokenFlags |= state.Symbols.StringLiteralFlags;
        state.NoAsciiEscape.Add(clone);
        if (clone is not TypeLiteralNode || (state.Flags & NodeBuilderFlags.MultilineObjectLiterals) == 0)
            state.SingleLine.Add(clone);
        return clone;

        ValueTask<SyntaxNode> Visit(SyntaxNode child) => RecoverTypeSyntaxAsync(child, state, cancellation);
        async ValueTask<SyntaxNode> Fallback() => await TypeSyntaxAsync((await Instantiation.Engine.InstantiateAsync(
            await Nodes.FromNodeAsync(node, cancellation), state.Mapper, cancellation: cancellation))!, state, cancellation);
    }

    private async ValueTask<SyntaxNode?> ReuseSimpleTypeSyntaxAsync(SyntaxNode node, TypeSyntaxContext state, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        SyntaxNode result;
        switch (node)
        {
            case TypeReferenceNode reference:
                return await RecoverTypeReferenceSyntaxAsync(reference, state, cancellation, allowStructuralFallback: false);
            case TypeQueryNode query:
                return await ReuseTypeQuerySyntaxAsync(query, state, cancellation, countLength: false, allowStructuralFallback: false);
            case IndexedAccessTypeNode indexed:
                var objectType = await ReuseSimpleTypeSyntaxAsync(indexed.ObjectType!, state, cancellation);
                if (objectType is null) return null;
                result = state.Factory.NewIndexedAccessTypeNode(objectType, await RecoverTypeSyntaxAsync(indexed.IndexType!, state, cancellation));
                break;
            case TypeOperatorNode { Operator: K.KeyOfKeyword } keyOf:
                var operand = await ReuseSimpleTypeSyntaxAsync(keyOf.Type!, state, cancellation);
                if (operand is null) return null;
                result = state.Factory.NewTypeOperatorNode(K.KeyOfKeyword, operand);
                break;
            default:
                return await RecoverTypeSyntaxAsync(node, state, cancellation);
        }
        result.Flags = node.Flags;
        if (SemanticSyntax.Source(node) == SemanticSyntax.Source(state.Symbols.Enclosing))
            (result.Pos, result.End) = (node.Pos, node.End);
        return result;
    }

    private async ValueTask<SyntaxNode?> RecoverTypeReferenceSyntaxAsync(TypeReferenceNode reference, TypeSyntaxContext state,
        CancellationToken cancellation, bool allowStructuralFallback = true)
    {
        if (reference.TypeName is IdentifierNode { Text.Span: var matchedText } && matchedText.SequenceEqual(""u8))
            return state.Factory.NewKeywordTypeNode(K.AnyKeyword);
        var type = await Nodes.FromNodeAsync(reference, cancellation);
        if (await DocumentationTypeReferenceAsync(reference, cancellation) is not null)
            return allowStructuralFallback ? await TypeSyntaxAsync(type, state, cancellation) : null;
        var symbol = links.SymbolNodes.TryGet(reference)?.ResolvedSymbol;
        if (symbol is null)
            return allowStructuralFallback ? await TypeSyntaxAsync(type, state, cancellation) : null;
        if (type is TypeReference { Target: InterfaceType target } && await Declared.GetAsync(symbol, cancellation) == target)
        {
            int required = target.AllTypeParameters.Count - target.OuterTypeParameterCount - (target.ThisType is null ? 0 : 1);
            while (required > 0 && await Instantiation.Constraints.DefaultAsync(
                (TypeParameter)target.AllTypeParameters[target.OuterTypeParameterCount + required - 1], cancellation) is not null)
                required--;
            if ((reference.TypeArguments?.Count ?? 0) < required)
                return allowStructuralFallback ? await TypeSyntaxAsync(type, state, cancellation) : null;
        }
        if ((symbol.Flags & SymbolFlags.TypeParameter) != 0 && state.Mapper is not null
            && await state.Mapper.MapAsync(program.Scopes.Parameter(symbol), cancellation) != program.Scopes.Parameter(symbol))
            return allowStructuralFallback ? await TypeSyntaxAsync(
                (await Instantiation.Engine.InstantiateAsync(type, state.Mapper, cancellation: cancellation))!,
                state,
                cancellation) : null;
        if ((symbol.Flags & SymbolFlags.TypeParameter) != 0)
        {
            state.Tracker.TrackSymbol(symbol, state.Symbols.Enclosing, SymbolFlags.Type);
            var name = state.Factory.NewIdentifier(TypeSyntaxParameterName(program.Scopes.Parameter(symbol), state, cancellation));
            if (SemanticSyntax.Source(reference) == SemanticSyntax.Source(state.Symbols.Enclosing))
                (name.Pos, name.End) = (reference.TypeName!.Pos, reference.TypeName.End);
            return WithRange(state.Factory.NewTypeReferenceNode(name, await ArgumentsAsync()));
        }
        var first = reference.TypeName;
        while (first is QualifiedNameNode qualified)
            first = qualified.Left;
        if (first is IdentifierNode identifier)
        {
            var meaning = reference.TypeName is QualifiedNameNode ? SymbolFlags.Namespace : SymbolFlags.Type;
            var original = await program.EntityNames.ResolveAsync(identifier, meaning, true, true, cancellation: cancellation);
            var current = await program.EntityNames.ResolveAsync(identifier, meaning, true, true, state.Symbols.Enclosing, cancellation);
            if ((current == original
                || current is not null && original is not null && await SameSymbolReferenceAsync(current, original, cancellation))
                && (current is null || (await SymbolAccessibilityAsync(current, state.Symbols.Enclosing, meaning, false, true, cancellation)).Accessibility
                    == SymbolAccessibility.Accessible))
            {
                if (current is not null)
                    await TrackTypeSymbolAsync(current, state.Symbols.Enclosing, meaning, state, cancellation);
                return WithRange(state.Factory.NewTypeReferenceNode(CloneSyntaxBindingName(reference.TypeName!, state), await ArgumentsAsync()));
            }
        }
        if ((await SymbolAccessibilityAsync(symbol, state.Symbols.Enclosing, SymbolFlags.Type, false, true, cancellation)).Accessibility
            != SymbolAccessibility.Accessible)
        {
            state.Tracker.ReportInferenceFallback(reference.TypeName!);
            return allowStructuralFallback ? await TypeSyntaxAsync(type, state, cancellation, expandAlias: true) : null;
        }
        return await SymbolTypeNodeAsync(symbol, SymbolFlags.Type, await ArgumentsAsync(), state.Symbols, false, cancellation);

        SyntaxNode WithRange(SyntaxNode node)
        {
            if (SemanticSyntax.Source(reference) == SemanticSyntax.Source(state.Symbols.Enclosing))
                (node.Pos, node.End) = (reference.Pos, reference.End);
            return node;
        }

        async ValueTask<NodeList?> ArgumentsAsync()
        {
            if (reference.TypeArguments is not { } originalArguments)
                return null;
            var arguments = new List<SyntaxNode>();
            foreach (var argument in originalArguments)
                arguments.Add(await RecoverTypeSyntaxAsync(argument, state, cancellation));
            return new(arguments.ToArray());
        }
    }
}

using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal ValueTask<string> SerializeJsTypeForEmitAsync(SyntaxNode annotation, SyntaxNode? enclosing,
        NodeBuilderFlags flags = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation, CancellationToken cancellation = default) =>
        EmitSyntaxQueryAsync(annotation, enclosing, flags, async state =>
        {
            return PrintEmitSyntax(await RecoverAnnotationSyntaxAsync(annotation, state, cancellation), enclosing, state, cancellation);
        }, cancellation);

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
        switch (node)
        {
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
                return await RecoverTypeReferenceSyntaxAsync(reference, state, cancellation);
            case TypeQueryNode query:
                return await ReuseTypeQuerySyntaxAsync(query, state, cancellation, countLength: false) ?? await Fallback();
            case ImportTypeNode import:
                if (import.Argument is not LiteralTypeNode { Literal: StringLiteralNode specifier }
                    || import.Attributes?.Token == K.AssertKeyword)
                    return await Fallback();
                var argument = CloneSyntaxBindingName(import.Argument, state, typeAnnotation: true);
                if (SemanticSyntax.Source(import) != SemanticSyntax.Source(state.Symbols.Enclosing)
                    && await ExternalModuleFileForEmitAsync(import, cancellation) is { } targetFile
                    && program.Symbols.Declaration(targetFile) is { } module)
                    argument = f.NewLiteralTypeNode(f.NewStringLiteral(
                        await DisplayModuleSpecifierAsync(module, state.Symbols, cancellation), TokenFlags.None));
                var typeArguments = new List<SyntaxNode>();
                if (import.TypeArguments is { } importArguments)
                    foreach (var typeArgument in importArguments)
                        typeArguments.Add(await Visit(typeArgument));
                return f.NewImportTypeNode(import.IsTypeOf, argument,
                    import.Attributes is null ? null : (ImportAttributesNode)CloneSyntaxBindingName(import.Attributes, state),
                    import.Qualifier is null ? null : CloneSyntaxBindingName(import.Qualifier, state),
                    import.TypeArguments is null ? null : new(typeArguments.ToArray()));
            case TypeOperatorNode { Operator: K.UniqueKeyword }:
                return await ReuseTypeAnnotationSyntaxAsync(node, state, cancellation) ?? await Fallback();
            case TypeParameterDeclarationNode parameter:
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

    private async ValueTask<SyntaxNode> RecoverTypeReferenceSyntaxAsync(TypeReferenceNode reference, TypeSyntaxContext state,
        CancellationToken cancellation)
    {
        if (reference.TypeName is IdentifierNode { Text: "" })
            return state.Factory.NewKeywordTypeNode(K.AnyKeyword);
        var type = await Nodes.FromNodeAsync(reference, cancellation);
        if (await DocumentationTypeReferenceAsync(reference, cancellation) is not null)
            return await TypeSyntaxAsync(type, state, cancellation);
        var symbol = links.SymbolNodes.TryGet(reference)?.ResolvedSymbol;
        if (symbol is null)
            return await TypeSyntaxAsync(type, state, cancellation);
        if (type is TypeReference { Target: InterfaceType target } && await Declared.GetAsync(symbol, cancellation) == target)
        {
            int required = target.AllTypeParameters.Count - target.OuterTypeParameterCount - (target.ThisType is null ? 0 : 1);
            while (required > 0 && await Instantiation.Constraints.DefaultAsync(
                (TypeParameter)target.AllTypeParameters[target.OuterTypeParameterCount + required - 1], cancellation) is not null)
                required--;
            if ((reference.TypeArguments?.Count ?? 0) < required)
                return await TypeSyntaxAsync(type, state, cancellation);
        }
        if ((symbol.Flags & SymbolFlags.TypeParameter) != 0 && state.Mapper is not null
            && await state.Mapper.MapAsync(program.Scopes.Parameter(symbol), cancellation) != program.Scopes.Parameter(symbol))
            return await TypeSyntaxAsync(
                (await Instantiation.Engine.InstantiateAsync(type, state.Mapper, cancellation: cancellation))!,
                state,
                cancellation);
        var arguments = new List<SyntaxNode>();
        if (reference.TypeArguments is { } originalArguments)
            foreach (var argument in originalArguments)
                arguments.Add(await RecoverTypeSyntaxAsync(argument, state, cancellation));
        NodeList? argumentList = reference.TypeArguments is null ? null : new(arguments.ToArray());
        if ((symbol.Flags & SymbolFlags.TypeParameter) != 0)
            return state.Factory.NewTypeReferenceNode(state.Factory.NewIdentifier(
                TypeSyntaxParameterName(program.Scopes.Parameter(symbol), state, cancellation)), argumentList);
        var first = reference.TypeName;
        while (first is QualifiedNameNode qualified)
            first = qualified.Left;
        if (first is IdentifierNode identifier)
        {
            var meaning = reference.TypeName is QualifiedNameNode ? SymbolFlags.Namespace : SymbolFlags.Type;
            var original = await program.EntityNames.ResolveAsync(identifier, meaning, true, true, cancellation: cancellation);
            var current = await program.EntityNames.ResolveAsync(identifier, meaning, true, true, state.Symbols.Enclosing, cancellation);
            if (current == original
                || current is not null && original is not null && await SameSymbolReferenceAsync(current, original, cancellation))
                return state.Factory.NewTypeReferenceNode(CloneSyntaxBindingName(reference.TypeName!, state), argumentList);
        }
        return await SymbolTypeNodeAsync(symbol, SymbolFlags.Type, argumentList, state.Symbols, false, cancellation);
    }
}

using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private sealed class TypeSyntaxContext(SyntaxNode? enclosing, bool aliasesOutsideScope)
    {
        internal NodeFactory Factory { get; } = new();
        internal SymbolDisplayContext Symbols { get; } = new(enclosing,
            aliasesOutsideScope ? SymbolFormatFlags.UseAliasDefinedOutsideCurrentScope : SymbolFormatFlags.None);
        internal HashSet<SyntaxNode> NoAsciiEscape { get; } = [];
        internal HashSet<SyntaxNode> SingleLine { get; } = [];
        internal HashSet<Type> Active { get; } = [];
        internal IReadOnlyList<TypeParameter>? InferParameters { get; set; }
    }

    internal ValueTask<string> SerializeTypeSyntaxAsync(Type type, SyntaxNode? enclosing = null, bool expandAlias = false,
        bool aliasesOutsideScope = false, CancellationToken cancellation = default)
    {
        context.RequireOwned(type);
        return VisibilityQueryAsync(enclosing, () => ChainOperationAsync(() => ContainerOperationAsync(async () =>
        {
            var state = new TypeSyntaxContext(enclosing, aliasesOutsideScope);
            var node = await TypeSyntaxAsync(type, state, cancellation, expandAlias);
            return PrintDiagnosticNode(
                node,
                enclosing is SourceFileNode,
                cancellation,
                enclosing is null ? null : SemanticSyntax.Source(enclosing),
                state.NoAsciiEscape, state.SingleLine);
        }, cancellation), cancellation), cancellation);
    }

    private async ValueTask<SyntaxNode> TypeSyntaxAsync(
        Type type,
        TypeSyntaxContext state,
        CancellationToken cancellation,
        bool expandAlias = false)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type is TypeParameter distributed)
            type = distributed.NonDistributed;
        type = await Views.ReducedAsync(type, cancellation);
        var f = state.Factory;
        if ((type.Flags & TypeFlags.Any) != 0)
            return type.Alias is { } anyAlias
                ? f.NewTypeReferenceNode(
                    DirectTypeName(anyAlias.Symbol, f),
                    await TypeSyntaxListAsync(anyAlias.TypeArguments, state, cancellation))
                : f.NewKeywordTypeNode(type == context.IntrinsicMarkerType ? K.IntrinsicKeyword : K.AnyKeyword);
        if ((type.Flags & TypeFlags.Unknown) != 0)
            return f.NewKeywordTypeNode(K.UnknownKeyword);
        if ((type.Flags & TypeFlags.String) != 0)
            return f.NewKeywordTypeNode(K.StringKeyword);
        if ((type.Flags & TypeFlags.Number) != 0)
            return f.NewKeywordTypeNode(K.NumberKeyword);
        if ((type.Flags & TypeFlags.BigInt) != 0)
            return f.NewKeywordTypeNode(K.BigIntKeyword);
        if ((type.Flags & TypeFlags.Boolean) != 0 && type.Alias is null)
            return f.NewKeywordTypeNode(K.BooleanKeyword);
        if ((type.Flags & TypeFlags.EnumLike) != 0 && type.Symbol is { } enumeration)
        {
            if ((enumeration.Flags & SymbolFlags.EnumMember) == 0)
                return await SymbolTypeNodeAsync(enumeration, SymbolFlags.Type, null, state.Symbols, false, cancellation);
            var parent = program.Symbols.Parent(enumeration)!;
            var parentNode = await SymbolTypeNodeAsync(parent, SymbolFlags.Type, null, state.Symbols, false, cancellation);
            if (await Declared.GetAsync(parent, cancellation) == type)
                return parentNode;
            if (IdentifierName(enumeration.Name))
                return AppendTypeName(parentNode, f.NewIdentifier(Symbol.EscapeName(enumeration.Name)), f);
            if (parentNode is ImportTypeNode import)
                import.IsTypeOf = true;
            else if (parentNode is TypeReferenceNode reference)
                parentNode = f.NewTypeQueryNode(reference.TypeName, null);
            else
                throw new InvalidOperationException("Enum type has no named reference");
            return f.NewIndexedAccessTypeNode(
                parentNode,
                f.NewLiteralTypeNode(f.NewStringLiteral(Symbol.EscapeName(enumeration.Name), TokenFlags.None)));
        }
        if (type is LiteralType literal)
        {
            SyntaxNode value = literal.Value switch
            {
                string text => f.NewStringLiteral(text, TokenFlags.None),
                double number when number < 0 => f.NewPrefixUnaryExpression(
                    K.MinusToken,
                    f.NewNumericLiteral(TokenFacts.NumberText(number)[1..], TokenFlags.None)),
                double number => f.NewNumericLiteral(TokenFacts.NumberText(number), TokenFlags.None),
                BigInteger integer => f.NewBigIntLiteral(integer.ToString(CultureInfo.InvariantCulture) + "n", TokenFlags.None),
                bool boolean => f.NewKeywordExpression(boolean ? K.TrueKeyword : K.FalseKeyword),
                _ => throw new InvalidOperationException("Unexpected literal type")
            };
            if (value is StringLiteralNode)
                state.NoAsciiEscape.Add(value);
            return f.NewLiteralTypeNode(value);
        }
        if (type is UniqueSymbolType unique)
            return (await SymbolAccessibilityAsync(
                unique.Symbol,
                state.Symbols.Enclosing,
                SymbolFlags.Value,
                false,
                true,
                cancellation)).Accessibility == SymbolAccessibility.Accessible
                ? await SymbolTypeNodeAsync(unique.Symbol!, SymbolFlags.Value, null, state.Symbols, false, cancellation)
                : f.NewTypeOperatorNode(K.UniqueKeyword, f.NewKeywordTypeNode(K.SymbolKeyword));
        if ((type.Flags & TypeFlags.Void) != 0)
            return f.NewKeywordTypeNode(K.VoidKeyword);
        if ((type.Flags & TypeFlags.Undefined) != 0)
            return f.NewKeywordTypeNode(K.UndefinedKeyword);
        if ((type.Flags & TypeFlags.Null) != 0)
            return f.NewLiteralTypeNode(f.NewKeywordExpression(K.NullKeyword));
        if ((type.Flags & TypeFlags.Never) != 0)
            return f.NewKeywordTypeNode(K.NeverKeyword);
        if ((type.Flags & TypeFlags.ESSymbol) != 0)
            return f.NewKeywordTypeNode(K.SymbolKeyword);
        if ((type.Flags & TypeFlags.NonPrimitive) != 0)
            return f.NewKeywordTypeNode(K.ObjectKeyword);
        if (type is TypeParameter { IsThisType: true })
            return f.NewThisTypeNode();
        if (!expandAlias && type.Alias is { } alias && ((state.Symbols.Flags & SymbolFormatFlags.UseAliasDefinedOutsideCurrentScope) != 0
            || (await SymbolAccessibilityAsync(
                alias.Symbol,
                state.Symbols.Enclosing,
                SymbolFlags.Type,
                false,
                true,
                cancellation)).Accessibility == SymbolAccessibility.Accessible))
            return await SymbolTypeNodeAsync(
                alias.Symbol,
                SymbolFlags.Type,
                await TypeSyntaxListAsync(alias.TypeArguments, state, cancellation),
                state.Symbols,
                false,
                cancellation);
        if (!state.Active.Add(type))
            return f.NewKeywordTypeNode(K.AnyKeyword);
        try
        {
            if (type is TypeReference reference && (type.ObjectFlags & ObjectFlags.Reference) != 0)
                return await ReferenceTypeSyntaxAsync(reference, state, cancellation);
            if (type is TypeParameter inferredParameter && state.InferParameters?.Contains(inferredParameter) == true)
            {
                SyntaxNode? constraintNode = null;
                if (await Instantiation.Constraints.ConstraintAsync(inferredParameter, cancellation) is { } constraint)
                {
                    var inferred = await InferredConstraints.GetAsync(inferredParameter, omitReferences: true, cancellation);
                    if (inferred is null || !await Relations.RelatedAsync(constraint, inferred, RelationKind.Identity, cancellation))
                    {
                        constraintNode = await TypeSyntaxAsync(constraint, state, cancellation);
                        if (constraintNode is ConditionalTypeNode)
                            constraintNode = f.NewParenthesizedTypeNode(constraintNode);
                    }
                }
                return f.NewInferTypeNode(await TypeParameterSyntaxAsync(inferredParameter, constraintNode, state, cancellation));
            }
            if (type is TypeParameter || (type.ObjectFlags & ObjectFlags.ClassOrInterface) != 0)
                return type.Symbol is { } symbol ? await SymbolTypeNodeAsync(
                    symbol,
                    SymbolFlags.Type,
                    null,
                    state.Symbols,
                    false,
                    cancellation)
                    : f.NewTypeReferenceNode(f.NewIdentifier("?"), null);
            if (type is UnionType { Origin: { } origin })
                return await TypeSyntaxAsync(origin, state, cancellation);
            if (type is UnionOrIntersectionType composite)
            {
                IReadOnlyList<Type> types = type is UnionType
                    ? await SyntaxUnionTypesAsync(composite.Types, cancellation)
                    : composite.Types;
                if (types.Count == 1)
                    return await TypeSyntaxAsync(types[0], state, cancellation);
                var nodes = await TypeSyntaxListAsync(types, state, cancellation);
                if (nodes is null)
                    return f.NewKeywordTypeNode(K.AnyKeyword);
                var parenthesized = new NodeList(nodes.Select(n => n is FunctionTypeNode or ConstructorTypeNode or ConditionalTypeNode
                    || type is IntersectionType && n is UnionTypeNode ? f.NewParenthesizedTypeNode(n) : n).ToArray());
                return type is UnionType ? f.NewUnionTypeNode(parenthesized) : f.NewIntersectionTypeNode(parenthesized);
            }
            if (type is IndexType index)
                return f.NewTypeOperatorNode(K.KeyOfKeyword, ParenthesizeType(await TypeSyntaxAsync(index.Target, state, cancellation), f));
            if (type is IndexedAccessType indexed)
                return f.NewIndexedAccessTypeNode(
                    ParenthesizeType(await TypeSyntaxAsync(indexed.ObjectType, state, cancellation), f, postfix: true),
                    await TypeSyntaxAsync(indexed.IndexType, state, cancellation));
            if (type is TemplateLiteralType template)
            {
                var head = f.NewTemplateHead(template.Texts[0], "", TokenFlags.None);
                state.NoAsciiEscape.Add(head);
                var spans = new List<SyntaxNode>();
                for (int i = 0; i < template.Types.Count; i++)
                {
                    SyntaxNode text = i == template.Types.Count - 1
                        ? f.NewTemplateTail(template.Texts[i + 1], "", TokenFlags.None)
                        : f.NewTemplateMiddle(template.Texts[i + 1], "", TokenFlags.None);
                    state.NoAsciiEscape.Add(text);
                    spans.Add(f.NewTemplateLiteralTypeSpan(await TypeSyntaxAsync(template.Types[i], state, cancellation), text));
                }
                return f.NewTemplateLiteralTypeNode(head, new(spans.ToArray()));
            }
            if (type is StringMappingType mapping)
                return await SymbolTypeNodeAsync(
                    mapping.Symbol!,
                    SymbolFlags.Type,
                    new([await TypeSyntaxAsync(mapping.Target, state, cancellation)]),
                    state.Symbols,
                    false,
                    cancellation);
            if (type is SubstitutionType substitution)
            {
                var node = await TypeSyntaxAsync(substitution.BaseType, state, cancellation);
                return substitution.Constraint == context.UnknownType && program.Symbols.Globals.GetValueOrDefault("NoInfer") is { } noInfer
                    ? await SymbolTypeNodeAsync(noInfer, SymbolFlags.Type, new([node]), state.Symbols, false, cancellation) : node;
            }
            if (type is ConditionalType conditional)
            {
                var check = await TypeSyntaxAsync(conditional.CheckType, state, cancellation);
                if (check is ConditionalTypeNode or FunctionTypeNode or ConstructorTypeNode)
                    check = f.NewParenthesizedTypeNode(check);
                var previous = state.InferParameters;
                SyntaxNode extends;
                try
                {
                    state.InferParameters = conditional.Root.InferTypeParameters;
                    extends = await TypeSyntaxAsync(conditional.ExtendsType, state, cancellation);
                }
                finally
                {
                    state.InferParameters = previous;
                }
                if (extends is ConditionalTypeNode)
                    extends = f.NewParenthesizedTypeNode(extends);
                var whenTrue = await TypeSyntaxAsync(
                    await Instantiation.Constraints.ConditionalTrueAsync(conditional, cancellation: cancellation),
                    state,
                    cancellation);
                var whenFalse = await TypeSyntaxAsync(
                    await Instantiation.Constraints.ConditionalFalseAsync(conditional, cancellation),
                    state,
                    cancellation);
                return f.NewConditionalTypeNode(check, extends, whenTrue, whenFalse);
            }
            if (type is ObjectType objectType)
            {
                if (type is MappedType mapped && (await Instantiation.Mapped.IsGenericAsync(mapped, cancellation) || mapped.ContainsError))
                    return await MappedTypeSyntaxAsync(mapped, state, cancellation);
                var resolved = await Members.ResolveAsync(objectType, cancellation);
                if (type.Symbol is null
                        || (type.Symbol.Flags & (SymbolFlags.TypeLiteral | SymbolFlags.ObjectLiteral)) != 0)
                    return await ObjectTypeSyntaxAsync(resolved, state, cancellation);
            }
            throw new NotSupportedException($"Type syntax construction is not yet implemented for {type.GetType().Name}");
        }
        finally
        {
            state.Active.Remove(type);
        }
    }

    private async ValueTask<NodeList?> TypeSyntaxListAsync(
        IReadOnlyList<Type> types,
        TypeSyntaxContext state,
        CancellationToken cancellation)
    {
        if (types.Count == 0)
            return null;
        var nodes = new SyntaxNode[types.Count];
        for (int i = 0; i < nodes.Length; i++)
            nodes[i] = await TypeSyntaxAsync(types[i], state, cancellation);
        return new(nodes);
    }

    private async ValueTask<SyntaxNode> MappedTypeSyntaxAsync(MappedType type, TypeSyntaxContext state, CancellationToken cancellation)
    {
        var f = state.Factory;
        var declaration = type.Declaration!;
        var template = await Instantiation.Mapped.TemplateAsync(type, cancellation);
        var parameter = await Instantiation.Mapped.ParameterAsync(type, cancellation);
        SyntaxNode constraint = MappedMembers.HasKeyofConstraint(type)
            ? f.NewTypeOperatorNode(K.KeyOfKeyword, ParenthesizeType(
                await TypeSyntaxAsync(await Instantiation.Members.ModifiersTypeAsync(type, cancellation), state, cancellation), f))
            : await TypeSyntaxAsync(await Instantiation.Mapped.ConstraintAsync(type, cancellation), state, cancellation);
        var parameterNode = await TypeParameterSyntaxAsync(parameter, constraint, state, cancellation);
        var name = await Instantiation.Mapped.NameAsync(type, cancellation);
        var nameNode = name is null ? null : await TypeSyntaxAsync(name, state, cancellation);
        var templateNode = await TypeSyntaxAsync(
            Values.NonMissing(template, (MappedTypes.Modifiers(type) & MappedTypeModifiers.IncludeOptional) != 0), state, cancellation);
        var result = f.NewMappedTypeNode(
            declaration.ReadonlyToken is { } readOnly ? f.NewToken(readOnly.Kind) : null,
            parameterNode, nameNode,
            declaration.QuestionToken is { } question ? f.NewToken(question.Kind) : null,
            templateNode, null);
        state.SingleLine.Add(result);
        return result;
    }

    private async ValueTask<TypeParameterDeclarationNode> TypeParameterSyntaxAsync(TypeParameter parameter, SyntaxNode? constraint,
        TypeSyntaxContext state, CancellationToken cancellation)
    {
        var f = state.Factory;
        var modifiers = new List<SyntaxNode>();
        foreach (var kind in new[] { K.ConstKeyword, K.InKeyword, K.OutKeyword })
            if (parameter.Symbol?.Declarations.Any(d => SemanticSyntax.HasModifier(d, kind)) == true)
                modifiers.Add(f.NewToken(kind));
        string name = parameter.Symbol is { } symbol
            ? DisplayNameAsWritten(symbol, state.Symbols, true, cancellation)
            : "(Missing type parameter)";
        var defaultType = await Instantiation.Constraints.DefaultAsync(parameter, cancellation);
        return f.NewTypeParameterDeclaration(
            modifiers.Count == 0 ? null : new(modifiers.ToArray()),
            f.NewIdentifier(name),
            constraint,
            null,
            defaultType is null ? null : await TypeSyntaxAsync(defaultType, state, cancellation));
    }

    private async ValueTask<SyntaxNode> ObjectPropertySyntaxAsync(
        StructuredType type,
        TypeSyntaxContext state,
        CancellationToken cancellation)
    {
        var f = state.Factory;
        var members = new List<SyntaxNode>();
        foreach (var signature in type.CallSignatures)
            members.Add(await SignatureSyntaxAsync(signature, K.CallSignature, state, cancellation));
        foreach (var signature in type.ConstructSignatures)
            members.Add(await SignatureSyntaxAsync(signature, K.ConstructSignature, state, cancellation));
        foreach (var index in type.IndexInfos)
        {
            string name = index.Declaration is IndexSignatureDeclarationNode { Parameters: { Count: > 0 } parameters }
                && SemanticSyntax.Name(parameters[0]) is IdentifierNode id ? id.Text : "x";
            var parameter = f.NewParameterDeclaration(
                null,
                null,
                f.NewIdentifier(name),
                null,
                await TypeSyntaxAsync(index.KeyType, state, cancellation),
                null);
            members.Add(f.NewIndexSignatureDeclaration(index.IsReadonly ? new([f.NewToken(K.ReadonlyKeyword)]) : null, new([parameter]),
                await TypeSyntaxAsync(index.ValueType, state, cancellation)));
        }
        foreach (var property in type.Properties ?? [])
        {
            cancellation.ThrowIfCancellationRequested();
            if ((property.CheckFlags & Binding.CheckFlags.ReverseMapped) != 0)
                throw new NotSupportedException("Reverse-mapped property syntax requires recovery handling");
            var value = Values.NonMissing(await Values.GetAsync(property, cancellation), (property.Flags & SymbolFlags.Optional) != 0);
            SyntaxNode? reusedType = null;
            var declaration = property.ValueDeclaration ?? property.Declarations.FirstOrDefault();
            if ((property.Flags & (SymbolFlags.Accessor | SymbolFlags.Method | SymbolFlags.Function)) == 0
                && state.Symbols.Enclosing is not null && declaration is ITypedNode { Type: { } annotation }
                && (value.ObjectFlags & ObjectFlags.RequiresWidening) == 0)
            {
                var annotated = await Nodes.FromNodeAsync(annotation, cancellation);
                if (annotated != value)
                {
                    bool optional = declaration is PropertyDeclarationNode { PostfixToken.Kind: K.QuestionToken }
                        or PropertySignatureDeclarationNode { PostfixToken.Kind: K.QuestionToken };
                    var comparable = optional ? await Facts.FilterAsync(value, TypeFacts.NEUndefined, cancellation) : value;
                    if (annotated == comparable || annotated is UnionType && comparable is UnionType
                        && await Relations.RelatedAsync(annotated, comparable, RelationKind.Identity, cancellation))
                        value = annotated;
                }
                if (annotated == value)
                    reusedType = ReuseLiteralTypeSyntax(annotation, state, cancellation);
            }
            var nameType = links.Values.TryGet(property)?.NameType;
            if (nameType is UniqueSymbolType || nameType is { Flags: var flags } && (flags & TypeFlags.EnumLiteral) != 0)
                throw new NotSupportedException("Symbol-named properties require computed reference tracking");
            string name = property.Name;
            if (nameType is LiteralType { Value: string text })
                name = text;
            else if (nameType is LiteralType { Value: double number })
                name = TokenFacts.NumberText(number);
            bool stringNamed = property.Declarations.Count != 0;
            bool singleQuote = property.Declarations.Count != 0;
            foreach (var propertyDeclaration in property.Declarations)
            {
                var declarationName = DisplayDeclarationName(propertyDeclaration);
                singleQuote &= declarationName is StringLiteralNode quoted && (quoted.TokenFlags & TokenFlags.SingleQuote) != 0;
                stringNamed &= declarationName is StringLiteralNode
                    || declarationName is ComputedPropertyNameNode computed
                        && ((await ExpressionTypeForQueryAsync(computed.Expression!, cancellation)).Flags & TypeFlags.StringLike) != 0;
            }
            SyntaxNode propertyName;
            if (SemanticSyntax.Name(property.ValueDeclaration) is PrivateIdentifierNode privateName)
                propertyName = f.NewPrivateIdentifier(privateName.Text);
            else if (IdentifierName(name))
                propertyName = f.NewIdentifier(name);
            else if (!stringNamed && TokenFacts.NumberText(JsNumber.FromString(name)) == name && JsNumber.FromString(name) >= 0)
                propertyName = f.NewNumericLiteral(name, TokenFlags.None);
            else if (nameType is LiteralType && name.StartsWith('-') && TokenFacts.NumberText(JsNumber.FromString(name)) == name)
                propertyName = f.NewComputedPropertyName(
                    f.NewPrefixUnaryExpression(K.MinusToken, f.NewNumericLiteral(name[1..], TokenFlags.None)));
            else
                propertyName = f.NewStringLiteral(name, singleQuote ? TokenFlags.SingleQuote : TokenFlags.None);
            bool readOnly = (property.CheckFlags & Binding.CheckFlags.Readonly) != 0
                || property.Declarations.Any(d => SemanticSyntax.HasModifier(d, K.ReadonlyKeyword))
                || (property.Flags & SymbolFlags.GetAccessor) != 0 && (property.Flags & SymbolFlags.SetAccessor) == 0;
            if ((property.Flags & SymbolFlags.Accessor) != 0)
            {
                var write = await Values.WriteAsync(property, cancellation);
                if (value != context.ErrorType && write != context.ErrorType && (value != write
                    || (property.Parent?.Flags & SymbolFlags.Class) != 0 && !property.Declarations.Any(d => d is PropertyDeclarationNode)))
                {
                    foreach (var kind in new[] { K.GetAccessor, K.SetAccessor })
                        if (property.Declarations.FirstOrDefault(d => d.Kind == kind) is { } accessor)
                        {
                            var signature = await Signatures.FromDeclarationAsync(accessor, cancellation);
                            if (links.Values.TryGet(property)?.Mapper is { } mapper)
                                signature = await Instantiation.Engine.SignatureAsync(signature, mapper, false, cancellation);
                            members.Add(await SignatureSyntaxAsync(signature, kind, state, cancellation, propertyName));
                        }
                    continue;
                }
            }
            var question = (property.Flags & SymbolFlags.Optional) != 0 ? f.NewToken(K.QuestionToken) : null;
            if ((property.Flags & (SymbolFlags.Method | SymbolFlags.Function)) != 0 && !readOnly
                && (await Properties.GetAsync(value, cancellation)).Count == 0)
            {
                var signatures = await SignaturesAsync(
                    Algebra.Filter(value, t => (t.Flags & TypeFlags.Undefined) == 0),
                    false,
                    cancellation);
                foreach (var signature in signatures)
                    members.Add(await SignatureSyntaxAsync(signature, K.MethodSignature, state, cancellation, propertyName, question));
                if (signatures.Count != 0 || question is null)
                    continue;
            }
            members.Add(f.NewPropertySignatureDeclaration(readOnly ? new([f.NewToken(K.ReadonlyKeyword)]) : null, propertyName,
                question,
                reusedType ?? ((property.Flags & SymbolFlags.Accessor) != 0
                    ? await DeclarationTypeSyntaxAsync(value,
                        property.Declarations.OfType<GetAccessorDeclarationNode>().FirstOrDefault() as SyntaxNode
                            ?? property.Declarations.OfType<SetAccessorDeclarationNode>().FirstOrDefault()?.Parameters?.LastOrDefault(),
                        false, state, cancellation)
                    : await TypeSyntaxAsync(value, state, cancellation)),
                null));
        }
        var result = f.NewTypeLiteralNode(new(members.ToArray()));
        state.SingleLine.Add(result);
        return result;
    }

    private static SyntaxNode? ReuseLiteralTypeSyntax(SyntaxNode node, TypeSyntaxContext state, CancellationToken cancellation)
    {
        foreach (var child in node.DescendantsAndSelf())
        {
            cancellation.ThrowIfCancellationRequested();
            if (child is not (KeywordTypeNode or LiteralTypeNode or StringLiteralNode or NumericLiteralNode or BigIntLiteralNode
                or KeywordExpressionNode or PrefixUnaryExpressionNode or UnionTypeNode or IntersectionTypeNode or ParenthesizedTypeNode))
                return null;
        }
        var clone = node.DeepClone<SyntaxNode>(state.Factory);
        bool sameFile = SemanticSyntax.Source(node) == SemanticSyntax.Source(state.Symbols.Enclosing!);
        foreach (var child in clone.DescendantsAndSelf())
        {
            child.Parent = null;
            if (!sameFile)
            {
                child.Pos = -1;
                child.End = -1;
            }
            if (child is StringLiteralNode)
                state.NoAsciiEscape.Add(child);
        }
        return clone;
    }

    private async ValueTask<SyntaxNode> ReferenceTypeSyntaxAsync(
        TypeReference reference,
        TypeSyntaxContext state,
        CancellationToken cancellation)
    {
        var f = state.Factory;
        var arguments = await References.TypeArgumentsAsync(reference, cancellation);
        if (IsArray(reference) && arguments.Count != 0)
        {
            var array = f.NewArrayTypeNode(ParenthesizeType(await TypeSyntaxAsync(arguments[0], state, cancellation), f, postfix: true));
            return IsReadonlyArray(reference) ? f.NewTypeOperatorNode(K.ReadonlyKeyword, array) : array;
        }
        if (reference.Target is TupleType tuple)
        {
            var elements = new List<SyntaxNode>();
            for (int i = 0; i < tuple.ElementInfos.Count; i++)
            {
                var info = tuple.ElementInfos[i];
                var value = Values.NonMissing(arguments[i], (info.Flags & ElementFlags.Optional) != 0);
                SyntaxNode node = await TypeSyntaxAsync(value, state, cancellation);
                if ((info.Flags & ElementFlags.Rest) != 0)
                    node = f.NewArrayTypeNode(ParenthesizeType(node, f, postfix: true));
                if (info.LabeledDeclaration is NamedTupleMemberNode label)
                    node = f.NewNamedTupleMember((info.Flags & ElementFlags.Variable) != 0 ? f.NewToken(K.DotDotDotToken) : null,
                        f.NewIdentifier(label.Name!.Text),
                        (info.Flags & ElementFlags.Optional) != 0 ? f.NewToken(K.QuestionToken) : null,
                        node);
                else if ((info.Flags & ElementFlags.Variable) != 0)
                    node = f.NewRestTypeNode(node);
                else if ((info.Flags & ElementFlags.Optional) != 0)
                    node = f.NewOptionalTypeNode(ParenthesizeType(node, f, postfix: true));
                elements.Add(node);
            }
            var result = f.NewTupleTypeNode(new(elements.ToArray()));
            state.SingleLine.Add(result);
            return tuple.IsReadonly ? f.NewTypeOperatorNode(K.ReadonlyKeyword, result) : result;
        }
        if (reference.Target is not InterfaceType target || reference.Symbol is null)
            throw new InvalidOperationException("Unnamed type reference");
        int count = target.AllTypeParameters.Count - (target.ThisType is null ? 0 : 1);
        if (target.OuterTypeParameterCount != 0
            && !arguments.Take(target.OuterTypeParameterCount).SequenceEqual(target.AllTypeParameters.Take(target.OuterTypeParameterCount)))
            throw new NotSupportedException("Instantiated outer type arguments require reference composition");
        var nodes = await TypeSyntaxListAsync(
            arguments.Skip(target.OuterTypeParameterCount).Take(count - target.OuterTypeParameterCount).ToArray(),
            state,
            cancellation);
        return await SymbolTypeNodeAsync(reference.Symbol, SymbolFlags.Type, nodes, state.Symbols, true, cancellation);
    }

    private static SyntaxNode ParenthesizeType(SyntaxNode node, NodeFactory f, bool postfix = false) =>
        node is UnionTypeNode or IntersectionTypeNode or ConditionalTypeNode or FunctionTypeNode or ConstructorTypeNode
            || postfix && node is TypeOperatorNode or TypeQueryNode or InferTypeNode ? f.NewParenthesizedTypeNode(node) : node;

    private async ValueTask<IReadOnlyList<Type>> SyntaxUnionTypesAsync(IReadOnlyList<Type> types, CancellationToken cancellation)
    {
        var result = new List<Type>();
        TypeFlags flags = 0;
        for (int i = 0; i < types.Count; i++)
        {
            var type = types[i];
            flags |= type.Flags;
            if ((type.Flags & TypeFlags.Nullable) != 0)
                continue;
            Type? basis = (type.Flags & TypeFlags.BooleanLiteral) != 0 ? context.BooleanType
                : (type.Flags & TypeFlags.EnumLike) != 0 && type.Symbol is { } symbol
                    ? await Declared.GetAsync(
                        (symbol.Flags & SymbolFlags.EnumMember) != 0 ? program.Symbols.Parent(symbol)! : symbol,
                        cancellation) : null;
            if (basis is UnionType union && i + union.Types.Count <= types.Count
                && Regular(types[i + union.Types.Count - 1]) == Regular(union.Types[^1]))
            {
                result.Add(basis);
                i += union.Types.Count - 1;
            }
            else
                result.Add(type);
        }
        if ((flags & TypeFlags.Null) != 0)
            result.Add(context.NullType);
        if ((flags & TypeFlags.Undefined) != 0)
            result.Add(context.UndefinedType);
        return result;
        static Type Regular(Type type) => type is LiteralType literal ? literal.RegularType : type;
    }

    private static SyntaxNode DirectTypeName(Symbol symbol, NodeFactory f)
    {
        var symbols = new Stack<Symbol>();
        for (var current = symbol; current is not null; current = current.Parent)
            symbols.Push(current);
        SyntaxNode? node = null;
        foreach (var part in symbols)
            node = node is null ? f.NewIdentifier(part.Name) : f.NewQualifiedName(node, f.NewIdentifier(part.Name));
        return node!;
    }

    private static SyntaxNode AppendTypeName(SyntaxNode node, IdentifierNode name, NodeFactory f)
    {
        if (node is TypeReferenceNode reference)
            return f.NewTypeReferenceNode(f.NewQualifiedName(reference.TypeName, name), null);
        if (node is ImportTypeNode import)
            return f.NewImportTypeNode(import.IsTypeOf, import.Argument, import.Attributes,
            import.Qualifier is null ? name : f.NewQualifiedName(import.Qualifier, name), null);
        throw new InvalidOperationException("Cannot append a name to this type node");
    }
}

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
    private sealed class TypeSyntaxContext
    {
        internal TypeSyntaxContext(SyntaxNode? enclosing, bool aliasesOutsideScope, bool externalAliasesOnly = false,
            NodeBuilderFlags flags = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation)
        {
            Flags = flags;
            Length = new((flags & NodeBuilderFlags.NoTruncation) != 0);
            Symbols = new(enclosing,
                (aliasesOutsideScope ? SymbolFormatFlags.UseAliasDefinedOutsideCurrentScope : SymbolFormatFlags.None)
                    | (externalAliasesOnly ? SymbolFormatFlags.UseOnlyExternalAliasing : SymbolFormatFlags.None))
            {
                Length = Length,
                StringLiteralFlags = (flags & NodeBuilderFlags.UseSingleQuotesForStringLiteralType) != 0
                    ? TokenFlags.SingleQuote
                    : TokenFlags.None
            };
        }

        internal NodeBuilderFlags Flags { get; }
        internal TypeSyntaxLength Length { get; }
        internal NodeFactory Factory { get; } = new();
        internal SymbolDisplayContext Symbols { get; }
        internal HashSet<SyntaxNode> NoAsciiEscape { get; } = [];
        internal HashSet<SyntaxNode> SingleLine { get; } = [];
        internal HashSet<Type> Active { get; } = [];
        internal IReadOnlyList<TypeParameter>? InferParameters { get; set; }
    }

    internal ValueTask<string> SerializeTypeSyntaxAsync(Type type, SyntaxNode? enclosing = null, bool expandAlias = false,
        bool aliasesOutsideScope = false, CancellationToken cancellation = default) =>
        SerializeTypeSyntaxAsync(type, enclosing, NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation
            | (expandAlias ? NodeBuilderFlags.InTypeAlias : 0)
            | (aliasesOutsideScope ? NodeBuilderFlags.UseAliasDefinedOutsideCurrentScope : 0), cancellation);

    internal ValueTask<string> SerializeTypeSyntaxAsync(Type type, SyntaxNode? enclosing, NodeBuilderFlags flags,
        CancellationToken cancellation = default)
    {
        const NodeBuilderFlags supported = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation
            | NodeBuilderFlags.WriteArrayAsGenericType | NodeBuilderFlags.UseOnlyExternalAliasing
            | NodeBuilderFlags.UseAliasDefinedOutsideCurrentScope | NodeBuilderFlags.UseSingleQuotesForStringLiteralType
            | NodeBuilderFlags.NoTypeReduction | NodeBuilderFlags.OmitThisParameter | NodeBuilderFlags.AllowUniqueESSymbolType
            | NodeBuilderFlags.InTypeAlias;
        if ((flags & ~supported) != 0)
            throw new NotSupportedException("The requested node-builder options are not implemented");
        context.RequireOwned(type);
        return VisibilityQueryAsync(enclosing, () => ChainOperationAsync(() => ContainerOperationAsync(async () =>
        {
            var state = new TypeSyntaxContext(enclosing, (flags & NodeBuilderFlags.UseAliasDefinedOutsideCurrentScope) != 0,
                (flags & NodeBuilderFlags.UseOnlyExternalAliasing) != 0, flags);
            var node = await TypeSyntaxAsync(type, state, cancellation, (flags & NodeBuilderFlags.InTypeAlias) != 0);
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
        if ((state.Flags & NodeBuilderFlags.NoTypeReduction) == 0)
            type = await Views.ReducedAsync(type, cancellation);
        state.Length.Add(IntrinsicSyntaxLength(type));
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
                f.NewLiteralTypeNode(f.NewStringLiteral(Symbol.EscapeName(enumeration.Name), state.Symbols.StringLiteralFlags)));
        }
        if (type is LiteralType literal)
        {
            SyntaxNode value = literal.Value switch
            {
                string text => f.NewStringLiteral(text, (state.Flags & NodeBuilderFlags.UseSingleQuotesForStringLiteralType) != 0
                    ? TokenFlags.SingleQuote : TokenFlags.None),
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
        {
            if ((state.Flags & NodeBuilderFlags.AllowUniqueESSymbolType) == 0 && (await SymbolAccessibilityAsync(
                unique.Symbol,
                state.Symbols.Enclosing,
                SymbolFlags.Value,
                false,
                true,
                cancellation)).Accessibility == SymbolAccessibility.Accessible)
            {
                state.Length.Add(6);
                return await SymbolTypeNodeAsync(unique.Symbol!, SymbolFlags.Value, null, state.Symbols, false, cancellation);
            }
            state.Length.Add(13);
            return f.NewTypeOperatorNode(K.UniqueKeyword, f.NewKeywordTypeNode(K.SymbolKeyword));
        }
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
        if (type is ObjectType { Symbol: { } objectSymbol } && (type.ObjectFlags & ObjectFlags.Anonymous) != 0
            && (type.ObjectFlags & ObjectFlags.InstantiationExpressionType) == 0)
        {
            bool named = (objectSymbol.Flags & (SymbolFlags.Enum | SymbolFlags.ValueModule)) != 0;
            if ((objectSymbol.Flags & SymbolFlags.Class) != 0)
            {
                var declared = (InterfaceType)await Declared.GetAsync(objectSymbol, cancellation);
                var baseConstructor = await ClassBases.ConstructorAsync(declared, cancellation);
                named = (baseConstructor.Flags & TypeFlags.TypeVariable) == 0
                    && (baseConstructor is not IntersectionType intersection
                        || intersection.Types.All(t => (t.Flags & TypeFlags.TypeVariable) == 0));
            }
            if (named)
                return await SymbolTypeNodeAsync(objectSymbol, SymbolFlags.Value, null, state.Symbols, false, cancellation);
        }
        if (!state.Active.Add(type))
            return await RecursiveTypeSyntaxAsync(type, state, cancellation);
        var activeType = type;
        try
        {
            if (type is TypeReference reference && (type.ObjectFlags & ObjectFlags.Reference) != 0)
                return await ReferenceTypeSyntaxAsync(reference, state, cancellation);
            if (type is TypeParameter inferredParameter && state.InferParameters?.Contains(inferredParameter) == true)
            {
                state.Length.Add(inferredParameter.Symbol?.Name ?? "", 6);
                SyntaxNode? constraintNode = null;
                if (await Instantiation.Constraints.ConstraintAsync(inferredParameter, cancellation) is { } constraint)
                {
                    var inferred = await InferredConstraints.GetAsync(inferredParameter, omitReferences: true, cancellation);
                    if (inferred is null || !await Relations.RelatedAsync(constraint, inferred, RelationKind.Identity, cancellation))
                    {
                        state.Length.Add(9);
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
                type = origin;
            if (type is UnionOrIntersectionType composite)
            {
                IReadOnlyList<Type> types = type is UnionType
                    ? await SyntaxUnionTypesAsync(composite.Types, cancellation)
                    : composite.Types;
                if (types.Count == 1)
                    return await TypeSyntaxAsync(types[0], state, cancellation);
                var nodes = await TypeSyntaxListAsync(types, state, cancellation, bareList: true);
                if (nodes is null)
                    return f.NewKeywordTypeNode(K.AnyKeyword);
                var parenthesized = new NodeList(nodes.Select(n => n is FunctionTypeNode or ConstructorTypeNode or ConditionalTypeNode
                    or UnionTypeNode or IntersectionTypeNode ? f.NewParenthesizedTypeNode(n) : n).ToArray());
                return type is UnionType ? f.NewUnionTypeNode(parenthesized) : f.NewIntersectionTypeNode(parenthesized);
            }
            if (type is IndexType index)
            {
                state.Length.Add(6);
                return f.NewTypeOperatorNode(K.KeyOfKeyword, ParenthesizeType(await TypeSyntaxAsync(index.Target, state, cancellation), f));
            }
            if (type is IndexedAccessType indexed)
            {
                state.Length.Add(2);
                return f.NewIndexedAccessTypeNode(
                    ParenthesizeType(await TypeSyntaxAsync(indexed.ObjectType, state, cancellation), f, postfix: true),
                    await TypeSyntaxAsync(indexed.IndexType, state, cancellation));
            }
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
                state.Length.Add(2);
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
                if (state.Length.Truncated())
                    return ElidedTypeSyntax(state);
                var check = await TypeSyntaxAsync(conditional.CheckType, state, cancellation);
                state.Length.Add(15);
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
                if (type is InstantiationExpressionType { Node: TypeQueryNode query }
                    && await Nodes.FromNodeAsync(query, cancellation) == type
                    && await ReuseTypeQuerySyntaxAsync(query, state, cancellation) is { } reused)
                    return reused;
                if (type is MappedType mapped && (await Instantiation.Mapped.IsGenericAsync(mapped, cancellation) || mapped.ContainsError))
                    return await MappedTypeSyntaxAsync(mapped, state, cancellation);
                var resolved = await Members.ResolveAsync(objectType, cancellation);
                return await ObjectTypeSyntaxAsync(resolved, state, cancellation);
            }
            throw new NotSupportedException($"Type syntax construction is not yet implemented for {type.GetType().Name}");
        }
        finally
        {
            state.Active.Remove(activeType);
        }
    }

    private async ValueTask<NodeList?> TypeSyntaxListAsync(
        IReadOnlyList<Type> types,
        TypeSyntaxContext state,
        CancellationToken cancellation,
        bool bareList = false)
    {
        if (types.Count == 0)
            return null;
        if (state.Length.Truncated())
        {
            if (!bareList)
                return new([ElidedTypeSyntax(state, countLength: false)]);
            if (types.Count > 2)
                return new([await TypeSyntaxAsync(types[0], state, cancellation),
                    ElidedTypeSyntax(state, types.Count - 2, false), await TypeSyntaxAsync(types[^1], state, cancellation)]);
        }
        var nodes = new List<SyntaxNode>();
        for (int i = 0; i < types.Count; i++)
        {
            if (state.Length.Truncated() && i + 3 < types.Count - 1)
            {
                nodes.Add(ElidedTypeSyntax(state, types.Count - i - 1, false));
                nodes.Add(await TypeSyntaxAsync(types[^1], state, cancellation));
                break;
            }
            state.Length.Add(2);
            nodes.Add(await TypeSyntaxAsync(types[i], state, cancellation));
        }
        return new(nodes.ToArray());
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
        state.Length.Add(10);
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
        if (type.CallSignatures.Count + type.ConstructSignatures.Count + type.IndexInfos.Count + (type.Properties?.Count ?? 0) != 0
            && state.Length.Truncated())
        {
            members.Add(state.Length.NoTruncation ? f.NewNotEmittedTypeElement()
                : f.NewPropertySignatureDeclaration(null, f.NewIdentifier("..."), null, null, null));
            return Finish();
        }
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
            state.Length.Add(name, 4 + (index.IsReadonly ? 9 : 0));
        }
        var properties = type.Properties ?? [];
        for (int propertyIndex = 0; propertyIndex < properties.Count; propertyIndex++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (state.Length.Truncated() && propertyIndex + 3 < properties.Count - 1)
            {
                if (!state.Length.NoTruncation)
                    members.Add(f.NewPropertySignatureDeclaration(null,
                        f.NewIdentifier(
                            "... " + (properties.Count - propertyIndex - 1).ToString(CultureInfo.InvariantCulture) + " more ..."),
                        null, null, null));
                propertyIndex = properties.Count - 1;
            }
            var property = properties[propertyIndex];
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
                    reusedType = ReuseLiteralTypeSyntax(annotation, state, cancellation)
                        ?? await ReuseTypeAnnotationSyntaxAsync(annotation, state, cancellation);
            }
            var nameType = links.Values.TryGet(property)?.NameType;
            SyntaxNode? computedName = null;
            if (nameType?.Symbol is { } nameSymbol)
            {
                if (nameType is UniqueSymbolType)
                {
                    var sourceScope = new SymbolDisplayContext(declaration ?? state.Symbols.Enclosing, state.Symbols.Flags)
                    { Length = state.Length, ExpressionNames = true };
                    if (await SymbolTypeNodeAsync(
                        nameSymbol,
                        SymbolFlags.Value,
                        null,
                        sourceScope,
                        false,
                        cancellation) is TypeQueryNode query)
                        computedName = f.NewComputedPropertyName(query.ExprName);
                }
                else if ((nameType.Flags & TypeFlags.EnumLiteral) != 0 && state.Symbols.Enclosing is not null
                    && (await SymbolAccessibilityAsync(nameSymbol.Parent ?? nameSymbol, state.Symbols.Enclosing,
                        SymbolFlags.Value, false, false, cancellation)).Accessibility == SymbolAccessibility.Accessible
                    && await SymbolTypeNodeAsync(
                        nameSymbol,
                        SymbolFlags.Value,
                        null,
                        state.Symbols,
                        false,
                        cancellation) is TypeQueryNode query)
                    computedName = f.NewComputedPropertyName(query.ExprName);
            }
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
            if (computedName is not null)
                propertyName = computedName;
            else if (nameType is UniqueSymbolType)
                throw new NotSupportedException("Computed property name has no value expression");
            else if (SemanticSyntax.Name(property.ValueDeclaration) is PrivateIdentifierNode privateName)
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
            state.Length.Add(Symbol.EscapeName(property.Name), 1);
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
            var propertyTypeNode = reusedType ?? ((property.Flags & SymbolFlags.Accessor) != 0
                ? await DeclarationTypeSyntaxAsync(value,
                    property.Declarations.OfType<GetAccessorDeclarationNode>().FirstOrDefault() as SyntaxNode
                        ?? property.Declarations.OfType<SetAccessorDeclarationNode>().FirstOrDefault()?.Parameters?.LastOrDefault(),
                    false, state, cancellation)
                : await TypeSyntaxAsync(value, state, cancellation));
            if (readOnly)
                state.Length.Add(9);
            members.Add(f.NewPropertySignatureDeclaration(readOnly ? new([f.NewToken(K.ReadonlyKeyword)]) : null, propertyName,
                question,
                propertyTypeNode,
                null));
        }
        return Finish();

        SyntaxNode Finish()
        {
            var result = f.NewTypeLiteralNode(new(members.ToArray()));
            state.Length.Add(2);
            state.SingleLine.Add(result);
            return result;
        }
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
            if (child is StringLiteralNode literal)
            {
                literal.TokenFlags |= state.Symbols.StringLiteralFlags;
                state.NoAsciiEscape.Add(child);
            }
        }
        AddReusedSyntaxLength(node, state);
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
            if ((state.Flags & NodeBuilderFlags.WriteArrayAsGenericType) != 0)
                return f.NewTypeReferenceNode(f.NewIdentifier(IsReadonlyArray(reference) ? "ReadonlyArray" : "Array"),
                    new([await TypeSyntaxAsync(arguments[0], state, cancellation)]));
            var array = f.NewArrayTypeNode(ParenthesizeType(await TypeSyntaxAsync(arguments[0], state, cancellation), f, postfix: true));
            return IsReadonlyArray(reference) ? f.NewTypeOperatorNode(K.ReadonlyKeyword, array) : array;
        }
        if (reference.Target is TupleType tuple)
        {
            var elements = new List<SyntaxNode>();
            var types = arguments.Select((value, i) => Values.NonMissing(value,
                i < tuple.ElementInfos.Count && (tuple.ElementInfos[i].Flags & ElementFlags.Optional) != 0)).ToArray();
            var tupleNodes = await TypeSyntaxListAsync(types.Take(tuple.ElementInfos.Count).ToArray(), state, cancellation);
            for (int i = 0; i < (tupleNodes?.Count ?? 0); i++)
            {
                var info = tuple.ElementInfos[i];
                SyntaxNode node = tupleNodes![i];
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
        SyntaxNode? outer = null;
        int outerCount = target.OuterTypeParameterCount;
        for (int i = 0; i < outerCount;)
        {
            int start = i;
            var parent = ParameterOwner(target.AllTypeParameters[i]);
            do
                i++;
            while (i < outerCount && ParameterOwner(target.AllTypeParameters[i]) == parent);
            if (!arguments.Skip(start).Take(i - start).SequenceEqual(target.AllTypeParameters.Skip(start).Take(i - start)))
            {
                var group = await TypeSyntaxListAsync(arguments.Skip(start).Take(i - start).ToArray(), state, cancellation);
                var node = await SymbolTypeNodeAsync(parent!, SymbolFlags.Type, group, state.Symbols, true, cancellation);
                outer = outer is null ? node : AppendTypeReference(outer, node, f);
            }
        }
        var nodes = await TypeSyntaxListAsync(
            arguments.Skip(target.OuterTypeParameterCount).Take(count - target.OuterTypeParameterCount).ToArray(),
            state,
            cancellation);
        var final = await SymbolTypeNodeAsync(reference.Symbol, SymbolFlags.Type, nodes, state.Symbols, true, cancellation);
        return outer is null ? final : AppendTypeReference(outer, final, f);

        Symbol? ParameterOwner(Type type)
        {
            var declaration = type.Symbol?.Declarations.OfType<TypeParameterDeclarationNode>().FirstOrDefault();
            return declaration?.Parent is { } owner ? program.Symbols.Declaration(owner) : null;
        }
    }

    private static SyntaxNode AppendTypeReference(SyntaxNode root, SyntaxNode reference, NodeFactory factory)
    {
        if (reference is not TypeReferenceNode typeReference)
            throw new InvalidOperationException("Outer type argument composition requires a named reference");
        var names = new Stack<SyntaxNode>();
        var current = typeReference.TypeName;
        while (current is QualifiedNameNode qualified)
        {
            names.Push(qualified.Right!);
            current = qualified.Left;
        }
        names.Push((IdentifierNode)current!);
        SyntaxNode? name = root is ImportTypeNode import ? import.Qualifier : ((TypeReferenceNode)root).TypeName;
        foreach (var part in names)
            name = name is null ? part : factory.NewQualifiedName(name, part);
        // The pinned builder discards the outer argument list when appending a reference.
        return root is ImportTypeNode imported
            ? factory.NewImportTypeNode(imported.IsTypeOf, imported.Argument, imported.Attributes, name, typeReference.TypeArguments)
            : factory.NewTypeReferenceNode(name, typeReference.TypeArguments);
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

using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
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
            if (type is ObjectType objectType)
            {
                var resolved = await Members.ResolveAsync(objectType, cancellation);
                if (resolved.Properties is null or { Count: 0 } && resolved.CallSignatures.Count == 0
                    && resolved.ConstructSignatures.Count == 0 && resolved.IndexInfos.Count == 0)
                {
                    var empty = f.NewTypeLiteralNode(new([]));
                    state.SingleLine.Add(empty);
                    return empty;
                }
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
            || postfix && node is TypeOperatorNode or TypeQueryNode ? f.NewParenthesizedTypeNode(node) : node;

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

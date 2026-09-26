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
    private ValueTask<T> EmitSyntaxQueryAsync<T>(SyntaxNode node, SyntaxNode? enclosing, NodeBuilderFlags flags,
        Func<TypeSyntaxContext, ValueTask<T>> action, CancellationToken cancellation) =>
        VisibilityQueryAsync(enclosing, () => ChainOperationAsync(() => ContainerOperationAsync(() =>
        {
            RequireNode(node);
            return action(new(enclosing, (flags & NodeBuilderFlags.UseAliasDefinedOutsideCurrentScope) != 0,
                (flags & NodeBuilderFlags.UseOnlyExternalAliasing) != 0, flags));
        }, cancellation), cancellation), cancellation);

    private static string PrintEmitSyntax(SyntaxNode? node, SyntaxNode? enclosing, TypeSyntaxContext state,
        CancellationToken cancellation) => node is null ? "" : PrintDiagnosticNode(node, enclosing is SourceFileNode, cancellation,
            enclosing is null ? null : SemanticSyntax.Source(enclosing), state.NoAsciiEscape, state.SingleLine);

    internal ValueTask<string> SerializeExpressionTypeForEmitAsync(SyntaxNode expression, SyntaxNode? enclosing,
        NodeBuilderFlags flags = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation, CancellationToken cancellation = default)
    {
        if (!EmitParseNode(expression))
            return ValueTask.FromResult("any");
        return EmitSyntaxQueryAsync(expression, enclosing, flags | NodeBuilderFlags.MultilineObjectLiterals, async state =>
        {
            var type = await Algebra.RegularTypeAsync(await ExpressionTypeForQueryAsync(
                QuerySyntax.RightSide(expression) ? expression.Parent! : expression, cancellation), cancellation);
            return PrintEmitSyntax(await TypeSyntaxAsync(await Widening.GetAsync(type, cancellation), state, cancellation),
                enclosing, state, cancellation);
        }, cancellation);
    }

    internal ValueTask<string> SerializeDeclarationTypeForEmitAsync(SyntaxNode declaration, SyntaxNode? enclosing,
        NodeBuilderFlags flags = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation, CancellationToken cancellation = default)
    {
        if (!EmitParseNode(declaration))
            return ValueTask.FromResult("any");
        return EmitSyntaxQueryAsync(declaration, enclosing, flags | NodeBuilderFlags.MultilineObjectLiterals, async state =>
        {
            var symbol = program.Symbols.Declaration(declaration);
            Type type;
            if (symbol is null)
                type = declaration is VariableDeclarationNode or ParameterDeclarationNode or PropertyDeclarationNode
                    or PropertySignatureDeclarationNode or BindingElementNode or PropertyAssignmentNode or ShorthandPropertyAssignmentNode
                    ? await Variables.DeclaredOrInferredAsync(declaration, false, cancellation: cancellation) ?? context.ErrorType
                    : context.ErrorType;
            else if ((symbol.Flags & SymbolFlags.Accessor) != 0 && declaration is SetAccessorDeclarationNode)
                type = await Values.WriteAsync(symbol, cancellation);
            else if ((symbol.Flags & (SymbolFlags.TypeLiteral | SymbolFlags.Signature)) == 0)
                type = await Widening.LiteralAsync(await Values.GetAsync(symbol, cancellation), cancellation);
            else
                type = context.ErrorType;
            bool addUndefined = declaration is ParameterDeclarationNode parameter
                && await ParameterRequiresImplicitUndefinedAsync(parameter, enclosing, cancellation);
            if (addUndefined)
                type = await Algebra.UnionAsync([type, context.UndefinedType], cancellation: cancellation);
            if (type is UniqueSymbolType && type.Symbol == symbol && (enclosing is null
                || symbol!.Declarations.Any(d => SemanticSyntax.Source(d) == SemanticSyntax.Source(enclosing))))
                state.Flags |= NodeBuilderFlags.AllowUniqueESSymbolType;
            var node = await DeclarationTypeSyntaxAsync(
                type,
                declaration,
                !addUndefined && VariableTypes.Optional(declaration),
                state,
                cancellation);
            return PrintEmitSyntax(node, enclosing, state, cancellation);
        }, cancellation);
    }

    private async ValueTask<SyntaxNode?> ReuseInitializerTypeSyntaxAsync(Type type, SyntaxNode expression, TypeSyntaxContext state,
        CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        while (expression is ParenthesizedExpressionNode || SemanticSyntax.ConstAssertion(expression))
            expression = expression switch
            {
                ParenthesizedExpressionNode parentheses => parentheses.Expression!,
                AsExpressionNode assertion => assertion.Expression!,
                TypeAssertionNode assertion => assertion.Expression!,
                _ => throw new InvalidOperationException("Unexpected const assertion")
            };
        if (type is LiteralType && expression is (StringLiteralNode or NumericLiteralNode or BigIntLiteralNode or PrefixUnaryExpressionNode
            or KeywordExpressionNode or NoSubstitutionTemplateLiteralNode))
        {
            var actual = await Algebra.RegularTypeAsync(await ExpressionTypeForQueryAsync(expression, cancellation), cancellation);
            if (actual == await Algebra.RegularTypeAsync(type, cancellation))
            {
                var literal = CloneSyntaxBindingName(expression, state, typeAnnotation: true);
                AddReusedSyntaxLength(expression, state);
                return state.Factory.NewLiteralTypeNode(literal);
            }
        }
        if (type is TypeReference { Target: TupleType { IsReadonly: true } tuple } reference
            && expression is ArrayLiteralExpressionNode { Elements: { } elements }
            && ReusableConstArray(expression)
            && elements.All(e => e is not SpreadElementNode) && tuple.ElementInfos.All(e => e.Flags == ElementFlags.Required))
        {
            var arguments = await References.TypeArgumentsAsync(reference, cancellation);
            if (elements.Count != arguments.Count)
                return null;
            var items = new List<SyntaxNode>();
            for (int i = 0; i < elements.Count; i++)
                items.Add(await ReuseInitializerTypeSyntaxAsync(arguments[i], elements[i], state, cancellation)
                    ?? await TypeSyntaxAsync(arguments[i], state, cancellation));
            var node = state.Factory.NewTupleTypeNode(new(items.ToArray()));
            state.SingleLine.Add(node);
            return state.Factory.NewTypeOperatorNode(K.ReadonlyKeyword, node);
        }
        if (expression is ObjectLiteralExpressionNode { Properties: { } properties }
            && properties.All(p => p is PropertyAssignmentNode { Name: IdentifierNode or StringLiteralNode or NumericLiteralNode }))
        {
            var expected = await Properties.GetAsync(type, cancellation);
            if (expected.Count != properties.Count)
                return null;
            var members = new List<SyntaxNode>();
            foreach (PropertyAssignmentNode property in properties)
            {
                string? name = property.Name switch
                {
                    IdentifierNode identifier => identifier.Text,
                    StringLiteralNode text => text.Text,
                    NumericLiteralNode number => number.Text,
                    _ => null
                };
                var symbol = expected.FirstOrDefault(p => p.Name == name);
                if (symbol is null || (symbol.Flags & SymbolFlags.Optional) != 0)
                    return null;
                var value = await Values.GetAsync(symbol, cancellation);
                var observed = await ExpressionTypeForQueryAsync(property.Initializer!, cancellation);
                var inferred = await Algebra.RegularTypeAsync(observed, cancellation);
                var widened = await Widening.LiteralAsync(observed, cancellation);
                if (await Algebra.RegularTypeAsync(value, cancellation) != inferred
                    && !await Relations.RelatedAsync(value, widened, RelationKind.Identity, cancellation))
                    return null;
                var typeNode = await ReuseInitializerTypeSyntaxAsync(value, property.Initializer!, state, cancellation)
                    ?? await TypeSyntaxAsync(value, state, cancellation);
                members.Add(
                    state.Factory.NewPropertySignatureDeclaration(
                    IsReadonly(symbol) ? new([state.Factory.NewToken(K.ReadonlyKeyword)]) : null,
                    CloneSyntaxBindingName(property.Name!, state), null, typeNode, null));
            }
            var result = state.Factory.NewTypeLiteralNode(new(members.ToArray()));
            state.SingleLine.Add(result);
            return result;
        }
        return null;
    }

    private static bool ReusableConstArray(SyntaxNode expression)
    {
        var parent = expression.Parent;
        while (parent is ArrayLiteralExpressionNode or ObjectLiteralExpressionNode or ParenthesizedExpressionNode
            or SpreadElementNode or PropertyAssignmentNode or ShorthandPropertyAssignmentNode or TemplateSpanNode or PrefixUnaryExpressionNode)
            parent = parent.Parent;
        if (parent is null || !SemanticSyntax.ConstAssertion(parent))
            return false;
        for (var node = expression.Parent; node is not null; node = node.Parent)
            if (node is CallExpressionNode or SatisfiesExpressionNode or JsxElementNode or JsxExpressionNode
                || node is (VariableDeclarationNode or ParameterDeclarationNode or PropertyDeclarationNode
                    or PropertySignatureDeclarationNode or AsExpressionNode or TypeAssertionNode)
                    and ITypedNode { Type: not null } && !SemanticSyntax.ConstAssertion(node))
                return false;
        return true;
    }

    internal ValueTask<string> SerializeReturnTypeForEmitAsync(SyntaxNode declaration, SyntaxNode? enclosing,
        NodeBuilderFlags flags = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation, CancellationToken cancellation = default)
    {
        if (!EmitParseNode(declaration))
            return ValueTask.FromResult("any");
        return EmitSyntaxQueryAsync(declaration, enclosing, flags, async state =>
        {
            var signature = await Signatures.FromDeclarationAsync(declaration, cancellation);
            var allocated = new List<Symbol>();
            using var names = state.ParameterNames?.EnterScope();
            using var qualifiedNames = state.QualifiedNames.EnterScope();
            try
            {
                state.Mapper = signature.Mapper;
                var expanded = await ExpandedSyntaxParametersAsync(signature, allocated, cancellation);
                using var values = EnterValueParameterScope(declaration, expanded, signature.Parameters, state, cancellation);
                using var parameters = EnterGeneratedParameterScope(declaration, signature.TypeParameters, state, cancellation);
                return PrintEmitSyntax(await ReturnTypeSyntaxAsync(signature, state, cancellation), enclosing, state, cancellation);
            }
            finally
            {
                foreach (var symbol in allocated)
                    links.Values.Remove(symbol);
            }
        }, cancellation);
    }

    private async ValueTask<SyntaxNode?> ReturnTypeSyntaxAsync(Signature signature, TypeSyntaxContext state,
        CancellationToken cancellation)
    {
        var flags = state.Flags;
        try
        {
            state.Flags &= ~NodeBuilderFlags.SuppressAnyReturnType;
            var type = await Signatures.ReturnAsync(signature, cancellation);
            if ((flags & NodeBuilderFlags.SuppressAnyReturnType) != 0 && (type.Flags & TypeFlags.Any) != 0)
                return null;
            if (await Signatures.PredicateAsync(signature, cancellation) is { } predicate)
                return await PredicateTypeSyntaxAsync(predicate, state, cancellation);
            return await DeclarationTypeSyntaxAsync(type, signature.Declaration, false, state, cancellation);
        }
        finally
        {
            state.Flags = flags;
        }
    }

    private async ValueTask<SyntaxNode> PredicateTypeSyntaxAsync(TypePredicate predicate, TypeSyntaxContext state,
        CancellationToken cancellation)
    {
        SyntaxNode name = predicate.Kind is TypePredicateKind.This or TypePredicateKind.AssertsThis
            ? state.Factory.NewThisTypeNode() : state.Factory.NewIdentifier(predicate.ParameterName);
        state.NoAsciiEscape.Add(name);
        return state.Factory.NewTypePredicateNode(
            predicate.Kind is TypePredicateKind.AssertsThis or TypePredicateKind.AssertsIdentifier
                ? state.Factory.NewToken(K.AssertsKeyword) : null,
            name, predicate.Type is null ? null : await TypeSyntaxAsync(predicate.Type, state, cancellation));
    }

    internal ValueTask<IReadOnlyList<string>> SerializeTypeParametersForEmitAsync(SyntaxNode declaration, SyntaxNode? enclosing,
        NodeBuilderFlags flags = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation, CancellationToken cancellation = default)
    {
        if (!EmitParseNode(declaration))
            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        return EmitSyntaxQueryAsync<IReadOnlyList<string>>(declaration, enclosing, flags, async state =>
        {
            var symbol = program.Symbols.Declaration(declaration);
            if (symbol is null)
                return [];
            var target = (symbol.CheckFlags & Binding.CheckFlags.Instantiated) != 0
                ? links.Values.TryGet(symbol)?.Target ?? symbol : symbol;
            IReadOnlyList<Type> parameters;
            if ((target.Flags & (SymbolFlags.Class | SymbolFlags.Interface | SymbolFlags.Alias)) != 0)
                parameters = program.Scopes.Local(symbol, cancellation);
            else if ((target.Flags & SymbolFlags.Function) != 0
                && symbol.ValueDeclaration is IFunctionSignature { TypeParameters: { } nodes })
                parameters = nodes.Select(n => program.Scopes.Parameter(program.Symbols.Declaration(n)!)).ToArray();
            else
                return [];
            var result = new List<string>();
            foreach (TypeParameter parameter in parameters)
            {
                var constraint = await Instantiation.Constraints.ConstraintAsync(parameter, cancellation);
                var node = await TypeParameterSyntaxAsync(parameter,
                    constraint is null ? null : await ConstraintSyntaxAsync(parameter, constraint, state, cancellation),
                    state,
                    cancellation);
                result.Add(PrintEmitSyntax(node, enclosing, state, cancellation));
            }
            return result;
        }, cancellation);
    }

    internal ValueTask<string?> SerializeLiteralConstForEmitAsync(SyntaxNode declaration, CancellationToken cancellation = default) =>
        EmitSyntaxQueryAsync<string?>(declaration, declaration, NodeBuilderFlags.None, async state =>
        {
            if (program.Symbols.Declaration(declaration) is not { } symbol)
                return null;
            var type = await Values.GetAsync(symbol, cancellation);
            var f = state.Factory;
            SyntaxNode? node;
            if ((type.Flags & TypeFlags.EnumLike) != 0 && type.Symbol is { } enumeration)
            {
                var scope = new SymbolDisplayContext(declaration, SymbolFormatFlags.None) { ExpressionNames = true };
                var reference = await SymbolTypeNodeAsync(enumeration, SymbolFlags.Value, null, scope, false, cancellation);
                node = reference is TypeQueryNode query ? TypeNameExpression(query.ExprName!, f) : reference;
            }
            else if (type is LiteralType literal)
                node = literal.Value switch
                {
                    string text => f.NewStringLiteral(text, TokenFlags.None),
                    double number when double.IsPositiveInfinity(number) => f.NewIdentifier("Infinity"),
                    double number when double.IsNegativeInfinity(number) => f.NewPrefixUnaryExpression(
                        K.MinusToken,
                        f.NewIdentifier("Infinity")),
                    double number when double.IsNaN(number) => f.NewIdentifier("NaN"),
                    double number when number < 0 => f.NewPrefixUnaryExpression(K.MinusToken,
                        f.NewNumericLiteral(TokenFacts.NumberText(number)[1..], TokenFlags.None)),
                    double number => f.NewNumericLiteral(TokenFacts.NumberText(number), TokenFlags.None),
                    BigInteger integer => f.NewBigIntLiteral(integer.ToString(CultureInfo.InvariantCulture) + "n", TokenFlags.None),
                    bool boolean => f.NewKeywordExpression(boolean ? K.TrueKeyword : K.FalseKeyword),
                    _ => throw new InvalidOperationException("Unexpected literal const type")
                };
            else
                node = null;
            return node is null ? null : PrintEmitSyntax(node, null, state, cancellation);
        }, cancellation);
}

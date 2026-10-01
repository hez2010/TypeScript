using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask<bool> RecoverableExpressionAsync(SyntaxNode expression, Type type, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (type == context.ErrorType)
            return true;
        while (expression is ParenthesizedExpressionNode || SemanticSyntax.ConstAssertion(expression))
            expression = expression is ParenthesizedExpressionNode parentheses ? parentheses.Expression!
                : expression is AsExpressionNode assertion ? assertion.Expression! : ((TypeAssertionNode)expression).Expression!;
        if (expression is AsExpressionNode or TypeAssertionNode)
            return await EquivalentReturnAnnotationAsync(((ITypedNode)expression).Type!, type, cancellation);
        if (expression is (FunctionExpressionNode or ArrowFunctionNode or MethodDeclarationNode)
            and IFullSignatureNode { FullSignature: { } fullSignature })
            return await EquivalentReturnAnnotationAsync(fullSignature, type, cancellation);
        if (expression is ArrayLiteralExpressionNode { Elements: { } elements })
        {
            if (!ReusableConstArray(expression) || elements.Any(e => e is SpreadElementNode)
                || type is not TypeReference { Target: TupleType tuple } reference
                || tuple.ElementInfos.Any(e => e.Flags != ElementFlags.Required))
                return false;
            var arguments = await References.TypeArgumentsAsync(reference, cancellation);
            if (elements.Count != arguments.Count)
                return false;
            for (int i = 0; i < elements.Count; i++)
                if (!await RecoverableExpressionAsync(elements[i], arguments[i], cancellation))
                    return false;
            return true;
        }
        if (expression is ObjectLiteralExpressionNode { Properties: { } properties })
        {
            var objectElements = RecoverableObjectElements(properties);
            if (objectElements is null)
                return false;
            var expected = await Properties.GetAsync(type, cancellation);
            if (expected.Sum(p => Math.Max(1, p.Declarations.Length)) != objectElements.Count)
                return false;
            foreach (var property in objectElements)
            {
                var source = program.Symbols.Declaration(property);
                var symbol = expected.FirstOrDefault(
                    p => p.Name == source?.Name || SemanticSyntax.Name(p.ValueDeclaration) == SemanticSyntax.Name(property));
                if (symbol is null || (symbol.Flags & SymbolFlags.Optional) != 0
                    || !await RecoverableObjectMemberAsync(property, symbol, cancellation))
                    return false;
            }
            return true;
        }
        if (expression is FunctionExpressionNode or ArrowFunctionNode or MethodDeclarationNode)
        {
            if ((await Properties.GetAsync(type, cancellation)).Count != 0) return false;
            var signatures = await SignaturesAsync(type, false, cancellation);
            if (signatures.Count != 1)
                return false;
            var target = signatures[0];
            var source = (IFunctionSignature)expression;
            if ((source.TypeParameters?.Count ?? 0) != target.TypeParameters.Count)
                return false;
            var parameters = source.Parameters?.Cast<ParameterDeclarationNode>().ToArray() ?? [];
            var targetParameters = target.ThisParameter is { } receiver ? new[] { receiver }.Concat(target.Parameters).ToArray()
                : target.Parameters.ToArray();
            if (parameters.Length != targetParameters.Length)
                return false;
            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                bool optional = await OptionalSyntaxParameterAsync(parameter, cancellation);
                if (targetParameters[i].ValueDeclaration is ParameterDeclarationNode targetParameter
                    && optional != await OptionalSyntaxParameterAsync(targetParameter, cancellation))
                    return false;
                var parameterType = await Parameters.ParameterAsync(targetParameters[i], cancellation);
                if (optional)
                    parameterType = await Facts.FilterAsync(parameterType, TypeFacts.NEUndefined, cancellation);
                if (parameter.Type is { } annotation)
                {
                    if (!await EquivalentReturnAnnotationAsync(annotation, parameterType, cancellation))
                        return false;
                }
                else if (parameter.Initializer is not { } initializer || ContextualReturnExpression(parameter)
                    || !await RecoverableExpressionAsync(initializer, parameterType, cancellation))
                    return false;
            }
            var predicate = await Signatures.PredicateAsync(target, cancellation);
            if (predicate is not null)
                return expression is ITypedNode { Type: TypePredicateNode annotation }
                    && await EquivalentPredicateSyntaxAsync(annotation, predicate, cancellation);
            var returned = await Signatures.ReturnAsync(target, cancellation);
            if (expression is ITypedNode { Type: { } annotationType })
                return await EquivalentReturnAnnotationAsync(annotationType, returned, cancellation);
            var candidate = SemanticSyntax.Generator(expression) || SemanticSyntax.HasModifier(expression, SyntaxKind.AsyncKeyword)
                ? null : SingleReturnExpression(expression, cancellation);
            if (candidate is not null && !ContextualReturnExpression(candidate))
                return await RecoverableExpressionAsync(candidate, returned, cancellation);
            return await IdenticalAsync(
                await Signatures.ReturnAsync(await Signatures.FromDeclarationAsync(expression, cancellation), cancellation),
                returned, cancellation);
        }
        Type candidateType;
        if (expression.Kind == SyntaxKind.OmittedExpression || expression is IdentifierNode { Text.Span: var matchedText } && matchedText.SequenceEqual("undefined"u8))
            candidateType = context.UndefinedWideningType;
        else if (expression.Kind == SyntaxKind.NullKeyword)
            candidateType = context.NullWideningType;
        else
        {
            candidateType = await Algebra.RegularTypeAsync(await ExpressionTypeForQueryAsync(expression, cancellation), cancellation);
            if (expression is StringLiteralNode or NoSubstitutionTemplateLiteralNode or NumericLiteralNode or BigIntLiteralNode
                or PrefixUnaryExpressionNode
                {
                    Operator: SyntaxKind.PlusToken or SyntaxKind.MinusToken, Operand: NumericLiteralNode
                    or BigIntLiteralNode
                }
                || expression.Kind is SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword)
            {
                if (!await Contexts.ConstAsync(expression, cancellation))
                    candidateType = await Widening.LiteralBaseAsync(candidateType, cancellation);
            }
            else
                candidateType = await Widening.GetAsync(candidateType, cancellation);
        }
        return await Algebra.RegularTypeAsync(candidateType, cancellation) == await Algebra.RegularTypeAsync(type, cancellation)
            || candidateType is UnionType && type is UnionType && await IdenticalAsync(candidateType, type, cancellation);
    }

    private IReadOnlyList<SyntaxNode>? RecoverableObjectElements(NodeList properties)
    {
        var elements = new List<SyntaxNode>();
        foreach (var property in properties)
        {
            var name = SemanticSyntax.Name(property);
            if (property is not (PropertyAssignmentNode or MethodDeclarationNode or GetAccessorDeclarationNode
                or SetAccessorDeclarationNode)
                || (property.Flags & NodeFlags.ThisNodeHasError) != 0 || name is null or PrivateIdentifierNode
                || (name.Flags & NodeFlags.ThisNodeHasError) != 0
                || name is ComputedPropertyNameNode
                {
                    Expression: not (StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode
                    or PrefixUnaryExpressionNode { Operator: SyntaxKind.PlusToken or SyntaxKind.MinusToken, Operand: NumericLiteralNode })
                })
                return null;
            if (property is GetAccessorDeclarationNode or SetAccessorDeclarationNode)
            {
                var pair = program.Symbols.Binding(property)?.Get(property)?.Symbol?.Declarations
                    .Where(n => n is GetAccessorDeclarationNode or SetAccessorDeclarationNode).ToArray() ?? [];
                bool bothAnnotated = pair.OfType<GetAccessorDeclarationNode>().Any(n => n.Type is not null)
                    && pair.OfType<SetAccessorDeclarationNode>().Any(n => n.Parameters is { Count: > 0 }
                        && n.Parameters[0] is ParameterDeclarationNode { Type: not null });
                if (!bothAnnotated && pair.FirstOrDefault() != property)
                    continue;
            }
            elements.Add(property);
        }
        return elements;
    }

    private static SyntaxNode RecoveredPropertyName(SyntaxNode name, bool method, TypeSyntaxContext state)
    {
        var value = name is ComputedPropertyNameNode computed ? computed.Expression : name;
        Utf8String? text = value switch
        {
            IdentifierNode identifier => identifier.Text,
            StringLiteralNode literal => literal.Text,
            NumericLiteralNode literal => literal.Text,
            NoSubstitutionTemplateLiteralNode literal => literal.Text,
            _ => (Utf8String?)null
        };
        if (text is not null)
        {
            SyntaxNode? result = null;
            if (IdentifierName(text.Value) && !(method && text == Utf8Literals.New))
                result = name is IdentifierNode ? CloneSyntaxBindingName(name, state) : state.Factory.NewIdentifier(text.Value);
            if (method && text == Utf8Literals.New && name is not StringLiteralNode)
                result = state.Factory.NewStringLiteral(text.Value, TokenFlags.None);
            if (result is not null)
            {
                if (SemanticSyntax.Source(name) == SemanticSyntax.Source(state.Symbols.Enclosing))
                    (result.Pos, result.End) = (name.Pos, name.End);
                return result;
            }
        }
        return CloneSyntaxBindingName(name, state);
    }

    private async ValueTask<bool> RecoverableObjectMemberAsync(SyntaxNode node, Symbol symbol, CancellationToken cancellation)
    {
        var type = node is SetAccessorDeclarationNode
            ? await Values.WriteAsync(symbol, cancellation)
            : await Values.GetAsync(symbol, cancellation);
        if (node is PropertyAssignmentNode property)
            return await RecoverableExpressionAsync(property.Initializer!, type, cancellation);
        if (node is MethodDeclarationNode)
            return await RecoverableExpressionAsync(node, type, cancellation);
        if (AccessorSyntaxAnnotation(node) is { } annotation)
            return await EquivalentReturnAnnotationAsync(annotation, type, cancellation);
        var getter = node as GetAccessorDeclarationNode
            ?? program.Symbols.Binding(node)?.Get(node)?.Symbol?.Declarations.OfType<GetAccessorDeclarationNode>().FirstOrDefault();
        if (getter is null)
            return false;
        return SingleReturnExpression(getter, cancellation) is { } expression
            ? await RecoverableExpressionAsync(expression, type, cancellation)
            : await IdenticalAsync(
                await Signatures.ReturnAsync(await Signatures.FromDeclarationAsync(getter, cancellation), cancellation),
                type,
                cancellation);
    }

    private async ValueTask<bool> EquivalentPredicateSyntaxAsync(
        TypePredicateNode node,
        TypePredicate predicate,
        CancellationToken cancellation)
    {
        bool isThis = node.ParameterName?.Kind == SyntaxKind.ThisType;
        if (isThis != predicate.Kind is TypePredicateKind.This or TypePredicateKind.AssertsThis
            || node.AssertsModifier is not null != predicate.Kind is TypePredicateKind.AssertsIdentifier
                or TypePredicateKind.AssertsThis
            || !isThis && node.ParameterName is IdentifierNode name && name.Text != predicate.ParameterName
            || node.Type is null != predicate.Type is null)
            return false;
        return node.Type is null || await IdenticalAsync(await Nodes.FromNodeAsync(node.Type, cancellation), predicate.Type!, cancellation);
    }

    private async ValueTask<SyntaxNode?> RecoverReturnSyntaxAsync(Signature signature, Type type, TypePredicate? predicate,
        TypeSyntaxContext state, CancellationToken cancellation)
    {
        var declaration = signature.Declaration;
        if (state.Symbols.Enclosing is null || declaration is null || (declaration.Flags & NodeFlags.Synthesized) != 0)
            return null;
        var annotation = declaration is GetAccessorDeclarationNode ? AccessorSyntaxAnnotation(declaration)
            : (declaration as ITypedNode)?.Type;
        if (annotation is not null)
        {
            if (annotation is TypePredicateNode annotatedPredicate)
            {
                if (predicate is null || !await EquivalentPredicateSyntaxAsync(annotatedPredicate, predicate, cancellation))
                    return null;
                return await RecoverAnnotationSyntaxAsync(annotation, state, cancellation);
            }
            if (predicate is null && await EquivalentReturnAnnotationAsync(annotation, type, cancellation))
                return await RecoverAnnotationSyntaxAsync(annotation, state, cancellation);
            return null;
        }
        if (predicate is not null
            || SemanticSyntax.Generator(declaration)
            || SemanticSyntax.HasModifier(declaration, SyntaxKind.AsyncKeyword))
            return null;
        var expression = SingleReturnExpression(declaration, cancellation);
        if (expression is null)
            return null;
        var assertion = expression as ITypedNode;
        bool cast = expression is AsExpressionNode or TypeAssertionNode && !SemanticSyntax.ConstAssertion(expression);
        if (ContextualReturnExpression(expression) && !cast)
            return null;
        if (cast && assertion?.Type is { } asserted && await EquivalentReturnAnnotationAsync(asserted, type, cancellation))
            return await RecoverAnnotationSyntaxAsync(asserted, state, cancellation);
        return await ReuseInitializerTypeSyntaxAsync(type, expression, state, cancellation);
    }

    private SyntaxNode? AccessorSyntaxAnnotation(SyntaxNode declaration)
    {
        static SyntaxNode? Annotation(SyntaxNode node) => node is GetAccessorDeclarationNode getter ? getter.Type
            : node is SetAccessorDeclarationNode { Parameters: { Count: > 0 } parameters }
                && parameters[0] is ParameterDeclarationNode parameter ? parameter.Type : null;
        if (Annotation(declaration) is { } own)
            return own;
        foreach (var other in program.Symbols.Binding(declaration)?.Get(declaration)?.Symbol?.Declarations ?? [])
            if (other != declaration
                && other is GetAccessorDeclarationNode or SetAccessorDeclarationNode
                && Annotation(other) is { } annotation)
                return annotation;
        return null;
    }

    private async ValueTask<SyntaxNode?> RecoverAccessorSyntaxAsync(SyntaxNode declaration, Type type, TypeSyntaxContext state,
        CancellationToken cancellation)
    {
        if (AccessorSyntaxAnnotation(declaration) is { } annotation)
            return await EquivalentReturnAnnotationAsync(annotation, type, cancellation)
                ? await RecoverAnnotationSyntaxAsync(annotation, state, cancellation) : null;
        var getter = declaration as GetAccessorDeclarationNode
            ?? program.Symbols.Binding(declaration)?.Get(declaration)?.Symbol?.Declarations.OfType<GetAccessorDeclarationNode>().FirstOrDefault();
        return getter is null ? null : await RecoverReturnSyntaxAsync(await Signatures.FromDeclarationAsync(getter, cancellation),
            type, null, state, cancellation);
    }

    private async ValueTask<bool> EquivalentReturnAnnotationAsync(SyntaxNode annotation, Type type, CancellationToken cancellation)
    {
        if (type == context.ErrorType)
            return true;
        var annotated = await Nodes.FromNodeAsync(annotation, cancellation);
        return await Algebra.RegularTypeAsync(annotated, cancellation) == await Algebra.RegularTypeAsync(type, cancellation)
            || annotated is UnionType && type is UnionType && await IdenticalAsync(annotated, type, cancellation);
    }

    private static SyntaxNode? SingleReturnExpression(SyntaxNode declaration, CancellationToken cancellation)
    {
        var body = SemanticSyntax.Body(declaration);
        if (body is null || body.Pos == body.End)
            return null;
        if (body is not BlockNode)
            return body;
        SyntaxNode? expression = null;
        var pending = new Stack<SyntaxNode>();
        pending.Push(body);
        while (pending.TryPop(out var node))
        {
            cancellation.ThrowIfCancellationRequested();
            if (node is ReturnStatementNode returned)
            {
                if (returned.Parent != body || expression is not null)
                    return null;
                expression = returned.Expression;
            }
            else if (node == body || !Signatures.FunctionLike(node) && !SemanticSyntax.ClassLike(node))
                for (int i = node.ChildCount - 1; i >= 0; i--)
                    pending.Push(node.GetChild(i));
        }
        return expression;
    }

    private static bool ContextualReturnExpression(SyntaxNode expression)
    {
        for (var node = expression.Parent; node is not null; node = node.Parent)
            if (node is CallExpressionNode or SatisfiesExpressionNode or JsxElementNode or JsxExpressionNode
                || node is (VariableDeclarationNode or ParameterDeclarationNode or PropertyDeclarationNode
                    or PropertySignatureDeclarationNode
                    or AsExpressionNode or TypeAssertionNode) and ITypedNode { Type: not null } && !SemanticSyntax.ConstAssertion(node))
                return true;
        return false;
    }
}

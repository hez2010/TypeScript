using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private enum InferenceSyntaxKind
    {
        Declaration,
        Expression,
        Return
    }

    private async ValueTask ReportInferenceFallbacksAsync(SyntaxNode declaration, bool returnType, TypeSyntaxContext state, CancellationToken cancellation)
    {
        if (!state.HasTracker || state.SuppressInferenceFallback || state.Symbols.Enclosing is null
            || (declaration.Flags & NodeFlags.Synthesized) != 0)
            return;
        var pending = new Stack<(SyntaxNode Node, InferenceSyntaxKind Kind)>();
        pending.Push((declaration, returnType ? InferenceSyntaxKind.Return : InferenceSyntaxKind.Declaration));
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            var node = item.Node;
            if (item.Kind == InferenceSyntaxKind.Return)
            {
                if (node is ITypedNode { Type: not null })
                    continue;
                if (await Signatures.PredicateAsync(await Signatures.FromDeclarationAsync(node, cancellation), cancellation) is not null)
                {
                    state.Tracker.ReportInferenceFallback(node);
                    continue;
                }
                var expression = SemanticSyntax.Generator(node) || SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword)
                    ? null : SingleReturnExpression(node, cancellation);
                if (node is GetAccessorDeclarationNode && (expression is null || PlainInferredExpression(expression)))
                {
                    state.Tracker.ReportInferenceFallback(node);
                    continue;
                }
                if (expression is null || ContextualReturnExpression(expression) && !SemanticSyntax.ConstAssertion(expression)
                    && expression is not (AsExpressionNode or TypeAssertionNode))
                    state.Tracker.ReportInferenceFallback(node);
                else
                    pending.Push((expression, InferenceSyntaxKind.Expression));
                continue;
            }
            if (item.Kind == InferenceSyntaxKind.Declaration)
            {
                if (node is ITypedNode { Type: { } annotation })
                {
                    if (node != declaration && node is ParameterDeclarationNode parameter
                        && await ParameterRequiresImplicitUndefinedAsync(parameter, state.Symbols.Enclosing, cancellation)
                        && CouldReferToUndefined(annotation))
                        state.Tracker.ReportInferenceFallback(parameter);
                    continue;
                }
                if (node is GetAccessorDeclarationNode or SetAccessorDeclarationNode)
                {
                    if (AccessorSyntaxAnnotation(node) is null)
                    {
                        var getter = node as GetAccessorDeclarationNode ?? program.Symbols.Declaration(node)?.Declarations
                            .OfType<GetAccessorDeclarationNode>().FirstOrDefault();
                        if (getter is null)
                            state.Tracker.ReportInferenceFallback(node);
                        else
                            pending.Push((getter, InferenceSyntaxKind.Return));
                    }
                    continue;
                }
                if (node is BindingElementNode or ParameterDeclarationNode { Name: BindingPatternNode })
                {
                    state.Tracker.ReportInferenceFallback(node);
                    continue;
                }
                if (node is ParameterDeclarationNode { Parent: SetAccessorDeclarationNode accessor })
                {
                    pending.Push((accessor, InferenceSyntaxKind.Declaration));
                    continue;
                }
                var initializer = node is ExportAssignmentNode export ? export.Expression : (node as IInitializedNode)?.Initializer;
                var assertedInitializer = initializer;
                while (assertedInitializer is ParenthesizedExpressionNode parentheses) assertedInitializer = parentheses.Expression;
                if (node != declaration && node is ParameterDeclarationNode inferredParameter
                    && assertedInitializer is AsExpressionNode or TypeAssertionNode && !SemanticSyntax.ConstAssertion(assertedInitializer)
                    && CouldReferToUndefined(((ITypedNode)assertedInitializer).Type!)
                    && await ParameterRequiresImplicitUndefinedAsync(inferredParameter, state.Symbols.Enclosing, cancellation))
                    state.Tracker.ReportInferenceFallback(inferredParameter);
                if (initializer is null || ContextualReturnExpression(node)
                    || PlainInferredExpression(initializer)
                    || initializer is TemplateExpressionNode && (node is VariableDeclarationNode
                        && (node.Parent?.Flags & NodeFlags.Const) != 0
                        || SemanticSyntax.HasModifier(node, SyntaxKind.ReadonlyKeyword)))
                    state.Tracker.ReportInferenceFallback(node is ExportAssignmentNode assignment ? assignment.Expression! : node);
                else
                    pending.Push((initializer, InferenceSyntaxKind.Expression));
                continue;
            }
            while (node is ParenthesizedExpressionNode parentheses)
                node = parentheses.Expression!;
            if (node is AsExpressionNode or TypeAssertionNode)
            {
                if (SemanticSyntax.ConstAssertion(node))
                    pending.Push(
                        (node is AsExpressionNode cast
                            ? cast.Expression!
                            : ((TypeAssertionNode)node).Expression!, InferenceSyntaxKind.Expression));
                continue;
            }
            if (node is ObjectLiteralExpressionNode { Properties: { } properties })
            {
                var errors = new List<SyntaxNode>();
                foreach (var property in properties)
                {
                    var name = SemanticSyntax.Name(property);
                    if ((property.Flags & NodeFlags.ThisNodeHasError) != 0
                        || property is ShorthandPropertyAssignmentNode or SpreadAssignmentNode || name is PrivateIdentifierNode)
                        errors.Add(property);
                    else if (name is not null && (name.Flags & NodeFlags.ThisNodeHasError) != 0)
                        errors.Add(name);
                    else if (name is ComputedPropertyNameNode { Expression: { } computed }
                        && !PrimitiveInferenceExpression(computed))
                        errors.Add(name);
                }
                if (errors.Count != 0)
                {
                    foreach (var error in errors)
                        state.Tracker.ReportInferenceFallback(error);
                    continue;
                }
                for (int i = properties.Count - 1; i >= 0; i--)
                {
                    var property = properties[i];
                    if (property is PropertyAssignmentNode assignment)
                        pending.Push((assignment.Initializer!, InferenceSyntaxKind.Expression));
                    else if (property is MethodDeclarationNode)
                        PushFunction(property);
                    else if (property is GetAccessorDeclarationNode or SetAccessorDeclarationNode)
                        pending.Push((property, InferenceSyntaxKind.Declaration));
                }
                continue;
            }
            if (node is ArrayLiteralExpressionNode array)
            {
                if (!ReusableConstArray(array))
                    state.Tracker.ReportInferenceFallback(array);
                else if (array.Elements!.FirstOrDefault(e => e is SpreadElementNode) is { } spread)
                    state.Tracker.ReportInferenceFallback(spread);
                else
                    for (int i = array.Elements!.Count - 1; i >= 0; i--)
                        pending.Push((array.Elements[i], InferenceSyntaxKind.Expression));
                continue;
            }
            if (node is FunctionExpressionNode or ArrowFunctionNode)
            {
                PushFunction(node);
                continue;
            }
            if (!PrimitiveInferenceExpression(node) && !(node is IdentifierNode { Text.Span: var matchedText } && matchedText.SequenceEqual("undefined"u8))
                && node.Kind != SyntaxKind.OmittedExpression && (node is not TemplateExpressionNode || ReusableConstArray(node)))
            {
                var target = node;
                if (node is TemplateExpressionNode)
                {
                    while (target.Parent is ParenthesizedExpressionNode || target.Parent is { } assertion && SemanticSyntax.ConstAssertion(assertion))
                        target = target.Parent;
                    if (target.Parent is VariableDeclarationNode or PropertyDeclarationNode) target = target.Parent;
                    else target = node;
                }
                state.Tracker.ReportInferenceFallback(!returnType && ConstantEvaluator.EntityName(node)
                    && QuerySyntax.Declaration(node.Parent) ? node.Parent! : target);
            }
        }

        void PushFunction(SyntaxNode function)
        {
            if (function is IFullSignatureNode { FullSignature: not null })
                return;
            pending.Push((function, InferenceSyntaxKind.Return));
            if (function is IFunctionSignature { Parameters: { } parameters })
                for (int i = parameters.Count - 1; i >= 0; i--)
                    pending.Push((parameters[i], InferenceSyntaxKind.Declaration));
        }
    }

    private static bool CouldReferToUndefined(SyntaxNode node) => node switch
    {
        ParenthesizedTypeNode parentheses => CouldReferToUndefined(parentheses.Type!),
        UnionTypeNode union => union.Types!.Any(CouldReferToUndefined),
        IntersectionTypeNode intersection => intersection.Types!.Any(CouldReferToUndefined),
        TypeReferenceNode or TypeQueryNode or IndexedAccessTypeNode or ConditionalTypeNode or ImportTypeNode
            or OptionalTypeNode or RestTypeNode or TypeOperatorNode or TypePredicateNode => true,
        _ => node.Kind == SyntaxKind.UndefinedKeyword
    };

    private static bool PrimitiveInferenceExpression(SyntaxNode node) => node is StringLiteralNode or NoSubstitutionTemplateLiteralNode
        or NumericLiteralNode or BigIntLiteralNode
        or PrefixUnaryExpressionNode
    {
        Operator: SyntaxKind.PlusToken or SyntaxKind.MinusToken, Operand: NumericLiteralNode
            or BigIntLiteralNode
    }
        || node.Kind is SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword or SyntaxKind.NullKeyword;

    private static bool PlainInferredExpression(SyntaxNode node)
    {
        while (node is ParenthesizedExpressionNode parentheses)
            node = parentheses.Expression!;
        return !PrimitiveInferenceExpression(node) && !(node is IdentifierNode { Text.Span: var matchedText2 } && matchedText2.SequenceEqual("undefined"u8) || node is ArrayLiteralExpressionNode || node is ObjectLiteralExpressionNode || node is FunctionExpressionNode || node is ArrowFunctionNode || node is AsExpressionNode || node is TypeAssertionNode || node is ClassExpressionNode || node is TemplateExpressionNode);
    }
}

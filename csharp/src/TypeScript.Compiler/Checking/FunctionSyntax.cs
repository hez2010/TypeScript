using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal static class FunctionSyntax
{
    internal static bool Contextual(SyntaxNode node) => node is FunctionExpressionNode or ArrowFunctionNode
        or MethodDeclarationNode { Parent: ObjectLiteralExpressionNode };

    internal static IEnumerable<ReturnStatementNode> Returns(SyntaxNode? body)
    {
        if (body is null)
            yield break;
        var pending = new Stack<SyntaxNode>();
        pending.Push(body);
        while (pending.TryPop(out var node))
        {
            if (node is ReturnStatementNode statement)
                yield return statement;
            else if (node.Kind is SyntaxKind.CaseBlock or SyntaxKind.Block or SyntaxKind.IfStatement or SyntaxKind.DoStatement
                or SyntaxKind.WhileStatement or SyntaxKind.ForStatement or SyntaxKind.ForInStatement or SyntaxKind.ForOfStatement
                or SyntaxKind.WithStatement or SyntaxKind.SwitchStatement or SyntaxKind.CaseClause or SyntaxKind.DefaultClause
                or SyntaxKind.LabeledStatement or SyntaxKind.TryStatement or SyntaxKind.CatchClause)
                for (int i = node.ChildCount - 1; i >= 0; i--)
                    pending.Push(node.GetChild(i));
        }
    }

    internal static bool SensitiveParameters(SyntaxNode node, CheckerSymbols? symbols = null) => node is IFunctionSignature { TypeParameters: null, Parameters: { } parameters }
        && (parameters.OfType<ParameterDeclarationNode>().Any(p => p.Type is null)
            || node is not ArrowFunctionNode
                && ((node.Flags | (symbols?.Binding(node)?.Get(node)?.Flags ?? 0)) & NodeFlags.ContainsThis) != 0
                && parameters.FirstOrDefault() is not ParameterDeclarationNode { Name: IdentifierNode { Text: "this" } });

    internal static bool Sensitive(SyntaxNode node, CheckerSymbols? symbols = null)
    {
        var pending = new Stack<SyntaxNode>();
        pending.Push(node);
        while (pending.TryPop(out var current))
        {
            switch (current)
            {
                case FunctionExpressionNode or ArrowFunctionNode or MethodDeclarationNode or FunctionDeclarationNode:
                    if (SensitiveParameters(current, symbols))
                        return true;
                    if (current is IFunctionSignature { TypeParameters: null, Type: null } && SemanticSyntax.Body(current) is { } body)
                    {
                        if (body is not BlockNode)
                            pending.Push(body);
                        else
                            foreach (var statement in Returns(body))
                                if (statement.Expression is { } expression)
                                    pending.Push(expression);
                    }
                    if (SemanticSyntax.Generator(current) && SemanticSyntax.Body(current) is { } generatorBody)
                        foreach (var expression in Yields(generatorBody))
                            pending.Push(expression);
                    break;
                case ObjectLiteralExpressionNode literal:
                    foreach (var property in literal.Properties!)
                        pending.Push(property);
                    break;
                case ArrayLiteralExpressionNode array:
                    foreach (var element in array.Elements!)
                        pending.Push(element);
                    break;
                case ConditionalExpressionNode conditional:
                    pending.Push(conditional.WhenFalse!);
                    pending.Push(conditional.WhenTrue!);
                    break;
                case BinaryExpressionNode binary when binary.OperatorToken!.Kind is SyntaxKind.BarBarToken
                    or SyntaxKind.QuestionQuestionToken:
                    pending.Push(binary.Right!);
                    pending.Push(binary.Left!);
                    break;
                case PropertyAssignmentNode property:
                    pending.Push(property.Initializer!);
                    break;
                case ParenthesizedExpressionNode parentheses:
                    pending.Push(parentheses.Expression!);
                    break;
                case JsxAttributesNode attributes:
                    foreach (var property in attributes.Properties!)
                        pending.Push(property);
                    if (attributes.Parent is JsxOpeningElementNode && attributes.Parent.Parent is JsxElementNode jsx)
                        foreach (var child in jsx.Children!)
                            pending.Push(child);
                    break;
                case JsxAttributeNode { Initializer: { } initializer }:
                    pending.Push(initializer);
                    break;
                case JsxExpressionNode { Expression: { } expression }:
                    pending.Push(expression);
                    break;
                case YieldExpressionNode { Expression: { } expression }:
                    pending.Push(expression);
                    break;
            }
        }
        return false;
    }

    internal static IEnumerable<YieldExpressionNode> Yields(SyntaxNode body)
    {
        var pending = new Stack<SyntaxNode>();
        pending.Push(body);
        while (pending.TryPop(out var node))
        {
            if (node is YieldExpressionNode expression)
                yield return expression;
            if (node != body && (node is IFunctionSignature || SemanticSyntax.ClassLike(node)))
                continue;
            for (int i = node.ChildCount - 1; i >= 0; i--)
                pending.Push(node.GetChild(i));
        }
    }
}

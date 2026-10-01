using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class EsDecoratorsTransformer
{
    private SyntaxNode This() => F.NewKeywordExpression(K.ThisKeyword);
    private SyntaxNode Null() => F.NewToken(K.NullKeyword);
    private SyntaxNode Bool(bool value) => F.NewKeywordExpression(value ? K.TrueKeyword : K.FalseKeyword);
    private StringLiteralNode String(Utf8String value) => F.NewStringLiteral(value, TokenFlags.None);
    private SyntaxNode Assign(SyntaxNode left, SyntaxNode right) => Context.Binary(left, K.EqualsToken, right);
    private SyntaxNode Access(SyntaxNode receiver, Utf8String name) => F.NewPropertyAccessExpression(receiver, null, F.NewIdentifier(name), NodeFlags.None);
    private PropertyAssignmentNode Property(Utf8String name, SyntaxNode value) => F.NewPropertyAssignment(null, F.NewIdentifier(name), null, null, value);
    private ArrayLiteralExpressionNode Array(IEnumerable<SyntaxNode> elements) => F.NewArrayLiteralExpression(new(elements.ToArray()), false);
    private ObjectLiteralExpressionNode Object(SyntaxNode[] properties) => F.NewObjectLiteralExpression(new(properties), false);
    private SyntaxNode Let(IdentifierNode name, SyntaxNode? initializer = null) => F.NewVariableStatement(null,
        F.NewVariableDeclarationList(new([F.NewVariableDeclaration(name, null, null, initializer)]), NodeFlags.Let));
    private SyntaxNode Run(SyntaxNode receiver, SyntaxNode initializers, SyntaxNode? value = null) => Context.HelperCall(EmitHelpers.RunInitializers, "__runInitializers"u8,
        value is null ? [receiver, initializers] : [receiver, initializers, value]);
    private SyntaxNode Decorate(SyntaxNode ctor, SyntaxNode descriptor, SyntaxNode decorators, SyntaxNode context, SyntaxNode initializers, SyntaxNode extras) =>
        Context.HelperCall(EmitHelpers.EsDecorate, "__esDecorate"u8, [ctor, descriptor, decorators, context, initializers, extras]);
    private ClassStaticBlockDeclarationNode StaticBlock(IEnumerable<SyntaxNode> statements, bool multiLine = true) => F.NewClassStaticBlockDeclaration(null, F.NewBlock(new(statements.ToArray()), multiLine));
    private SyntaxNode Arrow(SyntaxNode[] parameters, SyntaxNode body) => F.NewArrowFunction(null, null, new(parameters), null, null, F.NewToken(K.EqualsGreaterThanToken), body);
    private SyntaxNode Parameter(Utf8String name) => F.NewParameterDeclaration(null, null, F.NewIdentifier(name), null, null, null);
    private SyntaxNode Iife(IEnumerable<SyntaxNode> statements) => F.NewCallExpression(F.NewParenthesizedExpression(Arrow([], F.NewBlock(new(statements.ToArray()), true))), null, null, new([]), NodeFlags.None);
    private SyntaxNode Metadata(IdentifierNode name, IdentifierNode? super)
    {
        var symbol = Access(F.NewIdentifier("Symbol"u8), "metadata"u8);
        var parent = super is null ? Null() : Context.Binary(F.NewElementAccessExpression(super, null, symbol, NodeFlags.None), K.QuestionQuestionToken, Null());
        var create = Context.MethodCall(F.NewIdentifier("Object"u8), "create"u8, [parent]);
        var check = Context.Binary(Context.Binary(F.NewTypeOfExpression(F.NewIdentifier("Symbol"u8)), K.EqualsEqualsEqualsToken, String("function"u8)), K.AmpersandAmpersandToken, symbol);
        var conditional = F.NewConditionalExpression(check, F.NewToken(K.QuestionToken), create, F.NewToken(K.ColonToken), Context.VoidZero());
        return F.NewVariableStatement(null, F.NewVariableDeclarationList(new([F.NewVariableDeclaration(name, null, null, conditional)]), NodeFlags.Const));
    }
    private SyntaxNode SymbolMetadata(SyntaxNode target, IdentifierNode metadata)
    {
        var descriptor = Object([Property("enumerable"u8, Bool(true)), Property("configurable"u8, Bool(true)), Property("writable"u8, Bool(true)), Property("value"u8, metadata)]);
        var call = Context.MethodCall(F.NewIdentifier("Object"u8), "defineProperty"u8, [target, Access(F.NewIdentifier("Symbol"u8), "metadata"u8), descriptor]);
        var statement = F.NewIfStatement(metadata, F.NewExpressionStatement(call), null);
        Context.SetFlags(statement, EmitFlags.SingleLine);
        return statement;
    }
    private SyntaxNode ElementContext(SyntaxNode member, Utf8String kind, bool computed, SyntaxNode name, IdentifierNode metadata)
    {
        SyntaxNode MemberAccess() => computed ? F.NewElementAccessExpression(F.NewIdentifier("obj"u8), null, name, NodeFlags.None)
            : F.NewPropertyAccessExpression(F.NewIdentifier("obj"u8), null, name, NodeFlags.None);
        List<SyntaxNode> access = [Property("has"u8, Arrow([Parameter("obj"u8)], Context.Binary(!computed && name is IdentifierNode ? Context.StringLiteralFromNode(name) : name, K.InKeyword, F.NewIdentifier("obj"u8))))];
        if (member is PropertyDeclarationNode or GetAccessorDeclarationNode or MethodDeclarationNode)
            access.Add(Property("get"u8, Arrow([Parameter("obj"u8)], MemberAccess())));
        if (member is PropertyDeclarationNode or SetAccessorDeclarationNode)
            access.Add(Property("set"u8, Arrow([Parameter("obj"u8), Parameter("value"u8)], F.NewBlock(new([F.NewExpressionStatement(Assign(MemberAccess(), F.NewIdentifier("value"u8)))]), false))));
        return Object([Property("kind"u8, String(kind)), Property("name"u8, !computed && name is IdentifierNode or PrivateIdentifierNode ? Context.StringLiteralFromNode(name) : name),
            Property("static"u8, Bool(Static(member))), Property("private"u8, Bool(member.DeclarationName is PrivateIdentifierNode)), Property("access"u8, Object(access.ToArray())), Property("metadata"u8, metadata)]);
    }
    private async ValueTask<List<SyntaxNode>> DecoratorsAsync(SyntaxNode node)
    {
        List<SyntaxNode> result = [];
        foreach (var decorator in node.ModifierList?.OfType<DecoratorNode>() ?? [])
        {
            var expression = (await VisitAsync(decorator.Expression))!;
            Context.SetFlags(expression, EmitFlags.NoComments);
            var inner = NamedEvaluation.SkipOuter(expression);
            if (inner is PropertyAccessExpressionNode or ElementAccessExpressionNode)
            {
                SyntaxNode target = inner;
                SyntaxNode receiver = inner is PropertyAccessExpressionNode property ? property.Expression! : ((ElementAccessExpressionNode)inner).Expression!;
                if (receiver.Kind == K.SuperKeyword) receiver = This();
                else if (TransformSyntax.SkipParentheses(receiver) is not (NumericLiteralNode or BigIntLiteralNode or StringLiteralNode) && receiver.Kind != K.ThisKeyword)
                {
                    var temp = Temp();
                    var assignment = EmitContext.CopyRange(Assign(temp, receiver), receiver);
                    target = inner is PropertyAccessExpressionNode p ? F.NewPropertyAccessExpression(assignment, null, p.Name, NodeFlags.None)
                        : F.NewElementAccessExpression(assignment, null, ((ElementAccessExpressionNode)inner).ArgumentExpression, NodeFlags.None);
                    EmitContext.CopyRange(target, inner);
                    receiver = temp;
                }
                expression = named.RestoreOuter(expression, Context.MethodCall(target, "bind"u8, [receiver]));
            }
            result.Add(expression);
        }
        return result;
    }
    private SyntaxNode? Prepend(List<SyntaxNode> expressions, SyntaxNode? expression)
    {
        if (expressions.Count == 0) return expression;
        SyntaxNode result;
        if (expression is ParenthesizedExpressionNode parentheses)
        {
            var updated = Context.Clone(parentheses);
            updated.Expression = Context.InlineExpressions([.. expressions, parentheses.Expression!]);
            result = updated;
        }
        else result = Context.InlineExpressions(expression is null ? expressions.ToArray() : [.. expressions, expression])!;
        expressions.Clear();
        return result;
    }
    private async ValueTask<SyntaxNode> ComputedNameAsync(ComputedPropertyNameNode node)
    {
        var expression = (await VisitAsync(node.Expression))!;
        if (!Inlineable(expression)) expression = Prepend(pending, expression)!;
        if (expression == node.Expression) return node;
        var result = Context.Clone(node);
        result.Expression = expression;
        return result;
    }
    private async ValueTask<(SyntaxNode Reference, SyntaxNode Name)> ReferencedNameAsync(SyntaxNode node)
    {
        if (node is not ComputedPropertyNameNode computed) return (Context.StringLiteralFromNode(node), (await VisitAsync(node))!);
        if (computed.Expression is StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode)
            return (Context.StringLiteralFromNode(computed.Expression), await ComputedNameAsync(computed));
        var reference = Context.NewGeneratedNameForNode(node);
        Context.AddVariableDeclaration(reference);
        var key = Context.HelperCall(EmitHelpers.PropKey, "__propKey"u8, [(await VisitAsync(computed.Expression))!]);
        var result = Context.Clone(computed);
        result.Expression = Prepend(pending, Assign(reference, key));
        return (reference, result);
    }
    private static bool Inlineable(SyntaxNode node) => NamedEvaluation.SkipOuter(node) is NumericLiteralNode or BigIntLiteralNode or StringLiteralNode or NoSubstitutionTemplateLiteralNode
        || NamedEvaluation.SkipOuter(node).Kind is K.TrueKeyword or K.FalseKeyword or K.NullKeyword or K.ThisKeyword or K.SuperKeyword;
}

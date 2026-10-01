using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed class SuperAccessState(EmitContext context, CancellationToken cancellation) : SyntaxRewriter(context, cancellation)
{
    internal readonly List<Utf8String> Properties = [];
    private readonly HashSet<Utf8String> propertyNames = [];
    internal bool ElementAccess, Assignment;
    internal readonly IdentifierNode Binding = context.NewUniqueName("_super"u8, new(GeneratedIdentifierFlags.Optimistic | GeneratedIdentifierFlags.FileLevel));
    internal readonly IdentifierNode IndexBinding = context.NewUniqueName("_superIndex"u8, new(GeneratedIdentifierFlags.Optimistic | GeneratedIdentifierFlags.FileLevel));
    private NodeFactory F => Context.Factory;
    internal bool HasAccess => Properties.Count != 0 || ElementAccess;
    internal static bool IsSuperProperty(SyntaxNode? node) => node is PropertyAccessExpressionNode { Expression.Kind: K.SuperKeyword }
        or ElementAccessExpressionNode { Expression.Kind: K.SuperKeyword };

    internal void Track(SyntaxNode node)
    {
        switch (node)
        {
            case PropertyAccessExpressionNode { Expression.Kind: K.SuperKeyword, Name: IdentifierNode name }:
                if (propertyNames.Add(name.Text)) Properties.Add(name.Text);
                break;
            case ElementAccessExpressionNode { Expression.Kind: K.SuperKeyword }: ElementAccess = true; break;
            case BinaryExpressionNode { OperatorToken.Kind: >= K.FirstAssignment and <= K.LastAssignment, Left: { } target }:
                Assignment |= ContainsSuperTarget(target); break;
            case PrefixUnaryExpressionNode { Operator: K.PlusPlusToken or K.MinusMinusToken, Operand: { } target }:
                Assignment |= ContainsSuperTarget(target); break;
            case PostfixUnaryExpressionNode { Operator: K.PlusPlusToken or K.MinusMinusToken, Operand: { } target }:
                Assignment |= ContainsSuperTarget(target); break;
        }
    }

    private static bool ContainsSuperTarget(SyntaxNode root)
    {
        Stack<SyntaxNode> pending = new([root]);
        while (pending.TryPop(out var node))
        {
            if (IsSuperProperty(node)) return true;
            switch (node)
            {
                case ParenthesizedExpressionNode n: pending.Push(n.Expression!); break;
                case ArrayLiteralExpressionNode n: foreach (var child in n.Elements ?? new([])) pending.Push(child); break;
                case ObjectLiteralExpressionNode n:
                    foreach (var property in n.Properties ?? new([]))
                    {
                        var target = property switch { PropertyAssignmentNode p => p.Initializer, ShorthandPropertyAssignmentNode p => p.Name, SpreadAssignmentNode p => p.Expression, _ => null };
                        if (target is not null) pending.Push(target);
                    }
                    break;
                case SpreadElementNode n: pending.Push(n.Expression!); break;
            }
        }
        return false;
    }

    internal ValueTask<NodeList?> ParametersAsync(NodeList? nodes) => VisitListAsync(nodes);
    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        switch (node)
        {
            case CallExpressionNode n when IsSuperProperty(n.Expression):
                var target = n.Expression is PropertyAccessExpressionNode property ? Property(Binding, property.Name!) : Index(((ElementAccessExpressionNode)n.Expression!).ArgumentExpression!);
                return EmitContext.CopyRange(Context.MethodCall(target, "call"u8, [F.NewKeywordExpression(K.ThisKeyword), .. await VisitListAsync(n.Arguments) ?? new([])]), n);
            case PropertyAccessExpressionNode { Expression.Kind: K.SuperKeyword } n: return Property(Binding, n.Name!);
            case ElementAccessExpressionNode { Expression.Kind: K.SuperKeyword } n: return Index(n.ArgumentExpression!);
            case FunctionDeclarationNode or FunctionExpressionNode or MethodDeclarationNode or GetAccessorDeclarationNode
                or SetAccessorDeclarationNode or ConstructorDeclarationNode or ClassDeclarationNode or ClassExpressionNode: return node;
            default: return await VisitEachChildAsync(node);
        }
    }

    private SyntaxNode Index(SyntaxNode expression)
    {
        var call = F.NewCallExpression(IndexBinding, null, null, new([expression]), NodeFlags.None);
        return Assignment ? Property(call, F.NewIdentifier("value"u8)) : call;
    }
    private PropertyAccessExpressionNode Property(SyntaxNode expression, SyntaxNode name) => F.NewPropertyAccessExpression(expression, null, name, NodeFlags.None);

    internal VariableStatementNode Declaration()
    {
        List<SyntaxNode> accessors = [];
        foreach (var name in Properties)
        {
            var getter = F.NewArrowFunction(null, null, new([]), null, null, F.NewToken(K.EqualsGreaterThanToken), Property(F.NewKeywordExpression(K.SuperKeyword), F.NewIdentifier(name)));
            List<SyntaxNode> descriptor = [F.NewPropertyAssignment(null, F.NewIdentifier("get"u8), null, null, getter)];
            if (Assignment)
            {
                var parameter = F.NewParameterDeclaration(null, null, F.NewIdentifier("v"u8), null, null, null);
                var setter = F.NewArrowFunction(null, null, new([parameter]), null, null, F.NewToken(K.EqualsGreaterThanToken),
                    Context.Binary(Property(F.NewKeywordExpression(K.SuperKeyword), F.NewIdentifier(name)), K.EqualsToken, F.NewIdentifier("v"u8)));
                descriptor.Add(F.NewPropertyAssignment(null, F.NewIdentifier("set"u8), null, null, setter));
            }
            accessors.Add(F.NewPropertyAssignment(null, F.NewIdentifier(name), null, null, F.NewObjectLiteralExpression(new(descriptor.ToArray()), false)));
        }
        var initializer = Context.MethodCall(F.NewIdentifier("Object"u8), "create"u8, [F.NewKeywordExpression(K.NullKeyword), F.NewObjectLiteralExpression(new(accessors.ToArray()), true)]);
        return F.NewVariableStatement(null, F.NewVariableDeclarationList(new([F.NewVariableDeclaration(Binding, null, null, initializer)]), NodeFlags.Const));
    }

    internal void AddHelper(BlockNode block)
    {
        if (ElementAccess) Context.AddHelper(block, Assignment ? EmitHelpers.AdvancedAsyncSuper : EmitHelpers.AsyncSuper);
    }
}

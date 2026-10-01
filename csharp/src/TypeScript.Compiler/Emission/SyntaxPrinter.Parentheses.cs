using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

public sealed partial class SyntaxPrinter
{
    private enum Precedence
    {
        Comma, Spread, Yield, Assignment, Conditional, LogicalOr, LogicalAnd, BitwiseOr, BitwiseXor,
        BitwiseAnd, Equality, Relational, Shift, Additive, Multiplicative, Exponentiation, Unary,
        Update, LeftHandSide, OptionalChain, Member, Primary, Parentheses
    }

    private static SyntaxNode SkipPartial(SyntaxNode node)
    {
        while (node is PartiallyEmittedExpressionNode { Expression: { } expression })
            node = expression;
        return node;
    }

    private static SyntaxNode Leftmost(SyntaxNode node)
    {
        while (true)
        {
            var left = node switch
            {
                PostfixUnaryExpressionNode n => n.Operand,
                BinaryExpressionNode n => n.Left,
                ConditionalExpressionNode n => n.Condition,
                TaggedTemplateExpressionNode n => n.Tag,
                CallExpressionNode n => n.Expression,
                AsExpressionNode n => n.Expression,
                SatisfiesExpressionNode n => n.Expression,
                PropertyAccessExpressionNode n => n.Expression,
                ElementAccessExpressionNode n => n.Expression,
                NonNullExpressionNode n => n.Expression,
                PartiallyEmittedExpressionNode n => n.Expression,
                _ => null
            };
            if (left is null)
                return node;
            node = left;
        }
    }

    private static Precedence BinaryPrecedence(K token) => token switch
    {
        K.CommaToken => Precedence.Comma,
        >= K.FirstAssignment and <= K.LastAssignment => Precedence.Assignment,
        K.QuestionQuestionToken or K.BarBarToken => Precedence.LogicalOr,
        K.AmpersandAmpersandToken => Precedence.LogicalAnd,
        K.BarToken => Precedence.BitwiseOr,
        K.CaretToken => Precedence.BitwiseXor,
        K.AmpersandToken => Precedence.BitwiseAnd,
        K.EqualsEqualsToken or K.ExclamationEqualsToken or K.EqualsEqualsEqualsToken or K.ExclamationEqualsEqualsToken => Precedence.Equality,
        K.LessThanToken or K.GreaterThanToken or K.LessThanEqualsToken or K.GreaterThanEqualsToken
            or K.InstanceOfKeyword or K.InKeyword or K.AsKeyword or K.SatisfiesKeyword => Precedence.Relational,
        K.LessThanLessThanToken or K.GreaterThanGreaterThanToken or K.GreaterThanGreaterThanGreaterThanToken => Precedence.Shift,
        K.PlusToken or K.MinusToken => Precedence.Additive,
        K.AsteriskToken or K.SlashToken or K.PercentToken => Precedence.Multiplicative,
        K.AsteriskAsteriskToken => Precedence.Exponentiation,
        _ => Precedence.Comma
    };

    private static Precedence ExpressionPrecedence(SyntaxNode node)
    {
        node = SkipPartial(node);
        return node switch
        {
            SpreadElementNode => Precedence.Spread,
            YieldExpressionNode => Precedence.Yield,
            ArrowFunctionNode => Precedence.Assignment,
            ConditionalExpressionNode => Precedence.Conditional,
            BinaryExpressionNode binary => BinaryPrecedence(binary.OperatorToken!.Kind),
            TypeAssertionNode or NonNullExpressionNode or PrefixUnaryExpressionNode or TypeOfExpressionNode
                or VoidExpressionNode or DeleteExpressionNode or AwaitExpressionNode => Precedence.Unary,
            PostfixUnaryExpressionNode => Precedence.Update,
            PropertyAccessExpressionNode or ElementAccessExpressionNode or CallExpressionNode =>
                (node.Flags & NodeFlags.OptionalChain) != 0 ? Precedence.OptionalChain : Precedence.Member,
            NewExpressionNode { Arguments: null } => Precedence.LeftHandSide,
            NewExpressionNode or TaggedTemplateExpressionNode or MetaPropertyNode or ExpressionWithTypeArgumentsNode => Precedence.Member,
            AsExpressionNode or SatisfiesExpressionNode => Precedence.Relational,
            ParenthesizedExpressionNode => Precedence.Parentheses,
            _ => Precedence.Primary
        };
    }

    private bool NeedsParentheses(SyntaxNode parent, SyntaxNode child)
    {
        var expression = SkipPartial(child);
        Precedence minimum = Precedence.Comma;
        switch (parent)
        {
            case PropertyAccessExpressionNode n when n.Expression == child:
                minimum = (n.Flags & NodeFlags.OptionalChain) != 0 ? Precedence.OptionalChain : Precedence.Member;
                break;
            case ElementAccessExpressionNode n when n.Expression == child:
                minimum = (n.Flags & NodeFlags.OptionalChain) != 0 ? Precedence.OptionalChain : Precedence.Member;
                break;
            case CallExpressionNode n when n.Expression == child:
                if (expression is NewExpressionNode { Arguments: null })
                    return true;
                if (expression is FunctionExpressionNode or ArrowFunctionNode && states.Skip(1).FirstOrDefault()?.Node is ExpressionStatementNode)
                    return true;
                minimum = (n.Flags & NodeFlags.OptionalChain) != 0 ? Precedence.OptionalChain : Precedence.Member;
                break;
            case NewExpressionNode n when n.Expression == child:
                if (expression is CallExpressionNode)
                    return true;
                minimum = Precedence.Member;
                break;
            case TaggedTemplateExpressionNode n when n.Tag == child:
                minimum = (n.Flags & NodeFlags.OptionalChain) != 0 ? Precedence.OptionalChain : Precedence.Member;
                break;
            case BinaryExpressionNode n when n.Left == child || n.Right == child:
                var op = n.OperatorToken!.Kind;
                bool right = n.Right == child;
                minimum = BinaryPrecedence(op);
                if (expression is BinaryExpressionNode inner && (op == K.QuestionQuestionToken && inner.OperatorToken?.Kind is K.BarBarToken or K.AmpersandAmpersandToken
                    || op is K.BarBarToken or K.AmpersandAmpersandToken && inner.OperatorToken?.Kind == K.QuestionQuestionToken))
                    return true;
                if (minimum == Precedence.Assignment)
                    minimum = right ? Precedence.Yield : Precedence.Conditional;
                else if (minimum == Precedence.Exponentiation)
                    minimum = right ? Precedence.Exponentiation : Precedence.Update;
                else if (right && minimum != Precedence.Comma)
                    minimum++;
                break;
            case ConditionalExpressionNode n:
                if (n.Condition == child)
                    minimum = Precedence.LogicalOr;
                else if (n.WhenTrue == child || n.WhenFalse == child)
                    minimum = Precedence.Yield;
                break;
            case PrefixUnaryExpressionNode or DeleteExpressionNode or VoidExpressionNode or TypeOfExpressionNode or AwaitExpressionNode:
                minimum = Precedence.Unary;
                break;
            case PostfixUnaryExpressionNode:
                minimum = Precedence.LeftHandSide;
                break;
            case TypeAssertionNode n when n.Expression == child:
                minimum = Precedence.Update;
                break;
            case NonNullExpressionNode:
            case ExpressionWithTypeArgumentsNode n when n.Expression == child:
                minimum = Precedence.Member;
                break;
            case AsExpressionNode n when n.Expression == child:
            case SatisfiesExpressionNode satisfies when satisfies.Expression == child:
                minimum = Precedence.Relational;
                break;
            case ComputedPropertyNameNode or SpreadElementNode or SpreadAssignmentNode or JsxExpressionNode:
                minimum = Precedence.Yield;
                break;
            case DecoratorNode:
                minimum = Precedence.Member;
                break;
            case YieldExpressionNode n when n.Expression == child:
                minimum = Precedence.Yield;
                break;
            case ArrowFunctionNode n when n.Body == child && child is not BlockNode:
                if (Leftmost(expression) is ObjectLiteralExpressionNode)
                    return true;
                minimum = Precedence.Yield;
                break;
            case ExpressionStatementNode when sourceFile?.ScriptKind != ScriptKind.JSON:
                if (expression is CallExpressionNode call && SkipPartial(call.Expression!) is FunctionExpressionNode or ArrowFunctionNode)
                    break;
                return Leftmost(expression) is FunctionExpressionNode or ObjectLiteralExpressionNode;
            case ExportAssignmentNode n when n.Expression == child:
                if (!n.IsExportEquals && Leftmost(expression) is ClassExpressionNode or FunctionExpressionNode)
                    return true;
                minimum = Precedence.Assignment;
                break;
            case CallExpressionNode n when n.Arguments?.Contains(child) == true:
            case NewExpressionNode creation when creation.Arguments?.Contains(child) == true:
            case ArrayLiteralExpressionNode:
                minimum = Precedence.Spread;
                break;
            case ForStatementNode n when n.Initializer == child:
                minimum = Precedence.Comma;
                break;
            case IInitializedNode n when n.Initializer == child:
            case ShorthandPropertyAssignmentNode shorthand when shorthand.ObjectAssignmentInitializer == child:
                minimum = Precedence.Yield;
                break;
            default:
                return TypeNeedsParentheses(parent, child);
        }
        return ExpressionPrecedence(expression) < minimum;
    }

    private static int TypePrecedence(SyntaxNode node) => node switch
    {
        ConditionalTypeNode => 0,
        JSDocOptionalTypeNode or JSDocVariadicTypeNode => 1,
        FunctionTypeNode or ConstructorTypeNode => 2,
        UnionTypeNode => 3,
        IntersectionTypeNode => 4,
        InferTypeNode { TypeParameter.Constraint: not null } => 2,
        TypeOperatorNode or InferTypeNode or TypeQueryNode => 5,
        IndexedAccessTypeNode or ArrayTypeNode or OptionalTypeNode => 6,
        _ => 7
    };

    private bool TypeNeedsParentheses(SyntaxNode parent, SyntaxNode child)
    {
        int minimum = parent switch
        {
            ArrayTypeNode or OptionalTypeNode => 6,
            IndexedAccessTypeNode n when n.ObjectType == child => 6,
            UnionTypeNode or IntersectionTypeNode => 5,
            TypeOperatorNode n when n.Type == child => n.Operator == K.ReadonlyKeyword ? 6 : 5,
            ConditionalTypeNode n when n.CheckType == child => 3,
            ConditionalTypeNode n when n.ExtendsType == child => 2,
            TypeParameterDeclarationNode n when n.Constraint == child && states.Skip(1).FirstOrDefault()?.Node is InferTypeNode => 2,
            _ => 0
        };
        if (minimum == 6 && parent is ArrayTypeNode or OptionalTypeNode or IndexedAccessTypeNode && child is TypeQueryNode
            && (context.MostOriginal(parent).Flags & NodeFlags.Synthesized) == 0)
            minimum = 5;
        if (InExtendsForChild(parent, child))
        {
            minimum = Math.Max(minimum, 2);
            if (parent is FunctionTypeNode or ConstructorTypeNode && child is InferTypeNode { TypeParameter.Constraint: not null })
                minimum = 7;
        }
        return TypePrecedence(child) < minimum;
    }

    private bool InExtendsForChild(SyntaxNode parent, SyntaxNode child) => parent switch
    {
        ConditionalTypeNode n => n.ExtendsType == child,
        TypeParameterDeclarationNode n when n.Constraint == child && states.Skip(1).FirstOrDefault()?.Node is InferTypeNode => true,
        FunctionTypeNode n when n.Type == child => states.Peek().InExtends,
        ConstructorTypeNode n when n.Type == child => states.Peek().InExtends,
        UnionTypeNode or IntersectionTypeNode or TypeOperatorNode or ArrayTypeNode or OptionalTypeNode => states.Peek().InExtends,
        IndexedAccessTypeNode n when n.ObjectType == child => states.Peek().InExtends,
        _ => false
    };
}

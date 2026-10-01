using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed class NamedEvaluation(EmitContext context)
{
    private NodeFactory F => context.Factory;

    internal bool IsNameBlock(SyntaxNode node) => node is ClassStaticBlockDeclarationNode { Body: BlockNode { Statements.Count: 1 } body }
        && body.Statements[0] is ExpressionStatementNode { Expression: CallExpressionNode { Expression: IdentifierNode name, Arguments.Count: >= 2 } call }
        && name.Text == "__setFunctionName"u8 && (context.GetFlags(name) & EmitFlags.HelperName) != 0
        && call.Arguments[1] == context.GetAssignedName(node);

    internal bool HasAssignedName(SyntaxNode node) => context.GetAssignedName(node) is not null
        && Members(node)?.Any(IsNameBlock) == true;

    internal bool IsAnonymous(SyntaxNode? node)
    {
        if (node is null) return false;
        node = SkipOuter(node);
        return node is ArrowFunctionNode or FunctionExpressionNode { Name: null }
            || node is ClassExpressionNode { Name: null } && !HasAssignedName(node);
    }

    internal bool Applies(SyntaxNode node) => node switch
    {
        PropertyAssignmentNode n => !((n.Name is IdentifierNode id && id.Text == "__proto__"u8)
            || (n.Name is StringLiteralNode str && str.Text == "__proto__"u8)) && IsAnonymous(n.Initializer),
        ShorthandPropertyAssignmentNode n => IsAnonymous(n.ObjectAssignmentInitializer),
        VariableDeclarationNode { Name: IdentifierNode } n => IsAnonymous(n.Initializer),
        ParameterDeclarationNode { Name: IdentifierNode, DotDotDotToken: null } n => IsAnonymous(n.Initializer),
        BindingElementNode { Name: IdentifierNode, DotDotDotToken: null } n => IsAnonymous(n.Initializer),
        PropertyDeclarationNode n => IsAnonymous(n.Initializer),
        BinaryExpressionNode { Left: IdentifierNode, OperatorToken.Kind: K.EqualsToken or K.AmpersandAmpersandEqualsToken or K.BarBarEqualsToken or K.QuestionQuestionEqualsToken } n => IsAnonymous(n.Right),
        ExportAssignmentNode n => IsAnonymous(n.Expression),
        _ => false
    };

    internal SyntaxNode Transform(SyntaxNode node, bool ignoreEmpty = false, Utf8String assignedText = default)
    {
        var expression = node switch
        {
            ShorthandPropertyAssignmentNode n => n.ObjectAssignmentInitializer,
            BinaryExpressionNode n => n.Right,
            ExportAssignmentNode n => n.Expression,
            IInitializedNode n => n.Initializer,
            _ => throw new InvalidOperationException("Expected a named evaluation source")
        };
        SyntaxNode? name = node is BinaryExpressionNode binary ? binary.Left : node.DeclarationName;
        SyntaxNode assignedName;
        if (node is PropertyAssignmentNode or PropertyDeclarationNode)
            (assignedName, name) = PropertyName(name!, assignedText);
        else if (assignedText.Length != 0) assignedName = F.NewStringLiteral(assignedText, TokenFlags.None);
        else if (node is ExportAssignmentNode export) assignedName = F.NewStringLiteral(export.IsExportEquals ? ""u8 : "default"u8, TokenFlags.None);
        else
        {
            var original = context.MostOriginal(SkipOuter(expression!));
            assignedName = original is ClassDeclarationNode or FunctionDeclarationNode && original.DeclarationName is null && SemanticSyntax.HasModifier(original, K.DefaultKeyword)
                ? F.NewStringLiteral("default"u8, TokenFlags.None) : context.StringLiteralFromNode(name!);
        }
        var updated = Finish(expression!, assignedName, ignoreEmpty);
        var result = context.Clone(node);
        if (result is IModifiedNode modified && result is not PropertyDeclarationNode) modified.Modifiers = null;
        if (result is ITypedNode typed) typed.Type = null;
        if (result is IInitializedNode initialized) initialized.Initializer = updated;
        switch (result)
        {
            case PropertyAssignmentNode n: n.Name = name; n.PostfixToken = null; break;
            case PropertyDeclarationNode n: n.Name = name; n.PostfixToken = null; break;
            case ShorthandPropertyAssignmentNode n: n.ObjectAssignmentInitializer = updated; n.PostfixToken = null; break;
            case VariableDeclarationNode n: n.ExclamationToken = null; break;
            case ParameterDeclarationNode n: n.QuestionToken = null; break;
            case BinaryExpressionNode n: n.Right = updated; break;
            case ExportAssignmentNode n: n.Expression = updated; break;
        }
        return result;
    }

    internal (SyntaxNode AssignedName, SyntaxNode Name) PropertyName(SyntaxNode name, Utf8String assignedText = default)
    {
        if (assignedText.Length != 0) return (F.NewStringLiteral(assignedText, TokenFlags.None), name);
        if (name is IdentifierNode or PrivateIdentifierNode or StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode)
            return (context.StringLiteralFromNode(name), name);
        var computed = (ComputedPropertyNameNode)name;
        if (computed.Expression is StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode)
            return (context.StringLiteralFromNode(computed.Expression), name);
        var temporary = context.NewGeneratedNameForNode(name);
        context.AddVariableDeclaration(temporary);
        var updated = context.Clone(computed);
        updated.Expression = context.Binary(temporary, K.EqualsToken, context.HelperCall(EmitHelpers.PropKey, "__propKey"u8, [computed.Expression!]));
        return (temporary, updated);
    }

    internal SyntaxNode Finish(SyntaxNode expression, SyntaxNode assignedName, bool ignoreEmpty = false)
    {
        if (ignoreEmpty && assignedName is StringLiteralNode { Text.Length: 0 }) return expression;
        var inner = SkipOuter(expression);
        var updated = inner is ClassExpressionNode ? InjectClassName(inner, assignedName)
            : context.HelperCall(EmitHelpers.SetFunctionName, "__setFunctionName"u8, [inner, assignedName]);
        return RestoreOuter(expression, updated);
    }

    internal ClassStaticBlockDeclarationNode NameBlock(SyntaxNode assignedName, SyntaxNode? thisExpression = null)
    {
        var call = context.HelperCall(EmitHelpers.SetFunctionName, "__setFunctionName"u8, [thisExpression ?? F.NewKeywordExpression(K.ThisKeyword), assignedName]);
        var block = F.NewClassStaticBlockDeclaration(null, F.NewBlock(new([F.NewExpressionStatement(call)]), false));
        context.SetAssignedName(block, assignedName);
        return block;
    }

    internal SyntaxNode InjectClassName(SyntaxNode node, SyntaxNode assignedName, SyntaxNode? thisExpression = null)
    {
        if (HasAssignedName(node)) return node;
        var nameBlock = NameBlock(assignedName, thisExpression);
        if (node.DeclarationName is { } name)
            context.SetSourceMapRange(((BlockNode)nameBlock.Body!).Statements![0], new(name.Pos, name.End));
        var members = Members(node) ?? new([]);
        int index = 0;
        for (int i = 0; i < members.Count; i++) if (IsClassThisBlock(members[i])) { index = i + 1; break; }
        var list = new NodeList([.. members.Take(index), nameBlock, .. members.Skip(index)], members.Pos, members.End);
        var result = context.Clone(node);
        if (result is ClassDeclarationNode declaration) declaration.Members = list;
        else ((ClassExpressionNode)result).Members = list;
        context.SetAssignedName(result, assignedName);
        if (context.GetClassThis(node) is { } classThis) context.SetClassThis(result, classThis);
        return result;
    }

    internal bool IsClassThisBlock(SyntaxNode node) => node is ClassStaticBlockDeclarationNode { Body: BlockNode { Statements.Count: 1 } body }
        && body.Statements[0] is ExpressionStatementNode { Expression: BinaryExpressionNode { Left: IdentifierNode left, OperatorToken.Kind: K.EqualsToken, Right.Kind: K.ThisKeyword } }
        && context.GetClassThis(node) == left;
    private static NodeList? Members(SyntaxNode node) => node switch { ClassDeclarationNode n => n.Members, ClassExpressionNode n => n.Members, _ => null };

    internal static SyntaxNode? OuterExpression(SyntaxNode node) => node switch
    {
        ParenthesizedExpressionNode n => n.Expression, PartiallyEmittedExpressionNode n => n.Expression,
        AsExpressionNode n => n.Expression, TypeAssertionNode n => n.Expression,
        SatisfiesExpressionNode n => n.Expression, NonNullExpressionNode n => n.Expression, _ => null
    };
    internal static SyntaxNode SkipOuter(SyntaxNode node)
    {
        while (OuterExpression(node) is { } inner) node = inner;
        return node;
    }
    internal SyntaxNode RestoreOuter(SyntaxNode original, SyntaxNode inner)
    {
        Stack<SyntaxNode> wrappers = [];
        while (OuterExpression(original) is { } next) { wrappers.Push(original); original = next; }
        while (wrappers.TryPop(out var wrapper))
        {
            var updated = context.Clone(wrapper);
            switch (updated)
            {
                case ParenthesizedExpressionNode n: n.Expression = inner; break;
                case PartiallyEmittedExpressionNode n: n.Expression = inner; break;
                case AsExpressionNode n: n.Expression = inner; break;
                case TypeAssertionNode n: n.Expression = inner; break;
                case SatisfiesExpressionNode n: n.Expression = inner; break;
                case NonNullExpressionNode n: n.Expression = inner; break;
            }
            inner = updated;
        }
        return inner;
    }
}

public sealed partial class EmitContext
{
    internal CallExpressionNode HelperCall(EmitHelper helper, Utf8String name, SyntaxNode[] arguments)
    {
        RequestHelper(helper);
        var identifier = Factory.NewIdentifier(name);
        AddFlags(identifier, EmitFlags.HelperName);
        return Factory.NewCallExpression(identifier, null, null, new(arguments), NodeFlags.None);
    }

    internal StringLiteralNode StringLiteralFromNode(SyntaxNode node)
    {
        var text = node switch
        {
            IdentifierNode n => n.Text, PrivateIdentifierNode n => n.Text, StringLiteralNode n => n.Text,
            NumericLiteralNode n => n.Text, BigIntLiteralNode n => n.Text, NoSubstitutionTemplateLiteralNode n => n.Text,
            RegularExpressionLiteralNode n => n.Text, _ => Utf8String.Empty
        };
        var literal = Factory.NewStringLiteral(text, TokenFlags.None);
        SetTextSource(literal, node);
        return literal;
    }
}

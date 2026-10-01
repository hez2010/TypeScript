using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal static class TransformSyntax
{
    internal static TypeScript.Compiler.Text.Utf8String SourceText(SourceFileNode source, SyntaxNode node)
    {
        var scanner = new Scanner(source.Source);
        scanner.ResetPosition(node.Pos);
        scanner.Scan();
        return source.Source.Text[scanner.TokenStart..node.End];
    }
    internal static SyntaxNode SkipParentheses(SyntaxNode node)
    {
        while (node is ParenthesizedExpressionNode parentheses) node = parentheses.Expression!;
        return node;
    }
    internal static SyntaxNode SkipPartials(SyntaxNode node)
    {
        while (node is PartiallyEmittedExpressionNode partial) node = partial.Expression!;
        return node;
    }
    internal static SyntaxNode? Expression(SyntaxNode node) => node switch
    {
        CallExpressionNode n => n.Expression, PropertyAccessExpressionNode n => n.Expression,
        ElementAccessExpressionNode n => n.Expression, ParenthesizedExpressionNode n => n.Expression,
        PartiallyEmittedExpressionNode n => n.Expression, TaggedTemplateExpressionNode n => n.Tag,
        _ => null
    };
    internal static bool IsIdentifierReference(IdentifierNode name, SyntaxNode? parent) => parent switch
    {
        null => false,
        { Kind: K.BinaryExpression or K.PrefixUnaryExpression or K.PostfixUnaryExpression or K.YieldExpression
            or K.AsExpression or K.SatisfiesExpression or K.ElementAccessExpression or K.NonNullExpression
            or K.SpreadElement or K.SpreadAssignment or K.ParenthesizedExpression or K.ArrayLiteralExpression
            or K.DeleteExpression or K.TypeOfExpression or K.VoidExpression or K.AwaitExpression
            or K.TypeAssertionExpression or K.ExpressionWithTypeArguments or K.JsxSelfClosingElement
            or K.JsxSpreadAttribute or K.JsxExpression or K.PartiallyEmittedExpression } => true,
        ComputedPropertyNameNode n => n.Expression == name,
        DecoratorNode n => n.Expression == name,
        IfStatementNode n => n.Expression == name,
        DoStatementNode n => n.Expression == name,
        WhileStatementNode n => n.Expression == name,
        WithStatementNode n => n.Expression == name,
        ReturnStatementNode n => n.Expression == name,
        SwitchStatementNode n => n.Expression == name,
        CaseOrDefaultClauseNode n => n.Expression == name,
        ThrowStatementNode n => n.Expression == name,
        ExpressionStatementNode n => n.Expression == name,
        ExportAssignmentNode n => n.Expression == name,
        PropertyAccessExpressionNode n => n.Expression == name,
        TemplateSpanNode n => n.Expression == name,
        ShorthandPropertyAssignmentNode n => n.ObjectAssignmentInitializer == name,
        ForStatementNode n => n.Initializer == name || n.Condition == name || n.Incrementor == name,
        ForInOrOfStatementNode n => n.Initializer == name || n.Expression == name,
        IInitializedNode n => n.Initializer == name,
        ImportEqualsDeclarationNode n => n.ModuleReference == name,
        ArrowFunctionNode n => n.Body == name,
        ConditionalExpressionNode n => n.Condition == name || n.WhenTrue == name || n.WhenFalse == name,
        CallExpressionNode n => n.Expression == name || n.Arguments?.Contains(name) == true,
        NewExpressionNode n => n.Expression == name || n.Arguments?.Contains(name) == true,
        TaggedTemplateExpressionNode n => n.Tag == name,
        ImportAttributeNode n => n.Value == name,
        JsxOpeningElementNode n => n.TagName == name,
        JsxClosingElementNode n => n.TagName == name,
        _ => false
    };

    internal static bool SimpleCopiable(SyntaxNode node) => node is IdentifierNode or StringLiteralNode or NumericLiteralNode
        || node.Kind is K.NoSubstitutionTemplateLiteral or >= K.FirstKeyword and <= K.LastKeyword;
}

public sealed partial class EmitContext
{
    internal async ValueTask<SyntaxNode> BindingAssignmentAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (node is not BindingPatternNode pattern) return node;
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        List<SyntaxNode> elements = [];
        bool array = pattern.Kind == K.ArrayBindingPattern;
        foreach (var item in pattern.Elements ?? new([]))
        {
            SyntaxNode result;
            if (item is not BindingElementNode element || element.Name is null) result = Factory.NewOmittedExpression();
            else if (element.DotDotDotToken is not null) result = array ? Factory.NewSpreadElement(element.Name) : Factory.NewSpreadAssignment(element.Name);
            else if (array || element.PropertyName is not null)
            {
                result = await BindingAssignmentAsync(element.Name, cancellation);
                if (element.Initializer is not null) result = Binary(result, K.EqualsToken, element.Initializer);
                if (!array) result = Factory.NewPropertyAssignment(null, element.PropertyName, null, null, result);
                else if (element.Initializer is null) { elements.Add(result); continue; }
            }
            else result = Factory.NewShorthandPropertyAssignment(null, element.Name, null, null, element.Initializer is null ? null : Factory.NewToken(K.EqualsToken), element.Initializer);
            SetOriginal(result, item);
            AssignCommentAndSourceMapRanges(result, item);
            elements.Add(result);
        }
        var list = new NodeList(elements.ToArray(), pattern.Elements?.Pos ?? -1, pattern.Elements?.End ?? -1, trailingComma: false);
        SyntaxNode converted = array ? Factory.NewArrayLiteralExpression(list, false) : Factory.NewObjectLiteralExpression(list, false);
        SetOriginal(converted, pattern);
        AssignCommentAndSourceMapRanges(converted, pattern);
        return converted;
    }

    internal SyntaxNode NullCondition(SyntaxNode left, SyntaxNode right, bool invert)
    {
        var comparison = invert ? K.EqualsEqualsEqualsToken : K.ExclamationEqualsEqualsToken;
        return Binary(Binary(left, comparison, Factory.NewKeywordExpression(K.NullKeyword)),
            invert ? K.BarBarToken : K.AmpersandAmpersandToken, Binary(right, comparison, VoidZero()));
    }
    internal ConditionalExpressionNode Conditional(SyntaxNode condition, SyntaxNode whenTrue, SyntaxNode whenFalse) =>
        Factory.NewConditionalExpression(condition, Factory.NewToken(K.QuestionToken), whenTrue, Factory.NewToken(K.ColonToken), whenFalse);
    internal CallExpressionNode MethodCall(SyntaxNode target, TypeScript.Compiler.Text.Utf8String method, SyntaxNode[] arguments) =>
        Factory.NewCallExpression(Factory.NewPropertyAccessExpression(target, null, Factory.NewIdentifier(method), NodeFlags.None), null, null, new(arguments), NodeFlags.None);

    public SyntaxNode? InlineExpressions(IReadOnlyList<SyntaxNode> expressions)
    {
        if (expressions.Count == 0) return null;
        var result = expressions[0];
        for (int i = 1; i < expressions.Count; i++)
            result = Binary(result, K.CommaToken, expressions[i]);
        return result;
    }
}

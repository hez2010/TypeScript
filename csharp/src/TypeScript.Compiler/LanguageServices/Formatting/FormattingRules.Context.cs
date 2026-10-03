using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal static partial class FormattingRules
{
    private static bool IsForContext(FormattingContext c) => c.Node.Kind == K.ForStatement;
    private static bool IsNotForContext(FormattingContext c) => !IsForContext(c);
    private static bool IsBinaryOpContext(FormattingContext c) => c.Node.Kind switch
    {
        K.BinaryExpression => ((BinaryExpressionNode)c.Node).OperatorToken?.Kind != K.CommaToken,
        K.ConditionalExpression or K.ConditionalType or K.AsExpression or K.ExportSpecifier or K.ImportSpecifier
            or K.TypePredicate or K.UnionType or K.IntersectionType or K.SatisfiesExpression => true,
        K.BindingElement or K.TypeAliasDeclaration or K.ImportEqualsDeclaration or K.ExportAssignment or K.VariableDeclaration
            or K.Parameter or K.EnumMember or K.PropertyDeclaration or K.PropertySignature => c.Current.Kind == K.EqualsToken || c.Next.Kind == K.EqualsToken,
        K.ForInStatement or K.TypeParameter => c.Current.Kind is K.InKeyword or K.EqualsToken || c.Next.Kind is K.InKeyword or K.EqualsToken,
        K.ForOfStatement => c.Current.Kind == K.OfKeyword || c.Next.Kind == K.OfKeyword,
        _ => false,
    };
    private static bool IsNotBinaryOpContext(FormattingContext c) => !IsBinaryOpContext(c);
    private static bool IsNotTypeAnnotationContext(FormattingContext c) => !IsTypeAnnotationContext(c);
    private static bool IsTypeAnnotationContext(FormattingContext c) => c.Node.HasFunctionSignature
        || c.Node.Kind is K.IndexSignature or K.PropertyDeclaration or K.PropertySignature or K.Parameter or K.VariableDeclaration;
    private static bool IsNonOptionalPropertyContext(FormattingContext c) => c.Node is not PropertyDeclarationNode { PostfixToken.Kind: K.QuestionToken };
    private static bool IsConditionalOperatorContext(FormattingContext c) => c.Node.Kind is K.ConditionalExpression or K.ConditionalType;
    private static bool IsSameLineTokenOrBeforeBlockContext(FormattingContext c) => c.TokensOnOneLine || IsBeforeBlockContext(c);
    private static async ValueTask<bool> IsBraceWrappedContext(FormattingContext c) =>
        c.Node.Kind is K.ObjectBindingPattern or K.MappedType || await IsSingleLineBlockContext(c).ConfigureAwait(false);
    private static async ValueTask<bool> IsBeforeMultilineBlockContext(FormattingContext c) =>
        IsBeforeBlockContext(c) && !(c.NextOnOneLine || await c.NextBlockOnOneLineAsync().ConfigureAwait(false));
    private static async ValueTask<bool> IsMultilineBlockContext(FormattingContext c) =>
        NodeIsBlockContext(c.Node) && !(c.NodeOnOneLine || await c.BlockOnOneLineAsync().ConfigureAwait(false));
    private static async ValueTask<bool> IsSingleLineBlockContext(FormattingContext c) =>
        NodeIsBlockContext(c.Node) && (c.NodeOnOneLine || await c.BlockOnOneLineAsync().ConfigureAwait(false));
    private static bool IsBeforeBlockContext(FormattingContext c) => NodeIsBlockContext(c.NextParent);
    private static bool NodeIsBlockContext(SyntaxNode node) => NodeIsTypeScriptDeclWithBlockContext(node)
        || node.Kind is K.Block or K.CaseBlock or K.ObjectLiteralExpression or K.ModuleBlock;
    private static bool IsFunctionDeclContext(FormattingContext c) => c.Node.Kind is K.FunctionDeclaration or K.MethodDeclaration
        or K.MethodSignature or K.GetAccessor or K.SetAccessor or K.CallSignature or K.FunctionExpression or K.Constructor or K.ArrowFunction or K.InterfaceDeclaration;
    private static bool IsNotFunctionDeclContext(FormattingContext c) => !IsFunctionDeclContext(c);
    private static bool IsFunctionDeclarationOrFunctionExpressionContext(FormattingContext c) => c.Node.Kind is K.FunctionDeclaration or K.FunctionExpression;
    private static bool IsTypeScriptDeclWithBlockContext(FormattingContext c) => NodeIsTypeScriptDeclWithBlockContext(c.Node);
    private static bool NodeIsTypeScriptDeclWithBlockContext(SyntaxNode node) => node.Kind is K.ClassDeclaration or K.ClassExpression
        or K.InterfaceDeclaration or K.EnumDeclaration or K.TypeLiteral or K.ModuleDeclaration or K.ExportDeclaration or K.NamedExports or K.ImportDeclaration or K.NamedImports;
    private static bool IsAfterCodeBlockContext(FormattingContext c) => c.CurrentParent.Kind is K.ClassDeclaration or K.ModuleDeclaration
        or K.EnumDeclaration or K.CatchClause or K.ModuleBlock or K.SwitchStatement || c.CurrentParent.Kind == K.Block
            && c.CurrentParent.Parent?.Kind is not (K.ArrowFunction or K.FunctionExpression);
    private static bool IsControlDeclContext(FormattingContext c) => c.Node.Kind is K.IfStatement or K.SwitchStatement or K.ForStatement
        or K.ForInStatement or K.ForOfStatement or K.WhileStatement or K.TryStatement or K.DoStatement or K.WithStatement or K.CatchClause;
    private static bool IsObjectContext(FormattingContext c) => c.Node.Kind == K.ObjectLiteralExpression;
    private static bool IsFunctionCallOrNewContext(FormattingContext c) => c.Node.Kind is K.CallExpression or K.NewExpression;
    private static bool IsPreviousTokenNotComma(FormattingContext c) => c.Current.Kind != K.CommaToken;
    private static bool IsNextTokenNotCloseBracket(FormattingContext c) => c.Next.Kind != K.CloseBracketToken;
    private static bool IsNextTokenNotCloseParen(FormattingContext c) => c.Next.Kind != K.CloseParenToken;
    private static bool IsArrowFunctionContext(FormattingContext c) => c.Node.Kind == K.ArrowFunction;
    private static bool IsImportTypeContext(FormattingContext c) => c.Node.Kind == K.ImportType;
    private static bool IsNonJsxSameLineTokenContext(FormattingContext c) => c.TokensOnOneLine && c.Node.Kind != K.JsxText;
    private static bool IsNonJsxTextContext(FormattingContext c) => c.Node.Kind != K.JsxText;
    private static bool IsNonJsxElementOrFragmentContext(FormattingContext c) => c.Node.Kind is not (K.JsxElement or K.JsxFragment);
    private static bool IsJsxExpressionContext(FormattingContext c) => c.Node.Kind is K.JsxExpression or K.JsxSpreadAttribute;
    private static bool IsNextTokenParentJsxAttribute(FormattingContext c) => c.NextParent.Kind == K.JsxAttribute
        || c.NextParent is { Kind: K.JsxNamespacedName, Parent.Kind: K.JsxAttribute };
    private static bool IsJsxAttributeContext(FormattingContext c) => c.Node.Kind == K.JsxAttribute;
    private static bool IsNextTokenParentNotJsxNamespacedName(FormattingContext c) => c.NextParent.Kind != K.JsxNamespacedName;
    private static bool IsNextTokenParentJsxNamespacedName(FormattingContext c) => c.NextParent.Kind == K.JsxNamespacedName;
    private static bool IsJsxSelfClosingElementContext(FormattingContext c) => c.Node.Kind == K.JsxSelfClosingElement;
    private static bool IsNotBeforeBlockInFunctionDeclarationContext(FormattingContext c) => !IsFunctionDeclContext(c) && !IsBeforeBlockContext(c);
    private static bool IsEndOfDecoratorContextOnSameLine(FormattingContext c) => c.TokensOnOneLine && c.Node.ModifierList?.Any(n => n.Kind == K.Decorator) == true
        && InDecorator(c.CurrentParent) && !InDecorator(c.NextParent);
    private static bool InDecorator(SyntaxNode? node)
    {
        while (node is not null && ReferenceSyntax.IsExpression(node)) node = node.Parent;
        return node?.Kind == K.Decorator;
    }
    private static bool IsStartOfVariableDeclarationList(FormattingContext c) => c.CurrentParent.Kind == K.VariableDeclarationList
        && SmartIndenter.Start(c.CurrentParent, c.File) == c.Current.Start;
    private static bool IsNotFormatOnEnter(FormattingContext c) => c.RequestKind != FormatRequestKind.OnEnter;
    private static bool IsModuleDeclContext(FormattingContext c) => c.Node.Kind == K.ModuleDeclaration;
    private static bool IsObjectTypeContext(FormattingContext c) => c.Node.Kind == K.TypeLiteral;
    private static bool IsConstructorSignatureContext(FormattingContext c) => c.Node.Kind == K.ConstructSignature;
    private static bool TypeArgumentOrParameterOrAssertion(FormattingRange token, SyntaxNode parent) => token.Kind is K.LessThanToken or K.GreaterThanToken
        && parent.Kind is K.TypeReference or K.TypeAssertionExpression or K.TypeAliasDeclaration or K.ClassDeclaration or K.ClassExpression
            or K.InterfaceDeclaration or K.FunctionDeclaration or K.FunctionExpression or K.ArrowFunction or K.MethodDeclaration or K.MethodSignature
            or K.CallSignature or K.ConstructSignature or K.CallExpression or K.NewExpression or K.ExpressionWithTypeArguments;
    private static bool IsTypeArgumentOrParameterOrAssertionContext(FormattingContext c) =>
        TypeArgumentOrParameterOrAssertion(c.Current, c.CurrentParent) || TypeArgumentOrParameterOrAssertion(c.Next, c.NextParent);
    private static bool IsTypeAssertionContext(FormattingContext c) => c.Node.Kind == K.TypeAssertionExpression;
    private static bool IsNonTypeAssertionContext(FormattingContext c) => !IsTypeAssertionContext(c);
    private static bool IsVoidOpContext(FormattingContext c) => c.Current.Kind == K.VoidKeyword && c.CurrentParent.Kind == K.VoidExpression;
    private static bool IsYieldOrYieldStarWithOperand(FormattingContext c) => c.Node is YieldExpressionNode { Expression: not null };
    private static bool IsNonNullAssertionContext(FormattingContext c) => c.Node.Kind == K.NonNullExpression;
    private static bool IsNotStatementConditionContext(FormattingContext c) => c.Node.Kind is not
        (K.IfStatement or K.ForStatement or K.ForInStatement or K.ForOfStatement or K.DoStatement or K.WhileStatement);
    private static bool IsNotPropertyAccessOnIntegerLiteral(FormattingContext c) => c.Node is not PropertyAccessExpressionNode { Expression: NumericLiteralNode number }
        || number.Text.Span.Contains((byte)'.');

    private static async ValueTask<bool> IsSemicolonDeletionContext(FormattingContext c)
    {
        K nextKind = c.Next.Kind;
        int nextStart = c.Next.Start;
        if (nextKind is >= K.FirstTriviaToken and <= K.LastTriviaToken)
        {
            var next = c.NextParent == c.CurrentParent
                ? await SyntaxNavigation.FindNextTokenAsync(c.NextParent, c.File, c.File, c.Cancellation).ConfigureAwait(false)
                : await SyntaxNavigation.FirstTokenAsync(c.NextParent, c.File, c.Cancellation).ConfigureAwait(false);
            if (next is null) return true;
            nextKind = next.Kind; nextStart = SmartIndenter.Start(next, c.File);
        }
        if (SmartIndenter.SameLine(c.Current.Start, nextStart, c.File)) return nextKind is K.CloseBraceToken or K.EndOfFile;
        if (nextKind == K.SemicolonToken && c.Current.Kind == K.SemicolonToken) return true;
        if (nextKind is K.SemicolonClassElement or K.SemicolonToken) return false;
        if (c.Node.Kind is K.InterfaceDeclaration or K.TypeAliasDeclaration)
            return c.CurrentParent is not PropertySignatureDeclarationNode { Type: null } || nextKind != K.OpenParenToken;
        if (c.CurrentParent is PropertyDeclarationNode property) return property.Initializer is null;
        return c.CurrentParent.Kind is not (K.ForStatement or K.EmptyStatement or K.SemicolonClassElement)
            && nextKind is not (K.OpenBracketToken or K.OpenParenToken or K.PlusToken or K.MinusToken or K.SlashToken or K.RegularExpressionLiteral
                or K.CommaToken or K.TemplateExpression or K.TemplateHead or K.NoSubstitutionTemplateLiteral or K.DotToken);
    }

    private static ValueTask<bool> IsSemicolonInsertionContext(FormattingContext c) =>
        SyntaxNavigation.IsAutomaticSemicolonCandidateAsync(c.Current.End, c.CurrentParent, c.File, c.Cancellation);
}

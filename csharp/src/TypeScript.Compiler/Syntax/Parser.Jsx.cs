using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    private void NextJsx()
    { cancellation.ThrowIfCancellationRequested(); scanner.ScanJsxToken(); }
    private SyntaxNode JsxElement(bool expressionContext)
    {
        int start = Pos; Expected(K.LessThanToken);
        if (Token == K.GreaterThanToken)
        {
            NextJsx(); var opening = Finish(factory.NewJsxOpeningFragment(), start);
            var children = JsxChildren(); int closingStart = Pos; Expected(K.LessThanSlashToken);
            if (Token != K.GreaterThanToken) Error(Messages.X_0_expected, ">");
            if (expressionContext) Next(); else NextJsx();
            var closing = Finish(factory.NewJsxClosingFragment(), closingStart);
            return Finish(factory.NewJsxFragment(opening, children, closing), start);
        }
        SyntaxNode name = JsxName(); NodeList? typeArguments = TypeArguments();
        int attributesStart = Pos; var attributes = new List<SyntaxNode>();
        while (Token is not (K.GreaterThanToken or K.SlashToken or K.EndOfFile))
        {
            int before = scanner.Position, attrStart = Pos;
            if (Take(K.OpenBraceToken))
            { Expected(K.DotDotDotToken); var spread = Expression(2); Expected(K.CloseBraceToken); attributes.Add(Finish(factory.NewJsxSpreadAttribute(spread), attrStart)); }
            else
            {
                SyntaxNode attrName = JsxName(false); SyntaxNode? value = null;
                if (Token == K.EqualsToken)
                {
                    scanner.ScanJsxAttributeValue();
                    if (Token == K.StringLiteral) value = Literal();
                    else if (Token == K.OpenBraceToken) value = JsxExpression(true);
                    else if (Token == K.LessThanToken) value = JsxElement(true);
                    else { Error(Messages.X_or_JSX_element_expected); value = Finish(factory.NewIdentifier(""), Pos, Pos); }
                }
                attributes.Add(Finish(factory.NewJsxAttribute(attrName, value), attrStart));
            }
            if (before == scanner.Position) Next();
        }
        var attributeList = Finish(factory.NewJsxAttributes(new(attributes.ToArray(), attributesStart, Pos)), attributesStart);
        if (Take(K.SlashToken))
        {
            if (Token != K.GreaterThanToken) Error(Messages.X_0_expected, ">");
            if (expressionContext) Next(); else NextJsx();
            return Finish(factory.NewJsxSelfClosingElement(name, typeArguments, attributeList), start);
        }
        if (Token != K.GreaterThanToken) Error(Messages.X_0_expected, ">");
        NextJsx(); var open = Finish(factory.NewJsxOpeningElement(name, typeArguments, attributeList), start);
        NodeList content = JsxChildren(); int closeStart = Pos;
        JsxClosingElementNode close;
        if (Token == K.LessThanSlashToken)
        {
            Next(); SyntaxNode closeName = JsxName();
            if (Token != K.GreaterThanToken) Error(Messages.X_0_expected, ">");
            if (expressionContext) Next(); else NextJsx();
            close = Finish(factory.NewJsxClosingElement(closeName), closeStart);
        }
        else
        {
            Error(Messages.JSX_element_0_has_no_corresponding_closing_tag, source.Text[name.Pos..name.End].Trim());
            close = Finish(factory.NewJsxClosingElement(Finish(factory.NewIdentifier(""), Pos, Pos)), Pos, Pos);
        }
        return Finish(factory.NewJsxElement(open, content, close), start);
    }
    private SyntaxNode JsxName(bool propertyAccess = true)
    {
        int start = Pos;
        scanner.ScanJsxIdentifier(); IdentifierNode first = Identifier(true);
        if (Take(K.ColonToken)) { scanner.ScanJsxIdentifier(); return Finish(factory.NewJsxNamespacedName(first, Identifier(true)), start); }
        SyntaxNode result = first;
        while (propertyAccess && Take(K.DotToken))
        { scanner.ScanJsxIdentifier(); result = Finish(factory.NewPropertyAccessExpression(result, null, Identifier(true), 0), start); }
        return result;
    }
    private NodeList JsxChildren()
    {
        int start = Pos; var children = new List<SyntaxNode>();
        while (Token is not (K.LessThanSlashToken or K.EndOfFile))
        {
            int childStart = Pos;
            if (Token is K.JsxText or K.JsxTextAllWhiteSpaces)
            { bool whitespace = Token == K.JsxTextAllWhiteSpaces; string value = scanner.Value; NextJsx(); children.Add(Finish(factory.NewJsxText(value, whitespace), childStart)); }
            else if (Token == K.OpenBraceToken) children.Add(JsxExpression(false));
            else if (Token == K.LessThanToken) children.Add(JsxElement(false));
            else { Error(Messages.Unexpected_token); NextJsx(); }
        }
        return new(children.ToArray(), start, Pos);
    }
    private SyntaxNode JsxExpression(bool expressionContext)
    {
        int start = Pos; Expected(K.OpenBraceToken); var spread = OptionalToken(K.DotDotDotToken);
        SyntaxNode? expression = Token == K.CloseBraceToken ? null : Expression();
        if (Token != K.CloseBraceToken) Error(Messages.X_0_expected, "}");
        if (expressionContext) Next(); else NextJsx();
        return Finish(factory.NewJsxExpression(spread, expression), start);
    }
}

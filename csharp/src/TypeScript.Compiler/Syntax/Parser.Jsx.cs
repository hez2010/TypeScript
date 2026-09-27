using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    private void NextJsx()
    {
        cancellation.ThrowIfCancellationRequested();
        scanner.ScanJsxToken();
        for (; scannedDiagnostics < scanner.Diagnostics.Count; scannedDiagnostics++)
        {
            Diagnostic diagnostic = scanner.Diagnostics[scannedDiagnostics];
            ErrorAt(diagnostic.Message, diagnostic.Start, diagnostic.Length, diagnostic.Arguments);
        }
    }

    private async ValueTask<SyntaxNode> JsxElementCore(bool expressionContext, SyntaxNode? parentTagName = null, int invalidStart = -1,
        bool mustBeUnary = false)
    {
        await ParseStack;
        int start = Pos;
        Expected(K.LessThanToken);
        if (Token == K.GreaterThanToken)
        {
            NextJsx();
            var opening = Finish(factory.NewJsxOpeningFragment(), start);
            var children = (await JsxChildrenCore().ConfigureAwait(false));
            int closingStart = Pos;
            if (Token == K.EndOfFile)
                ErrorAt(Messages.JSX_fragment_has_no_corresponding_closing_tag, opening.Pos, opening.End - opening.Pos);
            Expected(K.LessThanSlashToken);
            if (Token != K.GreaterThanToken)
                Error(Messages.Expected_corresponding_closing_tag_for_JSX_fragment);
            else if (expressionContext)
                Next();
            else
                NextJsx();
            var closing = Finish(factory.NewJsxClosingFragment(), closingStart);
            return await JsxAdjacentElementsCore(
                Finish(factory.NewJsxFragment(opening, children, closing), start),
                expressionContext && !mustBeUnary,
                invalidStart).ConfigureAwait(false);
        }

        SyntaxNode name = (await JsxNameCore().ConfigureAwait(false));
        NodeList? typeArguments = (context & NodeFlags.JavaScriptFile) == 0
            ? await TypeArgumentsCore(false, allowLineBreak: true).ConfigureAwait(false) : null;
        int attributesStart = Pos;
        var attributes = new List<SyntaxNode>();
        while (Token is not (K.GreaterThanToken or K.SlashToken or K.EndOfFile))
        {
            int before = Pos, attrStart = Pos;
            if (Token != K.OpenBraceToken && Token != K.Identifier && Token is not (>= K.FirstKeyword and <= K.LastKeyword))
            {
                Error(Messages.Identifier_expected);
                if (!expressionContext || StartsStatement())
                    break;
                Next();
                continue;
            }
            if (Take(K.OpenBraceToken))
            {
                Expected(K.DotDotDotToken);
                var spread = (await ExpressionCore().ConfigureAwait(false));
                Expected(K.CloseBraceToken);
                attributes.Add(Finish(factory.NewJsxSpreadAttribute(spread), attrStart));
            }
            else
            {
                SyntaxNode attrName = (await JsxNameCore(false).ConfigureAwait(false));
                SyntaxNode? value = null;
                if (Token == K.EqualsToken)
                {
                    scanner.ScanJsxAttributeValue();
                    if (Token == K.StringLiteral)
                        value = Literal();
                    else if (Token == K.OpenBraceToken)
                        value = (await JsxExpressionCore(true).ConfigureAwait(false));
                    else if (Token == K.LessThanToken)
                        value = (await JsxElementCore(true).ConfigureAwait(false));
                    else
                    {
                        Error(Messages.X_or_JSX_element_expected);
                    }
                }

                attributes.Add(Finish(factory.NewJsxAttribute(attrName, value), attrStart));
            }

            if (before == Pos)
                Next();
        }

        var attributeList = Finish(factory.NewJsxAttributes(new(attributes.ToArray(), attributesStart, Pos)), attributesStart);
        if (Token != K.GreaterThanToken)
        {
            Expected(K.SlashToken);
            if (Token != K.GreaterThanToken)
                Error(Messages.X_0_expected, ">");
            else if (expressionContext)
                Next();
            else
                NextJsx();
            return await JsxAdjacentElementsCore(
                Finish(factory.NewJsxSelfClosingElement(name, typeArguments, attributeList), start),
                expressionContext && !mustBeUnary,
                invalidStart).ConfigureAwait(false);
        }

        NextJsx();
        var open = Finish(factory.NewJsxOpeningElement(name, typeArguments, attributeList), start);
        NodeList content = (await JsxChildrenCore(name).ConfigureAwait(false));
        int closeStart = Pos;
        JsxClosingElementNode close;
        if (content.Count != 0
            && content[^1] is JsxElementNode
            {
                OpeningElement.TagName: { } childOpen,
                ClosingElement: { TagName: { } childClose } childClosing,
                Children: { } childContent
            } child
            && !JsxTagNamesEqual(childOpen, childClose) && JsxTagNamesEqual(name, childClose))
        {
            int end = childContent.End;
            var missing = Finish(factory.NewJsxClosingElement(Finish(factory.NewIdentifier(""), end, end)), end, end);
            var replacement = Finish(factory.NewJsxElement(child.OpeningElement, childContent, missing), child.Pos, end);
            SyntaxNode[] children = content.ToArray();
            children[^1] = replacement;
            content = new(children, content.Pos, end);
            close = childClosing;
        }
        else if (Token == K.LessThanSlashToken)
        {
            Next();
            SyntaxNode closeName = (await JsxNameCore().ConfigureAwait(false));
            bool matches = JsxTagNamesEqual(name, closeName);
            if (Token != K.GreaterThanToken)
                Error(Messages.X_0_expected, ">");
            else if (expressionContext || !matches)
                Next();
            else
                NextJsx();
            close = Finish(factory.NewJsxClosingElement(closeName), closeStart);
            if (!matches)
            {
                if (parentTagName is not null && JsxTagNamesEqual(parentTagName, closeName))
                    ErrorAt(
                        Messages.JSX_element_0_has_no_corresponding_closing_tag,
                        name.Pos,
                        name.End - name.Pos,
                        source.Text[name.Pos..name.End].Trim());
                else
                    ErrorAt(
                        Messages.Expected_corresponding_JSX_closing_tag_for_0,
                        closeName.Pos,
                        closeName.End - closeName.Pos,
                        source.Text[name.Pos..name.End].Trim());
            }
        }
        else
        {
            ErrorAt(
                Messages.JSX_element_0_has_no_corresponding_closing_tag,
                name.Pos,
                name.End - name.Pos,
                source.Text[name.Pos..name.End].Trim());
            Expected(K.LessThanSlashToken);
            close = Finish(factory.NewJsxClosingElement(Finish(factory.NewIdentifier(""), Pos, Pos)), Pos, Pos);
        }

        return await JsxAdjacentElementsCore(
            Finish(factory.NewJsxElement(open, content, close), start),
            expressionContext && !mustBeUnary,
            invalidStart).ConfigureAwait(false);
    }

    private async ValueTask<SyntaxNode> JsxAdjacentElementsCore(SyntaxNode first, bool expressionContext, int invalidStart)
    {
        await ParseStack;
        if (!expressionContext || Token != K.LessThanToken)
            return first;
        if (invalidStart < 0)
            invalidStart = first.Pos;
        SyntaxNode next = await JsxElementCore(true, null, invalidStart).ConfigureAwait(false);
        var separator = factory.NewToken(K.CommaToken);
        separator.Pos = separator.End = next.Pos;
        ErrorAt(Messages.JSX_expressions_must_have_one_parent_element, invalidStart, next.End - invalidStart);
        return Finish(factory.NewBinaryExpression(null, first, null, separator, next), first.Pos);
    }

    private async ValueTask<SyntaxNode> JsxNameCore(bool propertyAccess = true)
    {
        await ParseStack;
        int start = Pos;
        scanner.ScanJsxIdentifier();
        bool isThis = Token == K.ThisKeyword;
        IdentifierNode first = JsxIdentifier();
        if (Take(K.ColonToken))
        {
            scanner.ScanJsxIdentifier();
            return Finish(factory.NewJsxNamespacedName(first, JsxIdentifier()), start);
        }

        SyntaxNode result = propertyAccess && isThis ? Finish(factory.NewKeywordExpression(K.ThisKeyword), start, first.End) : first;
        while (propertyAccess && Take(K.DotToken))
        {
            SyntaxNode right = RightOfDot(false, allowUnicodeEscape: false);
            result = Finish(factory.NewPropertyAccessExpression(result, null, right, 0), start);
        }

        return result;
    }

    private IdentifierNode JsxIdentifier()
    {
        if ((scanner.Flags & (TokenFlags.UnicodeEscape | TokenFlags.ExtendedUnicodeEscape)) != 0)
            Error(Messages.Unicode_escape_sequence_cannot_appear_here);
        return Identifier(true);
    }

    private static bool JsxTagNamesEqual(SyntaxNode left, SyntaxNode right)
    {
        while (true)
        {
            switch (left, right)
            {
                case (IdentifierNode a, IdentifierNode b):
                    return a.Text == b.Text;
                case (KeywordExpressionNode { Kind: K.ThisKeyword }, KeywordExpressionNode { Kind: K.ThisKeyword }):
                    return true;
                case (JsxNamespacedNameNode a, JsxNamespacedNameNode b):
                    return a.Namespace?.Text == b.Namespace?.Text && a.Name?.Text == b.Name?.Text;
                case (
                    PropertyAccessExpressionNode { Expression: { } a, Name: IdentifierNode an },
                    PropertyAccessExpressionNode { Expression: { } b, Name: IdentifierNode bn }):
                    if (an.Text != bn.Text)
                        return false;
                    left = a;
                    right = b;
                    break;
                default:
                    return false;
            }
        }
    }

    private async ValueTask<NodeList> JsxChildrenCore(SyntaxNode? openingTagName = null)
    {
        await ParseStack;
        int start = Pos;
        var children = new List<SyntaxNode>();
        while (true)
        {
            scanner.ResetPosition(Pos);
            NextJsx();
            if (Token is K.LessThanSlashToken or K.EndOfFile)
                break;
            int childStart = Pos;
            if (Token is K.JsxText or K.JsxTextAllWhiteSpaces)
            {
                bool whitespace = Token == K.JsxTextAllWhiteSpaces;
                string value = scanner.Value;
                NextJsx();
                children.Add(Finish(factory.NewJsxText(value, whitespace), childStart));
            }
            else if (Token == K.OpenBraceToken)
                children.Add((await JsxExpressionCore(false).ConfigureAwait(false)));
            else if (Token == K.LessThanToken)
            {
                SyntaxNode child = await JsxElementCore(false, openingTagName).ConfigureAwait(false);
                children.Add(child);
                if (openingTagName is not null
                    && child is JsxElementNode { OpeningElement.TagName: { } open, ClosingElement.TagName: { } close }
                    && !JsxTagNamesEqual(open, close) && JsxTagNamesEqual(openingTagName, close))
                    break;
            }
            else
            {
                Error(Messages.Unexpected_token);
                NextJsx();
            }
        }

        return new(children.ToArray(), start, Pos);
    }

    private async ValueTask<SyntaxNode> JsxExpressionCore(bool expressionContext)
    {
        await ParseStack;
        int start = Pos;
        Expected(K.OpenBraceToken);
        var spread = expressionContext ? null : OptionalToken(K.DotDotDotToken);
        if (expressionContext && Token == K.DotDotDotToken)
            Error(Messages.Expression_expected);
        SyntaxNode? expression = Token == K.CloseBraceToken ? null : (await ExpressionCore().ConfigureAwait(false));
        if (Token != K.CloseBraceToken)
            Error(Messages.X_0_expected, "}");
        else if (expressionContext)
            Next();
        else
            NextJsx();
        return Finish(factory.NewJsxExpression(spread, expression), start);
    }
}

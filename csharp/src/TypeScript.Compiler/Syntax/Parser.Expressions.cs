using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    private static int Precedence(K kind) => kind switch
    {
        K.CommaToken => 1,
        >= K.FirstAssignment and <= K.LastAssignment => 2,
        K.QuestionToken => 3,
        K.QuestionQuestionToken or K.BarBarToken => 4,
        K.AmpersandAmpersandToken => 5,
        K.BarToken => 6,
        K.CaretToken => 7,
        K.AmpersandToken => 8,
        K.EqualsEqualsToken or K.ExclamationEqualsToken or K.EqualsEqualsEqualsToken or K.ExclamationEqualsEqualsToken => 9,
        K.LessThanToken or K.GreaterThanToken or K.LessThanEqualsToken or K.GreaterThanEqualsToken or K.InstanceOfKeyword or K.InKeyword or K.AsKeyword or K.SatisfiesKeyword => 10,
        K.LessThanLessThanToken or K.GreaterThanGreaterThanToken or K.GreaterThanGreaterThanGreaterThanToken => 11,
        K.PlusToken or K.MinusToken => 12,
        K.AsteriskToken or K.SlashToken or K.PercentToken => 13,
        K.AsteriskAsteriskToken => 14,
        _ => 0,
    };
    private SyntaxNode Expression(int minimum = 1)
    {
        int start = Pos;
        SyntaxNode left;
        if (IsArrow()) left = Arrow();
        else left = UnaryExpression();
        while (true)
        {
            scanner.RescanGreaterThanToken();
            int precedence = Precedence(Token);
            if (precedence < minimum || precedence == 0 || Token == K.InKeyword && (context & NodeFlags.DisallowInContext) != 0) break;
            K op = Token;
            if (op is K.AsKeyword or K.SatisfiesKeyword)
            {
                if (LineBreak) break;
                Next(); SyntaxNode type = Type();
                left = op == K.AsKeyword ? Finish(factory.NewAsExpression(left, type), start) : Finish(factory.NewSatisfiesExpression(left, type), start);
                continue;
            }
            if (op == K.QuestionToken)
            {
                var question = ParseToken(); var whenTrue = Expression(2); var colon = ExpectedToken(K.ColonToken); var whenFalse = Expression(2);
                left = Finish(factory.NewConditionalExpression(left, question, whenTrue, colon, whenFalse), start); continue;
            }
            var operatorToken = ParseToken();
            SyntaxNode right = Expression(precedence == 2 || op == K.AsteriskAsteriskToken ? precedence : precedence + 1);
            left = Finish(factory.NewBinaryExpression(null, left, null, operatorToken, right), start);
        }
        return left;
    }
    private bool IsArrow() => Peek(() =>
    {
        if (Token == K.AsyncKeyword && !NextIs(K.EqualsGreaterThanToken)) { Next(); if (LineBreak) return false; }
        if (IsIdentifier) { Next(); return !LineBreak && Token == K.EqualsGreaterThanToken; }
        if (Token == K.LessThanToken)
        {
            int depth = 0;
            do { if (Token == K.LessThanToken) depth++; else if (Token == K.GreaterThanToken) depth--; Next(); } while (depth > 0 && Token != K.EndOfFile);
        }
        if (Token != K.OpenParenToken) return false;
        int parens = 0;
        do { if (Token == K.OpenParenToken) parens++; else if (Token == K.CloseParenToken) parens--; Next(); } while (parens > 0 && Token != K.EndOfFile);
        if (Token == K.ColonToken)
        {
            Next(); int before = scanner.Position; Type();
            if (scanner.Position == before) return false;
        }
        return !LineBreak && Token == K.EqualsGreaterThanToken;
    });
    private SyntaxNode Arrow()
    {
        int start = Pos; NodeList? modifiers = null;
        if (Token == K.AsyncKeyword && !NextIs(K.EqualsGreaterThanToken)) { var modifier = ParseToken(); modifiers = new([modifier], start, Pos); }
        NodeFlags old = context; context &= ~(NodeFlags.YieldContext | NodeFlags.AwaitContext);
        if (modifiers is not null && !IsIdentifier) context |= NodeFlags.AwaitContext;
        NodeList? typeParameters = TypeParameters(); NodeList parameters;
        if (IsIdentifier)
        {
            int paramStart = Pos;
            var parameter = Finish(factory.NewParameterDeclaration(null, null, Identifier(), null, null, null), paramStart);
            parameters = new([parameter], paramStart, Pos);
        }
        else parameters = Parameters(modifiers is not null ? NodeFlags.AwaitContext : 0);
        SyntaxNode? type = Annotation(); context = old; var arrow = ExpectedToken(K.EqualsGreaterThanToken);
        context = (old & ~(NodeFlags.YieldContext | NodeFlags.AwaitContext)) | (modifiers is not null ? NodeFlags.AwaitContext : 0);
        SyntaxNode body = Token == K.OpenBraceToken ? Block() : Expression(2);
        context = old;
        return Finish(factory.NewArrowFunction(modifiers, typeParameters, parameters, type, null, arrow, body), start);
    }
    private SyntaxNode UnaryExpression()
    {
        int start = Pos; K kind = Token;
        switch (kind)
        {
            case K.PlusToken:
            case K.MinusToken:
            case K.TildeToken:
            case K.ExclamationToken:
            case K.PlusPlusToken:
            case K.MinusMinusToken:
                Next(); return Finish(factory.NewPrefixUnaryExpression(kind, UnaryExpression()), start);
            case K.DeleteKeyword: Next(); return Finish(factory.NewDeleteExpression(UnaryExpression()), start);
            case K.TypeOfKeyword: Next(); return Finish(factory.NewTypeOfExpression(UnaryExpression()), start);
            case K.VoidKeyword: Next(); return Finish(factory.NewVoidExpression(UnaryExpression()), start);
            case K.AwaitKeyword:
                if ((context & NodeFlags.AwaitContext) != 0 || Peek(() => { Next(); return !LineBreak && (IsIdentifier || Token is K.NewKeyword or K.ThisKeyword); }))
                { Next(); return Finish(factory.NewAwaitExpression(UnaryExpression()), start); }
                break;
            case K.YieldKeyword:
                if ((context & NodeFlags.YieldContext) != 0)
                {
                    Next(); var star = LineBreak ? null : OptionalToken(K.AsteriskToken);
                    SyntaxNode? expression = IsSemicolon() || Token is K.CloseParenToken or K.CommaToken ? null : Expression(2);
                    return Finish(factory.NewYieldExpression(star, expression), start);
                }
                break;
            case K.LessThanToken when !scanner.Jsx:
                Next(); var type = Type(); Expected(K.GreaterThanToken); return Finish(factory.NewTypeAssertion(type, UnaryExpression()), start);
        }
        SyntaxNode left = MemberExpression(PrimaryExpression(), true);
        if (!LineBreak && Token is K.PlusPlusToken or K.MinusMinusToken)
        { K op = Token; Next(); left = Finish(factory.NewPostfixUnaryExpression(left, op), start); }
        return left;
    }
    private SyntaxNode PrimaryExpression()
    {
        int start = Pos;
        switch (Token)
        {
            case K.StringLiteral: case K.NumericLiteral: case K.BigIntLiteral: return Literal();
            case K.NoSubstitutionTemplateLiteral: case K.TemplateHead: return Template(false, false);
            case K.TrueKeyword:
            case K.FalseKeyword:
            case K.NullKeyword:
            case K.ThisKeyword:
            case K.SuperKeyword:
                var keyword = factory.NewKeywordExpression(Token); Next(); return Finish(keyword, start);
            case K.PrivateIdentifier: var name = factory.NewPrivateIdentifier(scanner.Value); Next(); return Finish(name, start);
            case K.SlashToken:
            case K.SlashEqualsToken:
                scanner.RescanSlashToken(true); return Literal();
            case K.FunctionKeyword: return Function(true, null, start);
            case K.AsyncKeyword:
                if (NextIs(K.FunctionKeyword)) { var asyncToken = ParseToken(); return Function(true, new([asyncToken], start, Pos), start); }
                break;
            case K.ClassKeyword: return Class(true, null, start);
            case K.NewKeyword:
                Next();
                if (Take(K.DotToken)) return Finish(factory.NewMetaProperty(K.NewKeyword, Identifier(true)), start);
                SyntaxNode construct = MemberExpression(PrimaryExpression(), false); NodeList? types = TypeArguments();
                NodeList? args = Token == K.OpenParenToken ? Arguments() : null;
                return Finish(factory.NewNewExpression(construct, types, args), start);
            case K.ImportKeyword:
                Next();
                if (Take(K.DotToken)) { sourceFlags |= NodeFlags.PossiblyContainsImportMeta; return Finish(factory.NewMetaProperty(K.ImportKeyword, Identifier(true)), start); }
                sourceFlags |= NodeFlags.PossiblyContainsDynamicImport;
                return Finish(factory.NewKeywordExpression(K.ImportKeyword), start);
            case K.OpenParenToken:
                Next(); NodeFlags saved = context; context &= ~(NodeFlags.DisallowInContext | NodeFlags.DecoratorContext);
                SyntaxNode inner = Expression(); Expected(K.CloseParenToken); context = saved; return Finish(factory.NewParenthesizedExpression(inner), start);
            case K.OpenBracketToken:
                Next(); bool arrayLines = LineBreak; var elements = Delimited(K.CloseBracketToken, Argument); Expected(K.CloseBracketToken);
                return Finish(factory.NewArrayLiteralExpression(elements, arrayLines), start);
            case K.OpenBraceToken:
                Next(); bool objectLines = LineBreak; var properties = Delimited(K.CloseBraceToken, ObjectProperty); Expected(K.CloseBraceToken);
                return Finish(factory.NewObjectLiteralExpression(properties, objectLines), start);
            case K.LessThanToken when scanner.Jsx: return JsxElement(true);
        }
        return Identifier();
    }
    private SyntaxNode MemberExpression(SyntaxNode left, bool calls)
    {
        int start = left.Pos;
        while (true)
        {
            TokenNode? question = OptionalToken(K.QuestionDotToken);
            NodeFlags chain = question is not null || (left.Flags & NodeFlags.OptionalChain) != 0 ? NodeFlags.OptionalChain : 0;
            if (Token == K.DotToken || question is not null && Token is not (K.OpenBracketToken or K.OpenParenToken or K.LessThanToken))
            {
                if (question is null) Next();
                SyntaxNode name;
                if (Token == K.PrivateIdentifier) { int nameStart = Pos; name = factory.NewPrivateIdentifier(scanner.Value); Next(); Finish(name, nameStart); }
                else name = Identifier(true);
                left = Finish(factory.NewPropertyAccessExpression(left, question, name, chain), start); continue;
            }
            if (Take(K.OpenBracketToken))
            {
                SyntaxNode argument;
                if (Token == K.CloseBracketToken) { Error(Messages.An_element_access_expression_should_take_an_argument); argument = Finish(factory.NewIdentifier(""), Pos, Pos); }
                else argument = Expression();
                Expected(K.CloseBracketToken); left = Finish(factory.NewElementAccessExpression(left, question, argument, chain), start); continue;
            }
            if (!LineBreak && Take(K.ExclamationToken)) { left = Finish(factory.NewNonNullExpression(left, chain), start); continue; }
            NodeList? typeArguments = null;
            if (calls && Token == K.LessThanToken && Peek(() => { TypeArguments(); return Token is K.OpenParenToken or K.NoSubstitutionTemplateLiteral or K.TemplateHead; })) typeArguments = TypeArguments();
            if (calls && Token == K.OpenParenToken)
            { left = Finish(factory.NewCallExpression(left, question, typeArguments, Arguments(), chain), start); continue; }
            if (Token is K.NoSubstitutionTemplateLiteral or K.TemplateHead)
            { left = Finish(factory.NewTaggedTemplateExpression(left, question, typeArguments, Template(false, true), chain), start); continue; }
            return left;
        }
    }
    private NodeList Arguments()
    { Expected(K.OpenParenToken); NodeList args = Delimited(K.CloseParenToken, Argument); Expected(K.CloseParenToken); return args; }
    private SyntaxNode Argument()
    {
        int start = Pos;
        if (Token == K.CommaToken) return Finish(factory.NewOmittedExpression(), start, start);
        if (Take(K.DotDotDotToken)) return Finish(factory.NewSpreadElement(Expression(2)), start);
        return Expression(2);
    }
    private SyntaxNode ObjectProperty()
    {
        int start = Pos;
        if (Take(K.DotDotDotToken)) return Finish(factory.NewSpreadAssignment(Expression(2)), start);
        var modifiers = Modifiers(); K accessor = K.Unknown;
        if (Token is K.GetKeyword or K.SetKeyword && Peek(() => { Next(); return !LineBreak && (IsIdentifier || Token is K.StringLiteral or K.NumericLiteral or K.OpenBracketToken); })) { accessor = Token; Next(); }
        var star = OptionalToken(K.AsteriskToken); SyntaxNode name = Name(); var postfix = OptionalToken(K.QuestionToken);
        if (Token is K.OpenParenToken or K.LessThanToken || accessor != K.Unknown)
        {
            NodeFlags signatureFlags = (star is not null ? NodeFlags.YieldContext : 0) | (modifiers?.Any(m => m.Kind == K.AsyncKeyword) == true ? NodeFlags.AwaitContext : 0);
            var types = TypeParameters(); var parameters = Parameters(signatureFlags); var type = Annotation(); var body = FunctionBody(signatureFlags);
            return accessor switch
            {
                K.GetKeyword => Finish(factory.NewGetAccessorDeclaration(modifiers, name, types, parameters, type, null, body), start),
                K.SetKeyword => Finish(factory.NewSetAccessorDeclaration(modifiers, name, types, parameters, type, null, body), start),
                _ => Finish(factory.NewMethodDeclaration(modifiers, star, name, postfix, types, parameters, type, null, body), start),
            };
        }
        if (Take(K.ColonToken)) return Finish(factory.NewPropertyAssignment(modifiers, name, postfix, null, Expression(2)), start);
        var equals = OptionalToken(K.EqualsToken); SyntaxNode? initializer = equals is null ? null : Expression(2);
        return Finish(factory.NewShorthandPropertyAssignment(modifiers, name, postfix, null, equals, initializer), start);
    }
    private SyntaxNode Template(bool type, bool tagged)
    {
        int start = Pos;
        if (!tagged) scanner.RescanTemplateToken(false);
        if (Token == K.NoSubstitutionTemplateLiteral) return type ? Finish(factory.NewLiteralTypeNode(Literal()), start) : Literal();
        var head = (TemplateHeadNode)TemplatePart();
        int spansStart = Pos; var spans = new List<SyntaxNode>();
        while (true)
        {
            int spanStart = Pos; SyntaxNode expression = type ? Type() : Expression();
            if (Token != K.CloseBraceToken)
            {
                Error(Messages.X_0_expected, "}");
                var missing = Finish(factory.NewTemplateTail("", "", 0), Pos, Pos);
                spans.Add(type ? Finish(factory.NewTemplateLiteralTypeSpan(expression, missing), spanStart) : Finish(factory.NewTemplateSpan(expression, missing), spanStart)); break;
            }
            scanner.RescanTemplateToken(tagged); K end = Token; SyntaxNode literal = TemplatePart();
            spans.Add(type ? Finish(factory.NewTemplateLiteralTypeSpan(expression, literal), spanStart) : Finish(factory.NewTemplateSpan(expression, literal), spanStart));
            if (end != K.TemplateMiddle) break;
        }
        return type ? Finish(factory.NewTemplateLiteralTypeNode(head, new(spans.ToArray(), spansStart, Pos)), start) : Finish(factory.NewTemplateExpression(head, new(spans.ToArray(), spansStart, Pos)), start);
    }
    private SyntaxNode TemplatePart()
    {
        int start = Pos; string value = scanner.Value;
        string raw = scanner.TokenText.ToString(); int suffix = Token == K.TemplateTail ? 1 : 2;
        raw = raw.Length > suffix ? raw[1..^suffix].Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : "";
        TokenFlags flags = scanner.Flags & TokenFlags.TemplateLiteralLikeFlags;
        SyntaxNode node = Token switch
        { K.TemplateHead => factory.NewTemplateHead(value, raw, flags), K.TemplateMiddle => factory.NewTemplateMiddle(value, raw, flags), _ => factory.NewTemplateTail(value, raw, flags) };
        Next(); return Finish(node, start);
    }
}

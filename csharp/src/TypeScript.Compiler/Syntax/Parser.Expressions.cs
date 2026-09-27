using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    private static bool IsAssignmentLeft(SyntaxNode node) =>
        node.Kind is K.Identifier or K.PrivateIdentifier or K.PropertyAccessExpression or K.ElementAccessExpression
            or K.ParenthesizedExpression or K.ArrayLiteralExpression or K.ObjectLiteralExpression or K.NonNullExpression
            or K.CallExpression or K.NewExpression or K.JsxElement or K.JsxSelfClosingElement or K.JsxFragment
            or K.TaggedTemplateExpression or K.ClassExpression or K.FunctionExpression or K.RegularExpressionLiteral or K.NumericLiteral
            or K.BigIntLiteral or K.StringLiteral or K.NoSubstitutionTemplateLiteral or K.TemplateExpression or K.FalseKeyword
            or K.NullKeyword or K.ThisKeyword or K.TrueKeyword or K.SuperKeyword or K.ExpressionWithTypeArguments or K.MetaProperty
            or K.ImportKeyword or K.MissingDeclaration;

    private void ErrorOnNode(SyntaxNode node, DiagnosticMessage message, params string[] arguments) =>
        ErrorOnRange(node.Pos, node.End, message, arguments);

    private void ErrorOnNode(NodeList nodes, DiagnosticMessage message) => ErrorOnRange(nodes.Pos - 1, nodes.End + 1, message, []);

    private void ErrorOnRange(int start, int end, DiagnosticMessage message, params string[] arguments)
    {
        var positionScanner = new Scanner(source);
        positionScanner.ResetPosition(Math.Max(0, start));
        positionScanner.Scan();
        ErrorAt(message, positionScanner.TokenStart, Math.Max(0, end - positionScanner.TokenStart), arguments);
    }

    private static NodeFlags ContinueOptionalChain(SyntaxNode node)
    {
        SyntaxNode expression = node;
        while (expression is NonNullExpressionNode { Expression: { } inner })
            expression = inner;
        if ((expression.Flags & NodeFlags.OptionalChain) == 0)
            return 0;
        while (node is NonNullExpressionNode { Expression: { } inner })
        {
            node.Flags |= NodeFlags.OptionalChain;
            node = inner;
        }
        return NodeFlags.OptionalChain;
    }

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
        K.LessThanToken or K.GreaterThanToken or K.LessThanEqualsToken or K.GreaterThanEqualsToken or K.InstanceOfKeyword or K.InKeyword
            or K.AsKeyword or K.SatisfiesKeyword => 10,
        K.LessThanLessThanToken or K.GreaterThanGreaterThanToken or K.GreaterThanGreaterThanGreaterThanToken => 11,
        K.PlusToken or K.MinusToken => 12,
        K.AsteriskToken or K.SlashToken or K.PercentToken => 13,
        K.AsteriskAsteriskToken => 14,
        _ => 0,
    };

    private HashSet<(int Position, NodeFlags Context, bool ReturnType)>? notArrows;

    private async ValueTask<SyntaxNode> ExpressionCore(int minimum = 1, bool allowArrowReturnType = true)
    {
        await ParseStack;
        int start = Pos;
        SyntaxNode left;
        bool assignmentComplete = minimum <= 2 && await IsArrowCore(allowArrowReturnType).ConfigureAwait(false);
        if (assignmentComplete)
            left = (await ArrowCore(allowArrowReturnType).ConfigureAwait(false));
        else
            left = (await UnaryExpressionCore(minimum <= 2).ConfigureAwait(false));
        int lastBinaryPrecedence = int.MaxValue;
        while (true)
        {
            scanner.RescanGreaterThanToken();
            int precedence = Precedence(Token);
            if (assignmentComplete && Token != K.CommaToken
                || precedence < minimum
                || precedence == 0
                || Token == K.InKeyword && (context & NodeFlags.DisallowInContext) != 0)
                break;
            K op = Token;
            if (op is K.AsKeyword or K.SatisfiesKeyword)
            {
                if (LineBreak)
                    break;
                Next();
                SyntaxNode type = (await TypeCore().ConfigureAwait(false));
                left = op == K.AsKeyword
                    ? Finish(factory.NewAsExpression(left, type), start)
                    : Finish(factory.NewSatisfiesExpression(left, type), start);
                scanner.RescanGreaterThanToken();
                int nextPrecedence = Precedence(Token);
                if (nextPrecedence > lastBinaryPrecedence || Token == K.AsteriskAsteriskToken && nextPrecedence == lastBinaryPrecedence)
                    break;
                continue;
            }

            if (op == K.QuestionToken)
            {
                var question = ParseToken();
                var whenTrue = (await ExpressionCore(2, false).ConfigureAwait(false));
                var colon = ExpectedToken(K.ColonToken);
                var whenFalse = (await ExpressionCore(2, allowArrowReturnType).ConfigureAwait(false));
                left = Finish(factory.NewConditionalExpression(left, question, whenTrue, colon, whenFalse), start);
                assignmentComplete = true;
                continue;
            }

            if (precedence == 2 && !IsAssignmentLeft(left))
                break;
            if (op == K.AsteriskAsteriskToken
                && left is PrefixUnaryExpressionNode { Operator: not (K.PlusPlusToken or K.MinusMinusToken) } or DeleteExpressionNode
                    or TypeOfExpressionNode or VoidExpressionNode or AwaitExpressionNode or TypeAssertionNode)
                ErrorOnNode(
                    left,
                    left is TypeAssertionNode
                        ? Messages.A_type_assertion_expression_is_not_allowed_in_the_left_hand_side_of_an_exponentiation_expression_Consider_enclosing_the_expression_in_parentheses
                        : Messages.An_unary_expression_with_the_0_operator_is_not_allowed_in_the_left_hand_side_of_an_exponentiation_expression_Consider_enclosing_the_expression_in_parentheses,
                    left is PrefixUnaryExpressionNode unary ? TokenFacts.Text(unary.Operator) : left.Kind.ToString());

            var operatorToken = ParseToken();
            SyntaxNode right = (await ExpressionCore(
                precedence == 2 || op == K.AsteriskAsteriskToken ? precedence : precedence + 1,
                allowArrowReturnType).ConfigureAwait(false));
            left = Finish(factory.NewBinaryExpression(null, left, null, operatorToken, right), start);
            assignmentComplete = precedence <= 2;
            lastBinaryPrecedence = precedence;
        }

        return left;
    }

    private async ValueTask<bool> IsArrowCore(bool allowReturnType = true)
    {
        await ParseStack;
        if (Token == K.EqualsGreaterThanToken)
            return true;
        if (!IsBindingIdentifier && Token is not (K.OpenParenToken or K.LessThanToken))
            return false;
        int certainty = ArrowCertainty();
        if (certainty != 0)
            return certainty > 0;
        var key = (Pos, context, allowReturnType);
        if (notArrows?.Contains(key) == true)
            return false;
        bool result = await PeekCore(async () =>
        {
            bool async = Token == K.AsyncKeyword && !NextIs(K.EqualsGreaterThanToken);
            if (async)
            {
                Next();
                if (LineBreak)
                    return false;
            }

            if (IsIdentifier)
            {
                Next();
                return Token == K.EqualsGreaterThanToken;
            }

            if (Token == K.LessThanToken)
            {
                int depth = 0;
                do
                {
                    if (Token == K.LessThanToken)
                        depth++;
                    else if (Token == K.GreaterThanToken)
                        depth--;
                    Next();
                }
                while (depth > 0 && Token != K.EndOfFile);
            }

            if (Token != K.OpenParenToken)
                return false;
            if (Peek(() =>
            {
                Next();
                return Token is not (K.CloseParenToken or K.OpenBracketToken or K.OpenBraceToken or K.DotDotDotToken or K.ThisKeyword)
                    && !IsBindingIdentifier
                    && !IsModifierKind(Token);
            }))
                return false;
            NodeList parameters = await ParametersCore(async ? NodeFlags.AwaitContext : 0, requireComplete: true).ConfigureAwait(false);
            if (parameters.IsMissing || parameters.Any(p => p is ParameterDeclarationNode { Name: IdentifierNode { Text.Length: 0 } }))
                return false;
            bool hasReturnColon = Token == K.ColonToken;
            if (hasReturnColon)
            {
                Next();
                int before = Pos;
                await TypeOrPredicateCore().ConfigureAwait(false);
                if (Pos == before)
                    return false;
            }

            if (Token is not (K.EqualsGreaterThanToken or K.OpenBraceToken))
                return false;
            if (!allowReturnType
                && hasReturnColon
                && !parameters.Any(
                    p => p is ParameterDeclarationNode { Type: not null } or ParameterDeclarationNode { QuestionToken: not null }
                        or ParameterDeclarationNode { DotDotDotToken: not null }))
            {
                Next();
                if (Token == K.OpenBraceToken)
                    await BlockCore().ConfigureAwait(false);
                else
                    await ExpressionCore(2, false).ConfigureAwait(false);
                return Token == K.ColonToken;
            }
            return true;
        }).ConfigureAwait(false);
        if (!result && Token is K.OpenParenToken or K.LessThanToken or K.AsyncKeyword)
            (notArrows ??= []).Add(key);
        return result;
    }

    private int ArrowCertainty()
    {
        if (Token is not (K.OpenParenToken or K.LessThanToken or K.AsyncKeyword))
            return 0;
        if (Token == K.AsyncKeyword && !Peek(() => Next() is K.OpenParenToken or K.LessThanToken))
            return 0;
        int result = 0;
        Peek(() =>
        {
            result = Worker();
            return false;
        });
        return result;

        int Worker()
        {
            if (Token == K.AsyncKeyword)
            {
                Next();
                if (LineBreak || Token is not (K.OpenParenToken or K.LessThanToken))
                    return -1;
            }
            K first = Token, second = Next();
            if (first == K.OpenParenToken)
            {
                if (second == K.CloseParenToken)
                    return Next() is K.EqualsGreaterThanToken or K.ColonToken or K.OpenBraceToken ? 1 : -1;
                if (second is K.OpenBracketToken or K.OpenBraceToken)
                    return 0;
                if (second == K.DotDotDotToken)
                    return 1;
                if (IsModifierKind(second) && second != K.AsyncKeyword && Peek(() =>
                {
                    Next();
                    return IsIdentifier;
                }))
                    return Next() == K.AsKeyword ? -1 : 1;
                if (!IsIdentifier && second != K.ThisKeyword)
                    return -1;
                return Next() switch
                {
                    K.ColonToken => 1,
                    K.QuestionToken => Next() is K.ColonToken or K.CommaToken or K.EqualsToken or K.CloseParenToken ? 1 : -1,
                    K.CommaToken or K.EqualsToken or K.CloseParenToken => 0,
                    _ => -1
                };
            }
            if (!IsIdentifier && Token != K.ConstKeyword)
                return -1;
            if (!scanner.Jsx)
                return 0;
            Take(K.ConstKeyword);
            K third = Next();
            return third == K.ExtendsKeyword ? Next() is K.EqualsToken or K.GreaterThanToken or K.SlashToken ? -1 : 1
                : third is K.CommaToken or K.EqualsToken ? 1 : -1;
        }
    }

    private async ValueTask<SyntaxNode> ArrowCore(bool allowReturnType = true)
    {
        await ParseStack;
        int start = Pos;
        TokenFlags trivia = scanner.Flags;
        allowReturnType |= ArrowCertainty() > 0;
        NodeList? modifiers = null;
        if (Token == K.AsyncKeyword && !NextIs(K.EqualsGreaterThanToken))
        {
            var modifier = ParseToken();
            modifiers = new([modifier], start, Pos);
        }

        NodeFlags old = context;
        NodeList? typeParameters = (await TypeParametersCore().ConfigureAwait(false));
        NodeList parameters;
        if (IsIdentifier)
        {
            int paramStart = Pos;
            var parameter = Finish(factory.NewParameterDeclaration(null, null, Identifier(), null, null, null), paramStart);
            parameters = new([parameter], paramStart, Pos);
        }
        else
            parameters = (await ParametersCore(modifiers is not null ? NodeFlags.AwaitContext : 0).ConfigureAwait(false));
        SyntaxNode? type = await ReturnAnnotationCore().ConfigureAwait(false);
        context = old;
        K beforeArrow = Token;
        var arrow = ExpectedToken(K.EqualsGreaterThanToken);
        context = (old & ~(NodeFlags.YieldContext | NodeFlags.AwaitContext)) | (modifiers is not null ? NodeFlags.AwaitContext : 0);
        bool missingBlock = Token is not (K.SemicolonToken or K.FunctionKeyword or K.ClassKeyword)
            && StartsStatement() && (Token == K.AtToken || !StartsExpression());
        SyntaxNode body = beforeArrow is not (K.EqualsGreaterThanToken or K.OpenBraceToken) ? Identifier()
            : Token == K.OpenBraceToken || missingBlock
            ? (await BlockCore(missingBlock).ConfigureAwait(false))
            : (await ExpressionCore(2, allowReturnType).ConfigureAwait(false));
        context = old;
        return await WithJSDocCore(
            Finish(factory.NewArrowFunction(modifiers, typeParameters, parameters, type, null, arrow, body), start),
            trivia).ConfigureAwait(false);
    }

    private async ValueTask<SyntaxNode> UnaryExpressionCore(bool allowYield = false, bool simple = false)
    {
        await ParseStack;
        int start = Pos;
        K kind = Token;
        switch (kind)
        {
            case K.PlusToken:
            case K.MinusToken:
            case K.TildeToken:
            case K.ExclamationToken:
            case K.PlusPlusToken:
            case K.MinusMinusToken:
                Next();
                SyntaxNode operand = kind is K.PlusPlusToken or K.MinusMinusToken
                    ? await MemberExpressionCore(await PrimaryExpressionCore().ConfigureAwait(false), true).ConfigureAwait(false)
                    : await UnaryExpressionCore(simple: true).ConfigureAwait(false);
                return Finish(factory.NewPrefixUnaryExpression(kind, operand), start);
            case K.DeleteKeyword:
                Next();
                return Finish(factory.NewDeleteExpression((await UnaryExpressionCore(simple: true).ConfigureAwait(false))), start);
            case K.TypeOfKeyword:
                Next();
                return Finish(factory.NewTypeOfExpression((await UnaryExpressionCore(simple: true).ConfigureAwait(false))), start);
            case K.VoidKeyword:
                Next();
                return Finish(factory.NewVoidExpression((await UnaryExpressionCore(simple: true).ConfigureAwait(false))), start);
            case K.AwaitKeyword:
                if ((context & NodeFlags.AwaitContext) != 0 || Peek(() =>
                {
                    Next();
                    return !LineBreak && (Token >= K.Identifier || Token is K.NumericLiteral or K.BigIntLiteral or K.StringLiteral);
                }))
                {
                    Next();
                    return Finish(factory.NewAwaitExpression((await UnaryExpressionCore(simple: true).ConfigureAwait(false))), start);
                }

                break;
            case K.YieldKeyword:
                if ((context & NodeFlags.YieldContext) != 0 || Peek(() =>
                {
                    Next();
                    return !LineBreak && (Token >= K.Identifier || Token is K.NumericLiteral or K.BigIntLiteral or K.StringLiteral);
                }))
                {
                    if (!allowYield)
                        Error(Messages.Expression_expected);
                    Next();
                    var star = LineBreak ? null : OptionalToken(K.AsteriskToken);
                    if (star is not null && !StartsExpression())
                        Error(Messages.Expression_expected);
                    SyntaxNode? expression = star is null && (LineBreak || !StartsExpression())
                        ? null
                        : (await ExpressionCore(2).ConfigureAwait(false));
                    return Finish(factory.NewYieldExpression(star, expression), start);
                }

                break;
            case K.LessThanToken when !scanner.Jsx:
                Next();
                var type = (await TypeCore().ConfigureAwait(false));
                Expected(K.GreaterThanToken);
                return Finish(factory.NewTypeAssertion(type, (await UnaryExpressionCore(simple: true).ConfigureAwait(false))), start);
            case K.LessThanToken when scanner.Jsx && (simple || Peek(() => Next() == K.GreaterThanToken || Token >= K.Identifier)):
                return await JsxElementCore(true, mustBeUnary: simple).ConfigureAwait(false);
        }

        SyntaxNode left = (await MemberExpressionCore((await PrimaryExpressionCore().ConfigureAwait(false)), true).ConfigureAwait(false));
        if (!LineBreak && Token is K.PlusPlusToken or K.MinusMinusToken)
        {
            K op = Token;
            Next();
            left = Finish(factory.NewPostfixUnaryExpression(left, op), start);
        }

        return left;
    }

    private async ValueTask<SyntaxNode> PrimaryExpressionCore()
    {
        await ParseStack;
        int start = Pos;
        switch (Token)
        {
            case K.StringLiteral:
            case K.NumericLiteral:
            case K.BigIntLiteral:
                return Literal();
            case K.NoSubstitutionTemplateLiteral:
            case K.TemplateHead:
                return (await TemplateCore(false, false).ConfigureAwait(false));
            case K.TrueKeyword:
            case K.FalseKeyword:
            case K.NullKeyword:
            case K.ThisKeyword:
            case K.SuperKeyword:
                var keyword = factory.NewKeywordExpression(Token);
                Next();
                return Finish(keyword, start);
            case K.PrivateIdentifier:
                var name = factory.NewPrivateIdentifier(scanner.Value);
                Next();
                return Finish(name, start);
            case K.SlashToken:
            case K.SlashEqualsToken:
                // Pattern grammar checks belong to semantic validation unless explicitly requested.
                scanner.RescanSlashToken(options.CheckRegularExpressions);
                return Literal();
            case K.FunctionKeyword:
                TokenFlags functionTrivia = scanner.Flags;
                return await WithJSDocCore(
                    await FunctionCore(true, null, start).ConfigureAwait(false),
                    functionTrivia).ConfigureAwait(false);
            case K.AsyncKeyword:
                if (NextIs(K.FunctionKeyword))
                {
                    TokenFlags asyncTrivia = scanner.Flags;
                    var asyncToken = ParseToken();
                    return await WithJSDocCore(
                        await FunctionCore(true, new([asyncToken], start, Pos), start).ConfigureAwait(false),
                        asyncTrivia).ConfigureAwait(false);
                }

                break;
            case K.ClassKeyword:
                return (await ClassCore(true, null, start).ConfigureAwait(false));
            case K.AtToken:
                var modifiers = await ModifiersCore().ConfigureAwait(false);
                if (Token == K.ClassKeyword)
                    return await ClassCore(true, modifiers, start).ConfigureAwait(false);
                Error(Messages.Expression_expected);
                return Finish(factory.NewMissingDeclaration(modifiers), start);
            case K.NewKeyword:
                Next();
                if (Take(K.DotToken))
                    return Finish(factory.NewMetaProperty(K.NewKeyword, Identifier(true)), start);
                SyntaxNode construct = (await MemberExpressionCore(
                    (await PrimaryExpressionCore().ConfigureAwait(false)),
                    false).ConfigureAwait(false));
                NodeList? types = null;
                if (construct is ExpressionWithTypeArgumentsNode instantiation)
                {
                    types = instantiation.TypeArguments;
                    construct = instantiation.Expression!;
                }
                if (Token == K.QuestionDotToken)
                    Error(Messages.Invalid_optional_chain_from_new_expression_Did_you_mean_to_call_0,
                        source.Text[construct.Pos..construct.End].Trim());
                NodeList? args = Token == K.OpenParenToken ? (await ArgumentsCore().ConfigureAwait(false)) : null;
                return Finish(factory.NewNewExpression(construct, types, args), start);
            case K.ImportKeyword:
                if (!Peek(() => Next() is K.OpenParenToken or K.DotToken or K.LessThanToken))
                {
                    Error(Messages.Expression_expected);
                    return Finish(factory.NewIdentifier(""), Pos, Pos);
                }
                Next();
                if (Take(K.DotToken))
                {
                    var metaName = Identifier(true);
                    if (metaName.Text != "defer")
                        sourceFlags |= NodeFlags.PossiblyContainsImportMeta;
                    else if (Token is K.OpenParenToken or K.LessThanToken)
                        sourceFlags |= NodeFlags.PossiblyContainsDynamicImport;
                    return Finish(factory.NewMetaProperty(K.ImportKeyword, metaName), start);
                }

                sourceFlags |= NodeFlags.PossiblyContainsDynamicImport;
                return Finish(factory.NewKeywordExpression(K.ImportKeyword), start);
            case K.OpenParenToken:
                TokenFlags trivia = scanner.Flags;
                Next();
                NodeFlags saved = context;
                context &= ~(NodeFlags.DisallowInContext | NodeFlags.DecoratorContext);
                SyntaxNode inner = (await ExpressionCore().ConfigureAwait(false));
                Expected(K.CloseParenToken);
                context = saved;
                return (await WithJSDocCore(Finish(factory.NewParenthesizedExpression(inner), start), trivia).ConfigureAwait(false));
            case K.OpenBracketToken:
                Next();
                bool arrayLines = LineBreak;
                var elements = await DelimitedCore(K.CloseBracketToken, ArgumentCore,
                    startsElement: () => Token is K.CommaToken or K.DotToken or K.DotDotDotToken || StartsExpression(),
                    elementExpected: Messages.Expression_or_comma_expected, recoveryBoundary: StartsStatement).ConfigureAwait(false);
                Expected(K.CloseBracketToken);
                return Finish(factory.NewArrayLiteralExpression(elements, arrayLines), start);
            case K.OpenBraceToken:
                Next();
                bool objectLines = LineBreak;
                objectLiteralDepth++;
                NodeList properties;
                try
                {
                    properties = await DelimitedCore(K.CloseBraceToken, ObjectPropertyCore,
                        startsElement: () => Token >= K.Identifier || Token is K.OpenBracketToken or K.AsteriskToken
                            or K.DotDotDotToken or K.DotToken or K.StringLiteral or K.NumericLiteral or K.BigIntLiteral,
                        elementExpected: Messages.Property_assignment_expected).ConfigureAwait(false);
                }
                finally
                {
                    objectLiteralDepth--;
                }

                Expected(K.CloseBraceToken);
                return Finish(factory.NewObjectLiteralExpression(properties, objectLines), start);
        }

        return Identifier();
    }

    private async ValueTask<SyntaxNode> MemberExpressionCore(SyntaxNode left, bool calls)
    {
        await ParseStack;
        int start = left.Pos;
        while (true)
        {
            if (!calls && Token == K.QuestionDotToken)
                return left;
            TokenNode? question = OptionalToken(K.QuestionDotToken);
            NodeFlags chain = question is not null || (left.Flags & NodeFlags.OptionalChain) != 0 ? NodeFlags.OptionalChain : 0;
            if (Token == K.DotToken
                || question is not null
                    && Token is not (K.OpenBracketToken or K.OpenParenToken or K.LessThanToken or K.NoSubstitutionTemplateLiteral
                        or K.TemplateHead))
            {
                if (question is null)
                    Next();
                if (left is ExpressionWithTypeArgumentsNode instantiation)
                    ErrorOnNode(instantiation.TypeArguments!, Messages.An_instantiation_expression_cannot_be_followed_by_a_property_access);
                chain |= ContinueOptionalChain(left);
                if (Token == K.PrivateIdentifier && chain != 0)
                    Error(Messages.An_optional_chain_cannot_contain_private_identifiers);
                SyntaxNode name = RightOfDot(true);
                left = Finish(factory.NewPropertyAccessExpression(left, question, name, chain), start);
                continue;
            }

            if (((context & NodeFlags.DecoratorContext) == 0 || question is not null) && Take(K.OpenBracketToken))
            {
                chain |= ContinueOptionalChain(left);
                SyntaxNode argument;
                if (Token == K.CloseBracketToken)
                {
                    Error(Messages.An_element_access_expression_should_take_an_argument);
                    argument = Finish(factory.NewIdentifier(""), Pos, Pos);
                }
                else
                    argument = (await ExpressionCore().ConfigureAwait(false));
                Expected(K.CloseBracketToken);
                left = Finish(factory.NewElementAccessExpression(left, question, argument, chain), start);
                continue;
            }

            if (!LineBreak && Take(K.ExclamationToken))
            {
                left = Finish(factory.NewNonNullExpression(left, 0), start);
                continue;
            }

            NodeList? typeArguments = null;
            if ((context & NodeFlags.JavaScriptFile) == 0
                && Token is K.LessThanToken or K.LessThanLessThanToken && (await PeekCore(async () =>
            {
                scanner.RescanLessThanToken();
                if (await TypeArgumentsCore(requireClose: true).ConfigureAwait(false) is null)
                    return false;
                if (Token == K.EqualsToken && scanner.TokenStart == Pos && Pos > 0 && source.Text[Pos - 1] == '>')
                    return false;
                return Token is not (K.LessThanToken or K.GreaterThanToken or K.PlusToken or K.MinusToken)
                    && (Token is K.OpenParenToken or K.NoSubstitutionTemplateLiteral or K.TemplateHead
                        || LineBreak
                        || Precedence(Token) > 0
                        || !StartsExpression());
            }).ConfigureAwait(false)))
            {
                scanner.RescanLessThanToken();
                typeArguments = (await TypeArgumentsCore().ConfigureAwait(false));
            }

            if (calls && Token == K.OpenParenToken)
            {
                chain |= ContinueOptionalChain(left);
                if (left.Kind == K.SuperKeyword && typeArguments is not null)
                    ErrorOnNode(typeArguments, Messages.X_super_may_not_use_type_arguments);
                left = Finish(
                    factory.NewCallExpression(left, question, typeArguments, (await ArgumentsCore().ConfigureAwait(false)), chain),
                    start);
                continue;
            }

            if (Token is K.NoSubstitutionTemplateLiteral or K.TemplateHead)
            {
                if (left.Kind == K.SuperKeyword)
                {
                    if (typeArguments is not null)
                        ErrorOnNode(typeArguments, Messages.X_super_may_not_use_type_arguments);
                    Error(Messages.X_super_must_be_followed_by_an_argument_list_or_member_access);
                    left = Finish(factory.NewPropertyAccessExpression(left, null, RightOfDot(true), 0), start);
                    typeArguments = null;
                }
                left = Finish(
                    factory.NewTaggedTemplateExpression(
                        left,
                        question,
                        typeArguments,
                        (await TemplateCore(false, true).ConfigureAwait(false)),
                        chain),
                    start);
                continue;
            }

            if (typeArguments is not null)
            {
                left = Finish(factory.NewExpressionWithTypeArguments(left, typeArguments), start);
                continue;
            }

            if (left.Kind == K.SuperKeyword && !(Token == K.OpenParenToken && !calls))
            {
                Error(Messages.X_super_must_be_followed_by_an_argument_list_or_member_access);
                return Finish(factory.NewPropertyAccessExpression(left, null, RightOfDot(true), 0), start);
            }
            return left;
        }
    }

    private bool StartsExpression() =>
        IsIdentifier
            || Token is K.ThisKeyword or K.SuperKeyword or K.NullKeyword or K.TrueKeyword or K.FalseKeyword or K.NumericLiteral
                or K.BigIntLiteral or K.StringLiteral or K.NoSubstitutionTemplateLiteral or K.TemplateHead or K.OpenParenToken
                or K.OpenBracketToken or K.OpenBraceToken or K.FunctionKeyword or K.ClassKeyword or K.NewKeyword or K.SlashToken
                or K.SlashEqualsToken or K.PlusToken or K.MinusToken or K.TildeToken or K.ExclamationToken or K.DeleteKeyword
                or K.TypeOfKeyword or K.VoidKeyword or K.PlusPlusToken or K.MinusMinusToken or K.LessThanToken or K.AwaitKeyword
                or K.YieldKeyword or K.PrivateIdentifier or K.AtToken
            || Token == K.ImportKeyword && Peek(() => Next() is K.OpenParenToken or K.LessThanToken or K.DotToken)
            || Precedence(Token) >= 4 && (Token != K.InKeyword || (context & NodeFlags.DisallowInContext) == 0);

    private async ValueTask<NodeList> ArgumentsCore()
    {
        await ParseStack;
        Expected(K.OpenParenToken);
        NodeList args = await DelimitedCore(K.CloseParenToken, async () =>
        {
            if (Token == K.CommaToken)
                Error(Messages.Argument_expression_expected);
            return await ArgumentCore().ConfigureAwait(false);
        }, stop: () => Token == K.SemicolonToken,
            startsElement: () => Token is K.CommaToken or K.DotDotDotToken || StartsExpression(),
            elementExpected: Messages.Argument_expression_expected).ConfigureAwait(false);
        Expected(K.CloseParenToken);
        return args;
    }

    private async ValueTask<SyntaxNode> ArgumentCore()
    {
        await ParseStack;
        int start = Pos;
        if (Token == K.CommaToken)
            return Finish(factory.NewOmittedExpression(), start, start);
        if (Take(K.DotDotDotToken))
            return Finish(factory.NewSpreadElement((await ExpressionCore(2).ConfigureAwait(false))), start);
        return (await ExpressionCore(2).ConfigureAwait(false));
    }

    private async ValueTask<SyntaxNode> ObjectPropertyCore()
    {
        await ParseStack;
        TokenFlags trivia = scanner.Flags;
        return (await WithJSDocCore((await ObjectPropertyWorkerCore().ConfigureAwait(false)), trivia).ConfigureAwait(false));
    }

    private async ValueTask<SyntaxNode> ObjectPropertyWorkerCore()
    {
        await ParseStack;
        int start = Pos;
        if (Take(K.DotDotDotToken))
            return Finish(factory.NewSpreadAssignment((await ExpressionCore(2).ConfigureAwait(false))), start);
        if (Token == K.AtToken)
            Error(Messages.Property_assignment_expected);
        var modifiers = (await ModifiersCore().ConfigureAwait(false));
        K accessor = K.Unknown;
        if (Token is K.GetKeyword or K.SetKeyword && Peek(() =>
        {
            Next();
            return !LineBreak
                && (Token >= K.Identifier
                    || Token is K.PrivateIdentifier or K.StringLiteral or K.NumericLiteral or K.BigIntLiteral or K.OpenBracketToken);
        }))
        {
            accessor = Token;
            Next();
        }

        var star = OptionalToken(K.AsteriskToken);
        bool shorthand = IsIdentifier;
        SyntaxNode name = (await NameCore().ConfigureAwait(false));
        var postfix = Token is K.QuestionToken or K.ExclamationToken ? ParseToken() : null;
        if (star is not null || Token is K.OpenParenToken or K.LessThanToken || accessor != K.Unknown)
        {
            NodeFlags signatureFlags = (accessor == K.Unknown && star is not null
                ? NodeFlags.YieldContext
                : 0) | (accessor == K.Unknown && modifiers?.Any(m => m.Kind == K.AsyncKeyword) == true ? NodeFlags.AwaitContext : 0);
            var types = (await TypeParametersCore().ConfigureAwait(false));
            var parameters = (await ParametersCore(signatureFlags).ConfigureAwait(false));
            var type = (await ReturnAnnotationCore().ConfigureAwait(false));
            var body = (await FunctionBodyCore(signatureFlags).ConfigureAwait(false));
            return accessor switch
            {
                K.GetKeyword => Finish(factory.NewGetAccessorDeclaration(modifiers, name, types, parameters, type, null, body), start),
                K.SetKeyword => Finish(factory.NewSetAccessorDeclaration(modifiers, name, types, parameters, type, null, body), start),
                _ => Finish(factory.NewMethodDeclaration(modifiers, star, name, postfix, types, parameters, type, null, body), start),
            };
        }

        if (!shorthand || Token == K.ColonToken)
        {
            Expected(K.ColonToken);
            return Finish(
                factory.NewPropertyAssignment(
                    modifiers,
                    name,
                    postfix,
                    null,
                    await Initializer().ConfigureAwait(false)),
                start);
        }
        var equals = OptionalToken(K.EqualsToken);
        SyntaxNode? initializer = equals is null ? null : await Initializer().ConfigureAwait(false);
        return Finish(
            factory.NewShorthandPropertyAssignment(
                modifiers,
                name,
                postfix,
                null,
                equals,
                initializer),
            start);

        async ValueTask<SyntaxNode> Initializer()
        {
            var saved = context;
            context &= ~NodeFlags.DisallowInContext;
            try
            {
                return await ExpressionCore(2).ConfigureAwait(false);
            }
            finally
            {
                context = saved;
            }
        }
    }

    private async ValueTask<SyntaxNode> TemplateCore(bool type, bool tagged)
    {
        await ParseStack;
        int start = Pos;
        if (!tagged)
            scanner.RescanTemplateToken(false);
        if (Token == K.NoSubstitutionTemplateLiteral)
            return type ? Finish(factory.NewLiteralTypeNode(Literal()), start) : Literal();
        var head = (TemplateHeadNode)TemplatePart();
        int spansStart = Pos;
        var spans = new List<SyntaxNode>();
        while (true)
        {
            int spanStart = Pos;
            SyntaxNode expression = type ? (await TypeCore().ConfigureAwait(false)) : (await ExpressionCore().ConfigureAwait(false));
            if (Token != K.CloseBraceToken)
            {
                Error(Messages.X_0_expected, "}");
                var missing = Finish(factory.NewTemplateTail("", "", 0), Pos, Pos);
                spans.Add(
                    type
                        ? Finish(factory.NewTemplateLiteralTypeSpan(expression, missing), spanStart)
                        : Finish(factory.NewTemplateSpan(expression, missing), spanStart));
                break;
            }

            scanner.RescanTemplateToken(tagged);
            K end = Token;
            SyntaxNode literal = TemplatePart();
            spans.Add(
                type
                    ? Finish(factory.NewTemplateLiteralTypeSpan(expression, literal), spanStart)
                    : Finish(factory.NewTemplateSpan(expression, literal), spanStart));
            if (end != K.TemplateMiddle)
                break;
        }

        return type
            ? Finish(factory.NewTemplateLiteralTypeNode(head, new(spans.ToArray(), spansStart, Pos)), start)
            : Finish(factory.NewTemplateExpression(head, new(spans.ToArray(), spansStart, Pos)), start);
    }

    private SyntaxNode TemplatePart()
    {
        int start = Pos;
        string value = scanner.Value;
        string raw = scanner.TokenText.ToString();
        int suffix = Token == K.TemplateTail ? 1 : 2;
        raw = raw.Length > suffix ? raw[1..^suffix] : "";
        TokenFlags flags = scanner.Flags & TokenFlags.TemplateLiteralLikeFlags;
        SyntaxNode node = Token switch
        {
            K.TemplateHead => factory.NewTemplateHead(value, raw, flags),
            K.TemplateMiddle => factory.NewTemplateMiddle(value, raw, flags),
            _ => factory.NewTemplateTail(value, raw, flags)
        };
        Next();
        return Finish(node, start);
    }
}

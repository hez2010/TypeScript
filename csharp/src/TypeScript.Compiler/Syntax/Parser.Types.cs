using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    private async ValueTask<SyntaxNode> TypeCore()
    {
        await ParseStack;
        NodeFlags saved = context;
        context &= ~NodeFlags.TypeExcludesFlags;
        try
        {
            if (StartsFunctionType())
                return await PrimaryTypeCore().ConfigureAwait(false);
            int start = Pos;
            SyntaxNode left = await UnionTypeCore(false).ConfigureAwait(false);
            if (!LineBreak && (context & NodeFlags.DisallowConditionalTypesContext) == 0 && Take(K.ExtendsKeyword))
            {
                context |= NodeFlags.DisallowConditionalTypesContext;
                SyntaxNode extends = await TypeCore().ConfigureAwait(false);
                context &= ~NodeFlags.DisallowConditionalTypesContext;
                Expected(K.QuestionToken);
                SyntaxNode whenTrue = await TypeCore().ConfigureAwait(false);
                Expected(K.ColonToken);
                SyntaxNode whenFalse = await TypeCore().ConfigureAwait(false);
                return Finish(factory.NewConditionalTypeNode(left, extends, whenTrue, whenFalse), start);
            }
            return left;
        }
        finally
        {
            context = saved;
        }
    }

    private async ValueTask<SyntaxNode> TypeOrPredicateCore()
    {
        await ParseStack;
        if (IsIdentifier && Peek(() =>
        {
            Next();
            return !LineBreak && Token == K.IsKeyword;
        }))
        {
            int start = Pos;
            var name = Identifier();
            Next();
            return Finish(factory.NewTypePredicateNode(null, name, await TypeCore().ConfigureAwait(false)), start);
        }
        return await TypeCore().ConfigureAwait(false);
    }

    private async ValueTask<SyntaxNode> UnionTypeCore(bool intersection)
    {
        await ParseStack;
        int start = Pos;
        K separator = intersection ? K.AmpersandToken : K.BarToken;
        bool leading = Take(separator);
        SyntaxNode first = await UnionConstituentCore(intersection, leading).ConfigureAwait(false);
        if (!leading && Token != separator)
            return first;
        var types = new List<SyntaxNode> { first };
        while (Take(separator))
            types.Add(await UnionConstituentCore(intersection, true).ConfigureAwait(false));
        NodeList list = new(types.ToArray(), start, Pos);
        return intersection ? Finish(factory.NewIntersectionTypeNode(list), start) : Finish(factory.NewUnionTypeNode(list), start);
    }

    private async ValueTask<SyntaxNode> UnionConstituentCore(bool intersection, bool reportFunction)
    {
        await ParseStack;
        if (reportFunction && StartsFunctionType())
        {
            SyntaxNode type = await PrimaryTypeCore().ConfigureAwait(false);
            DiagnosticMessage diagnostic = (type.Kind == K.FunctionType, intersection) switch
            {
                (true, false) => Messages.Function_type_notation_must_be_parenthesized_when_used_in_a_union_type,
                (true, true) => Messages.Function_type_notation_must_be_parenthesized_when_used_in_an_intersection_type,
                (false, false) => Messages.Constructor_type_notation_must_be_parenthesized_when_used_in_a_union_type,
                _ => Messages.Constructor_type_notation_must_be_parenthesized_when_used_in_an_intersection_type,
            };
            ErrorAt(diagnostic, type.Pos, type.End - type.Pos);
            return type;
        }
        return intersection ? await PostfixTypeCore().ConfigureAwait(false) : await UnionTypeCore(true).ConfigureAwait(false);
    }

    private async ValueTask<SyntaxNode> PostfixTypeCore()
    {
        await ParseStack;
        NodeFlags saved = context;
        if (Token is not (K.KeyOfKeyword or K.ReadonlyKeyword or K.UniqueKeyword or K.InferKeyword))
            context &= ~NodeFlags.DisallowConditionalTypesContext;
        try
        {
            int start = Pos;
            SyntaxNode type = await PrimaryTypeCore().ConfigureAwait(false);
            while (!LineBreak)
            {
                if (Token == K.ExclamationToken)
                {
                    Next();
                    type = Finish(factory.NewJSDocNonNullableType(type), start);
                    continue;
                }
                if (Token == K.QuestionToken)
                {
                    if (Peek(() =>
                    {
                        Next();
                        return StartsType();
                    }))
                        break;
                    Next();
                    type = Finish(factory.NewJSDocNullableType(type), start);
                    continue;
                }
                if (!Take(K.OpenBracketToken))
                    break;
                if (StartsType())
                {
                    SyntaxNode index = await TypeCore().ConfigureAwait(false);
                    Expected(K.CloseBracketToken);
                    type = Finish(factory.NewIndexedAccessTypeNode(type, index), start);
                }
                else
                {
                    Expected(K.CloseBracketToken);
                    type = Finish(factory.NewArrayTypeNode(type), start);
                }
            }
            return type;
        }
        finally
        {
            context = saved;
        }
    }

    private async ValueTask<SyntaxNode> PrimaryTypeCore()
    {
        await ParseStack;
        int start = Pos;
        if (Token == K.AsteriskEqualsToken)
        {
            scanner.RescanAsteriskEqualsToken();
            Next();
            return Finish(factory.NewJSDocAllType(), start);
        }
        if (Token == K.QuestionQuestionToken)
            scanner.RescanQuestionToken();
        switch (Token)
        {
            case K.AnyKeyword:
            case K.UnknownKeyword:
            case K.StringKeyword:
            case K.NumberKeyword:
            case K.BigIntKeyword:
            case K.BooleanKeyword:
            case K.SymbolKeyword:
            case K.UndefinedKeyword:
            case K.NeverKeyword:
            case K.ObjectKeyword:
                if (NextIs(K.DotToken))
                    break;
                var keyword = factory.NewKeywordTypeNode(Token);
                Next();
                return Finish(keyword, start);
            case K.VoidKeyword:
                Next();
                return Finish(factory.NewKeywordTypeNode(K.VoidKeyword), start);
            case K.ThisKeyword:
                Next();
                var thisType = Finish(factory.NewThisTypeNode(), start);
                if (!LineBreak && Take(K.IsKeyword))
                    return Finish(factory.NewTypePredicateNode(null, thisType, await TypeCore().ConfigureAwait(false)), start);
                return thisType;
            case K.AssertsKeyword:
                if (!Peek(() =>
                {
                    Next();
                    return !LineBreak && (IsIdentifier || Token == K.ThisKeyword);
                }))
                    break;
                var asserts = ParseToken();
                SyntaxNode parameter = Token == K.ThisKeyword ? Finish(factory.NewThisTypeNode(), Pos, scanner.Position) : Identifier();
                if (Token == K.ThisKeyword)
                    Next();
                return Finish(
                    factory.NewTypePredicateNode(asserts, parameter, Take(K.IsKeyword) ? await TypeCore().ConfigureAwait(false) : null),
                    start);
            case K.KeyOfKeyword:
            case K.ReadonlyKeyword:
            case K.UniqueKeyword:
                K op = Token;
                Next();
                return Finish(factory.NewTypeOperatorNode(op, await PostfixTypeCore().ConfigureAwait(false)), start);
            case K.InferKeyword:
                Next();
                int paramStart = Pos;
                var id = Identifier();
                SyntaxNode? constraint = null;
                if (Token == K.ExtendsKeyword)
                {
                    var state = scanner.Mark();
                    int count = diagnostics.Count, scanned = scannedDiagnostics;
                    bool error = hasError;
                    NodeFlags flags = context, fileFlags = sourceFlags;
                    Next();
                    context |= NodeFlags.DisallowConditionalTypesContext;
                    constraint = await TypeCore().ConfigureAwait(false);
                    context = flags;
                    if ((context & NodeFlags.DisallowConditionalTypesContext) == 0 && Token == K.QuestionToken)
                    {
                        scanner.Rewind(state);
                        diagnostics.RemoveRange(count, diagnostics.Count - count);
                        scannedDiagnostics = scanned;
                        hasError = error;
                        context = flags;
                        sourceFlags = fileFlags;
                        constraint = null;
                    }
                }
                var inferParameter = Finish(factory.NewTypeParameterDeclaration(null, id, constraint, null, null), paramStart);
                return Finish(factory.NewInferTypeNode(inferParameter), start);
            case K.TypeOfKeyword:
                Next();
                if (Token == K.ImportKeyword)
                    return await ImportTypeCore(start, true).ConfigureAwait(false);
                return Finish(factory.NewTypeQueryNode(EntityName(true), await TypeArgumentsCore(false).ConfigureAwait(false)), start);
            case K.ImportKeyword:
                return await ImportTypeCore(start, false).ConfigureAwait(false);
            case K.StringLiteral:
            case K.NumericLiteral:
            case K.BigIntLiteral:
            case K.NoSubstitutionTemplateLiteral:
                return Finish(factory.NewLiteralTypeNode(Literal()), start);
            case K.TrueKeyword:
            case K.FalseKeyword:
            case K.NullKeyword:
                var literal = factory.NewKeywordExpression(Token);
                Next();
                return Finish(factory.NewLiteralTypeNode(Finish(literal, start)), start);
            case K.MinusToken:
                if (!Peek(() => Next() is K.NumericLiteral or K.BigIntLiteral))
                    break;
                Next();
                return Finish(factory.NewLiteralTypeNode(Finish(factory.NewPrefixUnaryExpression(K.MinusToken, Literal()), start)), start);
            case K.TemplateHead:
                return await TemplateCore(true, false).ConfigureAwait(false);
            case K.LessThanToken:
                return await FunctionTypeCore(false, null, start).ConfigureAwait(false);
            case K.AbstractKeyword:
                if (!NextIs(K.NewKeyword))
                    break;
                var modifier = ParseToken();
                Expected(K.NewKeyword);
                return await FunctionTypeCore(true, new([modifier], start, modifier.End), start).ConfigureAwait(false);
            case K.NewKeyword:
                Next();
                return await FunctionTypeCore(true, null, start).ConfigureAwait(false);
            case K.OpenParenToken:
                if (IsFunctionType())
                    return await FunctionTypeCore(false, null, start).ConfigureAwait(false);
                Next();
                SyntaxNode inner = await TypeCore().ConfigureAwait(false);
                Expected(K.CloseParenToken);
                return Finish(factory.NewParenthesizedTypeNode(inner), start);
            case K.OpenBracketToken:
                Next();
                NodeList elements = await DelimitedCore(K.CloseBracketToken, TupleElementCore).ConfigureAwait(false);
                Expected(K.CloseBracketToken);
                return Finish(factory.NewTupleTypeNode(elements), start);
            case K.OpenBraceToken:
                if (IsMappedType())
                    return await MappedTypeCore().ConfigureAwait(false);
                Next();
                NodeList members = await ListCore(
                    K.CloseBraceToken,
                    () => TypeMemberCore(false),
                    stop: () => !Peek(ScanTypeMemberStart)).ConfigureAwait(false);
                Expected(K.CloseBraceToken);
                return Finish(factory.NewTypeLiteralNode(members), start);
            case K.AsteriskToken:
                Next();
                return Finish(factory.NewJSDocAllType(), start);
            case K.QuestionToken:
                Next();
                return Finish(factory.NewJSDocNullableType(await PostfixTypeCore().ConfigureAwait(false)), start);
            case K.ExclamationToken:
                Next();
                return Finish(factory.NewJSDocNonNullableType(await PostfixTypeCore().ConfigureAwait(false)), start);
            case K.DotDotDotToken:
                Next();
                return Finish(factory.NewJSDocVariadicType(await TypeCore().ConfigureAwait(false)), start);
        }
        if (Token != K.Identifier && Token is not (>= K.FirstKeyword and <= K.LastKeyword))
        {
            Error(Messages.Type_expected);
            return Finish(factory.NewTypeReferenceNode(Finish(factory.NewIdentifier(""), Pos, Pos), null), start, start);
        }
        SyntaxNode name = EntityName();
        return Finish(factory.NewTypeReferenceNode(name, await TypeArgumentsCore().ConfigureAwait(false)), start);
    }

    private bool StartsType() =>
        IsIdentifier
        || Token is K.AnyKeyword or K.UnknownKeyword or K.StringKeyword or K.NumberKeyword or K.BigIntKeyword
            or K.BooleanKeyword or K.VoidKeyword or K.UndefinedKeyword or K.NeverKeyword or K.ObjectKeyword
            or K.TypeOfKeyword or K.ThisKeyword
            or K.OpenBraceToken or K.OpenBracketToken or K.OpenParenToken or K.LessThanToken or K.BarToken or K.AmpersandToken
            or K.NewKeyword or K.ImportKeyword or K.StringLiteral or K.NumericLiteral or K.BigIntLiteral
            or K.TrueKeyword or K.FalseKeyword or K.NullKeyword
            or K.QuestionToken or K.ExclamationToken or K.AsteriskToken or K.DotDotDotToken
            or K.TemplateHead or K.NoSubstitutionTemplateLiteral
        || Token == K.MinusToken && Peek(() => Next() is K.NumericLiteral or K.BigIntLiteral);

    private bool IsFunctionType() => Peek(() =>
        {
            if (!Take(K.OpenParenToken))
                return false;
            if (Token is K.CloseParenToken or K.DotDotDotToken)
                return true;
            // A parenthesized type beginning with another '(' is not a parameter.
            // Inspect just the parameter start, rather than rescanning each enclosing
            // parenthesis to its matching close on deeply nested type expressions.
            while (IsModifierKind(Token) && Peek(() =>
            {
                Next();
                return !LineBreak && (IsIdentifier || Token is K.ThisKeyword or K.OpenBracketToken or K.OpenBraceToken or K.DotDotDotToken);
            }))
                Next();
            if (Take(K.DotDotDotToken))
                return true;
            if (IsIdentifier || Token == K.ThisKeyword)
                Next();
            else if (Token is K.OpenBracketToken or K.OpenBraceToken)
            {
                var close = new Stack<K>();
                do
                {
                    if (Token == K.OpenBracketToken)
                        close.Push(K.CloseBracketToken);
                    else if (Token == K.OpenBraceToken)
                        close.Push(K.CloseBraceToken);
                    else if (Token is K.CloseBracketToken or K.CloseBraceToken)
                    {
                        if (!close.TryPop(out K expected) || Token != expected)
                            return false;
                    }
                    Next();
                } while (close.Count != 0 && Token != K.EndOfFile);
                if (close.Count != 0)
                    return false;
            }
            else
                return false;
            return Token is K.ColonToken or K.CommaToken or K.QuestionToken or K.EqualsToken
                || Token == K.CloseParenToken && Next() == K.EqualsGreaterThanToken;
        });

    private bool StartsFunctionType() => Token is K.LessThanToken or K.NewKeyword
            || Token == K.AbstractKeyword && NextIs(K.NewKeyword) || Token == K.OpenParenToken && IsFunctionType();

    private async ValueTask<NodeList?> TypeArgumentsCore(bool rescan = true)
    {
        await ParseStack;
        if (LineBreak || (rescan ? scanner.RescanLessThanToken() : Token) != K.LessThanToken)
            return null;
        Next();
        NodeList args = await DelimitedCore(K.GreaterThanToken, TypeCore).ConfigureAwait(false);
        Expected(K.GreaterThanToken);
        return args;
    }

    private bool IsIndexSignature() => Token == K.OpenBracketToken && Peek(() =>
        {
            Next();
            if (Token is K.DotDotDotToken or K.CloseBracketToken)
                return true;
            if (IsModifierKind(Token))
            {
                Next();
                if (IsIdentifier)
                    return true;
            }
            else if (!IsIdentifier)
                return false;
            else
                Next();
            if (Token is K.ColonToken or K.CommaToken)
                return true;
            if (!Take(K.QuestionToken))
                return false;
            return Token is K.ColonToken or K.CommaToken or K.CloseBracketToken;
        });

    private async ValueTask<SyntaxNode> IndexSignatureCore(NodeList? modifiers, int start)
    {
        await ParseStack;
        Expected(K.OpenBracketToken);
        NodeList parameters = await DelimitedCore(K.CloseBracketToken, () => ParameterCore(),
            startsElement: StartsParameter, reportInvalidElement: ParameterExpected).ConfigureAwait(false);
        Expected(K.CloseBracketToken);
        SyntaxNode? type = await AnnotationCore().ConfigureAwait(false);
        MemberSemicolon();
        return Finish(factory.NewIndexSignatureDeclaration(modifiers, parameters, type), start);
    }

    private async ValueTask<SyntaxNode> HeritageTypeCore(bool typeReference)
    {
        await ParseStack;
        int start = Pos;
        SyntaxNode expression = await MemberExpressionCore(await PrimaryExpressionCore().ConfigureAwait(false), true).ConfigureAwait(false);
        var heritage = expression as ExpressionWithTypeArgumentsNode
            ?? Finish(factory.NewExpressionWithTypeArguments(expression, await TypeArgumentsCore(false).ConfigureAwait(false)), start);
        if (!typeReference || heritage.Expression is null)
            return heritage;
        var accesses = new Stack<PropertyAccessExpressionNode>();
        SyntaxNode name = heritage.Expression;
        while (name is PropertyAccessExpressionNode { Expression: { } left, Name: IdentifierNode right } access
            && (access.Flags & NodeFlags.OptionalChain) == 0 && right.End > right.Pos)
        {
            accesses.Push(access);
            name = left;
        }
        if (name is not IdentifierNode || name.End == name.Pos)
            return heritage;
        while (accesses.TryPop(out PropertyAccessExpressionNode? access))
            name = Finish(factory.NewQualifiedName(name, (IdentifierNode)access.Name!), access.Pos, access.End);
        return Finish(factory.NewTypeReferenceNode(name, heritage.TypeArguments), start);
    }

    private async ValueTask<NodeList?> TypeParametersCore()
    {
        await ParseStack;
        if (!Take(K.LessThanToken))
            return null;
        NodeList parameters = await DelimitedCore(K.GreaterThanToken, TypeParameterCore,
            stop: () => Token is K.OpenParenToken or K.OpenBraceToken or K.ExtendsKeyword,
            startsElement: () => IsIdentifier || Token is K.InKeyword or K.ConstKeyword,
            elementExpected: Messages.Type_parameter_declaration_expected).ConfigureAwait(false);
        Expected(K.GreaterThanToken);
        return parameters;
    }

    private async ValueTask<SyntaxNode> TypeParameterCore()
    {
        await ParseStack;
        int start = Pos;
        var modifiers = await ModifiersCore(true).ConfigureAwait(false);
        var name = Identifier();
        SyntaxNode? constraint = Take(K.ExtendsKeyword) ? await TypeCore().ConfigureAwait(false) : null;
        SyntaxNode? defaultType = Take(K.EqualsToken) ? await TypeCore().ConfigureAwait(false) : null;
        return Finish(factory.NewTypeParameterDeclaration(modifiers, name, constraint, null, defaultType), start);
    }

    private async ValueTask<NodeList> ParametersCore(NodeFlags signatureFlags = 0)
    {
        await ParseStack;
        NodeFlags saved = context;
        context = (context & ~(NodeFlags.YieldContext | NodeFlags.AwaitContext)) | signatureFlags;
        try
        {
            if (!Expected(K.OpenParenToken))
                return new([], Pos, Pos, true);
            NodeList list = await DelimitedCore(
                K.CloseParenToken,
                () => ParameterCore(saved & NodeFlags.AwaitContext), stop: () => Token == K.CloseBracketToken,
                startsElement: StartsParameter, reportInvalidElement: ParameterExpected).ConfigureAwait(false);
            Expected(K.CloseParenToken);
            return list;
        }
        finally
        {
            context = saved;
        }
    }

    private bool StartsParameter() => Token is K.DotDotDotToken or K.OpenBraceToken or K.OpenBracketToken or K.PrivateIdentifier
        or K.AtToken
        || IsBindingIdentifier || IsModifierKind(Token)
        || Token is not (K.FunctionKeyword or K.MinusToken or K.OpenParenToken) && StartsType();

    private void ParameterExpected()
    {
        if (Token is >= K.FirstKeyword and <= K.LastKeyword)
            Error(Messages.X_0_is_not_allowed_as_a_parameter_name, TokenFacts.Text(Token));
        else
            Error(Messages.Parameter_declaration_expected);
    }

    private async ValueTask<SyntaxNode> ParameterCore(NodeFlags outerAwait = 0)
    {
        await ParseStack;
        int start = Pos;
        TokenFlags trivia = scanner.Flags;
        NodeFlags saved = context;
        context = (context & ~NodeFlags.AwaitContext) | outerAwait;
        var modifiers = await ModifiersCore().ConfigureAwait(false);
        context = saved;
        if (Token == K.ThisKeyword)
        {
            var thisName = Identifier(true);
            SyntaxNode? type = await AnnotationCore().ConfigureAwait(false);
            if (modifiers is { Count: > 0 })
                ErrorAt(
                    Messages.Neither_decorators_nor_modifiers_may_be_applied_to_this_parameters,
                    modifiers[0].Pos,
                    modifiers[0].End - modifiers[0].Pos);
            return await WithJSDocCore(
                Finish(factory.NewParameterDeclaration(modifiers, null, thisName, null, type, null), start),
                trivia).ConfigureAwait(false);
        }
        var rest = OptionalToken(K.DotDotDotToken);
        SyntaxNode name = await BindingNameCore(Messages.Private_identifiers_cannot_be_used_as_parameters).ConfigureAwait(false);
        return await WithJSDocCore(
            Finish(
                factory.NewParameterDeclaration(
                    modifiers,
                    rest,
                    name,
                    OptionalToken(K.QuestionToken),
                    await AnnotationCore().ConfigureAwait(false),
                    await InitializerCore().ConfigureAwait(false)),
                start),
            trivia).ConfigureAwait(false);
    }

    private async ValueTask<SyntaxNode> FunctionTypeCore(bool constructor, NodeList? modifiers, int start)
    {
        await ParseStack;
        TokenFlags trivia = scanner.Flags;
        NodeList? types = await TypeParametersCore().ConfigureAwait(false);
        NodeList parameters = await ParametersCore().ConfigureAwait(false);
        Expected(K.EqualsGreaterThanToken);
        NodeFlags saved = context;
        context &= ~NodeFlags.DisallowConditionalTypesContext;
        SyntaxNode result = await TypeOrPredicateCore().ConfigureAwait(false);
        context = saved;
        SyntaxNode node = constructor
            ? Finish(factory.NewConstructorTypeNode(modifiers, types, parameters, result), start)
            : Finish(factory.NewFunctionTypeNode(types, parameters, result), start);
        return await WithJSDocCore(node, trivia).ConfigureAwait(false);
    }

    private async ValueTask<SyntaxNode> TupleElementCore()
    {
        await ParseStack;
        int start = Pos;
        TokenFlags trivia = scanner.Flags;
        var rest = OptionalToken(K.DotDotDotToken);
        if ((IsIdentifier || Token is >= K.FirstKeyword and <= K.LastKeyword) && Peek(() =>
        {
            Next();
            Take(K.QuestionToken);
            return Token == K.ColonToken;
        }))
        {
            var name = Identifier(true);
            var optional = OptionalToken(K.QuestionToken);
            Expected(K.ColonToken);
            return await WithJSDocCore(
                Finish(factory.NewNamedTupleMember(rest, name, optional, TupleElementType(await TypeCore().ConfigureAwait(false))), start),
                trivia).ConfigureAwait(false);
        }
        SyntaxNode type = await TypeCore().ConfigureAwait(false);
        if (rest is not null)
            return Finish(factory.NewRestTypeNode(type), start);
        return TupleElementType(type);
    }

    private SyntaxNode TupleElementType(SyntaxNode type)
    {
        if (type is JSDocVariadicTypeNode { Type: { } restElement } variadic)
        {
            var rest = factory.NewRestTypeNode(restElement);
            rest.Flags = variadic.Flags;
            rest.Pos = variadic.Pos;
            rest.End = variadic.End;
            restElement.Parent = rest;
            return rest;
        }
        if (type is JSDocNullableTypeNode { Type: { } element } nullable && nullable.Pos == element.Pos)
        {
            var optional = factory.NewOptionalTypeNode(element);
            optional.Flags = nullable.Flags;
            optional.Pos = nullable.Pos;
            optional.End = nullable.End;
            element.Parent = optional;
            return optional;
        }
        return type;
    }

    private bool IsMappedType() => Peek(() =>
        {
            Next();
            if (Token is K.PlusToken or K.MinusToken)
                Next();
            Take(K.ReadonlyKeyword);
            if (!Take(K.OpenBracketToken) || !IsIdentifier)
                return false;
            Next();
            return Token == K.InKeyword;
        });

    private async ValueTask<SyntaxNode> MappedTypeCore()
    {
        await ParseStack;
        int start = Pos;
        Next();
        TokenNode? readOnly = null;
        if (Token is K.PlusToken or K.MinusToken)
        {
            readOnly = ParseToken();
            Expected(K.ReadonlyKeyword);
        }
        else
            readOnly = OptionalToken(K.ReadonlyKeyword);
        Expected(K.OpenBracketToken);
        int parameterStart = Pos;
        var name = Identifier();
        Expected(K.InKeyword);
        var parameter = Finish(
            factory.NewTypeParameterDeclaration(null, name, await TypeCore().ConfigureAwait(false), null, null),
            parameterStart);
        SyntaxNode? nameType = Take(K.AsKeyword) ? await TypeCore().ConfigureAwait(false) : null;
        Expected(K.CloseBracketToken);
        TokenNode? question = null;
        if (Token is K.PlusToken or K.MinusToken)
        {
            question = ParseToken();
            Expected(K.QuestionToken);
        }
        else
            question = OptionalToken(K.QuestionToken);
        SyntaxNode? type = await AnnotationCore().ConfigureAwait(false);
        Semicolon();
        NodeList members = await ListCore(
            K.CloseBraceToken,
            () => TypeMemberCore(false),
            stop: () => !Peek(ScanTypeMemberStart)).ConfigureAwait(false);
        Expected(K.CloseBraceToken);
        return Finish(factory.NewMappedTypeNode(readOnly, parameter, nameType, question, type, members), start);
    }

    private async ValueTask<SyntaxNode> ImportTypeCore(int start, bool query)
    {
        await ParseStack;
        sourceFlags |= NodeFlags.PossiblyContainsDynamicImport;
        Expected(K.ImportKeyword);
        Expected(K.OpenParenToken);
        SyntaxNode argument = await TypeCore().ConfigureAwait(false);
        ImportAttributesNode? attributes = null;
        if (Take(K.CommaToken))
        {
            Expected(K.OpenBraceToken);
            K token = Token;
            if (token == K.AssertKeyword)
                Error(Messages.Import_assertions_have_been_replaced_by_import_attributes_Use_with_instead_of_assert);
            if (token is K.WithKeyword or K.AssertKeyword)
                Next();
            else
                Error(Messages.X_0_expected, "with");
            Expected(K.ColonToken);
            attributes = await ImportAttributesCore(token).ConfigureAwait(false);
            Take(K.CommaToken);
            Expected(K.CloseBraceToken);
        }
        Expected(K.CloseParenToken);
        SyntaxNode? qualifier = Take(K.DotToken) ? EntityName() : null;
        return Finish(
            factory.NewImportTypeNode(query, argument, attributes, qualifier, await TypeArgumentsCore().ConfigureAwait(false)),
            start);
    }
}

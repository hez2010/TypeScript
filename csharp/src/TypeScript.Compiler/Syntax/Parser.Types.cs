using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    private SyntaxNode Type()
    {
        NodeFlags saved = context; context &= ~NodeFlags.TypeExcludesFlags;
        try
        {
            if (Token is K.LessThanToken or K.NewKeyword || Token == K.AbstractKeyword && NextIs(K.NewKeyword) || Token == K.OpenParenToken && IsFunctionType()) return PrimaryType();
            int start = Pos;
            SyntaxNode left = UnionType(false);
            if (!LineBreak && (context & NodeFlags.DisallowConditionalTypesContext) == 0 && Take(K.ExtendsKeyword))
            {
                context |= NodeFlags.DisallowConditionalTypesContext;
                SyntaxNode extends = Type();
                context &= ~NodeFlags.DisallowConditionalTypesContext;
                Expected(K.QuestionToken); SyntaxNode whenTrue = Type(); Expected(K.ColonToken); SyntaxNode whenFalse = Type();
                return Finish(factory.NewConditionalTypeNode(left, extends, whenTrue, whenFalse), start);
            }
            return left;
        }
        finally { context = saved; }
    }
    private SyntaxNode UnionType(bool intersection)
    {
        int start = Pos; K separator = intersection ? K.AmpersandToken : K.BarToken;
        bool leading = Take(separator);
        SyntaxNode first = intersection ? PostfixType() : UnionType(true);
        if (!leading && Token != separator) return first;
        var types = new List<SyntaxNode> { first };
        while (Take(separator)) types.Add(intersection ? PostfixType() : UnionType(true));
        NodeList list = new(types.ToArray(), start, Pos);
        return intersection ? Finish(factory.NewIntersectionTypeNode(list), start) : Finish(factory.NewUnionTypeNode(list), start);
    }
    private SyntaxNode PostfixType()
    {
        NodeFlags saved = context;
        if (Token is not (K.KeyOfKeyword or K.ReadonlyKeyword or K.UniqueKeyword or K.InferKeyword)) context &= ~NodeFlags.DisallowConditionalTypesContext;
        try
        {
            int start = Pos;
            SyntaxNode type = PrimaryType();
            while (!LineBreak)
            {
                if ((context & NodeFlags.JSDoc) != 0 && Token == K.ExclamationToken)
                { Next(); type = Finish(factory.NewJSDocNonNullableType(type), start); continue; }
                if ((context & NodeFlags.JSDoc) != 0 && Token == K.QuestionToken)
                {
                    if (Peek(() => { Next(); return StartsType(); })) break;
                    Next(); type = Finish(factory.NewJSDocNullableType(type), start); continue;
                }
                if (!Take(K.OpenBracketToken)) break;
                if (Take(K.CloseBracketToken)) type = Finish(factory.NewArrayTypeNode(type), start);
                else { SyntaxNode index = Type(); Expected(K.CloseBracketToken); type = Finish(factory.NewIndexedAccessTypeNode(type, index), start); }
            }
            return type;
        }
        finally { context = saved; }
    }
    private SyntaxNode PrimaryType()
    {
        int start = Pos;
        if (Token == K.AsteriskEqualsToken) { scanner.RescanAsteriskEqualsToken(); Next(); return Finish(factory.NewJSDocAllType(), start); }
        if (Token == K.QuestionQuestionToken) scanner.RescanQuestionToken();
        switch (Token)
        {
            case K.AnyKeyword:
            case K.UnknownKeyword:
            case K.StringKeyword:
            case K.NumberKeyword:
            case K.BigIntKeyword:
            case K.BooleanKeyword:
            case K.SymbolKeyword:
            case K.VoidKeyword:
            case K.UndefinedKeyword:
            case K.NeverKeyword:
            case K.ObjectKeyword:
            case K.IntrinsicKeyword:
                if (NextIs(K.DotToken)) break;
                var keyword = factory.NewKeywordTypeNode(Token); Next(); return Finish(keyword, start);
            case K.ThisKeyword:
                Next(); var thisType = Finish(factory.NewThisTypeNode(), start);
                if (!LineBreak && Take(K.IsKeyword)) return Finish(factory.NewTypePredicateNode(null, thisType, Type()), start);
                return thisType;
            case K.AssertsKeyword:
                if (!Peek(() => { Next(); return !LineBreak && (IsIdentifier || Token == K.ThisKeyword); })) break;
                var asserts = ParseToken();
                SyntaxNode parameter = Token == K.ThisKeyword ? Finish(factory.NewThisTypeNode(), Pos, scanner.Position) : Identifier();
                if (Token == K.ThisKeyword) Next();
                return Finish(factory.NewTypePredicateNode(asserts, parameter, Take(K.IsKeyword) ? Type() : null), start);
            case K.KeyOfKeyword:
            case K.ReadonlyKeyword:
            case K.UniqueKeyword:
                K op = Token; Next(); return Finish(factory.NewTypeOperatorNode(op, PostfixType()), start);
            case K.InferKeyword:
                Next(); int paramStart = Pos; var id = Identifier(); SyntaxNode? constraint = null;
                if (Take(K.ExtendsKeyword)) constraint = PostfixType();
                var inferParameter = Finish(factory.NewTypeParameterDeclaration(null, id, constraint, null, null), paramStart);
                return Finish(factory.NewInferTypeNode(inferParameter), start);
            case K.TypeOfKeyword:
                Next();
                if (Token == K.ImportKeyword) return ImportType(start, true);
                return Finish(factory.NewTypeQueryNode(EntityName(), TypeArguments()), start);
            case K.ImportKeyword: return ImportType(start, false);
            case K.StringLiteral:
            case K.NumericLiteral:
            case K.BigIntLiteral:
            case K.NoSubstitutionTemplateLiteral:
                return Finish(factory.NewLiteralTypeNode(Literal()), start);
            case K.TrueKeyword:
            case K.FalseKeyword:
            case K.NullKeyword:
                var literal = factory.NewKeywordExpression(Token); Next();
                return Finish(factory.NewLiteralTypeNode(Finish(literal, start)), start);
            case K.MinusToken:
                if (!Peek(() => Next() is K.NumericLiteral or K.BigIntLiteral)) break;
                Next(); return Finish(factory.NewLiteralTypeNode(Finish(factory.NewPrefixUnaryExpression(K.MinusToken, Literal()), start)), start);
            case K.TemplateHead: return Template(true, false);
            case K.LessThanToken: return FunctionType(false, null, start);
            case K.AbstractKeyword:
                if (!NextIs(K.NewKeyword)) break;
                var modifier = ParseToken(); Expected(K.NewKeyword);
                return FunctionType(true, new([modifier], start, Pos), start);
            case K.NewKeyword:
                Next(); return FunctionType(true, null, start);
            case K.OpenParenToken:
                if (IsFunctionType()) return FunctionType(false, null, start);
                // Parentheses are a common adversarial nesting input; fold the run iteratively.
                var starts = new Stack<int>();
                do { starts.Push(Pos); Next(); } while (Token == K.OpenParenToken && !IsFunctionType());
                SyntaxNode inner = Type();
                while (starts.TryPop(out int open)) { Expected(K.CloseParenToken); inner = Finish(factory.NewParenthesizedTypeNode(inner), open); }
                return inner;
            case K.OpenBracketToken:
                Next(); NodeList elements = Delimited(K.CloseBracketToken, TupleElement); Expected(K.CloseBracketToken);
                return Finish(factory.NewTupleTypeNode(elements), start);
            case K.OpenBraceToken:
                if (IsMappedType()) return MappedType();
                Next(); NodeList members = List(K.CloseBraceToken, () => TypeMember(false)); Expected(K.CloseBraceToken);
                return Finish(factory.NewTypeLiteralNode(members), start);
            case K.AsteriskToken:
                Next(); return Finish(factory.NewJSDocAllType(), start);
            case K.QuestionToken:
                Next(); return Finish(factory.NewJSDocNullableType(PostfixType()), start);
            case K.ExclamationToken:
                Next(); return Finish(factory.NewJSDocNonNullableType(PostfixType()), start);
            case K.DotDotDotToken:
                Next(); return Finish(factory.NewJSDocVariadicType(Type()), start);
        }
        if (Token != K.Identifier && Token is not (>= K.FirstKeyword and <= K.LastKeyword))
        { Error(Messages.Type_expected); return Finish(factory.NewTypeReferenceNode(Finish(factory.NewIdentifier(""), Pos, Pos), null), start, start); }
        SyntaxNode name = EntityName();
        if (name is IdentifierNode && !LineBreak && Take(K.IsKeyword)) return Finish(factory.NewTypePredicateNode(null, name, Type()), start);
        return Finish(factory.NewTypeReferenceNode(name, TypeArguments()), start);
    }
    private bool StartsType() => IsIdentifier || Token is K.AnyKeyword or K.UnknownKeyword or K.StringKeyword or K.NumberKeyword or K.BigIntKeyword
        or K.BooleanKeyword or K.VoidKeyword or K.UndefinedKeyword or K.NeverKeyword or K.ObjectKeyword or K.TypeOfKeyword or K.ThisKeyword
        or K.OpenBraceToken or K.OpenBracketToken or K.OpenParenToken or K.LessThanToken or K.BarToken or K.AmpersandToken
        or K.NewKeyword or K.StringLiteral or K.NumericLiteral or K.BigIntLiteral or K.TrueKeyword or K.FalseKeyword or K.NullKeyword
        or K.QuestionToken or K.ExclamationToken or K.AsteriskToken or K.DotDotDotToken or K.TemplateHead or K.NoSubstitutionTemplateLiteral;
    private bool IsFunctionType() => Peek(() =>
    {
        if (Token != K.OpenParenToken) return false;
        int depth = 0;
        do
        {
            if (Token == K.OpenParenToken) depth++;
            else if (Token == K.CloseParenToken) depth--;
            Next();
        } while (depth > 0 && Token != K.EndOfFile);
        return Token == K.EqualsGreaterThanToken;
    });
    private NodeList? TypeArguments()
    {
        if (LineBreak || !Take(K.LessThanToken)) return null;
        NodeList args = Delimited(K.GreaterThanToken, Type); Expected(K.GreaterThanToken); return args;
    }
    private NodeList? TypeParameters()
    {
        if (!Take(K.LessThanToken)) return null;
        NodeList parameters = Delimited(K.GreaterThanToken, TypeParameter); Expected(K.GreaterThanToken); return parameters;
    }
    private SyntaxNode TypeParameter()
    {
        int start = Pos; var modifiers = Modifiers(true); var name = Identifier();
        SyntaxNode? constraint = Take(K.ExtendsKeyword) ? Type() : null;
        SyntaxNode? defaultType = Take(K.EqualsToken) ? Type() : null;
        return Finish(factory.NewTypeParameterDeclaration(modifiers, name, constraint, null, defaultType), start);
    }
    private NodeList Parameters(NodeFlags signatureFlags = 0)
    {
        NodeFlags saved = context;
        context = (context & ~(NodeFlags.YieldContext | NodeFlags.AwaitContext)) | signatureFlags;
        try
        {
            if (!Expected(K.OpenParenToken)) return new([], Pos, Pos, true);
            NodeList list = Delimited(K.CloseParenToken, () => Parameter(saved & NodeFlags.AwaitContext)); Expected(K.CloseParenToken); return list;
        }
        finally { context = saved; }
    }
    private SyntaxNode Parameter(NodeFlags outerAwait = 0)
    {
        int start = Pos; NodeFlags saved = context; context = (context & ~NodeFlags.AwaitContext) | outerAwait;
        var modifiers = Modifiers(); context = saved; var rest = OptionalToken(K.DotDotDotToken);
        SyntaxNode name = Token == K.ThisKeyword ? Identifier(true) : BindingName();
        return Finish(factory.NewParameterDeclaration(modifiers, rest, name, OptionalToken(K.QuestionToken), Annotation(), Initializer()), start);
    }
    private SyntaxNode FunctionType(bool constructor, NodeList? modifiers, int start)
    {
        NodeList? types = TypeParameters(); NodeList parameters = Parameters(); Expected(K.EqualsGreaterThanToken);
        NodeFlags saved = context; context &= ~NodeFlags.DisallowConditionalTypesContext;
        SyntaxNode result = Type(); context = saved;
        return constructor ? Finish(factory.NewConstructorTypeNode(modifiers, types, parameters, result), start) : Finish(factory.NewFunctionTypeNode(types, parameters, result), start);
    }
    private SyntaxNode TupleElement()
    {
        int start = Pos; var rest = OptionalToken(K.DotDotDotToken);
        if (IsIdentifier && Peek(() => { Next(); Take(K.QuestionToken); return Token == K.ColonToken; }))
        {
            var name = Identifier(); var optional = OptionalToken(K.QuestionToken); Expected(K.ColonToken);
            return Finish(factory.NewNamedTupleMember(rest, name, optional, Type()), start);
        }
        SyntaxNode type = Type();
        if (Take(K.QuestionToken)) type = Finish(factory.NewOptionalTypeNode(type), start);
        if (rest is not null) type = Finish(factory.NewRestTypeNode(type), start);
        return type;
    }
    private bool IsMappedType() => Peek(() =>
    {
        Next(); if (Token is K.PlusToken or K.MinusToken) Next(); Take(K.ReadonlyKeyword);
        if (!Take(K.OpenBracketToken) || !IsIdentifier) return false;
        Next(); return Token == K.InKeyword;
    });
    private SyntaxNode MappedType()
    {
        int start = Pos; Next(); TokenNode? readOnly = null;
        if (Token is K.PlusToken or K.MinusToken) { readOnly = ParseToken(); Expected(K.ReadonlyKeyword); }
        else readOnly = OptionalToken(K.ReadonlyKeyword);
        Expected(K.OpenBracketToken); int parameterStart = Pos; var name = Identifier(); Expected(K.InKeyword);
        var parameter = Finish(factory.NewTypeParameterDeclaration(null, name, Type(), null, null), parameterStart);
        SyntaxNode? nameType = Take(K.AsKeyword) ? Type() : null; Expected(K.CloseBracketToken);
        TokenNode? question = null;
        if (Token is K.PlusToken or K.MinusToken) { question = ParseToken(); Expected(K.QuestionToken); }
        else question = OptionalToken(K.QuestionToken);
        SyntaxNode? type = Annotation(); Semicolon();
        NodeList members = List(K.CloseBraceToken, () => TypeMember(false)); Expected(K.CloseBraceToken);
        return Finish(factory.NewMappedTypeNode(readOnly, parameter, nameType, question, type, members), start);
    }
    private SyntaxNode ImportType(int start, bool query)
    {
        sourceFlags |= NodeFlags.PossiblyContainsDynamicImport;
        Expected(K.ImportKeyword); Expected(K.OpenParenToken); SyntaxNode argument = Type();
        ImportAttributesNode? attributes = null;
        if (Take(K.CommaToken))
        {
            Expected(K.OpenBraceToken); K token = Token; Next(); Expected(K.ColonToken);
            attributes = ImportAttributes(token); Expected(K.CloseBraceToken);
        }
        Expected(K.CloseParenToken);
        SyntaxNode? qualifier = Take(K.DotToken) ? EntityName() : null;
        return Finish(factory.NewImportTypeNode(query, argument, attributes, qualifier, TypeArguments()), start);
    }
}

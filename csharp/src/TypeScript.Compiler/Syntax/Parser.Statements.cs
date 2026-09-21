using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    private SyntaxNode ParseStatement()
    {
        TokenFlags trivia = scanner.Flags;
        NodeFlags saved = context;
        try { return WithJSDoc(ParseStatementWorker(), trivia); }
        finally { context = saved; }
    }
    private static T WithJSDoc<T>(T node, TokenFlags trivia) where T : SyntaxNode
    {
        if ((trivia & TokenFlags.PrecedingJSDocComment) != 0) node.Flags |= NodeFlags.HasJSDoc;
        if ((trivia & TokenFlags.PrecedingJSDocWithDeprecated) != 0) node.Flags |= NodeFlags.PossiblyContainsDeprecatedTag;
        return node;
    }
    private void AmbientModifiers(NodeList? modifiers)
    {
        if (modifiers?.Any(m => m.Kind == K.DeclareKeyword) != true) return;
        context |= NodeFlags.Ambient;
        foreach (SyntaxNode modifier in modifiers) modifier.Flags |= NodeFlags.Ambient;
    }
    private SyntaxNode ParseStatementWorker()
    {
        int start = Pos;
        switch (Token)
        {
            case K.SemicolonToken: Next(); return Finish(factory.NewEmptyStatement(), start);
            case K.OpenBraceToken: return Block();
            case K.IfKeyword:
                Next(); var test = ParenthesizedCondition(); var then = ParseStatement();
                return Finish(factory.NewIfStatement(test, then, Take(K.ElseKeyword) ? ParseStatement() : null), start);
            case K.DoKeyword:
                Next(); var body = ParseStatement(); Expected(K.WhileKeyword); var condition = ParenthesizedCondition(); Take(K.SemicolonToken);
                return Finish(factory.NewDoStatement(body, condition), start);
            case K.WhileKeyword:
                Next(); return Finish(factory.NewWhileStatement(ParenthesizedCondition(), ParseStatement()), start);
            case K.WithKeyword:
                Next(); var withExpression = ParenthesizedCondition(); NodeFlags old = context; context |= NodeFlags.InWithStatement;
                var withBody = ParseStatement(); context = old; return Finish(factory.NewWithStatement(withExpression, withBody), start);
            case K.ForKeyword: return For();
            case K.ReturnKeyword:
                Next(); var value = IsSemicolon() ? null : Expression(); Semicolon(); return Finish(factory.NewReturnStatement(value), start);
            case K.ThrowKeyword:
                Next(); var thrown = LineBreak ? null : Expression(); Semicolon(); return Finish(factory.NewThrowStatement(thrown), start);
            case K.BreakKeyword:
            case K.ContinueKeyword:
                K jump = Token; Next(); var label = !IsSemicolon() && IsIdentifier ? Identifier() : null; Semicolon();
                return jump == K.BreakKeyword ? Finish(factory.NewBreakStatement(label), start) : Finish(factory.NewContinueStatement(label), start);
            case K.DebuggerKeyword:
                Next(); Semicolon(); return Finish(factory.NewDebuggerStatement(), start);
            case K.SwitchKeyword: return Switch();
            case K.TryKeyword: return Try();
            case K.ExportKeyword: return Export();
            case K.ImportKeyword:
                if (!Peek(() => Next() is K.OpenParenToken or K.DotToken or K.LessThanToken)) return Import(null, start);
                break;
        }
        NodeList? modifiers = Modifiers();
        AmbientModifiers(modifiers);
        switch (Token)
        {
            case K.VarKeyword:
            case K.ConstKeyword:
            case K.LetKeyword:
                var declarations = VariableDeclarations(); Semicolon(); return Finish(factory.NewVariableStatement(modifiers, declarations), start);
            case K.UsingKeyword:
                if (Peek(() => { Next(); return IsIdentifier && !LineBreak; }))
                { var usingDeclarations = VariableDeclarations(); Semicolon(); return Finish(factory.NewVariableStatement(modifiers, usingDeclarations), start); }
                break;
            case K.AwaitKeyword:
                if (NextIs(K.UsingKeyword)) { var usingDeclarations = VariableDeclarations(); Semicolon(); return Finish(factory.NewVariableStatement(modifiers, usingDeclarations), start); }
                break;
            case K.FunctionKeyword: return Function(false, modifiers, start);
            case K.ClassKeyword: return Class(false, modifiers, start);
            case K.InterfaceKeyword:
                if (Peek(() => { Next(); return IsIdentifier && !LineBreak; })) return Interface(modifiers, start);
                break;
            case K.TypeKeyword:
                if (Peek(() => { Next(); return IsIdentifier && !LineBreak; }))
                {
                    Next(); var name = Identifier(); var parameters = TypeParameters(); Expected(K.EqualsToken); var type = Type(); Semicolon();
                    return Finish(factory.NewTypeAliasDeclaration(K.TypeAliasDeclaration, modifiers, name, parameters, type), start);
                }
                break;
            case K.EnumKeyword:
                Next(); var enumName = Identifier(); Expected(K.OpenBraceToken);
                NodeList members = Delimited(K.CloseBraceToken, () => { int memberStart = Pos; return Finish(factory.NewEnumMember(Name(), Initializer()), memberStart); });
                Expected(K.CloseBraceToken); return Finish(factory.NewEnumDeclaration(modifiers, enumName, members), start);
            case K.NamespaceKeyword:
            case K.ModuleKeyword:
            case K.GlobalKeyword:
                if (Peek(() => { Next(); return !LineBreak && (IsIdentifier || Token is K.StringLiteral or K.OpenBraceToken); })) return Module(modifiers, start);
                break;
            case K.ImportKeyword: return Import(modifiers, start);
        }
        if (modifiers is not null)
        { Error(Messages.Declaration_expected); return Finish(factory.NewMissingDeclaration(modifiers), start); }
        SyntaxNode expression = Expression();
        if (expression is IdentifierNode id && Take(K.ColonToken)) return Finish(factory.NewLabeledStatement(id, ParseStatement()), start);
        Semicolon(); return Finish(factory.NewExpressionStatement(expression), start);
    }
    private bool IsSemicolon() => Token is K.SemicolonToken or K.CloseBraceToken or K.EndOfFile || LineBreak;
    private SyntaxNode ParenthesizedCondition()
    { Expected(K.OpenParenToken); SyntaxNode expression = Expression(); Expected(K.CloseParenToken); return expression; }
    private BlockNode Block()
    {
        int start = Pos; Expected(K.OpenBraceToken); bool multiline = LineBreak;
        statementDepth++;
        NodeList statements;
        try { statements = List(K.CloseBraceToken, ParseStatement); }
        finally { statementDepth--; }
        Expected(K.CloseBraceToken);
        return Finish(factory.NewBlock(statements, multiline), start);
    }
    private BlockNode? FunctionBody(NodeFlags signatureFlags = 0)
    {
        NodeFlags saved = context;
        context = (context & ~(NodeFlags.YieldContext | NodeFlags.AwaitContext | NodeFlags.DecoratorContext)) | signatureFlags;
        try { if (Token == K.OpenBraceToken) return Block(); Semicolon(); return null; }
        finally { context = saved; }
    }
    private SyntaxNode For()
    {
        int start = Pos; Next(); var awaitToken = OptionalToken(K.AwaitKeyword); Expected(K.OpenParenToken);
        NodeFlags old = context; context |= NodeFlags.DisallowInContext;
        SyntaxNode? initializer = Token == K.SemicolonToken ? null : Token is K.VarKeyword or K.LetKeyword or K.ConstKeyword or K.UsingKeyword ? VariableDeclarations() : Expression();
        context = old;
        if (Token is K.InKeyword or K.OfKeyword)
        {
            K kind = Token == K.InKeyword ? K.ForInStatement : K.ForOfStatement; Next();
            var expression = Expression(kind == K.ForOfStatement ? 2 : 0); Expected(K.CloseParenToken);
            return Finish(factory.NewForInOrOfStatement(kind, awaitToken, initializer, expression, ParseStatement()), start);
        }
        Expected(K.SemicolonToken); SyntaxNode? condition = Token == K.SemicolonToken ? null : Expression(); Expected(K.SemicolonToken);
        SyntaxNode? incrementor = Token == K.CloseParenToken ? null : Expression(); Expected(K.CloseParenToken);
        return Finish(factory.NewForStatement(initializer, condition, incrementor, ParseStatement()), start);
    }
    private SyntaxNode Switch()
    {
        int start = Pos; Next(); SyntaxNode expression = ParenthesizedCondition(); int blockStart = Pos; Expected(K.OpenBraceToken);
        int clausesStart = Pos; var clauses = new List<SyntaxNode>();
        while (Token is K.CaseKeyword or K.DefaultKeyword)
        {
            int clauseStart = Pos; bool isCase = Token == K.CaseKeyword; Next(); SyntaxNode? value = isCase ? Expression() : null; Expected(K.ColonToken);
            int statementsStart = Pos; var statements = new List<SyntaxNode>();
            while (Token is not (K.CaseKeyword or K.DefaultKeyword or K.CloseBraceToken or K.EndOfFile))
            { int before = scanner.Position; statements.Add(ParseStatement()); if (scanner.Position == before) Next(); }
            clauses.Add(Finish(factory.NewCaseOrDefaultClause(isCase ? K.CaseClause : K.DefaultClause, value, new(statements.ToArray(), statementsStart, Pos)), clauseStart));
        }
        NodeList clauseList = new(clauses.ToArray(), clausesStart, Pos); Expected(K.CloseBraceToken);
        return Finish(factory.NewSwitchStatement(expression, Finish(factory.NewCaseBlock(clauseList), blockStart)), start);
    }
    private SyntaxNode Try()
    {
        int start = Pos; Next(); BlockNode block = Block(); CatchClauseNode? catchClause = null; BlockNode? finallyBlock = null;
        if (Token == K.CatchKeyword)
        {
            int catchStart = Pos; Next(); VariableDeclarationNode? variable = null;
            if (Take(K.OpenParenToken)) { variable = VariableDeclaration(); Expected(K.CloseParenToken); }
            catchClause = Finish(factory.NewCatchClause(variable, Block()), catchStart);
        }
        if (Take(K.FinallyKeyword)) finallyBlock = Block();
        if (catchClause is null && finallyBlock is null) Expected(K.FinallyKeyword);
        return Finish(factory.NewTryStatement(block, catchClause, finallyBlock), start);
    }
    private VariableDeclarationListNode VariableDeclarations()
    {
        int start = Pos; NodeFlags flags = Token switch { K.LetKeyword => NodeFlags.Let, K.ConstKeyword => NodeFlags.Const, K.UsingKeyword => NodeFlags.Using, K.AwaitKeyword => NodeFlags.AwaitUsing, _ => 0 };
        if (Token == K.AwaitKeyword) Next();
        Next();
        int listStart = Pos; var declarations = new List<SyntaxNode> { VariableDeclaration() };
        while (true)
        {
            if (Take(K.CommaToken)) { declarations.Add(VariableDeclaration()); continue; }
            if (!LineBreak && IsIdentifier && Token is not (K.InKeyword or K.OfKeyword)) { Error(Messages.X_0_expected, ","); declarations.Add(VariableDeclaration()); continue; }
            break;
        }
        NodeFlags saved = context; context &= ~NodeFlags.DisallowInContext;
        var result = Finish(factory.NewVariableDeclarationList(new(declarations.ToArray(), listStart, Pos), flags), start);
        context = saved; return result;
    }
    private VariableDeclarationNode VariableDeclaration()
    { int start = Pos; return Finish(factory.NewVariableDeclaration(BindingName(), OptionalToken(K.ExclamationToken), Annotation(), Initializer()), start); }
    private SyntaxNode BindingName()
    {
        if (Token is not (K.OpenBraceToken or K.OpenBracketToken)) return Identifier();
        int start = Pos; bool objectPattern = Token == K.OpenBraceToken; K end = objectPattern ? K.CloseBraceToken : K.CloseBracketToken; Next();
        NodeList elements = Delimited(end, () =>
        {
            int elementStart = Pos;
            if (!objectPattern && Token == K.CommaToken) return Finish(factory.NewBindingElement(null, null, null, null), elementStart, elementStart);
            var rest = OptionalToken(K.DotDotDotToken); SyntaxNode? property = null;
            SyntaxNode name;
            if (objectPattern) { name = Name(); if (Take(K.ColonToken)) { property = name; name = BindingName(); } }
            else name = BindingName();
            return Finish(factory.NewBindingElement(rest, property, name, Initializer()), elementStart);
        });
        Expected(end); return Finish(factory.NewBindingPattern(objectPattern ? K.ObjectBindingPattern : K.ArrayBindingPattern, elements), start);
    }
    private SyntaxNode Function(bool expression, NodeList? modifiers, int start)
    {
        Expected(K.FunctionKeyword); var star = OptionalToken(K.AsteriskToken); IdentifierNode? name = IsIdentifier ? Identifier() : expression || modifiers?.Any(m => m.Kind == K.DefaultKeyword) == true ? null : Identifier();
        NodeFlags old = context;
        context &= ~(NodeFlags.AwaitContext | NodeFlags.YieldContext);
        if (star is not null) context |= NodeFlags.YieldContext;
        if (modifiers?.Any(m => m.Kind == K.AsyncKeyword) == true) context |= NodeFlags.AwaitContext;
        var typeParameters = TypeParameters(); var parameters = Parameters(context & (NodeFlags.YieldContext | NodeFlags.AwaitContext)); var type = Annotation(); var body = FunctionBody(context & (NodeFlags.YieldContext | NodeFlags.AwaitContext)); context = old;
        return expression ? Finish(factory.NewFunctionExpression(modifiers, star, name, typeParameters, parameters, type, null, body), start)
            : Finish(factory.NewFunctionDeclaration(modifiers, star, name, typeParameters, parameters, type, null, body), start);
    }
    private SyntaxNode Class(bool expression, NodeList? modifiers, int start)
    {
        Expected(K.ClassKeyword); var name = IsIdentifier && !(Token == K.ImplementsKeyword && Peek(() => Next() >= K.Identifier)) ? Identifier() : null; var parameters = TypeParameters();
        NodeFlags saved = context;
        if (statementDepth == 0 && modifiers?.Any(m => m.Kind == K.ExportKeyword) == true) context |= NodeFlags.AwaitContext;
        var heritage = Heritage();
        Expected(K.OpenBraceToken); var members = List(K.CloseBraceToken, () => TypeMember(true)); Expected(K.CloseBraceToken);
        context = saved;
        return expression ? Finish(factory.NewClassExpression(modifiers, name, parameters, heritage, members), start) : Finish(factory.NewClassDeclaration(modifiers, name, parameters, heritage, members), start);
    }
    private SyntaxNode Interface(NodeList? modifiers, int start)
    {
        Next(); var name = Identifier(); var parameters = TypeParameters(); var heritage = Heritage(true); Expected(K.OpenBraceToken);
        var members = List(K.CloseBraceToken, () => TypeMember(false)); Expected(K.CloseBraceToken);
        return Finish(factory.NewInterfaceDeclaration(modifiers, name, parameters, heritage, members), start);
    }
    private NodeList? Heritage(bool isInterface = false)
    {
        int start = Pos; var clauses = new List<SyntaxNode>();
        while (Token is K.ExtendsKeyword or K.ImplementsKeyword)
        {
            int clauseStart = Pos; K keyword = Token; Next(); int typesStart = Pos; var types = new List<SyntaxNode>();
            do
            {
                int typeStart = Pos;
                if (isInterface || keyword == K.ImplementsKeyword) types.Add(Finish(factory.NewTypeReferenceNode(EntityName(), TypeArguments()), typeStart));
                else { SyntaxNode name = MemberExpression(PrimaryExpression(), true); types.Add(Finish(factory.NewExpressionWithTypeArguments(name, TypeArguments()), typeStart)); }
            } while (Take(K.CommaToken));
            clauses.Add(Finish(factory.NewHeritageClause(keyword, new(types.ToArray(), typesStart, Pos)), clauseStart));
        }
        return clauses.Count == 0 ? null : new(clauses.ToArray(), start, Pos);
    }
    private SyntaxNode TypeMember(bool inClass)
    {
        NodeFlags saved = context; TokenFlags trivia = scanner.Flags;
        try { return WithJSDoc(TypeMemberWorker(inClass), trivia); }
        finally { context = saved; }
    }
    private SyntaxNode TypeMemberWorker(bool inClass)
    {
        int start = Pos;
        if (Take(K.SemicolonToken)) return Finish(factory.NewSemicolonClassElement(), start);
        if (inClass && Token == K.StaticKeyword && NextIs(K.OpenBraceToken))
        { var modifier = ParseToken(); return Finish(factory.NewClassStaticBlockDeclaration(new([modifier], start, Pos), Block()), start); }
        var modifiers = Modifiers(inClass);
        AmbientModifiers(modifiers);
        if (!inClass && Token is K.OpenParenToken or K.LessThanToken)
        { var types = TypeParameters(); var parameters = Parameters(); var type = Annotation(); MemberSemicolon(); return Finish(factory.NewCallSignatureDeclaration(types, parameters, type), start); }
        if (!inClass && Token == K.NewKeyword && Peek(() => Next() is K.OpenParenToken or K.LessThanToken))
        { Next(); var types = TypeParameters(); var parameters = Parameters(); var type = Annotation(); MemberSemicolon(); return Finish(factory.NewConstructSignatureDeclaration(types, parameters, type), start); }
        if (Token == K.OpenBracketToken && Peek(() => { Next(); if (!IsIdentifier) return false; Next(); return Token == K.ColonToken; }))
        {
            Next(); int parameterStart = Pos; var name = Identifier(); var annotation = Annotation();
            var parameter = Finish(factory.NewParameterDeclaration(null, null, name, null, annotation, null), parameterStart);
            var parameters = new NodeList([parameter], parameterStart, Pos); Expected(K.CloseBracketToken); var type = Annotation(); MemberSemicolon();
            return Finish(factory.NewIndexSignatureDeclaration(modifiers, parameters, type), start);
        }
        K accessor = K.Unknown;
        if (Token is K.GetKeyword or K.SetKeyword && Peek(() => { Next(); return IsIdentifier || Token is K.StringLiteral or K.NumericLiteral or K.OpenBracketToken; }))
        { accessor = Token; Next(); }
        var star = OptionalToken(K.AsteriskToken); SyntaxNode memberName = Name();
        var postfix = Token is K.QuestionToken or K.ExclamationToken ? ParseToken() : null;
        if (Token is K.OpenParenToken or K.LessThanToken || accessor != K.Unknown)
        {
            NodeFlags signatureFlags = (star is not null ? NodeFlags.YieldContext : 0) | (modifiers?.Any(m => m.Kind == K.AsyncKeyword) == true ? NodeFlags.AwaitContext : 0);
            var types = TypeParameters(); var parameters = Parameters(signatureFlags); var type = Annotation();
            BlockNode? body = inClass || Token == K.OpenBraceToken ? FunctionBody(signatureFlags) : null;
            if (!inClass && body is null) MemberSemicolon();
            if (accessor == K.GetKeyword) return Finish(factory.NewGetAccessorDeclaration(modifiers, memberName, types, parameters, type, null, body), start);
            if (accessor == K.SetKeyword) return Finish(factory.NewSetAccessorDeclaration(modifiers, memberName, types, parameters, type, null, body), start);
            if (inClass && memberName is IdentifierNode { Text: "constructor" }) return Finish(factory.NewConstructorDeclaration(modifiers, types, parameters, type, null, body), start);
            return inClass ? Finish(factory.NewMethodDeclaration(modifiers, star, memberName, postfix, types, parameters, type, null, body), start)
                : Finish(factory.NewMethodSignatureDeclaration(modifiers, memberName, postfix, types, parameters, type), start);
        }
        var propertyType = Annotation();
        NodeFlags savedPropertyContext = context; context &= ~(NodeFlags.AwaitContext | NodeFlags.YieldContext | NodeFlags.DisallowInContext);
        var initializer = Initializer(); context = savedPropertyContext; MemberSemicolon();
        return inClass ? Finish(factory.NewPropertyDeclaration(modifiers, memberName, postfix, propertyType, initializer), start)
            : Finish(factory.NewPropertySignatureDeclaration(modifiers, memberName, postfix, propertyType, initializer), start);
    }
    private void MemberSemicolon() { if (!Take(K.CommaToken)) Semicolon(); }
    private SyntaxNode Module(NodeList? modifiers, int start)
    {
        K keyword = Token; int nameStart = Pos; Next(); SyntaxNode name = keyword == K.GlobalKeyword ? Finish(factory.NewIdentifier("global"), nameStart, Pos) : Name();
        return ModuleRest(modifiers, start, keyword, name);
    }
    private SyntaxNode ModuleRest(NodeList? modifiers, int start, K keyword, SyntaxNode name)
    {
        SyntaxNode? body;
        if (Take(K.DotToken))
        {
            int nested = Pos;
            var implicitExport = factory.NewToken(K.ExportKeyword); implicitExport.Pos = implicitExport.End = nested; implicitExport.Flags = NodeFlags.Reparsed;
            body = ModuleRest(new([implicitExport], nested, nested), nested, keyword, Identifier(true));
        }
        else if (Token == K.OpenBraceToken)
        {
            int blockStart = Pos; Next(); statementDepth++;
            NodeList statements;
            try { statements = List(K.CloseBraceToken, ParseStatement); }
            finally { statementDepth--; }
            Expected(K.CloseBraceToken); body = Finish(factory.NewModuleBlock(statements), blockStart);
        }
        else { Semicolon(); body = null; }
        return Finish(factory.NewModuleDeclaration(modifiers, keyword, name, null, body), start);
    }
    private SyntaxNode Import(NodeList? modifiers, int start)
    {
        Expected(K.ImportKeyword);
        ImportClauseNode? clause = null;
        if (Token != K.StringLiteral)
        {
            int clauseStart = Pos; K phase = K.Unknown;
            if (Token is K.TypeKeyword or K.DeferKeyword && Peek(() => { Next(); return Token != K.FromKeyword && Token != K.EqualsToken && Token != K.CommaToken; })) { phase = Token; Next(); }
            IdentifierNode? name = IsIdentifier ? Identifier() : null;
            if (name is not null && Take(K.EqualsToken))
            {
                SyntaxNode reference;
                if (Token == K.RequireKeyword && NextIs(K.OpenParenToken))
                { int referenceStart = Pos; Next(); Expected(K.OpenParenToken); var expr = Expression(2); Expected(K.CloseParenToken); reference = Finish(factory.NewExternalModuleReference(expr), referenceStart); }
                else reference = EntityName();
                Semicolon(); return Finish(factory.NewImportEqualsDeclaration(modifiers, phase == K.TypeKeyword, name, reference), start);
            }
            SyntaxNode? bindings = null;
            if (name is null || Take(K.CommaToken))
            {
                int bindingsStart = Pos;
                if (Take(K.AsteriskToken)) { Expected(K.AsKeyword); bindings = Finish(factory.NewNamespaceImport(Identifier()), bindingsStart); }
                else if (Take(K.OpenBraceToken))
                { var elements = Delimited(K.CloseBraceToken, () => ImportExportSpecifier(false)); Expected(K.CloseBraceToken); bindings = Finish(factory.NewNamedImports(elements), bindingsStart); }
            }
            clause = Finish(factory.NewImportClause(phase, name, bindings), clauseStart);
            Expected(K.FromKeyword);
        }
        SyntaxNode specifier = Expression(2); ImportAttributesNode? attributes = null;
        if (!LineBreak && Token is K.WithKeyword or K.AssertKeyword) { K token = Token; Next(); attributes = ImportAttributes(token); }
        Semicolon(); return Finish(factory.NewImportDeclaration(K.ImportDeclaration, modifiers, clause, specifier, attributes), start);
    }
    private SyntaxNode ImportExportSpecifier(bool export)
    {
        int start = Pos; bool typeOnly = false;
        if (Token == K.TypeKeyword && Peek(() => { Next(); return Token != K.AsKeyword && Token != K.CommaToken && Token != K.CloseBraceToken; })) { typeOnly = true; Next(); }
        SyntaxNode? property = null; SyntaxNode name = Token == K.StringLiteral ? Literal() : Identifier(true);
        if (Take(K.AsKeyword)) { property = name; name = export && Token == K.StringLiteral ? Literal() : Identifier(true); }
        if (!export && name is not IdentifierNode) { Error(Messages.Identifier_expected); name = Finish(factory.NewIdentifier(""), name.Pos, name.End); }
        return export ? Finish(factory.NewExportSpecifier(typeOnly, property, name), start) : Finish(factory.NewImportSpecifier(typeOnly, property, (IdentifierNode)name), start);
    }
    private ImportAttributesNode ImportAttributes(K token)
    {
        int start = Pos; Expected(K.OpenBraceToken); bool multiline = LineBreak;
        var attributes = Delimited(K.CloseBraceToken, () => { int at = Pos; var name = Name(); Expected(K.ColonToken); return Finish(factory.NewImportAttribute(name, Expression(2)), at); });
        Expected(K.CloseBraceToken); return Finish(factory.NewImportAttributes(token, attributes, multiline), start);
    }
    private SyntaxNode Export()
    {
        int start = Pos; TokenNode export = ParseToken();
        if (Take(K.AsKeyword))
        { Expected(K.NamespaceKeyword); var name = Identifier(); Semicolon(); return Finish(factory.NewNamespaceExportDeclaration(null, name), start); }
        if (Token == K.EqualsToken)
        { NodeFlags saved = context; context |= NodeFlags.AwaitContext; Next(); var value = Expression(2); Semicolon(); context = saved; return Finish(factory.NewExportAssignment(null, true, null, value), start); }
        var modifiers = new List<SyntaxNode> { export };
        if (Token == K.DefaultKeyword)
        {
            if (Peek(() => Next() is K.InterfaceKeyword or K.AbstractKeyword)) { scanner.ResetPosition(start); Next(false); return DeclarationWithExport(); }
            modifiers.Add(ParseToken());
            if (Token == K.FunctionKeyword) return Function(false, new(modifiers.ToArray(), start, Pos), start);
            if (Token == K.ClassKeyword) return Class(false, new(modifiers.ToArray(), start, Pos), start);
            NodeFlags saved = context; context |= NodeFlags.AwaitContext;
            var expression = Expression(2); Semicolon(); context = saved; return Finish(factory.NewExportAssignment(null, false, null, expression), start);
        }
        bool typeOnly = Token == K.TypeKeyword && Peek(() => Next() is K.OpenBraceToken or K.AsteriskToken);
        if (typeOnly) Next();
        if (Token is K.OpenBraceToken or K.AsteriskToken)
        {
            NodeFlags saved = context; context |= NodeFlags.AwaitContext;
            SyntaxNode? clause = null;
            int clauseStart = Pos;
            if (Take(K.AsteriskToken)) { if (Take(K.AsKeyword)) clause = Finish(factory.NewNamespaceExport(Identifier(true)), clauseStart); }
            else { Next(); NodeList elements = Delimited(K.CloseBraceToken, () => ImportExportSpecifier(true)); Expected(K.CloseBraceToken); clause = Finish(factory.NewNamedExports(elements), clauseStart); }
            SyntaxNode? specifier = Take(K.FromKeyword) ? Expression(2) : null;
            ImportAttributesNode? attributes = null;
            if (Token is K.WithKeyword or K.AssertKeyword) { K token = Token; Next(); attributes = ImportAttributes(token); }
            Semicolon(); context = saved; return Finish(factory.NewExportDeclaration(null, typeOnly, clause, specifier, attributes), start);
        }
        // Parse a declaration with the export token still part of its modifier list.
        scanner.ResetPosition(start); Next(false);
        return DeclarationWithExport();
    }
    private SyntaxNode DeclarationWithExport()
    {
        // Re-enter after consuming export via the common modifier parser, not Export().
        int start = Pos; NodeList? modifiers = Modifiers();
        AmbientModifiers(modifiers);
        if (Token == K.ImportKeyword) return Import(modifiers, start);
        if (Token == K.FunctionKeyword) return Function(false, modifiers, start);
        if (Token == K.ClassKeyword) return Class(false, modifiers, start);
        if (Token == K.InterfaceKeyword) return Interface(modifiers, start);
        if (Token is K.NamespaceKeyword or K.ModuleKeyword) return Module(modifiers, start);
        if (Token == K.TypeKeyword)
        { Next(); var name = Identifier(); var parameters = TypeParameters(); Expected(K.EqualsToken); var type = Type(); Semicolon(); return Finish(factory.NewTypeAliasDeclaration(K.TypeAliasDeclaration, modifiers, name, parameters, type), start); }
        if (Token is K.VarKeyword or K.LetKeyword or K.ConstKeyword or K.UsingKeyword)
        { var list = VariableDeclarations(); Semicolon(); return Finish(factory.NewVariableStatement(modifiers, list), start); }
        Error(Messages.Declaration_expected); return Finish(factory.NewMissingDeclaration(modifiers), start);
    }
}

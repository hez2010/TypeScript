using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    private int classMemberBodyDepth;

    private async ValueTask<SyntaxNode> ParseStatementCore()
    {
        await ParseStack;
        TokenFlags trivia = scanner.Flags;
        bool parenthesized = Token == K.OpenParenToken;
        NodeFlags saved = context;
        try
        {
            SyntaxNode node = (await ParseStatementWorkerCore().ConfigureAwait(false));
            return (await WithJSDocCore(node, parenthesized && node is ExpressionStatementNode ? 0 : trivia).ConfigureAwait(false));
        }
        finally
        {
            context = saved;
        }
    }

    private async ValueTask<T> WithJSDocCore<T>(T node, TokenFlags trivia)
        where T : SyntaxNode
    {
        await ParseStack;
        if ((trivia & TokenFlags.PrecedingJSDocComment) != 0)
            node.Flags |= NodeFlags.HasJSDoc;
        if ((trivia & TokenFlags.PrecedingJSDocWithDeprecated) != 0)
            node.Flags |= NodeFlags.PossiblyContainsDeprecatedTag;
        if (speculationDepth == 0
            && (trivia & TokenFlags.PrecedingJSDocComment) != 0
            && options.ScriptKind is ScriptKind.JS or ScriptKind.JSX)
        {
            var reader = new DocumentationParser(source, options.ScriptKind, context, cancellation);
            JSDocNode[] comments = await reader.LeadingAsync(node.Pos, node.End, node.Kind).ConfigureAwait(false);
            documentation[node] = comments;
            foreach (JSDocNode comment in comments)
                comment.Parent = node;
            documentationDiagnostics.AddRange(reader.Diagnostics);
            sourceFlags |= reader.SourceFlags;
            foreach (JSDocNode comment in comments)
                ReparseUnhostedDocumentation(node, comment);
            if (comments.Length != 0)
                ReparseDocumentation(node, comments[^1]);
        }

        return node;
    }

    private void AmbientModifiers(NodeList? modifiers)
    {
        if (modifiers?.Any(m => m.Kind == K.DeclareKeyword) != true)
            return;
        context |= NodeFlags.Ambient;
        foreach (SyntaxNode modifier in modifiers)
            modifier.Flags |= NodeFlags.Ambient;
    }

    private async ValueTask<SyntaxNode> ParseStatementWorkerCore(bool skipExportDispatch = false)
    {
        await ParseStack;
        int start = Pos;
        switch (Token)
        {
            case K.SemicolonToken:
                Next();
                return Finish(factory.NewEmptyStatement(), start);
            case K.OpenBraceToken:
                return (await BlockCore().ConfigureAwait(false));
            case K.IfKeyword:
                Next();
                var test = (await ParenthesizedConditionCore().ConfigureAwait(false));
                var then = (await ParseStatementCore().ConfigureAwait(false));
                return Finish(
                    factory.NewIfStatement(test, then, Take(K.ElseKeyword) ? (await ParseStatementCore().ConfigureAwait(false)) : null),
                    start);
            case K.DoKeyword:
                Next();
                var body = (await ParseStatementCore().ConfigureAwait(false));
                Expected(K.WhileKeyword);
                var condition = (await ParenthesizedConditionCore().ConfigureAwait(false));
                Take(K.SemicolonToken);
                return Finish(factory.NewDoStatement(body, condition), start);
            case K.WhileKeyword:
                Next();
                return Finish(
                    factory.NewWhileStatement(
                        (await ParenthesizedConditionCore().ConfigureAwait(false)),
                        (await ParseStatementCore().ConfigureAwait(false))),
                    start);
            case K.WithKeyword:
                Next();
                var withExpression = (await ParenthesizedConditionCore().ConfigureAwait(false));
                NodeFlags old = context;
                context |= NodeFlags.InWithStatement;
                var withBody = (await ParseStatementCore().ConfigureAwait(false));
                context = old;
                return Finish(factory.NewWithStatement(withExpression, withBody), start);
            case K.ForKeyword:
                return (await ForCore().ConfigureAwait(false));
            case K.ReturnKeyword:
                Next();
                var value = IsSemicolon() ? null : (await ExpressionCore().ConfigureAwait(false));
                Semicolon();
                return Finish(factory.NewReturnStatement(value), start);
            case K.ThrowKeyword:
                Next();
                var thrown = LineBreak ? Finish(factory.NewIdentifier(""), Pos, Pos) : (await ExpressionCore().ConfigureAwait(false));
                Semicolon();
                return Finish(factory.NewThrowStatement(thrown), start);
            case K.BreakKeyword:
            case K.ContinueKeyword:
                K jump = Token;
                Next();
                var label = !IsSemicolon() && IsIdentifier ? Identifier() : null;
                Semicolon();
                return jump == K.BreakKeyword
                    ? Finish(factory.NewBreakStatement(label), start)
                    : Finish(factory.NewContinueStatement(label), start);
            case K.DebuggerKeyword:
                Next();
                Semicolon();
                return Finish(factory.NewDebuggerStatement(), start);
            case K.SwitchKeyword:
                return (await SwitchCore().ConfigureAwait(false));
            case K.TryKeyword:
            case K.CatchKeyword:
            case K.FinallyKeyword:
                return (await TryCore().ConfigureAwait(false));
            case K.ExportKeyword when !skipExportDispatch:
                return (await ExportCore().ConfigureAwait(false));
            case K.ImportKeyword:
                if (!Peek(() => Next() is K.OpenParenToken or K.DotToken or K.LessThanToken))
                    return (await ImportCore(null, start).ConfigureAwait(false));
                break;
        }

        NodeList? modifiers = Token == K.AtToken || Peek(StartsDeclaration) ? (await ModifiersCore().ConfigureAwait(false)) : null;
        AmbientModifiers(modifiers);
        switch (Token)
        {
            case K.VarKeyword:
            case K.ConstKeyword:
            case K.LetKeyword when modifiers is not null || Peek(() =>
            {
                Next();
                return IsBindingIdentifier || Token is K.OpenBraceToken or K.OpenBracketToken;
            }):
                var declarations = (await VariableDeclarationsCore().ConfigureAwait(false));
                Semicolon();
                return Finish(factory.NewVariableStatement(modifiers, declarations), start);
            case K.UsingKeyword:
                if (modifiers is not null || IsUsingDeclaration())
                {
                    var usingDeclarations = (await VariableDeclarationsCore().ConfigureAwait(false));
                    Semicolon();
                    return Finish(factory.NewVariableStatement(modifiers, usingDeclarations), start);
                }

                break;
            case K.AwaitKeyword:
                if (IsAwaitUsingDeclaration())
                {
                    var usingDeclarations = (await VariableDeclarationsCore().ConfigureAwait(false));
                    Semicolon();
                    return Finish(factory.NewVariableStatement(modifiers, usingDeclarations), start);
                }

                break;
            case K.FunctionKeyword:
                return (await FunctionCore(false, modifiers, start).ConfigureAwait(false));
            case K.ClassKeyword:
                return (await ClassCore(false, modifiers, start).ConfigureAwait(false));
            case K.InterfaceKeyword:
                if (Peek(() =>
                {
                    Next();
                    return IsIdentifier && !LineBreak;
                }))
                    return (await InterfaceCore(modifiers, start).ConfigureAwait(false));
                break;
            case K.TypeKeyword:
                if (modifiers is not null || Peek(() =>
                {
                    Next();
                    return IsIdentifier && !LineBreak;
                }))
                {
                    Next();
                    var name = Identifier();
                    var parameters = (await TypeParametersCore().ConfigureAwait(false));
                    Expected(K.EqualsToken);
                    SyntaxNode type;
                    if (Token == K.IntrinsicKeyword && !NextIs(K.DotToken))
                    {
                        int intrinsicStart = Pos;
                        Next();
                        type = Finish(factory.NewKeywordTypeNode(K.IntrinsicKeyword), intrinsicStart);
                    }
                    else
                        type = await TypeCore().ConfigureAwait(false);
                    Semicolon();
                    return Finish(factory.NewTypeAliasDeclaration(K.TypeAliasDeclaration, modifiers, name, parameters, type), start);
                }

                break;
            case K.EnumKeyword:
                Next();
                var enumName = Identifier();
                if (!Expected(K.OpenBraceToken))
                    return Finish(factory.NewEnumDeclaration(modifiers, enumName, new([], Pos, Pos, true)), start);
                var enumContext = context;
                context &= ~(NodeFlags.YieldContext | NodeFlags.AwaitContext);
                NodeList members;
                try
                {
                    members = (await DelimitedCore(K.CloseBraceToken, async () =>
                {
                    int memberStart = Pos;
                    TokenFlags memberTrivia = scanner.Flags;
                    return await WithJSDocCore(
                        Finish(
                            factory.NewEnumMember(
                                (await NameCore().ConfigureAwait(false)),
                                (await InitializerCore().ConfigureAwait(false))),
                            memberStart),
                        memberTrivia).ConfigureAwait(false);
                }, startsElement: () => Token is K.OpenBracketToken or K.StringLiteral or K.NumericLiteral or K.BigIntLiteral
                    || Token >= K.Identifier,
                    elementExpected: Messages.Enum_member_expected).ConfigureAwait(false));
                }
                finally
                {
                    context = enumContext;
                }
                Expected(K.CloseBraceToken);
                return Finish(factory.NewEnumDeclaration(modifiers, enumName, members), start);
            case K.NamespaceKeyword:
            case K.ModuleKeyword:
            case K.GlobalKeyword:
                if (Peek(() =>
                {
                    bool globalModule = Token == K.GlobalKeyword;
                    Next();
                    return !LineBreak && (IsIdentifier || Token == K.StringLiteral || globalModule && Token == K.OpenBraceToken);
                }))
                    return (await ModuleCore(modifiers, start).ConfigureAwait(false));
                break;
            case K.ImportKeyword:
                if (!Peek(() => Next() is K.OpenParenToken or K.LessThanToken or K.DotToken))
                    return (await ImportCore(modifiers, start).ConfigureAwait(false));
                break;
            case K.ExportKeyword when modifiers is not null:
                return await ExportCore(modifiers, start).ConfigureAwait(false);
        }

        if (modifiers is not null)
        {
            Error(Messages.Declaration_expected);
            return Finish(factory.NewMissingDeclaration(modifiers), start);
        }

        SyntaxNode expression = (await ExpressionCore().ConfigureAwait(false));
        if (expression is IdentifierNode id && Take(K.ColonToken))
            return Finish(factory.NewLabeledStatement(id, (await ParseStatementCore().ConfigureAwait(false))), start);
        Semicolon();
        return Finish(factory.NewExpressionStatement(expression), start);
    }

    private bool IsUsingDeclaration(bool disallowOf = false) => Peek(() =>
    {
        Next();
        if (LineBreak)
            return false;
        if (disallowOf && Token == K.OfKeyword)
            return Next() is K.EqualsToken or K.ColonToken or K.SemicolonToken;
        return IsBindingIdentifier || Token == K.OpenBraceToken;
    });

    private bool IsAwaitUsingDeclaration() => Peek(() => Next() == K.UsingKeyword && IsUsingDeclaration());

    private bool StartsDeclaration()
    {
        while (true)
        {
            switch (Token)
            {
                case K.VarKeyword or K.LetKeyword or K.ConstKeyword or K.FunctionKeyword or K.ClassKeyword or K.EnumKeyword:
                    return true;
                case K.InterfaceKeyword or K.TypeKeyword or K.DeferKeyword:
                    Next();
                    return IsIdentifier && !LineBreak;
                case K.ModuleKeyword or K.NamespaceKeyword:
                    Next();
                    return !LineBreak && (IsIdentifier || Token == K.StringLiteral);
                case K.AbstractKeyword or K.AccessorKeyword or K.AsyncKeyword or K.DeclareKeyword or K.PrivateKeyword
                    or K.ProtectedKeyword or K.PublicKeyword or K.ReadonlyKeyword:
                    K modifier = Token;
                    Next();
                    if (LineBreak)
                        return false;
                    if (modifier == K.DeclareKeyword && Token == K.TypeKeyword)
                        return true;
                    continue;
                case K.StaticKeyword:
                    Next();
                    continue;
                case K.ExportKeyword:
                    Next();
                    if (Token is K.EqualsToken or K.AsteriskToken or K.OpenBraceToken or K.DefaultKeyword or K.AsKeyword or K.AtToken)
                        return true;
                    if (Token == K.TypeKeyword)
                    {
                        Next();
                        return Token is K.AsteriskToken or K.OpenBraceToken || IsIdentifier && !LineBreak;
                    }
                    continue;
                case K.ImportKeyword:
                    Next();
                    return Token >= K.Identifier || Token is K.StringLiteral or K.AsteriskToken or K.OpenBraceToken;
                case K.GlobalKeyword:
                    Next();
                    return Token is K.OpenBraceToken or K.Identifier or K.ExportKeyword;
                case K.UsingKeyword:
                    Next();
                    return IsBindingIdentifier && !LineBreak;
                case K.AwaitKeyword:
                    Next();
                    return Token == K.UsingKeyword && !LineBreak;
                default:
                    return false;
            }
        }
    }

    private bool StartsStatement() => Token switch
    {
        K.AtToken or K.SemicolonToken or K.OpenBraceToken or K.VarKeyword or K.LetKeyword or K.UsingKeyword
            or K.FunctionKeyword or K.ClassKeyword or K.EnumKeyword or K.IfKeyword or K.DoKeyword or K.WhileKeyword
            or K.ForKeyword or K.ContinueKeyword or K.BreakKeyword or K.ReturnKeyword or K.WithKeyword or K.SwitchKeyword
            or K.ThrowKeyword or K.TryKeyword or K.DebuggerKeyword or K.CatchKeyword or K.FinallyKeyword
            or K.AsyncKeyword or K.DeclareKeyword or K.InterfaceKeyword or K.ModuleKeyword or K.NamespaceKeyword
            or K.TypeKeyword or K.GlobalKeyword or K.DeferKeyword => true,
        K.ImportKeyword => Peek(StartsDeclaration) || Peek(() => Next() is K.OpenParenToken or K.LessThanToken or K.DotToken),
        K.ConstKeyword or K.ExportKeyword => Peek(StartsDeclaration),
        K.AccessorKeyword or K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.StaticKeyword or K.ReadonlyKeyword =>
            Peek(StartsDeclaration) || !Peek(() =>
            {
                Next();
                return !LineBreak && (IsIdentifier || Token is >= K.FirstKeyword and <= K.LastKeyword);
            }),
        _ => StartsExpression()
    };

    private bool IsSemicolon() => Token is K.SemicolonToken or K.CloseBraceToken or K.EndOfFile || LineBreak;

    private async ValueTask<SyntaxNode> ParenthesizedConditionCore()
    {
        await ParseStack;
        Expected(K.OpenParenToken);
        SyntaxNode expression = (await ExpressionCore().ConfigureAwait(false));
        Expected(K.CloseParenToken);
        return expression;
    }

    private async ValueTask<BlockNode> BlockCore(bool ignoreMissingOpenBrace = false)
    {
        await ParseStack;
        int start = Pos;
        if (!Expected(K.OpenBraceToken) && !ignoreMissingOpenBrace)
            return Finish(factory.NewBlock(new NodeList([], Pos, Pos, true), false), start, start);
        bool multiline = LineBreak;
        statementDepth++;
        NodeList statements;
        try
        {
            statements = await ListCore(K.CloseBraceToken, ParseStatementCore, true,
                stop: () => classMemberBodyDepth != 0 && Token is K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword &&
                    Peek(() =>
                    {
                        Next();
                        return !LineBreak && (Token >= K.Identifier || Token is K.PrivateIdentifier or K.OpenBracketToken);
                    })).ConfigureAwait(false);
        }
        finally
        {
            statementDepth--;
        }

        Expected(K.CloseBraceToken);
        return Finish(factory.NewBlock(statements, multiline), start);
    }

    private async ValueTask<BlockNode?> FunctionBodyCore(NodeFlags signatureFlags = 0, bool classMember = false, bool allowSemicolon = true)
    {
        await ParseStack;
        NodeFlags saved = context;
        context = (context & ~(NodeFlags.YieldContext | NodeFlags.AwaitContext | NodeFlags.DecoratorContext)) | signatureFlags;
        if (classMember)
            classMemberBodyDepth++;
        try
        {
            if (Token == K.OpenBraceToken || !allowSemicolon || !IsSemicolon())
                return (await BlockCore().ConfigureAwait(false));
            Semicolon();
            return null;
        }
        finally
        {
            if (classMember)
                classMemberBodyDepth--;
            context = saved;
        }
    }

    private async ValueTask<SyntaxNode> ForCore()
    {
        await ParseStack;
        int start = Pos;
        Next();
        var awaitToken = OptionalToken(K.AwaitKeyword);
        Expected(K.OpenParenToken);
        NodeFlags old = context;
        context |= NodeFlags.DisallowInContext;
        bool variable = Token is K.VarKeyword or K.LetKeyword or K.ConstKeyword
            || Token == K.UsingKeyword && IsUsingDeclaration(true)
            || Token == K.AwaitKeyword && IsAwaitUsingDeclaration();
        SyntaxNode? initializer = Token == K.SemicolonToken
            ? null
            : variable ? (await VariableDeclarationsCore().ConfigureAwait(false)) : (await ExpressionCore().ConfigureAwait(false));
        context = old;
        if (Token is K.InKeyword or K.OfKeyword)
        {
            if (awaitToken is not null && Token == K.InKeyword)
                Error(Messages.X_0_expected, "of");
            K kind = Token == K.InKeyword ? K.ForInStatement : K.ForOfStatement;
            Next();
            var expression = (await ExpressionCore(kind == K.ForOfStatement ? 2 : 0).ConfigureAwait(false));
            Expected(K.CloseParenToken);
            return Finish(
                factory.NewForInOrOfStatement(
                    kind,
                    awaitToken,
                    initializer,
                    expression,
                    (await ParseStatementCore().ConfigureAwait(false))),
                start);
        }

        Expected(K.SemicolonToken);
        SyntaxNode? condition = Token == K.SemicolonToken ? null : (await ExpressionCore().ConfigureAwait(false));
        Expected(K.SemicolonToken);
        SyntaxNode? incrementor = Token == K.CloseParenToken ? null : (await ExpressionCore().ConfigureAwait(false));
        Expected(K.CloseParenToken);
        return Finish(
            factory.NewForStatement(initializer, condition, incrementor, (await ParseStatementCore().ConfigureAwait(false))),
            start);
    }

    private async ValueTask<SyntaxNode> SwitchCore()
    {
        await ParseStack;
        int start = Pos;
        Next();
        SyntaxNode expression = (await ParenthesizedConditionCore().ConfigureAwait(false));
        int blockStart = Pos;
        Expected(K.OpenBraceToken);
        int clausesStart = Pos;
        var clauses = new List<SyntaxNode>();
        while (Token is K.CaseKeyword or K.DefaultKeyword)
        {
            int clauseStart = Pos;
            bool isCase = Token == K.CaseKeyword;
            Next();
            SyntaxNode? value = isCase ? (await ExpressionCore().ConfigureAwait(false)) : null;
            Expected(K.ColonToken);
            int statementsStart = Pos;
            var statements = new List<SyntaxNode>();
            while (Token is not (K.CaseKeyword or K.DefaultKeyword or K.CloseBraceToken or K.EndOfFile))
            {
                int before = Pos;
                statements.Add((await ParseStatementCore().ConfigureAwait(false)));
                if (Pos == before)
                    Next();
            }

            clauses.Add(
                Finish(
                    factory.NewCaseOrDefaultClause(
                        isCase ? K.CaseClause : K.DefaultClause,
                        value,
                        new(statements.ToArray(), statementsStart, Pos)),
                    clauseStart));
        }

        NodeList clauseList = new(clauses.ToArray(), clausesStart, Pos);
        Expected(K.CloseBraceToken);
        return Finish(factory.NewSwitchStatement(expression, Finish(factory.NewCaseBlock(clauseList), blockStart)), start);
    }

    private async ValueTask<SyntaxNode> TryCore()
    {
        await ParseStack;
        int start = Pos;
        Expected(K.TryKeyword);
        BlockNode block = (await BlockCore().ConfigureAwait(false));
        CatchClauseNode? catchClause = null;
        BlockNode? finallyBlock = null;
        if (Token == K.CatchKeyword)
        {
            int catchStart = Pos;
            Next();
            VariableDeclarationNode? variable = null;
            if (Take(K.OpenParenToken))
            {
                variable = (await VariableDeclarationCore().ConfigureAwait(false));
                Expected(K.CloseParenToken);
            }

            catchClause = Finish(factory.NewCatchClause(variable, (await BlockCore().ConfigureAwait(false))), catchStart);
        }

        if (catchClause is null || Token == K.FinallyKeyword)
        {
            if (!Take(K.FinallyKeyword))
                Error(Messages.X_catch_or_finally_expected);
            finallyBlock = (await BlockCore().ConfigureAwait(false));
        }
        return Finish(factory.NewTryStatement(block, catchClause, finallyBlock), start);
    }

    private async ValueTask<VariableDeclarationListNode> VariableDeclarationsCore()
    {
        variableDeclarationDepth++;
        try
        {
            return await VariableDeclarationsWorkerCore().ConfigureAwait(false);
        }
        finally
        {
            variableDeclarationDepth--;
        }
    }

    private async ValueTask<VariableDeclarationListNode> VariableDeclarationsWorkerCore()
    {
        await ParseStack;
        int start = Pos;
        NodeFlags flags = Token switch
        {
            K.LetKeyword => NodeFlags.Let,
            K.ConstKeyword => NodeFlags.Const,
            K.UsingKeyword => NodeFlags.Using,
            K.AwaitKeyword => NodeFlags.AwaitUsing,
            _ => 0
        };
        if (Token == K.AwaitKeyword)
            Next();
        Next();
        int listStart = Pos;
        var declarations = new List<SyntaxNode>();
        bool emptyOf = Token == K.OfKeyword && Peek(() =>
        {
            Next();
            return IsIdentifier && Next() == K.CloseParenToken;
        });
        if (!emptyOf && Token is not (K.EndOfFile or K.SemicolonToken or K.CloseParenToken or K.InKeyword)
            && (IsBindingIdentifier || Token is K.PrivateIdentifier or K.OpenBraceToken or K.OpenBracketToken))
            declarations.Add(await VariableDeclarationCore().ConfigureAwait(false));
        while (true)
        {
            if (Take(K.CommaToken))
            {
                if (!IsBindingIdentifier && Token is not (K.OpenBraceToken or K.OpenBracketToken))
                    break;
                declarations.Add((await VariableDeclarationCore().ConfigureAwait(false)));
                continue;
            }

            if (!LineBreak && (IsIdentifier && Token is not (K.InKeyword or K.OfKeyword)
                || Token is K.PrivateIdentifier or K.OpenBraceToken or K.OpenBracketToken))
            {
                Error(Messages.X_0_expected, ",");
                declarations.Add((await VariableDeclarationCore().ConfigureAwait(false)));
                continue;
            }

            if (!IsSemicolon()
                && Token is not (K.InKeyword or K.OfKeyword or K.EqualsGreaterThanToken or K.CloseBracketToken)
                && !StartsStatement())
            {
                Error(Messages.Variable_declaration_expected);
                Next();
                continue;
            }

            break;
        }

        NodeFlags saved = context;
        context &= ~NodeFlags.DisallowInContext;
        var result = Finish(factory.NewVariableDeclarationList(new(declarations.ToArray(), listStart, Pos), flags), start);
        context = saved;
        return result;
    }

    private async ValueTask<VariableDeclarationNode> VariableDeclarationCore()
    {
        await ParseStack;
        int start = Pos;
        TokenFlags trivia = scanner.Flags;
        return await WithJSDocCore(
            Finish(
                factory.NewVariableDeclaration(
                    (await BindingNameCore().ConfigureAwait(false)),
                    OptionalToken(K.ExclamationToken),
                    (await AnnotationCore().ConfigureAwait(false)),
                    (await InitializerCore().ConfigureAwait(false))),
                start),
            trivia).ConfigureAwait(false);
    }

    private async ValueTask<SyntaxNode> BindingNameCore(DiagnosticMessage? privateIdentifierDiagnostic = null)
    {
        await ParseStack;
        if (Token is not (K.OpenBraceToken or K.OpenBracketToken))
            return Identifier(binding: true, privateIdentifierDiagnostic: privateIdentifierDiagnostic);
        int start = Pos;
        bool objectPattern = Token == K.OpenBraceToken;
        K end = objectPattern ? K.CloseBraceToken : K.CloseBracketToken;
        Next();
        NodeFlags saved = context;
        context &= ~NodeFlags.DisallowInContext;
        NodeList elements = (await DelimitedCore(end, async () =>
        {
            int elementStart = Pos;
            if (!objectPattern && Token == K.CommaToken)
                return Finish(factory.NewBindingElement(null, null, null, null), elementStart, elementStart);
            var rest = OptionalToken(K.DotDotDotToken);
            SyntaxNode? property = null;
            SyntaxNode name;
            if (objectPattern)
            {
                bool binding = IsBindingIdentifier;
                name = (await NameCore().ConfigureAwait(false));
                if (!binding || Token == K.ColonToken)
                {
                    Expected(K.ColonToken);
                    property = name;
                    name = (await BindingNameCore().ConfigureAwait(false));
                }
            }
            else
                name = (await BindingNameCore().ConfigureAwait(false));
            return Finish(factory.NewBindingElement(rest, property, name, (await InitializerCore().ConfigureAwait(false))), elementStart);
        }, startsElement: () => objectPattern
            ? Token >= K.Identifier
                || Token is K.OpenBracketToken or K.DotDotDotToken or K.StringLiteral or K.NumericLiteral or K.BigIntLiteral
            : Token is K.CommaToken or K.DotDotDotToken or K.OpenBraceToken or K.OpenBracketToken or K.PrivateIdentifier
                || IsBindingIdentifier,
            elementExpected: objectPattern
                ? Messages.Property_destructuring_pattern_expected
                : Messages.Array_element_destructuring_pattern_expected).ConfigureAwait(false));
        context = saved;
        Expected(end);
        return Finish(factory.NewBindingPattern(objectPattern ? K.ObjectBindingPattern : K.ArrayBindingPattern, elements), start);
    }

    private async ValueTask<SyntaxNode> FunctionCore(bool expression, NodeList? modifiers, int start)
    {
        await ParseStack;
        NodeFlags old = context;
        if (expression)
            context &= ~NodeFlags.DecoratorContext;
        Expected(K.FunctionKeyword);
        var star = OptionalToken(K.AsteriskToken);
        NodeFlags signatureFlags = (star is not null
            ? NodeFlags.YieldContext
            : 0) | (modifiers?.Any(m => m.Kind == K.AsyncKeyword) == true ? NodeFlags.AwaitContext : 0);
        NodeFlags nameContext = context;
        if (expression)
            context |= signatureFlags;
        IdentifierNode? name = IsBindingIdentifier
            ? Identifier(binding: true)
            : expression || modifiers?.Any(m => m.Kind == K.DefaultKeyword) == true ? null : Identifier();
        context = nameContext;
        var typeParameters = (await TypeParametersCore().ConfigureAwait(false));
        if (!expression && modifiers?.Any(m => m.Kind == K.ExportKeyword) == true)
            context |= NodeFlags.AwaitContext;
        var parameters = (await ParametersCore(signatureFlags).ConfigureAwait(false));
        var type = (await ReturnAnnotationCore().ConfigureAwait(false));
        var body = (await FunctionBodyCore(signatureFlags, allowSemicolon: !expression).ConfigureAwait(false));
        context = old;
        return expression
            ? Finish(factory.NewFunctionExpression(modifiers, star, name, typeParameters, parameters, type, null, body), start)
            : Finish(factory.NewFunctionDeclaration(modifiers, star, name, typeParameters, parameters, type, null, body), start);
    }

    private async ValueTask<SyntaxNode> ClassCore(bool expression, NodeList? modifiers, int start)
    {
        await ParseStack;
        Expected(K.ClassKeyword);
        var name = IsBindingIdentifier && !(Token == K.ImplementsKeyword && Peek(() => Next() >= K.Identifier))
            ? Identifier(binding: true)
            : null;
        var parameters = (await TypeParametersCore().ConfigureAwait(false));
        NodeFlags saved = context;
        if (statementDepth == 0 && modifiers?.Any(m => m.Kind == K.ExportKeyword) == true)
            context |= NodeFlags.AwaitContext;
        var heritage = (await HeritageCore().ConfigureAwait(false));
        NodeList members;
        if (Expected(K.OpenBraceToken))
        {
            members = await ListCore(K.CloseBraceToken, () => TypeMemberCore(true), stop: ClassMemberBoundary).ConfigureAwait(false);
            Expected(K.CloseBraceToken);
        }
        else
            members = new NodeList([], Pos, Pos, true);
        context = saved;
        return expression
            ? Finish(factory.NewClassExpression(modifiers, name, parameters, heritage, members), start)
            : Finish(factory.NewClassDeclaration(modifiers, name, parameters, heritage, members), start);
    }

    private bool ClassMemberBoundary()
    {
        while (Token is not (K.CloseBraceToken or K.EndOfFile) && !Peek(StartsClassMember))
        {
            if (StartsStatement())
                return true;
            Error(Messages.Unexpected_token_A_constructor_method_accessor_or_property_was_expected);
            Next();
        }
        return Token is K.CloseBraceToken or K.EndOfFile;
    }

    private bool StartsClassMember()
    {
        if (Token is K.AtToken or K.SemicolonToken)
            return true;
        K nameKind = K.Unknown;
        while (IsModifierKind(Token))
        {
            nameKind = Token;
            if (Token is K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.ReadonlyKeyword or K.StaticKeyword
                or K.OverrideKeyword or K.AccessorKeyword)
                return true;
            Next();
        }
        if (Token == K.AsteriskToken)
            return true;
        if (Token >= K.Identifier || Token is K.PrivateIdentifier or K.StringLiteral or K.NumericLiteral or K.BigIntLiteral)
        {
            nameKind = Token;
            Next();
        }
        if (Token == K.OpenBracketToken)
            return true;
        if (nameKind == K.Unknown)
            return false;
        if (nameKind is not (>= K.FirstKeyword and <= K.LastKeyword) || nameKind is K.GetKeyword or K.SetKeyword)
            return true;
        return Token is K.OpenParenToken or K.LessThanToken or K.ExclamationToken or K.ColonToken or K.EqualsToken or K.QuestionToken
            || IsSemicolon();
    }

    private async ValueTask<SyntaxNode> InterfaceCore(NodeList? modifiers, int start)
    {
        await ParseStack;
        Next();
        var name = Identifier();
        var parameters = (await TypeParametersCore().ConfigureAwait(false));
        var heritage = (await HeritageCore(true).ConfigureAwait(false));
        Expected(K.OpenBraceToken);
        var members = (await ListCore(
            K.CloseBraceToken,
            async () => (await TypeMemberCore(false).ConfigureAwait(false)), typeMembers: true).ConfigureAwait(false));
        Expected(K.CloseBraceToken);
        return Finish(factory.NewInterfaceDeclaration(modifiers, name, parameters, heritage, members), start);
    }

    private async ValueTask<NodeList?> HeritageCore(bool isInterface = false)
    {
        await ParseStack;
        int start = Pos;
        var clauses = new List<SyntaxNode>();
        while (Token is K.ExtendsKeyword or K.ImplementsKeyword)
        {
            int clauseStart = Pos;
            K keyword = Token;
            Next();
            int typesStart = Pos;
            var types = new List<SyntaxNode>();
            while (Token is not (K.ExtendsKeyword or K.ImplementsKeyword or K.EndOfFile) &&
                (Token != K.OpenBraceToken
                    || Peek(
                        () => Next() != K.CloseBraceToken
                            || Next() is K.OpenBraceToken or K.CommaToken or K.ExtendsKeyword or K.ImplementsKeyword)))
            {
                if (!StartsHeritageExpression())
                {
                    Error(Messages.Expression_expected);
                    if (!reparsingTopLevelAwait && StartsStatement())
                        break;
                    Next();
                    continue;
                }
                types.Add(
                    await HeritageTypeCore(
                        isInterface && keyword == K.ExtendsKeyword
                            || !isInterface && keyword == K.ImplementsKeyword).ConfigureAwait(false));
                if (!Take(K.CommaToken))
                {
                    if (Token is K.OpenBraceToken or K.CloseBraceToken or K.ExtendsKeyword or K.ImplementsKeyword or K.EndOfFile)
                        break;
                    Error(Messages.X_0_expected, ",");
                }
            }
            clauses.Add(Finish(factory.NewHeritageClause(keyword, new(types.ToArray(), typesStart, Pos)), clauseStart));
        }

        return clauses.Count == 0 ? null : new(clauses.ToArray(), start, Pos);
    }

    private bool StartsHeritageExpression() => IsIdentifier || Token is K.ThisKeyword or K.SuperKeyword or K.NullKeyword
        or K.TrueKeyword or K.FalseKeyword or K.NumericLiteral or K.BigIntLiteral or K.StringLiteral
        or K.NoSubstitutionTemplateLiteral or K.TemplateHead or K.OpenParenToken or K.OpenBracketToken or K.OpenBraceToken
        or K.FunctionKeyword or K.ClassKeyword or K.NewKeyword or K.SlashToken or K.SlashEqualsToken
        || Token == K.ImportKeyword && Peek(() => Next() is K.OpenParenToken or K.LessThanToken or K.DotToken);

    private async ValueTask<SyntaxNode> TypeMemberCore(bool inClass)
    {
        await ParseStack;
        NodeFlags saved = context;
        TokenFlags trivia = scanner.Flags;
        try
        {
            return (await WithJSDocCore((await TypeMemberWorkerCore(inClass).ConfigureAwait(false)), trivia).ConfigureAwait(false));
        }
        finally
        {
            context = saved;
        }
    }

    private bool ScanTypeMemberStart()
    {
        if (Token is K.OpenParenToken or K.LessThanToken or K.GetKeyword or K.SetKeyword)
            return true;
        bool identifier = false;
        while (IsModifierKind(Token))
        {
            identifier = true;
            Next();
        }
        if (Token == K.OpenBracketToken)
            return true;
        if (IsIdentifier || Token is >= K.FirstKeyword and <= K.LastKeyword
            or K.PrivateIdentifier or K.StringLiteral or K.NumericLiteral or K.BigIntLiteral)
        {
            identifier = true;
            Next();
        }
        return identifier && (Token is K.OpenParenToken or K.LessThanToken or K.QuestionToken or K.ColonToken or K.CommaToken
            || IsSemicolon());
    }

    private async ValueTask<SyntaxNode> TypeMemberWorkerCore(bool inClass)
    {
        await ParseStack;
        int start = Pos;
        if (Take(K.SemicolonToken))
            return Finish(factory.NewSemicolonClassElement(), start);
        var modifiers = (await ModifiersCore(inClass, inClass).ConfigureAwait(false));
        if (inClass && Token == K.StaticKeyword && NextIs(K.OpenBraceToken))
        {
            Next();
            NodeFlags saved = context;
            context = (context & ~NodeFlags.YieldContext) | NodeFlags.AwaitContext;
            var body = (await BlockCore().ConfigureAwait(false));
            context = saved;
            return Finish(factory.NewClassStaticBlockDeclaration(modifiers, body), start);
        }

        if (!inClass && Token is K.OpenParenToken or K.LessThanToken)
        {
            var types = (await TypeParametersCore().ConfigureAwait(false));
            var parameters = (await ParametersCore().ConfigureAwait(false));
            var type = (await ReturnAnnotationCore().ConfigureAwait(false));
            MemberSemicolon();
            return Finish(factory.NewCallSignatureDeclaration(types, parameters, type), start);
        }

        if (!inClass && Token == K.NewKeyword && Peek(() => Next() is K.OpenParenToken or K.LessThanToken))
        {
            Next();
            var types = (await TypeParametersCore().ConfigureAwait(false));
            var parameters = (await ParametersCore().ConfigureAwait(false));
            var type = (await ReturnAnnotationCore().ConfigureAwait(false));
            MemberSemicolon();
            return Finish(factory.NewConstructSignatureDeclaration(types, parameters, type), start);
        }

        if (IsIndexSignature())
            return await IndexSignatureCore(modifiers, start).ConfigureAwait(false);

        K accessor = K.Unknown;
        if (Token is K.GetKeyword or K.SetKeyword && Peek(() =>
        {
            Next();
            return Token is K.Identifier or K.PrivateIdentifier or K.StringLiteral or K.NumericLiteral or K.BigIntLiteral
                or K.OpenBracketToken or >= K.FirstKeyword and <= K.LastKeyword;
        }))
        {
            accessor = Token;
            Next();
        }

        var star = OptionalToken(K.AsteriskToken);
        bool constructor = inClass
            && star is null
            && accessor == K.Unknown
            && (Token == K.ConstructorKeyword || Token == K.StringLiteral && scanner.Value == "constructor" && NextIs(K.OpenParenToken));
        if (accessor == K.Unknown && !constructor)
            AmbientModifiers(modifiers);
        SyntaxNode memberName = (await NameCore().ConfigureAwait(false));
        var postfix = Token is K.QuestionToken or K.ExclamationToken ? ParseToken() : null;
        if (Token is K.OpenParenToken or K.LessThanToken || accessor != K.Unknown || constructor || star is not null)
        {
            NodeFlags signatureFlags = (accessor == K.Unknown && star is not null
                ? NodeFlags.YieldContext
                : 0) | (accessor == K.Unknown && modifiers?.Any(m => m.Kind == K.AsyncKeyword) == true ? NodeFlags.AwaitContext : 0);
            var types = (await TypeParametersCore().ConfigureAwait(false));
            var parameters = (await ParametersCore(signatureFlags).ConfigureAwait(false));
            var type = (await ReturnAnnotationCore().ConfigureAwait(false));
            BlockNode? body = inClass || accessor != K.Unknown && Token == K.OpenBraceToken
                ? (await FunctionBodyCore(signatureFlags, inClass).ConfigureAwait(false))
                : null;
            if (!inClass && body is null)
                MemberSemicolon();
            if (accessor == K.GetKeyword)
                return Finish(factory.NewGetAccessorDeclaration(modifiers, memberName, types, parameters, type, null, body), start);
            if (accessor == K.SetKeyword)
                return Finish(factory.NewSetAccessorDeclaration(modifiers, memberName, types, parameters, type, null, body), start);
            if (constructor)
                return Finish(factory.NewConstructorDeclaration(modifiers, types, parameters, type, null, body), start);
            return inClass
                ? Finish(factory.NewMethodDeclaration(modifiers, star, memberName, postfix, types, parameters, type, null, body), start)
                : Finish(factory.NewMethodSignatureDeclaration(modifiers, memberName, postfix, types, parameters, type), start);
        }

        var propertyType = (await AnnotationCore().ConfigureAwait(false));
        NodeFlags savedPropertyContext = context;
        context &= ~(NodeFlags.AwaitContext | NodeFlags.YieldContext | NodeFlags.DisallowInContext);
        var initializer = (await InitializerCore().ConfigureAwait(false));
        context = savedPropertyContext;
        if (inClass && Token == K.OpenParenToken)
        {
            Error(Messages.Cannot_start_a_function_call_in_a_type_annotation);
            Next();
        }
        else
            MemberSemicolon();
        return inClass
            ? Finish(factory.NewPropertyDeclaration(modifiers, memberName, postfix, propertyType, initializer), start)
            : Finish(factory.NewPropertySignatureDeclaration(modifiers, memberName, postfix, propertyType, initializer), start);
    }

    private void MemberSemicolon()
    {
        if (!Take(K.CommaToken))
            Semicolon();
    }

    private async ValueTask<SyntaxNode> ModuleCore(NodeList? modifiers, int start)
    {
        await ParseStack;
        K keyword = Token;
        int nameStart = Pos;
        Next();
        SyntaxNode name = keyword == K.GlobalKeyword
            ? Finish(factory.NewIdentifier("global"), nameStart, Pos)
            : (await NameCore().ConfigureAwait(false));
        return (await ModuleRestCore(modifiers, start, keyword, name).ConfigureAwait(false));
    }

    private async ValueTask<SyntaxNode> ModuleRestCore(NodeList? modifiers, int start, K keyword, SyntaxNode name)
    {
        await ParseStack;
        TypeLiteralNode? attributes = name is StringLiteralNode && Take(K.WithKeyword)
            ? (TypeLiteralNode)(await TypeCore().ConfigureAwait(false))
            : null;
        SyntaxNode? body;
        if (Take(K.DotToken))
        {
            int nested = Pos;
            var implicitExport = factory.NewToken(K.ExportKeyword);
            implicitExport.Pos = implicitExport.End = nested;
            implicitExport.Flags = NodeFlags.Reparsed;
            body = (await ModuleRestCore(new([implicitExport], nested, nested), nested, keyword, Identifier(true)).ConfigureAwait(false));
        }
        else if (Token == K.OpenBraceToken)
        {
            int blockStart = Pos;
            Next();
            statementDepth++;
            NodeList statements;
            try
            {
                statements = (await ListCore(K.CloseBraceToken, ParseStatementCore, true).ConfigureAwait(false));
            }
            finally
            {
                statementDepth--;
            }

            Expected(K.CloseBraceToken);
            body = Finish(factory.NewModuleBlock(statements), blockStart);
        }
        else
        {
            Semicolon();
            body = null;
        }

        return Finish(factory.NewModuleDeclaration(modifiers, keyword, name, attributes, body), start);
    }

    private async ValueTask<SyntaxNode> ImportCore(NodeList? modifiers, int start)
    {
        await ParseStack;
        int importTokenStart = scanner.TokenStart;
        Expected(K.ImportKeyword);
        if (!IsBindingIdentifier && Token is not (K.StringLiteral or K.AsteriskToken or K.OpenBraceToken))
        {
            ErrorAt(Messages.Declaration_or_statement_expected, importTokenStart, 6);
            return await ParseStatementWorkerCore().ConfigureAwait(false);
        }
        bool savedPossibleAwait = possibleTopLevelAwait;
        int clauseStart = Pos;
        IdentifierNode? name = IsBindingIdentifier ? Identifier(binding: true) : null;
        K phase = K.Unknown;
        if (name?.Text == "type"
            && (Token != K.FromKeyword || IsIdentifier && Peek(() => Next() is K.FromKeyword or K.EqualsToken))
            && (IsIdentifier || Token is K.AsteriskToken or K.OpenBraceToken))
        {
            phase = K.TypeKeyword;
            name = IsIdentifier ? Identifier() : null;
        }
        else if (name?.Text == "defer"
            && (Token == K.FromKeyword ? !NextIs(K.StringLiteral) : Token is not (K.CommaToken or K.EqualsToken)))
        {
            phase = K.DeferKeyword;
            name = IsIdentifier ? Identifier() : null;
        }

        if (name is not null && Token is not (K.CommaToken or K.FromKeyword) && phase != K.DeferKeyword)
        {
            Expected(K.EqualsToken);
            SyntaxNode reference;
            if (Token == K.RequireKeyword && NextIs(K.OpenParenToken))
            {
                int referenceStart = Pos;
                Next();
                Expected(K.OpenParenToken);
                var expr = Token == K.StringLiteral ? Literal() : await ExpressionCore().ConfigureAwait(false);
                Expected(K.CloseParenToken);
                reference = Finish(factory.NewExternalModuleReference(expr), referenceStart);
            }
            else
                reference = EntityName(allowReserved: false);
            Semicolon();
            possibleTopLevelAwait = savedPossibleAwait;
            return Finish(factory.NewImportEqualsDeclaration(modifiers, phase == K.TypeKeyword, name, reference), start);
        }

        ImportClauseNode? clause = null;
        if (name is not null || Token is K.AsteriskToken or K.OpenBraceToken)
        {
            SyntaxNode? bindings = null;
            if (name is null || Take(K.CommaToken))
            {
                int bindingsStart = Pos;
                if (Take(K.AsteriskToken))
                {
                    Expected(K.AsKeyword);
                    bindings = Finish(factory.NewNamespaceImport(Identifier()), bindingsStart);
                }
                else
                {
                    bool opened = Expected(K.OpenBraceToken);
                    var elements = opened
                        ? await DelimitedCore(
                            K.CloseBraceToken,
                            async () => await ImportExportSpecifierCore(false).ConfigureAwait(false),
                            stop: () => Token == K.FromKeyword && NextIs(K.StringLiteral),
                            startsElement: () => Token >= K.Identifier || Token == K.StringLiteral,
                            elementExpected: Messages.Identifier_expected).ConfigureAwait(false)
                        : new NodeList([], Pos, Pos, true);
                    if (opened)
                        Expected(K.CloseBraceToken);
                    bindings = Finish(factory.NewNamedImports(elements), bindingsStart);
                }
            }

            clause = Finish(factory.NewImportClause(phase, name, bindings), clauseStart);
            Expected(K.FromKeyword);
        }

        possibleTopLevelAwait = savedPossibleAwait;
        SyntaxNode specifier = Token == K.StringLiteral ? Literal() : await ExpressionCore().ConfigureAwait(false);
        ImportAttributesNode? attributes = null;
        if (Token == K.WithKeyword || !LineBreak && Token == K.AssertKeyword)
        {
            K token = Token;
            int attributesStart = Pos;
            if (token == K.AssertKeyword)
                Error(Messages.Import_assertions_have_been_replaced_by_import_attributes_Use_with_instead_of_assert);
            Next();
            attributes = (await ImportAttributesCore(token, attributesStart).ConfigureAwait(false));
        }

        Semicolon();
        return Finish(factory.NewImportDeclaration(K.ImportDeclaration, modifiers, clause, specifier, attributes), start);
    }

    private async ValueTask<SyntaxNode> ImportExportSpecifierCore(bool export)
    {
        await ParseStack;
        int start = Pos;
        TokenFlags trivia = scanner.Flags;
        bool typeOnly = false, canParseAs = true;
        SyntaxNode ModuleName(out bool valid, out int tokenStart)
        {
            tokenStart = scanner.TokenStart;
            valid = export || Token == K.StringLiteral || IsBindingIdentifier;
            return Token == K.StringLiteral ? Literal() : Identifier(true);
        }
        bool CanModuleName() => Token == K.Identifier || Token is >= K.FirstKeyword and <= K.LastKeyword || Token == K.StringLiteral;
        SyntaxNode? property = null;
        SyntaxNode name = ModuleName(out bool nameOk, out int nameStart);
        if (name is IdentifierNode { Text.Span: "type" })
        {
            if (Token == K.AsKeyword)
            {
                int firstAsStart = scanner.TokenStart;
                SyntaxNode firstAs = Identifier(true);
                if (Token == K.AsKeyword)
                {
                    int secondAsStart = scanner.TokenStart;
                    SyntaxNode secondAs = Identifier(true);
                    if (CanModuleName())
                    {
                        typeOnly = true;
                        property = firstAs;
                        name = ModuleName(out nameOk, out nameStart);
                    }
                    else
                    {
                        property = name;
                        name = secondAs;
                        nameStart = secondAsStart;
                    }
                    canParseAs = false;
                }
                else if (CanModuleName())
                {
                    property = name;
                    name = ModuleName(out nameOk, out nameStart);
                    canParseAs = false;
                }
                else
                {
                    typeOnly = true;
                    name = firstAs;
                    nameStart = firstAsStart;
                }
            }
            else if (CanModuleName())
            {
                typeOnly = true;
                name = ModuleName(out nameOk, out nameStart);
            }
        }
        if (canParseAs && Take(K.AsKeyword))
        {
            property = name;
            name = ModuleName(out nameOk, out nameStart);
        }
        if (!nameOk)
            ErrorAt(Messages.Identifier_expected, nameStart, name.End - nameStart);
        if (!export && name is not IdentifierNode)
        {
            ErrorAt(Messages.Identifier_expected, nameStart, name.End - nameStart);
            name = Finish(factory.NewIdentifier(""), name.Pos, name.End);
        }
        return export
            ? await WithJSDocCore(Finish(factory.NewExportSpecifier(typeOnly, property, name), start), trivia).ConfigureAwait(false)
            : Finish(factory.NewImportSpecifier(typeOnly, property, (IdentifierNode)name), start);
    }

    private async ValueTask<ImportAttributesNode> ImportAttributesCore(K token, int? originalStart = null)
    {
        await ParseStack;
        int start = originalStart ?? Pos;
        if (!Expected(K.OpenBraceToken))
            return Finish(factory.NewImportAttributes(token, new NodeList([], Pos, Pos), false), start);
        bool multiline = LineBreak;
        var attributes = (await DelimitedCore(K.CloseBraceToken, async () =>
        {
            int at = Pos;
            SyntaxNode? name = Token == K.StringLiteral ? Literal()
                : Token >= K.Identifier ? Identifier(true) : null;
            if (name is not null)
                Expected(K.ColonToken);
            else
                Error(Messages.Identifier_or_string_literal_expected);
            return Finish(factory.NewImportAttribute(name, (await ExpressionCore(2).ConfigureAwait(false))), at);
        }, startsElement: () => Token >= K.Identifier || Token == K.StringLiteral,
            elementExpected: Messages.Identifier_or_string_literal_expected).ConfigureAwait(false));
        Expected(K.CloseBraceToken);
        return Finish(factory.NewImportAttributes(token, attributes, multiline), start);
    }

    private async ValueTask<SyntaxNode> ExportCore(NodeList? leadingModifiers = null, int? originalStart = null)
    {
        await ParseStack;
        int start = originalStart ?? Pos;
        Expected(K.ExportKeyword);
        if (Take(K.AsKeyword))
        {
            Expected(K.NamespaceKeyword);
            var name = Identifier();
            Semicolon();
            return Finish(factory.NewNamespaceExportDeclaration(leadingModifiers, name), start);
        }

        if (Token == K.EqualsToken)
        {
            NodeFlags saved = context;
            context |= NodeFlags.AwaitContext;
            Next();
            var value = (await ExpressionCore(2).ConfigureAwait(false));
            Semicolon();
            context = saved;
            return Finish(factory.NewExportAssignment(leadingModifiers, true, null, value), start);
        }

        if (Token == K.DefaultKeyword)
        {
            if (Peek(() =>
            {
                Next();
                return Token is K.FunctionKeyword or K.ClassKeyword or K.InterfaceKeyword or K.AtToken
                    || Token == K.AbstractKeyword && NextIs(K.ClassKeyword)
                    || Token == K.AsyncKeyword && Peek(() => Next() == K.FunctionKeyword && !LineBreak);
            }))
            {
                scanner.ResetPosition(start);
                Next(false);
                return (await DeclarationWithExportCore().ConfigureAwait(false));
            }

            Next();
            NodeFlags saved = context;
            context |= NodeFlags.AwaitContext;
            var expression = (await ExpressionCore(2).ConfigureAwait(false));
            Semicolon();
            context = saved;
            return Finish(factory.NewExportAssignment(leadingModifiers, false, null, expression), start);
        }

        bool typeOnly = Token == K.TypeKeyword && Peek(() => Next() is K.OpenBraceToken or K.AsteriskToken);
        if (typeOnly)
            Next();
        if (Token is K.OpenBraceToken or K.AsteriskToken)
        {
            NodeFlags saved = context;
            context |= NodeFlags.AwaitContext;
            SyntaxNode? clause = null;
            SyntaxNode? specifier = null;
            int clauseStart = Pos;
            if (Take(K.AsteriskToken))
            {
                if (Take(K.AsKeyword))
                    clause = Finish(factory.NewNamespaceExport(Token == K.StringLiteral ? Literal() : Identifier(true)), clauseStart);
                Expected(K.FromKeyword);
                specifier = Token == K.StringLiteral ? Literal() : await ExpressionCore().ConfigureAwait(false);
            }
            else
            {
                Next();
                NodeList elements = (await DelimitedCore(
                    K.CloseBraceToken,
                    async () => (await ImportExportSpecifierCore(true).ConfigureAwait(false)),
                    stop: () => Token == K.FromKeyword && NextIs(K.StringLiteral),
                    startsElement: () => Token >= K.Identifier || Token == K.StringLiteral,
                    elementExpected: Messages.Identifier_expected).ConfigureAwait(false));
                Expected(K.CloseBraceToken);
                clause = Finish(factory.NewNamedExports(elements), clauseStart);
                if (Token == K.FromKeyword || Token == K.StringLiteral && !LineBreak)
                {
                    Expected(K.FromKeyword);
                    specifier = Token == K.StringLiteral ? Literal() : await ExpressionCore().ConfigureAwait(false);
                }
            }
            ImportAttributesNode? attributes = null;
            if (specifier is not null && !LineBreak && Token is K.WithKeyword or K.AssertKeyword)
            {
                K token = Token;
                int attributesStart = Pos;
                if (token == K.AssertKeyword)
                    Error(Messages.Import_assertions_have_been_replaced_by_import_attributes_Use_with_instead_of_assert);
                Next();
                attributes = (await ImportAttributesCore(token, attributesStart).ConfigureAwait(false));
            }

            Semicolon();
            context = saved;
            return Finish(factory.NewExportDeclaration(leadingModifiers, typeOnly, clause, specifier, attributes), start);
        }

        // Parse a declaration with the export token still part of its modifier list.
        scanner.ResetPosition(start);
        Next(false);
        return (await DeclarationWithExportCore().ConfigureAwait(false));
    }

    private async ValueTask<SyntaxNode> DeclarationWithExportCore()
    {
        await ParseStack;
        return (await ParseStatementWorkerCore(true).ConfigureAwait(false));
    }
}

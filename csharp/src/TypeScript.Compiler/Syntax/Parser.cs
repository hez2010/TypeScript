using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public enum ScriptKind
{
    Unknown,
    JS,
    JSX,
    TS,
    TSX,
    External,
    JSON,
    Deferred
}

public readonly record struct ParseOptions(
    string FileName,
    ScriptKind ScriptKind = ScriptKind.Unknown,
    int TargetYear = int.MaxValue,
    bool ForceExternalModule = false,
    bool JsxExternalModule = false,
    bool CheckRegularExpressions = false);
public sealed partial class Parser
{
    private readonly Scanner scanner;
    private readonly NodeFactory factory = new();
    private readonly SourceText source;
    private readonly ParseOptions options;
    private readonly CancellationToken cancellation;
    private readonly List<Diagnostic> diagnostics = [];
    private NodeFlags context;
    private NodeFlags sourceFlags;
    private bool hasError;
    private int scannedDiagnostics;
    private int statementDepth;
    private int objectLiteralDepth;
    private int variableDeclarationDepth;
    private bool reparsingTopLevelAwait;
    private bool possibleTopLevelAwait;
    private List<(int Start, int End)>? topLevelAwaitSpans;
    private readonly List<(int Start, int End)> possibleAwaitSpans = [];
    private int speculationDepth;
    private readonly Dictionary<SyntaxNode, JSDocNode[]> documentation = new(ReferenceEqualityComparer.Instance);
    private readonly List<Diagnostic> documentationDiagnostics = [];
    private readonly List<SyntaxNode> reparsedClones = [];
    private List<SyntaxNode> reparsedStatements = [];
    private K Token => scanner.Kind;
    private int Pos => scanner.FullStart;
    private bool LineBreak => scanner.HasPrecedingLineBreak;

    private Parser(
        ParseOptions options,
        SourceText source,
        CancellationToken cancellation,
        int initialPosition = 0,
        int? endPosition = null,
        bool documentation = false)
    {
        ScriptKind kind = options.ScriptKind;
        if (kind == ScriptKind.Unknown)
            kind = CompilerPath.Extension(options.FileName).ToLowerInvariant() switch
            {
                ".js" or ".mjs" or ".cjs" => ScriptKind.JS,
                ".jsx" => ScriptKind.JSX,
                ".tsx" => ScriptKind.TSX,
                ".json" => ScriptKind.JSON,
                _ => ScriptKind.TS
            };
        this.options = options with
        {
            ScriptKind = kind
        };
        this.source = source;
        this.cancellation = cancellation;
        scanner = new(source, true, kind is ScriptKind.JS or ScriptKind.JSX or ScriptKind.TSX)
        {
            TargetYear = options.TargetYear
        };
        if (kind is ScriptKind.JS or ScriptKind.JSX or ScriptKind.JSON)
            context = NodeFlags.JavaScriptFile;
        if (kind == ScriptKind.JSON)
            context |= NodeFlags.JsonFile;
        if (CompilerPath.IsDeclarationFile(options.FileName))
            context |= NodeFlags.Ambient;
        scanner.SetTextRange(initialPosition, endPosition ?? source.Length);
        if (documentation)
        {
            context = NodeFlags.JSDoc | (kind is ScriptKind.JS or ScriptKind.JSX ? NodeFlags.JavaScriptFile : 0);
            scanner.SetSkipJSDocLeadingAsterisks(true);
        }

        Next();
    }

    public static SourceFileNode ParseSourceFile(ParseOptions options, SourceText source, CancellationToken cancellation = default) =>
        RunParse(ParseSourceFileAsync(options, source, cancellation));

    internal static SyntaxNode? ParseIsolatedEntityName(TextSlice text)
    {
        var parser = new Parser(new("", ScriptKind.JS), new SourceText(text), default);
        var name = parser.EntityName();
        return parser.Token == K.EndOfFile && parser.diagnostics.Count == 0 ? name : null;
    }

    public static async ValueTask<SourceFileNode> ParseSourceFileAsync(
        ParseOptions options,
        SourceText source,
        CancellationToken cancellation = default)
    {
        var parser = new Parser(options, source, cancellation);
        var file = await parser.ParseFileCore().ConfigureAwait(false);
        if (parser.possibleAwaitSpans.Count != 0
            && file.ExternalModuleIndicator is not null
            && !file.IsDeclarationFile
            && file.ScriptKind != ScriptKind.JSON)
        {
            parser = new Parser(options, source, cancellation) { topLevelAwaitSpans = parser.possibleAwaitSpans };
            file = await parser.ParseFileCore().ConfigureAwait(false);
        }
        return file;
    }

    private async ValueTask<SourceFileNode> ParseFileCore()
    {
        await ParseStack;
        int start = Pos;
        int awaitSpan = 0;
        var statements = new List<SyntaxNode>();
        if (options.ScriptKind == ScriptKind.JSON)
        {
            var expressions = new List<SyntaxNode>();
            while (Token != K.EndOfFile)
            {
                int before = Pos;
                var expression = Token switch
                {
                    K.OpenBracketToken or K.TrueKeyword or K.FalseKeyword or K.NullKeyword => await PrimaryExpressionCore().ConfigureAwait(false),
                    K.MinusToken when Peek(() => Next() == K.NumericLiteral && Next() != K.ColonToken)
                        => await UnaryExpressionCore().ConfigureAwait(false),
                    K.NumericLiteral or K.StringLiteral when !NextIs(K.ColonToken) => Literal(),
                    _ => await ObjectLiteralCore().ConfigureAwait(false)
                };
                expressions.Add(expression);
                if (Token != K.EndOfFile)
                    Error(Messages.Unexpected_token);
                if (Pos == before)
                    Next();
            }

            if (expressions.Count != 0)
            {
                SyntaxNode expression = expressions.Count == 1
                    ? expressions[0]
                    : Finish(factory.NewArrayLiteralExpression(new(expressions.ToArray(), start, Pos), false), start);
                statements.Add(Finish(factory.NewExpressionStatement(expression), expression.Pos, expression.End));
                bool saved = hasError;
                ValidateJson(expression);
                hasError = saved;
            }
        }
        else
            while (Token != K.EndOfFile)
            {
                if (!StartsStatement())
                {
                    if (Token == K.DefaultKeyword)
                        Error(Messages.X_0_expected, "export");
                    else
                        Error(Messages.Declaration_or_statement_expected);
                    Next();
                    continue;
                }
                int before = Pos;
                NodeFlags statementContext = context;
                bool reparseAwait = topLevelAwaitSpans is not null && awaitSpan < topLevelAwaitSpans.Count
                    && before >= topLevelAwaitSpans[awaitSpan].Start;
                if (reparseAwait)
                    context |= NodeFlags.AwaitContext;
                possibleTopLevelAwait = false;
                reparsingTopLevelAwait = reparseAwait;
                SyntaxNode statement = (await ParseStatementCore().ConfigureAwait(false));
                reparsingTopLevelAwait = false;
                context = statementContext;
                if (topLevelAwaitSpans is null && possibleTopLevelAwait && (statement.Flags & NodeFlags.AwaitContext) == 0)
                {
                    if (possibleAwaitSpans.Count != 0 && possibleAwaitSpans[^1].End == before)
                        possibleAwaitSpans[^1] = (possibleAwaitSpans[^1].Start, Pos);
                    else
                        possibleAwaitSpans.Add((before, Pos));
                }
                if (reparseAwait)
                {
                    // A changed parse can consume the boundary; extend through the next
                    // marked span (or EOF) before returning to the original context.
                    while (awaitSpan < topLevelAwaitSpans!.Count && Pos > topLevelAwaitSpans[awaitSpan].End)
                    {
                        int spanStart = topLevelAwaitSpans[awaitSpan++].Start;
                        if (awaitSpan == topLevelAwaitSpans.Count)
                            topLevelAwaitSpans.Add((spanStart, source.Text.Length));
                        else
                            topLevelAwaitSpans[awaitSpan] = (spanStart, topLevelAwaitSpans[awaitSpan].End);
                    }
                    if (Pos == topLevelAwaitSpans[awaitSpan].End)
                        awaitSpan++;
                }
                statements.AddRange(reparsedStatements);
                reparsedStatements.Clear();
                statements.Add(statement);
                if (Pos == before)
                {
                    Error(Messages.Declaration_or_statement_expected);
                    Next();
                }
            }

        int end = Pos;
        TokenFlags endTrivia = scanner.Flags;
        var eof = (await WithJSDocCore(ParseToken(), endTrivia).ConfigureAwait(false));
        statements.AddRange(reparsedStatements);
        reparsedStatements.Clear();
        SourceFileNode file = Finish(factory.NewSourceFile(new(statements.ToArray(), start, end), eof), start);
        file.Flags |= sourceFlags;
        file.FileName = options.FileName;
        file.Source = source;
        file.ScriptKind = options.ScriptKind;
        file.IsDeclarationFile = CompilerPath.IsDeclarationFile(options.FileName);
        ProcessSourceMetadata(file);
        file.NodeCount = factory.NodeCount;
        // Source positions remain bytes at the public AST boundary; scanning uses UTF-16.
        if (!source.IsAsciiOnly)
        {
            Func<int, int> toBytePosition = source.ToBytePosition;
            foreach (SyntaxNode node in file.DescendantsAndSelf())
                node.ConvertPositions(toBytePosition);
            foreach (JSDocNode comment in documentation.Values.SelectMany(nodes => nodes).Distinct())
                foreach (SyntaxNode node in comment.DescendantsAndSelf())
                    node.ConvertPositions(toBytePosition);
        }
        file.SetDocumentation(documentation);
        file.JSDocDiagnostics = documentationDiagnostics.Select(
            d => d with
            {
                Start = source.ToBytePosition(d.Start),
                Length = source.ToBytePosition(d.Start + d.Length) - source.ToBytePosition(d.Start),
                RelatedInformation = d.RelatedInformation.Select(r => r with
                {
                    Start = source.ToBytePosition(r.Start),
                    Length = source.ToBytePosition(r.Start + r.Length) - source.ToBytePosition(r.Start),
                    FileName = options.FileName
                }).ToArray(),
                FileName = options.FileName
            }).ToArray();
        file.ReparsedClones = reparsedClones.OrderBy(n => n.Pos).ThenBy(n => n.End).ToArray();
        file.ParseDiagnostics = diagnostics.Select(
            d => d with
            {
                Start = source.ToBytePosition(d.Start),
                Length = source.ToBytePosition(d.Start + d.Length) - source.ToBytePosition(d.Start),
                FileName = options.FileName
            }).ToArray();
        file.CommentDirectives = scanner.CommentDirectives.Select(
            d => d with { Start = source.ToBytePosition(d.Start), End = source.ToBytePosition(d.End) }).ToArray();
        CheckJavaScriptSyntax(file);
        return file;
    }

    private K Next(bool checkEscapes = true)
    {
        cancellation.ThrowIfCancellationRequested();
        if (checkEscapes
            && Token is >= K.FirstKeyword and <= K.LastKeyword
            && (scanner.Flags & (TokenFlags.UnicodeEscape | TokenFlags.ExtendedUnicodeEscape)) != 0)
            Error(Messages.Keywords_cannot_contain_escape_characters);
        scanner.Scan();
        for (; scannedDiagnostics < scanner.Diagnostics.Count; scannedDiagnostics++)
        {
            Diagnostic d = scanner.Diagnostics[scannedDiagnostics];
            ErrorAt(d.Message, d.Start, d.Length, d.Arguments);
        }

        return Token;
    }

    private void Error(DiagnosticMessage message, params TextSlice[] args) =>
        ErrorAt(message, scanner.TokenStart, scanner.Position - scanner.TokenStart, args);

    private void ErrorAt(DiagnosticMessage message, int start, int length, params TextSlice[] args)
    {
        if (diagnostics.Count == 0 || diagnostics[^1].Start != start)
            diagnostics.Add(new(message, start, length, args));
        hasError = true;
    }

    private T Finish<T>(T node, int start, int? end = null)
        where T : SyntaxNode
    {
        node.Pos = start;
        node.End = end ?? Pos;
        node.Flags |= context;
        if (hasError)
        {
            node.Flags |= NodeFlags.ThisNodeHasError;
            hasError = false;
        }

        node.SetChildParents();
        return node;
    }

    private bool Take(K kind)
    {
        if (Token != kind)
            return false;
        Next();
        return true;
    }

    private bool Expected(K kind)
    {
        if (Take(kind))
            return true;
        Error(Messages.X_0_expected, TokenFacts.Text(kind));
        return false;
    }

    private TokenNode ParseToken()
    {
        int start = Pos;
        var node = factory.NewToken(Token);
        Next();
        return Finish(node, start);
    }

    private TokenNode? OptionalToken(K kind) => Token == kind ? ParseToken() : null;

    private TokenNode ExpectedToken(K kind)
    {
        if (Token == kind)
            return ParseToken();
        Error(Messages.X_0_expected, TokenFacts.Text(kind));
        return Finish(factory.NewToken(kind), Pos, Pos);
    }

    private bool IsBindingIdentifier => Token == K.Identifier || Token > K.LastReservedWord;
    private bool IsIdentifier =>
        IsBindingIdentifier
            && !(Token == K.YieldKeyword && (context & NodeFlags.YieldContext) != 0)
            && !(Token == K.AwaitKeyword && (context & NodeFlags.AwaitContext) != 0);

    private static bool IsModifierKind(K kind) =>
        kind is K.AbstractKeyword or K.AccessorKeyword or K.AsyncKeyword or K.ConstKeyword or K.DeclareKeyword or K.DefaultKeyword
            or K.ExportKeyword or K.InKeyword or K.OutKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.PublicKeyword
            or K.ReadonlyKeyword or K.OverrideKeyword or K.StaticKeyword;

    private IdentifierNode Identifier(
        bool allowKeywords = false,
        bool binding = false,
        DiagnosticMessage? privateIdentifierDiagnostic = null)
    {
        int start = Pos;
        if (IsIdentifier || binding && IsBindingIdentifier || allowKeywords && Token is >= K.FirstKeyword and <= K.LastKeyword)
        {
            var node = factory.NewIdentifier(scanner.Value);
            if (!binding && Token == K.AwaitKeyword && statementDepth == 0 && (context & NodeFlags.AwaitContext) == 0)
                possibleTopLevelAwait = true;
            Next(false);
            return Finish(node, start);
        }

        if (Token == K.PrivateIdentifier)
        {
            Error(privateIdentifierDiagnostic ?? Messages.Private_identifiers_are_not_allowed_outside_class_bodies);
            var node = factory.NewIdentifier(scanner.Value);
            Next(false);
            return Finish(node, start);
        }
        if (binding && Token is >= K.FirstReservedWord and <= K.LastReservedWord)
            Error(Messages.Identifier_expected_0_is_a_reserved_word_that_cannot_be_used_here, TokenFacts.Text(Token));
        else
            Error(Messages.Identifier_expected);
        return Finish(factory.NewIdentifier(""), start, start);
    }

    private bool Peek(Func<bool> action) => Peek(action, static callback => callback());

    private bool Peek(Func<Parser, bool> action) => Peek(this, action);

    private bool Peek<TState>(TState state, Func<TState, bool> action)
    {
        var marker = scanner.Mark();
        int count = diagnostics.Count, scanned = scannedDiagnostics;
        bool error = hasError;
        NodeFlags flags = context, fileFlags = sourceFlags;
        bool possibleAwait = possibleTopLevelAwait;
        speculationDepth++;
        try
        {
            return action(state);
        }
        finally
        {
            scanner.Rewind(marker);
            diagnostics.RemoveRange(count, diagnostics.Count - count);
            scannedDiagnostics = scanned;
            hasError = error;
            context = flags;
            sourceFlags = fileFlags;
            possibleTopLevelAwait = possibleAwait;
            speculationDepth--;
        }
    }

    private bool NextIs(K kind) => Peek((Parser: this, Kind: kind), static state => state.Parser.Next() == state.Kind);

    private void Semicolon()
    {
        if (!Take(K.SemicolonToken) && Token is not (K.EndOfFile or K.CloseBraceToken) && !LineBreak)
            Error(Messages.X_0_expected, ";");
    }

    private SyntaxNode Literal()
    {
        int start = Pos;
        TextSlice value = scanner.Value;
        TokenFlags flags = scanner.Flags;
        SyntaxNode node = Token switch
        {
            K.StringLiteral => factory.NewStringLiteral(value, flags & TokenFlags.StringLiteralFlags),
            K.NumericLiteral => factory.NewNumericLiteral(value, flags & TokenFlags.NumericLiteralFlags),
            K.BigIntLiteral => factory.NewBigIntLiteral(value, flags & TokenFlags.NumericLiteralFlags),
            K.RegularExpressionLiteral => factory.NewRegularExpressionLiteral(value, flags & TokenFlags.RegularExpressionLiteralFlags),
            K.NoSubstitutionTemplateLiteral => factory.NewNoSubstitutionTemplateLiteral(value, flags & TokenFlags.TemplateLiteralLikeFlags),
            _ => throw new InvalidOperationException($"Expected literal, got {Token}"),
        };
        Next();
        return Finish(node, start);
    }

    private async ValueTask<SyntaxNode> NameCore()
    {
        await ParseStack;
        if (Token is K.StringLiteral or K.NumericLiteral or K.BigIntLiteral)
            return Literal();
        int start = Pos;
        if (Token == K.PrivateIdentifier)
        {
            var name = factory.NewPrivateIdentifier(scanner.Value);
            Next();
            return Finish(name, start);
        }

        if (Take(K.OpenBracketToken))
        {
            var expression = (await ExpressionCore().ConfigureAwait(false));
            Expected(K.CloseBracketToken);
            return Finish(factory.NewComputedPropertyName(expression), start);
        }

        return Identifier(true);
    }

    private SyntaxNode EntityName(bool allowPrivate = false, bool allowReserved = true)
    {
        int start = Pos;
        SyntaxNode name = Identifier(allowReserved);
        while (Take(K.DotToken))
        {
            if (Token == K.LessThanToken)
                break;
            SyntaxNode right = RightOfDot(allowPrivate);
            name = Finish(factory.NewQualifiedName(name, right), start);
        }

        return name;
    }

    private SyntaxNode RightOfDot(bool allowPrivate, bool allowUnicodeEscape = true)
    {
        if (LineBreak && Token >= K.Identifier && Peek(() =>
        {
            Next();
            return !LineBreak && Token >= K.Identifier;
        }))
        {
            ErrorAt(Messages.Identifier_expected, Pos, 0);
            return Finish(factory.NewIdentifier(""), Pos, Pos);
        }
        if (Token == K.PrivateIdentifier)
        {
            int at = Pos;
            var name = factory.NewPrivateIdentifier(scanner.Value);
            Next();
            if (allowPrivate)
                return Finish(name, at);
            ErrorAt(Messages.Identifier_expected, Pos, 0);
            return Finish(factory.NewIdentifier(""), Pos, Pos);
        }
        if (!allowUnicodeEscape && (scanner.Flags & (TokenFlags.UnicodeEscape | TokenFlags.ExtendedUnicodeEscape)) != 0)
            Error(Messages.Unicode_escape_sequence_cannot_appear_here);
        return Identifier(true);
    }

    private async ValueTask<SyntaxNode?> AnnotationCore()
    {
        await ParseStack;
        return Take(K.ColonToken) ? (await TypeCore().ConfigureAwait(false)) : null;
    }

    private async ValueTask<SyntaxNode?> InitializerCore()
    {
        await ParseStack;
        return Take(K.EqualsToken) ? (await ExpressionCore(2).ConfigureAwait(false)) : null;
    }

    private async ValueTask<NodeList?> ModifiersCore(bool allowConst = false, bool stopOnStaticBlock = false)
    {
        await ParseStack;
        int start = Pos;
        List<SyntaxNode>? nodes = null;
        while (true)
        {
            if (Token == K.AtToken)
            {
                if (nodes?.Any(n => n.Kind is not (K.Decorator or K.ExportKeyword or K.DefaultKeyword)) == true)
                    Error(Messages.Decorators_must_precede_the_name_and_all_keywords_of_property_declarations);
                int at = Pos;
                Next();
                NodeFlags saved = context;
                context |= NodeFlags.DecoratorContext;
                SyntaxNode expression;
                if ((context & NodeFlags.AwaitContext) != 0 && Token == K.AwaitKeyword)
                {
                    Error(Messages.Expression_expected);
                    var missing = Finish(factory.NewIdentifier(""), Pos, Pos);
                    Next();
                    expression = await MemberExpressionCore(missing, true).ConfigureAwait(false);
                }
                else
                    expression = await MemberExpressionCore(
                        await PrimaryExpressionCore().ConfigureAwait(false),
                        true).ConfigureAwait(false);
                context = saved;
                (nodes ??= []).Add(Finish(factory.NewDecorator(expression), at));
                continue;
            }

            bool modifier = Token is K.ExportKeyword or K.DefaultKeyword or K.DeclareKeyword or K.AbstractKeyword or K.AsyncKeyword
                or K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.ReadonlyKeyword or K.OverrideKeyword or K.StaticKeyword
                or K.AccessorKeyword
                || allowConst && Token is K.ConstKeyword or K.InKeyword or K.OutKeyword
                || Token == K.ConstKeyword && NextIs(K.EnumKeyword);
            if (Token == K.ExportKeyword && Peek(static parser =>
            {
                parser.Next();
                if (parser.Token is K.OpenBraceToken or K.AsteriskToken or K.EqualsToken or K.AsKeyword)
                    return true;
                if (parser.Token == K.TypeKeyword)
                    return parser.Next() is K.OpenBraceToken or K.AsteriskToken;
                if (parser.Token == K.DefaultKeyword)
                    return parser.Next() is not (K.ClassKeyword or K.FunctionKeyword or K.InterfaceKeyword or K.AbstractKeyword
                        or K.AsyncKeyword or K.AtToken);
                return false;
            }))
                break;
            if (stopOnStaticBlock && Token == K.StaticKeyword && NextIs(K.OpenBraceToken))
                break;
            if (Token == K.StaticKeyword && nodes?.Any(n => n.Kind == K.StaticKeyword) == true)
                break;
            K modifierKind = Token;
            if (!modifier || !Peek((Parser: this, Kind: modifierKind), static state =>
            {
                var parser = state.Parser;
                parser.Next();
                return (!parser.LineBreak || state.Kind is K.ExportKeyword or K.DefaultKeyword or K.StaticKeyword)
                    && (parser.Token >= K.Identifier
                        || parser.Token is K.PrivateIdentifier or K.StringLiteral or K.NumericLiteral or K.BigIntLiteral or K.OpenBracketToken
                            or K.OpenBraceToken or K.DotDotDotToken or K.AsteriskToken or K.AtToken);
            }))
                break;
            (nodes ??= []).Add(ParseToken());
        }

        return nodes is null ? null : new(nodes.ToArray(), start, Pos);
    }
}

using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public enum ScriptKind { Unknown, JS, JSX, TS, TSX, External, JSON, Deferred }
public sealed record ParseOptions(string FileName, ScriptKind ScriptKind = ScriptKind.Unknown, int TargetYear = int.MaxValue);

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
    private K Token => scanner.Kind;
    private int Pos => scanner.FullStart;
    private bool LineBreak => scanner.HasPrecedingLineBreak;
    private Parser(ParseOptions options, SourceText source, CancellationToken cancellation)
    {
        ScriptKind kind = options.ScriptKind;
        if (kind == ScriptKind.Unknown) kind = CompilerPath.Extension(options.FileName).ToLowerInvariant() switch
        { ".js" or ".mjs" or ".cjs" => ScriptKind.JS, ".jsx" => ScriptKind.JSX, ".tsx" => ScriptKind.TSX, ".json" => ScriptKind.JSON, _ => ScriptKind.TS };
        this.options = options with { ScriptKind = kind }; this.source = source; this.cancellation = cancellation;
        scanner = new(source, true, kind is ScriptKind.JSX or ScriptKind.TSX) { TargetYear = options.TargetYear };
        if (kind is ScriptKind.JS or ScriptKind.JSX or ScriptKind.JSON) context = NodeFlags.JavaScriptFile;
        if (kind == ScriptKind.JSON) context |= NodeFlags.JsonFile;
        if (CompilerPath.IsDeclarationFile(options.FileName)) context |= NodeFlags.Ambient;
        Next();
    }
    public static SourceFileNode ParseSourceFile(ParseOptions options, SourceText source, CancellationToken cancellation = default) => new Parser(options, source, cancellation).ParseFile();
    private SourceFileNode ParseFile()
    {
        int start = Pos;
        var statements = new List<SyntaxNode>();
        if (options.ScriptKind == ScriptKind.JSON)
        {
            var expressions = new List<SyntaxNode>();
            while (Token != K.EndOfFile)
            {
                int before = scanner.Position;
                expressions.Add(PrimaryExpression());
                if (Token != K.EndOfFile) Error(Messages.Unexpected_token);
                if (scanner.Position == before) Next();
            }
            if (expressions.Count != 0)
            {
                SyntaxNode expression = expressions.Count == 1 ? expressions[0] : Finish(factory.NewArrayLiteralExpression(new(expressions.ToArray(), start, Pos), false), start);
                statements.Add(Finish(factory.NewExpressionStatement(expression), expression.Pos, expression.End));
                bool saved = hasError; ValidateJson(expression); hasError = saved;
            }
        }
        else
            while (Token != K.EndOfFile)
            {
                int before = scanner.Position;
                statements.Add(ParseStatement());
                if (scanner.Position == before) { Error(Messages.Declaration_or_statement_expected); Next(); }
            }
        int end = Pos;
        var eof = ParseToken();
        SourceFileNode file = Finish(factory.NewSourceFile(new(statements.ToArray(), start, end), eof), start);
        file.Flags |= sourceFlags;
        file.FileName = options.FileName; file.Source = source; file.ScriptKind = options.ScriptKind;
        file.IsDeclarationFile = CompilerPath.IsDeclarationFile(options.FileName);
        // Source positions remain bytes at the public AST boundary; scanning uses UTF-16.
        foreach (SyntaxNode node in file.DescendantsAndSelf())
            node.ConvertPositions(source.ToBytePosition);
        file.ParseDiagnostics = diagnostics.Select(d => d with { Start = source.ToBytePosition(d.Start), Length = source.ToBytePosition(d.Start + d.Length) - source.ToBytePosition(d.Start), FileName = options.FileName }).ToArray();
        file.CommentDirectives = scanner.CommentDirectives.Select(d => d with { Start = source.ToBytePosition(d.Start), End = source.ToBytePosition(d.End) }).ToArray();
        return file;
    }
    private K Next(bool checkEscapes = true)
    {
        cancellation.ThrowIfCancellationRequested();
        if (checkEscapes && Token is >= K.FirstKeyword and <= K.LastKeyword && (scanner.Flags & (TokenFlags.UnicodeEscape | TokenFlags.ExtendedUnicodeEscape)) != 0)
            Error(Messages.Keywords_cannot_contain_escape_characters);
        scanner.Scan();
        for (; scannedDiagnostics < scanner.Diagnostics.Count; scannedDiagnostics++)
        {
            Diagnostic d = scanner.Diagnostics[scannedDiagnostics];
            ErrorAt(d.Message, d.Start, d.Length, d.Arguments);
        }
        return Token;
    }
    private void Error(DiagnosticMessage message, params string[] args) => ErrorAt(message, scanner.TokenStart, scanner.Position - scanner.TokenStart, args);
    private void ErrorAt(DiagnosticMessage message, int start, int length, params string[] args)
    {
        if (diagnostics.Count == 0 || diagnostics[^1].Start != start) diagnostics.Add(new(message, start, length, args));
        hasError = true;
    }
    private T Finish<T>(T node, int start, int? end = null) where T : SyntaxNode
    {
        node.Pos = start; node.End = end ?? Pos; node.Flags |= context;
        if (hasError) { node.Flags |= NodeFlags.ThisNodeHasError; hasError = false; }
        for (int i = 0; i < node.ChildCount; i++) node.GetChild(i).Parent = node;
        return node;
    }
    private bool Take(K kind) { if (Token != kind) return false; Next(); return true; }
    private bool Expected(K kind)
    { if (Take(kind)) return true; Error(Messages.X_0_expected, TokenFacts.Text(kind)); return false; }
    private TokenNode ParseToken()
    { int start = Pos; var node = factory.NewToken(Token); Next(); return Finish(node, start); }
    private TokenNode? OptionalToken(K kind) => Token == kind ? ParseToken() : null;
    private TokenNode ExpectedToken(K kind)
    { if (Token == kind) return ParseToken(); Error(Messages.X_0_expected, TokenFacts.Text(kind)); return Finish(factory.NewToken(kind), Pos, Pos); }
    private bool IsIdentifier => Token == K.Identifier || Token > K.LastReservedWord;
    private IdentifierNode Identifier(bool allowKeywords = false)
    {
        int start = Pos;
        if (IsIdentifier || allowKeywords && Token is >= K.FirstKeyword and <= K.LastKeyword)
        { var node = factory.NewIdentifier(scanner.Value); Next(false); return Finish(node, start); }
        Error(Messages.Identifier_expected);
        return Finish(factory.NewIdentifier(""), start, start);
    }
    private bool Peek(Func<bool> action)
    {
        var state = scanner.Mark(); int count = diagnostics.Count, scanned = scannedDiagnostics; bool error = hasError; NodeFlags flags = context, fileFlags = sourceFlags;
        try { return action(); }
        finally { scanner.Rewind(state); diagnostics.RemoveRange(count, diagnostics.Count - count); scannedDiagnostics = scanned; hasError = error; context = flags; sourceFlags = fileFlags; }
    }
    private bool NextIs(K kind) => Peek(() => Next() == kind);
    private void Semicolon()
    { if (!Take(K.SemicolonToken) && Token is not (K.EndOfFile or K.CloseBraceToken) && !LineBreak) Error(Messages.X_0_expected, ";"); }
    private NodeList Delimited(K end, Func<SyntaxNode> element, bool semicolons = false)
    {
        int start = Pos;
        var nodes = new List<SyntaxNode>();
        while (Token != end && Token != K.EndOfFile)
        {
            int before = scanner.Position;
            nodes.Add(element());
            if (Token == end) break;
            if (!Take(K.CommaToken) && !(semicolons && (Take(K.SemicolonToken) || LineBreak)))
            {
                Error(Messages.X_0_expected, semicolons ? ";" : ",");
                if (Token is K.CloseBraceToken or K.CloseParenToken or K.CloseBracketToken) break;
            }
            if (scanner.Position == before) Next();
        }
        return new(nodes.ToArray(), start, Pos);
    }
    private NodeList List(K end, Func<SyntaxNode> element)
    {
        int start = Pos;
        var nodes = new List<SyntaxNode>();
        while (Token != end && Token != K.EndOfFile)
        {
            int before = scanner.Position;
            nodes.Add(element());
            if (scanner.Position == before) { Error(Messages.Declaration_or_statement_expected); Next(); }
        }
        return new(nodes.ToArray(), start, Pos);
    }
    private SyntaxNode Literal()
    {
        int start = Pos; string value = scanner.Value; TokenFlags flags = scanner.Flags;
        SyntaxNode node = Token switch
        {
            K.StringLiteral => factory.NewStringLiteral(value, flags & TokenFlags.StringLiteralFlags),
            K.NumericLiteral => factory.NewNumericLiteral(value, flags & TokenFlags.NumericLiteralFlags),
            K.BigIntLiteral => factory.NewBigIntLiteral(value, flags & TokenFlags.NumericLiteralFlags),
            K.RegularExpressionLiteral => factory.NewRegularExpressionLiteral(value, flags & TokenFlags.RegularExpressionLiteralFlags),
            K.NoSubstitutionTemplateLiteral => factory.NewNoSubstitutionTemplateLiteral(value, flags & TokenFlags.TemplateLiteralLikeFlags),
            _ => throw new InvalidOperationException($"Expected literal, got {Token}"),
        };
        Next(); return Finish(node, start);
    }
    private SyntaxNode Name()
    {
        if (Token is K.StringLiteral or K.NumericLiteral or K.BigIntLiteral) return Literal();
        int start = Pos;
        if (Token == K.PrivateIdentifier) { var name = factory.NewPrivateIdentifier(scanner.Value); Next(); return Finish(name, start); }
        if (Take(K.OpenBracketToken)) { var expression = Expression(); Expected(K.CloseBracketToken); return Finish(factory.NewComputedPropertyName(expression), start); }
        return Identifier(true);
    }
    private SyntaxNode EntityName()
    {
        int start = Pos;
        SyntaxNode name = Identifier(true);
        while (Take(K.DotToken)) name = Finish(factory.NewQualifiedName(name, Identifier(true)), start);
        return name;
    }
    private SyntaxNode? Annotation() => Take(K.ColonToken) ? Type() : null;
    private SyntaxNode? Initializer() => Take(K.EqualsToken) ? Expression(2) : null;
    private NodeList? Modifiers(bool allowConst = false)
    {
        int start = Pos; List<SyntaxNode>? nodes = null;
        while (true)
        {
            if (Token == K.AtToken)
            {
                int at = Pos; Next(); NodeFlags saved = context; context |= NodeFlags.DecoratorContext;
                var expression = Expression(17); context = saved;
                (nodes ??= []).Add(Finish(factory.NewDecorator(expression), at)); continue;
            }
            bool modifier = Token is K.ExportKeyword or K.DefaultKeyword or K.DeclareKeyword or K.AbstractKeyword or K.AsyncKeyword or K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.ReadonlyKeyword or K.OverrideKeyword or K.StaticKeyword or K.AccessorKeyword || allowConst && Token is K.ConstKeyword or K.InKeyword or K.OutKeyword || Token == K.ConstKeyword && NextIs(K.EnumKeyword);
            if (!modifier || !Peek(() => { Next(); return !LineBreak && (Token >= K.Identifier || Token is K.StringLiteral or K.NumericLiteral or K.OpenBracketToken or K.AsteriskToken or K.AtToken); })) break;
            (nodes ??= []).Add(ParseToken());
        }
        return nodes is null ? null : new(nodes.ToArray(), start, Pos);
    }
}

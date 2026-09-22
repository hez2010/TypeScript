using TypeScript.Compiler.Storage;

namespace TypeScript.Compiler.Syntax;

public sealed class SliceFile<TStore>(string name, byte[] text, TStore store) where TStore : INodeStore
{
    public string Name { get; } = name;
    public byte[] Text { get; } = text;
    public TStore Store { get; } = store;
    public NodeId Root { get; internal set; }
    public List<SliceDiagnostic> Diagnostics { get; } = [];
    public List<NodeId> Imports { get; } = [];
    public NodeId ExternalModuleIndicator { get; internal set; }
    public NodeId[] Statements => Store.Get<NodeListData>(Store.Get<SourceFileData>(Root).Statements).Nodes;
}

// Faithful bounded productions from parser.go: type aliases, named imports,
// variable declarations, union/parenthesized/reference/literal types. Input-
// shaped type nesting uses continuation frames rather than native recursion.
public sealed class SliceParser<TStore, TSource> where TStore : INodeStore where TSource : struct, ISourceView
{
    private readonly SliceFile<TStore> file;
    private readonly SliceLexer<TSource> lexer;
    private readonly CancellationToken cancellation;
    private SliceToken token;
    private TStore Store => file.Store;

    public SliceParser(SliceFile<TStore> file, TSource source, CancellationToken cancellation = default)
    {
        this.file = file;
        this.cancellation = cancellation;
        lexer = new(source, file.Diagnostics);
        token = lexer.Scan();
    }

    private void Next()
    {
        cancellation.ThrowIfCancellationRequested();
        token = lexer.Scan();
    }

    private bool Eat(SyntaxKind kind)
    {
        if (token.Kind != kind)
            return false;
        Next();
        return true;
    }

    private void Error(int code, string message) => file.Diagnostics.Add(new(code, token.Start, token.End - token.Start, message));

    private void Expect(SyntaxKind kind)
    {
        if (!Eat(kind))
            Error(1005, $"'{kind}' expected.");
    }

    private NodeId Add<T>(SyntaxKind kind, int pos, int end, T payload, uint flags = 0) where T : struct, INodePayload<T> =>
        Store.Add(new(kind, pos, end, flags), payload);

    private NodeId List(int pos, int end, List<NodeId> nodes, bool trailing = false) =>
        Add(SyntaxKind.NodeList, pos, end, new NodeListData([.. nodes], trailing));

    private NodeId TokenNode()
    {
        var current = token;
        Next();
        return Add(current.Kind, current.Pos, current.End, new TokenData());
    }

    private void Semicolon()
    {
        if (!Eat(SyntaxKind.SemicolonToken)
            && token.Kind is not SyntaxKind.EndOfFile and not SyntaxKind.CloseBraceToken
            && !token.LineBreak)
            Error(1005, "';' expected.");
    }

    public SliceFile<TStore> Parse()
    {
        using var profile = Diagnostics.NativeProfile.Enter("TypeScript.Parse");
        List<NodeId> statements = [];
        while (token.Kind != SyntaxKind.EndOfFile)
        {
            int before = token.Start, pos = token.Pos;
            NodeId modifiers = default;
            if (token.Kind == SyntaxKind.ExportKeyword)
            {
                NodeId export = TokenNode();
                modifiers = List(pos, token.Pos, [export]);
            }
            NodeId statement = default;
            if (Eat(SyntaxKind.TypeKeyword))
            {
                NodeId name = Identifier();
                Expect(SyntaxKind.EqualsToken);
                NodeId type = Type();
                Semicolon();
                statement = Add(
                    SyntaxKind.TypeAliasDeclaration,
                    pos,
                    token.Pos,
                    new TypeAliasDeclarationData(modifiers, name, default, type));
            }
            else if (Eat(SyntaxKind.ImportKeyword))
                statement = Import(pos, modifiers);
            else if (token.Kind is SyntaxKind.ConstKeyword or SyntaxKind.LetKeyword or SyntaxKind.VarKeyword)
                statement = Variables(pos, modifiers);
            else
            {
                Error(1003, "Unsupported declaration in phase-1 syntax slice.");
                while (token.Kind is not SyntaxKind.SemicolonToken and not SyntaxKind.EndOfFile)
                    Next();
                Eat(SyntaxKind.SemicolonToken);
            }
            if (!statement.IsNull)
            {
                statements.Add(statement);
                if (file.ExternalModuleIndicator.IsNull
                    && (!modifiers.IsNull || Store.Header(statement).Kind == SyntaxKind.ImportDeclaration))
                    file.ExternalModuleIndicator = statement;
            }
            if (token.Start == before && token.Kind != SyntaxKind.EndOfFile)
                Next();
        }
        NodeId statementList = List(0, token.Pos, statements);
        NodeId eof = TokenNode();
        file.Root = Add(SyntaxKind.SourceFile, 0, file.Text.Length, new SourceFileData(statementList, eof));
        Diagnostics.NativeProfile.TrackOwner(file, file.Name, file.Text.Length, Store.Count);
        return file;
    }

    private NodeId Identifier()
    {
        if (token.Kind == SyntaxKind.Identifier || token.Kind >= SyntaxKind.AsKeyword && token.Kind <= SyntaxKind.LastKeyword)
        {
            var current = token;
            Next();
            return Add(SyntaxKind.Identifier, current.Pos, current.End, new IdentifierData(current.Text));
        }
        Error(1003, "Identifier expected.");
        return Add(SyntaxKind.Identifier, token.Start, token.Start, new IdentifierData(""));
    }

    private NodeId Import(int pos, NodeId modifiers)
    {
        int clausePos = token.Pos;
        SyntaxKind phase = Eat(SyntaxKind.TypeKeyword) ? SyntaxKind.TypeKeyword : SyntaxKind.Unknown;
        int namedPos = token.Pos;
        Expect(SyntaxKind.OpenBraceToken);
        int listPos = token.Pos;
        List<NodeId> names = [];
        bool trailing = false;
        while (token.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFile)
        {
            int start = token.Pos, before = token.Start;
            bool typeOnly = Eat(SyntaxKind.TypeKeyword);
            NodeId first = Identifier(), original = default, name = first;
            if (Eat(SyntaxKind.AsKeyword))
            {
                original = first;
                name = Identifier();
            }
            names.Add(Add(SyntaxKind.ImportSpecifier, start, token.Pos, new ImportSpecifierData(typeOnly, original, name)));
            trailing = Eat(SyntaxKind.CommaToken);
            if (!trailing || before == token.Start)
                break;
        }
        NodeId elements = List(listPos, token.Pos, names, trailing);
        Expect(SyntaxKind.CloseBraceToken);
        NodeId named = Add(SyntaxKind.NamedImports, namedPos, token.Pos, new NamedImportsData(elements));
        NodeId clause = Add(SyntaxKind.ImportClause, clausePos, token.Pos, new ImportClauseData(phase, default, named));
        Expect(SyntaxKind.FromKeyword);
        NodeId module;
        if (token.Kind == SyntaxKind.StringLiteral)
            module = Literal();
        else
        {
            Error(1141, "String literal expected.");
            module = Add(SyntaxKind.StringLiteral, token.Start, token.Start, new StringLiteralData("", 0));
        }
        file.Imports.Add(module);
        Semicolon();
        return Add(SyntaxKind.ImportDeclaration, pos, token.Pos, new ImportDeclarationData(modifiers, clause, module, default));
    }

    private NodeId Variables(int pos, NodeId modifiers)
    {
        int declarationPos = token.Pos;
        uint flags = token.Kind == SyntaxKind.ConstKeyword ? 2u : token.Kind == SyntaxKind.LetKeyword ? 1u : 0;
        Next();
        int listPos = token.Pos;
        List<NodeId> declarations = [];
        do
        {
            int start = token.Pos;
            NodeId name = Identifier();
            NodeId type = Eat(SyntaxKind.ColonToken) ? Type() : default;
            NodeId initializer = Eat(SyntaxKind.EqualsToken) ? Expression() : default;
            declarations.Add(
                Add(SyntaxKind.VariableDeclaration, start, token.Pos, new VariableDeclarationData(name, default, type, initializer)));
        } while (Eat(SyntaxKind.CommaToken));
        NodeId list = List(listPos, token.Pos, declarations);
        NodeId declarationList = Add(
            SyntaxKind.VariableDeclarationList,
            declarationPos,
            token.Pos,
            new VariableDeclarationListData(list),
            flags);
        Semicolon();
        return Add(SyntaxKind.VariableStatement, pos, token.Pos, new VariableStatementData(modifiers, declarationList));
    }

    private sealed class TypeFrame(int pos)
    {
        public readonly int Pos = pos;
        public readonly List<NodeId> Types = [];
        public bool Leading;
    }

    private NodeId Type()
    {
        var parents = new Stack<(TypeFrame Frame, int Pos)>();
        var frame = new TypeFrame(token.Pos);
        bool expectType = true;
        while (true)
        {
            if (expectType)
            {
                if (Eat(SyntaxKind.BarToken))
                    frame.Leading = true;
                if (token.Kind == SyntaxKind.OpenParenToken)
                {
                    int pos = token.Pos;
                    Next();
                    parents.Push((frame, pos));
                    frame = new(token.Pos);
                    continue;
                }
                NodeId node;
                if (token.Kind is SyntaxKind.AnyKeyword or SyntaxKind.UnknownKeyword or SyntaxKind.NeverKeyword
                    or SyntaxKind.StringKeyword or SyntaxKind.NumberKeyword or SyntaxKind.BigIntKeyword or SyntaxKind.BooleanKeyword
                    or SyntaxKind.SymbolKeyword or SyntaxKind.ObjectKeyword or SyntaxKind.VoidKeyword or SyntaxKind.UndefinedKeyword)
                {
                    var current = token;
                    Next();
                    node = Add(current.Kind, current.Pos, current.End, new KeywordTypeNodeData());
                }
                else if (token.Kind is SyntaxKind.StringLiteral or SyntaxKind.NumericLiteral or SyntaxKind.TrueKeyword
                    or SyntaxKind.FalseKeyword or SyntaxKind.NullKeyword or SyntaxKind.MinusToken)
                {
                    int start = token.Pos;
                    NodeId value = Expression();
                    node = Add(SyntaxKind.LiteralType, start, token.Pos, new LiteralTypeNodeData(value));
                }
                else if (token.Kind == SyntaxKind.Identifier)
                {
                    int start = token.Pos;
                    NodeId name = Identifier();
                    node = Add(SyntaxKind.TypeReference, start, token.Pos, new TypeReferenceNodeData(name, default));
                }
                else
                {
                    Error(1110, "Type expected.");
                    NodeId missing = Add(SyntaxKind.Identifier, token.Start, token.Start, new IdentifierData(""));
                    node = Add(SyntaxKind.TypeReference, token.Start, token.Start, new TypeReferenceNodeData(missing, default));
                }
                frame.Types.Add(node);
                expectType = false;
            }
            if (Eat(SyntaxKind.BarToken))
            {
                expectType = true;
                continue;
            }
            NodeId result = Finish(frame);
            if (parents.Count == 0)
                return result;
            (TypeFrame parent, int parenPos) = parents.Pop();
            Expect(SyntaxKind.CloseParenToken);
            parent.Types.Add(Add(SyntaxKind.ParenthesizedType, parenPos, token.Pos, new ParenthesizedTypeNodeData(result)));
            frame = parent;
        }

        NodeId Finish(TypeFrame current) => current.Types.Count == 1 && !current.Leading ? current.Types[0]
            : Add(SyntaxKind.UnionType, current.Pos, token.Pos, new UnionTypeNodeData(List(current.Pos, token.Pos, current.Types)));
    }

    private NodeId Expression()
    {
        if (token.Kind is SyntaxKind.MinusToken or SyntaxKind.PlusToken)
        {
            var current = token;
            Next();
            if (token.Kind != SyntaxKind.NumericLiteral)
                Error(1109, "Numeric literal expected in phase-1 unary expression.");
            NodeId literal = Literal();
            return Add(SyntaxKind.PrefixUnaryExpression, current.Pos, token.Pos, new PrefixUnaryExpressionData(current.Kind, literal));
        }
        if (token.Kind is SyntaxKind.StringLiteral or SyntaxKind.NumericLiteral or SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword
            or SyntaxKind.NullKeyword)
            return Literal();
        return Identifier();
    }

    private NodeId Literal()
    {
        var current = token;
        Next();
        return current.Kind switch
        {
            SyntaxKind.StringLiteral => Add(current.Kind, current.Pos, current.End, new StringLiteralData(current.Text, current.Flags)),
            SyntaxKind.NumericLiteral => Add(current.Kind, current.Pos, current.End, new NumericLiteralData(current.Text, current.Flags)),
            _ => Add(current.Kind, current.Pos, current.End, new TokenData()),
        };
    }
}

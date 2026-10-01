using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

public sealed partial class EmitContext
{
    private sealed class EnvironmentScope
    {
        internal readonly List<SyntaxNode> Variables = [], Functions = [], Initializers = [];
        internal bool InParameters, HoistedInParameters;
    }

    private readonly Stack<EnvironmentScope> variableScopes = [], lexicalScopes = [];
    internal (int Variables, int Lexical) EnvironmentDepth => (variableScopes.Count, lexicalScopes.Count);
    internal void RestoreEnvironmentDepth((int Variables, int Lexical) depth)
    {
        while (variableScopes.Count > depth.Variables)
            variableScopes.Pop();
        while (lexicalScopes.Count > depth.Lexical)
            lexicalScopes.Pop();
    }

    public void StartVariableEnvironment()
    {
        variableScopes.Push(new());
        StartLexicalEnvironment();
    }

    public IReadOnlyList<SyntaxNode> EndVariableEnvironment()
    {
        var scope = variableScopes.Pop();
        var statements = new List<SyntaxNode>(scope.Functions);
        if (scope.Variables.Count != 0)
            statements.Add(HoistedVariables(scope.Variables, NodeFlags.None));
        statements.AddRange(scope.Initializers);
        statements.AddRange(EndLexicalEnvironment());
        return statements;
    }

    public void StartLexicalEnvironment() => lexicalScopes.Push(new());
    public IReadOnlyList<SyntaxNode> EndLexicalEnvironment()
    {
        var scope = lexicalScopes.Pop();
        return scope.Variables.Count == 0 ? [] : [HoistedVariables(scope.Variables, NodeFlags.Let)];
    }

    public void AddVariableDeclaration(IdentifierNode name)
    {
        var scope = variableScopes.Peek();
        scope.Variables.Add(HoistedVariable(name));
        scope.HoistedInParameters |= scope.InParameters;
    }

    public void AddLexicalDeclaration(IdentifierNode name) => lexicalScopes.Peek().Variables.Add(HoistedVariable(name));
    public void AddHoistedFunctionDeclaration(FunctionDeclarationNode node)
    {
        AddFlags(node, EmitFlags.CustomPrologue);
        variableScopes.Peek().Functions.Add(node);
    }

    public void AddInitializationStatement(SyntaxNode node)
    {
        AddFlags(node, EmitFlags.CustomPrologue);
        variableScopes.Peek().Initializers.Add(node);
    }

    private VariableDeclarationNode HoistedVariable(IdentifierNode name)
    {
        var declaration = Factory.NewVariableDeclaration(name, null, null, null);
        SetFlags(declaration, EmitFlags.NoNestedSourceMaps);
        return declaration;
    }

    private VariableStatementNode HoistedVariables(List<SyntaxNode> declarations, NodeFlags flags)
    {
        var statement = Factory.NewVariableStatement(null, Factory.NewVariableDeclarationList(new(declarations.ToArray()), flags));
        SetFlags(statement, EmitFlags.CustomPrologue);
        return statement;
    }

    public NodeList? MergeEnvironment(NodeList? statements, IReadOnlyList<SyntaxNode> declarations)
    {
        if (declarations.Count == 0)
            return statements;
        var left = statements?.ToArray() ?? [];
        var leftSpans = PrologueSpans(left);
        var rightSpans = PrologueSpans(declarations);
        if (rightSpans.Custom != declarations.Count)
            throw new ArgumentException("Environment declarations must be directive or custom prologues", nameof(declarations));
        var result = new List<SyntaxNode>(left.Length + declarations.Count);
        var existingDirectives = left.Take(leftSpans.Standard).Cast<ExpressionStatementNode>().Select(n => ((StringLiteralNode)n.Expression!).Text).ToHashSet();
        for (int i = 0; i < rightSpans.Standard; i++)
            if (!existingDirectives.Contains(((StringLiteralNode)((ExpressionStatementNode)declarations[i]).Expression!).Text))
                result.Add(declarations[i]);
        result.AddRange(left.Take(leftSpans.Standard));
        Append(declarations, rightSpans.Standard, rightSpans.Functions);
        Append(left, leftSpans.Standard, leftSpans.Functions);
        Append(declarations, rightSpans.Functions, rightSpans.Variables);
        Append(left, leftSpans.Functions, leftSpans.Variables);
        Append(declarations, rightSpans.Variables, rightSpans.Custom);
        Append(left, leftSpans.Variables, left.Length);
        if (result.Count == left.Length && result.SequenceEqual(left))
            return statements;
        return new(result.ToArray(), statements?.Pos ?? -1, statements?.End ?? -1, trailingComma: statements?.HasTrailingComma);

        void Append(IReadOnlyList<SyntaxNode> items, int start, int end)
        {
            for (int i = start; i < end; i++)
                result.Add(items[i]);
        }
    }

    private (int Standard, int Functions, int Variables, int Custom) PrologueSpans(IReadOnlyList<SyntaxNode> nodes)
    {
        int i = 0;
        while (i < nodes.Count && nodes[i] is ExpressionStatementNode { Expression: StringLiteralNode })
            i++;
        int standard = i;
        while (i < nodes.Count && (GetFlags(nodes[i]) & EmitFlags.CustomPrologue) != 0 && nodes[i] is FunctionDeclarationNode)
            i++;
        int functions = i;
        while (i < nodes.Count && (GetFlags(nodes[i]) & EmitFlags.CustomPrologue) != 0
            && nodes[i] is VariableStatementNode { DeclarationList.Declarations: { } declarations }
            && declarations.All(n => n is VariableDeclarationNode { Name: IdentifierNode, Initializer: null }))
            i++;
        int variables = i;
        while (i < nodes.Count && (GetFlags(nodes[i]) & EmitFlags.CustomPrologue) != 0)
            i++;
        return (standard, functions, variables, i);
    }

    internal void BeginParameters()
    {
        StartVariableEnvironment();
        variableScopes.Peek().InParameters = true;
    }

    internal NodeList? EndParameters(NodeList? parameters)
    {
        var scope = variableScopes.Peek();
        scope.InParameters = false;
        if (!scope.HoistedInParameters || parameters is null)
            return parameters;
        var result = parameters.Select(n => MoveParameterInitializer((ParameterDeclarationNode)n)).ToArray();
        return result.SequenceEqual(parameters) ? parameters : new(result, parameters.Pos, parameters.End, parameters.IsMissing, parameters.HasTrailingComma);
    }

    private SyntaxNode MoveParameterInitializer(ParameterDeclarationNode parameter)
    {
        if (parameter.DotDotDotToken is not null)
            return parameter;
        if (parameter.Name is BindingPatternNode)
        {
            SyntaxNode initializer = NewGeneratedNameForNode(parameter);
            if (parameter.Initializer is { } value)
                initializer = Factory.NewConditionalExpression(Binary(NewGeneratedNameForNode(parameter), SyntaxKind.EqualsEqualsEqualsToken, VoidZero()),
                    Factory.NewToken(SyntaxKind.QuestionToken), value, Factory.NewToken(SyntaxKind.ColonToken), initializer);
            var declaration = Factory.NewVariableDeclaration(parameter.Name, null, parameter.Type, initializer);
            AddInitializationStatement(Factory.NewVariableStatement(null, Factory.NewVariableDeclarationList(new([declaration]), NodeFlags.None)));
            var result = Clone(parameter);
            result.Name = NewGeneratedNameForNode(parameter);
            result.Initializer = null;
            return result;
        }
        if (parameter.Initializer is null)
            return parameter;
        AddFlags(parameter.Initializer, EmitFlags.NoSourceMap | EmitFlags.NoComments);
        var name = Clone(parameter.Name!);
        AddFlags(name, EmitFlags.NoSourceMap);
        var assignment = Binary(name, SyntaxKind.EqualsToken, parameter.Initializer);
        CopyRange(assignment, parameter);
        AddFlags(assignment, EmitFlags.NoComments);
        var block = Factory.NewBlock(new([Factory.NewExpressionStatement(assignment)]), false);
        CopyRange(block, parameter);
        AddFlags(block, EmitFlags.SingleLine | EmitFlags.NoTrailingSourceMap | EmitFlags.NoTokenSourceMaps | EmitFlags.NoComments);
        AddInitializationStatement(Factory.NewIfStatement(Binary(Clone(parameter.Name!), SyntaxKind.EqualsEqualsEqualsToken, VoidZero()), block, null));
        var updated = Clone(parameter);
        updated.Initializer = null;
        return updated;
    }

    public BinaryExpressionNode Binary(SyntaxNode left, SyntaxKind operation, SyntaxNode right) =>
        Factory.NewBinaryExpression(null, left, null, Factory.NewToken(operation), right);
    public VoidExpressionNode VoidZero() => Factory.NewVoidExpression(Factory.NewNumericLiteral("0"u8, TokenFlags.None));
    public static T CopyRange<T>(T target, SyntaxNode source) where T : SyntaxNode
    {
        target.Pos = source.Pos;
        target.End = source.End;
        return target;
    }

    public BlockNode ConvertToFunctionBlock(SyntaxNode node, bool multiLine = false)
    {
        if (node is BlockNode block)
            return block;
        var statement = CopyRange(Factory.NewReturnStatement(node), node);
        return CopyRange(Factory.NewBlock(new([statement], node.Pos, node.End), multiLine), node);
    }
}

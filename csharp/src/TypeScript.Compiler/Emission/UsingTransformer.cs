using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed class UsingTransformer : SyntaxRewriter
{
    private readonly Dictionary<SyntaxNode, bool> facts = [];
    private readonly Dictionary<Utf8String, ExportSpecifierNode> exportBindings = [];
    private readonly List<Utf8String> exportNames = [];
    private readonly List<SyntaxNode> exportVariables = [];
    private readonly NamedEvaluation named;
    private IdentifierNode? defaultBinding, exportEqualsBinding;
    private NodeFactory F => Context.Factory;

    internal UsingTransformer(EmitContext context, CancellationToken cancellation = default) : base(context, cancellation) => named = new(context);

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        try
        {
            if (!ContainsUsing(node)) return node;
            return node switch
            {
                SourceFileNode source => await SourceAsync(source),
                BlockNode block => await BlockAsync(block),
                ForStatementNode loop when UsingKind(loop.Initializer) != 0 => await ForAsync(loop),
                ForInOrOfStatementNode { Kind: K.ForOfStatement } loop when UsingKind(loop.Initializer) != 0 => await ForOfAsync(loop),
                _ => await VisitEachChildAsync(node)
            };
        }
        finally
        {
            if (node is SourceFileNode)
            {
                facts.Clear(); exportBindings.Clear(); exportNames.Clear(); exportVariables.Clear();
                defaultBinding = exportEqualsBinding = null;
            }
        }
    }

    private static int UsingKind(SyntaxNode? node) => node switch
    {
        VariableStatementNode statement => UsingKind(statement.DeclarationList),
        VariableDeclarationListNode list => (list.Flags & NodeFlags.BlockScoped) switch { NodeFlags.AwaitUsing => 2, NodeFlags.Using => 1, _ => 0 },
        _ => 0
    };
    private static int UsingKind(NodeList? statements) => statements?.Select(UsingKind).DefaultIfEmpty().Max() ?? 0;
    private bool ContainsUsing(SyntaxNode root)
    {
        if (facts.TryGetValue(root, out bool contains)) return contains;
        Stack<(SyntaxNode Node, bool Visited)> pending = new([(root, false)]);
        while (pending.TryPop(out var item))
        {
            Cancellation.ThrowIfCancellationRequested();
            if (facts.ContainsKey(item.Node)) continue;
            if (!item.Visited)
            {
                pending.Push((item.Node, true));
                for (int i = item.Node.ChildCount - 1; i >= 0; i--) pending.Push((item.Node.GetChild(i), false));
            }
            else
            {
                contains = UsingKind(item.Node) != 0;
                for (int i = 0; !contains && i < item.Node.ChildCount; i++) contains = facts[item.Node.GetChild(i)];
                facts[item.Node] = contains;
            }
        }
        return facts[root];
    }

    private async ValueTask<SyntaxNode> SourceAsync(SourceFileNode node)
    {
        if (node.IsDeclarationFile) return node;
        int kind = UsingKind(node.Statements);
        SyntaxNode updated;
        if (kind == 0) updated = (await VisitEachChildAsync(node))!;
        else
        {
            Context.StartVariableEnvironment();
            var input = node.Statements!;
            int start = 0;
            while (start < input.Count && UsingKind(input[start]) == 0) start++;
            List<SyntaxNode> topLevel = [.. await VisitArrayAsync(input.Take(start).ToArray())];
            var env = Context.NewUniqueName("env"u8);
            var body = await StatementsAsync(input.Skip(start), env, topLevel);
            if (exportBindings.Count != 0)
                topLevel.Add(F.NewExportDeclaration(null, false, F.NewNamedExports(new(exportNames.Select(name => (SyntaxNode)exportBindings[name]).ToArray())), null, null));
            topLevel.AddRange(Context.EndVariableEnvironment());
            if (exportVariables.Count != 0)
                topLevel.Add(F.NewVariableStatement(new([F.NewToken(K.ExportKeyword)]), F.NewVariableDeclarationList(new(exportVariables.ToArray()), NodeFlags.Let)));
            topLevel.AddRange(Downlevel(body, env, kind == 2));
            if (exportEqualsBinding is not null) topLevel.Add(F.NewExportAssignment(null, true, null, exportEqualsBinding));
            var result = Context.Clone(node);
            result.Statements = new(topLevel.ToArray());
            updated = result;
        }
        foreach (var helper in Context.ReadHelpers()) Context.AddHelper(updated, helper);
        return updated;
    }

    private async ValueTask<SyntaxNode> BlockAsync(BlockNode node)
    {
        int kind = UsingKind(node.Statements);
        if (kind == 0) return (await VisitEachChildAsync(node))!;
        var input = node.Statements!;
        int start = 0;
        while (start < input.Count && input[start] is ExpressionStatementNode { Expression: StringLiteralNode }) start++;
        var env = Context.NewUniqueName("env"u8);
        List<SyntaxNode> statements = [.. await VisitArrayAsync(input.Take(start).ToArray())];
        statements.AddRange(Downlevel(await StatementsAsync(input.Skip(start), env, null), env, kind == 2));
        var result = Context.Clone(node);
        result.Statements = new(statements.ToArray(), input.Pos, input.End);
        return result;
    }

    private async ValueTask<SyntaxNode?> ForAsync(ForStatementNode node)
    {
        var loop = Context.Clone(node);
        loop.Initializer = null;
        return await VisitAsync(F.NewBlock(new([F.NewVariableStatement(null, (VariableDeclarationListNode)node.Initializer!), loop]), false));
    }

    private async ValueTask<SyntaxNode?> ForOfAsync(ForInOrOfStatementNode node)
    {
        var initializer = (VariableDeclarationListNode)node.Initializer!;
        var declaration = initializer.Declarations?.FirstOrDefault() as VariableDeclarationNode ?? F.NewVariableDeclaration(Context.NewTempVariable(), null, null, null);
        var temp = Context.NewGeneratedNameForNode(declaration.Name!);
        var usingDeclaration = Context.Clone(declaration);
        usingDeclaration.Type = null;
        usingDeclaration.ExclamationToken = null;
        usingDeclaration.Initializer = temp;
        var usingStatement = F.NewVariableStatement(null, F.NewVariableDeclarationList(new([usingDeclaration]), UsingKind(initializer) == 2 ? NodeFlags.AwaitUsing : NodeFlags.Using));
        BlockNode body;
        if (node.Statement is BlockNode originalBody)
        {
            body = Context.Clone(originalBody);
            body.Statements = new([usingStatement, .. originalBody.Statements ?? new([])]);
        }
        else body = F.NewBlock(new([usingStatement, node.Statement!]), true);
        var loop = Context.Clone(node);
        loop.Initializer = F.NewVariableDeclarationList(new([F.NewVariableDeclaration(temp, null, null, null)]), NodeFlags.Const);
        loop.Statement = body;
        return await VisitAsync(loop);
    }

    private async ValueTask<List<SyntaxNode>> StatementsAsync(IEnumerable<SyntaxNode> input, IdentifierNode env, List<SyntaxNode>? topLevel)
    {
        List<SyntaxNode> statements = [];
        foreach (var statement in input)
        {
            Cancellation.ThrowIfCancellationRequested();
            int kind = UsingKind(statement);
            if (kind != 0)
            {
                var originalStatement = (VariableStatementNode)statement;
                var originalList = (VariableDeclarationListNode)originalStatement.DeclarationList!;
                List<SyntaxNode> declarations = [];
                foreach (VariableDeclarationNode declaration in originalList.Declarations!)
                {
                    if (declaration.Name is not IdentifierNode) { declarations.Clear(); break; }
                    var evaluated = named.Applies(declaration) ? (VariableDeclarationNode)named.Transform(declaration) : declaration;
                    var initializer = await VisitAsync(evaluated.Initializer) ?? Context.VoidZero();
                    var updated = Context.Clone(evaluated);
                    updated.Type = null;
                    updated.ExclamationToken = null;
                    updated.Initializer = Context.HelperCall(EmitHelpers.AddDisposableResource, "__addDisposableResource"u8,
                        [env, initializer, F.NewKeywordExpression(kind == 2 ? K.TrueKeyword : K.FalseKeyword)]);
                    declarations.Add(updated);
                }
                if (declarations.Count != 0)
                {
                    var list = EmitContext.CopyRange(F.NewVariableDeclarationList(new(declarations.ToArray()), NodeFlags.Const), originalList);
                    Context.SetOriginal(list, originalList);
                    var updated = Context.Clone(originalStatement);
                    updated.Modifiers = null;
                    updated.DeclarationList = list;
                    if (await HoistAsync(updated, topLevel) is { } result) statements.Add(result);
                    continue;
                }
            }
            foreach (var visited in await VisitArrayAsync([statement]))
                if (await HoistAsync(visited, topLevel) is { } result) statements.Add(result);
        }
        return statements;
    }

    private async ValueTask<SyntaxNode?> HoistAsync(SyntaxNode node, List<SyntaxNode>? topLevel)
    {
        if (topLevel is null) return node;
        switch (node)
        {
            case ImportDeclarationNode or ImportEqualsDeclarationNode or ExportDeclarationNode or FunctionDeclarationNode:
                topLevel.Add(node);
                return null;
            case ExportAssignmentNode export:
                return ExportAssignment(export);
            case ClassDeclarationNode declaration:
                return Class(declaration);
            case VariableStatementNode statement:
                List<SyntaxNode> expressions = [];
                bool exported = SemanticSyntax.HasModifier(statement, K.ExportKeyword);
                foreach (VariableDeclarationNode variable in ((VariableDeclarationListNode)statement.DeclarationList!).Declarations!)
                {
                    HoistPattern(variable.Name!, exported, variable);
                    if (variable.Initializer is not null)
                    {
                        SyntaxNode target;
                        if (variable.Name is IdentifierNode name)
                        {
                            target = Context.Clone(name);
                            Context.SetFlags(target, Context.GetFlags(target) & ~(EmitFlags.LocalName | EmitFlags.ExportName));
                        }
                        else target = await Context.BindingAssignmentAsync(variable.Name!, Cancellation);
                        expressions.Add(Mapped(Assign(target, variable.Initializer), variable));
                    }
                }
                return expressions.Count == 0 ? null : Mapped(F.NewExpressionStatement(Context.InlineExpressions(expressions)), statement);
            default: return node;
        }
    }

    private SyntaxNode ExportAssignment(ExportAssignmentNode node)
    {
        if (node.IsExportEquals)
        {
            if (exportEqualsBinding is not null) return node;
            exportEqualsBinding = NewDefault();
            Context.AddVariableDeclaration(exportEqualsBinding);
            return F.NewExpressionStatement(Assign(exportEqualsBinding, node.Expression!));
        }
        if (defaultBinding is not null) return node;
        defaultBinding = NewDefault();
        HoistIdentifier(defaultBinding, true, F.NewIdentifier("default"u8), node);
        var expression = node.Expression!;
        var inner = NamedEvaluation.SkipOuter(expression);
        if (named.Applies(inner)) expression = named.RestoreOuter(expression, named.Transform(inner, assignedText: "default"u8));
        return F.NewExpressionStatement(Assign(defaultBinding, expression));
    }

    private SyntaxNode Class(ClassDeclarationNode node)
    {
        if (node.Name is null && defaultBinding is not null) return node;
        bool exported = SemanticSyntax.HasModifier(node, K.ExportKeyword), isDefault = SemanticSyntax.HasModifier(node, K.DefaultKeyword);
        var modifiers = node.Modifiers is { } originalModifiers
            ? new NodeList(originalModifiers.Where(m => m.Kind is not K.ExportKeyword and not K.DefaultKeyword).ToArray(), originalModifiers.Pos, originalModifiers.End) : null;
        SyntaxNode expression = EmitContext.CopyRange(F.NewClassExpression(modifiers, node.Name, node.TypeParameters, node.HeritageClauses, node.Members), node);
        Context.SetOriginal(expression, node);
        if (node.Name is not null)
        {
            HoistIdentifier(Context.GetLocalName(node), exported && !isDefault, null, node);
            expression = Mapped(Assign(Context.GetDeclarationName(node), expression), node);
            if (named.Applies(expression)) expression = named.Transform(expression);
        }
        if (isDefault && defaultBinding is null)
        {
            defaultBinding = NewDefault();
            HoistIdentifier(defaultBinding, true, F.NewIdentifier("default"u8), node);
            expression = Assign(defaultBinding, expression);
            Context.SetOriginal(expression, node);
            if (named.Applies(expression)) expression = named.Transform(expression, assignedText: "default"u8);
        }
        return F.NewExpressionStatement(expression);
    }

    private void HoistPattern(SyntaxNode root, bool exported, SyntaxNode original)
    {
        Stack<SyntaxNode> pending = new([root]);
        while (pending.TryPop(out var node))
        {
            Cancellation.ThrowIfCancellationRequested();
            if (node is BindingPatternNode { Elements: { } elements })
            {
                for (int i = elements.Count - 1; i >= 0; i--) if (elements[i].DeclarationName is { } name) pending.Push(name);
            }
            else HoistIdentifier((IdentifierNode)node, exported, null, original);
        }
    }

    private void HoistIdentifier(IdentifierNode node, bool exported, IdentifierNode? alias, SyntaxNode original)
    {
        var name = Context.GetAutoGenerateInfo(node) is null ? Context.Clone(node) : node;
        if (exported)
        {
            if (alias is null && (Context.GetFlags(name) & EmitFlags.LocalName) == 0)
            {
                var declaration = F.NewVariableDeclaration(name, null, null, null);
                Context.SetOriginal(declaration, original);
                exportVariables.Add(declaration);
                return;
            }
            var specifier = F.NewExportSpecifier(false, alias is null ? null : name, alias ?? name);
            Context.SetOriginal(specifier, original);
            if (!exportBindings.ContainsKey(name.Text)) exportNames.Add(name.Text);
            exportBindings[name.Text] = specifier;
        }
        Context.AddVariableDeclaration(name);
    }

    private IdentifierNode NewDefault() => Context.NewUniqueName("_default"u8,
        new(GeneratedIdentifierFlags.ReservedInNestedScopes | GeneratedIdentifierFlags.FileLevel | GeneratedIdentifierFlags.Optimistic));
    private BinaryExpressionNode Assign(SyntaxNode target, SyntaxNode value) => Context.Binary(target, K.EqualsToken, value);
    private PropertyAccessExpressionNode Property(SyntaxNode target, Utf8String name) => F.NewPropertyAccessExpression(target, null, F.NewIdentifier(name), NodeFlags.None);
    private T Mapped<T>(T node, SyntaxNode original) where T : SyntaxNode
    {
        Context.SetOriginal(node, original);
        Context.SetSourceMapRange(node, new(original.Pos, original.End));
        Context.SetCommentRange(node, new(original.Pos, original.End));
        return node;
    }

    private SyntaxNode[] Downlevel(List<SyntaxNode> body, IdentifierNode env, bool isAsync)
    {
        var obj = F.NewObjectLiteralExpression(new([
            F.NewPropertyAssignment(null, F.NewIdentifier("stack"u8), null, null, F.NewArrayLiteralExpression(null, false)),
            F.NewPropertyAssignment(null, F.NewIdentifier("error"u8), null, null, Context.VoidZero()),
            F.NewPropertyAssignment(null, F.NewIdentifier("hasError"u8), null, null, F.NewKeywordExpression(K.FalseKeyword))]), false);
        var declaration = F.NewVariableStatement(null, F.NewVariableDeclarationList(new([F.NewVariableDeclaration(env, null, null, obj)]), NodeFlags.Const));
        var caught = Context.NewUniqueName("e"u8);
        var catchClause = F.NewCatchClause(F.NewVariableDeclaration(caught, null, null, null), F.NewBlock(new([
            F.NewExpressionStatement(Assign(Property(env, "error"u8), caught)),
            F.NewExpressionStatement(Assign(Property(env, "hasError"u8), F.NewKeywordExpression(K.TrueKeyword)))]), true));
        var dispose = Context.HelperCall(EmitHelpers.DisposeResources, "__disposeResources"u8, [env]);
        SyntaxNode[] finallyStatements;
        if (isAsync)
        {
            var result = Context.NewUniqueName("result"u8);
            finallyStatements = [F.NewVariableStatement(null, F.NewVariableDeclarationList(new([F.NewVariableDeclaration(result, null, null, dispose)]), NodeFlags.Const)),
                F.NewIfStatement(result, F.NewExpressionStatement(F.NewAwaitExpression(result)), null)];
        }
        else finallyStatements = [F.NewExpressionStatement(dispose)];
        return [declaration, F.NewTryStatement(F.NewBlock(new(body.ToArray()), true), catchClause, F.NewBlock(new(finallyStatements), true))];
    }
}

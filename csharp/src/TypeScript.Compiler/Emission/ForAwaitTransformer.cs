using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed class ForAwaitTransformer : SyntaxRewriter
{
    private sealed class Fallback(ForAwaitTransformer owner) : SyntaxRewriter(owner.Context, owner.Cancellation)
    {
        protected override ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node) => owner.FallbackAsync(node);
        internal ValueTask<SyntaxNode?> ChildrenAsync(SyntaxNode node) => VisitEachChildAsync(node);
    }
    private readonly Fallback fallback;
    private readonly Dictionary<SyntaxNode, bool> facts = [];
    private bool isAsync, isGenerator, lexicalThis, iteration;
    private SuperAccessState? super;
    private NodeFactory F => Context.Factory;

    internal ForAwaitTransformer(EmitContext context, CancellationToken cancellation = default) : base(context, cancellation) => fallback = new(this);

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        bool savedThis = lexicalThis, savedIteration = iteration;
        try
        {
            if (!ContainsTransform(node)) return await FallbackAsync(node);
            super?.Track(node);
            switch (node)
            {
                case SourceFileNode source:
                    if (source.IsDeclarationFile) return source;
                    iteration = false;
                    var result = (await VisitEachChildAsync(source))!;
                    foreach (var helper in Context.ReadHelpers()) Context.AddHelper(result, helper);
                    return result;
                case AwaitExpressionNode expression when isAsync && isGenerator:
                    return Original(F.NewYieldExpression(null, Helper(EmitHelpers.Await, "__await"u8, [(await VisitAsync(expression.Expression))!])), expression);
                case YieldExpressionNode expression when isAsync && isGenerator:
                    var value = await VisitAsync(expression.Expression) ?? Context.VoidZero();
                    if (expression.AsteriskToken is not null)
                    {
                        var values = EmitContext.CopyRange(Helper(EmitHelpers.AsyncValues, "__asyncValues"u8, [value]), value);
                        Context.RequestHelper(EmitHelpers.Await);
                        var delegator = EmitContext.CopyRange(Helper(EmitHelpers.AsyncDelegator, "__asyncDelegator"u8, [values]), value);
                        var inner = Context.Clone(expression);
                        inner.Expression = delegator;
                        return Original(F.NewYieldExpression(null, Helper(EmitHelpers.Await, "__await"u8, [inner])), expression);
                    }
                    return Original(F.NewYieldExpression(null, DownlevelAwait(value)), expression);
                case ReturnStatementNode statement when isAsync && isGenerator:
                    var ret = Context.Clone(statement);
                    ret.Expression = DownlevelAwait(await VisitAsync(statement.Expression) ?? Context.VoidZero());
                    return ret;
                case LabeledStatementNode label when isAsync:
                    SyntaxNode innerStatement = label.Statement!;
                    while (innerStatement is LabeledStatementNode nested) innerStatement = nested.Statement!;
                    if (innerStatement is ForInOrOfStatementNode { Kind: K.ForOfStatement, AwaitModifier: not null } labeledLoop)
                        return await ForOfAsync(labeledLoop, label);
                    return RestoreLabels((await VisitAsync(innerStatement))!, label);
                case ForInOrOfStatementNode { Kind: K.ForOfStatement } loop: return await ForOfAsync(loop, null);
                case DoStatementNode or WhileStatementNode or ForStatementNode or ForInOrOfStatementNode:
                    iteration = true;
                    return await VisitEachChildAsync(node);
                case FunctionDeclarationNode or FunctionExpressionNode or MethodDeclarationNode or GetAccessorDeclarationNode
                    or SetAccessorDeclarationNode or ConstructorDeclarationNode or ArrowFunctionNode:
                    iteration = false;
                    if (node is not ArrowFunctionNode) lexicalThis = true;
                    return await FunctionAsync(node);
                case ClassDeclarationNode or ClassExpressionNode:
                    iteration = false;
                    lexicalThis = true;
                    return await VisitEachChildAsync(node);
                default: return await VisitEachChildAsync(node);
            }
        }
        finally
        {
            lexicalThis = savedThis;
            iteration = savedIteration;
            if (node is SourceFileNode) facts.Clear();
        }
    }

    private bool ContainsTransform(SyntaxNode root)
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
                contains = item.Node is ReturnStatementNode or YieldExpressionNode or AwaitExpressionNode
                    or ForInOrOfStatementNode { Kind: K.ForOfStatement, AwaitModifier: not null } || AsyncTransformer.AsyncGenerator(item.Node);
                for (int i = 0; !contains && i < item.Node.ChildCount; i++) contains = facts[item.Node.GetChild(i)];
                facts[item.Node] = contains;
            }
        }
        return facts[root];
    }

    private ValueTask<SyntaxNode?> FallbackAsync(SyntaxNode node)
    {
        if (super is null || node is FunctionDeclarationNode or FunctionExpressionNode or MethodDeclarationNode
            or GetAccessorDeclarationNode or SetAccessorDeclarationNode or ConstructorDeclarationNode) return ValueTask.FromResult<SyntaxNode?>(node);
        super.Track(node);
        return fallback.ChildrenAsync(node);
    }

    private T Original<T>(T result, SyntaxNode original) where T : SyntaxNode
    {
        EmitContext.CopyRange(result, original);
        Context.SetOriginal(result, original);
        return result;
    }
    private CallExpressionNode Helper(EmitHelper helper, Utf8String name, SyntaxNode[] arguments)
    {
        Context.RequestHelper(helper);
        var identifier = F.NewIdentifier(name);
        Context.AddFlags(identifier, EmitFlags.HelperName);
        return F.NewCallExpression(identifier, null, null, new(arguments), NodeFlags.None);
    }
    private SyntaxNode DownlevelAwait(SyntaxNode value) => isGenerator
        ? F.NewYieldExpression(null, Helper(EmitHelpers.Await, "__await"u8, [value])) : F.NewAwaitExpression(value);
    private PropertyAccessExpressionNode Property(SyntaxNode value, Utf8String name) => F.NewPropertyAccessExpression(value, null, F.NewIdentifier(name), NodeFlags.None);
    private SyntaxNode Assign(SyntaxNode target, SyntaxNode value) => Context.Binary(target, K.EqualsToken, value);
    private SyntaxNode Not(SyntaxNode value) => F.NewPrefixUnaryExpression(K.ExclamationToken, value);
    private BlockNode Block(bool multiLine, params SyntaxNode[] statements) => F.NewBlock(new(statements), multiLine);

    private SyntaxNode RestoreLabels(SyntaxNode statement, LabeledStatementNode? label)
    {
        Stack<LabeledStatementNode> labels = [];
        while (label is not null) { labels.Push(label); label = label.Statement as LabeledStatementNode; }
        while (labels.TryPop(out label))
        {
            var result = Context.Clone(label);
            result.Statement = statement;
            statement = result;
        }
        return statement;
    }

    private async ValueTask<SyntaxNode> ForOfAsync(ForInOrOfStatementNode node, LabeledStatementNode? label)
    {
        bool outerIteration = iteration;
        iteration = true;
        try
        {
            if (node.AwaitModifier is null) return RestoreLabels((await VisitEachChildAsync(node))!, label);
            var expression = (await VisitAsync(node.Expression))!;
            var iterator = expression is IdentifierNode ? Context.NewGeneratedNameForNode(expression) : Context.NewTempVariable();
            var result = expression is IdentifierNode ? Context.NewGeneratedNameForNode(iterator) : Context.NewTempVariable();
            var nonUserCode = Context.NewTempVariable();
            var done = Context.NewTempVariable();
            Context.AddVariableDeclaration(done);
            var error = Context.NewUniqueName("e"u8);
            var caught = Context.NewGeneratedNameForNode(error);
            var returnMethod = Context.NewTempVariable();
            var values = EmitContext.CopyRange(Helper(EmitHelpers.AsyncValues, "__asyncValues"u8, [expression]), node.Expression!);
            Context.AddVariableDeclaration(error);
            Context.AddVariableDeclaration(returnMethod);
            SyntaxNode initializer = outerIteration ? Context.Binary(Assign(error, Context.VoidZero()), K.CommaToken, values) : values;
            var iteratorDeclaration = EmitContext.CopyRange(F.NewVariableDeclaration(iterator, null, null, initializer), node.Expression!);
            var declarations = EmitContext.CopyRange(F.NewVariableDeclarationList(new([
                F.NewVariableDeclaration(nonUserCode, null, null, F.NewKeywordExpression(K.TrueKeyword)), iteratorDeclaration,
                F.NewVariableDeclaration(result, null, null, null)]), NodeFlags.None), node.Expression!);
            var condition = Context.InlineExpressions([Assign(result, DownlevelAwait(Context.MethodCall(iterator, "next"u8, []))),
                Assign(done, Property(result, "done"u8)), Not(done)]);
            var forLoop = Original(F.NewForStatement(declarations, condition, Assign(nonUserCode, F.NewKeywordExpression(K.TrueKeyword)),
                await ForOfBodyAsync(node, Property(result, "value"u8), nonUserCode)), node);
            Context.AddFlags(forLoop, EmitFlags.NoTokenTrailingSourceMaps);
            var tryBlock = Block(true, RestoreLabels(forLoop, label));
            var catchBody = Block(false, F.NewExpressionStatement(Assign(error, F.NewObjectLiteralExpression(new([
                F.NewPropertyAssignment(null, F.NewIdentifier("error"u8), null, null, caught)]), false))));
            Context.AddFlags(catchBody, EmitFlags.SingleLine);
            var catchClause = F.NewCatchClause(F.NewVariableDeclaration(caught, null, null, null), catchBody);
            var close = F.NewIfStatement(Context.Binary(Context.Binary(Not(nonUserCode), K.AmpersandAmpersandToken, Not(done)),
                K.AmpersandAmpersandToken, Assign(returnMethod, Property(iterator, "return"u8))),
                F.NewExpressionStatement(DownlevelAwait(Context.MethodCall(returnMethod, "call"u8, [iterator]))), null);
            Context.AddFlags(close, EmitFlags.SingleLine);
            var rethrow = F.NewIfStatement(error, F.NewThrowStatement(Property(error, "error"u8)), null);
            Context.AddFlags(rethrow, EmitFlags.SingleLine);
            var innerFinally = Block(false, rethrow);
            Context.AddFlags(innerFinally, EmitFlags.SingleLine);
            return F.NewTryStatement(tryBlock, catchClause, Block(true, F.NewTryStatement(Block(false, close), null, innerFinally)));
        }
        finally { iteration = outerIteration; }
    }

    private async ValueTask<BlockNode> ForOfBodyAsync(ForInOrOfStatementNode node, SyntaxNode boundValue, SyntaxNode nonUserCode)
    {
        var value = Context.NewTempVariable();
        Context.AddVariableDeclaration(value);
        var read = F.NewExpressionStatement(Assign(value, boundValue));
        var enter = F.NewExpressionStatement(Assign(nonUserCode, F.NewKeywordExpression(K.FalseKeyword)));
        Context.SetSourceMapRange(read, new(node.Expression!.Pos, node.Expression.End));
        Context.SetSourceMapRange(enter, new(node.Expression.Pos, node.Expression.End));
        SyntaxNode binding;
        if (node.Initializer is VariableDeclarationListNode list)
        {
            var declaration = Context.Clone((VariableDeclarationNode)list.Declarations![0]);
            declaration.ExclamationToken = null;
            declaration.Type = null;
            declaration.Initializer = value;
            var updatedList = Context.Clone(list);
            updatedList.Declarations = new([declaration]);
            binding = EmitContext.CopyRange(F.NewVariableStatement(null, updatedList), list);
        }
        else binding = EmitContext.CopyRange(F.NewExpressionStatement(EmitContext.CopyRange(Assign(node.Initializer!, value), node.Initializer!)), node.Initializer!);
        List<SyntaxNode> statements = [read, enter, (await VisitAsync(binding))!];
        var body = (await VisitEmbeddedStatementAsync(node.Statement))!;
        int pos = 0, end = 0, listPos = 0, listEnd = 0;
        if (body is BlockNode block)
        {
            statements.AddRange(block.Statements ?? new([]));
            pos = body.Pos; end = body.End;
            listPos = block.Statements?.Pos ?? 0; listEnd = block.Statements?.End ?? 0;
        }
        else statements.Add(body);
        var result = F.NewBlock(new(statements.ToArray(), listPos, listEnd), true);
        result.Pos = pos; result.End = end;
        return result;
    }

    private static bool SimpleParameters(NodeList? parameters) => parameters is null || parameters.All(p => p is ParameterDeclarationNode { Initializer: null, Name: IdentifierNode });
    private async ValueTask<SyntaxNode> FunctionAsync(SyntaxNode node)
    {
        bool savedAsync = isAsync, savedGenerator = isGenerator;
        isAsync = SemanticSyntax.HasModifier(node, K.AsyncKeyword);
        isGenerator = node is FunctionDeclarationNode { AsteriskToken: not null } or FunctionExpressionNode { AsteriskToken: not null } or MethodDeclarationNode { AsteriskToken: not null };
        try
        {
            var signature = (IFunctionSignature)node;
            var modifiers = ((IModifiedNode)node).Modifiers;
            if (isGenerator && modifiers is not null && modifiers.Any(m => m.Kind == K.AsyncKeyword))
                modifiers = new(modifiers.Where(m => m.Kind != K.AsyncKeyword).ToArray(), modifiers.Pos, modifiers.End);
            var parameters = isAsync && isGenerator ? await GeneratorParametersAsync(signature.Parameters) : await VisitParametersAsync(signature.Parameters);
            var body = isAsync && isGenerator ? await GeneratorBodyAsync(node) : await VisitFunctionBodyAsync(DecoratorSyntax.Body(node));
            var name = node is MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode ? await VisitAsync(node.DeclarationName) : node.DeclarationName;
            var result = Context.Clone(node);
            ((IModifiedNode)result).Modifiers = modifiers;
            ((IFunctionSignature)result).Parameters = parameters;
            ((IFunctionSignature)result).Type = null;
            ((IFunctionSignature)result).TypeParameters = null;
            ((IFullSignatureNode)result).FullSignature = null;
            switch (result)
            {
                case FunctionDeclarationNode n: n.Body = body; if (isAsync) n.AsteriskToken = null; break;
                case FunctionExpressionNode n: n.Body = body; if (isAsync) n.AsteriskToken = null; break;
                case MethodDeclarationNode n: n.Body = body; n.Name = name; n.PostfixToken = null; if (isAsync) n.AsteriskToken = null; break;
                case GetAccessorDeclarationNode n: n.Body = body; n.Name = name; break;
                case SetAccessorDeclarationNode n: n.Body = body; n.Name = name; break;
                case ConstructorDeclarationNode n: n.Body = body; break;
                case ArrowFunctionNode n: n.Body = body; break;
            }
            return result;
        }
        finally { isAsync = savedAsync; isGenerator = savedGenerator; }
    }

    private async ValueTask<NodeList?> GeneratorParametersAsync(NodeList? parameters)
    {
        if (SimpleParameters(parameters) && parameters?.Any(p => p.DeclarationName is IdentifierNode { Text: var name } && name == "arguments"u8) != true)
            return await VisitParametersAsync(parameters);
        if (SimpleParameters(parameters)) Context.BeginParameters();
        List<SyntaxNode> result = [];
        foreach (ParameterDeclarationNode parameter in parameters!)
        {
            if (parameter.Initializer is not null || parameter.DotDotDotToken is not null) break;
            result.Add(F.NewParameterDeclaration(null, null, Context.NewGeneratedNameForNode(parameter.Name!, new(GeneratedIdentifierFlags.ReservedInNestedScopes)), null, null, null));
        }
        var updated = new NodeList(result.ToArray(), parameters.Pos, parameters.End);
        return SimpleParameters(parameters) ? Context.EndParameters(updated) : updated;
    }

    private async ValueTask<SyntaxNode> GeneratorBodyAsync(SyntaxNode node)
    {
        var parameters = ((IFunctionSignature)node).Parameters;
        var savedSuper = super;
        super = new(Context, Cancellation);
        try
        {
            // Keep parameter bindings in the generator so duplicate vars and mapped arguments
            // retain the original function's scope. Evaluate defaults when the iterator is created.
            var innerParameters = SimpleParameters(parameters) ? parameters : await VisitParametersAsync(parameters);
            var originalBody = (BlockNode)DecoratorSyntax.Body(node)!;
            var body = Context.Clone(originalBody);
            body.Statements = await VisitListAsync(originalBody.Statements);
            body.Statements = Context.MergeEnvironment(body.Statements, Context.EndVariableEnvironment());
            if (super.HasAccess)
            {
                innerParameters = await super.ParametersAsync(innerParameters);
                body = (BlockNode)(await super.VisitAsync(body))!;
            }
            var name = node.DeclarationName is { } originalName ? Context.NewGeneratedNameForNode(originalName) : null;
            var generator = F.NewFunctionExpression(null, F.NewToken(K.AsteriskToken), name, null, innerParameters ?? new([]), null, null, body);
            Context.AddFlags(generator, EmitFlags.AsyncFunctionBody | EmitFlags.ReuseTempVariableScope);
            Context.RequestHelper(EmitHelpers.Await);
            var call = Helper(EmitHelpers.AsyncGenerator, "__asyncGenerator"u8,
                [lexicalThis ? F.NewKeywordExpression(K.ThisKeyword) : Context.VoidZero(), F.NewIdentifier("arguments"u8), generator]);
            Context.StartVariableEnvironment();
            if (super.Properties.Count != 0) Context.AddInitializationStatement(super.Declaration());
            var result = Context.Clone(originalBody);
            result.Statements = Context.MergeEnvironment(new([F.NewReturnStatement(call)]), Context.EndVariableEnvironment());
            super.AddHelper(result);
            return result;
        }
        finally { super = savedSuper; }
    }
}

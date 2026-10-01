using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed class AsyncTransformer : SyntaxRewriter
{
    private sealed class ArgumentsInfo(IdentifierNode binding) { internal readonly IdentifierNode Binding = binding; internal bool Used; }
    private sealed class Auxiliary(AsyncTransformer owner, bool body) : SyntaxRewriter(owner.Context, owner.Cancellation)
    {
        protected override ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node) => body ? owner.BodyNodeAsync(node) : owner.FallbackAsync(node);
        internal ValueTask<SyntaxNode?> ChildrenAsync(SyntaxNode node) => VisitEachChildAsync(node);
        internal ValueTask<NodeList?> ListAsync(NodeList? nodes) => VisitListAsync(nodes);
        internal ValueTask<SyntaxNode?> EmbeddedAsync(SyntaxNode? node) => VisitEmbeddedStatementAsync(node);
    }
    private readonly Auxiliary bodyVisitor, fallback;
    private readonly Dictionary<SyntaxNode, bool> awaitFacts = [];
    private bool nonTopLevel, lexicalThis;
    private SuperAccessState? super;
    private ArgumentsInfo? arguments;
    private HashSet<Utf8String>? parameterNames;
    private bool parametersInOuterScope;
    private NodeFactory F => Context.Factory;

    internal AsyncTransformer(EmitContext context, CancellationToken cancellation = default) : base(context, cancellation)
    {
        bodyVisitor = new(this, true);
        fallback = new(this, false);
    }

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        bool savedThis = lexicalThis, savedTop = nonTopLevel;
        if ((Context.GetFlags(node) & EmitFlags.NoLexicalThis) != 0) lexicalThis = false;
        try
        {
            if (!ContainsAwait(node)) return await FallbackAsync(node);
            super?.Track(node);
            switch (node)
            {
                case SourceFileNode source:
                    if (source.IsDeclarationFile) return source;
                    nonTopLevel = lexicalThis = false;
                    var result = (await VisitEachChildAsync(node))!;
                    foreach (var helper in Context.ReadHelpers()) Context.AddHelper(result, helper);
                    return result;
                case { Kind: K.AsyncKeyword }: return null;
                case AwaitExpressionNode expression when nonTopLevel:
                    var yield = EmitContext.CopyRange(F.NewYieldExpression(null, await VisitAsync(expression.Expression)), expression);
                    Context.SetOriginal(yield, expression);
                    return yield;
                case FunctionDeclarationNode or FunctionExpressionNode or MethodDeclarationNode or GetAccessorDeclarationNode
                    or SetAccessorDeclarationNode or ConstructorDeclarationNode or ArrowFunctionNode:
                    nonTopLevel = true;
                    if (node is not ArrowFunctionNode) lexicalThis = true;
                    return await FunctionAsync(node);
                case ClassDeclarationNode or ClassExpressionNode:
                    nonTopLevel = lexicalThis = true;
                    return await VisitEachChildAsync(node);
                default: return await VisitEachChildAsync(node);
            }
        }
        finally
        {
            lexicalThis = savedThis;
            nonTopLevel = savedTop;
            if (node is SourceFileNode) awaitFacts.Clear();
        }
    }

    private bool ContainsAwait(SyntaxNode root)
    {
        if (awaitFacts.TryGetValue(root, out bool contains)) return contains;
        Stack<(SyntaxNode Node, bool Visited)> pending = new([(root, false)]);
        while (pending.TryPop(out var item))
        {
            Cancellation.ThrowIfCancellationRequested();
            if (awaitFacts.ContainsKey(item.Node)) continue;
            if (!item.Visited)
            {
                pending.Push((item.Node, true));
                for (int i = item.Node.ChildCount - 1; i >= 0; i--) pending.Push((item.Node.GetChild(i), false));
            }
            else
            {
                contains = item.Node.Kind is K.AsyncKeyword or K.AwaitExpression;
                for (int i = 0; !contains && i < item.Node.ChildCount; i++) contains = awaitFacts[item.Node.GetChild(i)];
                awaitFacts[item.Node] = contains;
            }
        }
        return awaitFacts[root];
    }

    private async ValueTask<SyntaxNode?> FallbackAsync(SyntaxNode node)
    {
        if (super is null && arguments is null) return node;
        super?.Track(node);
        if (node is FunctionDeclarationNode or FunctionExpressionNode or MethodDeclarationNode or GetAccessorDeclarationNode
            or SetAccessorDeclarationNode or ConstructorDeclarationNode) return node;
        if (node is ShorthandPropertyAssignmentNode { Name: IdentifierNode { Text: var shorthandName } } shorthand
            && shorthandName == "arguments"u8 && arguments is not null)
        {
            arguments.Used = true;
            SyntaxNode value = arguments.Binding;
            if (shorthand.ObjectAssignmentInitializer is not null)
                value = F.NewBinaryExpression(null, value, null, shorthand.EqualsToken ?? F.NewToken(K.EqualsToken), await VisitAsync(shorthand.ObjectAssignmentInitializer));
            var property = EmitContext.CopyRange(F.NewPropertyAssignment(null, shorthand.Name, null, null, value), shorthand);
            Context.SetOriginal(property, shorthand);
            Context.AssignCommentAndSourceMapRanges(property, shorthand);
            return property;
        }
        if (node is IdentifierNode { Text: var text } && text == "arguments"u8 && arguments is not null && !IsIdentifierNameOrLabel(node))
        {
            arguments.Used = true;
            return arguments.Binding;
        }
        return await fallback.ChildrenAsync(node);
    }

    private static bool IsIdentifierNameOrLabel(SyntaxNode node) => node.Parent switch
    {
        PropertyDeclarationNode or PropertySignatureDeclarationNode or MethodDeclarationNode or MethodSignatureDeclarationNode or GetAccessorDeclarationNode
            or SetAccessorDeclarationNode or EnumMemberNode or PropertyAssignmentNode or PropertyAccessExpressionNode => node.Parent.DeclarationName == node,
        QualifiedNameNode n => n.Right == node,
        BindingElementNode n => n.PropertyName == node,
        ImportSpecifierNode n => n.PropertyName == node,
        ExportSpecifierNode or JsxAttributeNode or JsxSelfClosingElementNode or JsxOpeningElementNode or JsxClosingElementNode => true,
        LabeledStatementNode n => n.Label == node,
        BreakStatementNode n => n.Label == node,
        ContinueStatementNode n => n.Label == node,
        _ => false
    };

    private async ValueTask<SyntaxNode> FunctionAsync(SyntaxNode node)
    {
        bool arrow = node is ArrowFunctionNode;
        bool resetArguments = !arrow || (Context.GetFlags(node) & EmitFlags.NoLexicalArguments) != 0;
        var savedArguments = arguments;
        if (resetArguments) arguments = null;
        try
        {
            var signature = (IFunctionSignature)node;
            bool isAsync = SemanticSyntax.HasModifier(node, K.AsyncKeyword);
            var parameters = isAsync ? await AsyncParametersAsync(node) : await VisitParametersAsync(signature.Parameters);
            var body = isAsync ? await AsyncBodyAsync(node, parameters) : node is MethodDeclarationNode or ConstructorDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode
                ? await MethodBodyAsync(node) : await VisitFunctionBodyAsync(DecoratorSyntax.Body(node));
            var modifiers = await VisitListAsync(((IModifiedNode)node).Modifiers);
            var name = node is FunctionDeclarationNode or FunctionExpressionNode ? await VisitAsync(node.DeclarationName) : node.DeclarationName;
            if (parameters == signature.Parameters && body == DecoratorSyntax.Body(node) && modifiers == ((IModifiedNode)node).Modifiers && name == node.DeclarationName) return node;
            var result = Context.Clone(node);
            ((IModifiedNode)result).Modifiers = modifiers;
            ((IFunctionSignature)result).Parameters = parameters;
            ((IFunctionSignature)result).Type = null;
            ((IFunctionSignature)result).TypeParameters = null;
            ((IFullSignatureNode)result).FullSignature = null;
            switch (result)
            {
                case FunctionDeclarationNode n: n.Body = body; n.Name = (IdentifierNode?)name; break;
                case FunctionExpressionNode n: n.Body = body; n.Name = (IdentifierNode?)name; break;
                case ArrowFunctionNode n: n.Body = body; break;
                case MethodDeclarationNode n: n.Body = body; n.PostfixToken = null; break;
                case ConstructorDeclarationNode n: n.Body = body; break;
                case GetAccessorDeclarationNode n: n.Body = body; break;
                case SetAccessorDeclarationNode n: n.Body = body; break;
            }
            return result;
        }
        finally { if (resetArguments) arguments = savedArguments; }
    }

    private static bool SimpleParameters(NodeList? parameters) => parameters is null || parameters.All(p => p is ParameterDeclarationNode { Initializer: null, Name: IdentifierNode });
    private async ValueTask<NodeList?> AsyncParametersAsync(SyntaxNode node)
    {
        var parameters = ((IFunctionSignature)node).Parameters;
        if (SimpleParameters(parameters)) return await VisitParametersAsync(parameters);
        List<SyntaxNode> result = [];
        foreach (ParameterDeclarationNode parameter in parameters!)
        {
            if (parameter.Initializer is not null || parameter.DotDotDotToken is not null)
            {
                if (node is ArrowFunctionNode)
                    result.Add(F.NewParameterDeclaration(null, F.NewToken(K.DotDotDotToken), Context.NewUniqueName("args"u8, new(GeneratedIdentifierFlags.ReservedInNestedScopes)), null, null, null));
                break;
            }
            result.Add(F.NewParameterDeclaration(null, null, Context.NewGeneratedNameForNode(parameter.Name!, new(GeneratedIdentifierFlags.ReservedInNestedScopes)), null, null, null));
        }
        return new(result.ToArray(), parameters.Pos, parameters.End);
    }

    private async ValueTask<SyntaxNode?> MethodBodyAsync(SyntaxNode node)
    {
        var saved = super;
        super = new(Context, Cancellation);
        try
        {
            Context.StartVariableEnvironment();
            var body = (BlockNode?)(await VisitFunctionBodyAsync(DecoratorSyntax.Body(node)));
            bool emit = super.HasAccess && !AsyncGenerator(Context.MostOriginal(node));
            if (emit && super.Properties.Count != 0) Context.AddInitializationStatement(super.Declaration());
            var statements = Context.MergeEnvironment(body?.Statements, Context.EndVariableEnvironment());
            if (body is null) return null;
            BlockNode result;
            if (emit && super.ElementAccess && body.MultiLine != true) result = EmitContext.CopyRange(F.NewBlock(statements, true), body);
            else { result = Context.Clone(body); result.Statements = statements; }
            if (emit) super.AddHelper(result);
            return result;
        }
        finally { super = saved; }
    }

    internal static bool AsyncGenerator(SyntaxNode node) => SemanticSyntax.HasModifier(node, K.AsyncKeyword) && node is
        (FunctionDeclarationNode { AsteriskToken: not null } or FunctionExpressionNode { AsteriskToken: not null } or MethodDeclarationNode { AsteriskToken: not null });

    private async ValueTask<SyntaxNode> AsyncBodyAsync(SyntaxNode node, NodeList? outerParameters)
    {
        bool arrow = node is ArrowFunctionNode;
        var savedSuper = super;
        var savedArguments = arguments;
        var savedNames = parameterNames;
        bool savedOuterParameters = parametersInOuterScope;
        bool capture = false, completed = false;
        if (!arrow) super = new(Context, Cancellation);
        try
        {
            var parameters = ((IFunctionSignature)node).Parameters;
            var innerParameters = SimpleParameters(parameters) ? null : await VisitParametersAsync(parameters);
            // Parameters are visited before introducing the body capture, as in the reference transformer.
            savedArguments = arguments;
            capture = arguments is null;
            if (capture) arguments = new(Context.NewUniqueName("arguments"u8));
            SyntaxNode? argumentsExpression = null;
            if (innerParameters is not null)
            {
                if (arrow)
                {
                    List<SyntaxNode> bindings = [];
                    for (int i = 0; i < parameters!.Count && i < outerParameters!.Count; i++)
                    {
                        var original = (ParameterDeclarationNode)parameters[i];
                        var outer = (ParameterDeclarationNode)outerParameters[i];
                        if (original.Initializer is not null || original.DotDotDotToken is not null) { bindings.Add(F.NewSpreadElement(outer.Name)); break; }
                        bindings.Add(outer.Name!);
                    }
                    argumentsExpression = F.NewArrayLiteralExpression(new(bindings.ToArray()), false);
                }
                else argumentsExpression = F.NewIdentifier("arguments"u8);
            }
            parameterNames = [];
            parametersInOuterScope = SimpleParameters(parameters);
            foreach (var parameter in parameters ?? new([])) RecordNames(parameter, parameterNames);
            var originalBody = DecoratorSyntax.Body(node)!;
            BlockNode body;
            if (originalBody is BlockNode block)
            {
                body = Context.Clone(block);
                body.Statements = await bodyVisitor.ListAsync(block.Statements);
            }
            else
            {
                var ret = EmitContext.CopyRange(F.NewReturnStatement(await bodyVisitor.VisitAsync(originalBody)), originalBody);
                body = EmitContext.CopyRange(F.NewBlock(new([ret], originalBody.Pos, originalBody.End), false), originalBody);
            }
            body.Statements = Context.MergeEnvironment(body.Statements, Context.EndVariableEnvironment());
            bool emitSuper = super?.HasAccess == true;
            if (emitSuper)
            {
                innerParameters = await super!.ParametersAsync(innerParameters);
                body = (BlockNode)(await super.VisitAsync(body))!;
            }
            SyntaxNode result;
            if (!arrow)
            {
                Context.StartVariableEnvironment();
                if (emitSuper && super!.Properties.Count != 0) Context.AddInitializationStatement(super.Declaration());
                if (capture && arguments!.Used) Context.AddInitializationStatement(CaptureArguments());
                var statements = Context.MergeEnvironment(new([F.NewReturnStatement(Awaiter(argumentsExpression, innerParameters, body))]), Context.EndVariableEnvironment());
                var outer = EmitContext.CopyRange(F.NewBlock(statements, true), originalBody);
                if (emitSuper) super!.AddHelper(outer);
                result = outer;
            }
            else
            {
                result = Awaiter(argumentsExpression, innerParameters, body);
                if (capture && arguments!.Used)
                {
                    var outer = Context.ConvertToFunctionBlock(result, true);
                    Context.SetOriginal(outer.Statements![0], result);
                    outer.Statements = Context.MergeEnvironment(outer.Statements, [CaptureArguments()]);
                    result = outer;
                }
            }
            completed = true;
            return result;
        }
        finally
        {
            parameterNames = savedNames;
            parametersInOuterScope = savedOuterParameters;
            if (!arrow || !completed) { super = savedSuper; arguments = savedArguments; }
            else if (capture && !arguments!.Used) arguments = savedArguments;
            else if (capture) arguments!.Used = false;
        }
    }

    private CallExpressionNode Awaiter(SyntaxNode? args, NodeList? parameters, BlockNode body)
    {
        Context.RequestHelper(EmitHelpers.Awaiter);
        var generator = F.NewFunctionExpression(null, F.NewToken(K.AsteriskToken), null, null, parameters ?? new([]), null, null, body);
        Context.AddFlags(generator, EmitFlags.AsyncFunctionBody | EmitFlags.ReuseTempVariableScope);
        var name = F.NewIdentifier("__awaiter"u8);
        Context.SetFlags(name, EmitFlags.HelperName);
        return F.NewCallExpression(name, null, null, new([lexicalThis ? F.NewKeywordExpression(K.ThisKeyword) : Context.VoidZero(), args ?? Context.VoidZero(), Context.VoidZero(), generator]), NodeFlags.None);
    }

    private VariableStatementNode CaptureArguments()
    {
        var statement = F.NewVariableStatement(null, F.NewVariableDeclarationList(new([F.NewVariableDeclaration(arguments!.Binding, null, null, F.NewIdentifier("arguments"u8))]), NodeFlags.None));
        Context.AddFlags(statement, EmitFlags.StartOnNewLine | EmitFlags.CustomPrologue);
        return statement;
    }

    private async ValueTask<SyntaxNode?> BodyNodeAsync(SyntaxNode node)
    {
        switch (node)
        {
            case VariableStatementNode n when Colliding(n.DeclarationList):
                var expression = await CollidingListAsync(n.DeclarationList!, false);
                return expression is null ? null : F.NewExpressionStatement(expression);
            case VariableStatementNode: return await VisitEachChildAsync(node);
            case ForStatementNode n:
                var forLoop = Context.Clone(n);
                forLoop.Initializer = Colliding(n.Initializer) ? await CollidingListAsync((VariableDeclarationListNode)n.Initializer!, false) : await VisitAsync(n.Initializer);
                forLoop.Condition = await VisitAsync(n.Condition);
                forLoop.Incrementor = await VisitAsync(n.Incrementor);
                forLoop.Statement = await bodyVisitor.EmbeddedAsync(n.Statement);
                return forLoop;
            case ForInOrOfStatementNode n:
                var forEach = Context.Clone(n);
                forEach.AwaitModifier = await VisitAsync(n.AwaitModifier);
                forEach.Initializer = Colliding(n.Initializer) ? await CollidingListAsync((VariableDeclarationListNode)n.Initializer!, true) : await VisitAsync(n.Initializer);
                forEach.Expression = await VisitAsync(n.Expression);
                forEach.Statement = await bodyVisitor.EmbeddedAsync(n.Statement);
                return forEach;
            case CatchClauseNode n:
                var saved = parameterNames;
                if ((!parametersInOuterScope || n.VariableDeclaration?.DeclarationName is BindingPatternNode) && n.VariableDeclaration is not null && parameterNames is not null)
                {
                    HashSet<Utf8String> catchNames = [];
                    RecordNames(n.VariableDeclaration, catchNames);
                    if (catchNames.Overlaps(parameterNames)) { parameterNames = new(parameterNames); parameterNames.ExceptWith(catchNames); }
                }
                try { return await bodyVisitor.ChildrenAsync(node); }
                finally { parameterNames = saved; }
            case BlockNode or SwitchStatementNode or CaseBlockNode or CaseOrDefaultClauseNode or TryStatementNode
                or DoStatementNode or WhileStatementNode or IfStatementNode or WithStatementNode or LabeledStatementNode:
                return await bodyVisitor.ChildrenAsync(node);
            default: return await VisitAsync(node);
        }
    }

    private bool Colliding(SyntaxNode? node)
    {
        if (node is not VariableDeclarationListNode list || (node.Flags & NodeFlags.BlockScoped) != 0 || parameterNames is null) return false;
        HashSet<Utf8String> names = [];
        foreach (var declaration in list.Declarations ?? new([])) RecordNames(declaration, names);
        return names.Overlaps(parameterNames);
    }

    internal static void RecordNames(SyntaxNode root, HashSet<Utf8String> names)
    {
        Stack<SyntaxNode> pending = new([root]);
        while (pending.TryPop(out var node))
        {
            var name = node.DeclarationName;
            if (name is IdentifierNode identifier) names.Add(identifier.Text);
            else if (name is BindingPatternNode pattern)
                foreach (var element in pattern.Elements ?? new([])) if (element.Kind != K.OmittedExpression) pending.Push(element);
        }
    }

    private async ValueTask<SyntaxNode?> CollidingListAsync(VariableDeclarationListNode list, bool receiver)
    {
        foreach (var declaration in list.Declarations ?? new([]))
        {
            Stack<SyntaxNode> pending = new([declaration]);
            while (pending.TryPop(out var node))
            {
                if (node.DeclarationName is IdentifierNode name)
                {
                    // A duplicate var belongs to the original parameter's scope. Declaring it in
                    // the generated function would hide the parameter's incoming value.
                    if (Context.GetAutoGenerateInfo(name) is not null || !parametersInOuterScope || name.Text == "arguments"u8 || parameterNames?.Contains(name.Text) != true)
                        Context.AddVariableDeclaration(name);
                }
                else if (node.DeclarationName is BindingPatternNode pattern)
                    for (int i = (pattern.Elements?.Count ?? 0) - 1; i >= 0; i--)
                        if (pattern.Elements![i].Kind != K.OmittedExpression) pending.Push(pattern.Elements[i]);
            }
        }
        List<SyntaxNode> assignments = [];
        foreach (VariableDeclarationNode declaration in list.Declarations!)
            if (declaration.Initializer is not null)
            {
                var target = await Context.BindingAssignmentAsync(declaration.Name!, Cancellation);
                var assignment = Context.Binary(target, K.EqualsToken, declaration.Initializer);
                Context.SetSourceMapRange(assignment, new(declaration.Pos, declaration.End));
                assignments.Add((await VisitAsync(assignment))!);
            }
        if (assignments.Count != 0) return Context.InlineExpressions(assignments);
        return receiver ? await VisitAsync(await Context.BindingAssignmentAsync(list.Declarations[0].DeclarationName!, Cancellation)) : null;
    }
}

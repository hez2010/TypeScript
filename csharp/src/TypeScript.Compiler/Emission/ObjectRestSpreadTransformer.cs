using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed class ObjectRestSpreadTransformer(EmitContext context, CancellationToken cancellation = default) : SyntaxRewriter(context, cancellation)
{
    private bool exportedVariable, unusedResult;
    private HashSet<SyntaxNode>? followingParameters;
    private readonly Dictionary<SyntaxNode, (bool Any, bool Marker)> facts = [];
    private NodeFactory F => Context.Factory;

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        if (!Facts(node).Any && followingParameters is null) return node;
        bool unused = unusedResult;
        unusedResult = false;
        try
        {
            switch (node)
            {
                case SourceFileNode:
                    try
                    {
                        var result = (await VisitEachChildAsync(node))!;
                        foreach (var helper in Context.ReadHelpers()) Context.AddHelper(result, helper);
                        return result;
                    }
                    finally { facts.Clear(); }
                case ObjectLiteralExpressionNode literal: return await ObjectAsync(literal);
                case BinaryExpressionNode binary: return await BinaryAsync(binary, unused);
                case ExpressionStatementNode: unusedResult = true; return await VisitEachChildAsync(node);
                case ParenthesizedExpressionNode: unusedResult = unused; return await VisitEachChildAsync(node);
                case ForInOrOfStatementNode loop when node.Kind == K.ForOfStatement: return await ForOfAsync(loop);
                case VariableStatementNode statement when SemanticSyntax.HasModifier(statement, K.ExportKeyword):
                    bool previousExport = exportedVariable;
                    exportedVariable = true;
                    try { return await VisitEachChildAsync(statement); }
                    finally { exportedVariable = previousExport; }
                case VariableDeclarationNode variable:
                    bool exported = exportedVariable;
                    exportedVariable = false;
                    try
                    {
                        return variable.Name is BindingPatternNode && Facts(variable).Marker
                            ? await Flattener().BindingAsync(variable, null, exported, false) : await VisitEachChildAsync(variable);
                    }
                    finally { exportedVariable = exported; }
                case CatchClauseNode clause: return await CatchAsync(clause);
                case ParameterDeclarationNode parameter: return await ParameterAsync(parameter);
                case ConstructorDeclarationNode or MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode
                    or FunctionDeclarationNode or FunctionExpressionNode or ArrowFunctionNode: return await FunctionAsync(node);
                default: return await VisitEachChildAsync(node);
            }
        }
        finally { unusedResult = unused; }
    }

    private (bool Any, bool Marker) Facts(SyntaxNode node)
    {
        if (facts.TryGetValue(node, out var result)) return result;
        var pending = new Stack<(SyntaxNode Node, bool Visited)>();
        pending.Push((node, false));
        while (pending.TryPop(out var item))
        {
            Cancellation.ThrowIfCancellationRequested();
            if (facts.ContainsKey(item.Node)) continue;
            if (!item.Visited)
            {
                pending.Push((item.Node, true));
                for (int i = 0; i < item.Node.ChildCount; i++) pending.Push((item.Node.GetChild(i), false));
                continue;
            }
            bool any = item.Node is SpreadAssignmentNode, marker = any;
            for (int i = 0; i < item.Node.ChildCount; i++)
            {
                var child = item.Node.GetChild(i);
                var childFacts = facts[child];
                any |= childFacts.Any;
                if (child is not (ObjectLiteralExpressionNode or VariableDeclarationListNode or CatchClauseNode or IFunctionSignature)) marker |= childFacts.Marker;
            }
            if (item.Node is BindingPatternNode { Kind: K.ObjectBindingPattern, Elements: { } elements }
                && elements.Any(n => n is BindingElementNode { DotDotDotToken: not null })) any = marker = true;
            if (item.Node is BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken, Left: ObjectLiteralExpressionNode or ArrayLiteralExpressionNode } assignment
                && DestructuringFlattener.ContainsObjectRest(assignment.Left!)) marker = true;
            facts[item.Node] = (any, marker);
        }
        return facts[node];
    }

    private async ValueTask<SyntaxNode> ParameterAsync(ParameterDeclarationNode node)
    {
        bool following = followingParameters?.Contains(node) == true;
        if (!following && !Facts(node).Marker) return (await VisitEachChildAsync(node))!;
        var updated = Context.Clone(node);
        updated.Modifiers = null;
        updated.QuestionToken = null;
        updated.Type = null;
        updated.Name = !following || node.Name is BindingPatternNode ? Context.NewGeneratedNameForNode(node) : node.Name;
        updated.Initializer = following ? null : await VisitAsync(node.Initializer);
        return updated;
    }

    private async ValueTask<SyntaxNode> FunctionAsync(SyntaxNode node)
    {
        var saved = followingParameters;
        followingParameters = null;
        foreach (var parameter in ((IFunctionSignature)node).Parameters ?? new([]))
            if (followingParameters is not null) followingParameters.Add(parameter);
            else if (Facts(parameter).Marker) followingParameters = [];
        try
        {
            var name = node is ArrowFunctionNode or ConstructorDeclarationNode ? null : await VisitAsync(node.DeclarationName);
            var parameters = await VisitListAsync(((IFunctionSignature)node).Parameters);
            var body = await FunctionBodyAsync(node);
            if (name == node.DeclarationName && parameters == ((IFunctionSignature)node).Parameters && body == DecoratorSyntax.Body(node)) return node;
            var updated = Context.Clone(node);
            var signature = (IFunctionSignature)updated;
            signature.TypeParameters = null;
            signature.Type = null;
            signature.Parameters = parameters;
            ((IFullSignatureNode)updated).FullSignature = null;
            switch (updated)
            {
                case ConstructorDeclarationNode n: n.Body = body; break;
                case MethodDeclarationNode n: n.Name = name; n.Body = body; break;
                case GetAccessorDeclarationNode n: n.Name = name; n.Body = body; break;
                case SetAccessorDeclarationNode n: n.Name = name; n.Body = body; break;
                case FunctionDeclarationNode n: n.Name = (IdentifierNode?)name; n.Body = body; break;
                case FunctionExpressionNode n: n.Name = (IdentifierNode?)name; n.Body = body; break;
                case ArrowFunctionNode n: n.Body = body; break;
            }
            return updated;
        }
        finally { followingParameters = saved; }
    }

    private async ValueTask<SyntaxNode?> FunctionBodyAsync(SyntaxNode node)
    {
        Context.StartVariableEnvironment();
        var body = await VisitAsync(DecoratorSyntax.Body(node));
        var bodyDeclarations = Context.EndVariableEnvironment();
        Context.StartVariableEnvironment();
        var assignments = await ParameterAssignmentsAsync(node);
        var extras = Context.MergeEnvironment(new(bodyDeclarations.ToArray()), Context.EndVariableEnvironment())!;
        if (assignments.Count == 0 && extras.Count == 0) return body;
        body ??= F.NewBlock(new([]), true);
        BlockNode block;
        SyntaxNode[] suffix;
        List<SyntaxNode> prefix = [];
        if (body is BlockNode originalBlock)
        {
            block = originalBlock;
            int offset = 0;
            bool custom = false;
            foreach (var statement in block.Statements ?? new([]))
            {
                if (!custom && statement is ExpressionStatementNode { Expression: StringLiteralNode }) prefix.Add(statement);
                else if ((Context.GetFlags(statement) & EmitFlags.CustomPrologue) != 0) { custom = true; prefix.Add(statement); }
                else break;
                offset++;
            }
            suffix = block.Statements?.Skip(offset).ToArray() ?? [];
        }
        else
        {
            suffix = [EmitContext.CopyRange(F.NewReturnStatement(body), body)];
            block = F.NewBlock(new([], body.Pos, body.End), true);
        }
        var updated = Context.Clone(block);
        updated.Statements = new([.. prefix, .. extras, .. assignments, .. suffix], block.Statements!.Pos, block.Statements.End);
        return updated;
    }

    private async ValueTask<List<SyntaxNode>> ParameterAssignmentsAsync(SyntaxNode node)
    {
        List<SyntaxNode> statements = [];
        bool precedingRest = false;
        foreach (ParameterDeclarationNode parameter in ((IFunctionSignature)node).Parameters ?? new([]))
        {
            SyntaxNode? statement = null;
            if (precedingRest)
            {
                if (parameter.Name is BindingPatternNode pattern)
                {
                    if (pattern.Elements is { Count: > 0 })
                        statement = BindingStatement(await Flattener(FlattenLevel.All).BindingAsync(parameter, Context.NewGeneratedNameForNode(parameter), false, false));
                    else if (parameter.Initializer is not null)
                        statement = F.NewExpressionStatement(Context.Binary(Context.NewGeneratedNameForNode(parameter), K.EqualsToken, (await VisitAsync(parameter.Initializer))!));
                }
                else if (parameter.Initializer is not null)
                {
                    var name = Context.Clone(parameter.Name!);
                    Context.AddFlags(name, EmitFlags.NoSourceMap);
                    var initializer = (await VisitAsync(parameter.Initializer))!;
                    Context.AddFlags(initializer, EmitFlags.NoSourceMap | EmitFlags.NoComments);
                    var assignment = EmitContext.CopyRange(Context.Binary(name, K.EqualsToken, initializer), parameter);
                    Context.AddFlags(assignment, EmitFlags.NoComments);
                    var block = EmitContext.CopyRange(F.NewBlock(new([F.NewExpressionStatement(assignment)]), false), parameter);
                    Context.AddFlags(block, EmitFlags.SingleLine | EmitFlags.NoTrailingSourceMap | EmitFlags.NoTokenSourceMaps | EmitFlags.NoComments);
                    statement = EmitContext.CopyRange(F.NewIfStatement(Context.Binary(Context.Clone(name), K.EqualsEqualsEqualsToken, Context.VoidZero()), block, null), parameter);
                    Context.AddFlags(statement, EmitFlags.NoTokenSourceMaps | EmitFlags.NoTrailingSourceMap | EmitFlags.NoComments | EmitFlags.StartOnNewLine);
                }
            }
            else if (Facts(parameter).Marker)
            {
                precedingRest = true;
                statement = BindingStatement(await Flattener().BindingAsync(parameter, Context.NewGeneratedNameForNode(parameter), false, true));
            }
            if (statement is not null)
            {
                Context.AddFlags(statement, EmitFlags.CustomPrologue);
                statements.Add(statement);
            }
        }
        return statements;
    }

    private async ValueTask<SyntaxNode> CatchAsync(CatchClauseNode node)
    {
        if (node.VariableDeclaration is not VariableDeclarationNode { Name: BindingPatternNode pattern } variable || !Facts(pattern).Marker)
            return (await VisitEachChildAsync(node))!;
        var name = Context.NewGeneratedNameForNode(pattern);
        var declaration = Context.Clone(variable);
        declaration.ExclamationToken = null;
        declaration.Type = null;
        declaration.Initializer = name;
        var bindings = BindingStatement(await Flattener().BindingAsync(declaration, null, false, false));
        var block = (BlockNode)(await VisitAsync(node.Block))!;
        if (bindings is not null)
        {
            block = Context.Clone(block);
            block.Statements = new([bindings, .. block.Statements ?? new([])], block.Statements!.Pos, block.Statements.End);
        }
        declaration = Context.Clone(variable);
        declaration.Name = name;
        declaration.ExclamationToken = null;
        declaration.Type = null;
        declaration.Initializer = null;
        var result = Context.Clone(node);
        result.VariableDeclaration = declaration;
        result.Block = block;
        return result;
    }

    private async ValueTask<SyntaxNode> ForOfAsync(ForInOrOfStatementNode node)
    {
        var initializer = TransformSyntax.SkipParentheses(node.Initializer!);
        bool pattern = initializer is ObjectLiteralExpressionNode or ArrayLiteralExpressionNode;
        if (!(Facts(node.Initializer!).Marker || pattern && DestructuringFlattener.ContainsObjectRest(initializer))
            || initializer is not VariableDeclarationListNode && !pattern) return (await VisitEachChildAsync(node))!;
        var temporary = Context.NewTempVariable();
        SyntaxNode binding;
        if (initializer is VariableDeclarationListNode list)
        {
            var declaration = Context.Clone((VariableDeclarationNode)list.Declarations![0]);
            declaration.ExclamationToken = null;
            declaration.Type = null;
            declaration.Initializer = temporary;
            var updatedList = Context.Clone(list);
            updatedList.Declarations = new([declaration]);
            binding = EmitContext.CopyRange(F.NewVariableStatement(null, updatedList), initializer);
        }
        else binding = EmitContext.CopyRange(F.NewExpressionStatement(EmitContext.CopyRange(Context.Binary(initializer, K.EqualsToken, temporary), initializer)), initializer);
        List<SyntaxNode> statements = [];
        if (await VisitAsync(binding) is { } visited) statements.Add(visited);
        var originalBody = node.Statement!;
        var originalStatements = (originalBody as BlockNode)?.Statements;
        foreach (var statement in originalStatements ?? new([originalBody]))
            if (await VisitEachChildAsync(statement) is { } rewritten) statements.Add(rewritten);
        var declarations = EmitContext.CopyRange(F.NewVariableDeclarationList(new([F.NewVariableDeclaration(temporary, null, null, null)]), NodeFlags.Let), node.Initializer!);
        var result = Context.Clone(node);
        result.Initializer = declarations;
        result.Expression = await VisitEachChildAsync(node.Expression!);
        result.Statement = EmitContext.CopyRange(F.NewBlock(new(statements.ToArray(), originalStatements?.Pos ?? originalBody.Pos, originalStatements?.End ?? originalBody.End), true), originalBody);
        return result;
    }

    private async ValueTask<SyntaxNode> BinaryAsync(BinaryExpressionNode node, bool unused)
    {
        if (node.OperatorToken!.Kind == K.EqualsToken && node.Left is ObjectLiteralExpressionNode or ArrayLiteralExpressionNode
            && DestructuringFlattener.ContainsObjectRest(node.Left!)) return await Flattener().AssignmentAsync(node, !unused);
        if (node.OperatorToken.Kind == K.CommaToken)
        {
            unusedResult = true;
            var left = await VisitAsync(node.Left);
            unusedResult = unused;
            var right = await VisitAsync(node.Right);
            if (left == node.Left && right == node.Right && node.Modifiers is null && node.Type is null) return node;
            var updated = Context.Clone(node);
            updated.Modifiers = null;
            updated.Type = null;
            updated.Left = left;
            updated.Right = right;
            return updated;
        }
        return (await VisitEachChildAsync(node))!;
    }

    private async ValueTask<SyntaxNode> ObjectAsync(ObjectLiteralExpressionNode node)
    {
        if (!Facts(node).Marker) return (await VisitEachChildAsync(node))!;
        List<SyntaxNode> objects = [], properties = [];
        foreach (var property in node.Properties ?? new([]))
        {
            if (property is SpreadAssignmentNode spread)
            {
                Flush();
                objects.Add((await VisitAsync(spread.Expression))!);
            }
            else properties.Add(property is PropertyAssignmentNode assignment
                ? F.NewPropertyAssignment(null, assignment.Name, null, null, await VisitAsync(assignment.Initializer)) : (await VisitAsync(property))!);
        }
        Flush();
        if (objects.Count == 0) return node;
        if (objects[0] is not ObjectLiteralExpressionNode) objects.Insert(0, F.NewObjectLiteralExpression(new([]), false));
        var result = objects[0];
        if (objects.Count == 1) return Context.MethodCall(F.NewIdentifier("Object"u8), "assign"u8, [result]);
        foreach (var item in objects.Skip(1)) result = Context.MethodCall(F.NewIdentifier("Object"u8), "assign"u8, [result, item]);
        return result;

        void Flush()
        {
            if (properties.Count == 0) return;
            objects.Add(F.NewObjectLiteralExpression(new(properties.ToArray()), false));
            properties.Clear();
        }
    }

    private SyntaxNode? BindingStatement(SyntaxNode? declarations) => declarations is null ? null
        : F.NewVariableStatement(null, F.NewVariableDeclarationList(new(declarations is SyntaxListNode list ? list.Children : [declarations]), NodeFlags.None));
    private DestructuringFlattener Flattener(FlattenLevel level = FlattenLevel.ObjectRest) => new(Context, VisitAsync, cancellation: Cancellation, level: level);
}

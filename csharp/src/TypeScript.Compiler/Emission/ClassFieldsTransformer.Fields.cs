using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class ClassFieldsTransformer
{
    private static bool Inlineable(SyntaxNode node) => node is not IdentifierNode && TransformSyntax.SimpleCopiable(node);
    private bool ParameterProperty(SyntaxNode node) => Context.MostOriginal(node) is ParameterDeclarationNode parameter
        && parameter.Parent is ConstructorDeclarationNode && parameter.Modifiers?.Any(m => m.Kind is K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.ReadonlyKeyword) == true;

    private async ValueTask<SyntaxNode?> PropertyAsync(PropertyDeclarationNode node)
    {
        if (AutoAccessor(node) && (LowerAutoAccessors || LowerMember(node) && Static(node))) return await AutoAccessorAsync(node);
        if (node.Name is PrivateIdentifierNode)
        {
            if (LowerMember(node))
            {
                var info = Private(node.Name)!;
                if (!info.Valid) return node;
                if (info.Static && !LowerPrivate && await PropertyExpressionAsync(node, F.NewKeywordExpression(K.ThisKeyword)) is { } init)
                    return F.NewClassStaticBlockDeclaration(null, F.NewBlock(new([PropertyStatement(init, node)]), true));
                return null;
            }
            if (!UseDefine && !Static(node) && environment?.HoistInitializers == true)
            {
                var retained = Context.Clone(node);
                retained.Initializer = null; retained.Type = null; retained.PostfixToken = null;
                return retained;
            }
        }
        if (node.Name is not PrivateIdentifierNode && LowerInitializers && !SemanticSyntax.HasModifier(node, K.AccessorKeyword))
        {
            if (await PropertyNameExpressionAsync(node.Name!, node.Initializer is not null || UseDefine) is { } expression)
            {
                Stack<SyntaxNode> pending = new([expression]);
                while (pending.TryPop(out var part))
                    if (part is BinaryExpressionNode { OperatorToken.Kind: K.CommaToken } comma) { pending.Push(comma.Right!); pending.Push(comma.Left!); }
                    else environment!.Pending.Add(part);
            }
            if (Static(node) && !LowerPrivate && await PropertyExpressionAsync(node, F.NewKeywordExpression(K.ThisKeyword)) is { } initializer)
            {
                var statement = PropertyStatement(initializer, node);
                Context.AddFlags(statement, EmitFlags.NoComments);
                var block = F.NewClassStaticBlockDeclaration(null, F.NewBlock(new([statement]), false));
                Context.SetOriginal(block, node);
                Context.SetCommentRange(block, new(node.Pos, node.End));
                return block;
            }
            return null;
        }
        return await VisitEachChildAsync(node);
    }

    private async ValueTask<SyntaxNode?> PropertyNameExpressionAsync(SyntaxNode name, bool hoist)
    {
        if (name is not ComputedPropertyNameNode computed) return null;
        var saved = (environment, computedName);
        computedName = true;
        if (environment?.Previous is { } previous) environment = previous;
        SyntaxNode expression;
        try { expression = (await VisitAsync(computed.Expression))!; }
        finally { (environment, computedName) = saved; }
        var inner = TransformSyntax.SkipPartials(expression);
        bool transformed = ComputedNameCache(computed) is not null || inner is BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken, Left: IdentifierNode generated }
            && Context.GetAutoGenerateInfo(generated) is not null;
        if (!transformed && !Inlineable(inner) && hoist)
        {
            var temp = Context.NewGeneratedNameForNode(name);
            if (iteration && environment?.Node is ClassExpressionNode) Context.AddLexicalDeclaration(temp);
            else Context.AddVariableDeclaration(temp);
            return Assign(temp, expression);
        }
        return Inlineable(inner) || inner is IdentifierNode ? null : expression;
    }

    private async ValueTask<SyntaxNode?> PropertyExpressionAsync(PropertyDeclarationNode node, SyntaxNode receiver)
    {
        var savedElement = classElement;
        var savedCurrent = current;
        current = node;
        try
        {
            if (Static(node)) classElement = node;
            if (named.Applies(node) && NamedEvaluation.SkipOuter(node.Initializer!) is ClassExpressionNode anonymous && NeedsAssignedName(anonymous))
                node = (PropertyDeclarationNode)named.Transform(node);
            SyntaxNode name = node.Name!;
            if (AutoAccessor(node)) name = AccessorStorage(name);
            else if (name is ComputedPropertyNameNode computed && !Inlineable(computed.Expression!))
            {
                var updated = Context.Clone(computed);
                updated.Expression = Context.NewGeneratedNameForNode(computed);
                name = updated;
            }
            if (name is PrivateIdentifierNode && LowerMember(node) && Private(name) is { } info)
            {
                if (info.Kind != "f"u8) return null;
                var value = await VisitAsync(node.Initializer) ?? Context.VoidZero();
                SyntaxNode init = info.Static ? Assign(info.Storage!, F.NewObjectLiteralExpression(new([Property("value"u8, value)]), false))
                    : Context.MethodCall(info.Brand!, "set"u8, [receiver, value]);
                return StaticMetadata(init, node);
            }
            if ((name is PrivateIdentifierNode || Static(node)) && node.Initializer is null
                || SemanticSyntax.HasModifier(Context.MostOriginal(node), K.AbstractKeyword)) return null;
            var initializer = await VisitAsync(node.Initializer);
            if (ParameterProperty(node) && name is IdentifierNode identifier)
            {
                var local = Context.Clone(identifier);
                initializer = initializer is null ? local : Context.InlineExpressions([initializer, local]);
                name = Context.Clone(name);
                Context.AddFlags(name, EmitFlags.NoComments | EmitFlags.NoSourceMap);
                Context.SetSourceMapRange(local, Context.GetSourceMapRange(Context.MostOriginal(node).DeclarationName!));
                Context.AddFlags(local, EmitFlags.NoComments);
            }
            initializer ??= Context.VoidZero();
            SyntaxNode result;
            if (!UseDefine || name is PrivateIdentifierNode)
            {
                var access = MemberAccess(receiver, name);
                Context.AddFlags(access, EmitFlags.NoLeadingComments);
                result = Assign(access, initializer);
            }
            else
            {
                var key = name is ComputedPropertyNameNode computedName ? computedName.Expression! : name is IdentifierNode id ? F.NewStringLiteral(id.Text, TokenFlags.None) : name;
                var descriptor = F.NewObjectLiteralExpression(new([
                    Property("enumerable"u8, F.NewKeywordExpression(K.TrueKeyword)), Property("configurable"u8, F.NewKeywordExpression(K.TrueKeyword)),
                    Property("writable"u8, F.NewKeywordExpression(K.TrueKeyword)), Property("value"u8, initializer)]), true);
                result = Context.MethodCall(F.NewIdentifier("Object"u8), "defineProperty"u8, [receiver, key, descriptor]);
            }
            return StaticMetadata(result, node);
        }
        finally { classElement = savedElement; current = savedCurrent; }
    }

    private SyntaxNode StaticMetadata(SyntaxNode expression, PropertyDeclarationNode node)
    {
        if (Static(node))
        {
            Context.AddFlags(expression, EmitFlags.NoLexicalThis);
            if (environment is { Constructor: not null } or { HoistInitializers: true } or { Decorated: true })
            {
                Context.SetOriginal(expression, node);
                Context.SetSourceMapRange(expression, Context.GetSourceMapRange(node.Name!));
            }
        }
        return expression;
    }

    private PropertyAssignmentNode Property(Utf8String name, SyntaxNode value) => F.NewPropertyAssignment(null, F.NewIdentifier(name), null, null, value);
    private SyntaxNode MemberAccess(SyntaxNode receiver, SyntaxNode name)
    {
        if (name is ComputedPropertyNameNode computed) return EmitContext.CopyRange(F.NewElementAccessExpression(receiver, null, computed.Expression, NodeFlags.None), name);
        SyntaxNode access = name is IdentifierNode or PrivateIdentifierNode ? F.NewPropertyAccessExpression(receiver, null, name, NodeFlags.None)
            : F.NewElementAccessExpression(receiver, null, name, NodeFlags.None);
        Context.SetCommentRange(access, new(name.Pos, name.End));
        Context.SetSourceMapRange(access, new(name.Pos, name.End));
        Context.AddFlags(access, EmitFlags.NoNestedSourceMaps);
        return access;
    }

    private ExpressionStatementNode PropertyStatement(SyntaxNode expression, SyntaxNode property)
    {
        var statement = F.NewExpressionStatement(expression);
        Context.SetOriginal(statement, property);
        Context.AddFlags(statement, Context.GetFlags(property) & EmitFlags.NoComments);
        Context.SetCommentRange(statement, new(property.Pos, property.End));
        Context.SetSourceMapRange(statement, ParameterProperty(property) ? Context.GetSourceMapRange(Context.MostOriginal(property)) : PastModifiers(property));
        if (ParameterProperty(property) || SemanticSyntax.HasModifier(Context.MostOriginal(property), K.AccessorKeyword)) Context.AddFlags(statement, EmitFlags.NoComments);
        Context.SetLeadingComments(expression, []);
        Context.SetTrailingComments(expression, []);
        return statement;
    }

    private async ValueTask<SyntaxNode?> ConstructorAsync(ConstructorDeclarationNode? node, SyntaxNode container)
    {
        if (environment?.HoistInitializers != true) return node is null ? null : await VisitEachChildAsync(node);
        var saved = (constructorSuper, constructorInitializers, classElement, iteration);
        constructorSuper = null;
        constructorInitializers = null;
        classElement = node;
        iteration = false;
        try
        {
            var parameters = node is null ? new([]) : await VisitListAsync(node.Parameters);
            Context.StartVariableEnvironment();
            List<SyntaxNode> initializers = [];
            if (LowerPrivate && environment.Instances is { } instances)
                initializers.Add(F.NewExpressionStatement(Context.MethodCall(instances, "add"u8, [F.NewKeywordExpression(K.ThisKeyword)])));
            var properties = Members(container).OfType<PropertyDeclarationNode>().Where(p => !Static(p)).ToArray();
            foreach (var property in properties.Where(ParameterProperty).Concat(properties.Where(p => !ParameterProperty(p) && (UseDefine || p.Initializer is not null || LowerPrivate && (p.Name is PrivateIdentifierNode || AutoAccessor(p))))))
                if (await PropertyExpressionAsync(property, F.NewKeywordExpression(K.ThisKeyword)) is { } expression) initializers.Add(PropertyStatement(expression, property));
            List<SyntaxNode> statements = [];
            var originalBody = node?.Body as BlockNode;
            if (originalBody is not null)
            {
                var input = originalBody.Statements ?? new([]);
                int start = 0;
                while (start < input.Count && input[start] is ExpressionStatementNode { Expression: StringLiteralNode }) statements.Add(input[start++]);
                constructorSuper = FindSuper(input);
                constructorInitializers = initializers;
                if (constructorSuper is null) statements.AddRange(initializers);
                statements.AddRange(await VisitArrayAsync(input.Skip(start).ToArray()));
            }
            else
            {
                if (Heritage(container)?.FirstOrDefault(h => h is HeritageClauseNode { Token: K.ExtendsKeyword }) is HeritageClauseNode { Types.Count: > 0 } heritage
                    && heritage.Types[0] is ExpressionWithTypeArgumentsNode { Expression: { } baseType } && NamedEvaluation.SkipOuter(baseType).Kind != K.NullKeyword)
                    statements.Add(F.NewExpressionStatement(F.NewCallExpression(F.NewKeywordExpression(K.SuperKeyword), null, null,
                        new([F.NewSpreadElement(F.NewIdentifier("arguments"u8))]), NodeFlags.None)));
                statements.AddRange(initializers);
            }
            var merged = Context.MergeEnvironment(new(statements.ToArray()), Context.EndVariableEnvironment())!;
            if (merged.Count == 0 && node is null) return null;
            bool multiLine = originalBody is not null && originalBody.Statements?.Count >= merged.Count ? originalBody.MultiLine : merged.Count != 0;
            var body = F.NewBlock(new(merged.ToArray(), originalBody?.Statements?.Pos ?? Members(container).Pos, originalBody?.Statements?.End ?? Members(container).End), multiLine);
            if (originalBody is not null) EmitContext.CopyRange(body, originalBody);
            if (node is null) return EmitContext.CopyRange(F.NewConstructorDeclaration(null, null, parameters, null, null, body), container);
            var result = Context.Clone(node);
            result.Modifiers = null; result.TypeParameters = null; result.Parameters = parameters; result.Type = null; result.FullSignature = null; result.Body = body;
            return result;
        }
        finally { (constructorSuper, constructorInitializers, classElement, iteration) = saved; }
    }

    private static SyntaxNode? FindSuper(NodeList statements)
    {
        Stack<SyntaxNode> pending = new(statements.Reverse());
        while (pending.TryPop(out var statement))
        {
            if (statement is ExpressionStatementNode { Expression: { } expression } && TransformSyntax.SkipParentheses(expression) is CallExpressionNode { Expression.Kind: K.SuperKeyword }) return statement;
            if (statement is TryStatementNode { TryBlock: BlockNode { Statements: { } children } })
                for (int i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
        }
        return null;
    }
}

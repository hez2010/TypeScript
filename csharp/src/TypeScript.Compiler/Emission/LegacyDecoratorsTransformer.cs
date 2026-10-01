using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed class LegacyDecoratorsTransformer(EmitContext context, CompilerOptions options, Checker checker, CancellationToken cancellation = default)
    : SyntaxRewriter(context, cancellation)
{
    private readonly Dictionary<SyntaxNode, IdentifierNode> aliases = [];
    private readonly List<ClassDeclarationNode> enclosingClasses = [];
    private readonly HashSet<SyntaxNode> containingDecorators = [];
    private NodeFactory F => Context.Factory;

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        if (node is SourceFileNode)
        {
            aliases.Clear();
            enclosingClasses.Clear();
            IndexDecorators(node);
            try
            {
                var result = (await VisitEachChildAsync(node))!;
                foreach (var helper in Context.ReadHelpers()) Context.AddHelper(result, helper);
                return result;
            }
            finally { aliases.Clear(); enclosingClasses.Clear(); containingDecorators.Clear(); }
        }
        if (enclosingClasses.Count == 0 && !containingDecorators.Contains(node)) return node;
        switch (node)
        {
            case DecoratorNode: return null;
            case IdentifierNode identifier:
                if (Context.ParseNode(identifier) is IdentifierNode parsed)
                {
                    var declaration = await checker.GetReferencedValueForEmitAsync(parsed, Cancellation);
                    foreach (var container in enclosingClasses)
                        if (aliases.TryGetValue(container, out var alias) && declaration == Context.MostOriginal(container))
                            return alias;
                }
                return node;
            case PropertyAccessExpressionNode access:
                var expression = await VisitAsync(access.Expression);
                if (expression == access.Expression) return node;
                var updatedAccess = Context.Clone(access);
                updatedAccess.Expression = expression;
                return updatedAccess;
            case ClassDeclarationNode declaration: return await ClassAsync(declaration);
            case ClassExpressionNode classExpression:
                var updatedClass = (ClassExpressionNode)(await VisitEachChildAsync(classExpression))!;
                if (updatedClass.TypeParameters is null) return updatedClass;
                updatedClass = Context.Clone(updatedClass);
                updatedClass.TypeParameters = null;
                return updatedClass;
            case ParameterDeclarationNode parameter: return await ParameterAsync(parameter);
            case PropertyDeclarationNode or MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode or ConstructorDeclarationNode:
                return await MemberAsync(node);
            default: return await VisitEachChildAsync(node);
        }
    }

    private void IndexDecorators(SyntaxNode root)
    {
        containingDecorators.Clear();
        var pending = new Stack<(SyntaxNode Node, bool Visited)>();
        pending.Push((root, false));
        while (pending.TryPop(out var item))
        {
            Cancellation.ThrowIfCancellationRequested();
            if (!item.Visited)
            {
                pending.Push((item.Node, true));
                for (int i = item.Node.ChildCount - 1; i >= 0; i--) pending.Push((item.Node.GetChild(i), false));
            }
            else
            {
                bool contains = item.Node is DecoratorNode;
                for (int i = 0; !contains && i < item.Node.ChildCount; i++) contains = containingDecorators.Contains(item.Node.GetChild(i));
                if (contains) containingDecorators.Add(item.Node);
            }
        }
    }

    private static EmitRange PastModifiers(SyntaxNode node)
    {
        if (node is PropertyDeclarationNode or MethodDeclarationNode) return new(node.DeclarationName!.Pos, node.End);
        var last = node.ModifierList?.LastOrDefault();
        if (last is not null && last.End >= 0) return new(last.End, node.End);
        last = node.ModifierList?.LastOrDefault(n => n is DecoratorNode);
        return new(last is not null && last.End >= 0 ? last.End : node.Pos, node.End);
    }

    private async ValueTask<SyntaxNode> ParameterAsync(ParameterDeclarationNode node)
    {
        var name = await VisitAsync(node.Name);
        var initializer = await VisitAsync(node.Initializer);
        var modifiers = node.Modifiers is { Count: > 0 } ? new NodeList([], node.Modifiers.Pos, node.Modifiers.End) : node.Modifiers;
        if (name == node.Name && initializer == node.Initializer && modifiers == node.Modifiers && node.QuestionToken is null && node.Type is null) return node;
        var updated = Context.Clone(node);
        updated.Name = name;
        updated.Initializer = initializer;
        updated.Modifiers = modifiers;
        updated.QuestionToken = null;
        updated.Type = null;
        var range = PastModifiers(node);
        (updated.Pos, updated.End) = (range.Pos, range.End);
        Context.SetCommentRange(updated, new(node.Pos, node.End));
        Context.SetSourceMapRange(updated, range);
        if (updated.Name is not null) Context.SetFlags(updated.Name, EmitFlags.NoTrailingSourceMap);
        return updated;
    }

    private async ValueTask<SyntaxNode?> MemberAsync(SyntaxNode node)
    {
        if (node is PropertyDeclarationNode && ((node.Flags & NodeFlags.Ambient) != 0
            || SemanticSyntax.HasModifier(node, K.DeclareKeyword) || SemanticSyntax.HasModifier(node, K.AbstractKeyword))) return null;
        var modifiers = await VisitListAsync(node.ModifierList);
        var name = node is ConstructorDeclarationNode ? null : await PropertyNameAsync(node);
        SyntaxNode? initializer = null, body = null;
        NodeList? parameters = null;
        if (node is PropertyDeclarationNode property) initializer = await VisitAsync(property.Initializer);
        else
        {
            // Decorator expressions on parameters execute in the containing class's environment.
            parameters = await VisitListAsync(((IFunctionSignature)node).Parameters);
            body = await VisitAsync(DecoratorSyntax.Body(node));
        }
        SyntaxNode updated = node switch
        {
            PropertyDeclarationNode p when modifiers == p.Modifiers && name == p.Name && initializer == p.Initializer && p.PostfixToken is null && p.Type is null => node,
            MethodDeclarationNode m when modifiers == m.Modifiers && name == m.Name && parameters == m.Parameters && body == m.Body && m.PostfixToken is null && m.TypeParameters is null && m.Type is null && m.FullSignature is null => node,
            GetAccessorDeclarationNode g when modifiers == g.Modifiers && name == g.Name && parameters == g.Parameters && body == g.Body && g.TypeParameters is null && g.Type is null && g.FullSignature is null => node,
            SetAccessorDeclarationNode s when modifiers == s.Modifiers && name == s.Name && parameters == s.Parameters && body == s.Body && s.TypeParameters is null && s.Type is null && s.FullSignature is null => node,
            ConstructorDeclarationNode c when modifiers == c.Modifiers && parameters == c.Parameters && body == c.Body && c.TypeParameters is null && c.Type is null && c.FullSignature is null => node,
            _ => Context.Clone(node)
        };
        if (updated == node) return node;
        ((IModifiedNode)updated).Modifiers = modifiers;
        if (updated is PropertyDeclarationNode resultProperty)
        {
            resultProperty.Name = name;
            resultProperty.Initializer = initializer;
            resultProperty.PostfixToken = null;
            resultProperty.Type = null;
        }
        else
        {
            var signature = (IFunctionSignature)updated;
            signature.TypeParameters = null;
            signature.Parameters = parameters;
            signature.Type = null;
            ((IFullSignatureNode)updated).FullSignature = null;
            switch (updated)
            {
                case MethodDeclarationNode m: m.Name = name; m.Body = body; m.PostfixToken = null; break;
                case GetAccessorDeclarationNode g: g.Name = name; g.Body = body; break;
                case SetAccessorDeclarationNode s: s.Name = name; s.Body = body; break;
                case ConstructorDeclarationNode c: c.Body = body; break;
            }
        }
        if (node is not ConstructorDeclarationNode)
        {
            Context.SetCommentRange(updated, new(node.Pos, node.End));
            Context.SetSourceMapRange(updated, PastModifiers(node));
        }
        return updated;
    }

    private async ValueTask<SyntaxNode?> PropertyNameAsync(SyntaxNode member)
    {
        if (member.DeclarationName is ComputedPropertyNameNode computed && DecoratorSyntax.HasDecorators(member))
        {
            var expression = (await VisitAsync(computed.Expression))!;
            var inner = expression;
            while (inner is PartiallyEmittedExpressionNode partial) inner = partial.Expression!;
            if (!Inlineable(inner))
            {
                var temporary = Context.NewGeneratedNameForNode(computed);
                Context.AddVariableDeclaration(temporary);
                var updated = Context.Clone(computed);
                updated.Expression = Assignment(temporary, expression);
                return updated;
            }
        }
        return await VisitAsync(member.DeclarationName);
    }

    private async ValueTask<SyntaxNode> ClassAsync(ClassDeclarationNode node)
    {
        bool classDecorated = DecoratorSyntax.ClassDecorated(true, node);
        if (!classDecorated && !DecoratorSyntax.ChildDecorated(true, node)) return (await VisitEachChildAsync(node))!;
        if (!classDecorated)
        {
            var modifiers = await VisitListAsync(node.Modifiers);
            var heritage = await VisitListAsync(node.HeritageClauses);
            var members = await VisitListAsync(node.Members);
            var transformed = await DecorateMembersAsync(node, members);
            var result = Context.Clone(node);
            result.Modifiers = modifiers;
            result.TypeParameters = null;
            result.HeritageClauses = heritage;
            result.Members = transformed.Members;
            if (result.Name is null && transformed.Statements.Count != 0) result.Name = Context.NewGeneratedNameForNode(node);
            return transformed.Statements.Count == 0 ? result : F.NewSyntaxList([result, .. transformed.Statements]);
        }

        IdentifierNode? alias = null;
        if (await HasSelfReferenceAsync(node))
        {
            alias = Context.NewUniqueName(node.Name is not null && Context.GetAutoGenerateInfo(node.Name) is null ? node.Name.Text : "default"u8);
            Context.AddVariableDeclaration(alias);
            aliases[node] = alias;
            enclosingClasses.Add(node);
        }
        try
        {
            var modifiers = node.Modifiers is { Count: > 0 }
                ? new NodeList(node.Modifiers.Where(n => n.Kind is not (K.Decorator or K.ExportKeyword or K.DefaultKeyword)).ToArray(), node.Modifiers.Pos, node.Modifiers.End) : null;
            var location = PastModifiers(node);
            var declarationName = Context.GetLocalName(node, allowSourceMaps: true);
            var heritage = await VisitListAsync(node.HeritageClauses);
            var members = await VisitListAsync(node.Members);
            var transformed = await DecorateMembersAsync(node, members);
            members = transformed.Members;
            bool staticAlias = options.EmitTargetYear >= 2022 && alias is not null
                && members?.Any(n => n is ClassStaticBlockDeclarationNode || n is PropertyDeclarationNode && DecoratorSyntax.Static(n)) == true;
            if (staticAlias)
            {
                var block = F.NewClassStaticBlockDeclaration(null, F.NewBlock(new([F.NewExpressionStatement(Assignment(alias!, F.NewKeywordExpression(K.ThisKeyword)))]), false));
                members = new([block, .. members!], members!.Pos, members.End);
            }
            var expressionName = node.Name is not null && Context.GetAutoGenerateInfo(node.Name) is not null ? null : node.Name;
            var expression = F.NewClassExpression(modifiers, expressionName, null, heritage, members);
            Context.SetOriginal(expression, node);
            (expression.Pos, expression.End) = (location.Pos, location.End);
            var declaration = F.NewVariableDeclaration(declarationName, null, null, alias is not null && !staticAlias ? Assignment(alias, expression) : expression);
            Context.SetOriginal(declaration, node);
            var statement = F.NewVariableStatement(null, F.NewVariableDeclarationList(new([declaration]), NodeFlags.Let));
            Context.SetOriginal(statement, node);
            (statement.Pos, statement.End) = (location.Pos, location.End);
            Context.SetCommentRange(statement, new(node.Pos, node.End));
            List<SyntaxNode> statements = [statement, .. transformed.Statements];

            // Class decorator expressions use the outer binding even when the class body needs an alias.
            if (alias is not null) enclosingClasses.RemoveAt(enclosingClasses.Count - 1);
            List<SyntaxNode> decorators;
            try { decorators = await DecoratorExpressionsAsync(Decorators(node), ParameterDecorators(DecoratorSyntax.Constructor(node))); }
            finally { if (alias is not null) enclosingClasses.Add(node); }
            if (decorators.Count != 0)
            {
                var local = Context.GetDeclarationName(node, allowSourceMaps: true);
                SyntaxNode decorated = Decorate(decorators, local);
                if (alias is not null) decorated = Assignment(alias, decorated);
                decorated = Assignment(local, decorated);
                Context.SetFlags(decorated, EmitFlags.NoComments);
                Context.SetSourceMapRange(decorated, location);
                var decoration = F.NewExpressionStatement(decorated);
                Context.SetOriginal(decoration, node);
                statements.Add(decoration);
            }
            if (SemanticSyntax.HasModifier(node, K.ExportKeyword))
                statements.Add(SemanticSyntax.HasModifier(node, K.DefaultKeyword)
                    ? F.NewExportAssignment(null, false, null, declarationName)
                    : F.NewExportDeclaration(null, false, F.NewNamedExports(new([F.NewExportSpecifier(false, null, Context.GetDeclarationName(node))])), null, null));
            return statements.Count == 1 ? statements[0] : F.NewSyntaxList(statements.ToArray());
        }
        finally { if (alias is not null) enclosingClasses.RemoveAt(enclosingClasses.Count - 1); }
    }

    private async ValueTask<bool> HasSelfReferenceAsync(ClassDeclarationNode node)
    {
        var pending = new Stack<SyntaxNode>();
        foreach (var member in node.Members ?? new([]))
            for (int i = 0; i < member.ChildCount; i++) pending.Push(member.GetChild(i));
        var original = Context.MostOriginal(node);
        while (pending.TryPop(out var child))
        {
            Cancellation.ThrowIfCancellationRequested();
            if (child is IdentifierNode && Context.ParseNode(child) is IdentifierNode parsed
                && await checker.GetReferencedValueForEmitAsync(parsed, Cancellation) == original) return true;
            if (child is PropertyAccessExpressionNode access) pending.Push(access.Expression!);
            else for (int i = 0; i < child.ChildCount; i++) pending.Push(child.GetChild(i));
        }
        return false;
    }

    private async ValueTask<(NodeList? Members, List<SyntaxNode> Statements)> DecorateMembersAsync(ClassDeclarationNode node, NodeList? members)
    {
        List<SyntaxNode> statements = [];
        bool privateExpression = false;
        foreach (bool isStatic in new[] { false, true })
            foreach (var member in node.Members ?? new([]))
            {
                if (DecoratorSyntax.Static(member) != isStatic
                    || !(DecoratorSyntax.Decorated(true, member, node) || DecoratorSyntax.ChildDecorated(true, member, node))) continue;
                var all = MemberDecorators(member, node);
                privateExpression |= all.Decorators.Concat(all.Parameters.SelectMany(p => p))
                    .Any(d => d.Expression?.DescendantsAndSelf().Any(n => n is PrivateIdentifierNode) == true);
                var expressions = await DecoratorExpressionsAsync(all.Decorators, all.Parameters);
                if (expressions.Count == 0) continue;
                SyntaxNode target = Context.GetDeclarationName(node);
                if (!isStatic) target = F.NewPropertyAccessExpression(target, null, F.NewIdentifier("prototype"u8), NodeFlags.None);
                var name = PropertyExpression(member, (member.Flags & NodeFlags.Ambient) == 0);
                SyntaxNode descriptor = member is PropertyDeclarationNode && !SemanticSyntax.HasModifier(member, K.AccessorKeyword)
                    ? Context.VoidZero() : F.NewKeywordExpression(K.NullKeyword);
                var helper = Decorate(expressions, target, name, descriptor);
                Context.SetFlags(helper, EmitFlags.NoComments);
                Context.SetSourceMapRange(helper, PastModifiers(member));
                statements.Add(F.NewExpressionStatement(helper));
            }
        if (privateExpression)
        {
            members = new([.. members ?? new([]), F.NewClassStaticBlockDeclaration(null, F.NewBlock(new(statements.ToArray()), true))]);
            statements = [];
        }
        return (members, statements);
    }

    private static DecoratorNode[] Decorators(SyntaxNode node) => node.ModifierList?.OfType<DecoratorNode>().ToArray() ?? [];
    private static DecoratorNode[][] ParameterDecorators(SyntaxNode? node)
    {
        var parameters = (node as IFunctionSignature)?.Parameters?.ToArray() ?? [];
        int offset = parameters.Length != 0 && DecoratorSyntax.ThisParameter(parameters[0]) ? 1 : 0;
        return parameters.Skip(offset).Any(DecoratorSyntax.HasDecorators) ? parameters.Skip(offset).Select(Decorators).ToArray() : [];
    }

    private static (DecoratorNode[] Decorators, DecoratorNode[][] Parameters) MemberDecorators(SyntaxNode member, ClassDeclarationNode parent)
    {
        if (member is PropertyDeclarationNode) return (Decorators(member), []);
        if (DecoratorSyntax.Body(member) is null) return ([], []);
        if (member is MethodDeclarationNode) return (Decorators(member), ParameterDecorators(member));
        if (member is GetAccessorDeclarationNode or SetAccessorDeclarationNode)
        {
            var group = DecoratorSyntax.Accessors(parent.Members, member);
            var first = DecoratorSyntax.HasDecorators(group.First) ? group.First : DecoratorSyntax.HasDecorators(group.Second) ? group.Second : null;
            if (first == member) return (Decorators(member), ParameterDecorators(group.Setter));
        }
        return ([], []);
    }

    private bool IsMetadata(DecoratorNode node) => node.Expression is CallExpressionNode { Expression: IdentifierNode name }
        && name.Text == "__metadata"u8 && (Context.GetFlags(name) & EmitFlags.HelperName) != 0;

    private async ValueTask<List<SyntaxNode>> DecoratorExpressionsAsync(DecoratorNode[] decorators, DecoratorNode[][] parameters)
    {
        List<SyntaxNode> expressions = [];
        foreach (var decorator in decorators)
            if (!IsMetadata(decorator)) expressions.Add((await VisitAsync(decorator.Expression))!);
        for (int i = 0; i < parameters.Length; i++)
            foreach (var decorator in parameters[i])
            {
                var helper = Helper(EmitHelpers.Param, [Context.ConstantExpression((double)i)!, (await VisitAsync(decorator.Expression))!]);
                EmitContext.CopyRange(helper, decorator.Expression!);
                Context.SetFlags(helper, EmitFlags.NoComments);
                expressions.Add(helper);
            }
        foreach (var decorator in decorators)
            if (IsMetadata(decorator)) expressions.Add((await VisitAsync(decorator.Expression))!);
        return expressions;
    }

    private SyntaxNode PropertyExpression(SyntaxNode member, bool generateName)
    {
        var name = member.DeclarationName!;
        return name switch
        {
            PrivateIdentifierNode => F.NewIdentifier(default),
            ComputedPropertyNameNode computed => generateName && !Inlineable(computed.Expression!) ? Context.NewGeneratedNameForNode(name) : computed.Expression!,
            IdentifierNode identifier => F.NewStringLiteral(identifier.Text, TokenFlags.None),
            _ => name.DeepClone<SyntaxNode>(F)
        };
    }

    private SyntaxNode Decorate(List<SyntaxNode> decorators, SyntaxNode target, SyntaxNode? name = null, SyntaxNode? descriptor = null)
    {
        List<SyntaxNode> arguments = [F.NewArrayLiteralExpression(new(decorators.ToArray()), true), target];
        if (name is not null) { arguments.Add(name); if (descriptor is not null) arguments.Add(descriptor); }
        return Helper(EmitHelpers.Decorate, arguments.ToArray());
    }

    private CallExpressionNode Helper(EmitHelper helper, SyntaxNode[] arguments)
    {
        Context.RequestHelper(helper);
        var name = F.NewIdentifier(helper.ImportName);
        Context.SetFlags(name, EmitFlags.HelperName);
        return F.NewCallExpression(name, null, null, new(arguments), NodeFlags.None);
    }
    private SyntaxNode Assignment(SyntaxNode left, SyntaxNode right) => Context.Binary(left, K.EqualsToken, right);
    private static bool Inlineable(SyntaxNode node) => node is not IdentifierNode && TransformSyntax.SimpleCopiable(node);
}

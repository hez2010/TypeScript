using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class EsDecoratorsTransformer
{
    private sealed class MemberResult(NodeList? modifiers)
    {
        internal readonly NodeList? Modifiers = modifiers;
        internal SyntaxNode? Name;
        internal IdentifierNode? Initializers, ExtraInitializers, Descriptor, This;
    }
    private async ValueTask<MemberResult> PrepareMemberAsync(SyntaxNode member)
    {
        var info = currentClass;
        var result = new MemberResult(Modifiers(member.ModifierList));
        if (info is not null)
        {
            var saved = classThis;
            List<SyntaxNode> decorators;
            classThis = null;
            try { decorators = await DecoratorsAsync(member); }
            finally { classThis = saved; }
            if (decorators.Count != 0)
            {
                var data = new MemberInfo(HelperName(member, "decorators"u8));
                info.Members.Add((member, data));
                pending.Add(Assign(data.Decorators, Array(decorators)));
                Utf8String kind = member switch
                {
                    GetAccessorDeclarationNode => "getter"u8, SetAccessorDeclarationNode => "setter"u8,
                    MethodDeclarationNode => "method"u8, _ => AutoAccessor(member) ? "accessor"u8 : "field"u8
                };
                bool computed = member.DeclarationName is not (IdentifierNode or PrivateIdentifierNode);
                SyntaxNode contextName;
                var name = member.DeclarationName!;
                if (!computed) contextName = name;
                else if (name is StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode) contextName = Context.StringLiteralFromNode(name);
                else if (name is ComputedPropertyNameNode { Expression: StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode } literal)
                    contextName = Context.StringLiteralFromNode(literal.Expression!);
                else
                {
                    Enter(ScopeKind.Name);
                    try { (contextName, result.Name) = await ReferencedNameAsync(name); }
                    finally { Exit(); }
                }
                var context = ElementContext(member, kind, computed, contextName, info.Metadata);
                SyntaxNode descriptor = Null(), ctor, initializers, extras;
                if (Method(member))
                {
                    extras = (Static(member) ? info.StaticExtra : info.InstanceExtra)!;
                    initializers = Null(); ctor = This();
                    if (name is PrivateIdentifierNode)
                    {
                        var value = await DescriptorAsync(member, Modifiers(result.Modifiers, m => m.Kind == K.AsyncKeyword));
                        result.Descriptor = data.Descriptor = HelperName(member, "descriptor"u8);
                        descriptor = Assign(data.Descriptor, value);
                    }
                }
                else
                {
                    result.Initializers = data.Initializers = HelperName(member, "initializers"u8);
                    result.ExtraInitializers = data.ExtraInitializers = HelperName(member, "extraInitializers"u8);
                    if (Static(member)) result.This = info.This;
                    ctor = AutoAccessor(member) ? This() : Null();
                    initializers = data.Initializers; extras = data.ExtraInitializers;
                    if (name is PrivateIdentifierNode && AutoAccessor(member))
                    {
                        var value = await DescriptorAsync(member, null);
                        result.Descriptor = data.Descriptor = HelperName(member, "descriptor"u8);
                        descriptor = Assign(data.Descriptor, value);
                    }
                }
                var statement = F.NewExpressionStatement(Decorate(ctor, descriptor, data.Decorators, context, initializers, extras));
                Context.SetSourceMapRange(statement, PastDecorators(member));
                info.Decorations[(Method(member) || AutoAccessor(member) ? 0 : 2) + (Static(member) ? 0 : 1)].Add(statement);
            }
        }
        if (result.Name is null)
        {
            Enter(ScopeKind.Name);
            try { result.Name = member.DeclarationName is ComputedPropertyNameNode computed ? await ComputedNameAsync(computed) : await VisitAsync(member.DeclarationName); }
            finally { Exit(); }
        }
        if (info is not null && result.Modifiers?.Count is not > 0 && member is MethodDeclarationNode or PropertyDeclarationNode && result.Name is not null)
            Context.SetFlags(result.Name, EmitFlags.NoLeadingComments);
        return result;
    }

    private async ValueTask<SyntaxNode?> MemberAsync(SyntaxNode node)
    {
        if (node is PropertyDeclarationNode) node = AssignName(node);
        Enter(ScopeKind.Element, element: node);
        try
        {
            if (node is ConstructorDeclarationNode constructor) return await ConstructorAsync(constructor);
            if (node is ClassStaticBlockDeclarationNode block) return await StaticBlockAsync(block);
            var prepared = await PrepareMemberAsync(node);
            if (node is PropertyDeclarationNode property) return await PropertyAsync(property, prepared);
            if (prepared.Descriptor is not null) return Finish(Forwarder(node.Kind, prepared.Modifiers, prepared.Name!, prepared.Descriptor), node);
            var parameters = await VisitListAsync(((IFunctionSignature)node).Parameters);
            var body = await VisitAsync(DecoratorSyntax.Body(node));
            var result = Context.Clone(node);
            ((IModifiedNode)result).Modifiers = prepared.Modifiers;
            var signature = (IFunctionSignature)result;
            signature.TypeParameters = null; signature.Parameters = parameters; signature.Type = null;
            ((IFullSignatureNode)result).FullSignature = null;
            switch (result)
            {
                case MethodDeclarationNode method: method.Name = prepared.Name; method.Body = body; method.PostfixToken = null; break;
                case GetAccessorDeclarationNode getter: getter.Name = prepared.Name; getter.Body = body; break;
                case SetAccessorDeclarationNode setter: setter.Name = prepared.Name; setter.Body = body; break;
            }
            return Finish(result, node);
        }
        finally { Exit(); }
    }

    private async ValueTask<SyntaxNode> PropertyAsync(PropertyDeclarationNode node, MemberResult prepared)
    {
        Context.StartVariableEnvironment();
        var initializer = await VisitAsync(node.Initializer);
        if (prepared.Initializers is not null) initializer = Run(prepared.This ?? This(), prepared.Initializers, initializer ?? Context.VoidZero());
        if (Static(node) && currentClass is not null && initializer is not null) currentClass.HasStaticInitializers = true;
        var declarations = Context.EndVariableEnvironment();
        if (declarations.Count != 0) initializer = Iife([.. declarations, F.NewReturnStatement(initializer)]);
        if (currentClass is { } info)
        {
            var initializers = Static(node) ? info.PendingStatic : info.PendingInstance;
            initializer = Prepend(initializers, initializer);
            if (prepared.ExtraInitializers is not null) initializers.Add(Run(Static(node) ? info.This ?? This() : This(), prepared.ExtraInitializers));
        }
        if (AutoAccessor(node) && prepared.Descriptor is not null)
        {
            var modifiers = Modifiers(prepared.Modifiers, m => m.Kind != K.AccessorKeyword);
            var backing = Context.Clone(node);
            backing.Modifiers = modifiers; backing.Name = Context.NewGeneratedPrivateNameForNode(node.Name!, new(Suffix: "_accessor_storage"u8));
            backing.PostfixToken = null; backing.Type = null; backing.Initializer = initializer;
            Context.SetFlags(backing, EmitFlags.NoComments);
            Context.SetSourceMapRange(backing, Context.GetSourceMapRange(node));
            Context.SetSourceMapRange(backing.Name!, Context.GetSourceMapRange(node.Name!));
            var getter = Forwarder(K.GetAccessor, modifiers, prepared.Name!, prepared.Descriptor);
            Context.SetOriginal(getter, node);
            Context.SetCommentRange(getter, Context.GetCommentRange(node));
            Context.SetSourceMapRange(getter, Context.GetSourceMapRange(node));
            var setter = Forwarder(K.SetAccessor, modifiers, prepared.Name!, prepared.Descriptor);
            Context.SetOriginal(setter, node);
            Context.SetFlags(setter, EmitFlags.NoComments);
            Context.SetSourceMapRange(setter, Context.GetSourceMapRange(node));
            return F.NewSyntaxList([backing, getter, setter]);
        }
        if (node.Modifiers == prepared.Modifiers && node.Name == prepared.Name && node.Initializer == initializer && node.PostfixToken is null && node.Type is null) return node;
        var result = Context.Clone(node);
        result.Modifiers = prepared.Modifiers; result.Name = prepared.Name; result.PostfixToken = null; result.Type = null; result.Initializer = initializer;
        return Finish(result, node);
    }

    private async ValueTask<SyntaxNode> StaticBlockAsync(ClassStaticBlockDeclarationNode node)
    {
        if (named.IsNameBlock(node))
        {
            var updated = (await VisitEachChildAsync(node))!;
            if (Context.GetAssignedName(node) is { } assignedName && updated != node) Context.SetAssignedName(updated, assignedName);
            return updated;
        }
        if (named.IsClassThisBlock(node))
        {
            var saved = classThis; classThis = null;
            try { return (await VisitEachChildAsync(node))!; }
            finally { classThis = saved; }
        }
        Context.StartVariableEnvironment();
        var result = (ClassStaticBlockDeclarationNode)(await VisitEachChildAsync(node))!;
        var declarations = Context.EndVariableEnvironment();
        if (declarations.Count != 0)
        {
            var body = (BlockNode)result.Body!;
            result = StaticBlock([.. declarations, .. body.Statements ?? new([])], body.MultiLine);
        }
        if (currentClass is { } info)
        {
            info.HasStaticInitializers = true;
            if (info.PendingStatic.Count != 0)
            {
                List<SyntaxNode> initializers = [];
                foreach (var initializer in info.PendingStatic)
                {
                    var statement = F.NewExpressionStatement(initializer);
                    Context.SetSourceMapRange(statement, Context.GetSourceMapRange(initializer));
                    initializers.Add(statement);
                }
                info.PendingStatic.Clear();
                return F.NewSyntaxList([StaticBlock(initializers), result]);
            }
        }
        return result;
    }

    private SyntaxNode? constructorSuper;
    private SyntaxNode[]? constructorInitializers;
    private SyntaxNode[] PrepareConstructor(ClassInfo info)
    {
        if (info.PendingInstance.Count == 0) return [];
        var statement = F.NewExpressionStatement(Context.InlineExpressions(info.PendingInstance.ToArray()));
        info.PendingInstance.Clear();
        return [statement];
    }
    private async ValueTask<SyntaxNode> ConstructorAsync(ConstructorDeclarationNode node)
    {
        var parameters = await VisitListAsync(node.Parameters);
        SyntaxNode? body = null;
        var saved = (constructorSuper, constructorInitializers);
        constructorSuper = null; constructorInitializers = null;
        try
        {
            if (node.Body is BlockNode block && currentClass is not null)
            {
                var initializers = PrepareConstructor(currentClass);
                if (initializers.Length != 0)
                {
                    var input = block.Statements ?? new([]);
                    int offset = 0;
                    while (offset < input.Count && input[offset] is ExpressionStatementNode { Expression: StringLiteralNode }) offset++;
                    constructorSuper = FindSuper(input.Skip(offset));
                    constructorInitializers = initializers;
                    List<SyntaxNode> statements = input.Take(offset).ToList();
                    if (constructorSuper is null) statements.AddRange(initializers);
                    statements.AddRange(await VisitArrayAsync(input.Skip(offset).ToArray()));
                    body = Original(F.NewBlock(new(statements.ToArray()), true), block);
                }
            }
            body ??= await VisitAsync(node.Body);
            var result = Context.Clone(node);
            result.Modifiers = Modifiers(node.Modifiers); result.Parameters = parameters; result.TypeParameters = null; result.Type = null; result.FullSignature = null; result.Body = body;
            return result;
        }
        finally { (constructorSuper, constructorInitializers) = saved; }
    }
    private static SyntaxNode? FindSuper(IEnumerable<SyntaxNode> statements)
    {
        Stack<SyntaxNode> stack = new(statements.Reverse());
        while (stack.TryPop(out var node))
        {
            if (node is ExpressionStatementNode { Expression: { } expression } && TransformSyntax.SkipParentheses(expression) is CallExpressionNode { Expression.Kind: K.SuperKeyword }) return node;
            if (node is TryStatementNode { TryBlock: BlockNode { Statements: { } nested } })
                for (int i = nested.Count - 1; i >= 0; i--) stack.Push(nested[i]);
        }
        return null;
    }

    private SyntaxNode DescriptorMethod(SyntaxNode original, NodeList? modifiers, SyntaxNode? asterisk, Utf8String kind, NodeList? parameters, SyntaxNode? body)
    {
        var function = F.NewFunctionExpression(modifiers, asterisk, null, null, parameters, null, null, body ?? F.NewBlock(new([]), false));
        Context.SetOriginal(function, original); Context.SetSourceMapRange(function, PastDecorators(original)); Context.SetFlags(function, EmitFlags.NoComments);
        var name = Context.StringLiteralFromNode(original.DeclarationName!);
        var namedFunction = Context.HelperCall(EmitHelpers.SetFunctionName, "__setFunctionName"u8,
            kind == "get"u8 || kind == "set"u8 ? [function, name, String(kind)] : [function, name]);
        var property = Property(kind, namedFunction);
        Context.SetOriginal(property, original); Context.SetSourceMapRange(property, PastDecorators(original)); Context.SetFlags(property, EmitFlags.NoComments);
        return property;
    }
    private async ValueTask<SyntaxNode> DescriptorAsync(SyntaxNode member, NodeList? modifiers)
    {
        if (AutoAccessor(member))
        {
            var backing = Context.NewGeneratedPrivateNameForNode(member.DeclarationName!, new(Suffix: "_accessor_storage"u8));
            SyntaxNode Member() => F.NewPropertyAccessExpression(This(), null, backing, NodeFlags.None);
            return Object([
                DescriptorMethod(member, null, null, "get"u8, new([]), F.NewBlock(new([F.NewReturnStatement(Member())]), false)),
                DescriptorMethod(member, null, null, "set"u8, new([Parameter("value"u8)]), F.NewBlock(new([F.NewExpressionStatement(Assign(Member(), F.NewIdentifier("value"u8)))]), false))
            ]);
        }
        var parameters = member is GetAccessorDeclarationNode ? new([]) : await VisitListAsync(((IFunctionSignature)member).Parameters);
        var body = await VisitAsync(DecoratorSyntax.Body(member));
        return Object([DescriptorMethod(member, modifiers, (member as MethodDeclarationNode)?.AsteriskToken,
            member is GetAccessorDeclarationNode ? "get"u8 : member is SetAccessorDeclarationNode ? "set"u8 : "value"u8, parameters, body)]);
    }
    private SyntaxNode Forwarder(K kind, NodeList? modifiers, SyntaxNode name, IdentifierNode descriptor)
    {
        var staticOnly = Modifiers(modifiers, m => m.Kind == K.StaticKeyword);
        var expression = Access(descriptor, kind == K.MethodDeclaration ? "value"u8 : kind == K.GetAccessor ? "get"u8 : "set"u8);
        if (kind != K.MethodDeclaration) expression = Context.MethodCall(expression, "call"u8, kind == K.GetAccessor ? [This()] : [This(), F.NewIdentifier("value"u8)]);
        var body = F.NewBlock(new([F.NewReturnStatement(expression)]), false);
        return kind == K.SetAccessor ? F.NewSetAccessorDeclaration(staticOnly, name, null, new([Parameter("value"u8)]), null, null, body)
            : F.NewGetAccessorDeclaration(staticOnly, name, null, new([]), null, null, body);
    }
}

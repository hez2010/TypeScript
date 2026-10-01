using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class ClassFieldsTransformer
{
    private sealed class PrivateInfo
    {
        internal Utf8String Kind;
        internal bool Static, Valid;
        internal IdentifierNode? Brand, Storage, Method, Getter, Setter;
    }
    private bool LowerMember(SyntaxNode node) => LowerPrivate || Static(node) && (Context.GetFlags(node) & EmitFlags.TransformPrivateStaticElements) != 0;
    private static bool PrivateInstanceMethod(SyntaxNode node) => !Static(node) && node.DeclarationName is PrivateIdentifierNode
        && (node is MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode || node is PropertyDeclarationNode && Binding.SemanticSyntax.HasModifier(node, K.AccessorKeyword));
    private static bool IdentifierText(Utf8String text)
    {
        if (text.Length == 0) return false;
        for (int i = 0; i < text.Length;)
        {
            int point = Wtf8.Decode(text.Span[i..], out int width);
            if (i == 0 ? !TokenFacts.IsIdentifierStart(point) : !TokenFacts.IsIdentifierPart(point)) return false;
            i += width;
        }
        return true;
    }
    private static IdentifierNode? InferredClassName(SyntaxNode node) => node is not ClassExpressionNode ? null : node.Parent switch
    {
        PropertyAssignmentNode p => p.Name as IdentifierNode,
        BindingElementNode b => b.Name as IdentifierNode,
        VariableDeclarationNode v => v.Name as IdentifierNode,
        BinaryExpressionNode b when b.Right == node => b.Left as IdentifierNode ?? (b.Left as PropertyAccessExpressionNode)?.Name as IdentifierNode,
        _ => null
    };
    private object PrivateKey(SyntaxNode name) => Context.GetAutoGenerateInfo(name) is not null
        ? Context.GetNodeForGeneratedName(name) : ((PrivateIdentifierNode)name).Text;
    private PrivateInfo? Private(SyntaxNode name)
    {
        var key = PrivateKey(name);
        for (var env = environment; env is not null; env = env.Previous)
            if (env.PrivateNames.TryGetValue(key, out var info)) return info.Kind.Length == 0 ? null : info;
        return null;
    }
    private IdentifierNode HoistPrivateName(Utf8String name, SyntaxNode? node = null, Utf8String suffix = default)
    {
        var prefix = environment!.ClassName is { } cls ? Utf8String.Concat("_"u8, cls.Text, "_"u8) : new Utf8String("_"u8);
        var options = new AutoGenerateOptions(GeneratedIdentifierFlags.Optimistic | GeneratedIdentifierFlags.ReservedInNestedScopes, Suffix: suffix);
        var result = node is not null && Context.GetAutoGenerateInfo(node) is not null ? Context.NewGeneratedNameForNode(node, options with { Prefix = prefix })
            : Context.NewUniqueName(Utf8String.Concat(prefix, name), options);
        if (iteration && environment.Node is ClassExpressionNode) Context.AddLexicalDeclaration(result);
        else Context.AddVariableDeclaration(result);
        return result;
    }
    private IdentifierNode HoistPrivateName(SyntaxNode name, Utf8String suffix = default)
    {
        var text = ((PrivateIdentifierNode)name).Text;
        return HoistPrivateName(text.Length > 0 && text[0] == '#' ? text.Substring(1) : text, name, suffix);
    }
    private void RegisterPrivateMembers(SyntaxNode node)
    {
        foreach (var member in Members(node))
            if (member.DeclarationName is PrivateIdentifierNode name)
            {
                if (LowerMember(member)) RegisterPrivate(member, name);
                else environment!.PrivateNames[PrivateKey(name)] = new();
            }
        if (environment!.Instances is { } instances) environment.Pending.Add(Assign(instances, F.NewNewExpression(F.NewIdentifier("WeakSet"u8), null, new([]))));
        foreach (var member in Members(node).Where(m => AutoAccessor(m) && (LowerAutoAccessors || Static(m) && LowerMember(m))))
        {
            var name = AccessorStorage(member.DeclarationName!);
            if (LowerMember(member))
            {
                var field = Context.Clone((PropertyDeclarationNode)member);
                field.Modifiers = FilterModifiers(field.Modifiers, K.AccessorKeyword);
                RegisterPrivate(field, name);
            }
            else environment.PrivateNames.TryAdd(PrivateKey(name), new());
        }
    }
    private void RegisterPrivate(SyntaxNode node, SyntaxNode name)
    {
        var env = environment!;
        var key = PrivateKey(name);
        env.PrivateNames.TryGetValue(key, out var previous);
        bool isStatic = Static(node), valid = (Context.GetAutoGenerateInfo(name) is not null || ((PrivateIdentifierNode)name).Text != "#constructor"u8) && previous is null;
        var brand = isStatic ? env.This ?? env.Constructor : env.Instances;
        if (node is PropertyDeclarationNode && !Binding.SemanticSyntax.HasModifier(node, K.AccessorKeyword))
        {
            var storage = HoistPrivateName(name);
            env.PrivateNames[key] = new() { Kind = "f"u8, Static = isStatic, Valid = valid, Brand = isStatic ? brand : storage, Storage = isStatic ? storage : null };
            if (!isStatic) env.Pending.Add(Assign(storage, F.NewNewExpression(F.NewIdentifier("WeakMap"u8), null, new([]))));
        }
        else if (node is MethodDeclarationNode)
            env.PrivateNames[key] = new() { Kind = "m"u8, Static = isStatic, Valid = valid, Brand = brand, Method = HoistPrivateName(name) };
        else
        {
            bool get = node is GetAccessorDeclarationNode or PropertyDeclarationNode, set = node is SetAccessorDeclarationNode or PropertyDeclarationNode;
            var getter = get ? HoistPrivateName(name, "_get"u8) : null;
            var setter = set ? HoistPrivateName(name, "_set"u8) : null;
            if (previous is { Kind: var kind } && kind == "a"u8 && previous.Static == isStatic
                && (!get || previous.Getter is null) && (!set || previous.Setter is null))
            { previous.Getter ??= getter; previous.Setter ??= setter; }
            else env.PrivateNames[key] = new() { Kind = "a"u8, Static = isStatic, Valid = valid, Brand = brand, Getter = getter, Setter = setter };
        }
    }
    private async ValueTask<bool> PrivateConstructorReferenceAsync(SyntaxNode node)
    {
        if (checker is null) return false;
        foreach (var member in Members(node).Where(m => !Static(m) && m.DeclarationName is PrivateIdentifierNode))
        {
            var root = DecoratorSyntax.Body(member) ?? (member as IInitializedNode)?.Initializer;
            if (root is null) continue;
            Stack<SyntaxNode> pending = new([root]);
            while (pending.TryPop(out var child))
            {
                Cancellation.ThrowIfCancellationRequested();
                if (child is IdentifierNode && Context.ParseNode(child) is IdentifierNode parsed
                    && await checker.GetReferencedValueForEmitAsync(parsed, Cancellation) == Context.MostOriginal(node)) return true;
                if (child is PropertyAccessExpressionNode access) { if (access.Expression is not null) pending.Push(access.Expression); }
                else for (int i = child.ChildCount - 1; i >= 0; i--) pending.Push(child.GetChild(i));
            }
        }
        return false;
    }
    private async ValueTask<SyntaxNode?> MethodAsync(SyntaxNode node)
    {
        if (node.DeclarationName is not PrivateIdentifierNode name || !LowerMember(node)) return await VisitEachChildAsync(node);
        var info = Private(name)!;
        if (!info.Valid) return node;
        var functionName = info.Method ?? (node is GetAccessorDeclarationNode ? info.Getter : info.Setter);
        if (functionName is not null)
        {
            Context.StartVariableEnvironment();
            var body = await VisitFunctionBodyAsync(DecoratorSyntax.Body(node));
            var parameters = await VisitListAsync(((IFunctionSignature)node).Parameters);
            var function = F.NewFunctionExpression(FilterModifiers(node.ModifierList, K.StaticKeyword, K.AccessorKeyword), (node as MethodDeclarationNode)?.AsteriskToken,
                functionName, null, parameters, null, null, body);
            environment!.Pending.Add(Assign(functionName, function));
        }
        return null;
    }
    private SyntaxNode PrivateGet(PrivateInfo info, SyntaxNode receiver)
    {
        Context.SetCommentRange(receiver, new(-1, receiver.End));
        var extra = info.Kind == "a"u8 ? info.Getter : info.Kind == "m"u8 ? info.Method : info.Static ? info.Storage : null;
        List<SyntaxNode> args = [receiver, info.Brand!, F.NewStringLiteral(info.Kind, TokenFlags.None)];
        if (extra is not null) args.Add(extra);
        return Context.HelperCall(EmitHelpers.ClassPrivateFieldGet, "__classPrivateFieldGet"u8, [.. args]);
    }
    private async ValueTask<SyntaxNode> PrivateSetAsync(PrivateInfo info, SyntaxNode receiver, SyntaxNode value, K op)
    {
        receiver = (await VisitAsync(receiver))!;
        value = (await VisitAsync(value))!;
        if (op != K.EqualsToken)
        {
            var (read, initialize) = CopiableReceiver(receiver);
            receiver = initialize ?? read;
            value = Context.Binary(PrivateGet(info, read), NonAssignment(op), value);
        }
        Context.SetCommentRange(receiver, new(-1, receiver.End));
        var extra = info.Kind == "a"u8 ? info.Setter : info.Kind == "f"u8 && info.Static ? info.Storage : null;
        List<SyntaxNode> args = [receiver, info.Brand!, value, F.NewStringLiteral(info.Kind, TokenFlags.None)];
        if (extra is not null) args.Add(extra);
        return Context.HelperCall(EmitHelpers.ClassPrivateFieldSet, "__classPrivateFieldSet"u8, [.. args]);
    }
    private (SyntaxNode Read, SyntaxNode? Initialize) CopiableReceiver(SyntaxNode receiver)
    {
        var copy = receiver.Pos < 0 ? receiver : Context.Clone(receiver);
        if (Inlineable(receiver)) return (copy, null);
        var temp = Temp();
        return (temp, Assign(temp, copy));
    }
    private (SyntaxNode Receiver, SyntaxNode Target) PrivateCallBinding(PropertyAccessExpressionNode node)
    {
        var receiver = node.Expression!;
        if (TransformSyntax.SkipParentheses(receiver).Kind is K.Identifier or K.ThisKeyword or K.NumericLiteral or K.BigIntLiteral or K.StringLiteral) return (receiver, node);
        var temp = Temp();
        return (temp, F.NewPropertyAccessExpression(F.NewParenthesizedExpression(Assign(temp, receiver)), null, node.Name, NodeFlags.None));
    }
}

using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class EsDecoratorsTransformer : SyntaxRewriter
{
    private enum ScopeKind { Class, Element, Name, Other }
    private sealed class Scope(ScopeKind kind, Scope? previous)
    {
        internal readonly ScopeKind Kind = kind;
        internal readonly Scope? Previous = previous;
        internal ClassInfo? Class;
        internal IdentifierNode? This, Super;
        internal List<SyntaxNode>? Pending;
    }
    private sealed class MemberInfo(IdentifierNode decorators)
    {
        internal readonly IdentifierNode Decorators = decorators;
        internal IdentifierNode? Initializers, ExtraInitializers, Descriptor;
    }
    private sealed class ClassInfo(SyntaxNode node, IdentifierNode metadata)
    {
        internal readonly SyntaxNode Node = node;
        internal readonly IdentifierNode Metadata = metadata;
        internal IdentifierNode? This, Super, Decorators, Descriptor, ExtraInitializers, StaticExtra, InstanceExtra;
        internal readonly List<(SyntaxNode Node, MemberInfo Info)> Members = [];
        internal readonly List<SyntaxNode>[] Decorations = [[], [], [], []];
        internal readonly List<SyntaxNode> PendingStatic = [], PendingInstance = [];
        internal bool HasStaticInitializers, HasInstanceFields, HasStaticPrivate;
    }

    private readonly CompilerOptions options;
    private readonly NamedEvaluation named;
    private readonly HashSet<SyntaxNode> containingDecorators = [];
    private Scope? scope;
    private ClassInfo? currentClass;
    private IdentifierNode? classThis, classSuper;
    private List<SyntaxNode> pending = [];
    private bool discarded, forceStaticPrivate;
    private NodeFactory F => Context.Factory;

    internal EsDecoratorsTransformer(EmitContext context, CompilerOptions options, CancellationToken cancellation = default)
        : base(context, cancellation) { this.options = options; named = new(context); }

    private void UpdateState()
    {
        currentClass = null; classThis = null; classSuper = null;
        var entry = scope?.Kind == ScopeKind.Name ? scope.Previous?.Previous?.Previous : scope;
        if (entry?.Kind == ScopeKind.Class) { if (scope?.Kind != ScopeKind.Name) currentClass = entry.Class; }
        else if (entry?.Kind == ScopeKind.Element)
        { currentClass = entry.Previous?.Class; classThis = entry.This; classSuper = entry.Super; }
    }
    private void Enter(ScopeKind kind, ClassInfo? info = null, SyntaxNode? element = null)
    {
        var entry = new Scope(kind, scope) { Class = info };
        if (kind is ScopeKind.Class or ScopeKind.Other) { entry.Pending = pending; pending = []; }
        if (kind == ScopeKind.Element && (element is ClassStaticBlockDeclarationNode || element is PropertyDeclarationNode && Static(element)))
        { entry.This = scope?.Class?.This; entry.Super = scope?.Class?.Super; }
        scope = entry;
        UpdateState();
    }
    private void Exit()
    {
        if (scope!.Pending is { } saved) pending = saved;
        scope = scope.Previous;
        UpdateState();
    }

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        if (options.ExperimentalDecorators == true || options.EmitTargetYear == int.MaxValue && options.UseDefineForClassFields != false) return node;
        bool discard = discarded;
        discarded = false;
        try
        {
            if (node is SourceFileNode source)
            {
                if (source.IsDeclarationFile) return source;
                IndexDecorators(source);
                scope = null; forceStaticPrivate = false;
                try
                {
                    var result = (await VisitEachChildAsync(source))!;
                    foreach (var helper in Context.ReadHelpers()) Context.AddHelper(result, helper);
                    if (forceStaticPrivate) Context.AddFlags(result, EmitFlags.TransformPrivateStaticElements);
                    return result;
                }
                finally { scope = null; UpdateState(); pending = []; containingDecorators.Clear(); }
            }
            // Class elements are visited explicitly even when only a sibling has decorators.
            if (scope?.Kind == ScopeKind.Class && node is ConstructorDeclarationNode or MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode or PropertyDeclarationNode or ClassStaticBlockDeclarationNode)
                return await MemberAsync(node);
            if (classThis is null && constructorSuper is null && !ContainsDecorators(node)) return node;
            switch (node)
            {
                case DecoratorNode: return null;
                case ClassDeclarationNode or ClassExpressionNode: return await ClassAsync(node);
                case KeywordExpressionNode { Kind: K.ThisKeyword }: return classThis ?? node;
                case ParameterDeclarationNode parameter: return await ParameterAsync(parameter);
                case ComputedPropertyNameNode computed: return await ComputedNameAsync(computed);
                case BinaryExpressionNode binary: return await BinaryAsync(binary, discard);
                case PrefixUnaryExpressionNode or PostfixUnaryExpressionNode: return await UpdateAsync(node, discard);
                case PropertyAccessExpressionNode or ElementAccessExpressionNode: return await AccessAsync(node);
                case CallExpressionNode call: return await CallAsync(call);
                case TaggedTemplateExpressionNode tag: return await TagAsync(tag);
                case ExpressionStatementNode statement:
                    var expression = Context.Clone(statement);
                    expression.Expression = await DiscardedAsync(statement.Expression);
                    return statement == constructorSuper ? F.NewSyntaxList([expression, .. constructorInitializers!]) : expression;
                case TryStatementNode { TryBlock: BlockNode { Statements: { } statements } } statement
                    when constructorSuper is not null && FindSuper(statements) == constructorSuper:
                    var updatedTry = (TryStatementNode)(await VisitEachChildAsync(statement))!;
                    var tryBody = (BlockNode)updatedTry.TryBlock!;
                    updatedTry.TryBlock = EmitContext.CopyRange(F.NewBlock(new(tryBody.Statements!.ToArray()), true), statement.TryBlock!);
                    return updatedTry;
                case ForStatementNode loop:
                    var updatedLoop = Context.Clone(loop);
                    updatedLoop.Initializer = await DiscardedAsync(loop.Initializer);
                    updatedLoop.Condition = await VisitAsync(loop.Condition);
                    updatedLoop.Incrementor = await DiscardedAsync(loop.Incrementor);
                    updatedLoop.Statement = await VisitIterationBodyAsync(loop.Statement);
                    return updatedLoop;
                case ParenthesizedExpressionNode parentheses when discard:
                    var updatedParentheses = Context.Clone(parentheses);
                    updatedParentheses.Expression = await DiscardedAsync(parentheses.Expression);
                    return updatedParentheses;
                case PartiallyEmittedExpressionNode emitted when discard:
                    var updatedPartial = Context.Clone(emitted);
                    updatedPartial.Expression = await DiscardedAsync(emitted.Expression);
                    return updatedPartial;
                case MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode or FunctionExpressionNode or FunctionDeclarationNode:
                    Enter(ScopeKind.Other);
                    try { return await VisitEachChildAsync(node); }
                    finally { Exit(); }
                default: return await VisitEachChildAsync(AssignName(node));
            }
        }
        finally { discarded = discard; }
    }

    private void IndexDecorators(SyntaxNode root)
    {
        containingDecorators.Clear();
        Stack<(SyntaxNode Node, bool Visited)> stack = new([(root, false)]);
        while (stack.TryPop(out var entry))
        {
            Cancellation.ThrowIfCancellationRequested();
            if (!entry.Visited)
            {
                stack.Push((entry.Node, true));
                for (int i = entry.Node.ChildCount - 1; i >= 0; i--) stack.Push((entry.Node.GetChild(i), false));
            }
            else
            {
                bool contains = entry.Node is DecoratorNode;
                for (int i = 0; !contains && i < entry.Node.ChildCount; i++) contains = containingDecorators.Contains(entry.Node.GetChild(i));
                if (contains) containingDecorators.Add(entry.Node);
            }
        }
    }
    private bool ContainsDecorators(SyntaxNode node)
    {
        for (SyntaxNode? original = node; original is not null; original = Context.Original(original))
            if (containingDecorators.Contains(original)) return true;
        return false;
    }
    private static NodeList Members(SyntaxNode node) => DecoratorSyntax.Members(node) ?? new([]);
    private static bool Static(SyntaxNode node) => node is ClassStaticBlockDeclarationNode || DecoratorSyntax.Static(node);
    private static bool AutoAccessor(SyntaxNode node) => node is PropertyDeclarationNode && SemanticSyntax.HasModifier(node, K.AccessorKeyword);
    private static bool Method(SyntaxNode node) => node is MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode;
    private static bool Decorated(SyntaxNode node) => DecoratorSyntax.ClassDecorated(false, node) || DecoratorSyntax.ChildDecorated(false, node);
    private static NodeList? Heritage(SyntaxNode node) => node switch { ClassDeclarationNode n => n.HeritageClauses, ClassExpressionNode n => n.HeritageClauses, _ => null };
    private static SyntaxNode? Initializer(SyntaxNode node) => node switch
    { BinaryExpressionNode n => n.Right, ExportAssignmentNode n => n.Expression, ShorthandPropertyAssignmentNode n => n.ObjectAssignmentInitializer, IInitializedNode n => n.Initializer, _ => null };
    private SyntaxNode AssignName(SyntaxNode node)
    {
        if (named.Applies(node) && Initializer(node) is { } initializer && NamedEvaluation.SkipOuter(initializer) is ClassExpressionNode { Name: null } expression && Decorated(expression))
            return named.Transform(node, ignoreEmpty: !DecoratorSyntax.ClassDecorated(false, expression));
        return node;
    }
    private static NodeList? Modifiers(NodeList? list, Func<SyntaxNode, bool>? predicate = null)
    {
        if (list is null) return null;
        var filtered = list.Where(n => n is not DecoratorNode && (predicate?.Invoke(n) ?? true)).ToArray();
        return filtered.Length == list.Count ? list : new(filtered, list.Pos, list.End);
    }
    private static EmitRange PastDecorators(SyntaxNode node)
    {
        var last = node.ModifierList?.LastOrDefault(n => n is DecoratorNode);
        return new(last is { End: >= 0 } ? last.End : node.Pos, node.End);
    }
    private static EmitRange PastModifiers(SyntaxNode node)
    {
        if (node is PropertyDeclarationNode or MethodDeclarationNode) return new(node.DeclarationName!.Pos, node.End);
        var last = node.ModifierList?.LastOrDefault();
        return last is { End: >= 0 } ? new(last.End, node.End) : PastDecorators(node);
    }
    private T Finish<T>(T result, SyntaxNode original) where T : SyntaxNode
    {
        if (result != original) { Context.SetCommentRange(result, new(original.Pos, original.End)); Context.SetSourceMapRange(result, PastDecorators(original)); }
        return result;
    }
    private async ValueTask<SyntaxNode> ParameterAsync(ParameterDeclarationNode node)
    {
        node = (ParameterDeclarationNode)AssignName(node);
        var updated = Context.Clone(node);
        updated.Modifiers = null; updated.QuestionToken = null; updated.Type = null;
        updated.Name = await VisitAsync(node.Name);
        updated.Initializer = await VisitAsync(node.Initializer);
        Context.SetCommentRange(updated, new(node.Pos, node.End));
        var range = PastModifiers(node);
        (updated.Pos, updated.End) = (range.Pos, range.End);
        Context.SetSourceMapRange(updated, range);
        if (updated.Name is not null) Context.SetFlags(updated.Name, EmitFlags.NoTrailingSourceMap);
        return updated;
    }
    private IdentifierNode Unique(Utf8String text, bool nested = false) => Context.NewUniqueName(text,
        new(GeneratedIdentifierFlags.Optimistic | (nested ? GeneratedIdentifierFlags.ReservedInNestedScopes : GeneratedIdentifierFlags.FileLevel)));
    private IdentifierNode HelperName(SyntaxNode member, Utf8String suffix)
    {
        var name = member.DeclarationName;
        Utf8String text = name switch
        {
            IdentifierNode n when Context.GetAutoGenerateInfo(n) is null => n.Text,
            PrivateIdentifierNode n when Context.GetAutoGenerateInfo(n) is null => n.Text.Substring(1),
            StringLiteralNode n when IdentifierText(n.Text) => n.Text,
            _ => member is ClassDeclarationNode or ClassExpressionNode ? "class"u8 : "member"u8
        };
        if (member is GetAccessorDeclarationNode) text = Utf8String.Concat("get_"u8, text);
        if (member is SetAccessorDeclarationNode) text = Utf8String.Concat("set_"u8, text);
        if (name is PrivateIdentifierNode) text = Utf8String.Concat("private_"u8, text);
        if (Static(member)) text = Utf8String.Concat("static_"u8, text);
        return Unique(Utf8String.Concat(Utf8String.Concat("_"u8, text, "_"u8), suffix), nested: true);
    }
    private static bool IdentifierText(Utf8String text)
    {
        if (text.Length == 0) return false;
        for (int offset = 0; offset < text.Length;)
        {
            int rune = Wtf8.Decode(text.Span[offset..], out int width);
            if (offset == 0 ? !TokenFacts.IsIdentifierStart(rune) : !TokenFacts.IsIdentifierPart(rune)) return false;
            offset += width;
        }
        return true;
    }
    private ClassInfo CreateClassInfo(SyntaxNode node)
    {
        var info = new ClassInfo(node, Unique("_metadata"u8));
        if (DecoratorSyntax.Decorated(false, node, null)) info.This = Unique("_classThis"u8,
            Members(node).Any(m => Static(m) && (m.DeclarationName is PrivateIdentifierNode || AutoAccessor(m))));
        foreach (var member in Members(node))
        {
            if (Method(member) && (DecoratorSyntax.Decorated(false, member, node) || DecoratorSyntax.ChildDecorated(false, member, node)))
            {
                bool isStatic = Static(member);
                if ((isStatic ? info.StaticExtra : info.InstanceExtra) is null)
                {
                    var name = Unique(isStatic ? "_staticExtraInitializers"u8 : "_instanceExtraInitializers"u8);
                    if (isStatic) info.StaticExtra = name; else info.InstanceExtra = name;
                    var initializer = Run(isStatic ? info.This ?? This() : This(), name);
                    Context.SetSourceMapRange(initializer, node.DeclarationName is { } declarationName ? new(declarationName.Pos, declarationName.End) : PastDecorators(node));
                    (isStatic ? info.PendingStatic : info.PendingInstance).Add(initializer);
                }
            }
            if (member is ClassStaticBlockDeclarationNode && !named.IsNameBlock(member)) info.HasStaticInitializers = true;
            else if (member is PropertyDeclarationNode property)
            {
                if (Static(member)) info.HasStaticInitializers |= property.Initializer is not null || DecoratorSyntax.HasDecorators(member);
                else info.HasInstanceFields |= !SemanticSyntax.HasModifier(member, K.DeclareKeyword);
            }
            info.HasStaticPrivate |= Static(member) && (member.DeclarationName is PrivateIdentifierNode || AutoAccessor(member));
        }
        return info;
    }
}

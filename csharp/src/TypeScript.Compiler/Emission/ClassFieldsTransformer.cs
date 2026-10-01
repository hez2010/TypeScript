using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class ClassFieldsTransformer : SyntaxRewriter
{
    private sealed class ClassEnvironment(SyntaxNode node, ClassEnvironment? previous)
    {
        internal readonly SyntaxNode Node = node;
        internal readonly ClassEnvironment? Previous = previous;
        internal IdentifierNode? Constructor, This, Super;
        internal IdentifierNode? ClassName, Instances;
        internal readonly Dictionary<object, PrivateInfo> PrivateNames = [];
        internal bool Decorated, HoistInitializers;
        internal List<SyntaxNode> Pending = [];
    }

    private readonly CompilerOptions options;
    private readonly Checker? checker;
    private readonly NamedEvaluation named;
    private readonly Dictionary<SyntaxNode, IdentifierNode> aliases = [];
    private readonly HashSet<SyntaxNode> enclosingClasses = [];
    private ClassEnvironment? environment;
    private SyntaxNode? current, parent, classElement;
    private bool iteration, computedName, discarded;
    private List<SyntaxNode> pendingStatements = [];
    private List<SyntaxNode> pendingTargets = [];
    private SyntaxNode? constructorSuper;
    private List<SyntaxNode>? constructorInitializers;
    private NodeFactory F => Context.Factory;
    private bool LowerPrivate => options.EmitTargetYear < 2022;
    private bool UseDefine => options.UseDefineForClassFields ?? options.EmitTargetYear >= 2022;
    private bool LowerInitializers => !UseDefine || LowerPrivate;

    internal ClassFieldsTransformer(EmitContext context, CompilerOptions options, Checker? checker = null, CancellationToken cancellation = default)
        : base(context, cancellation) { this.options = options; this.checker = checker; named = new(context); }

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        var saved = (current, parent, classElement, iteration, discarded);
        parent = current;
        current = node;
        discarded = false;
        try
        {
            if (options.EmitTargetYear == int.MaxValue && UseDefine) return node;
            switch (node)
            {
                case SourceFileNode source:
                    if (source.IsDeclarationFile) return source;
                    var file = (await VisitEachChildAsync(source))!;
                    foreach (var helper in Context.ReadHelpers()) Context.AddHelper(file, helper);
                    return file;
                case ClassDeclarationNode or ClassExpressionNode: return await ClassAsync(node);
                case VariableStatementNode statement:
                    var savedPending = pendingStatements;
                    pendingStatements = [];
                    try
                    {
                        var visited = (await VisitEachChildAsync(statement))!;
                        return pendingStatements.Count == 0 ? visited : F.NewSyntaxList([visited, .. pendingStatements]);
                    }
                    finally { pendingStatements = savedPending; }
                case IdentifierNode name when aliases.Count != 0 && TransformSyntax.IsIdentifierReference(name, parent):
                    if (checker is not null && Context.ParseNode(name) is IdentifierNode parsed
                        && await checker.GetReferencedValueForEmitAsync(parsed, Cancellation) is { } declaration
                        && enclosingClasses.Contains(declaration) && aliases.TryGetValue(declaration, out var alias))
                    {
                        var copy = Context.Clone(alias);
                        Context.AssignCommentAndSourceMapRanges(copy, name);
                        return copy;
                    }
                    return name;
                case ComputedPropertyNameNode computed: return await ComputedNameAsync(computed);
                case FunctionDeclarationNode or FunctionExpressionNode:
                    iteration = false;
                    var original = Context.MostOriginal(node);
                    if (classElement is null || original == node || environment is null
                        || !Members(environment.Node).Any(m => Context.MostOriginal(m) == original && Static(m))) classElement = null;
                    return await VisitEachChildAsync(node);
                case ConstructorDeclarationNode constructor:
                    iteration = false;
                    classElement = node;
                    return environment is null ? await VisitEachChildAsync(node) : await ConstructorAsync(constructor, environment.Node);
                case MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode:
                    iteration = false;
                    classElement = node;
                    return await MethodAsync(node);
                case PrivateIdentifierNode when LowerPrivate: return F.NewIdentifier(default);
                case PropertyDeclarationNode property: return await PropertyAsync(property);
                case ExpressionStatementNode statement:
                    if (constructorInitializers is not null && ParameterProperty(statement)) return null;
                    if (LowerPrivate && statement.Expression is PrivateIdentifierNode) return statement;
                    var visitedExpression = await VisitDiscardedAsync(statement.Expression);
                    var expressionStatement = visitedExpression == statement.Expression ? statement : Context.Clone(statement);
                    if (expressionStatement != statement) expressionStatement.Expression = visitedExpression;
                    return statement == constructorSuper ? F.NewSyntaxList([expressionStatement, .. constructorInitializers!]) : expressionStatement;
                case ForStatementNode loop: return await ForAsync(loop);
                case ForInOrOfStatementNode or DoStatementNode or WhileStatementNode:
                    iteration = true;
                    return await VisitEachChildAsync(node);
                case PropertyAccessExpressionNode or ElementAccessExpressionNode: return await AccessAsync(node);
                case CallExpressionNode call: return await CallAsync(call);
                case TaggedTemplateExpressionNode tag: return await TagAsync(tag);
                case PrefixUnaryExpressionNode or PostfixUnaryExpressionNode: return await UpdateAsync(node, saved.discarded);
                case BinaryExpressionNode binary: return await BinaryAsync(binary, saved.discarded);
                case ParenthesizedExpressionNode parentheses when saved.discarded:
                    var updatedParentheses = Context.Clone(parentheses);
                    updatedParentheses.Expression = await VisitDiscardedAsync(parentheses.Expression);
                    return updatedParentheses;
                case KeywordExpressionNode { Kind: K.ThisKeyword }:
                    if (LowerPrivate && environment is not null && (computedName || StaticInitializer(classElement)))
                    {
                        if ((!computedName || !environment.Decorated || options.ExperimentalDecorators == true)
                            && (environment.This ?? environment.Constructor) is { } receiver) return receiver;
                        if (!computedName && environment.Decorated && options.ExperimentalDecorators == true) return F.NewParenthesizedExpression(Context.VoidZero());
                    }
                    return node;
                default:
                    if (named.Applies(node) && NamedInitializer(node) is { } initializer
                        && NamedEvaluation.SkipOuter(initializer) is ClassExpressionNode anonymous && NeedsAssignedName(anonymous))
                        node = named.Transform(node, ignoreEmpty: node is ExportAssignmentNode);
                    return await VisitEachChildAsync(node);
            }
        }
        finally
        {
            (current, parent, classElement, iteration, discarded) = saved;
            if (node is SourceFileNode) { aliases.Clear(); enclosingClasses.Clear(); environment = null; }
        }
    }

    private static SyntaxNode? NamedInitializer(SyntaxNode node) => node switch
    {
        BinaryExpressionNode n => n.Right, ExportAssignmentNode n => n.Expression,
        ShorthandPropertyAssignmentNode n => n.ObjectAssignmentInitializer, IInitializedNode n => n.Initializer, _ => null
    };
    private static NodeList Members(SyntaxNode node) => DecoratorSyntax.Members(node) ?? new([]);
    private static NodeList? Heritage(SyntaxNode node) => node switch { ClassDeclarationNode n => n.HeritageClauses, ClassExpressionNode n => n.HeritageClauses, _ => null };
    private static bool Static(SyntaxNode node) => node is ClassStaticBlockDeclarationNode || DecoratorSyntax.Static(node);
    private static bool StaticInitializer(SyntaxNode? node) => node is ClassStaticBlockDeclarationNode || node is PropertyDeclarationNode && Static(node);
    private bool NeedsAssignedName(ClassExpressionNode node) => node.Name is null && !Members(node).Any(named.IsNameBlock)
        && (LowerPrivate || (Context.GetFlags(node) & EmitFlags.TransformPrivateStaticElements) != 0)
        && Members(node).Any(m => m.DeclarationName is PrivateIdentifierNode || m is ClassStaticBlockDeclarationNode
            || Static(m) && LowerInitializers && m is PropertyDeclarationNode { Initializer: not null });

    private async ValueTask<SyntaxNode> ComputedNameAsync(ComputedPropertyNameNode node)
    {
        var saved = (environment, computedName);
        computedName = true;
        if (environment?.Previous is { } previous) environment = previous;
        try
        {
            var result = Context.Clone(node);
            result.Expression = await VisitAsync(node.Expression);
            if (saved.environment?.Pending is { Count: > 0 } expressions)
            {
                if (result.Expression is ParenthesizedExpressionNode parentheses)
                {
                    var inner = Context.Clone(parentheses);
                    inner.Expression = Context.InlineExpressions([.. expressions, parentheses.Expression!]);
                    result.Expression = inner;
                }
                else result.Expression = Context.InlineExpressions([.. expressions, result.Expression!]);
                expressions.Clear();
            }
            return result;
        }
        finally { (environment, computedName) = saved; }
    }

    private bool HasLexical(SyntaxNode root, K kind)
    {
        Stack<SyntaxNode> pending = new([root]);
        while (pending.TryPop(out var node))
        {
            Cancellation.ThrowIfCancellationRequested();
            if (node.Kind == kind) return true;
            if (node != root && node is FunctionDeclarationNode or FunctionExpressionNode or MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode or ConstructorDeclarationNode)
            {
                if (node.DeclarationName is ComputedPropertyNameNode name) pending.Push(name);
                continue;
            }
            if (node is ClassDeclarationNode or ClassExpressionNode)
            {
                foreach (var member in Members(node)) if (member.DeclarationName is ComputedPropertyNameNode name) pending.Push(name);
                if (Heritage(node) is { } heritage) foreach (var item in heritage) pending.Push(item);
                continue;
            }
            for (int i = node.ChildCount - 1; i >= 0; i--) pending.Push(node.GetChild(i));
        }
        return false;
    }

    private async ValueTask<SyntaxNode> ClassAsync(SyntaxNode node)
    {
        var savedEnvironment = environment;
        var original = Context.MostOriginal(node);
        environment = new(node, environment)
        {
            This = Context.GetClassThis(node) as IdentifierNode,
            Decorated = original is ClassDeclarationNode or ClassExpressionNode && DecoratorSyntax.ClassDecorated(options.ExperimentalDecorators == true, original)
        };
        enclosingClasses.Add(original);
        try
        {
            bool isDeclaration = node is ClassDeclarationNode;
            environment.ClassName = node.DeclarationName as IdentifierNode ?? InferredClassName(original);
            if (environment.ClassName is null && Context.GetAssignedName(node) is StringLiteralNode assigned
                && IdentifierText(assigned.Text)) environment.ClassName = F.NewIdentifier(assigned.Text);
            if (LowerPrivate && Members(node).Any(PrivateInstanceMethod)) environment.Instances = HoistPrivateName("instances"u8);
            var statics = Members(node).Where(m => m is ClassStaticBlockDeclarationNode || m is PropertyDeclarationNode && Static(m)).ToArray();
            bool constructorReference = LowerPrivate && (named.HasAssignedName(node) || statics.Any(named.IsClassThisBlock)
                || Members(node).Any(m => Static(m) && (m.DeclarationName is PrivateIdentifierNode || AutoAccessor(m)))
                || !environment.Decorated && statics.Any(member => HasLexical(member, K.ThisKeyword) || HasLexical(member, K.SuperKeyword)))
                || options.EmitTargetYear < int.MaxValue && node.DeclarationName is null && environment.This is null && Members(node).Any(m => Static(m) && AutoAccessor(m))
                || await PrivateConstructorReferenceAsync(node);
            environment.HoistInitializers = Members(node).OfType<PropertyDeclarationNode>().Any(p => !Static(p)
                && p.Name is not PrivateIdentifierNode && !AutoAccessor(p) && !SemanticSyntax.HasModifier(Context.MostOriginal(p), K.AbstractKeyword)
                && LowerInitializers && (UseDefine || p.Initializer is not null));
            environment.HoistInitializers |= LowerPrivate && Members(node).Any(m => !Static(m) && (m.DeclarationName is PrivateIdentifierNode || AutoAccessor(m)));
            bool transformStatics = LowerPrivate && statics.Any(m => m is ClassStaticBlockDeclarationNode || m is PropertyDeclarationNode { Initializer: not null });
            IdentifierNode? temp = null;
            if (constructorReference || !isDeclaration && !environment.Decorated && transformStatics)
            {
                temp = LowerPrivate ? environment.This : null;
                if (temp is null)
                {
                    temp = Context.NewTempVariable(new(GeneratedIdentifierFlags.ReservedInNestedScopes));
                    Context.AddVariableDeclaration(temp);
                }
                environment.Constructor = Context.Clone(temp);
                if (!environment.Decorated && (isDeclaration || transformStatics || LowerPrivate && Members(node).Any(m => m.DeclarationName is PrivateIdentifierNode && !Static(m))))
                    aliases[original] = environment.Constructor;
            }
            var heritage = await HeritageAsync(Heritage(node), LowerPrivate && !environment.Decorated && statics.Any(m => HasLexical(m, K.SuperKeyword)));
            RegisterPrivateMembers(node);
            List<SyntaxNode> members = [];
            foreach (var member in Members(node))
            {
                if (LowerPrivate && member is ClassStaticBlockDeclarationNode) continue;
                var savedElement = classElement;
                classElement = member;
                try { members.AddRange(await VisitArrayAsync([member])); }
                finally { classElement = savedElement; }
            }
            var syntheticConstructor = !members.Any(m => m is ConstructorDeclarationNode) ? await ConstructorAsync(null, node) : null;
            SyntaxNode? membersPrologue = null, syntheticStaticBlock = null;
            if (!LowerPrivate && environment.Pending.Count != 0)
            {
                var statement = F.NewExpressionStatement(Context.InlineExpressions(environment.Pending));
                if (HasLexical(statement, K.ThisKeyword) || HasLexical(statement, K.SuperKeyword))
                {
                    var cache = Context.NewTempVariable();
                    Context.AddVariableDeclaration(cache);
                    var arrow = F.NewArrowFunction(null, null, new([]), null, null, F.NewToken(K.EqualsGreaterThanToken), F.NewBlock(new([statement]), false));
                    membersPrologue = Assign(cache, arrow);
                    statement = F.NewExpressionStatement(F.NewCallExpression(cache, null, null, new([]), NodeFlags.None));
                }
                syntheticStaticBlock = F.NewClassStaticBlockDeclaration(null, F.NewBlock(new([statement]), false));
                environment.Pending.Clear();
            }
            if (syntheticConstructor is not null || syntheticStaticBlock is not null)
            {
                var leading = members.Where(m => named.IsClassThisBlock(m) || named.IsNameBlock(m)).ToArray();
                members = [.. leading, .. syntheticConstructor is null ? Array.Empty<SyntaxNode>() : [syntheticConstructor],
                    .. syntheticStaticBlock is null ? Array.Empty<SyntaxNode>() : [syntheticStaticBlock], .. members.Except(leading)];
            }
            var memberList = new NodeList(members.ToArray(), Members(node).Pos, Members(node).End);
            var updated = Context.Clone(node);
            if (updated is ClassDeclarationNode cls) { cls.TypeParameters = null; cls.HeritageClauses = heritage; cls.Members = memberList; }
            else { var expression = (ClassExpressionNode)updated; expression.TypeParameters = null; expression.HeritageClauses = heritage; expression.Members = memberList; }
            List<SyntaxNode> expressions = [.. environment.Pending];
            int pendingCount = expressions.Count;
            Dictionary<SyntaxNode, SyntaxNode> staticOrigins = [];
            if (LowerPrivate)
                foreach (var member in statics)
                {
                    var expression = member is ClassStaticBlockDeclarationNode block ? await StaticBlockAsync(block)
                        : await PropertyExpressionAsync((PropertyDeclarationNode)member, environment.Decorated ? environment.This ?? Context.GetLocalName(node)
                            : isDeclaration ? Context.GetLocalName(node) : temp!);
                    if (expression is null) continue;
                    if (!isDeclaration && !environment.Decorated)
                    {
                        Context.SetOriginal(expression, member, overwrite: true);
                        Context.AssignCommentAndSourceMapRanges(expression, member);
                    }
                    staticOrigins[expression] = member;
                    expressions.Add(expression);
                }
            if (isDeclaration)
            {
                List<SyntaxNode> statements = [];
                List<SyntaxNode> prologue = [];
                if (temp is not null) prologue.Add(Assign(temp, Context.GetLocalName(node)));
                prologue.AddRange(expressions.Take(pendingCount));
                if (prologue.Count != 0) statements.Add(F.NewExpressionStatement(Context.InlineExpressions(prologue)));
                foreach (var expression in expressions.Skip(pendingCount))
                    statements.Add(PropertyStatement(expression, staticOrigins[expression]));
                var declaration = (ClassDeclarationNode)updated;
                if ((statements.Count != 0 || LowerInitializers && statics.Length != 0) && declaration.Name is null) declaration.Name = Context.NewGeneratedNameForNode(node);
                if (statements.Count != 0 && SemanticSyntax.HasModifier(node, K.ExportKeyword) && SemanticSyntax.HasModifier(node, K.DefaultKeyword))
                {
                    declaration.Modifiers = FilterModifiers(declaration.Modifiers, K.ExportKeyword, K.DefaultKeyword);
                    statements.Add(F.NewExportAssignment(null, false, null, Context.GetLocalName(node)));
                }
                return F.NewSyntaxList([.. membersPrologue is null ? Array.Empty<SyntaxNode>() : [F.NewExpressionStatement(membersPrologue)], updated, .. statements]);
            }
            if (environment.Decorated)
            {
                foreach (var expression in expressions.Take(pendingCount)) pendingStatements.Add(F.NewExpressionStatement(expression));
                foreach (var expression in expressions.Skip(pendingCount)) pendingStatements.Add(PropertyStatement(expression, staticOrigins[expression]));
                var classReference = temp ?? (LowerPrivate ? environment.This : null);
                return classReference is null ? updated : Assign(classReference, updated);
            }
            if (expressions.Count == 0 && membersPrologue is null) return updated;
            if (expressions.Count == 0)
            {
                Context.AddFlags(updated, EmitFlags.Indented | EmitFlags.StartOnNewLine);
                Context.AddFlags(membersPrologue!, EmitFlags.StartOnNewLine);
                return Context.InlineExpressions([membersPrologue!, updated])!;
            }
            if (temp is null)
            {
                temp = Context.NewTempVariable(new(GeneratedIdentifierFlags.ReservedInNestedScopes));
                if (iteration && Members(node).Any(m => m is PropertyDeclarationNode { Name: ComputedPropertyNameNode } && !Static(m))) Context.AddLexicalDeclaration(temp);
                else Context.AddVariableDeclaration(temp);
            }
            List<SyntaxNode> sequence = [.. membersPrologue is null ? Array.Empty<SyntaxNode>() : [membersPrologue], Assign(temp!, updated), .. expressions, Context.Clone(temp!)];
            Context.AddFlags(updated, EmitFlags.Indented);
            foreach (var expression in sequence) Context.AddFlags(expression, EmitFlags.StartOnNewLine);
            return Context.InlineExpressions(sequence)!;
        }
        finally { enclosingClasses.Remove(original); environment = savedEnvironment; }
    }

    private async ValueTask<SyntaxNode?> StaticBlockAsync(ClassStaticBlockDeclarationNode node)
    {
        var saved = classElement;
        classElement = node;
        try
        {
            var body = (BlockNode)node.Body!;
            if (named.IsClassThisBlock(node) || named.IsNameBlock(node))
            {
                var expression = (await VisitAsync(((ExpressionStatementNode)body.Statements![0]).Expression))!;
                if (expression is BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken } assignment && assignment.Left == assignment.Right) return null;
                Context.SetOriginal(expression, node, overwrite: true);
                Context.AssignCommentAndSourceMapRanges(expression, node);
                return expression;
            }
            Context.StartVariableEnvironment();
            var statements = await VisitListAsync(body.Statements);
            statements = Context.MergeEnvironment(statements, Context.EndVariableEnvironment());
            var block = F.NewBlock(new(statements?.ToArray() ?? [], body.Statements?.Pos ?? -1, body.Statements?.End ?? -1), true);
            var arrow = F.NewArrowFunction(null, null, new([]), null, null, F.NewToken(K.EqualsGreaterThanToken), block);
            Context.SetOriginal(arrow, node);
            Context.AddFlags(arrow, EmitFlags.NoLexicalArguments | EmitFlags.NoLexicalThis);
            var call = F.NewCallExpression(F.NewParenthesizedExpression(arrow), null, null, new([]), NodeFlags.None);
            Context.SetOriginal(call, node);
            Context.SetSourceMapRange(call, Context.GetSourceMapRange(node));
            return call;
        }
        finally { classElement = saved; }
    }

    private BinaryExpressionNode Assign(SyntaxNode target, SyntaxNode value) => Context.Binary(target, K.EqualsToken, value);
    private static NodeList? FilterModifiers(NodeList? modifiers, params K[] kinds) => modifiers is null ? null
        : new(modifiers.Where(m => !kinds.Contains(m.Kind)).ToArray(), modifiers.Pos, modifiers.End);
    private static EmitRange PastModifiers(SyntaxNode node) => new(node is PropertyDeclarationNode or MethodDeclarationNode ? node.DeclarationName!.Pos
        : node.ModifierList is { Count: > 0 } modifiers ? modifiers[^1].End : node.Pos, node.End);
}

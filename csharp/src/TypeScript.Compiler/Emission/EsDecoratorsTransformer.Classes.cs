using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class EsDecoratorsTransformer
{
    private async ValueTask<SyntaxNode> ClassAsync(SyntaxNode node)
    {
        if (!Decorated(node))
        {
            var heritage = await VisitListAsync(Heritage(node));
            Enter(ScopeKind.Class);
            NodeList? members;
            try { members = await VisitListAsync(Members(node)); }
            finally { Exit(); }
            var result = Context.Clone(node);
            if (result is ClassDeclarationNode declaration)
            { declaration.Modifiers = Modifiers(declaration.Modifiers); declaration.TypeParameters = null; declaration.HeritageClauses = heritage; declaration.Members = members; }
            else
            { var expression = (ClassExpressionNode)result; expression.Modifiers = Modifiers(expression.Modifiers); expression.TypeParameters = null; expression.HeritageClauses = heritage; expression.Members = members; }
            return result;
        }
        if (node is ClassExpressionNode) { var expression = await TransformClassAsync(node); Context.SetOriginal(expression, node); return expression; }

        var original = Context.MostOriginal(node);
        if (original is not (ClassDeclarationNode or ClassExpressionNode)) original = node;
        bool isExport = SemanticSyntax.HasModifier(node, K.ExportKeyword), isDefault = SemanticSyntax.HasModifier(node, K.DefaultKeyword);
        if (node.DeclarationName is null) node = named.InjectClassName(node, original.DeclarationName is { } name ? Context.StringLiteralFromNode(name) : String("default"u8));
        var iife = await TransformClassAsync(node);
        List<SyntaxNode> statements = [];
        if (isExport && isDefault)
        {
            SyntaxNode value = iife;
            if (node.DeclarationName is not null)
            {
                var declaration = F.NewVariableDeclaration(Context.GetLocalName(node), null, null, iife);
                Context.SetOriginal(declaration, node);
                statements.Add(F.NewVariableStatement(null, F.NewVariableDeclarationList(new([declaration]), NodeFlags.Let)));
                value = Context.GetDeclarationName(node);
            }
            var export = F.NewExportAssignment(null, false, null, value);
            Context.SetOriginal(export, node);
            Finish(export, node);
            statements.Add(export);
        }
        else
        {
            var name = Context.GetLocalName(node, allowSourceMaps: true);
            var declaration = F.NewVariableDeclaration(name, null, null, iife);
            Context.SetOriginal(declaration, node);
            var statement = F.NewVariableStatement(Modifiers(node.ModifierList, m => m.Kind != K.ExportKeyword), F.NewVariableDeclarationList(new([declaration]), NodeFlags.Let));
            Context.SetOriginal(statement, node);
            Context.SetCommentRange(statement, new(node.Pos, node.End));
            statements.Add(statement);
            if (isExport)
            {
                var export = F.NewExportDeclaration(null, false, F.NewNamedExports(new([F.NewExportSpecifier(false, null, name)])), null, null);
                Context.SetOriginal(export, node);
                statements.Add(export);
            }
        }
        return statements.Count == 1 ? statements[0] : F.NewSyntaxList(statements.ToArray());
    }

    private async ValueTask<SyntaxNode> TransformClassAsync(SyntaxNode node)
    {
        Context.StartVariableEnvironment();
        if (node.DeclarationName is null && !named.HasAssignedName(node) && DecoratorSyntax.ClassDecorated(false, node))
            node = named.InjectClassName(node, String(""u8));
        var reference = Context.GetLocalName(node, ignoreAssignedName: true);
        var info = CreateClassInfo(node);
        List<SyntaxNode> definitions = [], leading = [], trailing = [];
        var decorators = await DecoratorsAsync(node);
        bool transformStatic = decorators.Count != 0 && info.HasStaticPrivate;
        if (decorators.Count != 0)
        {
            info.Decorators = Unique("_classDecorators"u8);
            info.Descriptor = Unique("_classDescriptor"u8);
            info.ExtraInitializers = Unique("_classExtraInitializers"u8);
            definitions.AddRange([Let(info.Decorators, Array(decorators)), Let(info.Descriptor), Let(info.ExtraInitializers, Array([])), Let(info.This!)]);
            forceStaticPrivate |= transformStatic;
        }
        NodeList? heritage = null;
        var extendsClause = Heritage(node)?.OfType<HeritageClauseNode>().FirstOrDefault(h => h.Token == K.ExtendsKeyword);
        var extendsType = extendsClause?.Types?.FirstOrDefault() as ExpressionWithTypeArgumentsNode;
        if (extendsType is not null && await VisitAsync(extendsType.Expression) is { } baseExpression)
        {
            info.Super = Unique("_classSuper"u8);
            if (NamedEvaluation.SkipOuter(baseExpression) is ClassExpressionNode { Name: null } or FunctionExpressionNode { Name: null } or ArrowFunctionNode)
                baseExpression = Context.Binary(F.NewNumericLiteral("0"u8, TokenFlags.None), K.CommaToken, baseExpression);
            definitions.Add(Let(info.Super, baseExpression));
            var type = Context.Clone(extendsType);
            type.Expression = info.Super; type.TypeArguments = null;
            var clause = Context.Clone(extendsClause!);
            clause.Types = new([type]);
            heritage = new([clause]);
        }
        SyntaxNode receiver = info.This ?? This();
        NodeList members;
        Enter(ScopeKind.Class, info);
        try
        {
            leading.Add(Metadata(info.Metadata, info.Super));
            List<SyntaxNode> first = [];
            foreach (var member in Members(node))
            {
                if (member is ConstructorDeclarationNode) first.Add(member);
                else first.AddRange(await VisitArrayAsync([member]));
            }
            List<SyntaxNode> second = [];
            foreach (var member in first)
            {
                if (member is ConstructorDeclarationNode) second.AddRange(await VisitArrayAsync([member]));
                else second.Add(member);
            }
            members = new(second.ToArray(), Members(node).Pos, Members(node).End);
            var capture = new OuterThisRewriter(Context, Cancellation);
            foreach (var expression in pending) leading.Add(F.NewExpressionStatement(await capture.VisitAsync(expression)));
            if (capture.Name is not null) definitions.Insert(0, Let(capture.Name, This()));
            pending.Clear();
        }
        finally { Exit(); }
        SyntaxNode? syntheticConstructor = null;
        if (info.PendingInstance.Count != 0 && DecoratorSyntax.Constructor(node) is null)
        {
            List<SyntaxNode> statements = [];
            if (extendsType?.Expression is { } baseType && NamedEvaluation.SkipOuter(baseType).Kind != K.NullKeyword)
                statements.Add(F.NewExpressionStatement(F.NewCallExpression(F.NewKeywordExpression(K.SuperKeyword), null, null,
                    new([F.NewSpreadElement(F.NewIdentifier("arguments"u8))]), NodeFlags.None)));
            statements.AddRange(PrepareConstructor(info));
            syntheticConstructor = F.NewConstructorDeclaration(null, null, new([]), null, null, F.NewBlock(new(statements.ToArray()), true));
        }
        if (info.StaticExtra is not null) definitions.Add(Let(info.StaticExtra, Array([])));
        if (info.InstanceExtra is not null) definitions.Add(Let(info.InstanceExtra, Array([])));
        foreach (bool isStatic in new[] { true, false })
            foreach (var (member, data) in info.Members)
            {
                if (Static(member) != isStatic) continue;
                definitions.Add(Let(data.Decorators));
                if (data.Initializers is not null) definitions.Add(Let(data.Initializers, Array([])));
                if (data.ExtraInitializers is not null) definitions.Add(Let(data.ExtraInitializers, Array([])));
                if (data.Descriptor is not null) definitions.Add(Let(data.Descriptor));
            }
        foreach (var group in info.Decorations) leading.AddRange(group);
        if (info.Descriptor is not null)
        {
            var descriptor = Assign(info.Descriptor, Object([Property("value"u8, receiver)]));
            var context = Object([Property("kind"u8, String("class"u8)), Property("name"u8, Access(receiver, "name"u8)), Property("metadata"u8, info.Metadata)]);
            var statement = F.NewExpressionStatement(Decorate(Null(), descriptor, info.Decorators!, context, Null(), info.ExtraInitializers!));
            Context.SetSourceMapRange(statement, PastDecorators(node));
            leading.Add(statement);
            leading.Add(F.NewExpressionStatement(Assign(reference, Assign(info.This!, Access(info.Descriptor, "value"u8)))));
        }
        leading.Add(SymbolMetadata(receiver, info.Metadata));
        foreach (var initializer in info.PendingStatic)
        {
            var statement = F.NewExpressionStatement(initializer);
            Context.SetSourceMapRange(statement, Context.GetSourceMapRange(initializer));
            trailing.Add(statement);
        }
        info.PendingStatic.Clear();
        if (info.ExtraInitializers is not null)
        {
            var statement = F.NewExpressionStatement(Run(receiver, info.ExtraInitializers));
            Context.SetSourceMapRange(statement, node.DeclarationName is { } name ? new(name.Pos, name.End) : PastDecorators(node));
            trailing.Add(statement);
        }
        if (leading.Count != 0 && trailing.Count != 0 && !info.HasStaticInitializers) { leading.AddRange(trailing); trailing.Clear(); }
        List<SyntaxNode> all = members.ToList();
        if (leading.Count != 0)
        {
            var block = StaticBlock(leading);
            if (transformStatic) Context.SetFlags(block, EmitFlags.TransformPrivateStaticElements);
            int index = all.FindIndex(named.IsNameBlock);
            all.Insert(index + 1, block);
        }
        if (syntheticConstructor is not null) all.Add(syntheticConstructor);
        if (trailing.Count != 0) all.Add(StaticBlock(trailing));
        members = new(all.ToArray(), members.Pos, members.End);
        var declarations = Context.EndVariableEnvironment();
        var classExpression = F.NewClassExpression(null, decorators.Count != 0 ? null : node.DeclarationName as IdentifierNode, null, heritage, members);
        Context.SetOriginal(classExpression, node);
        if (decorators.Count != 0)
        {
            var block = StaticBlock([F.NewExpressionStatement(Assign(info.This!, This()))], multiLine: false);
            Context.SetClassThis(block, info.This!);
            classExpression.Members = new([block, .. members], members.Pos, members.End);
            Context.SetClassThis(classExpression, info.This!);
            definitions.Add(F.NewVariableStatement(null, F.NewVariableDeclarationList(new([F.NewVariableDeclaration(reference, null, null, classExpression)]), NodeFlags.None)));
            definitions.Add(F.NewReturnStatement(Assign(reference, info.This!)));
        }
        else definitions.Add(F.NewReturnStatement(classExpression));
        if (transformStatic)
        {
            Context.AddFlags(classExpression, EmitFlags.TransformPrivateStaticElements);
            foreach (var member in Members(classExpression))
                if (Static(member) && (member.DeclarationName is PrivateIdentifierNode || AutoAccessor(member))) Context.AddFlags(member, EmitFlags.TransformPrivateStaticElements);
        }
        return Iife(Context.MergeEnvironment(new(definitions.ToArray()), declarations)!);
    }

    private sealed class OuterThisRewriter(EmitContext context, CancellationToken cancellation) : SyntaxRewriter(context, cancellation)
    {
        internal IdentifierNode? Name;
        protected override ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
        {
            if (node.Kind == K.ThisKeyword)
                return ValueTask.FromResult<SyntaxNode?>(Name ??= Context.NewUniqueName("_outerThis"u8, new(GeneratedIdentifierFlags.Optimistic)));
            if (node is FunctionDeclarationNode or FunctionExpressionNode or MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode)
                return ValueTask.FromResult<SyntaxNode?>(node);
            return VisitEachChildAsync(node);
        }
    }
}

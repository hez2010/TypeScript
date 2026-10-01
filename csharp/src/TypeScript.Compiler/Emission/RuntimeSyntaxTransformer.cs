using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

/// <summary>Lowers enums, namespaces and parameter properties after type erasure.</summary>
internal sealed class RuntimeSyntaxTransformer(EmitContext context, CompilerOptions options, Checker checker, CancellationToken cancellation = default)
    : SyntaxRewriter(context, cancellation)
{
    private SyntaxNode? current, parent, scope, sourceFile;
    private Dictionary<Utf8String, SyntaxNode> firstDeclarations = [];
    private ModuleDeclarationNode? currentNamespace;
    private EnumDeclarationNode? currentEnum;
    private SyntaxNode? constructorSuper;
    private SyntaxNode[] propertyAssignments = [];
    private NodeFactory F => Context.Factory;
    private bool PreserveConstEnums => options.PreserveConstEnums == true || options.IsolatedModules == true || options.VerbatimModuleSyntax == true;

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        var saved = (current, parent, scope, sourceFile, firstDeclarations);
        parent = current;
        current = node;
        if (node is SourceFileNode or BlockNode or ModuleBlockNode or CaseBlockNode)
        {
            scope = node;
            firstDeclarations = [];
            if (node is SourceFileNode)
                sourceFile = node;
        }
        else if (node is FunctionDeclarationNode or ClassDeclarationNode or VariableStatementNode)
            RecordDeclaration(node);
        try
        {
            var result = await VisitWorkerAsync(node);
            if (node == constructorSuper && result is not null)
                return F.NewSyntaxList([result, .. propertyAssignments]);
            return result;
        }
        finally { (current, parent, scope, sourceFile, firstDeclarations) = saved; }
    }

    private async ValueTask<SyntaxNode?> VisitWorkerAsync(SyntaxNode node)
    {
        switch (node.Kind)
        {
            case K.PublicKeyword: case K.PrivateKeyword: case K.ProtectedKeyword: case K.ReadonlyKeyword: case K.OverrideKeyword:
                return null;
        }
        switch (node)
        {
            case EnumDeclarationNode declaration:
                if (SemanticSyntax.HasModifier(node, K.ConstKeyword) && !PreserveConstEnums)
                    return NotEmitted(node);
                return await TransformContainerAsync(declaration);
            case ModuleDeclarationNode declaration:
                if (Context.ParseNode(node) is ModuleDeclarationNode parsed && (Binder.ModuleState(parsed) is 0 || Binder.ModuleState(parsed) == 1 && !PreserveConstEnums))
                    return NotEmitted(node);
                return await TransformContainerAsync(declaration);
            case ClassDeclarationNode declaration:
                return await ClassAsync(declaration);
            case ClassExpressionNode expression:
                return await ClassAsync(expression);
            case ConstructorDeclarationNode constructor:
                return await ConstructorAsync(constructor);
            case FunctionDeclarationNode function when IsNamespaceExport(function):
                var updated = Context.Clone(function);
                updated.Modifiers = FilterModifiers(function.Modifiers, K.ExportKeyword);
                updated = (FunctionDeclarationNode)(await VisitEachChildAsync(updated))!;
                return F.NewSyntaxList([updated, ExportStatement(function)]);
            case VariableStatementNode statement when IsNamespaceExport(statement):
                return await ExportVariablesAsync(statement);
            case ImportDeclarationNode or ExportDeclarationNode or ImportClauseNode when currentNamespace is not null && scope is not null and not BlockNode:
                return null;
            case ImportEqualsDeclarationNode import:
                if (currentNamespace is not null && scope is not null
                    && (scope is not BlockNode && import.ModuleReference is ExternalModuleReferenceNode
                        || scope is BlockNode && import.ModuleReference is not ExternalModuleReferenceNode))
                    return null;
                return await ImportEqualsAsync(import);
            case IdentifierNode name:
                return TransformSyntax.IsIdentifierReference(name, parent) ? await ExpressionIdentifierAsync(name) : name;
            case ShorthandPropertyAssignmentNode shorthand:
                return await ShorthandAsync(shorthand);
            default:
                return await VisitEachChildAsync(node);
        }
    }

    private SyntaxNode NotEmitted(SyntaxNode node)
    {
        var result = EmitContext.CopyRange(F.NewNotEmittedStatement(), node);
        Context.SetOriginal(result, node);
        Context.SetCommentRange(result, new(node.Pos, node.End));
        return result;
    }

    private void RecordDeclaration(SyntaxNode node)
    {
        var pending = new Stack<SyntaxNode>();
        pending.Push(node);
        while (pending.TryPop(out node!))
        {
            if (node is VariableStatementNode statement)
                pending.Push(statement.DeclarationList!);
            else if (node is VariableDeclarationListNode list)
                foreach (var item in list.Declarations ?? new([])) pending.Push(item);
            else if (node is BindingPatternNode pattern)
                foreach (var item in pattern.Elements ?? new([])) pending.Push(item);
            else if (node.DeclarationName is IdentifierNode name)
                firstDeclarations.TryAdd(name.Text, node);
            else if (node.DeclarationName is BindingPatternNode binding)
                pending.Push(binding);
        }
    }

    private bool IsNamespaceExport(SyntaxNode node) => currentNamespace is not null && scope is not BlockNode
        && SemanticSyntax.HasModifier(node, K.ExportKeyword);
    private IdentifierNode ContainerName(SyntaxNode node) => Context.NewGeneratedNameForNode(node);
    private SyntaxNode ExportReference(SyntaxNode node) => IsNamespaceExport(node)
        ? Context.GetExternalModuleOrNamespaceExportName(ContainerName(currentNamespace!), node, allowSourceMaps: true)
        : Context.GetDeclarationName(node, allowSourceMaps: true);
    private SyntaxNode Assignment(SyntaxNode left, SyntaxNode right) => Context.Binary(left, K.EqualsToken, right);
    private static NodeList? FilterModifiers(NodeList? modifiers, params K[] excluded) => modifiers is null ? null
        : new(modifiers.Where(n => !excluded.Contains(n.Kind)).ToArray(), modifiers.Pos, modifiers.End);

    private bool AddContainerVariable(List<SyntaxNode> statements, SyntaxNode node)
    {
        RecordDeclaration(node);
        if (node.DeclarationName is not IdentifierNode name || firstDeclarations.GetValueOrDefault(name.Text) != node)
            return false;
        var declaration = F.NewVariableDeclaration(Context.GetLocalName(node, allowSourceMaps: true), null, null, null);
        var declarations = F.NewVariableDeclarationList(new([declaration]), scope == sourceFile ? NodeFlags.None : NodeFlags.Let);
        var excluded = new List<K> { K.Decorator, K.PublicKeyword, K.PrivateKeyword, K.ProtectedKeyword, K.AbstractKeyword,
            K.OverrideKeyword, K.ConstKeyword, K.DeclareKeyword, K.ReadonlyKeyword, K.InKeyword, K.OutKeyword };
        if (currentNamespace is not null) excluded.Add(K.ExportKeyword);
        var statement = F.NewVariableStatement(FilterModifiers(node.ModifierList, excluded.ToArray()), declarations);
        Context.SetOriginal(declaration, node);
        Context.SetOriginal(statement, node);
        Context.SetSourceMapRange(node is EnumDeclarationNode ? declarations : statement, new(node.Pos, node.End));
        Context.SetCommentRange(statement, new(node.Pos, node.End));
        Context.AddFlags(statement, EmitFlags.NoTrailingComments);
        statements.Add(statement);
        return true;
    }

    private async ValueTask<SyntaxNode> TransformContainerAsync(SyntaxNode node)
    {
        List<SyntaxNode> statements = [];
        bool added = AddContainerVariable(statements, node);
        SyntaxNode argument = Context.Binary(ExportReference(node), K.BarBarToken,
            Assignment(ExportReference(node), F.NewObjectLiteralExpression(new([]), false)));
        if (IsNamespaceExport(node))
            argument = Assignment(Context.GetLocalName(node, allowSourceMaps: true), argument);
        var parameterName = ContainerName(node);
        var name = node.DeclarationName!;
        Context.SetSourceMapRange(parameterName, new(name.Pos, name.End));
        var parameter = F.NewParameterDeclaration(null, null, parameterName, null, null, null);
        var body = node is EnumDeclarationNode enumeration ? await EnumBodyAsync(enumeration) : await ModuleBodyAsync((ModuleDeclarationNode)node);
        var function = F.NewFunctionExpression(null, null, null, null, new([parameter]), null, null, body);
        var call = F.NewCallExpression(F.NewParenthesizedExpression(function), null, null, new([argument]), NodeFlags.None);
        var statement = F.NewExpressionStatement(call);
        Context.SetOriginal(statement, node);
        Context.AssignCommentAndSourceMapRanges(statement, node);
        if (added && (options.Module != ModuleKind.System || scope != sourceFile))
            Context.AddFlags(statement, EmitFlags.NoLeadingComments);
        statements.Add(statement);
        return F.NewSyntaxList(statements.ToArray());
    }

    private async ValueTask<BlockNode> EnumBodyAsync(EnumDeclarationNode node)
    {
        var saved = currentEnum;
        currentEnum = node;
        try
        {
            node = (EnumDeclarationNode)(await VisitEachChildAsync(node))!;
            List<SyntaxNode> statements = [];
            foreach (EnumMemberNode member in node.Members ?? new([]))
            {
                var value = Context.ParseNode(member) is EnumMemberNode parsed
                    ? await checker.GetEnumMemberValueForEmitAsync(parsed, Cancellation) : default;
                var expression = Context.ConstantExpression(value.Value) ?? member.Initializer ?? Context.VoidZero();
                var access = F.NewElementAccessExpression(ContainerName(node), null, PropertyExpression(member.Name!), NodeFlags.None);
                Context.AssignCommentAndSourceMapRanges(access, access.ArgumentExpression!);
                Context.AddFlags(access, EmitFlags.NoComments | EmitFlags.NoNestedComments | EmitFlags.NoSourceMap | EmitFlags.NoNestedSourceMaps);
                expression = Assignment(access, expression);
                if (value.Value is double || value.Value is not Utf8String && !value.IsSyntacticallyString)
                    expression = Assignment(F.NewElementAccessExpression(ContainerName(node), null, expression, NodeFlags.None), PropertyExpression(member.Name!));
                var statement = F.NewExpressionStatement(expression);
                Context.AssignCommentAndSourceMapRanges(expression, member);
                Context.AssignCommentAndSourceMapRanges(statement, member);
                statements.Add(statement);
            }
            return F.NewBlock(new(statements.ToArray(), node.Members?.Pos ?? -1, node.Members?.End ?? -1), true);
        }
        finally { currentEnum = saved; }
    }

    private SyntaxNode PropertyExpression(SyntaxNode name) => name switch
    {
        PrivateIdentifierNode => F.NewIdentifier(default),
        ComputedPropertyNameNode computed => computed.Expression!,
        IdentifierNode id => F.NewStringLiteral(id.Text, TokenFlags.None),
        StringLiteralNode literal => F.NewStringLiteral(literal.Text, TokenFlags.None),
        NumericLiteralNode literal => F.NewNumericLiteral(literal.Text, TokenFlags.None),
        _ => name
    };

    private async ValueTask<BlockNode> ModuleBodyAsync(ModuleDeclarationNode node)
    {
        var saved = (currentNamespace, scope, firstDeclarations);
        currentNamespace = node;
        firstDeclarations = [];
        Context.StartVariableEnvironment();
        NodeList? statements = null;
        EmitRange blockRange = new(0, 0), statementRange = new(0, 0);
        try
        {
            if (node.Body is ModuleBlockNode)
            {
                node = (ModuleDeclarationNode)(await VisitEachChildAsync(node))!;
                var body = (ModuleBlockNode)node.Body!;
                statements = body.Statements;
                statementRange = new(statements!.Pos, statements.End);
                blockRange = new(body.Pos, body.End);
            }
            else if (node.Body is ModuleDeclarationNode nested)
            {
                statements = await VisitListAsync(new([nested]));
                while (nested.Body is ModuleDeclarationNode inner) nested = inner;
                statementRange = new(-1, ((ModuleBlockNode)nested.Body!).Statements!.End);
            }
        }
        finally { (currentNamespace, scope, firstDeclarations) = saved; }
        statements = Context.MergeEnvironment(statements, Context.EndVariableEnvironment());
        var result = F.NewBlock(new(statements?.ToArray() ?? [], statementRange.Pos, statementRange.End), true);
        result.Pos = blockRange.Pos;
        result.End = blockRange.End;
        if (node.Body is not ModuleBlockNode)
            Context.AddFlags(result, EmitFlags.NoComments);
        return result;
    }

    private async ValueTask<SyntaxNode?> ImportEqualsAsync(ImportEqualsDeclarationNode node)
    {
        if (node.ModuleReference is ExternalModuleReferenceNode)
            return await VisitEachChildAsync(node);
        var reference = Context.CreateExpressionFromEntityName(node.ModuleReference!);
        Context.SetFlags(reference, EmitFlags.NoComments | EmitFlags.NoNestedComments);
        if (IsNamespaceExport(node))
        {
            var assignment = Assignment(Context.GetNamespaceMemberName(ContainerName(currentNamespace!), node.Name!, allowSourceMaps: true), reference);
            Context.SetOriginal(assignment, node);
            Context.SetSourceMapRange(assignment, new(node.Pos, node.End));
            var statement = EmitContext.CopyRange(F.NewExpressionStatement(assignment), node);
            Context.SetOriginal(statement, node);
            Context.SetSourceMapRange(statement, new(node.Pos, node.End));
            return statement;
        }
        var declaration = F.NewVariableDeclaration(node.Name, null, null, reference);
        Context.SetOriginal(declaration, node);
        var declarations = F.NewVariableDeclarationList(new([declaration]), NodeFlags.None);
        var result = F.NewVariableStatement(node.Modifiers is null ? null : new(node.Modifiers.Where(n => n.Kind == K.ExportKeyword).ToArray(), node.Modifiers.Pos, node.Modifiers.End), declarations);
        Context.SetOriginal(result, node);
        Context.AssignCommentAndSourceMapRanges(result, node);
        return result;
    }

    private async ValueTask<SyntaxNode?> ExportVariablesAsync(VariableStatementNode node)
    {
        List<SyntaxNode> expressions = [];
        foreach (VariableDeclarationNode declaration in node.DeclarationList!.Declarations!)
        {
            if (declaration.Initializer is null) continue;
            SyntaxNode expression;
            if (declaration.Name is BindingPatternNode)
                expression = await new DestructuringFlattener(Context, VisitAsync, NamespaceAssignment, Cancellation)
                    .AssignmentAsync((await VisitAsync(declaration))!, false);
            else
            {
                expression = Assignment(declaration.Name!, declaration.Initializer);
                Context.SetOriginal(expression, declaration);
                Context.AssignCommentAndSourceMapRanges(expression, declaration);
            }
            expressions.Add(expression);
        }
        if (expressions.Count == 0) return null;
        var statement = F.NewExpressionStatement(Context.InlineExpressions(expressions));
        Context.SetOriginal(statement, node);
        Context.AssignCommentAndSourceMapRanges(statement, node);
        var saved = current;
        current = statement;
        try { return await VisitEachChildAsync(statement); }
        finally { current = saved; }
    }

    private SyntaxNode NamespaceAssignment(IdentifierNode name, SyntaxNode value, EmitRange range)
    {
        var assignment = Assignment(Context.GetNamespaceMemberName(ContainerName(currentNamespace!), name, allowSourceMaps: true), value);
        assignment.Pos = range.Pos;
        assignment.End = range.End;
        return assignment;
    }

    private SyntaxNode ExportStatement(SyntaxNode node)
    {
        var expression = Assignment(Context.GetExternalModuleOrNamespaceExportName(ContainerName(currentNamespace!), node, allowSourceMaps: true), Context.GetLocalName(node));
        Context.SetSourceMapRange(expression, new(node.DeclarationName?.Pos ?? node.Pos, node.End));
        var statement = F.NewExpressionStatement(expression);
        Context.SetSourceMapRange(statement, new(-1, node.End));
        return statement;
    }

    private static ParameterDeclarationNode[] ParameterProperties(ConstructorDeclarationNode? constructor) => constructor?.Parameters?
        .OfType<ParameterDeclarationNode>().Where(p => p.Modifiers?.Any(m => m.Kind is K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.ReadonlyKeyword or K.OverrideKeyword) == true).ToArray() ?? [];

    private async ValueTask<SyntaxNode> ClassAsync(SyntaxNode node)
    {
        bool exported = node is ClassDeclarationNode && IsNamespaceExport(node);
        var updated = Context.Clone(node);
        var modified = (IModifiedNode)updated;
        if (exported || node is ClassExpressionNode)
            modified.Modifiers = FilterModifiers(modified.Modifiers, K.ExportKeyword, K.DefaultKeyword);
        var originalMembers = node is ClassDeclarationNode declaration ? declaration.Members : ((ClassExpressionNode)node).Members;
        if (updated is ClassDeclarationNode { Name: null } anonymous && (exported || DecoratorSyntax.ChildDecorated(options.ExperimentalDecorators == true, node)))
            anonymous.Name = ContainerName(node);
        updated = (await VisitEachChildAsync(updated))!;
        var members = updated is ClassDeclarationNode cd ? cd.Members : ((ClassExpressionNode)updated).Members;
        var parameters = ParameterProperties(originalMembers?.OfType<ConstructorDeclarationNode>().FirstOrDefault());
        List<SyntaxNode> properties = [];
        foreach (var parameter in parameters)
            if (parameter.Name is IdentifierNode name)
            {
                var property = F.NewPropertyDeclaration(null, Context.Clone(name), null, null, null);
                Context.SetOriginal(property, parameter);
                properties.Add(property);
            }
        if (properties.Count != 0)
        {
            members = new([.. properties, .. members ?? new([])], originalMembers!.Pos, originalMembers.End);
            if (updated is ClassDeclarationNode result) result.Members = members;
            else ((ClassExpressionNode)updated).Members = members;
        }
        return exported ? F.NewSyntaxList([updated, ExportStatement(node)]) : updated;
    }

    private async ValueTask<SyntaxNode> ConstructorAsync(ConstructorDeclarationNode node)
    {
        var saved = (constructorSuper, propertyAssignments);
        var updated = Context.Clone(node);
        updated.Modifiers = await VisitListAsync(node.Modifiers);
        updated.Parameters = await VisitParametersAsync(node.Parameters);
        var properties = ParameterProperties(node);
        List<SyntaxNode> assignments = [];
        foreach (var parameter in properties)
            if (parameter.Name is IdentifierNode name)
            {
                var property = Context.Clone(name);
                Context.AddFlags(property, EmitFlags.NoComments | EmitFlags.NoSourceMap);
                var local = Context.Clone(name);
                Context.AddFlags(local, EmitFlags.NoComments);
                var statement = F.NewExpressionStatement(Assignment(F.NewPropertyAccessExpression(F.NewKeywordExpression(K.ThisKeyword), null, property, NodeFlags.None), local));
                Context.SetOriginal(statement, parameter);
                Context.AddFlags(statement, EmitFlags.StartOnNewLine);
                assignments.Add(statement);
            }
        constructorSuper = properties.Length == 0 ? null : FindSuperStatement((node.Body as BlockNode)?.Statements);
        propertyAssignments = assignments.ToArray();
        try
        {
            updated.Body = await VisitFunctionBodyAsync(node.Body);
            if (properties.Length != 0 && updated.Body is BlockNode body)
            {
                var block = Context.Clone(body);
                block.MultiLine = true;
                if (constructorSuper is null)
                {
                    var statements = body.Statements?.ToArray() ?? [];
                    int prologue = 0;
                    while (prologue < statements.Length && statements[prologue] is ExpressionStatementNode { Expression: StringLiteralNode }) prologue++;
                    block.Statements = new([.. statements[..prologue], .. assignments, .. statements[prologue..]], body.Statements?.Pos ?? -1, body.Statements?.End ?? -1);
                }
                updated.Body = block;
            }
            return updated;
        }
        finally { (constructorSuper, propertyAssignments) = saved; }
    }

    private static SyntaxNode? FindSuperStatement(NodeList? statements)
    {
        var pending = new Stack<SyntaxNode>((statements ?? new([])).Reverse());
        while (pending.TryPop(out var statement))
        {
            if (statement is ExpressionStatementNode expression)
            {
                var value = expression.Expression;
                while (value is ParenthesizedExpressionNode parenthesized) value = parenthesized.Expression;
                if (value is CallExpressionNode { Expression.Kind: K.SuperKeyword }) return statement;
            }
            else if (statement is TryStatementNode { TryBlock: BlockNode block })
                foreach (var child in (block.Statements ?? new([])).Reverse()) pending.Push(child);
        }
        return null;
    }

    private async ValueTask<SyntaxNode> ExpressionIdentifierAsync(IdentifierNode node)
    {
        if ((currentEnum is not null || currentNamespace is not null) && Context.GetAutoGenerateInfo(node) is null
            && (Context.GetFlags(node) & EmitFlags.LocalName) == 0 && Context.ParseNode(node) is IdentifierNode parsed
            && await checker.GetReferencedExportContainerForEmitAsync(parsed, false, Cancellation) is { } container
            && container is EnumDeclarationNode or ModuleDeclarationNode)
        {
            var name = Context.Clone(node);
            Context.SetFlags(name, EmitFlags.NoComments | EmitFlags.NoSourceMap);
            var result = Context.GetNamespaceMemberName(ContainerName(container), name, allowSourceMaps: true);
            Context.AssignCommentAndSourceMapRanges(result, node);
            return result;
        }
        return node;
    }

    private async ValueTask<SyntaxNode> ShorthandAsync(ShorthandPropertyAssignmentNode node)
    {
        var name = await ExpressionIdentifierAsync((IdentifierNode)node.Name!);
        var initializer = await VisitAsync(node.ObjectAssignmentInitializer);
        if (name != node.Name)
        {
            if (initializer is not null)
                name = F.NewBinaryExpression(null, name, null, node.EqualsToken ?? F.NewToken(K.EqualsToken), initializer);
            var result = EmitContext.CopyRange(F.NewPropertyAssignment(null, node.Name, null, null, name), node);
            Context.SetOriginal(result, node);
            Context.AssignCommentAndSourceMapRanges(result, node);
            return result;
        }
        var updated = Context.Clone(node);
        updated.Modifiers = null;
        updated.PostfixToken = null;
        updated.Type = null;
        updated.ObjectAssignmentInitializer = initializer;
        return updated;
    }
}

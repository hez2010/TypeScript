using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class CommonJsModuleTransformer
{
    private SyntaxNode ExportExpression(SyntaxNode name, SyntaxNode value, EmitRange? location = null, bool live = false)
    {
        SyntaxNode result = live
            ? Call(Property(F.NewIdentifier("Object"u8), "defineProperty"u8), [F.NewIdentifier("exports"u8), Context.StringLiteralFromNode(name),
                Object([AssignmentProperty("enumerable"u8, F.NewKeywordExpression(K.TrueKeyword)), AssignmentProperty("get"u8,
                    F.NewFunctionExpression(null, null, null, null, new([]), null, null, F.NewBlock(new([F.NewReturnStatement(value)]), false)))])])
            : Assign(ExportAccess(Context.Clone(name)), value);
        if (location is { } range) Context.SetCommentRange(result, range);
        return result;
    }

    private SyntaxNode ExportStatement(SyntaxNode name, SyntaxNode value, SyntaxNode? location = null, bool comments = false, bool live = false)
    {
        var result = F.NewExpressionStatement(ExportExpression(name, value, live: live));
        if (location is not null) Context.SetCommentRange(result, new(location.Pos, location.End));
        Context.AddFlags(result, EmitFlags.StartOnNewLine | (comments ? EmitFlags.None : EmitFlags.NoComments));
        return result;
    }

    private async ValueTask AppendDeclarationAsync(List<SyntaxNode> statements, SyntaxNode declaration, HashSet<Utf8String>? seen = null, bool live = false)
    {
        if (info.ExportEquals is not null || declaration.DeclarationName is not IdentifierNode
            || !info.Bindings.ContainsKey(Context.MostOriginal(declaration))) return;
        var name = Context.GetDeclarationName(declaration);
        if (!info.Specifiers.TryGetValue(name.Text, out var specifiers)) return;
        seen ??= [];
        var value = await IdentifierAsync(name);
        foreach (var specifier in specifiers)
            if (specifier.Name is StringLiteralNode || seen.Add(SyntaxNameText.Get(specifier.Name!)))
                statements.Add(ExportStatement(specifier.Name!, value, specifier.Name, live: live));
    }

    private async ValueTask AppendClassOrFunctionAsync(List<SyntaxNode> statements, SyntaxNode declaration)
    {
        if (info.ExportEquals is not null) return;
        HashSet<Utf8String> seen = [];
        if (Exported(declaration))
        {
            var name = SemanticSyntax.HasModifier(declaration, K.DefaultKeyword) ? F.NewIdentifier("default"u8) : Context.GetDeclarationName(declaration);
            seen.Add(name.Text);
            statements.Add(ExportStatement(name, Context.GetLocalName(declaration), declaration));
        }
        await AppendDeclarationAsync(statements, declaration, seen);
    }

    private async ValueTask AppendVariablesAsync(List<SyntaxNode> statements, VariableDeclarationListNode list, bool iteration = false)
    {
        if (info.ExportEquals is not null) return;
        Stack<SyntaxNode> work = new((list.Declarations ?? new([])).Reverse());
        while (work.TryPop(out var declaration))
        {
            Cancellation.ThrowIfCancellationRequested();
            if (declaration.DeclarationName is BindingPatternNode pattern)
            {
                for (int i = (pattern.Elements?.Count ?? 0) - 1; i >= 0; i--) work.Push(pattern.Elements![i]);
            }
            else if (declaration.DeclarationName is { } name && Context.GetAutoGenerateInfo(name) is null
                && (declaration is not VariableDeclarationNode variable || variable.Initializer is not null || iteration))
                await AppendDeclarationAsync(statements, declaration);
        }
    }

    private SyntaxNode Require(SyntaxNode node)
    {
        var name = ModuleUtilities.ExternalModuleName(F, node);
        return Call(F.NewIdentifier("require"u8), name is null ? [] : [ModuleUtilities.RewriteSpecifier(Context, name, options)!]);
    }
    private SyntaxNode ImportStar(SyntaxNode value) => Context.HelperCall(EmitHelpers.ImportStar, "__importStar"u8, [value]);
    private SyntaxNode ImportDefault(SyntaxNode value) => Context.HelperCall(EmitHelpers.ImportDefault, "__importDefault"u8, [value]);
    private SyntaxNode ImportHelper(ImportDeclarationNode node, SyntaxNode value)
    {
        var clause = node.ImportClause;
        if (clause?.NamedBindings is NamespaceImportNode) return ImportStar(value);
        var named = (clause?.NamedBindings as NamedImportsNode)?.Elements;
        int defaults = named?.OfType<ImportSpecifierNode>().Count(specifier => SyntaxNameText.Get(specifier.PropertyName ?? specifier.Name!) == "default"u8) ?? 0;
        if (named is not null && (defaults > 0 && defaults != named.Count || named.Count != defaults && clause?.Name is not null)) return ImportStar(value);
        return clause?.Name is not null || defaults > 0 ? ImportDefault(value) : value;
    }

    private async ValueTask<SyntaxNode> ImportAsync(ImportDeclarationNode node)
    {
        if (node.ImportClause is not { } clause) return Located(F.NewExpressionStatement(Require(node)), node);
        List<SyntaxNode> declarations = [];
        var space = clause.NamedBindings as NamespaceImportNode;
        declarations.Add(F.NewVariableDeclaration(space is not null && clause.Name is null ? Context.Clone(space.Name!) : Context.NewGeneratedNameForNode(node),
            null, null, ImportHelper(node, Require(node))));
        if (space is not null && clause.Name is not null) declarations.Add(F.NewVariableDeclaration(Context.Clone(space.Name!), null, null, Context.NewGeneratedNameForNode(node)));
        List<SyntaxNode> statements = [Located(F.NewVariableStatement(null, F.NewVariableDeclarationList(new(declarations.ToArray()), NodeFlags.Const)), node)];
        HashSet<Utf8String> seen = [];
        if (clause.Name is not null) await AppendDeclarationAsync(statements, clause, seen);
        if (space is not null) await AppendDeclarationAsync(statements, space, seen);
        else if (clause.NamedBindings is NamedImportsNode imports)
            foreach (var import in imports.Elements ?? new([])) await AppendDeclarationAsync(statements, import, seen, true);
        return Many(statements);
    }

    private async ValueTask<SyntaxNode> ImportEqualsAsync(ImportEqualsDeclarationNode node)
    {
        if (node.ModuleReference is not ExternalModuleReferenceNode) throw new InvalidOperationException("Internal import aliases must be transformed before module emission");
        SyntaxNode statement = Exported(node)
            ? F.NewExpressionStatement(ExportExpression(node.Name!, Require(node), new(node.Pos, node.End)))
            : F.NewVariableStatement(null, F.NewVariableDeclarationList(new([F.NewVariableDeclaration(Context.Clone(node.Name!), null, null, Require(node))]), NodeFlags.Const));
        List<SyntaxNode> statements = [Located(statement, node)];
        await AppendDeclarationAsync(statements, node);
        return Many(statements);
    }

    private SyntaxNode? ExportDeclaration(ExportDeclarationNode node)
    {
        if (node.ModuleSpecifier is null) return null;
        if (node.ExportClause is NamedExportsNode named)
        {
            var target = Context.NewGeneratedNameForNode(node);
            List<SyntaxNode> statements = [Located(F.NewVariableStatement(null, F.NewVariableDeclarationList(
                new([F.NewVariableDeclaration(target, null, null, Require(node))]), NodeFlags.None)), node)];
            foreach (var specifier in named.Elements?.OfType<ExportSpecifierNode>() ?? [])
            {
                var key = specifier.PropertyName ?? specifier.Name!;
                var valueTarget = SyntaxNameText.Get(key) == "default"u8 ? ImportDefault(target) : target;
                SyntaxNode name = specifier.Name is StringLiteralNode ? Context.StringLiteralFromNode(specifier.Name) : Context.GetExportName(specifier);
                var value = key is StringLiteralNode ? (SyntaxNode)F.NewElementAccessExpression(valueTarget, null, key, NodeFlags.None)
                    : F.NewPropertyAccessExpression(valueTarget, null, key, NodeFlags.None);
                statements.Add(Located(F.NewExpressionStatement(ExportExpression(name, value, live: true)), specifier));
            }
            return Many(statements);
        }
        if (node.ExportClause is NamespaceExportNode space)
        {
            var name = space.Name is StringLiteralNode ? Context.StringLiteralFromNode(space.Name) : Context.Clone(space.Name!);
            return Located(F.NewExpressionStatement(ExportExpression(name, ImportStar(Require(node)))), node);
        }
        return Located(F.NewExpressionStatement(Context.HelperCall(EmitHelpers.ExportStar, "__exportStar"u8, [Require(node), F.NewIdentifier("exports"u8)])), node);
    }

    private async ValueTask<SyntaxNode> FunctionAsync(FunctionDeclarationNode node)
    {
        if (!Exported(node)) return (await VisitEachChildAsync(node))!;
        var result = Context.Clone(node);
        result.Modifiers = RemoveExport(node.Modifiers);
        result.Name = Context.GetDeclarationName(node);
        result.TypeParameters = null; result.Type = null; result.FullSignature = null;
        return (await VisitEachChildAsync(result))!;
    }
    private async ValueTask<SyntaxNode> ClassAsync(ClassDeclarationNode node)
    {
        var result = node;
        if (Exported(node))
        {
            result = Context.Clone(node); result.Modifiers = RemoveExport(node.Modifiers);
            result.Name = Context.GetDeclarationName(node); result.TypeParameters = null;
        }
        List<SyntaxNode> statements = [(await VisitEachChildAsync(result))!];
        await AppendClassOrFunctionAsync(statements, node);
        return Many(statements);
    }

    private async ValueTask<SyntaxNode> VariableAsync(VariableStatementNode node)
    {
        List<SyntaxNode> statements = [];
        if (Exported(node))
        {
            List<SyntaxNode> variables = [], expressions = [];
            NodeList? modifiers = null;
            foreach (var declaration in node.DeclarationList!.Declarations?.OfType<VariableDeclarationNode>() ?? [])
            {
                if (declaration.Name is IdentifierNode && (Context.GetFlags(declaration.Name) & EmitFlags.LocalName) != 0)
                {
                    modifiers ??= RemoveExport(node.Modifiers);
                    var variable = declaration;
                    if (declaration.Initializer is not null)
                    {
                        variable = Context.Clone(declaration); variable.ExclamationToken = null; variable.Type = null;
                        variable.Initializer = ExportExpression(declaration.Name, (await VisitAsync(declaration.Initializer))!);
                    }
                    PushVariable(variable);
                }
                else if (declaration.Name is not BindingPatternNode && declaration.Initializer is ArrowFunctionNode or FunctionExpressionNode or ClassExpressionNode)
                {
                    PushVariable(F.NewVariableDeclaration(declaration.Name, declaration.ExclamationToken, declaration.Type, await VisitAsync(declaration.Initializer)));
                    var access = F.NewPropertyAccessExpression(F.NewIdentifier("exports"u8), null, declaration.Name, NodeFlags.None);
                    Context.AssignCommentAndSourceMapRanges(access, declaration.Name!);
                    PushExpression(Assign(access, Context.Clone(declaration.Name!)));
                }
                else if (declaration.Initializer is not null)
                {
                    if (declaration.Name is BindingPatternNode)
                    {
                        var assignment = Located(Assign(await Context.BindingAssignmentAsync(declaration.Name, Cancellation), declaration.Initializer), declaration);
                        var savedCurrent = current; current = assignment;
                        try { PushExpression(await DestructuringAsync((BinaryExpressionNode)assignment)); }
                        finally { current = savedCurrent; }
                    }
                    else
                    {
                        var access = F.NewPropertyAccessExpression(F.NewIdentifier("exports"u8), null, declaration.Name, NodeFlags.None);
                        Context.AssignCommentAndSourceMapRanges(access, declaration.Name!);
                        PushExpression((await VisitAsync(Assign(access, declaration.Initializer)))!);
                    }
                }
            }
            CommitVariables(); CommitExpressions();

            void CommitVariables()
            {
                if (variables.Count == 0) return;
                var list = Context.Clone(node.DeclarationList!); list.Declarations = new(variables.ToArray());
                var statement = Context.Clone(node); statement.Modifiers = modifiers; statement.DeclarationList = list;
                if (statements.Count > 0) Context.AddFlags(statement, EmitFlags.NoComments);
                statements.Add(statement); variables.Clear();
            }
            void CommitExpressions()
            {
                if (expressions.Count == 0) return;
                var statement = Located(F.NewExpressionStatement(Context.InlineExpressions(expressions)), node, false);
                if (statements.Count > 0) Context.AddFlags(statement, EmitFlags.NoComments);
                statements.Add(statement); expressions.Clear();
            }
            void PushVariable(SyntaxNode variable) { CommitExpressions(); variables.Add(variable); }
            void PushExpression(SyntaxNode expression) { CommitVariables(); expressions.Add(expression); }
        }
        else statements.Add((await VisitEachChildAsync(node))!);
        await AppendVariablesAsync(statements, node.DeclarationList!);
        return Many(statements);
    }
}

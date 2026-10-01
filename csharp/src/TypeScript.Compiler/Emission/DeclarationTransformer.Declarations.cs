using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class DeclarationTransformer
{
    private async ValueTask<SyntaxNode> TypeAliasAsync(TypeAliasDeclarationNode node)
    {
        needsDeclare = false;
        var result = Context.Clone(node);
        result.Modifiers = Modifiers(node);
        result.TypeParameters = await VisitListAsync(node.TypeParameters);
        result.Type = await VisitAsync(node.Type);
        return result;
    }

    private async ValueTask<SyntaxNode> InterfaceAsync(InterfaceDeclarationNode node)
    {
        var result = Context.Clone(node);
        result.Modifiers = Modifiers(node);
        result.TypeParameters = await VisitListAsync(node.TypeParameters);
        result.HeritageClauses = await VisitListAsync(node.HeritageClauses);
        result.Members = await VisitListAsync(node.Members);
        return result;
    }

    private async ValueTask<SyntaxNode?> FunctionAsync(FunctionDeclarationNode node)
    {
        if (await checker.IsExpandoFunctionForEmitAsync(node, Cancellation)) await ReportExpandoErrorsAsync(node);
        return await SignatureAsync(node);
    }

    private async ValueTask<SyntaxNode?> VariablesAsync(VariableStatementNode node)
    {
        if (node.DeclarationList is not VariableDeclarationListNode list) return null;
        List<SyntaxNode> imports = [], ordinary = [];
        foreach (var declaration in list.Declarations ?? new([]))
            (checker.Symbols.Binding(source)?.CommonJSModuleIndicator is not null && RequireVariable(declaration) ? imports : ordinary).Add(declaration);
        var visitedImports = await VisitListAsync(new(imports.ToArray()));
        var declarations = await VisitListAsync(new(ordinary.ToArray()));
        if (declarations is null || declarations.Count == 0) return visitedImports is { Count: > 0 } ? F.NewSyntaxList(visitedImports.ToArray()) : null;
        var updatedList = Context.Clone(list);
        updatedList.Declarations = declarations;
        if ((list.Flags & NodeFlags.Using) != 0) updatedList.Flags = NodeFlags.Const;
        var result = Context.Clone(node);
        result.Modifiers = Modifiers(node);
        result.DeclarationList = updatedList;
        return visitedImports is { Count: > 0 } ? F.NewSyntaxList([.. visitedImports, result]) : result;
    }

    private async ValueTask<SyntaxNode?> VariableAsync(VariableDeclarationNode node)
    {
        if (checker.Symbols.Binding(source)?.CommonJSModuleIndicator is not null && RequireVariable(node)) return RequireImport(node);
        if (node.Name is BindingPatternNode pattern && pattern.DescendantsAndSelf().Any(child => child is BindingElementNode { Initializer: not null }))
        {
            List<SyntaxNode> results = [];
            foreach (var child in pattern.DescendantsAndSelf().OfType<BindingElementNode>())
                if (child.Name is not (null or BindingPatternNode) && await BindingVisibleAsync(child))
                    results.Add(F.NewVariableDeclaration(child.Name, null, await EnsureTypeAsync(child), null));
            return results.Count == 0 ? null : F.NewSyntaxList(results.ToArray());
        }
        suppressDiagnostics = true;
        var result = Context.Clone(node);
        result.Name = await BindingNameAsync(node.Name);
        result.ExclamationToken = null;
        result.Type = await EnsureTypeAsync(node);
        result.Initializer = await InitializerAsync(node);
        return result;
    }

    private async ValueTask<SyntaxNode> EnumAsync(EnumDeclarationNode node)
    {
        List<SyntaxNode> members = [];
        foreach (var member in (node.Members ?? new([])).Cast<EnumMemberNode>())
        {
            if (StripInternal(member)) continue;
            var value = await checker.GetEnumMemberValueForEmitAsync(member, Cancellation);
            if (options.IsolatedDeclarations == true && member.Initializer is not null && value.HasExternalReferences && member.Name is not ComputedPropertyNameNode)
                Report(member, Messages.Enum_member_initializers_must_be_computable_without_references_to_external_symbols_with_isolatedDeclarations);
            var updated = Context.Clone(member);
            updated.Initializer = Context.ConstantExpression(value.Value);
            members.Add(updated);
        }
        var result = Context.Clone(node);
        result.Modifiers = Modifiers(node);
        result.Members = new(members.ToArray());
        return result;
    }

    private async ValueTask<SyntaxNode> ModuleAsync(ModuleDeclarationNode node)
    {
        var result = Context.Clone(node);
        result.Modifiers = Modifiers(node);
        needsDeclare = false;
        if (node.Keyword != K.GlobalKeyword && node.Name is not StringLiteralNode) result.Keyword = K.NamespaceKeyword;
        result.Attributes = (TypeLiteralNode?)await VisitAsync(node.Attributes);
        if (node.Body is ModuleBlockNode block)
        {
            var saved = (needsScopeMarker, hasScopeMarker);
            try
            {
                needsScopeMarker = hasScopeMarker = false;
                var statements = await ReplaceLateAsync(await VisitListAsync(block.Statements));
                if ((node.Flags & NodeFlags.Ambient) != 0) needsScopeMarker = false;
                if ((node.Flags & NodeFlags.Reparsed) == 0 && node.Keyword != K.GlobalKeyword && !hasScopeMarker
                    && !(statements?.Any(statement => statement is ExportDeclarationNode or ExportAssignmentNode) ?? false))
                    statements = needsScopeMarker ? new([.. statements ?? new([]), ModuleUtilities.EmptyExport(F)])
                        : new((statements ?? new([])).Select(StripExport).ToArray(), statements?.Pos ?? -1, statements?.End ?? -1);
                var updatedBlock = Context.Clone(block);
                updatedBlock.Statements = statements;
                result.Body = updatedBlock;
            }
            finally { (needsScopeMarker, hasScopeMarker) = saved; }
        }
        else if (node.Body is { } inner)
        {
            await VisitAsync(inner);
            lateStatements.Remove(Context.MostOriginal(inner), out var body);
            result.Body = body;
        }
        return result;
    }

    private SyntaxNode StripExport(SyntaxNode node)
    {
        if (node is ImportEqualsDeclarationNode || node is not IModifiedNode || (CombinedFlags(node) & ModifierFlags.Default) != 0
            || !SemanticSyntax.HasModifier(node, K.ExportKeyword)) return node;
        var result = Context.Clone(node);
        ((IModifiedNode)result).Modifiers = FromModifierFlags(CombinedFlags(node) & ~ModifierFlags.Export);
        return result;
    }

    private async ValueTask<NodeList> ClassMembersAsync(SyntaxNode node, NodeList? members)
    {
        List<SyntaxNode> properties = [];
        var saved = tracker.DiagnosticSelector;
        try
        {
            var constructor = members?.OfType<ConstructorDeclarationNode>().FirstOrDefault(candidate => candidate.Body is not null);
            foreach (var parameter in constructor?.Parameters ?? new([]))
            {
                if ((CombinedFlags(parameter) & ModifierFlags.ParameterPropertyModifier) == 0 || StripInternal(parameter)) continue;
                var p = (ParameterDeclarationNode)parameter;
                tracker.DiagnosticSelector = result => DeclarationDiagnostics.ForNode(p, result);
                if (p.Name is IdentifierNode)
                    properties.Add(Located(F.NewPropertyDeclaration(Modifiers(p), p.Name, p.QuestionToken, await EnsureTypeAsync(p), await InitializerAsync(p)), p));
                else if (p.Name is BindingPatternNode pattern)
                    foreach (var element in pattern.DescendantsAndSelf().OfType<BindingElementNode>())
                        if (element.Name is IdentifierNode)
                            properties.Add(F.NewPropertyDeclaration(Modifiers(p), element.Name, p.QuestionToken, await EnsureTypeAsync(element), await InitializerAsync(element)));
            }
        }
        finally { tracker.DiagnosticSelector = saved; }
        List<SyntaxNode> result = [];
        if (members?.Any(member => SemanticSyntax.Name(member) is PrivateIdentifierNode) == true)
            result.Add(F.NewPropertyDeclaration(null, F.NewPrivateIdentifier("#private"u8), null, null, null));
        var indexes = await checker.CreateLateBoundIndexesForEmitAsync(node, enclosing, Context, BuilderFlags, tracker, InternalFlags, Cancellation);
        await FlushInferenceAsync();
        if (indexes is not null) result.AddRange(indexes);
        result.AddRange(properties);
        if ((node.Flags & NodeFlags.JavaScriptFile) != 0) result.AddRange(await ThisPropertiesAsync(node, members));
        if (await VisitListAsync(members) is { } visited) result.AddRange(visited);
        return new(result.ToArray());
    }

    private async ValueTask<SyntaxNode> ClassAsync(ClassDeclarationNode node)
    {
        tracker.ErrorName = node.Name;
        tracker.PushErrorFallbackNode(node);
        try
        {
            var result = Context.Clone(node);
            result.Modifiers = Modifiers(node);
            result.TypeParameters = await TypeParametersAsync(node, node.TypeParameters);
            result.Members = await ClassMembersAsync(node, node.Members);
            var extends = node.HeritageClauses?.OfType<HeritageClauseNode>().FirstOrDefault(clause => clause.Token == K.ExtendsKeyword);
            if (extends?.Types?.FirstOrDefault() is ExpressionWithTypeArgumentsNode { Expression: { } expression } baseType
                && !ConstantEvaluator.EntityName(expression) && expression.Kind != K.NullKeyword)
            {
                tracker.ReportInferenceFallback(expression);
                var name = Context.NewUniqueName(Utf8String.FromString((node.Name?.Text.ToString() ?? "default") + "_base"), new(GeneratedIdentifierFlags.Optimistic));
                tracker.DiagnosticSelector = _ => new(baseType, Messages.X_extends_clause_of_exported_class_0_has_or_is_using_private_name_1, node.Name);
                var type = await checker.CreateExpressionTypeForEmitAsync(expression, node, Context, BuilderFlags, tracker, InternalFlags, Cancellation);
                await FlushInferenceAsync();
                var statement = F.NewVariableStatement(needsDeclare ? new([F.NewToken(K.DeclareKeyword)]) : null,
                    F.NewVariableDeclarationList(new([F.NewVariableDeclaration(name, null, type, null)]), NodeFlags.Const));
                var updatedBase = Context.Clone(baseType);
                updatedBase.Expression = name;
                updatedBase.TypeArguments = await VisitListAsync(baseType.TypeArguments);
                var updatedExtends = Context.Clone(extends!);
                updatedExtends.Types = new([updatedBase]);
                result.HeritageClauses = new([updatedExtends, .. await VisitListAsync(node.HeritageClauses) ?? new([])]);
                return F.NewSyntaxList([statement, result]);
            }
            result.HeritageClauses = await VisitListAsync(node.HeritageClauses);
            return result;
        }
        finally { tracker.PopErrorFallbackNode(); }
    }

    private async ValueTask<SyntaxNode> ClassExpressionAsync(ClassExpressionNode node, IdentifierNode name, NodeList? modifiers)
    {
        var saved = (enclosing, classExpression);
        try
        {
            enclosing = node; classExpression = true;
            var members = await ClassMembersAsync(node, node.Members);
            return F.NewClassDeclaration(modifiers, name, await TypeParametersAsync(node, node.TypeParameters), await VisitListAsync(node.HeritageClauses), members);
        }
        finally { (enclosing, classExpression) = saved; }
    }

    private async ValueTask<SyntaxNode?> ImportEqualsAsync(ImportEqualsDeclarationNode node)
    {
        if (!await checker.IsDeclarationVisibleAsync(node, Cancellation)) return null;
        if (node.ModuleReference is ExternalModuleReferenceNode) hasModuleIndicator = true;
        else if (node.ModuleReference is { } reference)
        {
            var saved = tracker.DiagnosticSelector;
            try { tracker.DiagnosticSelector = result => DeclarationDiagnostics.ForNode(node, result); await CheckEntityAsync(reference); }
            finally { tracker.DiagnosticSelector = saved; }
        }
        return node;
    }

    private async ValueTask<SyntaxNode?> ImportAsync(ImportDeclarationNode node)
    {
        var clause = node.ImportClause;
        ImportClauseNode? updatedClause = null;
        if (clause is not null)
        {
            var name = clause.Name is not null && await checker.IsDeclarationVisibleAsync(clause, Cancellation) ? clause.Name : null;
            SyntaxNode? bindings = null;
            if (clause.NamedBindings is NamespaceImportNode space && await checker.IsDeclarationVisibleAsync(space, Cancellation)) bindings = space;
            else if (clause.NamedBindings is NamedImportsNode imports)
            {
                List<SyntaxNode> elements = [];
                foreach (var element in imports.Elements ?? new([])) if (await checker.IsDeclarationVisibleAsync(element, Cancellation)) elements.Add(element);
                if (elements.Count > 0) { var updated = Context.Clone(imports); updated.Elements = new(elements.ToArray()); bindings = updated; }
            }
            if (name is not null || bindings is not null)
            {
                updatedClause = Context.Clone(clause);
                updatedClause.Name = name; updatedClause.NamedBindings = bindings;
                if (updatedClause.PhaseModifier == K.DeferKeyword) updatedClause.PhaseModifier = K.Unknown;
            }
            else
            {
                if (clause.NamedBindings is not NamedImportsNode || !await checker.IsImportRequiredByAugmentationAsync(node, Cancellation)) return null;
                if (options.IsolatedDeclarations == true)
                    Report(node, Messages.Declaration_emit_for_this_file_requires_preserving_this_import_for_augmentations_This_is_not_supported_with_isolatedDeclarations);
            }
        }
        hasModuleIndicator = true;
        return F.Update(node, F.NewImportDeclaration(K.ImportDeclaration, node.Modifiers, updatedClause, node.ModuleSpecifier, node.Attributes));
    }

    private async ValueTask<SyntaxNode?> ExportAssignmentAsync(SyntaxNode input, SyntaxNode assignment, SyntaxNode expression, bool exportEquals)
    {
        if (StripInternal(input)) return null;
        if (input.Parent is SourceFileNode) hasModuleIndicator = true;
        hasScopeMarker = true;
        if (expression is IdentifierNode && input.Parent is SourceFileNode or ModuleBlockNode)
            return Located(F.NewExportAssignment(null, exportEquals, null, expression), input);
        var unwrapped = Unwrap(expression);
        if (exportEquals && (source.Flags & NodeFlags.JavaScriptFile) != 0)
            while (unwrapped is BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken, Right: { } assigned }) unwrapped = Unwrap(assigned);
        var nameText = unwrapped is PropertyAccessExpressionNode ? Utf8String.Empty : SemanticSyntax.Name(unwrapped) is IdentifierNode declarationName ? declarationName.Text
            : unwrapped is IdentifierNode identifier ? identifier.Text : Utf8String.Empty;
        IdentifierNode name;
        if (nameText.Length != 0 && nameText != "default"u8)
            name = await checker.IsNameResolvableForEmitAsync(enclosing, nameText, Cancellation)
                ? Context.NewUniqueName(nameText, new(GeneratedIdentifierFlags.Optimistic)) : F.NewIdentifier(nameText);
        else name = Context.NewUniqueName(exportEquals && (source.Flags & NodeFlags.JavaScriptFile) != 0 ? "_exports"u8 : "_default"u8, new(GeneratedIdentifierFlags.Optimistic));
        cjsAssignmentName = name;
        var modifiers = needsDeclare ? new NodeList([F.NewToken(K.DeclareKeyword)]) : null;
        if (unwrapped is ClassExpressionNode type)
        {
            var declaration = Located(await ClassExpressionAsync(type, name, modifiers), input);
            var export = F.NewExportAssignment(null, exportEquals, null, name);
            Context.AddFlags(export, EmitFlags.NoComments);
            return F.NewSyntaxList([export, declaration]);
        }
        if (unwrapped.HasFunctionSignature)
        {
            var declaration = Located(await PromoteFunctionAsync(unwrapped, name, modifiers, (assignment as ITypedNode)?.Type), input);
            var export = F.NewExportAssignment(null, exportEquals, null, name);
            Context.AddFlags(export, EmitFlags.NoComments);
            return F.NewSyntaxList([export, declaration]);
        }
        var saved = SaveContext();
        tracker.PushErrorFallbackNode(assignment);
        try
        {
            tracker.DiagnosticSelector = _ => new(input, Messages.Default_export_of_the_module_has_or_is_using_private_name_0);
            var initializer = Primitive(unwrapped) ? await checker.CreateLiteralConstForEmitAsync(assignment, Context, Cancellation) : null;
            var inferredType = initializer is null ? await EnsureTypeAsync(assignment) : null;
            var statement = Located(F.NewVariableStatement(modifiers,
                F.NewVariableDeclarationList(new([F.NewVariableDeclaration(name, null, inferredType, initializer)]), NodeFlags.Const)), input);
            return F.NewSyntaxList([statement, F.NewExportAssignment(null, exportEquals, null, name)]);
        }
        finally { tracker.PopErrorFallbackNode(); RestoreContext(saved); }
    }

    private async ValueTask<SyntaxNode> PromoteFunctionAsync(SyntaxNode node, IdentifierNode name, NodeList? modifiers, SyntaxNode? fullSignature)
    {
        var signature = (IFunctionSignature)node;
        fullSignature = (node as IFullSignatureNode)?.FullSignature ?? fullSignature;
        if (fullSignature is not null)
            return F.NewVariableStatement(modifiers, F.NewVariableDeclarationList(new([F.NewVariableDeclaration(name, null, await VisitAsync(fullSignature), null)]), NodeFlags.Const));
        return F.NewFunctionDeclaration(modifiers, null, name, await TypeParametersAsync(node, signature.TypeParameters),
            await ParametersAsync(node, signature.Parameters), await EnsureTypeAsync(node), null, null);
    }
}

using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly HashSet<SourceFileNode> checkedFiles = [];
    private readonly Dictionary<SourceFileNode, List<SyntaxNode>> deferredSourceNodes = [];
    private bool sourceCheckCancelled;
    internal SyntaxNode? CurrentSourceNode { get; private set; }
    internal int CheckedFileCount => checkedFiles.Count;
    internal Action<SyntaxNode>? BeforeSourceElement { get; set; }

    internal async ValueTask CheckProgramAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        RequireUsable();
        foreach (var file in program.Symbols.Program.SourceFiles)
        {
            if (SkipProgramFile(file.Syntax))
                continue;
            await CheckSourceFileAsync(file.Syntax, cancellation).ConfigureAwait(false);
        }
    }

    internal bool SkipProgramFile(SourceFileNode file) => NoCheck || file.CheckJsDirective?.Enabled == false
        || file.ScriptKind is not (ScriptKind.TS or ScriptKind.TSX)
            && (file.ScriptKind is not (ScriptKind.JS or ScriptKind.JSX)
                || file.CheckJsDirective?.Enabled != true && program.Symbols.Program.Configuration.Options.CheckJs == false)
        || file.IsDeclarationFile && program.Symbols.Program.Configuration.Options.SkipLibCheck == true
        || program.Symbols.Program.GetFile(file.FileName)!.Library
            && program.Symbols.Program.Configuration.Options.SkipDefaultLibCheck == true;

    internal async ValueTask CheckSourceFileAsync(SourceFileNode file, CancellationToken cancellation = default)
    {
        await queryGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            RequireNode(file);
            RequireUsable();
            if (checkedFiles.Contains(file))
                return;
            SourceFileGrammar(file);
            foreach (var statement in file.Statements!)
                await CheckSourceElementAsync(statement, cancellation).ConfigureAwait(false);
            if (deferredSourceNodes.TryGetValue(file, out var deferred))
                for (int i = 0; i < deferred.Count; i++)
                    await CheckDeferredSourceAsync(deferred[i], cancellation).ConfigureAwait(false);
            foreach (var diagnostic in DeferredIterationDiagnostics.Where(d => SemanticSyntax.Source(d.Node) == file).ToArray())
                await Iteration.NotIterableAsync(
                    diagnostic.Node,
                    diagnostic.Type,
                    diagnostic.Async,
                    cancellation,
                    diagnostic.Related).ConfigureAwait(false);
            await CheckMissingPropertiesAsync(file, cancellation).ConfigureAwait(false);
            CheckDeferredDeclarationNames(file, cancellation);
            if (program.Symbols.Binding(file)?.IsModule == true)
            {
                await CheckExternalExportsAsync(file, cancellation).ConfigureAwait(false);
                RegisterUnused(file);
            }
            CheckUnusedSource(file, cancellation);
            cancellation.ThrowIfCancellationRequested();
            checkedFiles.Add(file);
            if (deferred is not null)
                foreach (var node in deferred)
                    DeferredExpressions.Remove(node);
            deferredSourceNodes.Remove(file);
            DeferredIterationDiagnostics.RemoveAll(d => SemanticSyntax.Source(d.Node) == file);
            reportedUnreachable.Clear();
        }
        catch (OperationCanceledException)
        {
            sourceCheckCancelled = true;
            throw;
        }
        finally
        {
            queryGate.Release();
        }
    }

    private void RequireUsable()
    {
        if (sourceCheckCancelled)
            throw new InvalidOperationException("A cancelled source check requires a new checker");
    }

    private async ValueTask CheckSourceElementAsync(SyntaxNode? node, CancellationToken cancellation)
    {
        if (node is null)
            return;
        await Task.CompletedTask.ConfigureAwait(
            RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var previous = CurrentSourceNode;
        bool previousUnreachable = withinUnreachable;
        CurrentSourceNode = node;
        Instantiation.Engine.ResetExpressionCount();
        try
        {
            BeforeSourceElement?.Invoke(node);
            if ((node.Flags & NodeFlags.HasJSDoc) != 0)
                foreach (var comment in await SemanticSyntax.Source(node)!.GetDocumentationAsync(node, cancellation).ConfigureAwait(false))
                    foreach (var part in comment.DescendantsAndSelf())
                        if (part is JSDocLinkNode or JSDocLinkCodeNode or JSDocLinkPlainNode
                            && ((INamedNode)part).Name is IdentifierNode or QualifiedNameNode)
                            await DocumentationMemberAsync(((INamedNode)part).Name!, cancellation).ConfigureAwait(false);
            if (!withinUnreachable && program.Symbols.Program.Configuration.Options.AllowUnreachableCode != true
                && await CheckUnreachableAsync(node, cancellation).ConfigureAwait(false))
                withinUnreachable = true;
            switch (node)
            {
                case BlockNode block:
                    RegisterUnused(block);
                    AmbientStatement(block);
                    bool disabled = FlowTypes.AnalysisDisabled;
                    try
                    {
                        foreach (var statement in block.Statements!)
                            await CheckSourceElementAsync(statement, cancellation).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (block.Parent is IFunctionSignature)
                            FlowTypes.AnalysisDisabled = disabled;
                    }
                    break;
                case VariableStatementNode variable:
                    ExportedDeclaration(variable, true);
                    await CheckSourceElementAsync(variable.DeclarationList, cancellation).ConfigureAwait(false);
                    break;
                case VariableDeclarationListNode declarations:
                    await CheckVariableListAsync(declarations, cancellation).ConfigureAwait(false);
                    foreach (var declaration in declarations.Declarations!)
                        await CheckSourceElementAsync(declaration, cancellation).ConfigureAwait(false);
                    break;
                case VariableDeclarationNode declaration:
                    await VariableGrammarAsync(declaration, cancellation).ConfigureAwait(false);
                    CheckDeclarationName(declaration);
                    await FunctionDeclarations.VariableAsync(declaration, cancellation).ConfigureAwait(false);
                    await CheckDisposableInitializerAsync(declaration, cancellation).ConfigureAwait(false);
                    await CheckMergedExportsAsync(declaration, cancellation).ConfigureAwait(false);
                    break;
                case BindingElementNode element:
                    CheckDeclarationName(element);
                    await FunctionDeclarations.VariableAsync(element, cancellation).ConfigureAwait(false);
                    await CheckMergedExportsAsync(element, cancellation).ConfigureAwait(false);
                    break;
                case ExpressionStatementNode expression:
                    AmbientStatement(expression);
                    await Expressions.CheckAsync(expression.Expression!, cancellation: cancellation).ConfigureAwait(false);
                    break;
                case FunctionDeclarationNode function:
                    CheckDeclarationName(function);
                    ExportedDeclaration(function, true);
                    await FunctionDeclarations.GrammarAsync(function, cancellation).ConfigureAwait(false);
                    await CheckFunctionDeclarationAsync(function, cancellation).ConfigureAwait(false);
                    await CheckFunctionOverloadsAsync(function, cancellation).ConfigureAwait(false);
                    await CheckSourceElementAsync(function.Body, cancellation).ConfigureAwait(false);
                    await CheckFunctionPathsAsync(function, cancellation).ConfigureAwait(false);
                    await CheckFullSignatureAsync(function, cancellation).ConfigureAwait(false);
                    if (function.Type is null && (function.Body is null || function.Body.Pos == function.Body.End))
                        await ReportImplicitAnyAsync(function, context.AnyType, cancellation).ConfigureAwait(false);
                    if (function.Type is null && SemanticSyntax.Generator(function) && function.Body is not null)
                        await Signatures.ReturnAsync(
                            await Signatures.FromDeclarationAsync(function, cancellation).ConfigureAwait(false),
                            cancellation).ConfigureAwait(false);
                    break;
                case ClassDeclarationNode:
                    await CheckClassSourceAsync(node, false, cancellation).ConfigureAwait(false);
                    break;
                case ConstructorDeclarationNode constructor:
                    await CheckConstructorSourceAsync(constructor, cancellation).ConfigureAwait(false);
                    break;
                case PropertyDeclarationNode property:
                    await CheckPropertySourceAsync(property, cancellation).ConfigureAwait(false);
                    break;
                case MethodDeclarationNode method:
                    await CheckMethodSourceAsync(method, cancellation).ConfigureAwait(false);
                    break;
                case GetAccessorDeclarationNode or SetAccessorDeclarationNode:
                    await CheckAccessorSourceAsync(node, cancellation).ConfigureAwait(false);
                    break;
                case ClassStaticBlockDeclarationNode block:
                    DeclarationModifiers(block);
                    await CheckSourceElementAsync(block.Body, cancellation).ConfigureAwait(false);
                    break;
                case { Kind: SyntaxKind.SemicolonClassElement }:
                    break;
                case ReturnStatementNode statement:
                    await CheckReturnSourceAsync(statement, cancellation).ConfigureAwait(false);
                    break;
                case IfStatementNode condition:
                    AmbientStatement(condition);
                    var conditionType = await CheckConditionAsync(condition.Expression!, cancellation).ConfigureAwait(false);
                    await KnownTruthyAsync(
                        conditionType,
                        condition.Expression!,
                        condition.ThenStatement,
                        cancellation).ConfigureAwait(false);
                    await CheckSourceElementAsync(condition.ThenStatement, cancellation).ConfigureAwait(false);
                    if (condition.ThenStatement?.Kind == SyntaxKind.EmptyStatement)
                        Error(condition.ThenStatement, DiagnosticCode.TheBodyOfAnIfStatementCannotBeTheEmptyStatement);
                    await CheckSourceElementAsync(condition.ElseStatement, cancellation).ConfigureAwait(false);
                    break;
                case WithStatementNode statement:
                    if (!AmbientStatement(statement) && (statement.Flags & NodeFlags.AwaitContext) != 0
                        && SemanticSyntax.Source(statement)?.ParseDiagnostics.Count == 0)
                        ErrorOnFirstToken(statement, DiagnosticCode.XWithStatementsAreNotAllowedInAnAsyncFunctionBlock);
                    await Expressions.CheckAsync(statement.Expression!, cancellation: cancellation).ConfigureAwait(false);
                    if (SemanticSyntax.Source(statement)?.ParseDiagnostics.Count == 0)
                    {
                        var diagnostic = CheckerDiagnostic.Create(
                            statement,
                            TypeScript.Compiler.Diagnostics.DiagnosticLocalization.GetMessage(
                                DiagnosticCode.TheWithStatementIsNotSupportedAllSymbolsInAWithBlockWillHaveTypeAny));
                        Error(statement, diagnostic with { Length = statement.Statement!.Pos - diagnostic.Start });
                    }
                    break;
                case WhileStatementNode loop:
                    AmbientStatement(loop);
                    await CheckConditionAsync(loop.Expression!, cancellation).ConfigureAwait(false);
                    await CheckSourceElementAsync(loop.Statement, cancellation).ConfigureAwait(false);
                    break;
                case DoStatementNode loop:
                    AmbientStatement(loop);
                    await CheckSourceElementAsync(loop.Statement, cancellation).ConfigureAwait(false);
                    await CheckConditionAsync(loop.Expression!, cancellation).ConfigureAwait(false);
                    break;
                case ForStatementNode loop:
                    RegisterUnused(loop);
                    AmbientStatement(loop);
                    if (loop.Initializer is VariableDeclarationListNode)
                        await CheckSourceElementAsync(loop.Initializer, cancellation).ConfigureAwait(false);
                    else if (loop.Initializer is not null)
                        await Expressions.CheckAsync(loop.Initializer, cancellation: cancellation).ConfigureAwait(false);
                    if (loop.Condition is not null)
                        await CheckConditionAsync(loop.Condition, cancellation).ConfigureAwait(false);
                    if (loop.Incrementor is not null)
                        await Expressions.CheckAsync(loop.Incrementor, cancellation: cancellation).ConfigureAwait(false);
                    await CheckSourceElementAsync(loop.Statement, cancellation).ConfigureAwait(false);
                    break;
                case ForInOrOfStatementNode loop when loop.Kind == SyntaxKind.ForOfStatement:
                    RegisterUnused(loop);
                    await ForEachGrammarAsync(loop, cancellation).ConfigureAwait(false);
                    if (loop.Initializer is VariableDeclarationListNode)
                        await CheckSourceElementAsync(loop.Initializer, cancellation).ConfigureAwait(false);
                    else
                    {
                        var elementType = await ForOfElementAsync(loop, cancellation).ConfigureAwait(false);
                        if (loop.Initializer is ObjectLiteralExpressionNode or ArrayLiteralExpressionNode)
                            await CheckDestructuringAsync(loop.Initializer, elementType, 0, false, cancellation).ConfigureAwait(false);
                        else
                        {
                            var left = await Expressions.CheckAsync(loop.Initializer!, cancellation: cancellation).ConfigureAwait(false);
                            AssignmentChecks.Reference(
                                loop.Initializer!,
                                DiagnosticCode.TheLeftHandSideOfAForOfStatementMustBeAVariableOrAPropertyAccess,
                                DiagnosticCode.TheLeftHandSideOfAForOfStatementMayNotBeAnOptionalPropertyAccess);
                            await CheckLiteralAssignableAsync(
                                elementType,
                                left,
                                loop.Initializer!,
                                loop.Expression!,
                                cancellation).ConfigureAwait(false);
                        }
                    }
                    await CheckSourceElementAsync(loop.Statement, cancellation).ConfigureAwait(false);
                    break;
                case ForInOrOfStatementNode loop:
                    RegisterUnused(loop);
                    await CheckForInSourceAsync(loop, cancellation).ConfigureAwait(false);
                    break;
                case SwitchStatementNode statement:
                    RegisterUnused(statement.CaseBlock!);
                    await CheckSwitchSourceAsync(statement, cancellation).ConfigureAwait(false);
                    break;
                case ThrowStatementNode statement:
                    if (!AmbientStatement(statement) && statement.Expression is IdentifierNode { Text.Length: 0 } missingThrow
                        && SemanticSyntax.Source(statement)?.ParseDiagnostics.Count == 0)
                        Error(statement, CheckerDiagnostic.Create(statement, Messages.Line_break_not_permitted_here) with
                        { Start = missingThrow.Pos, Length = 0 });
                    if (statement.Expression is not null)
                        await Expressions.CheckAsync(statement.Expression, cancellation: cancellation).ConfigureAwait(false);
                    break;
                case TryStatementNode statement:
                    AmbientStatement(statement);
                    await CheckSourceElementAsync(statement.TryBlock, cancellation).ConfigureAwait(false);
                    if (statement.CatchClause is { } clause)
                    {
                        if (clause.VariableDeclaration is { } caught)
                        {
                            CheckDeclarationName(caught);
                            await FunctionDeclarations.VariableAsync(caught, cancellation).ConfigureAwait(false);
                            if (SemanticSyntax.Source(caught)?.ParseDiagnostics.Count == 0)
                            {
                                if (caught.Type is not null)
                                {
                                    if (((await Nodes.FromNodeAsync(
                                        caught.Type,
                                        cancellation).ConfigureAwait(false)).Flags & TypeFlags.AnyOrUnknown) == 0)
                                        Error(caught.Type, DiagnosticCode.CatchClauseVariableTypeAnnotationMustBeAnyOrUnknownIfSpecified);
                                }
                                else if (caught.Initializer is not null)
                                    Error(caught.Initializer, DiagnosticCode.CatchClauseVariableCannotHaveAnInitializer);
                                else
                                {
                                    var binding = program.Symbols.Binding(clause)!;
                                    var locals = binding.Get(clause.Block!)?.Locals;
                                    foreach (TextSlice name in binding.Get(clause)?.Locals.Keys ?? [])
                                        if (locals?.GetValueOrDefault(name) is { ValueDeclaration: { } declaration } symbol
                                            && (symbol.Flags & SymbolFlags.BlockScopedVariable) != 0)
                                            Error(declaration, DiagnosticCode.CannotRedeclareIdentifier0InCatchClause, name);
                                }
                            }
                        }
                        await CheckSourceElementAsync(clause.Block, cancellation).ConfigureAwait(false);
                    }
                    await CheckSourceElementAsync(statement.FinallyBlock, cancellation).ConfigureAwait(false);
                    break;
                case LabeledStatementNode labeled:
                    LabelGrammar(labeled);
                    await CheckSourceElementAsync(labeled.Statement, cancellation).ConfigureAwait(false);
                    break;
                case { Kind: SyntaxKind.BreakStatement or SyntaxKind.ContinueStatement }:
                    if (!AmbientStatement(node))
                        JumpGrammar(node);
                    break;
                case TypeAliasDeclarationNode alias:
                    if (!AllowsBlockScopedDeclaration(alias.Parent) && SemanticSyntax.Source(alias)?.ParseDiagnostics.Count == 0)
                        Error(alias, DiagnosticCode.X0DeclarationsCanOnlyBeDeclaredInsideABlock, "type");
                    RegisterUnused(alias);
                    ExportedDeclaration(alias, false);
                    await CheckMergedExportsAsync(alias, cancellation).ConfigureAwait(false);
                    if (ReservedTypeName(alias.Name!.Text))
                        Error(alias.Name, DiagnosticCode.TypeAliasNameCannotBe0, alias.Name.Text);
                    if (alias.TypeParameters is not null)
                        foreach (TypeParameterDeclarationNode parameter in alias.TypeParameters)
                            await FunctionDeclarations.TypeParameterAsync(parameter, cancellation).ConfigureAwait(false);
                    if (alias.Type?.Kind == SyntaxKind.IntrinsicKeyword)
                    {
                        int count = alias.TypeParameters?.Count ?? 0;
                        if (!(count == 0 && alias.Name.Text == "BuiltinIteratorReturn"
                            || count == 1 && alias.Name.Text.Span is "Uppercase" or "Lowercase" or "Capitalize" or "Uncapitalize" or "NoInfer"))
                            Error(alias.Type, DiagnosticCode.TheIntrinsicKeywordCanOnlyBeUsedToDeclareCompilerProvidedIntrinsicTypes);
                        break;
                    }
                    await CheckedFunctionTypeAsync(alias.Type!, cancellation).ConfigureAwait(false);
                    break;
                case InterfaceDeclarationNode declaration:
                    await CheckInterfaceSourceAsync(declaration, cancellation).ConfigureAwait(false);
                    break;
                case ModuleDeclarationNode module:
                    await CheckNamespaceSourceAsync(module, cancellation).ConfigureAwait(false);
                    break;
                case ModuleBlockNode block:
                    bool previousAnalysis = FlowTypes.AnalysisDisabled;
                    try
                    {
                        foreach (var statement in block.Statements!)
                            await CheckSourceElementAsync(statement, cancellation).ConfigureAwait(false);
                    }
                    finally
                    {
                        FlowTypes.AnalysisDisabled = previousAnalysis;
                    }
                    break;
                case EnumDeclarationNode declaration:
                    await CheckEnumSourceAsync(declaration, cancellation).ConfigureAwait(false);
                    break;
                case EnumMemberNode member:
                    if (member.Name is PrivateIdentifierNode)
                        Error(member, DiagnosticCode.AnEnumMemberCannotBeNamedWithAPrivateIdentifier);
                    if (member.Initializer is not null)
                        await Expressions.CheckAsync(member.Initializer, cancellation: cancellation).ConfigureAwait(false);
                    break;
                case ImportDeclarationNode import:
                    await CheckImportSourceAsync(import, cancellation).ConfigureAwait(false);
                    break;
                case ImportEqualsDeclarationNode import:
                    await CheckImportEqualsSourceAsync(import, cancellation).ConfigureAwait(false);
                    break;
                case ExportDeclarationNode export:
                    await CheckExportSourceAsync(export, cancellation).ConfigureAwait(false);
                    break;
                case ExportAssignmentNode export:
                    await CheckExportAssignmentSourceAsync(export, cancellation).ConfigureAwait(false);
                    break;
                case NamespaceExportDeclarationNode:
                    // Namespace-export declarations are handled by the binder and alias resolver.
                    break;
                case PropertySignatureDeclarationNode property:
                    PropertySignatureGrammar(property);
                    if (property.Name is PrivateIdentifierNode)
                        Error(property, DiagnosticCode.PrivateIdentifiersAreNotAllowedOutsideClassBodies);
                    if (property.Name is ComputedPropertyNameNode computed)
                        await ComputedNameAsync(computed, cancellation).ConfigureAwait(false);
                    await FunctionDeclarations.VariableAsync(property, cancellation).ConfigureAwait(false);
                    break;
                case MethodSignatureDeclarationNode or CallSignatureDeclarationNode or ConstructSignatureDeclarationNode:
                    await FunctionDeclarations.GrammarAsync(node, cancellation).ConfigureAwait(false);
                    await FunctionDeclarations.CheckAsync(node, cancellation).ConfigureAwait(false);
                    if (node is MethodSignatureDeclarationNode)
                    {
                        await CheckMethodNameAsync(node, cancellation);
                        await CheckFunctionOverloadsAsync(node, cancellation);
                    }
                    break;
                case IndexSignatureDeclarationNode index:
                    await CheckIndexSignatureSourceAsync(index, cancellation).ConfigureAwait(false);
                    break;
                case { Kind: SyntaxKind.EmptyStatement or SyntaxKind.DebuggerStatement }:
                    AmbientStatement(node);
                    break;
                case { Kind: SyntaxKind.MissingDeclaration }:
                    break;
                default:
                    throw new InvalidOperationException($"Checker requires source-element checking for {node.Kind}");
            }
        }
        finally
        {
            CurrentSourceNode = previous;
            withinUnreachable = previousUnreachable;
        }
    }

    private async ValueTask<Type> CheckConditionAsync(SyntaxNode expression, CancellationToken cancellation)
    {
        var type = await Expressions.CheckAsync(expression, cancellation: cancellation).ConfigureAwait(false);
        await TruthinessAsync(type, expression, cancellation).ConfigureAwait(false);
        return type;
    }

    private bool AmbientStatement(SyntaxNode node)
    {
        if ((node.Flags & NodeFlags.Ambient) == 0 || SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return false;
        var owner = node.Parent is IFunctionSignature
            ? node
            : node.Parent is BlockNode or ModuleBlockNode or SourceFileNode ? node.Parent : null;
        if (owner is null || links.Nodes.Get(owner).HasReportedStatementInAmbientContext)
            return false;
        ErrorOnFirstToken(
            node,
            owner == node
                ? DiagnosticCode.AnImplementationCannotBeDeclaredInAmbientContexts
                : DiagnosticCode.StatementsAreNotAllowedInAmbientContexts);
        links.Nodes.Get(owner).HasReportedStatementInAmbientContext = true;
        return true;
    }

    private async ValueTask VariableGrammarAsync(VariableDeclarationNode node, CancellationToken cancellation)
    {
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        var flags = node.Flags | (node.Parent is VariableDeclarationListNode list ? list.Flags : 0);
        if (EmitModuleKind(node) < 4 && node.Parent?.Parent is VariableStatementNode statement
            && (statement.Flags & NodeFlags.Ambient) == 0 && SemanticSyntax.HasModifier(statement, SyntaxKind.ExportKeyword)
            && program.Symbols.Program.Configuration.Options.NoEmit != true)
        {
            var marker = node.Name;
            while (marker is BindingPatternNode pattern)
                marker = pattern.Elements?.OfType<BindingElementNode>().FirstOrDefault(e => e.Name is not null)?.Name;
            if (marker is IdentifierNode { Text.Span: "__esModule" })
                Error(marker, DiagnosticCode.IdentifierExpectedEsModuleIsReservedAsAnExportedMarkerWhenTransformingECMAScriptModules);
        }
        if ((flags & (NodeFlags.Let | NodeFlags.Const)) != 0)
        {
            if (node.Name is IdentifierNode identifier)
            {
                cancellation.ThrowIfCancellationRequested();
                if (identifier.Text.Span is "let")
                    Error(identifier, DiagnosticCode.XLetIsNotAllowedToBeUsedAsANameInLetOrConstDeclarations);
            }
            else if (node.Name is { } rootName)
            {
                var names = new Stack<SyntaxNode>();
                names.Push(rootName);
                while (names.TryPop(out var name))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (name is IdentifierNode { Text.Span: "let" })
                    {
                        Error(name, DiagnosticCode.XLetIsNotAllowedToBeUsedAsANameInLetOrConstDeclarations);
                        continue;
                    }
                    if (name is BindingPatternNode pattern)
                        for (int i = pattern.Elements!.Count - 1; i >= 0; i--)
                            if (pattern.Elements[i] is BindingElementNode { Name: { } bindingName })
                                names.Push(bindingName);
                }
            }
        }
        if ((flags & NodeFlags.Using) != 0 && node.Name is BindingPatternNode)
        {
            Error(
                node,
                DiagnosticCode.X0DeclarationsMayNotHaveBindingPatterns,
                (flags & NodeFlags.BlockScoped) == NodeFlags.AwaitUsing ? "await using" : "using");
            return;
        }
        if (node.Parent?.Parent is not ForInOrOfStatementNode)
        {
            if ((flags & NodeFlags.Ambient) != 0)
                await CheckAmbientInitializerAsync(node, cancellation).ConfigureAwait(false);
            else if (node.Initializer is null)
            {
                if (node.Name is BindingPatternNode && node.Parent is not BindingPatternNode)
                    Error(node, DiagnosticCode.ADestructuringDeclarationMustHaveAnInitializer);
                else if ((flags & NodeFlags.BlockScoped) is NodeFlags.Const or NodeFlags.Using or NodeFlags.AwaitUsing)
                    Error(node, DiagnosticCode.X0DeclarationsMustBeInitialized, (flags & NodeFlags.BlockScoped) switch
                    {
                        NodeFlags.Const => "const",
                        NodeFlags.Using => "using",
                        _ => "await using"
                    });
            }
        }
        if (node.ExclamationToken is not null
            && (node.Parent?.Parent is not VariableStatementNode
                || node.Type is null
                || node.Initializer is not null
                || (flags & NodeFlags.Ambient) != 0))
            Error(
                node.ExclamationToken,
                node.Initializer is not null
                    ? DiagnosticCode.DeclarationsWithInitializersCannotAlsoHaveDefiniteAssignmentAssertions
                    : node.Type is null
                        ? DiagnosticCode.DeclarationsWithDefiniteAssignmentAssertionsMustAlsoHaveTypeAnnotations
                        : DiagnosticCode.ADefiniteAssignmentAssertionIsNotPermittedInThisContext);
    }

    private async ValueTask CheckReturnSourceAsync(ReturnStatementNode node, CancellationToken cancellation)
    {
        var value = node.Expression is not null
            ? await CachedExpressionAsync(node.Expression, 0, cancellation).ConfigureAwait(false)
            : context.UndefinedType;
        if (AmbientStatement(node))
            return;
        var function = DeclarationOrder.Ancestor(node.Parent, n => n is IFunctionSignature or ClassStaticBlockDeclarationNode);
        if (function is null || function is ClassStaticBlockDeclarationNode)
        {
            if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
                Error(
                    node,
                    function is null
                        ? DiagnosticCode.AReturnStatementCanOnlyBeUsedWithinAFunctionBody
                        : DiagnosticCode.AReturnStatementCannotBeUsedInsideAClassStaticBlock);
            return;
        }
        var result = await Signatures.ReturnAsync(
            await Signatures.FromDeclarationAsync(function, cancellation).ConfigureAwait(false),
            cancellation).ConfigureAwait(false);
        if (context.StrictNullChecks || node.Expression is not null || (result.Flags & TypeFlags.Never) != 0)
        {
            if (function is SetAccessorDeclarationNode)
            {
                if (node.Expression is not null)
                    Error(node, DiagnosticCode.SettersCannotReturnAValue);
            }
            else if (function is ConstructorDeclarationNode)
            {
                if (node.Expression is not null
                    && !await RelationDiagnostics.CheckAsync(
                        value,
                        result,
                        RelationKind.Assignable,
                        node,
                        node.Expression,
                        cancellation: cancellation).ConfigureAwait(false))
                    Error(node, DiagnosticCode.ReturnTypeOfConstructorSignatureMustBeAssignableToTheInstanceTypeOfTheClass);
            }
            else if (await Signatures.AnnotationAsync(function, cancellation).ConfigureAwait(false) is not null)
                await CheckReturnExpressionAsync(
                    function,
                    await UnwrapReturnAsync(function, result, cancellation).ConfigureAwait(false) ?? result,
                    node, node.Expression, value, false, cancellation).ConfigureAwait(false);
        }
        else if (function is not ConstructorDeclarationNode
            && program.Symbols.Program.Configuration.Options.NoImplicitReturns == true
            && !await EmptyReturnTypeAsync(function, result, cancellation).ConfigureAwait(false))
            Error(node, DiagnosticCode.NotAllCodePathsReturnAValue);
    }

    private async ValueTask CheckReturnExpressionAsync(
        SyntaxNode function,
        Type target,
        SyntaxNode node,
        SyntaxNode? expression,
        Type value,
        bool conditional,
        CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(
            RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        var unwrapped = expression;
        while (unwrapped is ParenthesizedExpressionNode parentheses)
            unwrapped = parentheses.Expression;
        if (unwrapped is ConditionalExpressionNode choice)
        {
            foreach (var arm in new[] { choice.WhenTrue!, choice.WhenFalse! })
                await CheckReturnExpressionAsync(
                    function,
                    target,
                    node,
                    arm,
                    await Expressions.CheckAsync(arm, cancellation: cancellation).ConfigureAwait(false),
                    true,
                    cancellation).ConfigureAwait(false);
            return;
        }
        if (SemanticSyntax.HasModifier(function, SyntaxKind.AsyncKeyword))
            value = await Awaited.GetAsync(
                value,
                false,
                node,
                DiagnosticCode.TheReturnTypeOfAnAsyncFunctionMustEitherBeAValidPromiseOrMustNotContainACallableThenMember,
                cancellation).ConfigureAwait(false) ?? context.ErrorType;
        var effectiveExpression = expression is null ? null : CallResolution.EffectiveNode(expression);
        await RelationDiagnostics.CheckAsync(
            value,
            target,
            RelationKind.Assignable,
            node is ReturnStatementNode && !conditional ? node : effectiveExpression,
            effectiveExpression, cancellation: cancellation).ConfigureAwait(false);
    }

    private async ValueTask<bool> EmptyReturnTypeAsync(SyntaxNode function, Type type, CancellationToken cancellation) =>
        await UnwrapReturnAsync(function, type, cancellation).ConfigureAwait(false) is { } unwrapped
            && (Predicates.Maybe(unwrapped, TypeFlags.Void, cancellation)
                || (unwrapped.Flags & (TypeFlags.Any | TypeFlags.Undefined)) != 0);

    private async ValueTask CheckFunctionPathsAsync(SyntaxNode function, CancellationToken cancellation)
    {
        var annotation = await Signatures.AnnotationAsync(function, cancellation).ConfigureAwait(false);
        var type = annotation is null ? null : await UnwrapReturnAsync(function, annotation, cancellation).ConfigureAwait(false);
        if (type is not null
            && (Predicates.Maybe(type, TypeFlags.Void, cancellation) || (type.Flags & (TypeFlags.Any | TypeFlags.Undefined)) != 0))
            return;
        if (SemanticSyntax.Body(function) is not BlockNode
            || !await FunctionBodies.ImplicitAsync(function, cancellation).ConfigureAwait(false))
            return;
        bool explicitReturn = ((function.Flags | (program.Symbols.Binding(function)?.Get(function)?.Flags ?? 0)) & NodeFlags.HasExplicitReturn) != 0;
        var location = (function as ITypedNode)?.Type ?? (function as IFullSignatureNode)?.FullSignature ?? function;
        if (type is not null && (type.Flags & TypeFlags.Never) != 0)
            Error(location, DiagnosticCode.AFunctionReturningNeverCannotHaveAReachableEndPoint);
        else if (type is not null && !explicitReturn)
            Error(location, DiagnosticCode.AFunctionWhoseDeclaredTypeIsNeitherUndefinedVoidNorAnyMustReturnAValue);
        else if (type is not null
            && context.StrictNullChecks
            && !await AssignableAsync(context.UndefinedType, type, cancellation).ConfigureAwait(false))
            Error(location, DiagnosticCode.FunctionLacksEndingReturnStatementAndReturnTypeDoesNotIncludeUndefined);
        else if (program.Symbols.Program.Configuration.Options.NoImplicitReturns == true)
        {
            if (type is null && (!explicitReturn || await EmptyReturnTypeAsync(function,
                await Signatures.ReturnAsync(
                    await Signatures.FromDeclarationAsync(function, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false)))
                return;
            Error(location, DiagnosticCode.NotAllCodePathsReturnAValue);
        }
    }

    private async ValueTask CheckDeferredSourceAsync(SyntaxNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var previous = CurrentSourceNode;
        CurrentSourceNode = node;
        Instantiation.Engine.ResetExpressionCount();
        try
        {
            switch (node)
            {
                case AsExpressionNode or TypeAssertionNode:
                    await Assertions.DeferredAsync(node, cancellation).ConfigureAwait(false);
                    break;
                case VoidExpressionNode expression:
                    await Expressions.CheckAsync(expression.Expression!, cancellation: cancellation).ConfigureAwait(false);
                    break;
                case FunctionExpressionNode or ArrowFunctionNode or MethodDeclarationNode or MethodSignatureDeclarationNode:
                    await CheckFunctionPathsAsync(node, cancellation).ConfigureAwait(false);
                    var annotation = await Signatures.AnnotationAsync(node, cancellation).ConfigureAwait(false);
                    var body = SemanticSyntax.Body(node);
                    if (body is not null)
                    {
                        if (annotation is null)
                            await Signatures.ReturnAsync(
                                await Signatures.FromDeclarationAsync(node, cancellation).ConfigureAwait(false),
                                cancellation).ConfigureAwait(false);
                        if (body is BlockNode)
                            await CheckSourceElementAsync(body, cancellation).ConfigureAwait(false);
                        else
                        {
                            var value = await Expressions.CheckAsync(body, cancellation: cancellation).ConfigureAwait(false);
                            if (annotation is not null
                                && await UnwrapReturnAsync(node, annotation, cancellation).ConfigureAwait(false) is { } target)
                                await CheckReturnExpressionAsync(
                                    node,
                                    target,
                                    body,
                                    body,
                                    value,
                                    false,
                                    cancellation).ConfigureAwait(false);
                        }
                    }
                    break;
                case CallExpressionNode or NewExpressionNode or TaggedTemplateExpressionNode or BinaryExpressionNode or DecoratorNode
                    or JsxOpeningElementNode or JsxOpeningFragmentNode:
                    await CallResolution.UntypedAsync(node, false, cancellation).ConfigureAwait(false);
                    break;
                case TypeParameterDeclarationNode parameter:
                    await CheckTypeParameterVarianceAsync(parameter, cancellation).ConfigureAwait(false);
                    break;
                case ObjectLiteralExpressionNode or JsxAttributesNode:
                    await ContextualDeprecationsAsync(node, cancellation).ConfigureAwait(false);
                    break;
                case ClassExpressionNode expression:
                    foreach (var member in expression.Members!)
                        await CheckSourceElementAsync(member, cancellation).ConfigureAwait(false);
                    break;
                case GetAccessorDeclarationNode or SetAccessorDeclarationNode:
                    await CheckAccessorSourceAsync(node, cancellation).ConfigureAwait(false);
                    break;
                default:
                    if (node is JsxElementNode or JsxSelfClosingElementNode)
                        await CheckJsxDeferredAsync(node, cancellation).ConfigureAwait(false);
                    else
                        throw new InvalidOperationException($"Checker requires deferred source checking for {node.Kind}");
                    break;
            }
        }
        finally
        {
            CurrentSourceNode = previous;
        }
    }

    private async ValueTask ContextualDeprecationsAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var contextual = await Contexts.ApparentAsync(node, cancellation: cancellation).ConfigureAwait(false);
        if (contextual is null)
            return;
        foreach (var member in (node is ObjectLiteralExpressionNode literal ? literal.Properties : ((JsxAttributesNode)node).Properties)!)
        {
            cancellation.ThrowIfCancellationRequested();
            if (member is INamedNode { Name: { } name } && name is not ComputedPropertyNameNode
                && await Properties.PropertyAsync(
                    contextual,
                    SyntaxNameText.Get(name),
                    cancellation: cancellation).ConfigureAwait(false) is { Declarations.Length: > 0 } property
                && program.Deprecations.Symbol(property))
                program.Suggestion(name, DiagnosticCode.X0IsDeprecated, property.Name);
        }
    }
}

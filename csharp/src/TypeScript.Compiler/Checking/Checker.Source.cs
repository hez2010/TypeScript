using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
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

    private bool SkipProgramFile(SourceFileNode file) => NoCheck || file.CheckJsDirective?.Enabled == false
        || file.IsDeclarationFile && program.Symbols.Program.Configuration.Options.Boolean("skipLibCheck") == true
        || program.Symbols.Program.GetFile(file.FileName)!.Library
            && program.Symbols.Program.Configuration.Options.Boolean("skipDefaultLibCheck") == true;

    internal async ValueTask CheckSourceFileAsync(SourceFileNode file, CancellationToken cancellation = default)
    {
        await queryGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            RequireNode(file);
            RequireUsable();
            if (checkedFiles.Contains(file))
                return;
            foreach (var statement in file.Statements!)
                await CheckSourceElementAsync(statement, cancellation).ConfigureAwait(false);
            if (deferredSourceNodes.TryGetValue(file, out var deferred))
                for (int i = 0; i < deferred.Count; i++)
                    await CheckDeferredSourceAsync(deferred[i], cancellation).ConfigureAwait(false);
            foreach (var diagnostic in DeferredIterationDiagnostics.Where(d => SemanticSyntax.Source(d.Node) == file).ToArray())
                await Iteration.NotIterableAsync(diagnostic.Node, diagnostic.Type, diagnostic.Async, cancellation).ConfigureAwait(false);
            await CheckMissingPropertiesAsync(file, cancellation).ConfigureAwait(false);
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
            if (!withinUnreachable && program.Symbols.Program.Configuration.Options.Boolean("allowUnreachableCode") != true
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
                    if ((declarations.Flags & NodeFlags.Using) != 0)
                        throw new InvalidOperationException("Checker requires disposable declaration checks");
                    foreach (var declaration in declarations.Declarations!)
                        await CheckSourceElementAsync(declaration, cancellation).ConfigureAwait(false);
                    break;
                case VariableDeclarationNode declaration:
                    VariableGrammar(declaration);
                    await FunctionDeclarations.VariableAsync(declaration, cancellation).ConfigureAwait(false);
                    await CheckMergedExportsAsync(declaration, cancellation).ConfigureAwait(false);
                    break;
                case BindingElementNode element:
                    await FunctionDeclarations.VariableAsync(element, cancellation).ConfigureAwait(false);
                    await CheckMergedExportsAsync(element, cancellation).ConfigureAwait(false);
                    break;
                case ExpressionStatementNode expression:
                    AmbientStatement(expression);
                    await Expressions.CheckAsync(expression.Expression!, cancellation: cancellation).ConfigureAwait(false);
                    break;
                case FunctionDeclarationNode function:
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
                        Error(condition.ThenStatement, 1313);
                    await CheckSourceElementAsync(condition.ElseStatement, cancellation).ConfigureAwait(false);
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
                    ForEachGrammar(loop);
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
                            AssignmentChecks.Reference(loop.Initializer!, 2487, 2781);
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
                    AmbientStatement(statement);
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
                            if (caught.Type is not null
                                && (await Nodes.FromNodeAsync(caught.Type, cancellation).ConfigureAwait(false)).Flags is var flags
                                && (flags & TypeFlags.AnyOrUnknown) == 0)
                                Error(caught.Type, 1196);
                            await CheckSourceElementAsync(caught, cancellation).ConfigureAwait(false);
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
                    RegisterUnused(alias);
                    ExportedDeclaration(alias, false);
                    await CheckMergedExportsAsync(alias, cancellation).ConfigureAwait(false);
                    if (ReservedTypeName(alias.Name!.Text))
                        Error(alias.Name, 2457);
                    if (alias.TypeParameters is not null)
                        foreach (TypeParameterDeclarationNode parameter in alias.TypeParameters)
                            await FunctionDeclarations.TypeParameterAsync(parameter, cancellation).ConfigureAwait(false);
                    await Declared.GetAsync(program.Symbols.Declaration(alias)!, cancellation).ConfigureAwait(false);
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
                        Error(member, 18024);
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
                    if (property.Name is PrivateIdentifierNode)
                        Error(property, 18016);
                    if (property.Name is ComputedPropertyNameNode computed)
                        await ComputedNameAsync(computed, cancellation).ConfigureAwait(false);
                    await FunctionDeclarations.VariableAsync(property, cancellation).ConfigureAwait(false);
                    break;
                case MethodSignatureDeclarationNode or CallSignatureDeclarationNode or ConstructSignatureDeclarationNode:
                    await FunctionDeclarations.GrammarAsync(node, cancellation).ConfigureAwait(false);
                    await FunctionDeclarations.CheckAsync(node, cancellation).ConfigureAwait(false);
                    break;
                case IndexSignatureDeclarationNode index:
                    await CheckIndexSignatureSourceAsync(index, cancellation).ConfigureAwait(false);
                    break;
                case { Kind: SyntaxKind.EmptyStatement or SyntaxKind.DebuggerStatement }:
                    AmbientStatement(node);
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
        Error(node, owner == node ? 1183 : 1036);
        links.Nodes.Get(owner).HasReportedStatementInAmbientContext = true;
        return true;
    }

    private void VariableGrammar(VariableDeclarationNode node)
    {
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        var flags = node.Flags | (node.Parent is VariableDeclarationListNode list ? list.Flags : 0);
        if (node.Parent?.Parent is not ForInOrOfStatementNode && (flags & NodeFlags.Ambient) == 0 && node.Initializer is null)
        {
            if (node.Name is BindingPatternNode)
                Error(node, 1182);
            else if ((flags & NodeFlags.Const) != 0)
                Error(node, 1155);
        }
        if (node.ExclamationToken is not null
            && (node.Parent?.Parent is not VariableStatementNode
                || node.Type is null
                || node.Initializer is not null
                || (flags & NodeFlags.Ambient) != 0))
            Error(node.ExclamationToken, node.Initializer is not null ? 1263 : node.Type is null ? 1264 : 1255);
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
                Error(node, function is null ? 1108 : 18041);
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
                    Error(node, 2408);
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
                    Error(node, 2409);
            }
            else if (await Signatures.AnnotationAsync(function, cancellation).ConfigureAwait(false) is not null)
                await CheckReturnExpressionAsync(
                    function,
                    await UnwrapReturnAsync(function, result, cancellation).ConfigureAwait(false) ?? result,
                    node, node.Expression, value, false, cancellation).ConfigureAwait(false);
        }
        else if (function is not ConstructorDeclarationNode
            && program.Symbols.Program.Configuration.Options.Boolean("noImplicitReturns") == true
            && !await EmptyReturnTypeAsync(function, result, cancellation).ConfigureAwait(false))
            Error(node, 7030);
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
            value = await Awaited.GetAsync(value, false, node, 1058, cancellation).ConfigureAwait(false) ?? context.ErrorType;
        await RelationDiagnostics.CheckAsync(
            value,
            target,
            RelationKind.Assignable,
            node is ReturnStatementNode && !conditional ? node : expression,
            expression, cancellation: cancellation).ConfigureAwait(false);
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
            Error(location, 2534);
        else if (type is not null && !explicitReturn)
            Error(location, 2355);
        else if (type is not null
            && context.StrictNullChecks
            && !await AssignableAsync(context.UndefinedType, type, cancellation).ConfigureAwait(false))
            Error(location, 2366);
        else if (program.Symbols.Program.Configuration.Options.Boolean("noImplicitReturns") == true)
        {
            if (type is null && (!explicitReturn || await EmptyReturnTypeAsync(function,
                await Signatures.ReturnAsync(
                    await Signatures.FromDeclarationAsync(function, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false)))
                return;
            Error(location, 7030);
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
                case CallExpressionNode or NewExpressionNode or TaggedTemplateExpressionNode or BinaryExpressionNode:
                    await CallResolution.UntypedAsync(node, false, cancellation).ConfigureAwait(false);
                    break;
                case TypeParameterDeclarationNode parameter:
                    if (parameter.Modifiers?.Any(m => m.Kind is SyntaxKind.InKeyword or SyntaxKind.OutKeyword) == true)
                        throw new InvalidOperationException("Checker requires variance annotation diagnostics");
                    break;
                case ObjectLiteralExpressionNode:
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
                    throw new InvalidOperationException($"Checker requires deferred source checking for {node.Kind}");
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
        foreach (var member in ((ObjectLiteralExpressionNode)node).Properties!)
        {
            cancellation.ThrowIfCancellationRequested();
            if (member is INamedNode { Name: { } name } && name is not ComputedPropertyNameNode
                && await Properties.PropertyAsync(
                    contextual,
                    SyntaxNameText.Get(name),
                    cancellation: cancellation).ConfigureAwait(false) is { Declarations.Count: > 0 } property
                && program.Deprecations.Symbol(property))
                program.Suggestion(name, 6385, property.Name);
        }
    }
}

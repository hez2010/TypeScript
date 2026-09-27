using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly HashSet<SyntaxNode> reportedUnreachable = [];
    private bool withinUnreachable;

    private async ValueTask<bool> CheckUnreachableAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (!Executable(node))
            return false;
        if (reportedUnreachable.Contains(node))
            return true;
        if (!await UnreachableAsync(node, cancellation).ConfigureAwait(false))
            return false;
        reportedUnreachable.Add(node);
        var endNode = node;
        var statements = Statements(node.Parent);
        if (statements is not null)
        {
            int index = statements.IndexOf(node);
            for (int i = index + 1; index >= 0 && i < statements.Count; i++)
            {
                if (!Executable(statements[i]) || !await UnreachableAsync(statements[i], cancellation).ConfigureAwait(false))
                    break;
                reportedUnreachable.Add(statements[i]);
                endNode = statements[i];
            }
        }
        if (program.Symbols.Program.Configuration.Options.Boolean("allowUnreachableCode") == false)
        {
            var source = SemanticSyntax.Source(node)!;
            int start = CheckerDiagnostic.TokenRange(source, node.Pos).Start;
            Error(node, new Diagnostic(Messages.Unreachable_code_detected, start, endNode.End - start, []) { FileName = source.FileName });
        }
        else
            ExpressionSuggestion(node, DiagnosticCode.UnreachableCodeDetected);
        return true;
    }

    private async ValueTask<bool> UnreachableAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var data = program.Symbols.Binding(node)?.Get(node);
        if (((node.Flags | (data?.Flags ?? 0)) & NodeFlags.Unreachable) != 0)
        {
            if (node is EnumDeclarationNode && SemanticSyntax.HasModifier(node, SyntaxKind.ConstKeyword))
                return program.Symbols.Program.Configuration.Options.Boolean("preserveConstEnums") == true || IsolatedModules;
            if (node is ModuleDeclarationNode module)
            {
                int state = Binder.ModuleState(module);
                return state == 2
                    || state == 1
                        && (program.Symbols.Program.Configuration.Options.Boolean("preserveConstEnums") == true || IsolatedModules);
            }
            return true;
        }
        return data?.Flow is { } flow && !await FlowTypes.Reachability.ReachableAsync(flow, cancellation).ConfigureAwait(false);
    }

    private static bool Executable(SyntaxNode node) => node is VariableStatementNode { DeclarationList: { } list }
        ? (list.Flags & NodeFlags.BlockScoped) != 0
            || list.Declarations!.OfType<VariableDeclarationNode>().Any(d => d.Initializer is not null)
        : node.Kind is >= SyntaxKind.FirstStatement and <= SyntaxKind.LastStatement
            or SyntaxKind.ClassDeclaration or SyntaxKind.EnumDeclaration or SyntaxKind.ModuleDeclaration;

    private static NodeList? Statements(SyntaxNode? node) => node switch
    {
        SourceFileNode file => file.Statements,
        BlockNode block => block.Statements,
        ModuleBlockNode block => block.Statements,
        CaseOrDefaultClauseNode clause => clause.Statements,
        _ => null
    };

    private static bool IterationStatement(SyntaxNode? node) => node?.Kind is SyntaxKind.DoStatement or SyntaxKind.WhileStatement
        or SyntaxKind.ForStatement or SyntaxKind.ForInStatement or SyntaxKind.ForOfStatement;

    private void JumpGrammar(SyntaxNode node)
    {
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        var label = node is BreakStatementNode stop ? stop.Label : ((ContinueStatementNode)node).Label;
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current is IFunctionSignature or ClassStaticBlockDeclarationNode)
            {
                Error(node, DiagnosticCode.JumpTargetCannotCrossFunctionBoundary);
                return;
            }
            if (current is LabeledStatementNode labeled && label is not null && labeled.Label!.Text == label.Text)
            {
                var target = labeled.Statement;
                while (target is LabeledStatementNode nested)
                    target = nested.Statement;
                if (node is ContinueStatementNode && !IterationStatement(target))
                    Error(node, DiagnosticCode.AContinueStatementCanOnlyJumpToALabelOfAnEnclosingIterationStatement);
                return;
            }
            if (label is null && (current is SwitchStatementNode && node is BreakStatementNode || IterationStatement(current)))
                return;
        }
        Error(
            node,
            label is not null
                ? node is BreakStatementNode
                    ? DiagnosticCode.ABreakStatementCanOnlyJumpToALabelOfAnEnclosingStatement
                    : DiagnosticCode.AContinueStatementCanOnlyJumpToALabelOfAnEnclosingIterationStatement
                : node is BreakStatementNode
                    ? DiagnosticCode.ABreakStatementCanOnlyBeUsedWithinAnEnclosingIterationOrSwitchStatement
                    : DiagnosticCode.AContinueStatementCanOnlyBeUsedWithinAnEnclosingIterationStatement);
    }

    private void LabelGrammar(LabeledStatementNode node)
    {
        if (!AmbientStatement(node) && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            for (var parent = node.Parent; parent is not null && parent is not IFunctionSignature; parent = parent.Parent)
                if (parent is LabeledStatementNode label && label.Label!.Text == node.Label!.Text)
                {
                    Error(node.Label, DiagnosticCode.DuplicateLabel0, node.Label.Text);
                    break;
                }
        if (((node.Label!.Flags | (program.Symbols.Binding(node.Label)?.Get(node.Label)?.Flags ?? 0)) & NodeFlags.Unreachable) != 0
            && program.Symbols.Program.Configuration.Options.Boolean("allowUnusedLabels") != true)
        {
            if (program.Symbols.Program.Configuration.Options.Boolean("allowUnusedLabels") == false)
                Error(node.Label, DiagnosticCode.UnusedLabel);
            else
                ExpressionSuggestion(node.Label, DiagnosticCode.UnusedLabel);
        }
    }

    private async ValueTask CheckSwitchSourceAsync(SwitchStatementNode node, CancellationToken cancellation)
    {
        AmbientStatement(node);
        var expression = await Expressions.CheckAsync(node.Expression!, cancellation: cancellation).ConfigureAwait(false);
        bool sawDefault = false, reportedDefault = false;
        foreach (CaseOrDefaultClauseNode clause in ((CaseBlockNode)node.CaseBlock!).Clauses!)
        {
            if (clause.Kind == SyntaxKind.DefaultClause && !reportedDefault)
            {
                if (sawDefault)
                {
                    if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
                        Error(clause, DiagnosticCode.ADefaultClauseCannotAppearMoreThanOnceInASwitchStatement);
                    reportedDefault = true;
                }
                sawDefault = true;
            }
            if (clause.Expression is { } value)
            {
                var caseType = await Expressions.CheckAsync(value, cancellation: cancellation).ConfigureAwait(false);
                if ((caseType.Flags & TypeFlags.Nullable) == 0
                    && !await Relations.RelatedAsync(expression, caseType, RelationKind.Comparable, cancellation).ConfigureAwait(false))
                    await RelationDiagnostics.CheckAsync(
                        caseType,
                        expression,
                        RelationKind.Comparable,
                        value,
                        null,
                        DiagnosticCode.Type0IsNotComparableToType1,
                        cancellation).ConfigureAwait(false);
            }
            foreach (var statement in clause.Statements!)
                await CheckSourceElementAsync(statement, cancellation).ConfigureAwait(false);
            if (program.Symbols.Program.Configuration.Options.Boolean("noFallthroughCasesInSwitch") == true
                && program.Symbols.Binding(clause)?.Get(clause)?.EndFlow is { } flow && await FlowTypes.Reachability.ReachableAsync(
                    flow,
                    cancellation).ConfigureAwait(false))
                Error(clause, DiagnosticCode.FallthroughCaseInSwitch);
        }
    }

    private async ValueTask CheckForInSourceAsync(ForInOrOfStatementNode node, CancellationToken cancellation)
    {
        await ForEachGrammarAsync(node, cancellation).ConfigureAwait(false);
        var right = await Expressions.CheckAsync(node.Expression!, cancellation: cancellation).ConfigureAwait(false);
        if (await Facts.GetAsync(right, TypeFacts.IsUndefinedOrNull, cancellation).ConfigureAwait(false) != 0)
            right = await Facts.NonNullableAsync(right, cancellation).ConfigureAwait(false);
        if (node.Initializer is VariableDeclarationListNode declarations)
        {
            if (declarations.Declarations?.FirstOrDefault() is VariableDeclarationNode { Name: BindingPatternNode pattern })
                Error(pattern, DiagnosticCode.TheLeftHandSideOfAForInStatementCannotBeADestructuringPattern);
            await CheckSourceElementAsync(declarations, cancellation).ConfigureAwait(false);
        }
        else
        {
            var left = await Expressions.CheckAsync(node.Initializer!, cancellation: cancellation).ConfigureAwait(false);
            if (node.Initializer is ArrayLiteralExpressionNode or ObjectLiteralExpressionNode)
                Error(node.Initializer, DiagnosticCode.TheLeftHandSideOfAForInStatementCannotBeADestructuringPattern);
            else
            {
                var key = await Keys.GetAsync(right, cancellation: cancellation).ConfigureAwait(false);
                var extract = await program.Globals.AliasAsync("Extract", 2, Declared, cancellation).ConfigureAwait(false);
                key = extract is null
                    ? context.StringType
                    : await References.AliasInstantiationAsync(
                        extract,
                        [key, context.StringType],
                        cancellation: cancellation).ConfigureAwait(false);
                if (!await AssignableAsync(key == context.NeverType ? context.StringType : key, left, cancellation).ConfigureAwait(false))
                    Error(node.Initializer!, DiagnosticCode.TheLeftHandSideOfAForInStatementMustBeOfTypeStringOrAny);
                else
                    AssignmentChecks.Reference(
                        node.Initializer!,
                        DiagnosticCode.TheLeftHandSideOfAForInStatementMustBeAVariableOrAPropertyAccess,
                        DiagnosticCode.TheLeftHandSideOfAForInStatementMayNotBeAnOptionalPropertyAccess);
            }
        }
        if (right == context.NeverType
            || !await AssignableKindAsync(
                right,
                TypeFlags.NonPrimitive | TypeFlags.InstantiableNonPrimitive,
                cancellation).ConfigureAwait(false))
            Error(
                node.Expression!,
                DiagnosticCode.TheRightHandSideOfAForInStatementMustBeOfTypeAnyAnObjectTypeOrATypeParameterButHereHasType0,
                await TypeDisplay.GetAsync(right, cancellation));
        await CheckSourceElementAsync(node.Statement, cancellation).ConfigureAwait(false);
    }

    private async ValueTask ForEachGrammarAsync(ForInOrOfStatementNode node, CancellationToken cancellation)
    {
        if (AmbientStatement(node))
            return;
        bool grammar = SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0;
        bool forOf = node.Kind == SyntaxKind.ForOfStatement;
        if (node.AwaitModifier is { } awaitToken)
        {
            var container = DeclarationOrder.Ancestor(node.Parent, n => n is IFunctionSignature or ClassStaticBlockDeclarationNode);
            if (container is ClassStaticBlockDeclarationNode)
            {
                if (grammar)
                    Error(awaitToken, DiagnosticCode.XForAwaitLoopsCannotBeUsedInsideAClassStaticBlock);
            }
            else if ((node.Flags & NodeFlags.AwaitContext) == 0 && grammar)
            {
                if (container is null)
                    TopLevelAwait(
                        awaitToken,
                        DiagnosticCode.XForAwaitLoopsAreOnlyAllowedAtTheTopLevelOfAFileWhenThatFileIsAModuleButThisFileHasNoImportsOrExportsConsiderAddingAnEmptyExportToMakeThisFileAModule,
                        DiagnosticCode.TopLevelForAwaitLoopsAreOnlyAllowedWhenTheModuleOptionIsSetToEs2022EsnextSystemNode16Node18Node20NodenextOrPreserveAndTheTargetOptionIsSetToEs2017OrHigher);
                else
                {
                    var diagnostic = CheckerDiagnostic.Create(awaitToken,
                        Messages.X_for_await_loops_are_only_allowed_within_async_functions_and_at_the_top_levels_of_modules);
                    if (container is not ConstructorDeclarationNode)
                        diagnostic = diagnostic with
                        {
                            RelatedInformation = [CheckerDiagnostic.Create(
                            container,
                            Messages.Did_you_mean_to_mark_this_function_as_async)]
                        };
                    Error(awaitToken, diagnostic);
                    return;
                }
            }
            if (TargetYear < 2018)
                await ExternalHelpersAsync(node, ["__asyncValues"], cancellation);
        }
        if (!grammar)
            return;
        if (forOf && (node.Flags & NodeFlags.AwaitContext) == 0 && node.Initializer is IdentifierNode { Text.Span: "async" })
        {
            Error(node.Initializer, DiagnosticCode.TheLeftHandSideOfAForOfStatementMayNotBeAsync);
            return;
        }
        if (node.Initializer is not VariableDeclarationListNode { Declarations: { Count: > 0 } declarations })
            return;
        if (declarations.Count > 1)
        {
            ErrorOnFirstToken(
                declarations[1],
                forOf
                    ? DiagnosticCode.OnlyASingleVariableDeclarationIsAllowedInAForOfStatement
                    : DiagnosticCode.OnlyASingleVariableDeclarationIsAllowedInAForInStatement);
            return;
        }
        var variable = (VariableDeclarationNode)declarations[0];
        if (variable.Initializer is not null)
            Error(
                variable.Name!,
                forOf
                    ? DiagnosticCode.TheVariableDeclarationOfAForOfStatementCannotHaveAnInitializer
                    : DiagnosticCode.TheVariableDeclarationOfAForInStatementCannotHaveAnInitializer);
        else if (variable.Type is not null)
            Error(
                variable,
                forOf
                    ? DiagnosticCode.TheLeftHandSideOfAForOfStatementCannotUseATypeAnnotation
                    : DiagnosticCode.TheLeftHandSideOfAForInStatementCannotUseATypeAnnotation);
    }
}

using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
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
        var statements = Statements(node.Parent);
        if (statements is not null)
        {
            int index = statements.ToList().IndexOf(node);
            for (int i = index + 1; index >= 0 && i < statements.Count; i++)
            {
                if (!Executable(statements[i]) || !await UnreachableAsync(statements[i], cancellation).ConfigureAwait(false))
                    break;
                reportedUnreachable.Add(statements[i]);
            }
        }
        if (program.Symbols.Program.Configuration.Options.Boolean("allowUnreachableCode") == false)
            Error(node, 7027);
        else
            ExpressionSuggestion(node, 7027);
        return true;
    }

    private async ValueTask<bool> UnreachableAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var data = program.Symbols.Binding(node)?.Get(node);
        if (((node.Flags | (data?.Flags ?? 0)) & NodeFlags.Unreachable) != 0)
        {
            if (node is EnumDeclarationNode && SemanticSyntax.HasModifier(node, SyntaxKind.ConstKeyword))
                return program.Symbols.Program.Configuration.Options.Boolean("preserveConstEnums") == true || IsolatedModules;
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
                Error(node, 1107);
                return;
            }
            if (current is LabeledStatementNode labeled && label is not null && labeled.Label!.Text == label.Text)
            {
                var target = labeled.Statement;
                while (target is LabeledStatementNode nested)
                    target = nested.Statement;
                if (node is ContinueStatementNode && !IterationStatement(target))
                    Error(node, 1115);
                return;
            }
            if (label is null && (current is SwitchStatementNode && node is BreakStatementNode || IterationStatement(current)))
                return;
        }
        Error(node, label is not null ? node is BreakStatementNode ? 1116 : 1115 : node is BreakStatementNode ? 1105 : 1104);
    }

    private void LabelGrammar(LabeledStatementNode node)
    {
        if (!AmbientStatement(node) && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            for (var parent = node.Parent; parent is not null && parent is not IFunctionSignature; parent = parent.Parent)
                if (parent is LabeledStatementNode label && label.Label!.Text == node.Label!.Text)
                {
                    Error(node.Label, 1114);
                    break;
                }
        if (((node.Label!.Flags | (program.Symbols.Binding(node.Label)?.Get(node.Label)?.Flags ?? 0)) & NodeFlags.Unreachable) != 0
            && program.Symbols.Program.Configuration.Options.Boolean("allowUnusedLabels") != true)
        {
            if (program.Symbols.Program.Configuration.Options.Boolean("allowUnusedLabels") == false)
                Error(node.Label, 7028);
            else
                ExpressionSuggestion(node.Label, 7028);
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
                        Error(clause, 1113);
                    reportedDefault = true;
                }
                sawDefault = true;
            }
            if (clause.Expression is { } value)
            {
                var caseType = await Expressions.CheckAsync(value, cancellation: cancellation).ConfigureAwait(false);
                if ((expression.Flags & TypeFlags.Nullable) == 0
                    && !await Relations.RelatedAsync(expression, caseType, RelationKind.Comparable, cancellation).ConfigureAwait(false))
                    await RelationDiagnostics.CheckAsync(
                        caseType,
                        expression,
                        RelationKind.Comparable,
                        value,
                        null,
                        2678,
                        cancellation).ConfigureAwait(false);
            }
            foreach (var statement in clause.Statements!)
                await CheckSourceElementAsync(statement, cancellation).ConfigureAwait(false);
            if (program.Symbols.Program.Configuration.Options.Boolean("noFallthroughCasesInSwitch") == true
                && program.Symbols.Binding(clause)?.Get(clause)?.EndFlow is { } flow && await FlowTypes.Reachability.ReachableAsync(
                    flow,
                    cancellation).ConfigureAwait(false))
                Error(clause, 7029);
        }
    }

    private async ValueTask CheckForInSourceAsync(ForInOrOfStatementNode node, CancellationToken cancellation)
    {
        ForEachGrammar(node);
        var right = await Expressions.CheckAsync(node.Expression!, cancellation: cancellation).ConfigureAwait(false);
        if (await Facts.GetAsync(right, TypeFacts.IsUndefinedOrNull, cancellation).ConfigureAwait(false) != 0)
            right = await Facts.NonNullableAsync(right, cancellation).ConfigureAwait(false);
        if (node.Initializer is VariableDeclarationListNode declarations)
        {
            if (declarations.Declarations?.FirstOrDefault() is VariableDeclarationNode { Name: BindingPatternNode pattern })
                Error(pattern, 2491);
            await CheckSourceElementAsync(declarations, cancellation).ConfigureAwait(false);
        }
        else
        {
            var left = await Expressions.CheckAsync(node.Initializer!, cancellation: cancellation).ConfigureAwait(false);
            if (node.Initializer is ArrayLiteralExpressionNode or ObjectLiteralExpressionNode)
                Error(node.Initializer, 2491);
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
                    Error(node.Initializer!, 2405);
                else
                    AssignmentChecks.Reference(node.Initializer!, 2406, 2780);
            }
        }
        if (right == context.NeverType
            || !await AssignableKindAsync(
                right,
                TypeFlags.NonPrimitive | TypeFlags.InstantiableNonPrimitive,
                cancellation).ConfigureAwait(false))
            Error(node.Expression!, 2407);
        await CheckSourceElementAsync(node.Statement, cancellation).ConfigureAwait(false);
    }

    private void ForEachGrammar(ForInOrOfStatementNode node)
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
                    Error(awaitToken, 18038);
            }
            else if ((node.Flags & NodeFlags.AwaitContext) == 0 && grammar)
            {
                if (container is null)
                    TopLevelAwait(awaitToken, 1431, 1432);
                else
                {
                    Error(awaitToken, 1103);
                    return;
                }
            }
            if (TargetYear < 2018 && program.Symbols.Program.Configuration.Options.Boolean("importHelpers") == true)
                throw new InvalidOperationException("Checker requires for-await emit helper validation");
        }
        if (!grammar)
            return;
        if (forOf && (node.Flags & NodeFlags.AwaitContext) == 0 && node.Initializer is IdentifierNode { Text: "async" })
        {
            Error(node.Initializer, 1106);
            return;
        }
        if (node.Initializer is not VariableDeclarationListNode { Declarations: { Count: > 0 } declarations })
            return;
        if (declarations.Count > 1)
        {
            Error(declarations[1], forOf ? 1188 : 1091);
            return;
        }
        var variable = (VariableDeclarationNode)declarations[0];
        if (variable.Initializer is not null)
            Error(variable.Name!, forOf ? 1190 : 1189);
        else if (variable.Type is not null)
            Error(variable, forOf ? 2483 : 2404);
    }
}

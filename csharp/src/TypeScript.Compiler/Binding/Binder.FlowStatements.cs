using TypeScript.Compiler.Ast;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using F = TypeScript.Compiler.Binding.FlowFlags;

namespace TypeScript.Compiler.Binding;

public sealed partial class Binder
{
    private FlowNode Reduce(FlowNode target, IReadOnlyList<FlowNode> antecedents) => new(F.ReduceLabel, antecedent: currentFlow)
    { ReducedTarget = target, ReducedAntecedents = antecedents.ToArray() };

    private async ValueTask Try(TryStatementNode node)
    {
        var savedReturn = returnTarget;
        var savedException = exceptionTarget;
        var normal = Label();
        var returned = Label();
        var exception = Label();
        if (node.FinallyBlock is not null)
            returnTarget = returned;
        Add(exception, currentFlow);
        exceptionTarget = exception;
        await Visit(node.TryBlock).ConfigureAwait(false);
        Add(normal, currentFlow);
        if (node.CatchClause is not null)
        {
            currentFlow = Finish(exception);
            exception = Label();
            Add(exception, currentFlow);
            exceptionTarget = exception;
            await Visit(node.CatchClause).ConfigureAwait(false);
            Add(normal, currentFlow);
        }
        returnTarget = savedReturn;
        exceptionTarget = savedException;
        if (node.FinallyBlock is null)
        {
            currentFlow = Finish(normal);
            return;
        }
        var final = Label();
        final.AntecedentList.AddRange(normal.Antecedents);
        final.AntecedentList.AddRange(exception.Antecedents);
        final.AntecedentList.AddRange(returned.Antecedents);
        currentFlow = final;
        await Visit(node.FinallyBlock).ConfigureAwait(false);
        if ((currentFlow.Flags & F.Unreachable) != 0)
        {
            currentFlow = unreachable;
            return;
        }
        if (returnTarget is not null && returned.Antecedents.Count > 0)
            Add(returnTarget, Reduce(final, returned.Antecedents));
        if (exceptionTarget is not null && exception.Antecedents.Count > 0)
            Add(exceptionTarget, Reduce(final, exception.Antecedents));
        currentFlow = normal.Antecedents.Count == 0 ? unreachable : Reduce(final, normal.Antecedents);
    }

    private FlowNode SwitchFlow(SyntaxNode node, FlowNode antecedent, int start, int end)
    {
        Reference(antecedent);
        return new(F.SwitchClause, node, antecedent) { ClauseStart = start, ClauseEnd = end };
    }

    private async ValueTask Switch(SwitchStatementNode node)
    {
        var exit = Label();
        await Visit(node.Expression).ConfigureAwait(false);
        var savedBreak = breakTarget;
        var savedSwitch = preSwitchFlow;
        breakTarget = exit;
        preSwitchFlow = currentFlow;
        await Visit(node.CaseBlock).ConfigureAwait(false);
        Add(exit, currentFlow);
        if (node.CaseBlock?.Clauses?.Any(c => c.Kind == K.DefaultClause) != true)
            Add(exit, SwitchFlow(node, preSwitchFlow, 0, 0));
        breakTarget = savedBreak;
        preSwitchFlow = savedSwitch;
        currentFlow = Finish(exit);
    }

    private async ValueTask Cases(CaseBlockNode node)
    {
        if (node.Parent is not SwitchStatementNode owner || node.Clauses is not { } clauses)
            return;
        bool narrowing = owner.Expression?.Kind == K.TrueKeyword || owner.Expression is { } expression && Narrowing(expression);
        var fallthrough = unreachable;
        for (int i = 0; i < clauses.Count; i++)
        {
            int start = i;
            while (Statements(clauses[i])?.Count == 0 && i + 1 < clauses.Count)
            {
                if (fallthrough == unreachable)
                    currentFlow = preSwitchFlow!;
                await Visit(clauses[i++]).ConfigureAwait(false);
            }
            var label = Label();
            Add(label, narrowing ? SwitchFlow(owner, preSwitchFlow!, start, i + 1) : preSwitchFlow!);
            Add(label, fallthrough);
            currentFlow = Finish(label);
            await Visit(clauses[i]).ConfigureAwait(false);
            fallthrough = currentFlow;
            if ((currentFlow.Flags & F.Unreachable) == 0 && i + 1 < clauses.Count)
                Data(clauses[i]).EndFlow = currentFlow;
        }
    }
}

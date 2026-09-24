using System.Globalization;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask ReportRelationMessageAsync(
        SyntaxNode node,
        int code,
        Type source,
        Type target,
        RelationKind kind,
        CancellationToken cancellation)
    {
        if (code is not (2322 or 2344 or 2345 or 2352 or 2375 or 2412 or 2420 or 2720 or 2739 or 2740 or 2741 or 2787 or 2788 or 2789))
        {
            Error(node, code);
            return;
        }
        var originalSource = source;
        if ((target.Flags & TypeFlags.Never) == 0 && source.IsLiteral && !await CouldHaveSingletonTypesAsync(target, cancellation))
            source = await Widening.LiteralBaseAsync(source, cancellation);
        string sourceText = await TypeDisplay.GetAsync(source, cancellation);
        string targetText = await TypeDisplay.GetAsync(target, cancellation);
        string[] arguments;
        if (code is 2787 or 2788 or 2789)
            arguments = [sourceText];
        else if (code is 2739 or 2740 or 2741 && RequiredPropertyDeclarations.TryGetValue(node, out var missing))
        {
            if (code == 2741)
                arguments = [TypeDisplay.SymbolName(missing[0]), sourceText, targetText];
            else if (code == 2740)
                arguments = [sourceText, targetText, string.Join(", ", missing.Take(4).Select(TypeDisplay.SymbolName)),
                    (missing.Count - 4).ToString(CultureInfo.InvariantCulture)];
            else
                arguments = [sourceText, targetText, string.Join(", ", missing.Select(TypeDisplay.SymbolName))];
        }
        else
            arguments = [sourceText, targetText];
        var diagnostic = CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), arguments);
        if (code is not (2739 or 2740 or 2741))
        {
            var explanation = await Relations.ExplainAsync(originalSource, target, kind, cancellation);
            if (await RelationChainAsync(explanation?.Next, diagnostic, cancellation) is { } chain)
                diagnostic = diagnostic with { MessageChain = [chain] };
            diagnostic = await ConstraintReasonAsync(diagnostic, originalSource, source, target, sourceText, targetText, cancellation);
        }
        Error(node, diagnostic);
    }

    private async ValueTask<Diagnostic?> RelationChainAsync(
        RelationExplanation? explanation,
        Diagnostic location,
        CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        if (explanation is null)
            return null;
        var next = await RelationChainAsync(explanation.Next, location, cancellation);
        var diagnostic = location with
        {
            Message = DiagnosticLocalization.GetMessage(explanation.Code),
            Arguments = [],
            MessageChain = next is null ? [] : [next],
            RelatedInformation = []
        };
        if (explanation.Code is 2322 or 2678)
        {
            var source = explanation.Source!;
            var target = explanation.Target!;
            var originalSource = source;
            if ((target.Flags & TypeFlags.Never) == 0 && source.IsLiteral && !await CouldHaveSingletonTypesAsync(target, cancellation))
                source = await Widening.LiteralBaseAsync(source, cancellation);
            string sourceText = await TypeDisplay.GetAsync(source, cancellation);
            string targetText = await TypeDisplay.GetAsync(target, cancellation);
            diagnostic = diagnostic with { Arguments = [sourceText, targetText] };
            return await ConstraintReasonAsync(diagnostic, originalSource, source, target, sourceText, targetText, cancellation);
        }
        diagnostic = diagnostic with
        {
            Arguments = explanation.Code switch
            {
                2326 or 2530 => [TypeDisplay.SymbolName(explanation.Property!)],
                2634 => [await TypeDisplay.GetAsync(explanation.Source!, cancellation)],
                2330 or 2329 =>
                    [
                        await TypeDisplay.GetAsync(explanation.Source!, cancellation),
                        await TypeDisplay.GetAsync(explanation.Target!, cancellation)
                    ],
                _ => throw new InvalidOperationException($"Unsupported relation explanation {explanation.Code}")
            }
        };
        if (explanation.Code == 2326 && next is { MessageChain.Count: 1 } && next.MessageChain[0] is { Code: 2326 or 2200 } inner)
        {
            static string PropertyPath(string name) => name.Length != 0 && name[0] is '\'' or '"' or '`' ? "[" + name + "]" : name;
            string head = PropertyPath(diagnostic.Arguments[0]), tail = PropertyPath(inner.Arguments[0]);
            diagnostic = diagnostic with
            {
                Message = DiagnosticLocalization.GetMessage(2200),
                Arguments = [head + (tail.StartsWith('[') ? "" : ".") + tail],
                MessageChain = inner.MessageChain
            };
        }
        return diagnostic;
    }

    private async ValueTask<Diagnostic> ConstraintReasonAsync(Diagnostic diagnostic, Type originalSource, Type source, Type target,
        string sourceText, string targetText, CancellationToken cancellation)
    {
        var targetFlags = target is IndexedAccessType indexed && originalSource is not IndexedAccessType
            ? indexed.ObjectType.Flags
            : target.Flags;
        if ((targetFlags & TypeFlags.TypeParameter) == 0 || target == context.MarkerSuper || target == context.MarkerSub)
            return diagnostic;
        var constraint = await Instantiation.Constraints.BaseConstraintAsync(target, cancellation);
        int code;
        string[] arguments;
        if (target is TypeParameter { IsDistributed: true, Constraint: { } distributed }
            && await Relations.RelatedAsync(source, distributed, RelationKind.Assignable, cancellation))
        {
            code = 5113;
            arguments = [sourceText, targetText];
        }
        else if (constraint is not null && await Relations.RelatedAsync(source, constraint, RelationKind.Assignable, cancellation))
        {
            code = 5075;
            arguments = [sourceText, targetText, await TypeDisplay.GetAsync(constraint, cancellation)];
        }
        else if (constraint is not null && await Relations.RelatedAsync(originalSource, constraint, RelationKind.Assignable, cancellation))
        {
            code = 5075;
            arguments =
                [
                    await TypeDisplay.GetAsync(originalSource, cancellation),
                    targetText,
                    await TypeDisplay.GetAsync(constraint, cancellation)
                ];
        }
        else
        {
            code = 5082;
            arguments = [targetText, sourceText];
        }
        var reason = diagnostic with
        {
            Message = DiagnosticLocalization.GetMessage(code),
            Arguments = arguments,
            MessageChain = code == 5082 ? [] : diagnostic.MessageChain,
            RelatedInformation = []
        };
        return diagnostic with { MessageChain = [reason] };
    }

    private async ValueTask<bool> CouldHaveSingletonTypesAsync(Type type, CancellationToken cancellation)
    {
        var pending = new Stack<Type>();
        var seen = new HashSet<Type>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current) || (current.Flags & TypeFlags.Boolean) != 0)
                continue;
            if (current is UnionOrIntersectionType composite)
            {
                foreach (var part in composite.Types)
                    pending.Push(part);
            }
            else if ((current.Flags & TypeFlags.Instantiable) != 0
                && await Instantiation.Constraints.ConstraintAsync(current, cancellation) is { } constraint && constraint != current)
                pending.Push(constraint);
            else if ((current.Flags & (TypeFlags.Unit | TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0)
                return true;
        }
        return false;
    }
}

using System.Globalization;
using TypeScript.Compiler.Ast;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask ReportRelationMessageAsync(SyntaxNode node, int code, Type source, Type target, CancellationToken cancellation)
    {
        if (code is not (2322 or 2344 or 2345 or 2352 or 2375 or 2412 or 2420 or 2720 or 2739 or 2740 or 2741 or 2787 or 2788 or 2789))
        {
            Error(node, code);
            return;
        }
        if ((target.Flags & TypeFlags.Never) == 0 && source.IsLiteral && !await CouldHaveSingletonTypesAsync(target, cancellation))
            source = await Widening.LiteralBaseAsync(source, cancellation);
        string sourceText = await TypeDisplay.GetAsync(source, cancellation);
        string targetText = await TypeDisplay.GetAsync(target, cancellation);
        if (code is 2787 or 2788 or 2789)
            Error(node, code, sourceText);
        else if (code is 2739 or 2740 or 2741 && RequiredPropertyDeclarations.TryGetValue(node, out var missing))
        {
            if (code == 2741)
                Error(node, code, TypeDisplay.SymbolName(missing[0]), sourceText, targetText);
            else if (code == 2740)
                Error(node, code, sourceText, targetText, string.Join(", ", missing.Take(4).Select(TypeDisplay.SymbolName)),
                    (missing.Count - 4).ToString(CultureInfo.InvariantCulture));
            else
                Error(node, code, sourceText, targetText, string.Join(", ", missing.Select(TypeDisplay.SymbolName)));
        }
        else
            Error(node, code, sourceText, targetText);
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

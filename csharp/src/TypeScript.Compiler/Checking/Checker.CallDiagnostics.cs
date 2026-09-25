using System.Globalization;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private static string CountText(int count) => count.ToString(CultureInfo.InvariantCulture);

    private static SyntaxNode CallErrorNode(SyntaxNode node) => node is CallExpressionNode call
        ? call.Expression is PropertyAccessExpressionNode access ? access.Name! : call.Expression! : node;

    private async ValueTask ReportArgumentArityAsync(
        CallResolution.State state,
        IReadOnlyList<Signature> signatures,
        CancellationToken cancellation)
    {
        int spread = Checking.CallArguments.SpreadIndex(state.Arguments);
        if (spread >= 0)
        {
            Error(state.Arguments[spread], 2556);
            return;
        }
        int count = state.Arguments.Count, minimum = int.MaxValue, maximum = int.MinValue, below = int.MinValue, above = int.MaxValue;
        bool rest = false;
        Signature? closest = null;
        foreach (var signature in signatures)
        {
            int min = await Parameters.MinimumAsync(signature, cancellation: cancellation);
            int max = await Parameters.CountAsync(signature, cancellation);
            if (min < minimum)
            {
                minimum = min;
                closest = signature;
            }
            maximum = Math.Max(maximum, max);
            if (min < count)
                below = Math.Max(below, min);
            if (count < max)
                above = Math.Min(above, max);
            rest |= await Parameters.HasRestAsync(signature, cancellation);
        }
        string range = CountText(minimum) + (!rest && minimum < maximum ? "-" + CountText(maximum) : "");
        var node = CallErrorNode(state.Node);
        bool promise = !rest && range == "1" && count == 0 && PromiseResolveArity(state.Node, cancellation);
        if (promise && (state.Node.Flags & NodeFlags.JavaScriptFile) != 0)
        {
            Error(node, 2810);
            return;
        }
        if (minimum < count && count < maximum)
        {
            Error(node, 2575, CountText(count), CountText(below), CountText(above));
            return;
        }
        int code = rest ? 2555 : promise ? 2794 : 2554;
        var diagnostic = CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), range, CountText(count));
        if (count < minimum && closest?.Declaration is IFunctionSignature { Parameters: { } parameters })
        {
            int index = count + (closest.ThisParameter is null ? 0 : 1);
            if (index < parameters.Count && parameters[index] is ParameterDeclarationNode parameter)
            {
                var message = parameter.Name is BindingPatternNode ? Messages.An_argument_matching_this_binding_pattern_was_not_provided
                    : parameter.DotDotDotToken is not null ? Messages.Arguments_for_the_rest_parameter_0_were_not_provided
                    : Messages.An_argument_for_0_was_not_provided;
                diagnostic = diagnostic with
                {
                    RelatedInformation = [CheckerDiagnostic.Create(parameter, message,
                    parameter.Name is BindingPatternNode ? [] : [SyntaxNameText.Get(parameter.Name!)])]
                };
            }
        }
        else if (maximum < count)
        {
            var file = SemanticSyntax.Source(state.Node)!;
            int position = state.Arguments[maximum].Pos, end = state.Arguments[^1].End;
            if (end == position)
                end++;
            int start = CheckerDiagnostic.TokenRange(file, position).Start;
            diagnostic = diagnostic with { Start = start, Length = Math.Max(start, end) - start };
        }
        Error(state.Node, diagnostic);
    }

    private bool PromiseResolveArity(SyntaxNode node, CancellationToken cancellation)
    {
        if (node is not CallExpressionNode { Expression: IdentifierNode name })
            return false;
        var resolver = program.Symbols.NameResolver(cancellation);
        var symbol = resolver.Resolve(name, name.Text, SymbolFlags.Value);
        if (symbol?.ValueDeclaration is not ParameterDeclarationNode { Parent: FunctionExpressionNode or ArrowFunctionNode } declaration
            || declaration.Parent?.Parent is not NewExpressionNode { Expression: IdentifierNode constructor })
            return false;
        var promise = program.Symbols.Lookup(program.Symbols.Globals, "Promise", SymbolFlags.Value);
        return promise is not null && resolver.Resolve(constructor, constructor.Text, SymbolFlags.Value) == promise;
    }

    private void ReportTypeArgumentArity(CallResolution.State state, IReadOnlyList<Signature> signatures)
    {
        int count = state.TypeArguments.Count, code = 2558;
        string[] arguments;
        if (signatures.Count == 1)
        {
            int minimum = Checking.CallSignatures.MinimumTypes(signatures[0]), maximum = signatures[0].TypeParameters.Count;
            arguments = [CountText(minimum) + (minimum < maximum ? "-" + CountText(maximum) : ""), CountText(count)];
        }
        else
        {
            int below = int.MinValue, above = int.MaxValue;
            foreach (var signature in signatures)
            {
                int minimum = Checking.CallSignatures.MinimumTypes(signature), maximum = signature.TypeParameters.Count;
                if (minimum > count)
                    above = Math.Min(above, minimum);
                else if (maximum < count)
                    below = Math.Max(below, maximum);
            }
            if (below != int.MinValue && above != int.MaxValue)
            {
                code = 2743;
                arguments = [CountText(count), CountText(below), CountText(above)];
            }
            else
                arguments = [CountText(below == int.MinValue ? above : below), CountText(count)];
        }
        var diagnostic = CheckerDiagnostic.Create(state.Node, DiagnosticLocalization.GetMessage(code), arguments);
        if (Checking.CallArguments.TypeNodes(state.Node) is { } nodes)
        {
            int start = CheckerDiagnostic.TokenRange(SemanticSyntax.Source(state.Node)!, nodes.Pos).Start;
            diagnostic = diagnostic with { Start = start, Length = Math.Max(0, nodes.End - start) };
        }
        Error(state.Node, diagnostic);
    }
}

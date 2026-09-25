using System.Globalization;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private List<(SyntaxNode Node, Diagnostic Diagnostic)>? callDiagnosticOutput;
    private List<(SyntaxNode Node, Diagnostic Diagnostic)>? relationDiagnosticOutput;

    private void RelationError(SyntaxNode node, int code, params string[] arguments)
        => RelationError(node, CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), arguments));

    private void RelationError(SyntaxNode node, Diagnostic diagnostic)
    {
        if (relationDiagnosticOutput is { } output)
            output.Add((node, diagnostic));
        else
            Error(node, diagnostic);
    }

    public async ValueTask ExpectedPropertyInfoAsync(
        SyntaxNode node,
        Type target,
        Type key,
        Symbol? property,
        CancellationToken cancellation)
    {
        Diagnostic? note = null;
        bool DefaultLibrary(SyntaxNode declaration) => SemanticSyntax.Source(declaration) is { } file
            && program.Symbols.Program.GetFile(file.FileName)?.Library == true;
        if (property is null && await ApplicableIndexAsync(target, key, cancellation) is { Declaration: { } indexDeclaration }
            && !DefaultLibrary(indexDeclaration))
            note = CheckerDiagnostic.Create(indexDeclaration, Messages.The_expected_type_comes_from_this_index_signature);
        else if ((property?.Declarations.FirstOrDefault() ?? target.Symbol?.Declarations.FirstOrDefault()) is { } declaration
            && !DefaultLibrary(declaration))
        {
            string name = MappedMembers.PropertyName(key);
            if (name.Length == 0 || (key.Flags & TypeFlags.UniqueESSymbol) != 0)
                name = await TypeDisplay.GetAsync(key, cancellation);
            note = CheckerDiagnostic.Create(declaration, Messages.The_expected_type_comes_from_property_0_which_is_declared_here_on_type_1,
                name, await TypeDisplay.GetAsync(target, cancellation));
        }
        if (note is null)
            return;
        if (relationDiagnosticOutput is { } output)
        {
            for (int i = output.Count - 1; i >= 0; i--)
                if (output[i].Node == node)
                {
                    var diagnostic = output[i].Diagnostic;
                    output[i] = (node, diagnostic with { RelatedInformation = [.. diagnostic.RelatedInformation, note] });
                    return;
                }
        }
        else
            for (int i = diagnosticFiles.Count - 1; i >= 0; i--)
                if (diagnosticFiles[i].Node == node)
                {
                    var diagnostic = diagnosticFiles[i].Diagnostic;
                    diagnosticFiles[i] = (node, diagnostic with { RelatedInformation = [.. diagnostic.RelatedInformation, note] });
                    return;
                }
    }

    private async ValueTask ReportOverloadFailureAsync(CallResolution.State state, CancellationToken cancellation)
    {
        var last = state.ArgumentErrors[^1];
        var diagnostics = new List<(SyntaxNode Node, Diagnostic Diagnostic)>();
        var previous = callDiagnosticOutput;
        callDiagnosticOutput = diagnostics;
        try
        {
            await CallResolution.ApplicableAsync(state.Node, state.Arguments, last, RelationKind.Assignable, 0,
                true, state.Node is BinaryExpressionNode ? 2860 : 2345, cancellation);
        }
        finally
        {
            callDiagnosticOutput = previous;
        }
        Diagnostic? implementationNote = null;
        if (diagnostics.Count != 0 && last.Declaration is { } declaration
            && program.Symbols.Declaration(declaration) is { Declarations.Count: > 1 } symbol
            && symbol.Declarations.FirstOrDefault(d => d is IFunctionSignature && SemanticSyntax.Body(d) is not null) is { } implementation)
        {
            var signature = await Signatures.FromDeclarationAsync(implementation, cancellation);
            if (await CallResolution.ImplementationApplicableAsync(state, signature, cancellation))
                implementationNote = CheckerDiagnostic.Create(implementation,
                    Messages.The_call_would_have_succeeded_against_this_implementation_but_implementation_signatures_of_overloads_are_not_externally_visible);
        }
        foreach (var (node, value) in diagnostics)
        {
            var diagnostic = value;
            if (state.ArgumentErrors.Count > 1)
            {
                diagnostic = diagnostic with
                {
                    Message = Messages.The_last_overload_gave_the_following_error,
                    Arguments = [],
                    MessageChain = [diagnostic]
                };
                diagnostic = diagnostic with { Message = Messages.No_overload_matches_this_call, MessageChain = [diagnostic] };
                if (last.Declaration is { } lastDeclaration)
                    diagnostic = diagnostic with
                    {
                        RelatedInformation = [.. diagnostic.RelatedInformation,
                        CheckerDiagnostic.Create(lastDeclaration, Messages.The_last_overload_is_declared_here)]
                    };
            }
            if (implementationNote is not null)
                diagnostic = diagnostic with { RelatedInformation = [.. diagnostic.RelatedInformation, implementationNote] };
            Error(node, diagnostic);
        }
    }

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

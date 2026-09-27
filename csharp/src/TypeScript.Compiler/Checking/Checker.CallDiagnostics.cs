using TypeScript.Compiler.Text;
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
    private readonly HashSet<Diagnostic> reportedRelationDiagnostics = new(DiagnosticEqualityComparer.Instance);

    private async ValueTask ReportJsxExcessAsync(SyntaxNode node, Type source, Type target, Symbol property, Type errorTarget,
        CancellationToken cancellation)
    {
        var location = property.ValueDeclaration is JsxAttributeNode attribute
            && SemanticSyntax.Source(attribute) == SemanticSyntax.Source(node) ? attribute.Name! : node;
        TextSlice name = TypeDisplay.SymbolName(property), targetText = await TypeDisplay.GetAsync(errorTarget, cancellation);
        var properties = await Properties.GetAsync(errorTarget, cancellation);
        TextSlice? jsxName = TextSlice.FromNullable(name == "class" ? "className" : name == "for" ? "htmlFor" : null);
        var suggestion = (jsxName is null ? null : properties.FirstOrDefault(p => p.Name == jsxName))
            ?? await SymbolSuggestions.FindAsync(name, properties, SymbolFlags.Value, cancellation);
        var detail = CheckerDiagnostic.Create(
            location,
            DiagnosticLocalization.GetMessage(
                suggestion is null ? DiagnosticCode.Property0DoesNotExistOnType1 : DiagnosticCode.Property0DoesNotExistOnType1DidYouMean2),
            suggestion is null ? [name, targetText] : [name, targetText, TypeDisplay.SymbolName(suggestion)]);
        var (errorSource, errorDestination) = await RelationErrorTypesAsync(source, target, cancellation);
        var (sourceText, destinationText) = await RelationTypeNamesAsync(errorSource, errorDestination, cancellation);
        var diagnostic = CheckerDiagnostic.Create(location, Messages.Type_0_is_not_assignable_to_type_1, sourceText, destinationText)
            with
        { MessageChain = [detail] };
        if (relationDiagnosticHead is { } head)
            diagnostic = diagnostic with { Message = DiagnosticLocalization.GetMessage(head), Arguments = [], MessageChain = [diagnostic] };
        RelationError(location, diagnostic);
    }

    private void RelationError(SyntaxNode node, DiagnosticCode code, params TextSlice[] arguments)
        => RelationError(node, CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), arguments));

    private void RelationError(SyntaxNode node, Diagnostic diagnostic)
    {
        // Match the reference's addDiagnostic guard during recursive TypeToString calls.
        if (TypeDisplay.AtRecursionLimit)
            return;
        if (relationDiagnosticOutput is { } output)
            output.Add((node, diagnostic));
        else
        {
            if (reportedRelationDiagnostics.Add(diagnostic))
            {
                Diagnostics.Add(diagnostic.Code);
                diagnosticFiles.Add((node, diagnostic));
            }
        }
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
            TextSlice name = MappedMembers.PropertyName(key);
            if (name.Length == 0 || (key.Flags & TypeFlags.UniqueESSymbol) != 0)
                name = await TypeDisplay.GetAsync(key, cancellation);
            note = CheckerDiagnostic.Create(declaration, Messages.The_expected_type_comes_from_property_0_which_is_declared_here_on_type_1,
                name, await TypeDisplay.GetAsync(target, cancellation));
        }
        if (note is null)
            return;
        AddRelationNote(node.Parent is PropertyAssignmentNode assignment && assignment.Name == node ? assignment : node, note);
    }

    public void ExpectedReturnInfo(ArrowFunctionNode node, Type target, bool suggestAsync)
    {
        if (target.Symbol?.Declarations.FirstOrDefault() is { } declaration)
            AddRelationNote(
                node.Body!,
                CheckerDiagnostic.Create(declaration, Messages.The_expected_type_comes_from_the_return_type_of_this_signature));
        if (suggestAsync)
            AddRelationNote(node.Body!, CheckerDiagnostic.Create(node, Messages.Did_you_mean_to_mark_this_function_as_async));
    }

    private void AddRelationNote(SyntaxNode node, Diagnostic note)
    {
        if (relationDiagnosticOutput is { } output)
        {
            for (int i = output.Count - 1; i >= 0; i--)
                if (Contains(output[i].Node))
                {
                    var diagnostic = output[i].Diagnostic;
                    if (diagnostic.RelatedInformation.Contains(note, DiagnosticEqualityComparer.Instance))
                        return;
                    output[i] = (output[i].Node, diagnostic with { RelatedInformation = [.. diagnostic.RelatedInformation, note] });
                    return;
                }
        }
        else
            for (int i = diagnosticFiles.Count - 1; i >= 0; i--)
                if (Contains(diagnosticFiles[i].Node))
                {
                    var diagnostic = diagnosticFiles[i].Diagnostic;
                    if (diagnostic.RelatedInformation.Contains(note, DiagnosticEqualityComparer.Instance))
                        return;
                    diagnosticFiles[i] = (diagnosticFiles[i].Node, diagnostic with
                    {
                        RelatedInformation =
                        [
                            .. diagnostic.RelatedInformation,
                            note
                        ]
                    });
                    return;
                }
        bool Contains(SyntaxNode? location)
        {
            for (; location is not null; location = location.Parent)
                if (location == node)
                    return true;
            return false;
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
                true, DiagnosticCode.ArgumentOfType0IsNotAssignableToParameterOfType1, cancellation);
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
            if (state.Node is BinaryExpressionNode)
                diagnostic = diagnostic with
                {
                    Message = DiagnosticLocalization.GetMessage(
                        DiagnosticCode.TheLeftHandSideOfAnInstanceofExpressionMustBeAssignableToTheFirstArgumentOfTheRightHandSideSSymbolHasInstanceMethod),
                    Arguments = [],
                    MessageChain = [diagnostic]
                };
            Error(node, diagnostic);
        }
    }

    private static TextSlice CountText(int count) => TextSlice.Format(count);

    private static SyntaxNode CallErrorNode(SyntaxNode node) => node is CallExpressionNode call
        ? call.Expression is PropertyAccessExpressionNode access ? access.Name! : call.Expression! : node;

    private async ValueTask ReportArgumentArityAsync(
        CallResolution.State state,
        IReadOnlyList<Signature> signatures,
        CancellationToken cancellation,
        DiagnosticCode? head = null)
    {
        int spread = Checking.CallArguments.SpreadIndex(state.Arguments);
        if (spread >= 0)
        {
            Error(state.Arguments[spread], DiagnosticCode.ASpreadArgumentMustEitherHaveATupleTypeOrBePassedToARestParameter);
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
        TextSlice range = TextSlice.Concat(CountText(minimum), (!rest && minimum < maximum ? TextSlice.Concat("-", CountText(maximum)) : ""));
        var node = CallErrorNode(state.Node);
        bool promise = !rest && range == "1" && count == 0 && PromiseResolveArity(state.Node, cancellation);
        if (promise && (state.Node.Flags & NodeFlags.JavaScriptFile) != 0)
        {
            Error(node, DiagnosticCode.Expected1ArgumentButGot0NewPromiseNeedsAJSDocHintToProduceAResolveThatCanBeCalledWithoutArguments);
            return;
        }
        if (minimum < count && count < maximum)
        {
            Error(
                node,
                DiagnosticCode.NoOverloadExpects0ArgumentsButOverloadsDoExistThatExpectEither1Or2Arguments,
                CountText(count),
                CountText(below),
                CountText(above));
            return;
        }
        DiagnosticCode code = state.Node is DecoratorNode
            ? rest
                ? DiagnosticCode.TheRuntimeWillInvokeTheDecoratorWith1ArgumentsButTheDecoratorExpectsAtLeast0
                : DiagnosticCode.TheRuntimeWillInvokeTheDecoratorWith1ArgumentsButTheDecoratorExpects0
            : rest
                ? DiagnosticCode.ExpectedAtLeast0ArgumentsButGot1
                : promise
                    ? DiagnosticCode.Expected0ArgumentsButGot1DidYouForgetToIncludeVoidInYourTypeArgumentToPromise
                    : DiagnosticCode.Expected0ArgumentsButGot1;
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
        Error(state.Node, head is null ? diagnostic : diagnostic with
        {
            Message = DiagnosticLocalization.GetMessage(head.Value),
            Arguments = [],
            MessageChain = [diagnostic with { RelatedInformation = [] }]
        });
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
        int count = state.TypeArguments.Count;
        DiagnosticCode code = DiagnosticCode.Expected0TypeArgumentsButGot1;
        TextSlice[] arguments;
        if (signatures.Count == 1)
        {
            int minimum = Checking.CallSignatures.MinimumTypes(signatures[0]), maximum = signatures[0].TypeParameters.Count;
            arguments = [TextSlice.Concat(CountText(minimum), (minimum < maximum ? TextSlice.Concat("-", CountText(maximum)) : "")), CountText(count)];
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
                code = DiagnosticCode.NoOverloadExpects0TypeArgumentsButOverloadsDoExistThatExpectEither1Or2TypeArguments;
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

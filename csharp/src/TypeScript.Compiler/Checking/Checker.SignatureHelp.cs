using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.LanguageServices;

namespace TypeScript.Compiler.Checking;

internal sealed record SignatureHelpParameterDisplay(Utf8String Label, Symbol? Symbol);
internal sealed record SignatureHelpDisplay(IReadOnlyList<ClassifiedTextRun> Runs,
    IReadOnlyList<SignatureHelpParameterDisplay> Parameters, bool Variadic);

internal sealed partial class Checker
{
    private const NodeBuilderFlags SignatureHelpFlags = NodeBuilderFlags.OmitParameterModifiers | NodeBuilderFlags.IgnoreErrors
        | NodeBuilderFlags.UseAliasDefinedOutsideCurrentScope;

    internal ValueTask<IReadOnlyList<SignatureHelpDisplay>> GetSignatureHelpDisplaysAsync(Signature signature, bool typeArguments,
        SyntaxNode enclosing, SourceFileNode source, CancellationToken cancellation) => VisibilityQueryAsync(enclosing,
        () => ChainOperationAsync(() => ContainerOperationAsync(async () =>
        {
            var allocated = new List<Symbol>();
            try
            {
                var state = new TypeSyntaxContext(enclosing, true, flags: SignatureHelpFlags);
                List<IReadOnlyList<Symbol>> lists = [];
                if (signature.HasRestParameter && await Values.GetAsync(signature.Parameters[^1], cancellation) is UnionType union
                    && union.Types.All(type => type is TypeReference { Target: TupleType }))
                {
                    foreach (TypeReference type in union.Types)
                        lists.Add(await ExpandSyntaxTupleAsync(signature, type, (TupleType)type.Target!, allocated, cancellation));
                }
                else lists.Add(await ExpandedSyntaxParametersAsync(signature, allocated, cancellation));
                List<SignatureHelpParameterDisplay> typeParameters = [];
                foreach (var parameter in (typeArguments ? signature.Target ?? signature : signature).TypeParameters)
                {
                    state = new(enclosing, true, flags: SignatureHelpFlags);
                    typeParameters.Add(new(await TypeParameterLabelAsync(parameter, state, source, cancellation), null));
                }
                var prefix = new DisplayParts(true);
                if (typeArguments || typeParameters.Count != 0)
                {
                    prefix.Punctuation("<"u8);
                    for (int i = 0; i < typeParameters.Count; i++)
                    {
                        if (i != 0) prefix.Punctuation(", "u8);
                        prefix.Write("type parameter name"u8, typeParameters[i].Label);
                    }
                    prefix.Punctuation(">"u8);
                }
                prefix.Punctuation("("u8);
                state = new(enclosing, true, flags: SignatureHelpFlags);
                var suffix = new DisplayParts(true).Punctuation(": "u8);
                if (await Signatures.PredicateAsync(signature, cancellation) is { } predicate)
                    suffix.Append(await TypeDisplay.GetPredicateAsync(predicate, null, TypeFormatFlags.UseAliasDefinedOutsideCurrentScope, cancellation));
                else
                    suffix.Copy(Print(await TypeSyntaxAsync(await Signatures.ReturnAsync(signature, cancellation), state, cancellation)));
                List<SignatureHelpDisplay> result = [];
                bool hasRest = await Parameters.HasRestAsync(signature, cancellation);
                foreach (var list in lists)
                {
                    var parts = new DisplayParts(true); parts.Copy(prefix.Runs);
                    List<SignatureHelpParameterDisplay> parameters = [];
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i != 0) parts.Punctuation(", "u8);
                        state = new(enclosing, true, flags: SignatureHelpFlags);
                        var runs = Print(await ParameterSyntaxAsync(list[i], state, cancellation));
                        parts.Copy(runs);
                        parameters.Add(new(Utf8String.Concat(runs.Select(run => run.Text)), list[i]));
                    }
                    parts.Punctuation(")"u8); parts.Copy(suffix.Runs);
                    result.Add(new(parts.Runs, typeArguments ? typeParameters : parameters,
                        !typeArguments && hasRest && (lists.Count == 1 || list.Count != 0 && (list[^1].CheckFlags & CheckFlags.RestParameter) != 0)));
                }
                return (IReadOnlyList<SignatureHelpDisplay>)result;

                IReadOnlyList<ClassifiedTextRun> Print(SyntaxNode node) => SyntaxPrinter.PrintDisplay(node, source,
                    new Dictionary<SyntaxNode, Symbol>(), SignatureHelpEmit(state), cancellation);
            }
            finally { foreach (var symbol in allocated) links.Values.Remove(symbol); }
        }, cancellation), cancellation), cancellation);

    internal ValueTask<IReadOnlyList<Utf8String>> GetSignatureHelpTypeParametersAsync(Symbol symbol, SyntaxNode enclosing,
        CancellationToken cancellation) => VisibilityQueryAsync(enclosing, () => ChainOperationAsync(() => ContainerOperationAsync(async () =>
        {
            List<Utf8String> result = [];
            foreach (var parameter in LocalTypeParameters(symbol, cancellation))
                result.Add(await TypeParameterLabelAsync(parameter, new(enclosing, true, flags: SignatureHelpFlags), SemanticSyntax.Source(enclosing), cancellation));
            return (IReadOnlyList<Utf8String>)result;
        }, cancellation), cancellation), cancellation);

    private async ValueTask<Utf8String> TypeParameterLabelAsync(Type parameter, TypeSyntaxContext state, SourceFileNode? source, CancellationToken cancellation)
    {
        var constraint = await Instantiation.Constraints.ConstraintAsync(parameter, cancellation);
        var node = await TypeParameterSyntaxAsync((TypeParameter)parameter,
            constraint is null ? null : await ConstraintSyntaxAsync((TypeParameter)parameter, constraint, state, cancellation), state, cancellation);
        return new SyntaxPrinter(context: SignatureHelpEmit(state)).Print(node, source, cancellation: cancellation);
    }

    private static EmitContext SignatureHelpEmit(TypeSyntaxContext state)
    {
        var emit = new EmitContext();
        foreach (var node in state.NoAsciiEscape) emit.AddFlags(node, EmitFlags.NoAsciiEscaping);
        foreach (var node in state.SingleLine) emit.AddFlags(node, EmitFlags.SingleLine);
        return emit;
    }
}

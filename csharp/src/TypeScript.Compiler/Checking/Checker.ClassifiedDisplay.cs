using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.LanguageServices;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal ValueTask<IReadOnlyList<ClassifiedTextRun>> GetClassifiedTypeDisplayAsync(Type type, SyntaxNode? enclosing, TypeFormatFlags flags,
        CancellationToken cancellation) => ClassifiedSyntaxAsync(enclosing, flags, state => TypeSyntaxAsync(type, state, cancellation), cancellation);

    internal ValueTask<IReadOnlyList<ClassifiedTextRun>> GetClassifiedSignatureDisplayAsync(Signature signature, SyntaxNode? enclosing,
        TypeFormatFlags flags, CancellationToken cancellation)
    {
        bool construct = (signature.Flags & SignatureFlags.Construct) != 0 && (flags & TypeFormatFlags.WriteCallStyleSignature) == 0;
        var kind = (flags & TypeFormatFlags.WriteArrowStyleSignature) != 0
            ? construct ? K.ConstructorType : K.FunctionType : construct ? K.ConstructSignature : K.CallSignature;
        return ClassifiedSyntaxAsync(enclosing, flags, state => SignatureSyntaxAsync(signature, kind, state, cancellation), cancellation);
    }

    private ValueTask<IReadOnlyList<ClassifiedTextRun>> ClassifiedSyntaxAsync(SyntaxNode? enclosing, TypeFormatFlags flags,
        Func<TypeSyntaxContext, ValueTask<SyntaxNode>> build, CancellationToken cancellation)
        => VisibilityQueryAsync(enclosing, () => ChainOperationAsync(() => ContainerOperationAsync(async () =>
        {
            var state = new TypeSyntaxContext(enclosing, true, flags: (NodeBuilderFlags)(flags & TypeFormatFlags.NodeBuilderFlagsMask)
                | NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.UseAliasDefinedOutsideCurrentScope | NodeBuilderFlags.WriteTypeParametersInQualifiedName)
            { DisplaySymbols = [] };
            var node = await build(state);
            var emit = new EmitContext();
            foreach (var item in state.NoAsciiEscape) emit.AddFlags(item, EmitFlags.NoAsciiEscaping);
            foreach (var item in state.SingleLine) emit.AddFlags(item, EmitFlags.SingleLine);
            return SyntaxPrinter.PrintDisplay(node, enclosing is null ? null : Binding.SemanticSyntax.Source(enclosing), state.DisplaySymbols, emit, cancellation);
        }, cancellation), cancellation), cancellation);
}

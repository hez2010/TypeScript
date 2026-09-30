using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal TypeParameter? VarianceTypeParameter { get; private set; }

    private void TypeParameterGrammar(TypeParameterDeclarationNode node)
    {
        if (node.Modifiers is not { } modifiers || SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        bool input = false, output = false;
        foreach (var modifier in modifiers)
        {
            DiagnosticCode code = DiagnosticCode.None;
            switch (modifier.Kind)
            {
                case SyntaxKind.ConstKeyword:
                    if (node.Parent is not IFunctionSignature && !SemanticSyntax.ClassLike(node.Parent))
                        code = DiagnosticCode.X0ModifierCanOnlyAppearOnATypeParameterOfAFunctionMethodOrClass;
                    break;
                case SyntaxKind.InKeyword:
                case SyntaxKind.OutKeyword:
                    if (node.Parent is not (InterfaceDeclarationNode or TypeAliasDeclarationNode)
                        && !SemanticSyntax.ClassLike(node.Parent))
                        code = DiagnosticCode.X0ModifierCanOnlyAppearOnATypeParameterOfAClassInterfaceOrTypeAlias;
                    else if (modifier.Kind == SyntaxKind.InKeyword ? input : output)
                        code = DiagnosticCode.X0ModifierAlreadySeen;
                    else if (modifier.Kind == SyntaxKind.InKeyword && output)
                        code = DiagnosticCode.X0ModifierMustPrecede1Modifier;
                    input |= modifier.Kind == SyntaxKind.InKeyword;
                    output |= modifier.Kind == SyntaxKind.OutKeyword;
                    break;
                default:
                    code = modifier is DecoratorNode
                        ? DiagnosticCode.DecoratorsAreNotValidHere
                        : DiagnosticCode.X0ModifierCannotAppearOnATypeParameter;
                    break;
            }
            if (code != DiagnosticCode.None)
            {
                Error(modifier, code, code == DiagnosticCode.X0ModifierMustPrecede1Modifier ? [Utf8Literals.InKeyword, Utf8Literals.Out]
                    : code is DiagnosticCode.X0ModifierCanOnlyAppearOnATypeParameterOfAFunctionMethodOrClass
                        or DiagnosticCode.X0ModifierCanOnlyAppearOnATypeParameterOfAClassInterfaceOrTypeAlias
                        or DiagnosticCode.X0ModifierAlreadySeen or DiagnosticCode.X0ModifierCannotAppearOnATypeParameter
                        ? [TokenFacts.Text(modifier.Kind)]
                        : []);
                return;
            }
        }
    }

    private async ValueTask CheckTypeParameterVarianceAsync(TypeParameterDeclarationNode node, CancellationToken cancellation)
    {
        if (node.Parent is not (InterfaceDeclarationNode or TypeAliasDeclarationNode) && !SemanticSyntax.ClassLike(node.Parent))
            return;
        var parameter = program.Scopes.Parameter(program.Symbols.Declaration(node)!);
        bool input = parameter.Symbol!.Declarations.Any(d => SemanticSyntax.HasModifier(d, SyntaxKind.InKeyword));
        bool output = parameter.Symbol.Declarations.Any(d => SemanticSyntax.HasModifier(d, SyntaxKind.OutKeyword));
        if (!input && !output)
            return;
        var symbol = program.Symbols.Declaration(node.Parent!)!;
        var type = await Declared.GetAsync(symbol, cancellation).ConfigureAwait(false);
        if (node.Parent is TypeAliasDeclarationNode && (type.ObjectFlags & (ObjectFlags.Anonymous | ObjectFlags.Mapped)) == 0)
        {
            Error(node, DiagnosticCode.VarianceAnnotationsAreOnlySupportedInTypeAliasesForObjectFunctionConstructorAndMappedTypes);
            return;
        }
        if (input == output)
            return;
        var source = await Variances.MarkerAsync(symbol, parameter,
            output ? context.VarianceCheckSub : context.VarianceCheckSuper, cancellation).ConfigureAwait(false);
        var target = await Variances.MarkerAsync(symbol, parameter,
            output ? context.VarianceCheckSuper : context.VarianceCheckSub, cancellation).ConfigureAwait(false);
        var previous = VarianceTypeParameter;
        VarianceTypeParameter = parameter;
        try
        {
            await RelationDiagnostics.CheckAsync(
                source,
                target,
                RelationKind.Assignable,
                node,
                null,
                DiagnosticCode.Type0IsNotAssignableToType1AsImpliedByVarianceAnnotation,
                cancellation);
        }
        finally
        {
            VarianceTypeParameter = previous;
        }
    }
}

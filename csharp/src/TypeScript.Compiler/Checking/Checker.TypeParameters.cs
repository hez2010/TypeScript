using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
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
            int code = 0;
            switch (modifier.Kind)
            {
                case SyntaxKind.ConstKeyword:
                    if (node.Parent is not IFunctionSignature && !SemanticSyntax.ClassLike(node.Parent))
                        code = 1277;
                    break;
                case SyntaxKind.InKeyword:
                case SyntaxKind.OutKeyword:
                    if (node.Parent is not (InterfaceDeclarationNode or TypeAliasDeclarationNode)
                        && !SemanticSyntax.ClassLike(node.Parent))
                        code = 1274;
                    else if (modifier.Kind == SyntaxKind.InKeyword ? input : output)
                        code = 1030;
                    else if (modifier.Kind == SyntaxKind.InKeyword && output)
                        code = 1029;
                    input |= modifier.Kind == SyntaxKind.InKeyword;
                    output |= modifier.Kind == SyntaxKind.OutKeyword;
                    break;
                default:
                    code = modifier is DecoratorNode ? 1206 : 1273;
                    break;
            }
            if (code != 0)
            {
                Error(modifier, code);
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
            Error(node, 2637);
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
            if (!await Relations.RelatedAsync(source, target, RelationKind.Assignable, cancellation).ConfigureAwait(false))
                Error(node, 2636);
        }
        finally
        {
            VarianceTypeParameter = previous;
        }
    }
}

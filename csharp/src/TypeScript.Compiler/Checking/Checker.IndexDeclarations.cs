using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask CheckIndexSignatureSourceAsync(IndexSignatureDeclarationNode node, CancellationToken cancellation)
    {
        ClassMemberModifiers(node);
        if (TypeScript.Compiler.Binding.SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            await GrammarAsync().ConfigureAwait(false);
        await FunctionDeclarations.CheckAsync(node, cancellation).ConfigureAwait(false);

        async ValueTask GrammarAsync()
        {
            if (node.Parameters is not { Count: 1 } parameters)
            {
                Error(node.Parameters?.FirstOrDefault() is ParameterDeclarationNode p ? p.Name! : node, 1096);
                return;
            }
            var parameter = (ParameterDeclarationNode)parameters[0];
            if (parameters.HasTrailingComma)
                Error(node, 1025);
            if (parameter.DotDotDotToken is not null)
            {
                Error(parameter.DotDotDotToken, 1017);
                return;
            }
            if (parameter.Modifiers is not null)
            {
                Error(parameter.Name!, 1018);
                return;
            }
            if (parameter.QuestionToken is not null)
            {
                Error(parameter.QuestionToken, 1019);
                return;
            }
            if (parameter.Initializer is not null)
            {
                Error(parameter.Name!, 1020);
                return;
            }
            if (parameter.Type is null)
            {
                Error(parameter.Name!, 1022);
                return;
            }
            var type = await Nodes.FromNodeAsync(parameter.Type, cancellation).ConfigureAwait(false);
            var parts = type is UnionType union ? union.Types : [type];
            if (parts.Any(t => (t.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0)
                || await IsGenericTypeAsync(type, cancellation).ConfigureAwait(false))
            {
                Error(parameter.Name!, 1337);
                return;
            }
            foreach (var part in parts)
                if (!await Instantiation.Members.ValidIndexKeyAsync(part, cancellation).ConfigureAwait(false))
                {
                    Error(parameter.Name!, 1268);
                    return;
                }
            if (node.Type is null)
                Error(node, 1021);
        }
    }
}

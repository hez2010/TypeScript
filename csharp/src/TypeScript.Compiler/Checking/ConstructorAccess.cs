using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed class ConstructorAccess(CheckerSymbols symbols, DeclaredTypes declared, BaseTypes bases,
    CompositeMembers composites, ICallResolutionHost host)
{
    internal async ValueTask<bool> CheckAsync(
        SyntaxNode node,
        IReadOnlyList<Signature> signatures,
        CancellationToken cancellation = default)
    {
        foreach (var signature in signatures)
        {
            if (signature.Declaration is not ConstructorDeclarationNode constructor)
                continue;
            bool privateAccess = SemanticSyntax.HasModifier(constructor, SyntaxKind.PrivateKeyword);
            bool protectedAccess = SemanticSyntax.HasModifier(constructor, SyntaxKind.ProtectedKeyword);
            if (!privateAccess && !protectedAccess)
                continue;
            var target = symbols.Declaration(constructor.Parent!)!;
            var declaration = target.Declarations.First(SemanticSyntax.ClassLike);
            if (DeclarationOrder.Ancestor(node, n => n == declaration) is not null)
                continue;
            if (protectedAccess && DeclarationOrder.Ancestor(node, SemanticSyntax.ClassLike) is { } containing
                && await ProtectedBaseAsync(
                    target,
                    await declared.GetAsync(symbols.Declaration(containing)!, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false))
                continue;
            await declared.GetAsync(target, cancellation).ConfigureAwait(false);
            if (privateAccess)
                host.ExpressionError(node, 2673);
            if (protectedAccess)
                host.ExpressionError(node, 2674);
            return false;
        }
        return true;
    }

    private async ValueTask<bool> ProtectedBaseAsync(Symbol target, Type type, CancellationToken cancellation)
    {
        var pending = new Stack<Type>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            var actual = current is TypeReference { Target: InterfaceType generic } ? generic : (InterfaceType)current;
            var baseTypes = await bases.GetAsync(actual, cancellation).ConfigureAwait(false);
            if (baseTypes.Count == 0)
                continue;
            var first = baseTypes[0];
            if (first is IntersectionType intersection)
            {
                var mixins = new bool[intersection.Types.Count];
                int total = 0, mixinCount = 0, firstMixin = -1;
                for (int i = 0; i < mixins.Length; i++)
                {
                    var signatures = await host.SignaturesAsync(intersection.Types[i], true, cancellation).ConfigureAwait(false);
                    mixins[i] = await composites.MixinAsync(signatures, cancellation).ConfigureAwait(false);
                    if (signatures.Count != 0)
                        total++;
                    if (mixins[i])
                    {
                        mixinCount++;
                        if (firstMixin < 0)
                            firstMixin = i;
                    }
                }
                if (total > 0 && total == mixinCount)
                    mixins[firstMixin] = false;
                for (int i = intersection.Types.Count - 1; i >= 0; i--)
                    if (!mixins[i] && (intersection.Types[i].ObjectFlags & ObjectFlags.ClassOrInterface) != 0)
                    {
                        if (intersection.Types[i].Symbol == target)
                            return true;
                        pending.Push(intersection.Types[i]);
                    }
            }
            else
            {
                if (first.Symbol == target)
                    return true;
                pending.Push(first);
            }
        }
        return false;
    }
}

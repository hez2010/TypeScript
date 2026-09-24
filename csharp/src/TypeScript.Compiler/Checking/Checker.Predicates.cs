using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask CheckTypePredicateAsync(TypePredicateNode node, CancellationToken cancellation)
    {
        var parent = node.Parent;
        if (parent is not (ArrowFunctionNode or CallSignatureDeclarationNode or FunctionDeclarationNode or FunctionExpressionNode
            or FunctionTypeNode or MethodDeclarationNode or MethodSignatureDeclarationNode)
            || parent is not ITypedNode typed || typed.Type != node)
        {
            Error(node, 1228);
            return;
        }
        var signature = await Signatures.FromDeclarationAsync(parent, cancellation).ConfigureAwait(false);
        var predicate = await Signatures.PredicateAsync(signature, cancellation).ConfigureAwait(false);
        if (predicate is null || predicate.Kind is TypePredicateKind.This or TypePredicateKind.AssertsThis)
            return;
        if (predicate.ParameterIndex >= 0)
        {
            if ((signature.Flags & SignatureFlags.HasRestParameter) != 0 && predicate.ParameterIndex == signature.Parameters.Count - 1)
                Error(node.ParameterName!, 1229);
            else if (predicate.Type is { } type && !await AssignableAsync(type,
                await Values.GetAsync(signature.Parameters[predicate.ParameterIndex], cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false))
                Error(node.Type!, 2677);
        }
        else if (node.ParameterName is { } name)
        {
            var pending = new Stack<BindingPatternNode>();
            foreach (ParameterDeclarationNode parameter in ((IFunctionSignature)parent).Parameters!)
                if (parameter.Name is BindingPatternNode pattern)
                    pending.Push(pattern);
            while (pending.TryPop(out var pattern))
                foreach (var child in pattern.Elements!)
                    if (child is BindingElementNode element)
                    {
                        if (element.Name is IdentifierNode identifier && identifier.Text == predicate.ParameterName)
                        {
                            Error(name, 1230);
                            return;
                        }
                        if (element.Name is BindingPatternNode nested)
                            pending.Push(nested);
                    }
            Error(name, 1225);
        }
    }
}

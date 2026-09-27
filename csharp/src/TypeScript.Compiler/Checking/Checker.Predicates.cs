using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;

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
            Error(node, DiagnosticCode.ATypePredicateIsOnlyAllowedInReturnTypePositionForFunctionsAndMethods);
            return;
        }
        var signature = await Signatures.FromDeclarationAsync(parent, cancellation).ConfigureAwait(false);
        var predicate = await Signatures.PredicateAsync(signature, cancellation).ConfigureAwait(false);
        if (predicate is null || predicate.Kind is TypePredicateKind.This or TypePredicateKind.AssertsThis)
            return;
        if (predicate.ParameterIndex >= 0)
        {
            if ((signature.Flags & SignatureFlags.HasRestParameter) != 0 && predicate.ParameterIndex == signature.Parameters.Count - 1)
                Error(node.ParameterName!, DiagnosticCode.ATypePredicateCannotReferenceARestParameter);
            else if (predicate.Type is { } type)
            {
                var parameterType = await Values.GetAsync(
                    signature.Parameters[predicate.ParameterIndex],
                    cancellation).ConfigureAwait(false);
                if (!await AssignableAsync(type, parameterType, cancellation).ConfigureAwait(false))
                    await ReportRelationMessageAsync(
                        node.Type!,
                        DiagnosticCode.Type0IsNotAssignableToType1,
                        type,
                        parameterType,
                        RelationKind.Assignable,
                        cancellation,
                        CheckerDiagnostic.Create(node.Type!, Messages.A_type_predicate_s_type_must_be_assignable_to_its_parameter_s_type));
            }
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
                            Error(name, DiagnosticCode.ATypePredicateCannotReferenceElement0InABindingPattern, predicate.ParameterName!);
                            return;
                        }
                        if (element.Name is BindingPatternNode nested)
                            pending.Push(nested);
                    }
            Error(name, DiagnosticCode.CannotFindParameter0, predicate.ParameterName!);
        }
    }
}

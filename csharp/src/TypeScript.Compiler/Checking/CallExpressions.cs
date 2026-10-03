using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface ICallExpressionHost
{
    bool NoImplicitAny { get; }

    ValueTask CallGrammarAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask DeprecatedSignatureAsync(SyntaxNode node, Signature signature, CancellationToken cancellation);

    ValueTask<Type?> SpecialCallResultAsync(SyntaxNode node, Type result, CancellationToken cancellation);

    ValueTask CheckAssertionCallAsync(CallExpressionNode node, CancellationToken cancellation);

    void ExpressionError(SyntaxNode node, DiagnosticCode code);
}

internal sealed class CallExpressions(TypeContext context, CallResolution resolution, CallSignatures rules, Signatures signatures,
    ICallExpressionHost host)
{
    internal async ValueTask<Type> CheckAsync(SyntaxNode node, CheckMode mode = 0, CancellationToken cancellation = default)
    {
        await host.CallGrammarAsync(node, cancellation).ConfigureAwait(false);
        var signature = await resolution.GetAsync(node, mode: mode, cancellation: cancellation).ConfigureAwait(false);
        if (signature == rules.Resolving)
            return context.SilentNeverType;
        await host.DeprecatedSignatureAsync(node, signature, cancellation);
        if (CallArguments.Target(node)?.Kind == SyntaxKind.SuperKeyword)
            return context.VoidType;
        if (node is NewExpressionNode && signature.Declaration is { } declaration
            && declaration is not ConstructorDeclarationNode and not ConstructSignatureDeclarationNode and not ConstructorTypeNode)
        {
            if (host.NoImplicitAny)
                host.ExpressionError(node, DiagnosticCode.XNewExpressionWhoseTargetLacksAConstructSignatureImplicitlyHasAnAnyType);
            return context.AnyType;
        }
        var result = await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false);
        if (await host.SpecialCallResultAsync(node, result, cancellation).ConfigureAwait(false) is { } special)
            return special;
        if (node is CallExpressionNode { QuestionDotToken: null, Parent: ExpressionStatementNode } call
            && (result.Flags & TypeFlags.Void) != 0 && await signatures.PredicateAsync(
                signature,
                cancellation).ConfigureAwait(false) is not null)
            await host.CheckAssertionCallAsync(call, cancellation).ConfigureAwait(false);
        return result;
    }
}

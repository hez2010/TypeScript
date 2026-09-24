using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IFunctionWideningHost
{
    bool NoImplicitAny { get; }

    ValueTask<Type?> ContextualIterationAsync(SyntaxNode node, Type type, WideningKind kind, CancellationToken cancellation);

    ValueTask ReportImplicitAnyAsync(SyntaxNode node, Type type, WideningKind kind, CancellationToken cancellation);
}

internal sealed class FunctionWidening(FunctionContexts contexts, Signatures signatures, AwaitedTypes awaited,
    MappedTypes mapped, WideningDiagnostics diagnostics, IFunctionWideningHost host)
{
    internal async ValueTask ReportAsync(SyntaxNode node, Type type, WideningKind kind, CancellationToken cancellation = default)
    {
        if (!host.NoImplicitAny || (type.ObjectFlags & ObjectFlags.ContainsWideningType) == 0)
            return;
        var signature = await contexts.GetAsync(node, cancellation).ConfigureAwait(false);
        if (signature is not null)
        {
            Type? contextual = await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false);
            if (SemanticSyntax.Generator(node))
                contextual = await host.ContextualIterationAsync(node, contextual, kind, cancellation).ConfigureAwait(false);
            else if (SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword))
                contextual = await awaited.GetAsync(contextual, false, cancellation: cancellation).ConfigureAwait(false) ?? contextual;
            if (contextual is null || await mapped.GenericFlagsAsync(contextual, cancellation).ConfigureAwait(false) == 0)
                return;
        }
        if (!await diagnostics.InsideAsync(type, cancellation).ConfigureAwait(false))
            await host.ReportImplicitAnyAsync(node, type, kind, cancellation).ConfigureAwait(false);
    }
}

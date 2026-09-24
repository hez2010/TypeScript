using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface IFunctionExpressionHost
{
    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);

    ValueTask CheckFunctionDeclarationAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask FunctionGrammarAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask FunctionNameAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Signature?> FullSignatureAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask CheckFullSignatureAsync(SyntaxNode node, CancellationToken cancellation);

    void DeferExpression(SyntaxNode node);
}

internal sealed class FunctionExpressions(TypeContext context, CheckerLinks links, CheckerSymbols symbols, SymbolTypes values,
    Signatures signatures, FunctionContexts contexts, FunctionBodies bodies, ExpressionContexts expressions,
    SignatureParameters parameters, TypeInstantiation instantiation, TypeVariables variables, IFunctionExpressionHost host)
{
    private List<Action>? rollback;

    internal void RecordExpressionCache(SyntaxNode node, Type type)
    {
        if (rollback is null)
            return;
        var data = links.TypeNodes.Get(node);
        var previous = data.ResolvedType;
        rollback.Add(() =>
        {
            if (data.ResolvedType == type)
                data.ResolvedType = previous;
        });
    }

    internal async ValueTask<Type> CheckAsync(SyntaxNode node, CheckMode mode = 0, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        host.DeferExpression(node);
        if (node is FunctionExpressionNode)
            await host.FunctionNameAsync(node, cancellation).ConfigureAwait(false);
        if ((mode & CheckMode.SkipContextSensitive) != 0 && FunctionSyntax.Sensitive(node, symbols))
        {
            if (node is ITypedNode { Type: null } && !FunctionSyntax.SensitiveParameters(node, symbols)
                && await contexts.GetAsync(node, cancellation).ConfigureAwait(false) is { } contextual
                && await variables.CouldContainAsync(
                    await signatures.ReturnAsync(contextual, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false))
            {
                if (expressions.CachedContextFree(node) is { } cached)
                    return cached;
                var result = context.NewObjectType(
                    ObjectFlags.Anonymous | ObjectFlags.MembersResolved | ObjectFlags.NonInferrableType,
                    symbols.Declaration(node));
                result.Members = new Dictionary<string, Symbol>().AsReadOnly();
                result.Properties = [];
                result.CallSignatures = [context.NewSignature(SignatureFlags.IsNonInferrable, null, [], null, [],
                    await bodies.ReturnAsync(node, mode, cancellation).ConfigureAwait(false), null, 0)];
                result.ConstructSignatures = [];
                result.IndexInfos = [];
                cancellation.ThrowIfCancellationRequested();
                expressions.SetContextFree(node, result);
                return result;
            }
            return context.AnyFunctionType;
        }
        await host.FunctionGrammarAsync(node, cancellation).ConfigureAwait(false);
        if (node is IFullSignatureNode { FullSignature: not null })
            await host.CheckFullSignatureAsync(node, cancellation).ConfigureAwait(false);
        await ContextualAsync(node, mode, cancellation).ConfigureAwait(false);
        return await values.GetAsync(symbols.Declaration(node)!, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask ContextualAsync(SyntaxNode node, CheckMode mode = 0, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var data = links.Nodes.Get(node);
        if ((data.Flags & NodeCheckFlags.ContextChecked) != 0)
            return;
        var contextual = await contexts.GetAsync(node, cancellation).ConfigureAwait(false);
        if ((data.Flags & NodeCheckFlags.ContextChecked) != 0)
            return;
        bool owner = rollback is null;
        rollback ??= [];
        var previousFlags = data.Flags;
        rollback.Add(() => data.Flags = data.Flags & ~NodeCheckFlags.ContextChecked | previousFlags & NodeCheckFlags.ContextChecked);
        data.Flags |= NodeCheckFlags.ContextChecked;
        try
        {
            var signature = (await host.SignaturesAsync(
                await values.GetAsync(symbols.Declaration(node)!, cancellation).ConfigureAwait(false),
                false,
                cancellation).ConfigureAwait(false)).FirstOrDefault();
            if (signature is null)
                return;
            var oldParameters = signature.TypeParameters;
            var oldThis = signature.ThisParameter;
            var oldReturn = signature.ResolvedReturnType;
            rollback.Add(() =>
            {
                signature.TypeParameters = oldParameters;
                signature.ThisParameter = oldThis;
                signature.ResolvedReturnType = oldReturn;
            });
            foreach (var parameter in signature.Parameters.Concat(signature.ThisParameter is { } receiver ? [receiver] : []))
            {
                Capture(parameter);
                if (parameter.ValueDeclaration is INamedNode { Name: BindingPatternNode pattern })
                    foreach (var element in pattern.DescendantsAndSelf().OfType<BindingElementNode>())
                        if (symbols.Declaration(element) is { } symbol)
                            Capture(symbol);
            }
            var inference = expressions.InferenceFor(node);
            if (FunctionSyntax.Sensitive(node, symbols))
            {
                Signature? instantiated = null;
                if (contextual is not null)
                {
                    if ((mode & CheckMode.Inferential) != 0)
                    {
                        await contexts.InferAnnotationsAsync(signature, contextual, inference!, cancellation).ConfigureAwait(false);
                        if (await parameters.EffectiveRestAsync(contextual, cancellation).ConfigureAwait(false) is TypeParameter)
                            instantiated = await instantiation.SignatureAsync(
                                contextual,
                                inference!.NonFixingMapper,
                                false,
                                cancellation).ConfigureAwait(false);
                    }
                    instantiated ??= inference is not null
                        ? await instantiation.SignatureAsync(
                            contextual,
                            inference.Mapper,
                            false,
                            cancellation).ConfigureAwait(false) : contextual;
                }
                await contexts.AssignAsync(signature, instantiated, cancellation).ConfigureAwait(false);
            }
            else if (contextual is not null && node is IFunctionSignature { TypeParameters: null } declaration
                && contextual.Parameters.Count > (declaration.Parameters?.Count ?? 0) && (mode & CheckMode.Inferential) != 0)
                await contexts.InferAnnotationsAsync(signature, contextual, inference!, cancellation).ConfigureAwait(false);
            if (contextual is not null
                && await signatures.AnnotationAsync(node, cancellation).ConfigureAwait(false) is null
                && signature.ResolvedReturnType is null)
            {
                var type = await bodies.ReturnAsync(node, mode & ~CheckMode.SkipContextSensitive, cancellation).ConfigureAwait(false);
                signature.ResolvedReturnType ??= type;
            }
            await host.CheckFunctionDeclarationAsync(node, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
        }
        catch
        {
            if (owner)
                for (int i = rollback.Count - 1; i >= 0; i--)
                    rollback[i]();
            throw;
        }
        finally
        {
            if (owner)
                rollback = null;
        }

        void Capture(Symbol symbol)
        {
            var value = links.Values.Get(symbol);
            var original = value.ResolvedType;
            rollback!.Add(() => value.ResolvedType = original);
        }
    }
}

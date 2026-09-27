using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.Checking;

internal interface IAwaitedTypeHost
{
    ValueTask<Type> PromiseTypeAsync(CancellationToken cancellation);

    ValueTask<Symbol?> AwaitedSymbolAsync(bool reportErrors, CancellationToken cancellation);

    ValueTask<Type> AliasInstantiationAsync(Symbol symbol, Type argument, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);

    ValueTask AwaitedErrorAsync(SyntaxNode node, DiagnosticCode code, Type type, Type? thisType, CancellationToken cancellation);
}

internal sealed class AwaitedTypes(TypeContext context, TypeAlgebra algebra, TypeConstraints constraints,
    TypeProperties properties, SymbolTypes values, SignatureParameters parameters, TypeRelations relations,
    MappedTypes mapped, TypePredicates predicates, TypeViews views, TypeFactQueries facts, IAwaitedTypeHost host)
{
    private readonly Dictionary<Type, Type> promised = [], awaited = [];
    private readonly List<Type> stack = [];
    internal int StackDepth => stack.Count;

    internal async ValueTask<Type?> GetAsync(
        Type type,
        bool withAlias = true,
        SyntaxNode? errorNode = null,
        DiagnosticCode diagnosticCode = DiagnosticCode.TypeOfAwaitOperandMustEitherBeAValidPromiseOrMustNotContainACallableThenMember,
        CancellationToken cancellation = default)
    {
        var result = await NoAliasAsync(type, errorNode, diagnosticCode, cancellation).ConfigureAwait(false);
        return result is not null && withAlias ? await WrapAsync(result, cancellation).ConfigureAwait(false) : result;
    }

    internal async ValueTask<Type?> OfPromiseAsync(
        Type type,
        SyntaxNode? errorNode = null,
        DiagnosticCode diagnosticCode = DiagnosticCode.TypeOfAwaitOperandMustEitherBeAValidPromiseOrMustNotContainACallableThenMember,
        CancellationToken cancellation = default)
    {
        var (result, _) = await PromisedAsync(type, errorNode, cancellation).ConfigureAwait(false);
        return result is null ? null : await GetAsync(result, true, errorNode, diagnosticCode, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<(Type? Type, Type? ThisError)> PromisedAsync(
        Type type,
        SyntaxNode? errorNode = null,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.Flags & TypeFlags.Any) != 0)
            return (null, null);
        if (promised.TryGetValue(type, out var cached))
            return (cached, null);
        var promise = await host.PromiseTypeAsync(cancellation).ConfigureAwait(false);
        if (type is TypeReference reference && (type.ObjectFlags & ObjectFlags.Reference) != 0 && reference.Target == promise)
        {
            var argument = (await host.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false))[0];
            cancellation.ThrowIfCancellationRequested();
            promised[type] = argument;
            return (argument, null);
        }
        if (await AllPrimitiveAsync(
            await constraints.BaseConstraintOrTypeAsync(type, cancellation).ConfigureAwait(false),
            cancellation).ConfigureAwait(false))
            return (null, null);
        var then = await PropertyTypeAsync(type, "then", cancellation).ConfigureAwait(false);
        if (then is not null && (then.Flags & TypeFlags.Any) != 0)
            return (null, null);
        var signatures = then is null ? [] : await host.SignaturesAsync(then, false, cancellation).ConfigureAwait(false);
        if (signatures.Count == 0)
        {
            if (errorNode is not null)
                await host.AwaitedErrorAsync(
                    errorNode,
                    DiagnosticCode.APromiseMustHaveAThenMethod,
                    type,
                    null,
                    cancellation).ConfigureAwait(false);
            return (null, null);
        }
        Type? thisError = null;
        var candidates = new List<Signature>();
        foreach (var signature in signatures)
        {
            var thisType = await parameters.ThisAsync(signature, cancellation).ConfigureAwait(false);
            if (thisType is not null && thisType != context.VoidType
                && !await relations.RelatedAsync(type, thisType, RelationKind.Subtype, cancellation).ConfigureAwait(false))
                thisError = thisType;
            else
                candidates.Add(signature);
        }
        if (candidates.Count == 0)
        {
            if (thisError is null)
                throw new InvalidOperationException("Promise signatures rejected without a this type");
            if (errorNode is not null)
                await host.AwaitedErrorAsync(
                    errorNode,
                    DiagnosticCode.TheThisContextOfType0IsNotAssignableToMethodSThisOfType1,
                    type,
                    thisError,
                    cancellation).ConfigureAwait(false);
            return (null, thisError);
        }
        var callbacks = new List<Type>();
        foreach (var signature in candidates)
            callbacks.Add(await FirstAsync(signature, cancellation).ConfigureAwait(false));
        var callbackType = await facts.FilterAsync(
            await algebra.UnionAsync(callbacks, cancellation: cancellation).ConfigureAwait(false),
            TypeFacts.NEUndefinedOrNull,
            cancellation).ConfigureAwait(false);
        if ((callbackType.Flags & TypeFlags.Any) != 0)
            return (null, null);
        var callbackSignatures = await host.SignaturesAsync(callbackType, false, cancellation).ConfigureAwait(false);
        if (callbackSignatures.Count == 0)
        {
            if (errorNode is not null)
                await host.AwaitedErrorAsync(
                    errorNode,
                    DiagnosticCode.TheFirstParameterOfTheThenMethodOfAPromiseMustBeACallback,
                    type,
                    null,
                    cancellation).ConfigureAwait(false);
            return (null, null);
        }
        var results = new List<Type>();
        foreach (var signature in callbackSignatures)
            results.Add(await FirstAsync(signature, cancellation).ConfigureAwait(false));
        var result = await algebra.UnionAsync(results, UnionReduction.Subtype, cancellation: cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        promised[type] = result;
        return (result, null);
    }

    internal async ValueTask<Type?> NoAliasAsync(
        Type type,
        SyntaxNode? errorNode = null,
        DiagnosticCode diagnosticCode = DiagnosticCode.TypeOfAwaitOperandMustEitherBeAValidPromiseOrMustNotContainACallableThenMember,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.Flags & TypeFlags.Any) != 0 || await IsInstantiationAsync(type, cancellation).ConfigureAwait(false))
            return type;
        if (awaited.TryGetValue(type, out var cached))
            return cached;
        if (type is UnionType)
        {
            if (stack.Contains(type))
                return await CircularAsync().ConfigureAwait(false);
            stack.Add(type);
            Type? result;
            try
            {
                result = await algebra.MapAsync(
                    type,
                    t => NoAliasAsync(t, errorNode, diagnosticCode, cancellation),
                    cancellation: cancellation).ConfigureAwait(false);
            }
            finally
            {
                stack.RemoveAt(stack.Count - 1);
            }
            cancellation.ThrowIfCancellationRequested();
            if (result is not null)
                awaited[type] = result;
            return result;
        }
        if (await NeededAsync(type, cancellation).ConfigureAwait(false))
        {
            cancellation.ThrowIfCancellationRequested();
            return awaited[type] = type;
        }
        var (promisedType, thisError) = await PromisedAsync(type, cancellation: cancellation).ConfigureAwait(false);
        if (promisedType is not null)
        {
            if (type == promisedType || stack.Contains(promisedType))
                return await CircularAsync().ConfigureAwait(false);
            stack.Add(type);
            Type? result;
            try
            {
                result = await NoAliasAsync(promisedType, errorNode, diagnosticCode, cancellation).ConfigureAwait(false);
            }
            finally
            {
                stack.RemoveAt(stack.Count - 1);
            }
            cancellation.ThrowIfCancellationRequested();
            if (result is not null)
                awaited[type] = result;
            return result;
        }
        if (await ThenableAsync(type, cancellation).ConfigureAwait(false))
        {
            if (errorNode is not null)
                await host.AwaitedErrorAsync(errorNode, diagnosticCode, type, thisError, cancellation).ConfigureAwait(false);
            return null;
        }
        cancellation.ThrowIfCancellationRequested();
        return awaited[type] = type;

        async ValueTask<Type?> CircularAsync()
        {
            if (errorNode is not null)
                await host.AwaitedErrorAsync(
                    errorNode,
                    DiagnosticCode.TypeIsReferencedDirectlyOrIndirectlyInTheFulfillmentCallbackOfItsOwnThenMethod,
                    type,
                    null,
                    cancellation).ConfigureAwait(false);
            return null;
        }
    }

    internal async ValueTask<bool> NeededAsync(Type type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.Flags & TypeFlags.Any) != 0 || await IsInstantiationAsync(type, cancellation).ConfigureAwait(false))
            return false;
        if ((await mapped.GenericFlagsAsync(type, cancellation).ConfigureAwait(false) & ObjectFlags.IsGenericObjectType) == 0)
            return false;
        var constraint = await constraints.BaseConstraintAsync(type, cancellation).ConfigureAwait(false);
        if (constraint is null)
            return predicates.Maybe(type, TypeFlags.TypeVariable, cancellation);
        if ((constraint.Flags & TypeFlags.AnyOrUnknown) != 0
            || await views.EmptyObjectAsync(constraint, cancellation).ConfigureAwait(false))
            return true;
        foreach (var part in constraint is UnionType union ? union.Types : (IReadOnlyList<Type>)[constraint])
            if (await ThenableAsync(part, cancellation).ConfigureAwait(false))
                return true;
        return false;
    }

    internal async ValueTask<bool> IsInstantiationAsync(Type type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        return type is ConditionalType && await host.AwaitedSymbolAsync(false, cancellation).ConfigureAwait(false) is { } symbol
            && type.Alias is { TypeArguments.Count: 1 } alias && alias.Symbol == symbol;
    }

    internal async ValueTask<Type> WrapAsync(Type type, CancellationToken cancellation = default)
        => await NeededAsync(type, cancellation).ConfigureAwait(false)
            && await host.AwaitedSymbolAsync(true, cancellation).ConfigureAwait(false) is { } symbol
            ? await host.AliasInstantiationAsync(
                symbol,
                await UnwrapAsync(type, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false) : type;

    internal async ValueTask<Type> UnwrapAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (type is UnionType)
            return await algebra.MapAsync(
                type,
                async t => await UnwrapAsync(t, cancellation).ConfigureAwait(false),
                cancellation: cancellation).ConfigureAwait(false) ?? context.NeverType;
        return await IsInstantiationAsync(type, cancellation).ConfigureAwait(false) ? type.Alias!.TypeArguments[0] : type;
    }

    internal async ValueTask<bool> ThenableAsync(Type type, CancellationToken cancellation = default)
    {
        if (await AllPrimitiveAsync(
            await constraints.BaseConstraintOrTypeAsync(type, cancellation).ConfigureAwait(false),
            cancellation).ConfigureAwait(false))
            return false;
        var then = await PropertyTypeAsync(type, "then", cancellation).ConfigureAwait(false);
        return then is not null
            && (await host.SignaturesAsync(
                await facts.FilterAsync(then, TypeFacts.NEUndefinedOrNull, cancellation).ConfigureAwait(false),
                false,
                cancellation).ConfigureAwait(false)).Count != 0;
    }

    private async ValueTask<bool> AllPrimitiveAsync(Type type, CancellationToken cancellation)
    {
        foreach (var part in type is UnionType union ? union.Types : (IReadOnlyList<Type>)[type])
            if (!await predicates.AssignableAsync(
                part,
                TypeFlags.Primitive | TypeFlags.Never,
                cancellation: cancellation).ConfigureAwait(false))
                return false;
        return true;
    }

    private async ValueTask<Type?> PropertyTypeAsync(Type type, TextSlice name, CancellationToken cancellation)
            =>
                await properties.PropertyAsync(type, name, cancellation: cancellation).ConfigureAwait(false) is { } property
                    ? await values.GetAsync(property, cancellation).ConfigureAwait(false)
                    : null;

    private ValueTask<Type> FirstAsync(Signature signature, CancellationToken cancellation)
            =>
                signature.Parameters.Count == 0
                    ? ValueTask.FromResult<Type>(context.NeverType)
                    : parameters.AtAsync(signature, 0, cancellation);
}

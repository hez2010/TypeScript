using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.Checking;

internal readonly record struct IterationTypes(Type? Yield, Type? Return, Type? Next)
{
    internal bool HasTypes => Yield is not null || Return is not null || Next is not null;

    internal Type? Get(IterationTypeKind kind) => kind switch
    {
        IterationTypeKind.Yield => Yield,
        IterationTypeKind.Return => Return,
        IterationTypeKind.Next => Next,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

internal readonly record struct IterationDiagnostic(
    SyntaxNode Node,
    DiagnosticCode Code,
    Utf8String? Member = null,
    Type? Source = null,
    Type? Target = null);

internal interface IIteratorProtocolHost
{
    bool StrictBuiltinIteratorReturn { get; }

    ValueTask<Type> IterationGlobalAsync(Utf8String name, int arity, bool report, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Type>> BuiltinIteratorsAsync(bool async, CancellationToken cancellation);

    ValueTask<Utf8String> KnownSymbolNameAsync(Utf8String name, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation);

    ValueTask<Symbol?> AwaitedSymbolAsync(bool reportErrors, CancellationToken cancellation);

    ValueTask IterationDiagnosticAsync(IterationDiagnostic diagnostic, CancellationToken cancellation);

    void DeferIteratorDiagnostic(SyntaxNode node, Type type, bool async, IReadOnlyList<IterationDiagnostic> related);
}

internal sealed class IteratorProtocols(TypeContext context, TypeAlgebra algebra, TypeViews views, TypeFactQueries facts,
    TypeProperties properties, SymbolTypes values, SignatureParameters parameters, Signatures signatures,
    TypeRelations relations, AwaitedTypes awaited, IIteratorProtocolHost host)
{
    private readonly Dictionary<(Type Type, IterationUse Use), IterationTypes> cache = [];
    internal int CacheCount => cache.Count;
    private IterationTypes Any => new(context.AnyType, context.AnyType, context.AnyType);

    internal async ValueTask<IterationTypes> GeneratorAsync(Type type, bool async, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.Flags & TypeFlags.Any) != 0)
            return Any;
        var result = await IterableAsync(
            type,
            async ? IterationUse.AsyncGeneratorReturnType : IterationUse.GeneratorReturnType,
            cancellation: cancellation).ConfigureAwait(false);
        return result.HasTypes ? result : await IteratorAsync(type, async, cancellation: cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<IterationTypes> IterableAsync(
        Type type,
        IterationUse use,
        SyntaxNode? errorNode = null,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        type = await views.ReducedAsync(type, cancellation).ConfigureAwait(false);
        if ((type.Flags & TypeFlags.Any) != 0)
            return Any;
        var key = (type, use & IterationUse.CacheFlags);
        bool noCache = false;
        if (cache.TryGetValue(key, out var cached))
        {
            if (errorNode is null || cached.HasTypes)
                return cached;
            noCache = true;
        }
        var result = await IterableWorkerAsync(type, use, errorNode, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (!noCache)
            cache[key] = result;
        return result;
    }

    private async ValueTask<IterationTypes> IterableWorkerAsync(
        Type type,
        IterationUse use,
        SyntaxNode? node,
        CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        if (type is UnionType union)
        {
            var all = new List<IterationTypes>();
            foreach (var part in union.Types)
            {
                var result = await IterableWorkerAsync(part, use, null, cancellation).ConfigureAwait(false);
                if (!result.HasTypes)
                {
                    if (node is not null)
                        host.DeferIteratorDiagnostic(node, type, (use & IterationUse.AllowsAsyncIterablesFlag) != 0, []);
                    return default;
                }
                all.Add(result);
            }
            return await CombineAsync(all, cancellation).ConfigureAwait(false);
        }
        var diagnostics = new List<IterationDiagnostic>();
        if ((use & IterationUse.AllowsAsyncIterablesFlag) != 0)
        {
            var result = await FastAsync(type, true, true, cancellation).ConfigureAwait(false);
            if (result.HasTypes)
                return (use & IterationUse.ForOfFlag) != 0
                ? await AsyncFromSyncAsync(result, node, cancellation).ConfigureAwait(false) : result;
            result = await IterableSlowAsync(type, true, node, diagnostics, cancellation).ConfigureAwait(false);
            if (result.HasTypes)
            {
                foreach (var diagnostic in diagnostics)
                    await host.IterationDiagnosticAsync(diagnostic, cancellation).ConfigureAwait(false);
                return result;
            }
        }
        if ((use & IterationUse.AllowsSyncIterablesFlag) != 0)
        {
            var result = await FastAsync(type, false, true, cancellation).ConfigureAwait(false);
            if (result.HasTypes)
                return (use & IterationUse.AllowsAsyncIterablesFlag) != 0
                ? await AsyncFromSyncAsync(result, node, cancellation).ConfigureAwait(false) : result;
            result = await IterableSlowAsync(type, false, node, diagnostics, cancellation).ConfigureAwait(false);
            if (result.HasTypes)
            {
                foreach (var diagnostic in diagnostics)
                    await host.IterationDiagnosticAsync(diagnostic, cancellation).ConfigureAwait(false);
                return (use & IterationUse.AllowsAsyncIterablesFlag) != 0
                    ? await AsyncFromSyncAsync(result, node, cancellation).ConfigureAwait(false) : result;
            }
        }
        if (node is not null)
            host.DeferIteratorDiagnostic(node, type, (use & IterationUse.AllowsAsyncIterablesFlag) != 0, diagnostics.ToArray());
        return default;
    }

    private async ValueTask<IterationTypes> FastAsync(Type type, bool async, bool iterable, CancellationToken cancellation)
    {
        Utf8String prefix = async ? Utf8Literals.AsyncSuffix : Utf8String.Empty;
        foreach (Utf8String name in new Utf8String[] { iterable ? Utf8Literals.Iterable : Utf8Literals.Iterator, Utf8Literals.IteratorObject, Utf8Literals.IterableIterator, Utf8Literals.Generator })
            if (Reference(type, await host.IterationGlobalAsync(Utf8String.Concat(prefix, name), 3, false, cancellation).ConfigureAwait(false)))
            {
                var args = await host.TypeArgumentsAsync((TypeReference)type, cancellation).ConfigureAwait(false);
                return await ResolveAsync(args[0], args[1], args[2], async, cancellation).ConfigureAwait(false);
            }
        if (type is TypeReference reference && (type.ObjectFlags & ObjectFlags.Reference) != 0
            && (await host.BuiltinIteratorsAsync(async, cancellation).ConfigureAwait(false)).Contains(reference.Target!))
            return await ResolveAsync((await host.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false))[0],
                host.StrictBuiltinIteratorReturn ? context.UndefinedType : context.AnyType,
                context.UnknownType,
                async,
                cancellation).ConfigureAwait(false);
        // The global builtin types are resolved even for a non-reference input in the reference implementation.
        if (type is not TypeReference || (type.ObjectFlags & ObjectFlags.Reference) == 0)
            await host.BuiltinIteratorsAsync(async, cancellation).ConfigureAwait(false);
        return default;
    }

    private async ValueTask<IterationTypes> ResolveAsync(Type yield, Type result, Type next, bool async, CancellationToken cancellation) =>
        new(await ResolveTypeAsync(yield, async, null, cancellation).ConfigureAwait(false) ?? yield,
            await ResolveTypeAsync(result, async, null, cancellation).ConfigureAwait(false) ?? result, next);

    private ValueTask<Type?> ResolveTypeAsync(Type type, bool async, SyntaxNode? node, CancellationToken cancellation) =>
        async
            ? awaited.GetAsync(
                type,
                true,
                node,
                DiagnosticCode.TypeOfAwaitOperandMustEitherBeAValidPromiseOrMustNotContainACallableThenMember,
                cancellation)
            : ValueTask.FromResult<Type?>(type);

    internal async ValueTask<IterationTypes> AsyncFromSyncAsync(
        IterationTypes types,
        SyntaxNode? node,
        CancellationToken cancellation = default)
    {
        if (!types.HasTypes || types == Any)
            return types;
        if (node is not null)
            await host.AwaitedSymbolAsync(true, cancellation).ConfigureAwait(false);
        return new(
            types.Yield is null
                ? context.AnyType
                : await awaited.GetAsync(types.Yield, true, node, cancellation: cancellation).ConfigureAwait(false) ?? context.AnyType,
            types.Return is null
                ? context.AnyType
                : await awaited.GetAsync(types.Return, true, node, cancellation: cancellation).ConfigureAwait(false) ?? context.AnyType,
            types.Next);
    }

    private async ValueTask<IterationTypes> IterableSlowAsync(
        Type type,
        bool async,
        SyntaxNode? node,
        List<IterationDiagnostic> diagnostics,
        CancellationToken cancellation)
    {
        var name = await host.KnownSymbolNameAsync(async ? Utf8Literals.AsyncIterator : Utf8Literals.IteratorProperty, cancellation).ConfigureAwait(false);
        var method = await properties.PropertyAsync(type, name, cancellation: cancellation).ConfigureAwait(false);
        if (method is null || (method.Flags & SymbolFlags.Optional) != 0)
            return default;
        var methodType = await values.GetAsync(method, cancellation).ConfigureAwait(false);
        if ((methodType.Flags & TypeFlags.Any) != 0)
            return Any;
        var all = await host.SignaturesAsync(methodType, false, cancellation).ConfigureAwait(false);
        var valid = new List<Signature>();
        foreach (var signature in all)
            if (await parameters.MinimumAsync(signature, cancellation: cancellation).ConfigureAwait(false) == 0)
                valid.Add(signature);
        if (valid.Count != 0)
        {
            var returns = new List<Type>();
            foreach (var signature in valid)
                returns.Add(await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false));
            return await IteratorAsync(
                await algebra.IntersectionAsync(returns, cancellation: cancellation).ConfigureAwait(false),
                async,
                node,
                diagnostics,
                cancellation).ConfigureAwait(false);
        }
        if (node is not null && all.Count != 0)
        {
            var target = await host.IterationGlobalAsync(async ? Utf8Literals.AsyncIterable : Utf8Literals.Iterable, 3, true, cancellation).ConfigureAwait(false);
            if (!await relations.RelatedAsync(type, target, RelationKind.Assignable, cancellation).ConfigureAwait(false))
                diagnostics.Add(new(node, DiagnosticCode.Type0IsNotAssignableToType1, Source: type, Target: target));
        }
        return default;
    }

    internal async ValueTask<IterationTypes> IteratorAsync(Type type, bool async, SyntaxNode? node = null,
        List<IterationDiagnostic>? diagnostics = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.Flags & TypeFlags.Any) != 0)
            return Any;
        var fast = await FastAsync(type, async, false, cancellation).ConfigureAwait(false);
        if (fast.HasTypes)
            return fast;
        var parts = new List<IterationTypes>();
        foreach (Utf8String name in new Utf8String[] { Utf8Literals.Next, Utf8Literals.Return, Utf8Literals.ThrowKeyword })
            parts.Add(await MethodAsync(type, async, name, node, diagnostics, cancellation).ConfigureAwait(false));
        return await CombineAsync(parts, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<IterationTypes> MethodAsync(Type type, bool async, Utf8String name, SyntaxNode? node,
        List<IterationDiagnostic>? diagnostics, CancellationToken cancellation)
    {
        var method = await properties.PropertyAsync(type, name, cancellation: cancellation).ConfigureAwait(false);
        if (method is null && name != Utf8Literals.Next)
            return default;
        Type? methodType = null;
        if (method is not null && !(name == Utf8Literals.Next && (method.Flags & SymbolFlags.Optional) != 0))
        {
            methodType = await values.GetAsync(method, cancellation).ConfigureAwait(false);
            if (name != Utf8Literals.Next)
                methodType = await facts.FilterAsync(methodType, TypeFacts.NEUndefinedOrNull, cancellation).ConfigureAwait(false);
        }
        if (methodType is not null && (methodType.Flags & TypeFlags.Any) != 0)
            return Any;
        var methodSignatures = methodType is null ? [] : await host.SignaturesAsync(methodType, false, cancellation).ConfigureAwait(false);
        if (methodSignatures.Count == 0)
        {
            await ReportAsync(
                name == Utf8Literals.Next
                    ? async ? DiagnosticCode.AnAsyncIteratorMustHaveANextMethod : DiagnosticCode.AnIteratorMustHaveANextMethod
                    : async
                        ? DiagnosticCode.The0PropertyOfAnAsyncIteratorMustBeAMethod
                        : DiagnosticCode.The0PropertyOfAnIteratorMustBeAMethod).ConfigureAwait(false);
            return default;
        }
        if (methodSignatures.Count == 1 && methodType!.Symbol is { } symbol)
        {
            var generator = await host.IterationGlobalAsync(
                async ? Utf8Literals.AsyncGeneratorType : Utf8Literals.Generator,
                3,
                false,
                cancellation).ConfigureAwait(false);
            var iterator = await host.IterationGlobalAsync(
                async ? Utf8Literals.AsyncIteratorType : Utf8Literals.Iterator,
                3,
                false,
                cancellation).ConfigureAwait(false);
            bool isGenerator = generator.Symbol?.Members.GetValueOrDefault(name) == symbol;
            bool isIterator = !isGenerator && iterator.Symbol?.Members.GetValueOrDefault(name) == symbol;
            if (isGenerator || isIterator)
            {
                var typeParameters = ((InterfaceType)(isGenerator ? generator : iterator)).AllTypeParameters;
                var mapper = ((ObjectType)methodType).Mapper;
                async ValueTask<Type> Map(int index) =>
                    mapper is null
                        ? typeParameters[index]
                        : await mapper.MapAsync(typeParameters[index], cancellation).ConfigureAwait(false);
                Type? next = name == Utf8Literals.Next ? await Map(2).ConfigureAwait(false) : null;
                return new(await Map(0).ConfigureAwait(false), await Map(1).ConfigureAwait(false), next);
            }
        }
        var parameterTypes = new List<Type>();
        var methodReturns = new List<Type>();
        foreach (var signature in methodSignatures)
        {
            if (name != Utf8Literals.ThrowKeyword && signature.Parameters.Count != 0)
                parameterTypes.Add(await parameters.AtAsync(signature, 0, cancellation).ConfigureAwait(false));
            methodReturns.Add(await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false));
        }
        var returns = new List<Type>();
        Type? nextType = null;
        if (name != Utf8Literals.ThrowKeyword)
        {
            var parameter = parameterTypes.Count != 0
                ? await algebra.UnionAsync(parameterTypes, cancellation: cancellation).ConfigureAwait(false)
                : context.UnknownType;
            if (name == Utf8Literals.Next)
                nextType = parameter;
            else
                returns.Add(await ResolveTypeAsync(parameter, async, node, cancellation).ConfigureAwait(false) ?? context.AnyType);
        }
        var returnType = methodReturns.Count != 0
            ? await algebra.IntersectionAsync(methodReturns, cancellation: cancellation).ConfigureAwait(false)
            : context.NeverType;
        var result = await ResultAsync(
            await ResolveTypeAsync(returnType, async, node, cancellation).ConfigureAwait(false) ?? context.AnyType,
            cancellation).ConfigureAwait(false);
        Type? yield = result.Yield;
        if (!result.HasTypes)
        {
            await ReportAsync(
                async
                    ? DiagnosticCode.TheTypeReturnedByThe0MethodOfAnAsyncIteratorMustBeAPromiseForATypeWithAValueProperty
                    : DiagnosticCode.TheTypeReturnedByThe0MethodOfAnIteratorMustHaveAValueProperty).ConfigureAwait(false);
            yield = context.AnyType;
            returns.Add(context.AnyType);
        }
        else if (result.Return is not null)
            returns.Add(result.Return);
        return new(yield, await algebra.UnionAsync(returns, cancellation: cancellation).ConfigureAwait(false), nextType);

        async ValueTask ReportAsync(DiagnosticCode code)
        {
            if (node is null)
                return;
            var diagnostic = new IterationDiagnostic(node, code, name);
            if (diagnostics is null)
                await host.IterationDiagnosticAsync(diagnostic, cancellation).ConfigureAwait(false);
            else
                diagnostics.Add(diagnostic);
        }
    }

    internal async ValueTask<IterationTypes> ResultAsync(Type type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.Flags & TypeFlags.Any) != 0)
            return Any;
        if (Reference(type, await host.IterationGlobalAsync(Utf8Literals.IteratorYieldResult, 1, false, cancellation).ConfigureAwait(false)))
            return new((await host.TypeArgumentsAsync((TypeReference)type, cancellation).ConfigureAwait(false))[0], null, null);
        if (Reference(type, await host.IterationGlobalAsync(Utf8Literals.IteratorReturnResult, 1, false, cancellation).ConfigureAwait(false)))
            return new(null, (await host.TypeArgumentsAsync((TypeReference)type, cancellation).ConfigureAwait(false))[0], null);
        var yielded = await algebra.FilterAsync(type, part => IsResultAsync(part, false, cancellation), cancellation).ConfigureAwait(false);
        var yield = yielded != context.NeverType ? await PropertyAsync(yielded, Utf8Literals.Value, cancellation).ConfigureAwait(false) : null;
        var returned = await algebra.FilterAsync(type, part => IsResultAsync(part, true, cancellation), cancellation).ConfigureAwait(false);
        var result = returned != context.NeverType ? await PropertyAsync(returned, Utf8Literals.Value, cancellation).ConfigureAwait(false) : null;
        return yield is null && result is null ? default : new(yield, result ?? context.VoidType, null);
    }

    private async ValueTask<bool> IsResultAsync(Type type, bool returned, CancellationToken cancellation) =>
        await relations.RelatedAsync(returned ? context.TrueType : context.FalseType,
            await PropertyAsync(type, Utf8Literals.Done, cancellation).ConfigureAwait(false) ?? context.FalseType,
            RelationKind.Assignable,
            cancellation).ConfigureAwait(false);

    private async ValueTask<Type?> PropertyAsync(Type type, Utf8String name, CancellationToken cancellation) =>
            await properties.PropertyAsync(type, name, cancellation: cancellation).ConfigureAwait(false) is { } property
                ? await values.GetAsync(property, cancellation).ConfigureAwait(false) : null;

    private static bool Reference(Type type, Type target) =>
        type is TypeReference reference && (type.ObjectFlags & ObjectFlags.Reference) != 0 && reference.Target == target;

    private async ValueTask<IterationTypes> CombineAsync(IReadOnlyList<IterationTypes> parts, CancellationToken cancellation)
    {
        async ValueTask<Type?> Union(IterationTypeKind kind)
        {
            var types = parts.Select(p => p.Get(kind)).Where(t => t is not null).Cast<Type>().ToArray();
            return types.Length == 0 ? null : await algebra.UnionAsync(types, cancellation: cancellation).ConfigureAwait(false);
        }
        return new(
            await Union(IterationTypeKind.Yield).ConfigureAwait(false),
            await Union(IterationTypeKind.Return).ConfigureAwait(false),
            await Union(IterationTypeKind.Next).ConfigureAwait(false));
    }
}

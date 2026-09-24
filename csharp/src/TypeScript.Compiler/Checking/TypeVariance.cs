using System.Runtime.CompilerServices;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface ITypeVarianceHost
{
    Type ArrayTarget(bool isReadonly);

    ValueTask<bool> AssignableAsync(Type source, Type target, CancellationToken cancellation);

    ValueTask<bool> IdenticalAsync(Type source, Type target, CancellationToken cancellation);

    ValueTask<bool> EmptyArrayAsync(Type type, CancellationToken cancellation);
}

internal sealed class TypeVariance(
    TypeContext context,
    CheckerLinks links,
    DeclaredTypes declared,
    TypeReferences references,
    TypeInstantiation instantiation,
    TypeConstraints constraints,
    TypeResolutionStack resolutions,
    TypeOrder order,
    RelationState state,
    ITypeVarianceHost host)
{
    private readonly Dictionary<Symbol, IReadOnlyList<VarianceFlags>> cache = [];
    private readonly HashSet<Type> markers = [];
    private List<(Symbol Symbol, IReadOnlyList<Type> Parameters)> stack = [];
    private readonly Dictionary<Symbol, IReadOnlyList<VarianceFlags>?> changes = [];
    private readonly List<Type> addedMarkers = [];
    private int active;
    internal bool Measuring => stack.Count != 0;

    internal bool IsMarker(Type type) => markers.Contains(type);

    internal IReadOnlyDictionary<Symbol, IReadOnlyList<VarianceFlags>> Cache => cache;
    private TypeMapper? unreliable, unmeasurable;

    internal async ValueTask ReportAsync(Type type, bool measurable, CancellationToken cancellation = default)
        => await ReportTypeAsync(type, measurable, cancellation).ConfigureAwait(false);

    internal async ValueTask<Type> ReportTypeAsync(Type type, bool measurable, CancellationToken cancellation = default)
    {
        var mapper = measurable ? unmeasurable ??= TypeMapper.Function(t => Report(t, RelationComparisonResult.ReportsUnmeasurable))
            : unreliable ??= TypeMapper.Function(t => Report(t, RelationComparisonResult.ReportsUnreliable));
        return await instantiation.InstantiateAsync(type, mapper, cancellation: cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Variance marker instantiation returned no type");
    }

    private Type Report(Type type, RelationComparisonResult flag)
    {
        if (type == context.MarkerSuper || type == context.MarkerSub || type == context.MarkerOther)
            state.Reliability |= flag;
        return type;
    }

    internal ValueTask<IReadOnlyList<VarianceFlags>> OfTypeAsync(InterfaceType type, CancellationToken cancellation = default)
        => type == host.ArrayTarget(false)
            || type == host.ArrayTarget(true)
            || type is TupleType ? ValueTask.FromResult<IReadOnlyList<VarianceFlags>>([VarianceFlags.Covariant])
            : GetAsync(
                type.Symbol!,
                type.AllTypeParameters.Take(type.AllTypeParameters.Count - (type.ThisType is null ? 0 : 1)).ToArray(),
                cancellation);

    internal async ValueTask<IReadOnlyList<VarianceFlags>> GetAsync(
        Symbol symbol,
        IReadOnlyList<Type> parameters,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(
            RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        bool outer = active++ == 0;
        try
        {
            return await WorkerAsync(symbol, parameters, cancellation).ConfigureAwait(false);
        }
        catch
        {
            if (outer)
            {
                foreach (var (key, value) in changes)
                    if (value is null)
                        cache.Remove(key);
                    else
                        cache[key] = value;
                foreach (var type in addedMarkers)
                    markers.Remove(type);
            }
            throw;
        }
        finally
        {
            active--;
            if (outer)
            {
                changes.Clear();
                addedMarkers.Clear();
            }
        }
    }

    private async ValueTask<IReadOnlyList<VarianceFlags>> WorkerAsync(
        Symbol symbol,
        IReadOnlyList<Type> parameters,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (cache.TryGetValue(symbol, out var cached))
            return cached;
        int index = stack.FindIndex(e => e.Symbol == symbol);
        if (index >= 0)
        {
            int minimum = index;
            for (int i = index + 1; i < stack.Count; i++)
                if (order.CompareSymbols(stack[i].Symbol, stack[minimum].Symbol) < 0)
                    minimum = i;
            if (minimum > index)
            {
                var saved = stack;
                stack = [];
                try
                {
                    await GetAsync(saved[minimum].Symbol, saved[minimum].Parameters, cancellation).ConfigureAwait(false);
                }
                finally
                {
                    stack = saved;
                }
            }
            if (!cache.TryGetValue(symbol, out cached) || cached.Count == 0)
                Set(symbol, []);
            return cache[symbol];
        }
        int oldResolutionStart = resolutions.ResolutionStart;
        if (stack.Count == 0)
            resolutions.ResolutionStart = resolutions.Count;
        var ownStack = stack;
        ownStack.Add((symbol, parameters));
        try
        {
            var result = new VarianceFlags[parameters.Count];
            for (int i = 0; i < parameters.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var parameter = parameters[i];
                context.RequireOwned(parameter);
                bool input = parameter.Symbol?.Declarations.Any(d => SemanticSyntax.HasModifier(d, SyntaxKind.InKeyword)) == true;
                bool output = parameter.Symbol?.Declarations.Any(d => SemanticSyntax.HasModifier(d, SyntaxKind.OutKeyword)) == true;
                VarianceFlags variance;
                if (output)
                    variance = input ? VarianceFlags.Invariant : VarianceFlags.Covariant;
                else if (input)
                    variance = VarianceFlags.Contravariant;
                else
                {
                    var oldReliability = state.Reliability;
                    state.Reliability = 0;
                    try
                    {
                        var super = await MarkerAsync(symbol, parameter, context.MarkerSuper, cancellation).ConfigureAwait(false);
                        var sub = await MarkerAsync(symbol, parameter, context.MarkerSub, cancellation).ConfigureAwait(false);
                        variance = (await host.AssignableAsync(sub, super, cancellation).ConfigureAwait(false)
                            ? VarianceFlags.Covariant
                            : 0)
                            | (await host.AssignableAsync(super, sub, cancellation).ConfigureAwait(false)
                                ? VarianceFlags.Contravariant
                                : 0);
                        if (variance == VarianceFlags.Bivariant
                            && await host.AssignableAsync(
                                await MarkerAsync(symbol, parameter, context.MarkerOther, cancellation).ConfigureAwait(false),
                                super,
                                cancellation).ConfigureAwait(false))
                            variance = VarianceFlags.Independent;
                        if ((state.Reliability & RelationComparisonResult.ReportsUnmeasurable) != 0)
                            variance |= VarianceFlags.Unmeasurable;
                        if ((state.Reliability & RelationComparisonResult.ReportsUnreliable) != 0)
                            variance |= VarianceFlags.Unreliable;
                    }
                    finally
                    {
                        state.Reliability = oldReliability;
                    }
                }
                if (cache.TryGetValue(symbol, out cached) && cached.Count != 0)
                    break;
                result[i] = variance;
            }
            cancellation.ThrowIfCancellationRequested();
            if (!cache.TryGetValue(symbol, out cached) || cached.Count == 0)
                Set(symbol, Array.AsReadOnly(result));
            return cache[symbol];
        }
        finally
        {
            ownStack.RemoveAt(ownStack.Count - 1);
            if (stack.Count == 0)
                resolutions.ResolutionStart = oldResolutionStart;
        }
    }

    internal async ValueTask<Type> MarkerAsync(Symbol symbol, Type source, Type target, CancellationToken cancellation)
    {
        var mapper = TypeMapper.Create([source], [target]);
        var type = await declared.GetAsync(symbol, cancellation).ConfigureAwait(false);
        if (type == context.ErrorType || (type.Flags & TypeFlags.Any) != 0 && type.Alias is not null)
            return type;
        Type result;
        if ((symbol.Flags & SymbolFlags.TypeAlias) != 0)
        {
            var arguments = await instantiation.TypesAsync(
                links.TypeAliases.Get(symbol).TypeParameters!,
                mapper,
                cancellation).ConfigureAwait(false);
            result = await references.AliasInstantiationAsync(symbol, arguments, cancellation: cancellation).ConfigureAwait(false);
        }
        else
        {
            var definition = (InterfaceType)type;
            var parameters = definition.AllTypeParameters.Take(
                definition.AllTypeParameters.Count - (definition.ThisType is null ? 0 : 1)).ToArray();
            result = context.CreateTypeReference(
                definition,
                (await instantiation.TypesAsync(parameters, mapper, cancellation).ConfigureAwait(false)).ToArray());
        }
        if (markers.Add(result) && active != 0)
            addedMarkers.Add(result);
        return result;
    }

    private void Set(Symbol symbol, IReadOnlyList<VarianceFlags> value)
    {
        if (!changes.ContainsKey(symbol))
            changes.Add(symbol, cache.GetValueOrDefault(symbol));
        cache[symbol] = value;
    }

    internal async ValueTask<Ternary?> RelateAsync(
        RelationOperation operation,
        Type source,
        Type target,
        IntersectionState intersection = 0,
        CancellationToken cancellation = default)
    {
        if (IsMarker(source) || IsMarker(target))
            return null;
        IReadOnlyList<VarianceFlags> variances;
        IReadOnlyList<Type> sourceArguments, targetArguments;
        if ((source.Flags & (TypeFlags.Object | TypeFlags.Conditional)) != 0
            && source.Alias is { TypeArguments.Count: > 0 } alias
            && target.Alias?.Symbol == alias.Symbol)
        {
            var parameters = links.TypeAliases.Get(alias.Symbol).TypeParameters!;
            variances = await GetAsync(alias.Symbol, parameters, cancellation).ConfigureAwait(false);
            if (variances.Count == 0)
                return Ternary.Unknown;
            bool js = alias.Symbol.ValueDeclaration is { } declaration && (declaration.Flags & NodeFlags.JavaScriptFile) != 0;
            sourceArguments = await constraints.FillMissingArgumentsAsync(
                alias.TypeArguments,
                parameters,
                js,
                host.IdenticalAsync,
                cancellation).ConfigureAwait(false);
            targetArguments = await constraints.FillMissingArgumentsAsync(
                target.Alias!.TypeArguments,
                parameters,
                js,
                host.IdenticalAsync,
                cancellation).ConfigureAwait(false);
        }
        else if (source is TypeReference sr
            && target is TypeReference tr
            && (source.ObjectFlags & ObjectFlags.Reference) != 0
            && (target.ObjectFlags & ObjectFlags.Reference) != 0
            && sr.Target == tr.Target && sr.Target is InterfaceType definition && definition is not TupleType)
        {
            if (await host.EmptyArrayAsync(source, cancellation).ConfigureAwait(false))
                return Ternary.True;
            variances = await OfTypeAsync(definition, cancellation).ConfigureAwait(false);
            if (variances.Count == 0)
                return Ternary.Unknown;
            sourceArguments = await references.TypeArgumentsAsync(sr, cancellation).ConfigureAwait(false);
            targetArguments = await references.TypeArgumentsAsync(tr, cancellation).ConfigureAwait(false);
        }
        else
            return null;
        var result = await ArgumentsAsync(
            operation,
            sourceArguments,
            targetArguments,
            variances,
            intersection,
            cancellation).ConfigureAwait(false);
        if (result != Ternary.False)
            return result;
        if (variances.Any(v => (v & VarianceFlags.AllowsStructuralFallback) != 0))
            return null;
        for (int i = 0; i < variances.Count; i++)
            if ((variances[i] & VarianceFlags.VarianceMask) == VarianceFlags.Covariant && (targetArguments[i].Flags & TypeFlags.Void) != 0)
                return null;
        return Ternary.False;
    }

    internal async ValueTask<Ternary> ArgumentsAsync(RelationOperation operation, IReadOnlyList<Type> source, IReadOnlyList<Type> target,
        IReadOnlyList<VarianceFlags> variances, IntersectionState intersection = 0, CancellationToken cancellation = default)
    {
        if (source.Count != target.Count && operation.Kind == RelationKind.Identity)
            return Ternary.False;
        Ternary result = Ternary.True;
        for (int i = 0; i < Math.Min(source.Count, target.Count); i++)
        {
            var flags = i < variances.Count ? variances[i] : VarianceFlags.Covariant;
            var variance = flags & VarianceFlags.VarianceMask;
            if (variance == VarianceFlags.Independent)
                continue;
            var s = source[i];
            var t = target[i];
            Ternary related;
            if ((flags & VarianceFlags.Unmeasurable) != 0)
                related = operation.Kind == RelationKind.Identity ? await operation.CompareAsync(
                    s,
                    t,
                    cancellation: cancellation).ConfigureAwait(false)
                    : await host.IdenticalAsync(s, t, cancellation).ConfigureAwait(false) ? Ternary.True : Ternary.False;
            else
            {
                if (Measuring && (flags & VarianceFlags.Unreliable) != 0)
                    await ReportAsync(s, false, cancellation).ConfigureAwait(false);
                if (variance == VarianceFlags.Covariant)
                    related = await operation.CompareAsync(
                        s,
                        t,
                        intersection: intersection,
                        cancellation: cancellation).ConfigureAwait(false);
                else if (variance == VarianceFlags.Contravariant)
                    related = await operation.CompareAsync(
                        t,
                        s,
                        intersection: intersection,
                        cancellation: cancellation).ConfigureAwait(false);
                else if (variance == VarianceFlags.Bivariant)
                {
                    related = await operation.CompareAsync(t, s, cancellation: cancellation).ConfigureAwait(false);
                    if (related == Ternary.False)
                        related = await operation.CompareAsync(
                            s,
                            t,
                            intersection: intersection,
                            cancellation: cancellation).ConfigureAwait(false);
                }
                else
                {
                    related = await operation.CompareAsync(
                        s,
                        t,
                        intersection: intersection,
                        cancellation: cancellation).ConfigureAwait(false);
                    if (related != Ternary.False)
                        related &= await operation.CompareAsync(
                            t,
                            s,
                            intersection: intersection,
                            cancellation: cancellation).ConfigureAwait(false);
                }
            }
            if (related == Ternary.False)
                return related;
            result &= related;
        }
        return result;
    }
}

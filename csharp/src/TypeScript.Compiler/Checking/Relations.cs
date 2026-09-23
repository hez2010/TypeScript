using System.Runtime.CompilerServices;

namespace TypeScript.Compiler.Checking;

internal enum RelationKind
{
    Identity,
    Subtype,
    StrictSubtype,
    Assignable,
    Comparable
}

internal sealed class Relation(RelationKind kind)
{
    internal sealed record Entry(RelationComparisonResult Result);

    private readonly Dictionary<RelationKey, Entry> results = [];
    internal RelationKind Kind { get; } = kind;
    internal int Count => results.Count;
    internal IEnumerable<KeyValuePair<RelationKey, RelationComparisonResult>> Results =>
        results.Select(p => new KeyValuePair<RelationKey, RelationComparisonResult>(p.Key, p.Value.Result));

    internal RelationComparisonResult Get(RelationKey key) => results.GetValueOrDefault(key)?.Result ?? 0;

    internal Entry? Snapshot(RelationKey key) => results.GetValueOrDefault(key);

    internal Entry Set(RelationKey key, RelationComparisonResult result) => results[key] = new(result);

    internal void Restore(RelationKey key, Entry? before, Entry written)
    {
        if (!ReferenceEquals(results.GetValueOrDefault(key), written))
            return;
        if (before is null)
            results.Remove(key);
        else
            results[key] = before;
    }
}

internal sealed class RelationState
{
    internal RelationComparisonResult Reliability { get; set; }
}

internal sealed class RelationSession(
    TypeContext context,
    Relation relation,
    RelationKeys keys,
    TypeRecursion recursion,
    RelationState state)
{
    private readonly List<RelationKey> maybe = [];
    private readonly HashSet<RelationKey> maybeSet = [];
    private readonly List<Type> sourceStack = [], targetStack = [];
    private ExpandingFlags expanding;
    private readonly Dictionary<RelationKey, (Relation.Entry? Before, Relation.Entry Written)> writes = [];
    private int active;
    private readonly RelationComparisonResult initialReliability = state.Reliability;
    internal int Remaining { get; set; } = (16_000_000 - relation.Count) / 8;
    internal bool Overflow { get; private set; }
    internal int PendingCount => maybe.Count;
    internal IReadOnlyList<Type> SourceStack => sourceStack;
    internal IReadOnlyList<Type> TargetStack => targetStack;

    internal async ValueTask<Ternary> RecursiveAsync(Type source, Type target, IntersectionState intersection, RecursionFlags flags,
        bool reportErrors, Func<ValueTask<Ternary>> structured, Action<Type, Type> reportOverflow, CancellationToken cancellation = default)
    {
        active++;
        try
        {
            return await RecursiveCoreAsync(
                source,
                target,
                intersection,
                flags,
                reportErrors,
                structured,
                reportOverflow,
                cancellation).ConfigureAwait(false);
        }
        catch
        {
            if (active == 1)
                Abort();
            throw;
        }
        finally
        {
            active--;
        }
    }

    private async ValueTask<Ternary> RecursiveCoreAsync(Type source, Type target, IntersectionState intersection, RecursionFlags flags,
        bool reportErrors, Func<ValueTask<Ternary>> structured, Action<Type, Type> reportOverflow, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        if (Overflow)
            return Ternary.False;
        var (key, constrained) = await keys.CreateAsync(
            source,
            target,
            intersection,
            relation.Kind == RelationKind.Identity,
            cancellation: cancellation).ConfigureAwait(false);
        var cached = relation.Get(key);
        if (cached != 0
            && !(reportErrors && (cached & RelationComparisonResult.Failed) != 0 && (cached & RelationComparisonResult.Overflow) == 0))
        {
            state.Reliability |= cached & RelationComparisonResult.ReportsMask;
            if (reportErrors && (cached & RelationComparisonResult.Overflow) != 0)
                reportOverflow(source, target);
            return (cached & RelationComparisonResult.Succeeded) != 0 ? Ternary.True : Ternary.False;
        }
        if (Remaining <= 0)
        {
            Overflow = true;
            return Ternary.False;
        }
        if (maybeSet.Contains(key))
            return Ternary.Maybe;
        if (constrained)
        {
            var broad = await keys.CreateAsync(
                source,
                target,
                intersection,
                relation.Kind == RelationKind.Identity,
                true,
                cancellation).ConfigureAwait(false);
            if (maybeSet.Contains(broad.Key))
                return Ternary.Maybe;
        }
        if (sourceStack.Count == 100 || targetStack.Count == 100)
            return Ternary.Maybe;
        int start = maybe.Count, sourceDepth = sourceStack.Count, targetDepth = targetStack.Count;
        var oldExpanding = expanding;
        var oldReliability = state.Reliability;
        maybe.Add(key);
        maybeSet.Add(key);
        Ternary result;
        RelationComparisonResult reliability = 0;
        try
        {
            if ((flags & RecursionFlags.Source) != 0)
            {
                sourceStack.Add(source);
                if ((expanding & ExpandingFlags.Source) == 0
                    && await recursion.IsDeeplyNestedAsync(source, sourceStack, 3, cancellation).ConfigureAwait(false))
                    expanding |= ExpandingFlags.Source;
            }
            if ((flags & RecursionFlags.Target) != 0)
            {
                targetStack.Add(target);
                if ((expanding & ExpandingFlags.Target) == 0
                    && await recursion.IsDeeplyNestedAsync(target, targetStack, 3, cancellation).ConfigureAwait(false))
                    expanding |= ExpandingFlags.Target;
            }
            state.Reliability = 0;
            result = expanding == ExpandingFlags.Both ? Ternary.Maybe : await structured().ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            reliability = state.Reliability;
        }
        catch
        {
            Reset(start, 0, false);
            state.Reliability = oldReliability;
            throw;
        }
        finally
        {
            if (sourceStack.Count > sourceDepth)
                sourceStack.RemoveRange(sourceDepth, sourceStack.Count - sourceDepth);
            if (targetStack.Count > targetDepth)
                targetStack.RemoveRange(targetDepth, targetStack.Count - targetDepth);
            expanding = oldExpanding;
            state.Reliability |= oldReliability;
        }
        if (result != Ternary.False)
        {
            if (result == Ternary.True || sourceStack.Count == 0 && targetStack.Count == 0)
                Reset(start, reliability, result is Ternary.True or Ternary.Maybe);
        }
        else
        {
            Record(key, RelationComparisonResult.Failed | reliability);
            Remaining--;
            Reset(start, reliability, false);
        }
        return result;
    }

    private void Reset(int start, RelationComparisonResult reliability, bool succeeded)
    {
        for (int i = start; i < maybe.Count; i++)
        {
            maybeSet.Remove(maybe[i]);
            if (succeeded)
            {
                Record(maybe[i], RelationComparisonResult.Succeeded | reliability);
                Remaining--;
            }
        }
        maybe.RemoveRange(start, maybe.Count - start);
    }

    internal async ValueTask CompleteAsync(
        Type source,
        Type target,
        Action<Type, Type> reportOverflow,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (Overflow)
        {
            var (key, _) = await keys.CreateAsync(
                source,
                target,
                identity: relation.Kind == RelationKind.Identity,
                cancellation: cancellation).ConfigureAwait(false);
            Record(key, RelationComparisonResult.Failed | RelationComparisonResult.ComplexityOverflow);
            reportOverflow(source, target);
        }
        maybe.Clear();
        maybeSet.Clear();
        sourceStack.Clear();
        targetStack.Clear();
        writes.Clear();
    }

    private void Record(RelationKey key, RelationComparisonResult result)
    {
        var before = writes.TryGetValue(key, out var old) ? old.Before : relation.Snapshot(key);
        writes[key] = (before, relation.Set(key, result));
    }

    internal void Abort()
    {
        foreach (var (key, entry) in writes)
            relation.Restore(key, entry.Before, entry.Written);
        writes.Clear();
        maybe.Clear();
        maybeSet.Clear();
        sourceStack.Clear();
        targetStack.Clear();
        expanding = 0;
        Overflow = false;
        Remaining = (16_000_000 - relation.Count) / 8;
        state.Reliability = initialReliability;
    }
}

using System.Runtime.CompilerServices;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface ITypeRelationHost
{
    ValueTask<bool> EnumRelatedAsync(Symbol source, Symbol target, CancellationToken cancellation, RelationOperation? operation = null);

    ValueTask<Ternary> IdentityAsync(RelationOperation operation, Type source, Type target, CancellationToken cancellation);

    ValueTask<Ternary> RelatedAsync(RelationOperation operation, Type source, Type target, RecursionFlags recursion,
            IntersectionState intersection, CancellationToken cancellation);

    ValueTask ComplexityOverflowAsync(Type source, Type target, CancellationToken cancellation);
}

internal sealed class TypeRelations(TypeContext context, TypeNormalization normalization, TypeViews views,
    RelationKeys keys, TypeRecursion recursion, ITypeRelationHost host)
{
    private readonly Dictionary<RelationKind, Relation> relations = Enum.GetValues<RelationKind>().ToDictionary(
        k => k,
        k => new Relation(k));
    internal RelationState State { get; } = new();

    internal async ValueTask<bool> HasOverflowAsync(Type source, Type target, RelationKind kind, CancellationToken cancellation)
    {
        var (key, _) = await keys.CreateAsync(source, target, identity: kind == RelationKind.Identity,
            cancellation: cancellation).ConfigureAwait(false);
        return (relations[kind].Get(key) & RelationComparisonResult.Overflow) != 0;
    }

    internal Relation Cache(RelationKind kind) => relations[kind];

    internal async ValueTask<RelationExplanation?> ExplainAsync(Type source, Type target, RelationKind kind, CancellationToken cancellation)
    {
        var session = new RelationSession(context, relations[kind], keys, recursion, State);
        var operation = new RelationOperation(context, this, session, normalization, host, kind, reportErrors: true);
        try
        {
            var result = await operation.CompareAsync(source, target, cancellation: cancellation).ConfigureAwait(false);
            await session.CompleteAsync(source, target, host.ComplexityOverflowAsync, cancellation).ConfigureAwait(false);
            return result == Ternary.False ? operation.Explanation : null;
        }
        catch
        {
            session.Abort();
            throw;
        }
    }

    internal async ValueTask<bool> SignatureAsync(Signature source, Signature target, SignatureAssignability signatures,
        SignatureInstantiation instantiation, bool ignoreReturn, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var sourceType = instantiation.FromSignature(source);
        var targetType = instantiation.FromSignature(target);
        var session = new RelationSession(context, relations[RelationKind.Assignable], keys, recursion, State);
        var operation = new RelationOperation(context, this, session, normalization, host, RelationKind.Assignable);
        try
        {
            var result = await signatures.CompareAsync(
                operation,
                source,
                target,
                ignoreReturn ? SignatureCheckMode.IgnoreReturnTypes : 0,
                cancellation: cancellation).ConfigureAwait(false);
            await session.CompleteAsync(sourceType, targetType, host.ComplexityOverflowAsync, cancellation).ConfigureAwait(false);
            return result != Ternary.False;
        }
        catch
        {
            session.Abort();
            throw;
        }
    }

    internal async ValueTask<bool> RelatedAsync(Type source, Type target, RelationKind kind, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        if (source is LiteralType sl && sl.FreshType == source)
            source = sl.RegularType;
        if (target is LiteralType tl && tl.FreshType == target)
            target = tl.RegularType;
        if (source == target)
            return true;
        if (kind != RelationKind.Identity)
        {
            if (kind == RelationKind.Comparable
                && (target.Flags & TypeFlags.Never) == 0
                && await SimpleAsync(target, source, kind, cancellation).ConfigureAwait(false)
                || await SimpleAsync(source, target, kind, cancellation).ConfigureAwait(false))
                return true;
        }
        else if (((source.Flags | target.Flags) & (TypeFlags.UnionOrIntersection | TypeFlags.IndexedAccess | TypeFlags.Conditional | TypeFlags.Substitution)) == 0)
        {
            if (source.Flags != target.Flags)
                return false;
            if ((source.Flags & TypeFlags.Singleton) != 0)
                return true;
        }
        var relation = relations[kind];
        if (source is ObjectType && target is ObjectType)
        {
            var (key, _) = await keys.CreateAsync(
                source,
                target,
                identity: kind == RelationKind.Identity,
                cancellation: cancellation).ConfigureAwait(false);
            var cached = relation.Get(key);
            if (cached != 0)
                return (cached & RelationComparisonResult.Succeeded) != 0;
        }
        if (((source.Flags | target.Flags) & TypeFlags.StructuredOrInstantiable) == 0)
            return false;
        var session = new RelationSession(context, relation, keys, recursion, State);
        var operation = new RelationOperation(context, this, session, normalization, host, kind);
        try
        {
            var result = await operation.CompareAsync(source, target, cancellation: cancellation).ConfigureAwait(false);
            await session.CompleteAsync(source, target, host.ComplexityOverflowAsync, cancellation).ConfigureAwait(false);
            return result != Ternary.False;
        }
        catch
        {
            session.Abort();
            throw;
        }
    }

    internal async ValueTask<bool> SimpleAsync(Type source, Type target, RelationKind relation, CancellationToken cancellation = default,
        RelationOperation? operation = null)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        var s = source.Flags;
        var t = target.Flags;
        if ((t & TypeFlags.Any) != 0 || (s & TypeFlags.Never) != 0 || source == context.WildcardType)
            return true;
        if ((t & TypeFlags.Unknown) != 0 && !(relation == RelationKind.StrictSubtype && (s & TypeFlags.Any) != 0))
            return true;
        if ((t & TypeFlags.Never) != 0)
            return false;
        if ((s & TypeFlags.StringLike) != 0 && (t & TypeFlags.String) != 0)
            return true;
        if ((s & (TypeFlags.StringLiteral | TypeFlags.EnumLiteral)) == (TypeFlags.StringLiteral | TypeFlags.EnumLiteral)
            && (t & TypeFlags.StringLiteral) != 0 && (t & TypeFlags.EnumLiteral) == 0 && LiteralEqual(
                ((LiteralType)source).Value,
                ((LiteralType)target).Value))
            return true;
        if ((s & TypeFlags.NumberLike) != 0 && (t & TypeFlags.Number) != 0)
            return true;
        if ((s & (TypeFlags.NumberLiteral | TypeFlags.EnumLiteral)) == (TypeFlags.NumberLiteral | TypeFlags.EnumLiteral)
            && (t & TypeFlags.NumberLiteral) != 0 && (t & TypeFlags.EnumLiteral) == 0 && LiteralEqual(
                ((LiteralType)source).Value,
                ((LiteralType)target).Value))
            return true;
        if ((s & TypeFlags.BigIntLike) != 0 && (t & TypeFlags.BigInt) != 0
            || (s & TypeFlags.BooleanLike) != 0 && (t & TypeFlags.Boolean) != 0
            || (s & TypeFlags.ESSymbolLike) != 0 && (t & TypeFlags.ESSymbol) != 0)
            return true;
        if ((s & TypeFlags.Enum) != 0 && (t & TypeFlags.Enum) != 0 && source.Symbol!.Name == target.Symbol!.Name
            && await host.EnumRelatedAsync(source.Symbol, target.Symbol, cancellation, operation).ConfigureAwait(false))
            return true;
        if ((s & TypeFlags.EnumLiteral) != 0 && (t & TypeFlags.EnumLiteral) != 0)
        {
            if (source is UnionType
                && target is UnionType
                && await host.EnumRelatedAsync(source.Symbol!, target.Symbol!, cancellation, operation).ConfigureAwait(false))
                return true;
            if (source is LiteralType a && target is LiteralType b && LiteralEqual(a.Value, b.Value)
                && await host.EnumRelatedAsync(source.Symbol!, target.Symbol!, cancellation, operation).ConfigureAwait(false))
                return true;
        }
        if ((s & TypeFlags.Undefined) != 0
            && (!context.StrictNullChecks && (t & TypeFlags.UnionOrIntersection) == 0 || (t & (TypeFlags.Undefined | TypeFlags.Void)) != 0))
            return true;
        if ((s & TypeFlags.Null) != 0
            && (!context.StrictNullChecks && (t & TypeFlags.UnionOrIntersection) == 0 || (t & TypeFlags.Null) != 0))
            return true;
        if ((s & TypeFlags.Object) != 0 && (t & TypeFlags.NonPrimitive) != 0
            && !(relation == RelationKind.StrictSubtype
                && await views.EmptyAnonymousAsync(source, cancellation).ConfigureAwait(false)
                && (source.ObjectFlags & ObjectFlags.FreshLiteral) == 0))
            return true;
        if (relation is RelationKind.Assignable or RelationKind.Comparable)
        {
            if ((s & TypeFlags.Any) != 0)
                return true;
            if ((s & TypeFlags.Number) != 0
                && ((t & TypeFlags.Enum) != 0
                    || (t & (TypeFlags.NumberLiteral | TypeFlags.EnumLiteral)) == (TypeFlags.NumberLiteral | TypeFlags.EnumLiteral)))
                return true;
            if ((s & TypeFlags.NumberLiteral) != 0 && (s & TypeFlags.EnumLiteral) == 0 && ((t & TypeFlags.Enum) != 0
                || (t & (TypeFlags.NumberLiteral | TypeFlags.EnumLiteral)) == (TypeFlags.NumberLiteral | TypeFlags.EnumLiteral)
                    && LiteralEqual(((LiteralType)source).Value, ((LiteralType)target).Value)))
                return true;
            if (await views.UnknownLikeUnionAsync(target, cancellation).ConfigureAwait(false))
                return true;
        }
        return false;
    }

    private static bool LiteralEqual(object? left, object? right) => left is double a && right is double b ? a == b : Equals(left, right);
}

internal sealed record RelationExplanation(int Code, Type? Source = null, Type? Target = null, Symbol? Property = null,
    RelationExplanation? Next = null, IReadOnlyList<object>? Arguments = null, bool SuppressRelatedInformation = false);

internal sealed class RelationOperation(
    TypeContext context,
    TypeRelations relations,
    RelationSession session,
    TypeNormalization normalization,
    ITypeRelationHost host,
    RelationKind kind,
    bool reportErrors = false)
{
    internal RelationKind Kind => kind;
    internal RelationSession Session => session;
    internal RelationExplanation? Explanation { get; private set; }
    private int suppressed;
    internal bool ReportErrors => reportErrors && suppressed == 0;

    internal void RestoreExplanation(RelationExplanation? explanation) => Explanation = explanation;

    internal void Explain(int code, Type? source = null, Type? target = null, Symbol? property = null)
    {
        if (ReportErrors)
            Explanation = new(code, source, target, property, Explanation);
    }

    internal void ExplainArguments(int code, params object[] arguments)
    {
        if (ReportErrors)
            Explanation = new(code, Next: Explanation, Arguments: Array.AsReadOnly(arguments));
    }

    internal async ValueTask<Ternary> WithoutErrorsAsync(Func<ValueTask<Ternary>> compare)
    {
        suppressed++;
        try
        {
            return await compare().ConfigureAwait(false);
        }
        finally
        {
            suppressed--;
        }
    }

    internal async ValueTask<Ternary> CompareWithoutErrorsAsync(Type source, Type target, RecursionFlags recursion = RecursionFlags.Both,
        IntersectionState intersection = 0, CancellationToken cancellation = default)
    {
        suppressed++;
        try
        {
            return await CompareAsync(source, target, recursion, intersection, cancellation).ConfigureAwait(false);
        }
        finally
        {
            suppressed--;
        }
    }

    internal ValueTask<bool> SimpleAsync(Type source, Type target, CancellationToken cancellation = default, bool report = true)
        => relations.SimpleAsync(source, target, kind, cancellation, report && ReportErrors ? this : null);

    internal ValueTask<Ternary> RecursiveAsync(Type source, Type target, RecursionFlags recursion, IntersectionState intersection,
        Func<ValueTask<Ternary>> compare, CancellationToken cancellation = default)
        =>
            session.RecursiveAsync(
                source,
                target,
                intersection,
                recursion,
                ReportErrors,
                compare,
                host.ComplexityOverflowAsync,
                cancellation);

    internal ValueTask<Ternary> CompareContinuingAsync(Type source, Type target, RecursionFlags recursion = RecursionFlags.Both,
        IntersectionState intersection = 0, CancellationToken cancellation = default)
        => CompareAsync(source, target, recursion, intersection, cancellation, true);

    internal async ValueTask<Ternary> CompareAsync(Type source, Type target, RecursionFlags recursion = RecursionFlags.Both,
            IntersectionState intersection = 0, CancellationToken cancellation = default, bool preservePriorExplanation = false)
    {
        if (!ReportErrors)
            return await CompareCoreAsync(source, target, recursion, intersection, cancellation).ConfigureAwait(false);
        var previous = Explanation;
        Explanation = null;
        try
        {
            var result = await CompareCoreAsync(source, target, recursion, intersection, cancellation).ConfigureAwait(false);
            if (result == Ternary.False && preservePriorExplanation && previous is not null)
            {
                var pending = new Stack<RelationExplanation>();
                for (var current = Explanation; current is not null; current = current.Next)
                    pending.Push(current);
                Explanation = previous;
                while (pending.TryPop(out var current))
                    Explanation = current with { Next = Explanation };
            }
            Explanation = result == Ternary.False
                ? new(kind == RelationKind.Comparable ? 2678 : 2322, source, target, Next: Explanation)
                : previous;
            return result;
        }
        catch
        {
            Explanation = previous;
            throw;
        }
    }

    private async ValueTask<Ternary> CompareCoreAsync(Type source, Type target, RecursionFlags recursion,
            IntersectionState intersection, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        if (source == target)
            return Ternary.True;
        if (source is ObjectType && (target.Flags & TypeFlags.Primitive) != 0)
            return kind == RelationKind.Comparable
                && (target.Flags & TypeFlags.Never) == 0
                && await relations.SimpleAsync(target, source, kind, cancellation).ConfigureAwait(false)
                || await relations.SimpleAsync(source, target, kind, cancellation).ConfigureAwait(false) ? Ternary.True : Ternary.False;
        source = await normalization.GetAsync(source, false, cancellation).ConfigureAwait(false);
        target = await normalization.GetAsync(target, true, cancellation).ConfigureAwait(false);
        if (source == target)
            return Ternary.True;
        if (kind == RelationKind.Identity)
        {
            if (source.Flags != target.Flags)
                return Ternary.False;
            if ((source.Flags & TypeFlags.Singleton) != 0)
                return Ternary.True;
            return await session.RecursiveAsync(
                source,
                target,
                0,
                recursion,
                false,
                () => host.IdentityAsync(this, source, target, cancellation),
                host.ComplexityOverflowAsync,
                cancellation).ConfigureAwait(false);
        }
        return await host.RelatedAsync(this, source, target, recursion, intersection, cancellation).ConfigureAwait(false);
    }
}

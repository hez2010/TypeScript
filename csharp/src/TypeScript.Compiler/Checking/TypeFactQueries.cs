using TypeScript.Compiler.Text;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace TypeScript.Compiler.Checking;

internal interface ITypeFactHost
{
    Type GlobalFunction { get; }

    ValueTask<bool> SubtypeAsync(Type source, Type target, CancellationToken cancellation);

    ValueTask<Type> NonNullableInstantiationAsync(Type type, CancellationToken cancellation);
}

internal sealed class TypeFactQueries(
    TypeContext context,
    TypeAlgebra algebra,
    TypeConstraints constraints,
    TypeViews views,
    StructuredMembers members,
    ITypeFactHost host)
{
    internal async ValueTask<TypeFacts> GetAsync(Type type, TypeFacts mask, CancellationToken cancellation = default)
        => await WorkerAsync(type, mask, cancellation).ConfigureAwait(false) & mask;

    private async ValueTask<TypeFacts> WorkerAsync(Type type, TypeFacts mask, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(
            RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.Flags & (TypeFlags.Intersection | TypeFlags.Instantiable)) != 0)
            type = await constraints.BaseConstraintAsync(type, cancellation).ConfigureAwait(false) ?? context.UnknownType;
        var flags = type.Flags;
        bool strict = context.StrictNullChecks;
        if ((flags & (TypeFlags.String | TypeFlags.StringMapping)) != 0)
            return strict ? TypeFacts.StringStrictFacts : TypeFacts.StringFacts;
        if ((flags & (TypeFlags.StringLiteral | TypeFlags.TemplateLiteral)) != 0)
        {
            bool empty = type is LiteralType { Value: TextSlice text } && text.Length == 0;
            return strict
                ? empty ? TypeFacts.EmptyStringStrictFacts : TypeFacts.NonEmptyStringStrictFacts
                : empty ? TypeFacts.EmptyStringFacts : TypeFacts.NonEmptyStringFacts;
        }
        if ((flags & (TypeFlags.Number | TypeFlags.Enum)) != 0)
            return strict ? TypeFacts.NumberStrictFacts : TypeFacts.NumberFacts;
        if ((flags & TypeFlags.NumberLiteral) != 0)
        {
            bool zero = (double)((LiteralType)type).Value! == 0;
            return strict
                ? zero ? TypeFacts.ZeroNumberStrictFacts : TypeFacts.NonZeroNumberStrictFacts
                : zero ? TypeFacts.ZeroNumberFacts : TypeFacts.NonZeroNumberFacts;
        }
        if ((flags & TypeFlags.BigInt) != 0)
            return strict ? TypeFacts.BigIntStrictFacts : TypeFacts.BigIntFacts;
        if ((flags & TypeFlags.BigIntLiteral) != 0)
        {
            bool zero = ((BigInteger)((LiteralType)type).Value!).IsZero;
            return strict
                ? zero ? TypeFacts.ZeroBigIntStrictFacts : TypeFacts.NonZeroBigIntStrictFacts
                : zero ? TypeFacts.ZeroBigIntFacts : TypeFacts.NonZeroBigIntFacts;
        }
        if ((flags & TypeFlags.Boolean) != 0)
            return strict ? TypeFacts.BooleanStrictFacts : TypeFacts.BooleanFacts;
        if ((flags & TypeFlags.BooleanLike) != 0)
        {
            bool isFalse = type == context.FalseType || type == context.RegularFalseType;
            return strict
                ? isFalse ? TypeFacts.FalseStrictFacts : TypeFacts.TrueStrictFacts
                : isFalse ? TypeFacts.FalseFacts : TypeFacts.TrueFacts;
        }
        if ((flags & TypeFlags.Object) != 0)
        {
            var possible = strict
                ? TypeFacts.EmptyObjectStrictFacts | TypeFacts.FunctionStrictFacts | TypeFacts.ObjectStrictFacts
                : TypeFacts.EmptyObjectFacts | TypeFacts.FunctionFacts | TypeFacts.ObjectFacts;
            if ((mask & possible) == 0)
                return 0;
            if ((type.ObjectFlags & ObjectFlags.Anonymous) != 0 && await views.EmptyObjectAsync(type, cancellation).ConfigureAwait(false))
                return strict ? TypeFacts.EmptyObjectStrictFacts : TypeFacts.EmptyObjectFacts;
            if (await FunctionAsync((ObjectType)type, cancellation).ConfigureAwait(false))
                return strict ? TypeFacts.FunctionStrictFacts : TypeFacts.FunctionFacts;
            return strict ? TypeFacts.ObjectStrictFacts : TypeFacts.ObjectFacts;
        }
        if ((flags & TypeFlags.Void) != 0)
            return TypeFacts.VoidFacts;
        if ((flags & TypeFlags.Undefined) != 0)
            return TypeFacts.UndefinedFacts;
        if ((flags & TypeFlags.Null) != 0)
            return TypeFacts.NullFacts;
        if ((flags & TypeFlags.ESSymbolLike) != 0)
            return strict ? TypeFacts.SymbolStrictFacts : TypeFacts.SymbolFacts;
        if ((flags & TypeFlags.NonPrimitive) != 0)
            return strict ? TypeFacts.ObjectStrictFacts : TypeFacts.ObjectFacts;
        if ((flags & TypeFlags.Never) != 0)
            return 0;
        if (type is UnionType union)
        {
            TypeFacts result = 0;
            foreach (var part in union.Types)
                result |= await WorkerAsync(part, mask, cancellation).ConfigureAwait(false);
            return result;
        }
        if (type is IntersectionType intersection)
        {
            bool ignore = MaybeKind(intersection, TypeFlags.Primitive);
            TypeFacts either = 0, both = TypeFacts.All;
            foreach (var part in intersection.Types)
                if (!ignore || (part.Flags & TypeFlags.Object) == 0)
                {
                    var facts = await WorkerAsync(part, mask, cancellation).ConfigureAwait(false);
                    either |= facts;
                    both &= facts;
                }
            return either & TypeFacts.OrFactsMask | both & TypeFacts.AndFactsMask;
        }
        return TypeFacts.UnknownFacts;
    }

    private async ValueTask<bool> FunctionAsync(ObjectType type, CancellationToken cancellation)
    {
        if ((type.ObjectFlags & ObjectFlags.EvolvingArray) != 0)
            return false;
        var resolved = await members.ResolveAsync(type, cancellation).ConfigureAwait(false);
        return resolved.CallSignatures.Count != 0 || resolved.ConstructSignatures.Count != 0
            || resolved.Members?.ContainsKey("bind") == true
                && await host.SubtypeAsync(type, host.GlobalFunction, cancellation).ConfigureAwait(false);
    }

    private static bool MaybeKind(Type type, TypeFlags kind)
    {
        var pending = new Stack<Type>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            if ((current.Flags & kind) != 0)
                return true;
            if (current is UnionOrIntersectionType composite)
                foreach (var part in composite.Types)
                    pending.Push(part);
        }
        return false;
    }

    internal async ValueTask<Type> FilterAsync(Type type, TypeFacts include, CancellationToken cancellation = default)
    {
        var decisions = new Dictionary<Type, bool>();
        var candidates = type is UnionType union ? union.Types : [type];
        foreach (var part in candidates)
            decisions[part] = (await GetAsync(part, include, cancellation).ConfigureAwait(false)) != 0;
        if (type is UnionType { Origin: UnionType origin })
            foreach (var part in origin.Types)
                if (part is not UnionType && !decisions.ContainsKey(part))
                    decisions[part] = (await GetAsync(part, include, cancellation).ConfigureAwait(false)) != 0;
        return algebra.Filter(type, t => decisions[t]);
    }

    internal ValueTask<Type> NonNullableAsync(Type type, CancellationToken cancellation = default)
        => context.StrictNullChecks ? AdjustAsync(type, TypeFacts.NEUndefinedOrNull, cancellation) : ValueTask.FromResult(type);

    internal async ValueTask<Type> AdjustAsync(Type type, TypeFacts facts, CancellationToken cancellation = default)
    {
        var result = await FilterAsync(
            context.StrictNullChecks && (type.Flags & TypeFlags.Unknown) != 0 ? context.UnknownUnionType : type,
            facts,
            cancellation).ConfigureAwait(false);
        if (result == context.UnknownUnionType)
            result = context.UnknownType;
        if (!context.StrictNullChecks)
            return result;
        if (facts == TypeFacts.NEUndefined)
            return await RemoveNullableAsync(
                result,
                TypeFacts.EQUndefined,
                TypeFacts.EQNull,
                TypeFacts.IsNull,
                context.NullType,
                cancellation).ConfigureAwait(false);
        if (facts == TypeFacts.NENull)
            return await RemoveNullableAsync(
                result,
                TypeFacts.EQNull,
                TypeFacts.EQUndefined,
                TypeFacts.IsUndefined,
                context.UndefinedType,
                cancellation).ConfigureAwait(false);
        if (facts is TypeFacts.NEUndefinedOrNull or TypeFacts.Truthy)
            return await algebra.MapAsync(
                result,
                async part => (await GetAsync(part, TypeFacts.EQUndefinedOrNull, cancellation).ConfigureAwait(false)) != 0
                ? await host.NonNullableInstantiationAsync(part, cancellation).ConfigureAwait(false) : part,
                cancellation: cancellation).ConfigureAwait(false) ?? context.NeverType;
        return result;
    }

    private async ValueTask<Type> RemoveNullableAsync(
        Type type,
        TypeFacts target,
        TypeFacts other,
        TypeFacts otherIncludes,
        Type otherType,
        CancellationToken cancellation)
    {
        var facts = await GetAsync(
            type,
            TypeFacts.EQUndefined | TypeFacts.EQNull | TypeFacts.IsUndefined | TypeFacts.IsNull,
            cancellation).ConfigureAwait(false);
        if ((facts & target) == 0)
            return type;
        var nullable = await algebra.UnionAsync([context.EmptyObjectType, otherType], cancellation: cancellation).ConfigureAwait(false);
        return await algebra.MapAsync(type, async part =>
        {
            if ((await GetAsync(part, target, cancellation).ConfigureAwait(false)) == 0)
                return part;
            var right = (facts & otherIncludes) == 0 && (await GetAsync(part, other, cancellation).ConfigureAwait(false)) != 0
                ? nullable
                : context.EmptyObjectType;
            return await algebra.IntersectionAsync([part, right], cancellation: cancellation).ConfigureAwait(false);
        }, cancellation: cancellation).ConfigureAwait(false) ?? context.NeverType;
    }
}

using System.Runtime.CompilerServices;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : ITypeAlgebraHost
{
    internal List<int> AlgebraDiagnostics { get; } = [];

    ValueTask<Type?> ITypeAlgebraHost.GetBaseConstraintAsync(Type type, CancellationToken cancellation) =>
        Instantiation.Constraints.BaseConstraintAsync(type, cancellation);

    ValueTask<bool> ITypeAlgebraHost.IsSubtypeAsync(Type source, Type target, bool strict, CancellationToken cancellation) =>
        Relations.RelatedAsync(source, target, strict ? RelationKind.StrictSubtype : RelationKind.Subtype, cancellation);

    ValueTask<bool> ITypeAlgebraHost.IsDerivedFromAsync(Type source, Type target, CancellationToken cancellation) =>
        DerivedAsync(source, target, cancellation);

    ValueTask<IReadOnlyList<Symbol>> ITypeAlgebraHost.GetPropertiesAsync(Type type, CancellationToken cancellation) =>
        Properties.GetAsync(type, cancellation);

    ValueTask<Type> ITypeAlgebraHost.GetTypeOfSymbolAsync(Symbol symbol, CancellationToken cancellation) =>
        Values.GetAsync(symbol, cancellation);

    async ValueTask<Type?> ITypeAlgebraHost.GetPropertyTypeAsync(Type type, string name, CancellationToken cancellation) =>
        await Properties.PropertyAsync(type, name, cancellation: cancellation).ConfigureAwait(false) is { } property
            ? await Values.GetAsync(property, cancellation).ConfigureAwait(false)
            : null;

    ValueTask<bool> ITypeAlgebraHost.IsEmptyAnonymousObjectAsync(Type type, CancellationToken cancellation) =>
        Views.EmptyAnonymousAsync(type, cancellation);

    ValueTask<bool> ITypeAlgebraHost.IsEmptyObjectAsync(Type type, CancellationToken cancellation) =>
        Views.EmptyObjectAsync(type, cancellation);

    async ValueTask<bool> ITypeAlgebraHost.IsGenericIndexAsync(Type type, CancellationToken cancellation) =>
        (await Instantiation.Mapped.GenericFlagsAsync(type, cancellation).ConfigureAwait(false) & ObjectFlags.IsGenericIndexType) != 0;

    ValueTask<bool> ITypeAlgebraHost.MatchesPatternAsync(Type literal, Type pattern, CancellationToken cancellation) => pattern is TemplateLiteralType template
            ? Templates.MatchesAsync(
                literal,
                template,
                async (source, target, token) => await AssignableAsync(source, target, token).ConfigureAwait(false)
                    ? Ternary.True
                    : Ternary.False,
                cancellation)
            : Templates.MemberAsync(literal, pattern, cancellation);

    void ITypeAlgebraHost.ReportComplexity(string operation, long size)
    {
        AlgebraDiagnostics.Add(2590);
        TrackDiagnostic(DiagnosticNode, 2590);
    }

    public async ValueTask<bool> DerivedAsync(Type source, Type target, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(
            RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        if (source is UnionType sourceUnion)
        {
            foreach (var part in sourceUnion.Types)
                if (!await DerivedAsync(part, target, cancellation).ConfigureAwait(false))
                    return false;
            return true;
        }
        if (target is UnionType targetUnion)
        {
            foreach (var part in targetUnion.Types)
                if (await DerivedAsync(source, part, cancellation).ConfigureAwait(false))
                    return true;
            return false;
        }
        if (source is IntersectionType intersection)
        {
            foreach (var part in intersection.Types)
                if (await DerivedAsync(part, target, cancellation).ConfigureAwait(false))
                    return true;
            return false;
        }
        if ((source.Flags & TypeFlags.InstantiableNonPrimitive) != 0)
            return await DerivedAsync(
                await Instantiation.Constraints.BaseConstraintAsync(source, cancellation).ConfigureAwait(false) ?? context.UnknownType,
                target,
                cancellation).ConfigureAwait(false);
        if (await Views.EmptyAnonymousAsync(target, cancellation).ConfigureAwait(false))
            return (source.Flags & (TypeFlags.Object | TypeFlags.NonPrimitive)) != 0;
        if (target == GlobalObject)
            return (source.Flags & (TypeFlags.Object | TypeFlags.NonPrimitive)) != 0
                && !await Views.EmptyAnonymousAsync(source, cancellation).ConfigureAwait(false);
        if (target == GlobalFunction)
        {
            if (source is not ObjectType objectType || (source.ObjectFlags & ObjectFlags.EvolvingArray) != 0)
                return false;
            var resolved = await Members.ResolveAsync(objectType, cancellation).ConfigureAwait(false);
            return resolved.CallSignatures.Count != 0 || resolved.ConstructSignatures.Count != 0
                || resolved.Members?.ContainsKey("bind") == true
                    && await Relations.RelatedAsync(source, GlobalFunction, RelationKind.Subtype, cancellation).ConfigureAwait(false);
        }
        return await Bases.HasBaseAsync(
            source,
            target is TypeReference reference && (target.ObjectFlags & ObjectFlags.Reference) != 0 ? reference.Target! : target,
            cancellation).ConfigureAwait(false)
            || IsArray(target)
                && !IsReadonlyArray(target)
                && await DerivedAsync(source, ArrayTarget(true), cancellation).ConfigureAwait(false);
    }
}

using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface ITypeNormalizationHost
{
    ValueTask<Type> SimplifyAsync(Type type, bool writing, CancellationToken cancellation);

    ValueTask<IReadOnlyDictionary<Utf8String, Symbol>> MembersAsync(Symbol symbol, CancellationToken cancellation);
}

internal sealed class TypeNormalization(TypeContext context, TypeAlgebra algebra, TypeReferences references, TupleTypes tuples,
    TypeInstantiation instantiation, BaseTypes bases, TypeViews views, ITypeNormalizationHost host)
{
    private readonly Dictionary<Type, Type> equivalentBases = [];

    internal async ValueTask<Type> RelationTargetAsync(Type source, Type target, CancellationToken cancellation)
    {
        if ((source.Flags & TypeFlags.DefinitelyNonNullable) != 0 && target is UnionType union)
        {
            var parts = union.Types;
            Type? candidate = parts.Count == 2 && (parts[0].Flags & TypeFlags.Nullable) != 0 ? parts[1]
                : parts.Count == 3 && (parts[0].Flags & TypeFlags.Nullable) != 0 && (parts[1].Flags & TypeFlags.Nullable) != 0
                    ? parts[2] : null;
            if (candidate is not null && (candidate.Flags & TypeFlags.Nullable) == 0)
                return await GetAsync(candidate, true, cancellation).ConfigureAwait(false);
        }
        return target;
    }

    internal async ValueTask<Type> GetAsync(Type type, bool writing = false, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        context.RequireOwned(type);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            Type next;
            if (type is LiteralType literal && literal.FreshType == type)
                next = literal.RegularType;
            else if (TypeConstraints.IsGenericTuple(type))
                next = await TupleAsync((TypeReference)type, writing, cancellation).ConfigureAwait(false);
            else if (type is TypeReference reference && (type.ObjectFlags & ObjectFlags.Reference) != 0)
                next = reference.Node is not null ? context.CreateTypeReference(
                    (InterfaceType)reference.ReferencedType,
                    (await references.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false)).ToArray())
                    : await SingleBaseAsync(reference, cancellation).ConfigureAwait(false) ?? type;
            else if (type is UnionOrIntersectionType composite)
            {
                next = await views.ReducedAsync(type, cancellation).ConfigureAwait(false);
                if (next == type
                    && composite is IntersectionType
                    && await NormalizeIntersectionAsync(composite, cancellation).ConfigureAwait(false))
                {
                    var parts = new Type[composite.Types.Count];
                    bool changed = false;
                    for (int i = 0; i < parts.Length; i++)
                    {
                        parts[i] = await GetAsync(composite.Types[i], writing, cancellation).ConfigureAwait(false);
                        changed |= parts[i] != composite.Types[i];
                    }
                    if (changed)
                        next = await algebra.IntersectionAsync(parts, cancellation: cancellation).ConfigureAwait(false);
                }
            }
            else if (type is SubstitutionType substitution)
                next = writing
                    ? substitution.BaseType
                    : await algebra.IntersectionAsync(
                        [substitution.Constraint, substitution.BaseType],
                        cancellation: cancellation).ConfigureAwait(false);
            else if ((type.Flags & TypeFlags.Simplifiable) != 0)
                next = await host.SimplifyAsync(type, writing, cancellation).ConfigureAwait(false);
            else
                return type;
            if (next == type)
                return next;
            type = next;
        }
    }

    private async ValueTask<bool> NormalizeIntersectionAsync(UnionOrIntersectionType type, CancellationToken cancellation)
    {
        bool instantiable = false, nullable = false;
        foreach (var part in type.Types)
        {
            instantiable |= (part.Flags & TypeFlags.Instantiable) != 0;
            nullable = nullable
                || (part.Flags & TypeFlags.Nullable) != 0
                || await views.EmptyAnonymousAsync(part, cancellation).ConfigureAwait(false);
            if (instantiable && nullable)
                return true;
        }
        return false;
    }

    private async ValueTask<Type> TupleAsync(TypeReference type, bool writing, CancellationToken cancellation)
    {
        var target = (TupleType)type.ReferencedType;
        var elements = (await references.TypeArgumentsAsync(
            type,
            cancellation).ConfigureAwait(false)).Take(target.ElementInfos.Count).ToArray();
        bool changed = false;
        for (int i = 0; i < elements.Length; i++)
            if ((elements[i].Flags & TypeFlags.Simplifiable) != 0)
            {
                var next = await host.SimplifyAsync(elements[i], writing, cancellation).ConfigureAwait(false);
                changed |= next != elements[i];
                elements[i] = next;
            }
        return changed ? await tuples.NormalizeReferenceAsync(target, elements, cancellation: cancellation).ConfigureAwait(false) : type;
    }

    internal async ValueTask<Type?> SingleBaseAsync(TypeReference type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.ObjectFlags & ObjectFlags.Reference) == 0 || (type.ReferencedType.ObjectFlags & ObjectFlags.ClassOrInterface) == 0)
            return null;
        if ((type.ObjectFlags & ObjectFlags.IdenticalBaseTypeCalculated) != 0)
            return equivalentBases.GetValueOrDefault(type);
        type.ObjectFlags |= ObjectFlags.IdenticalBaseTypeCalculated;
        try
        {
            var target = (InterfaceType)type.ReferencedType;
            if ((target.ObjectFlags & ObjectFlags.Class) != 0)
            {
                var declaration = target.Symbol!.Declarations.OfType<ClassDeclarationNode>().FirstOrDefault();
                var node = declaration?.HeritageClauses?.OfType<HeritageClauseNode>().FirstOrDefault(c => c.Token == SyntaxKind.ExtendsKeyword)?.Types?.FirstOrDefault();
                if (node is ExpressionWithTypeArgumentsNode expression
                    && expression.Expression is not (IdentifierNode or PropertyAccessExpressionNode))
                    return null;
            }
            var baseTypes = await bases.GetAsync(target, cancellation).ConfigureAwait(false);
            if (baseTypes.Count != 1 || (await host.MembersAsync(type.Symbol!, cancellation).ConfigureAwait(false)).Count != 0)
                return null;
            int count = target.AllTypeParameters.Count - (target.ThisType is null ? 0 : 1);
            Type result = baseTypes[0];
            if (count != 0)
            {
                var arguments = await references.TypeArgumentsAsync(type, cancellation).ConfigureAwait(false);
                result = await instantiation.InstantiateAsync(
                    result,
                    TypeMapper.Create(target.AllTypeParameters.Take(count).ToArray(), arguments.Take(count).ToArray()),
                    cancellation: cancellation).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Base normalization returned no type");
            }
            var fullArguments = await references.TypeArgumentsAsync(type, cancellation).ConfigureAwait(false);
            if (fullArguments.Count > count)
                result = await bases.WithThisAsync(result, fullArguments[^1], cancellation: cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            equivalentBases[type] = result;
            return result;
        }
        catch
        {
            type.ObjectFlags &= ~ObjectFlags.IdenticalBaseTypeCalculated;
            equivalentBases.Remove(type);
            throw;
        }
    }
}

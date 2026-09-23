using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IBaseTypeHost
{
    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> ReducedAsync(Type type, CancellationToken cancellation);

    ValueTask<Type> ApparentAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Type>> ClassBasesAsync(InterfaceType type, CancellationToken cancellation);

    ValueTask<Type> IndexedAccessAsync(Type objectType, Type indexType, CancellationToken cancellation);

    void CircularBase(SyntaxNode declaration, Type type);

    void InvalidInterfaceBase(SyntaxNode declaration);
}

internal sealed class BaseTypes(TypeContext context, TypeAlgebra algebra, TypeConstraints constraints, TypeReferences references,
    TupleTypes tuples, MappedTypes mapped, TypeResolutionStack resolutions, IBaseTypeHost host)
{
    internal async ValueTask<IReadOnlyList<Type>> GetAsync(InterfaceType type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type.BaseTypesResolved)
            return type.ResolvedBaseTypes ?? [];
        if (!resolutions.Push(type, TypeSystemPropertyName.ResolvedBaseTypes))
            return type.ResolvedBaseTypes ?? [];
        var previous = type.ResolvedBaseTypes;
        bool active = true;
        try
        {
            if (type is TupleType tuple)
            {
                var elements = tuple.ResolvedTypeArguments!.ToArray();
                for (int i = 0; i < elements.Length; i++)
                    if ((tuple.ElementInfos[i].Flags & ElementFlags.Variadic) != 0)
                        elements[i] = await host.IndexedAccessAsync(elements[i], context.NumberType, cancellation).ConfigureAwait(false);
                type.ResolvedBaseTypes = [await tuples.ArrayAsync(
                    await algebra.UnionAsync(elements, cancellation: cancellation).ConfigureAwait(false),
                    tuple.IsReadonly,
                    cancellation).ConfigureAwait(false)];
            }
            else if (type.Symbol is { } symbol)
            {
                if ((symbol.Flags & SymbolFlags.Class) != 0)
                    type.ResolvedBaseTypes = await host.ClassBasesAsync(type, cancellation).ConfigureAwait(false);
                if ((symbol.Flags & SymbolFlags.Interface) != 0)
                {
                    var result = (type.ResolvedBaseTypes ?? []).ToList();
                    foreach (var declaration in symbol.Declarations.OfType<InterfaceDeclarationNode>())
                        foreach (var heritage in declaration.HeritageClauses?.OfType<HeritageClauseNode>() ?? [])
                            if (heritage.Token == SyntaxKind.ExtendsKeyword && heritage.Types is { } bases)
                                foreach (var node in bases)
                                {
                                    var baseType = await host.ReducedAsync(
                                        await host.TypeFromNodeAsync(node, cancellation).ConfigureAwait(false),
                                        cancellation).ConfigureAwait(false);
                                    if (baseType == context.ErrorType
                                        || (baseType.Flags & TypeFlags.Any) != 0 && baseType.Alias is not null)
                                        continue;
                                    if (!await ValidAsync(baseType, cancellation).ConfigureAwait(false))
                                        host.InvalidInterfaceBase(node);
                                    else if (type == baseType || await HasBaseAsync(baseType, type, cancellation).ConfigureAwait(false))
                                        host.CircularBase(declaration, type);
                                    else
                                    {
                                        result.Add(baseType);
                                        type.ResolvedBaseTypes = result.AsReadOnly();
                                    }
                                }
                }
            }
            else
                throw new InvalidOperationException("Class/interface has no symbol");
            bool resolved = resolutions.Pop();
            active = false;
            if (!resolved && type.Symbol is { } circular)
                foreach (var declaration in circular.Declarations.Where(
                    d => d.Kind is SyntaxKind.ClassDeclaration or SyntaxKind.InterfaceDeclaration))
                    host.CircularBase(declaration, type);
            cancellation.ThrowIfCancellationRequested();
            type.ObjectFlags &= ~ObjectFlags.MembersResolved;
            type.BaseTypesResolved = true;
            return type.ResolvedBaseTypes ?? [];
        }
        catch
        {
            type.ResolvedBaseTypes = previous;
            throw;
        }
        finally
        {
            if (active)
                resolutions.Pop();
        }
    }

    internal async ValueTask<bool> ValidAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type is TypeParameter && await constraints.BaseConstraintAsync(type, cancellation).ConfigureAwait(false) is { } constraint)
            return await ValidAsync(constraint, cancellation).ConfigureAwait(false);
        if ((type.Flags & (TypeFlags.Object | TypeFlags.NonPrimitive | TypeFlags.Any)) != 0)
            return type is not MappedType mappedType || !await mapped.IsGenericAsync(mappedType, cancellation).ConfigureAwait(false);
        if (type is IntersectionType intersection)
        {
            foreach (var part in intersection.Types)
                if (!await ValidAsync(part, cancellation).ConfigureAwait(false))
                    return false;
            return true;
        }
        return false;
    }

    internal async ValueTask<bool> HasBaseAsync(Type type, Type target, CancellationToken cancellation = default)
    {
        context.RequireOwned(type);
        context.RequireOwned(target);
        var pending = new Stack<Type>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if (current is InterfaceType || current is TypeReference && (current.ObjectFlags & ObjectFlags.Reference) != 0)
            {
                var source = (current.ObjectFlags & ObjectFlags.Reference) != 0 ? ((TypeReference)current).ReferencedType : current;
                if (source == target)
                    return true;
                var bases = await GetAsync((InterfaceType)source, cancellation).ConfigureAwait(false);
                for (int i = bases.Count - 1; i >= 0; i--)
                    pending.Push(bases[i]);
            }
            else if (current is IntersectionType intersection)
                for (int i = intersection.Types.Count - 1; i >= 0; i--)
                    pending.Push(intersection.Types[i]);
        }
        return false;
    }

    internal async ValueTask<Type> WithThisAsync(Type type, Type? argument, bool apparent = false, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (argument is not null)
            context.RequireOwned(argument);
        if (type is TypeReference reference && (type.ObjectFlags & ObjectFlags.Reference) != 0)
        {
            var target = (InterfaceType)reference.ReferencedType;
            var arguments = await references.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false);
            return arguments.Count == target.AllTypeParameters.Count - (target.ThisType is null ? 0 : 1)
                ? context.CreateTypeReference(target, [.. arguments, argument ?? target.ThisType!]) : type;
        }
        if (type is IntersectionType intersection)
        {
            var parts = new Type[intersection.Types.Count];
            bool changed = false;
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = await WithThisAsync(intersection.Types[i], argument, apparent, cancellation).ConfigureAwait(false);
                changed |= parts[i] != intersection.Types[i];
            }
            return changed ? await algebra.IntersectionAsync(parts, cancellation: cancellation).ConfigureAwait(false) : type;
        }
        return apparent ? await host.ApparentAsync(type, cancellation).ConfigureAwait(false) : type;
    }
}

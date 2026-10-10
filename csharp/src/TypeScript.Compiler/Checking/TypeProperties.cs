using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using S = TypeScript.Compiler.Binding.SymbolFlags;
using C = TypeScript.Compiler.Binding.CheckFlags;
using F = TypeScript.Compiler.Checking.TypeFlags;
using O = TypeScript.Compiler.Checking.ObjectFlags;

namespace TypeScript.Compiler.Checking;

internal interface ITypePropertyHost
{
    ValueTask<Type> ReducedApparentAsync(Type type, CancellationToken cancellation);

    ValueTask<Type> ApparentAsync(Type type, CancellationToken cancellation);

    ValueTask<StructuredType> ResolveAsync(StructuredType type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    ValueTask<IndexInfo?> ApplicableIndexAsync(Type type, Utf8String name, CancellationToken cancellation);

    ValueTask<Type?> TupleRestAsync(TypeReference type, CancellationToken cancellation);

    bool IsReadonly(Symbol symbol);

    CheckFlags AccessFlags(Symbol symbol, bool write);

    Type GlobalObject { get; }
    Type GlobalFunction { get; }
    Type GlobalCallableFunction { get; }
    Type GlobalNewableFunction { get; }
}

internal sealed class TypeProperties(TypeContext context, CheckerLinks links, CheckerSymbols symbols, TypeParameterScopes scopes,
    AliasResolver aliases, SymbolTypes values, TypeAlgebra algebra, ITypePropertyHost host)
{
    internal async ValueTask<IReadOnlyList<Symbol>> GetAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        type = await host.ReducedApparentAsync(type, cancellation).ConfigureAwait(false);
        return type is UnionOrIntersectionType composite ? await CompositePropertiesAsync(composite, cancellation).ConfigureAwait(false)
            : type is ObjectType obj ? (await host.ResolveAsync(obj, cancellation).ConfigureAwait(false)).Properties ?? [] : [];
    }

    internal async ValueTask<IReadOnlyList<Symbol>> CompositePropertiesAsync(
        UnionOrIntersectionType type,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type.ResolvedProperties is { } cached)
            return cached;
        var names = new HashSet<Utf8String>();
        var result = new List<Symbol>();
        foreach (var part in type.Types)
        {
            foreach (var property in await GetAsync(part, cancellation).ConfigureAwait(false))
                if (names.Add(property.Name)
                    && await CompositePropertyAsync(
                        type,
                        property.Name,
                        type is IntersectionType,
                        cancellation).ConfigureAwait(false) is { } combined)
                    result.Add(combined);
            if (type is UnionType && (await host.IndexesAsync(part, cancellation).ConfigureAwait(false)).Count == 0)
                break;
        }
        cancellation.ThrowIfCancellationRequested();
        return type.ResolvedProperties = result.AsReadOnly();
    }

    internal ValueTask<Symbol?> PropertyAsync(Type type, Utf8String name, bool skipAugment = false, bool includeTypeOnly = false,
        CancellationToken cancellation = default)
    {
        // Fast path for the shape contextual queries hit constantly: a plain object type whose member
        // table is already resolved. ReducedAsync and ApparentAsync are identity for such a type (they
        // only transform unions, intersections, mapped types, references and instantiable types),
        // ResolveAsync returns immediately once MembersResolved is set, and what remains are flag tests.
        // Aliases fall through because their value flags need the resolver, and the module check reads
        // its link without creating one.
        if (type is ObjectType obj && (obj.ObjectFlags & ObjectFlags.MembersResolved) != 0
            && (type.Flags & TypeFlags.Instantiable) == 0 && type is not (TypeReference or MappedType or UnionOrIntersectionType)
            && obj.Members is { } members && members.GetValueOrDefault(name) is { } symbol
            && (symbol.Flags & S.Value) != 0
            && (includeTypeOnly || !(type.Symbol is { } owner && (owner.Flags & S.ValueModule) != 0
                && links.Modules.TryGet(owner)?.TypeOnlyExportStars?.ContainsKey(name) == true)))
        {
            return ValueTask.FromResult<Symbol?>(symbol);
        }
        Diagnostics.CompilationCapture.ProbeMark lookupMark = Diagnostics.CompilationCapture.Mark();
        try
        {
            return PropertySlowAsync(type, name, skipAugment, includeTypeOnly, cancellation);
        }
        finally
        {
            Diagnostics.CompilationCapture.Report(Diagnostics.AllocationProbes.PropertyLookup, lookupMark);
        }
    }

    private async ValueTask<Symbol?> PropertySlowAsync(Type type, Utf8String name, bool skipAugment, bool includeTypeOnly,
        CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        type = await host.ReducedApparentAsync(type, cancellation).ConfigureAwait(false);
        if (type is ObjectType obj)
        {
            var resolved = await host.ResolveAsync(obj, cancellation).ConfigureAwait(false);
            if (resolved.Members?.GetValueOrDefault(name) is { } symbol)
            {
                if (!includeTypeOnly && type.Symbol is { } owner && (owner.Flags & S.ValueModule) != 0
                    && links.Modules.Get(owner).TypeOnlyExportStars?.ContainsKey(name) == true)
                    return null;
                if (await ValueAsync(symbol, includeTypeOnly, cancellation).ConfigureAwait(false))
                    return symbol;
            }
            if (skipAugment)
                return null;
            var function = type == context.AnyFunctionType ? host.GlobalFunction
                : resolved.CallSignatures.Count != 0 ? host.GlobalCallableFunction
                : resolved.ConstructSignatures.Count != 0 ? host.GlobalNewableFunction : null;
            if (function is not null && await ObjectPropertyAsync(function, name, cancellation).ConfigureAwait(false) is { } member)
                return member;
            return await ObjectPropertyAsync(host.GlobalObject, name, cancellation).ConfigureAwait(false);
        }
        if (type is IntersectionType intersection)
        {
            var property = await CompositePropertyAsync(intersection, name, true, cancellation).ConfigureAwait(false);
            return property ?? (skipAugment
                ? null
                : await CompositePropertyAsync(intersection, name, false, cancellation).ConfigureAwait(false));
        }
        return type is UnionType union ? await CompositePropertyAsync(union, name, skipAugment, cancellation).ConfigureAwait(false) : null;
    }

    internal async ValueTask<Symbol?> ObjectPropertyAsync(Type type, Utf8String name, CancellationToken cancellation)
    {
        if (type is not ObjectType obj)
            return null;
        var property = (await host.ResolveAsync(obj, cancellation).ConfigureAwait(false)).Members?.GetValueOrDefault(name);
        return property is not null && await ValueAsync(property, false, cancellation).ConfigureAwait(false) ? property : null;
    }

    private async ValueTask<bool> ValueAsync(Symbol symbol, bool includeTypeOnly, CancellationToken cancellation)
        => (symbol.Flags & S.Value) != 0 || (symbol.Flags & S.Alias) != 0
            && (await aliases.FlagsAsync(
                symbol,
                excludeTypeOnly: !includeTypeOnly,
                cancellation: cancellation).ConfigureAwait(false) & S.Value) != 0;

    internal async ValueTask<Symbol?> CompositePropertyAsync(UnionOrIntersectionType type, Utf8String name, bool skipAugment = false,
        CancellationToken cancellation = default)
    {
        var property = await CachedPropertyAsync(type, name, skipAugment, cancellation).ConfigureAwait(false);
        return property is not null && (property.CheckFlags & C.ReadPartial) == 0 ? property : null;
    }

    internal async ValueTask<Symbol?> CachedPropertyAsync(UnionOrIntersectionType type, Utf8String name, bool skipAugment = false,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        var cache = skipAugment ? type.PropertyCacheWithoutFunctionAugment : type.PropertyCache;
        if (cache?.GetValueOrDefault(name) is { } cached)
            return cached;
        var property = await CreateAsync(type, name, skipAugment, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (property is not null)
        {
            cache = skipAugment ? type.PropertyCacheWithoutFunctionAugment ??= new(Utf8StringComparer.Ordinal)
                : type.PropertyCache ??= new(Utf8StringComparer.Ordinal);
            cache[name] = property;
            if (skipAugment && (property.CheckFlags & C.Partial) == 0)
                (type.PropertyCache ??= new(Utf8StringComparer.Ordinal)).TryAdd(name, property);
        }
        return property;
    }

    private async ValueTask<Symbol?> CreateAsync(
        UnionOrIntersectionType containingType,
        Utf8String name,
        bool skipAugment,
        CancellationToken cancellation)
    {
        S flags = 0;
        Symbol? single = null;
        List<Symbol>? properties = null;
        HashSet<Symbol>? propertySet = null;
        List<Type>? indexTypes = null;
        bool isUnion = containingType is UnionType, mergedInstantiations = false;
        C checks = isUnion ? 0 : C.Readonly;
        S optional = isUnion ? 0 : S.Optional;
        C synthetic = C.SyntheticMethod;
        foreach (var current in containingType.Types)
        {
            var type = await host.ApparentAsync(current, cancellation).ConfigureAwait(false);
            if (type == context.ErrorType || (type.Flags & F.Any) != 0 && type.Alias is not null || (type.Flags & F.Never) != 0)
                continue;
            var property = await PropertyAsync(type, name, skipAugment, cancellation: cancellation).ConfigureAwait(false);
            if (property is not null)
            {
                if ((property.Flags & S.ClassMember) != 0)
                    optional = isUnion ? optional | property.Flags & S.Optional : optional & property.Flags;
                if (single is null)
                {
                    single = property;
                    flags = (property.Flags & S.Accessor) != 0 ? property.Flags & S.Accessor : S.Property;
                }
                else if (property != single)
                {
                    if (Target(property) == Target(single)
                        && await SamePropertiesAsync(single, property, cancellation).ConfigureAwait(false))
                        mergedInstantiations = single.Parent is { } parent
                            && (parent.Flags & (S.Class | S.Interface | S.TypeAlias)) != 0
                            && scopes.Local(parent).Count != 0;
                    else
                    {
                        if (properties is null)
                        {
                            properties = [single];
                            propertySet = [single];
                        }
                        if (propertySet!.Add(property))
                            properties.Add(property);
                    }
                    if ((flags & S.Accessor) != 0 && (property.Flags & S.Accessor) != (flags & S.Accessor))
                        flags = flags & ~S.Accessor | S.Property;
                }
                if (isUnion && host.IsReadonly(property))
                    checks |= C.Readonly;
                else if (!isUnion && !host.IsReadonly(property))
                    checks &= ~C.Readonly;
                checks |= host.AccessFlags(property, false) | host.AccessFlags(property, true);
                if ((property.Flags & S.Method) == 0 && (property.CheckFlags & C.SyntheticMethod) == 0)
                    synthetic = C.SyntheticProperty;
            }
            else if (isUnion)
            {
                var index = name.Span.StartsWith(Symbol.InternalUnique, StringComparison.Ordinal) ? null
                    : await host.ApplicableIndexAsync(type, name, cancellation).ConfigureAwait(false);
                if (index is not null)
                {
                    flags = flags & ~S.Accessor | S.Property;
                    checks |= C.WritePartial | (index.IsReadonly ? C.Readonly : 0);
                    (indexTypes ??= []).Add(type is TypeReference { Target: TupleType } tuple
                        ? await host.TupleRestAsync(tuple, cancellation).ConfigureAwait(false) ?? context.UndefinedType : index.ValueType);
                }
                else if ((type.ObjectFlags & O.ObjectLiteral) != 0 && (type.ObjectFlags & O.ContainsSpread) == 0)
                {
                    checks |= C.WritePartial;
                    (indexTypes ??= []).Add(context.UndefinedType);
                }
                else
                    checks |= C.ReadPartial;
            }
        }
        if (single is null)
            return null;
        if (isUnion && (properties is not null || (checks & C.Partial) != 0)
            && (checks & (C.ContainsPrivate | C.ContainsProtected | C.ContainsWritePrivate | C.ContainsWriteProtected)) != 0
            && !(properties is not null && CommonDeclaration(properties)))
        {
            if ((checks & (C.ContainsPrivate | C.ContainsProtected)) != 0)
                return null;
            if ((checks & C.ContainsWritePrivate) != 0)
                checks &= ~(C.ContainsWritePublic | C.ContainsWriteProtected);
            else if ((checks & C.ContainsWriteProtected) != 0)
                checks &= ~C.ContainsWritePublic;
        }
        if (properties is null && (checks & C.ReadPartial) == 0 && indexTypes is null)
        {
            if (!mergedInstantiations)
                return single;
            var source = (single.Flags & S.Transient) != 0 ? links.Values.Get(single) : null;
            var clone = Clone(single, source?.ResolvedType);
            if (single.ValueDeclaration is { } declaration)
                clone.Parent = symbols.Binding(declaration)?.Get(declaration)?.Symbol?.Parent;
            var data = links.Values.Get(clone);
            data.ContainingType = containingType;
            data.Mapper = source?.Mapper;
            try
            {
                data.WriteType = await values.WriteAsync(single, cancellation).ConfigureAwait(false);
            }
            catch
            {
                links.Values.Remove(clone);
                throw;
            }
            return clone;
        }
        properties ??= [single];
        var declarations = new List<SyntaxNode>();
        var declarationSet = new HashSet<SyntaxNode>();
        Type? firstType = null, nameType = null;
        var types = new List<Type>();
        List<Type>? writes = null;
        SyntaxNode? firstDeclaration = null;
        bool nonUniform = false;
        foreach (var property in properties)
        {
            if (firstDeclaration is null)
                firstDeclaration = property.ValueDeclaration;
            else if (property.ValueDeclaration is not null && property.ValueDeclaration != firstDeclaration)
                nonUniform = true;
            foreach (var declaration in property.Declarations)
                if (declarationSet.Add(declaration))
                    declarations.Add(declaration);
            var type = await values.GetAsync(property, cancellation).ConfigureAwait(false);
            if (firstType is null)
            {
                firstType = type;
                nameType = links.Values.Get(property).NameType;
            }
            var write = await values.WriteAsync(property, cancellation).ConfigureAwait(false);
            if (writes is not null || write != type)
            {
                writes ??= [.. types];
                writes.Add(write);
            }
            if (type != firstType)
                checks |= C.HasNonUniformType;
            if (Literal(type) || TypeAlgebra.IsPatternLiteral(type))
                checks |= C.HasLiteralType;
            if ((type.Flags & F.Never) != 0 && type != context.UniqueLiteralType)
                checks |= C.HasNeverType;
            types.Add(type);
        }
        if (indexTypes is not null)
            types.AddRange(indexTypes);
        Type? resolved = null, resolvedWrite = null;
        if (types.Count <= 2)
        {
            resolved = isUnion ? await algebra.UnionAsync(types, cancellation: cancellation).ConfigureAwait(false)
                : await algebra.IntersectionAsync(types, cancellation: cancellation).ConfigureAwait(false);
            if (writes is not null)
                resolvedWrite = isUnion ? await algebra.UnionAsync(writes, cancellation: cancellation).ConfigureAwait(false)
                : await algebra.IntersectionAsync(writes, cancellation: cancellation).ConfigureAwait(false);
        }
        cancellation.ThrowIfCancellationRequested();
        var result = new Symbol(flags | optional | S.Transient, name) { CheckFlags = checks | synthetic };
        result.DeclarationList = result.DeclarationList.AddRange(declarations);
        if (!nonUniform && firstDeclaration is not null)
        {
            result.ValueDeclaration = firstDeclaration;
            result.Parent = symbols.Binding(firstDeclaration)?.Get(firstDeclaration)?.Symbol?.Parent;
        }
        var resultLinks = links.Values.Get(result);
        resultLinks.ContainingType = containingType;
        resultLinks.NameType = nameType;
        if (types.Count > 2)
        {
            result.CheckFlags |= C.DeferredType;
            var deferred = links.DeferredSymbols.Get(result);
            deferred.Parent = containingType;
            deferred.Constituents = types.AsReadOnly();
            deferred.WriteConstituents = writes?.AsReadOnly() ?? (IReadOnlyList<Type>)[];
        }
        else
        {
            resultLinks.ResolvedType = resolved;
            resultLinks.WriteType = resolvedWrite;
        }
        return result;
    }

    internal Symbol Clone(Symbol source, Type? type)
    {
        if (type is not null)
            context.RequireOwned(type);
        var result = new Symbol(source.Flags | S.Transient, source.Name)
        { CheckFlags = source.CheckFlags & C.Readonly, Parent = source.Parent, ValueDeclaration = source.ValueDeclaration };
        result.DeclarationList = source.Declarations;
        var data = links.Values.Get(result);
        data.ResolvedType = type;
        data.Target = source;
        data.NameType = links.Values.Get(source).NameType;
        return result;
    }

    private Symbol? Target(Symbol symbol) => (symbol.CheckFlags & C.Instantiated) != 0 ? links.Values.Get(symbol).Target : symbol;

    private async ValueTask<bool> SamePropertiesAsync(Symbol left, Symbol right, CancellationToken cancellation)
    {
        if (left == right)
            return true;
        var access = host.AccessFlags(left, false) & (C.ContainsPrivate | C.ContainsProtected);
        if (access != (host.AccessFlags(right, false) & (C.ContainsPrivate | C.ContainsProtected)))
            return false;
        if (access != 0 ? Target(left) != Target(right) : (left.Flags & S.Optional) != (right.Flags & S.Optional))
            return false;
        if (host.IsReadonly(left) != host.IsReadonly(right))
            return false;
        return values.NonMissing(await values.GetAsync(left, cancellation).ConfigureAwait(false), (left.Flags & S.Optional) != 0)
            == values.NonMissing(await values.GetAsync(right, cancellation).ConfigureAwait(false), (right.Flags & S.Optional) != 0);
    }

    private static bool CommonDeclaration(IReadOnlyList<Symbol> symbols)
    {
        var common = new HashSet<SyntaxNode>(symbols[0].Declarations);
        foreach (var symbol in symbols.Skip(1))
        {
            common.IntersectWith(symbol.Declarations);
            if (common.Count == 0)
                return false;
        }
        return common.Count != 0;
    }

    private static bool Literal(Type type) => (type.Flags & F.Boolean) != 0
        || (type is UnionType union
            ? (type.Flags & F.EnumLiteral) != 0 || union.Types.All(t => (t.Flags & F.Unit) != 0)
            : (type.Flags & F.Unit) != 0);
}

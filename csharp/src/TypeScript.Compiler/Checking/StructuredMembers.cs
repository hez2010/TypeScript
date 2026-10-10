using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using S = TypeScript.Compiler.Binding.SymbolFlags;
using O = TypeScript.Compiler.Checking.ObjectFlags;

namespace TypeScript.Compiler.Checking;

internal interface IStructuredMemberHost
{
    ValueTask<IReadOnlyDictionary<Utf8String, Symbol>> MembersAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<IReadOnlyDictionary<Utf8String, Symbol>> ExportsAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexInfosAsync(Symbol indexSymbol, IReadOnlyList<Symbol> siblings, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Type>> BaseTypesAsync(InterfaceType type, CancellationToken cancellation);

    ValueTask<Type> WithThisAsync(Type type, Type argument, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Symbol>> PropertiesAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    ValueTask<Type> BaseConstructorAsync(InterfaceType type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Signature>> DefaultConstructorsAsync(InterfaceType type, CancellationToken cancellation);

    ValueTask<Type> SymbolTypeAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<Type> DeclaredTypeAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask ResolveUnionAsync(UnionType type, CancellationToken cancellation);

    ValueTask ResolveIntersectionAsync(IntersectionType type, CancellationToken cancellation);

    ValueTask ResolveReverseMappedAsync(ReverseMappedType type, CancellationToken cancellation);
}

internal sealed class StructuredMembers(TypeContext context, CheckerSymbols symbols, TypeParameterScopes scopes,
    TypeReferences references, TypeInstantiation instantiation, Signatures signatures, AliasResolver aliases,
    MappedMembers mapped, TypeOrder order, IStructuredMemberHost host)
{
    private readonly IndexInfo anyBaseIndex = context.NewIndexInfo(context.StringType, context.AnyType);
    internal IndexInfo AnyBaseIndex => anyBaseIndex;
    private readonly IndexInfo enumIndex = context.NewIndexInfo(context.NumberType, context.StringType, true);
    internal IndexInfo EnumNumberIndex => enumIndex;

    internal async ValueTask<StructuredType> ResolveAsync(StructuredType type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.ObjectFlags & O.MembersResolved) != 0)
            return type;
        var oldMembers = type.Members;
        var oldProperties = type.Properties;
        var oldCalls = type.CallSignatures;
        var oldConstructors = type.ConstructSignatures;
        var oldIndexes = type.IndexInfos;
        var oldFlags = type.ObjectFlags & (O.MembersResolved | O.UnresolvedMembers);
        Diagnostics.CompilationCapture.ProbeMark allocMark = Diagnostics.CompilationCapture.Mark();
        try
        {
            switch (type)
            {
                case TypeReference reference when (type.ObjectFlags & O.Reference) != 0:
                    var source = (InterfaceType)reference.ReferencedType;
                    var arguments = await references.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false);
                    await ObjectAsync(reference, source, source.AllTypeParameters,
                        arguments.Count == source.AllTypeParameters.Count - 1 ? [.. arguments, reference] : arguments,
                        cancellation).ConfigureAwait(false);
                    break;
                case InterfaceType intf:
                    await ObjectAsync(intf, intf, [], [], cancellation).ConfigureAwait(false);
                    break;
                case ReverseMappedType reverse:
                    await host.ResolveReverseMappedAsync(reverse, cancellation).ConfigureAwait(false);
                    break;
                case MappedType mappedType:
                    await mapped.ResolveAsync(mappedType, cancellation).ConfigureAwait(false);
                    break;
                case ObjectType anonymous:
                    await AnonymousAsync(anonymous, cancellation).ConfigureAwait(false);
                    break;
                case UnionType union:
                    await host.ResolveUnionAsync(union, cancellation).ConfigureAwait(false);
                    break;
                case IntersectionType intersection:
                    await host.ResolveIntersectionAsync(intersection, cancellation).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException("Unknown structured type");
            }
            cancellation.ThrowIfCancellationRequested();
            if ((type.ObjectFlags & O.MembersResolved) == 0)
                throw new InvalidOperationException("Member resolver did not resolve the type");
            return type;
        }
        catch
        {
            type.Members = oldMembers;
            type.Properties = oldProperties;
            type.CallSignatures = oldCalls;
            type.ConstructSignatures = oldConstructors;
            type.IndexInfos = oldIndexes;
            type.ObjectFlags = type.ObjectFlags & ~(O.MembersResolved | O.UnresolvedMembers) | oldFlags;
            throw;
        }
        finally
        {
            Diagnostics.CompilationCapture.Report(Diagnostics.AllocationProbes.ResolveMembers, allocMark);
        }
    }

    internal async ValueTask ResolveDeclaredAsync(InterfaceType type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type.DeclaredMembersResolved)
            return;
        var members = await host.MembersAsync(type.Symbol!, cancellation).ConfigureAwait(false);
        var oldMembers = type.DeclaredMembers;
        var oldCalls = type.DeclaredCallSignatures;
        var oldConstructs = type.DeclaredConstructSignatures;
        var oldIndexes = type.DeclaredIndexInfos;
        type.DeclaredMembersResolved = true;
        type.DeclaredMembers = members;
        try
        {
            type.DeclaredCallSignatures = await signatures.OfSymbolAsync(
                members.GetValueOrDefault(Symbol.InternalCall),
                cancellation).ConfigureAwait(false);
            type.DeclaredConstructSignatures = await signatures.OfSymbolAsync(
                members.GetValueOrDefault(Symbol.InternalNew),
                cancellation).ConfigureAwait(false);
            type.DeclaredIndexInfos = members.TryGetValue(Symbol.InternalIndex, out var index)
                ? await host.IndexInfosAsync(index, members.Values.ToArray(), cancellation).ConfigureAwait(false) : [];
            cancellation.ThrowIfCancellationRequested();
        }
        catch
        {
            type.DeclaredMembersResolved = false;
            type.DeclaredMembers = oldMembers;
            type.DeclaredCallSignatures = oldCalls;
            type.DeclaredConstructSignatures = oldConstructs;
            type.DeclaredIndexInfos = oldIndexes;
            throw;
        }
    }

    private async ValueTask ObjectAsync(ObjectType type, InterfaceType source, IReadOnlyList<Type> parameters,
        IReadOnlyList<Type> arguments, CancellationToken cancellation)
    {
        await ResolveDeclaredAsync(source, cancellation).ConfigureAwait(false);
        bool instantiated = !parameters.SequenceEqual(arguments);
        TypeMapper? mapper = instantiated ? TypeMapper.Create(parameters.ToArray(), arguments.ToArray()) : null;
        IReadOnlyDictionary<Utf8String, Symbol>? members = source.DeclaredMembers;
        IReadOnlyList<Signature> calls = source.DeclaredCallSignatures ?? [], constructors = source.DeclaredConstructSignatures ?? [];
        IReadOnlyList<IndexInfo> indexes = source.DeclaredIndexInfos ?? [];
        if (mapper is not null)
        {
            members = await InstantiateTableAsync(source.DeclaredMembers, mapper, cancellation).ConfigureAwait(false);
            calls = await InstantiateSignaturesAsync(calls, mapper, cancellation).ConfigureAwait(false);
            constructors = await InstantiateSignaturesAsync(constructors, mapper, cancellation).ConfigureAwait(false);
            indexes = await InstantiateIndexesAsync(indexes, mapper, cancellation).ConfigureAwait(false);
        }
        var bases = await host.BaseTypesAsync(source, cancellation).ConfigureAwait(false);
        if (bases.Count != 0)
        {
            var ownedMembers = members is null
                ? new Dictionary<Utf8String, Symbol>()
                : new(members, Utf8StringComparer.Ordinal);
            members = ownedMembers.AsReadOnly();
            await SetAsync(type, members, calls, constructors, indexes, cancellation).ConfigureAwait(false);
            type.ObjectFlags |= O.UnresolvedMembers;
            foreach (var baseType in bases)
            {
                var instantiatedBase = baseType;
                if (arguments.Count != 0)
                {
                    instantiatedBase = await instantiation.InstantiateAsync(
                        baseType,
                        mapper,
                        cancellation: cancellation).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Base instantiation returned no type");
                    instantiatedBase = await host.WithThisAsync(instantiatedBase, arguments[^1], cancellation).ConfigureAwait(false);
                }
                Inherit(ownedMembers, await host.PropertiesAsync(instantiatedBase, cancellation).ConfigureAwait(false));
                calls = Concatenate(calls, await host.SignaturesAsync(instantiatedBase, false, cancellation).ConfigureAwait(false));
                constructors = Concatenate(
                    constructors,
                    await host.SignaturesAsync(instantiatedBase, true, cancellation).ConfigureAwait(false));
                var inheritedIndexes = instantiatedBase == context.AnyType ? [anyBaseIndex]
                    : await host.IndexesAsync(instantiatedBase, cancellation).ConfigureAwait(false);
                indexes = Array.AsReadOnly<IndexInfo>(
                    [.. indexes, .. inheritedIndexes.Where(next => !indexes.Any(old => old.KeyType == next.KeyType))]);
            }
            type.ObjectFlags &= ~O.UnresolvedMembers;
        }
        await SetAsync(type, members, calls, constructors, indexes, cancellation).ConfigureAwait(false);
    }

    private async ValueTask AnonymousAsync(ObjectType type, CancellationToken cancellation)
    {
        if (type.Target is { } target)
        {
            await SetAsync(type, null, [], [], [], cancellation).ConfigureAwait(false);
            var members = new Dictionary<Utf8String, Symbol>();
            foreach (var property in await host.PropertiesAsync(target, cancellation).ConfigureAwait(false))
                members[property.Name] = (await instantiation.SymbolAsync(property, type.Mapper!, cancellation).ConfigureAwait(false))!;
            await SetAsync(type, members.AsReadOnly(),
                await InstantiateSignaturesAsync(
                    await host.SignaturesAsync(target, false, cancellation).ConfigureAwait(false),
                    type.Mapper!,
                    cancellation).ConfigureAwait(false),
                await InstantiateSignaturesAsync(
                    await host.SignaturesAsync(target, true, cancellation).ConfigureAwait(false),
                    type.Mapper!,
                    cancellation).ConfigureAwait(false),
                await InstantiateIndexesAsync(
                    await host.IndexesAsync(target, cancellation).ConfigureAwait(false),
                    type.Mapper!,
                    cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
            return;
        }
        var symbol = symbols.Merger.GetMergedSymbol(type.Symbol) ?? throw new InvalidOperationException("Anonymous type has no symbol");
        if ((symbol.Flags & S.TypeLiteral) != 0)
        {
            await SetAsync(type, null, [], [], [], cancellation).ConfigureAwait(false);
            var members = await host.MembersAsync(symbol, cancellation).ConfigureAwait(false);
            await SetAsync(type, members,
                await signatures.OfSymbolAsync(
                    members.GetValueOrDefault(Symbol.InternalCall),
                    cancellation).ConfigureAwait(false),
                await signatures.OfSymbolAsync(
                    members.GetValueOrDefault(Symbol.InternalNew),
                    cancellation).ConfigureAwait(false),
                members.TryGetValue(Symbol.InternalIndex, out var index)
                    ? await host.IndexInfosAsync(index, members.Values.ToArray(), cancellation).ConfigureAwait(false) : [],
                cancellation).ConfigureAwait(false);
            return;
        }
        IReadOnlyDictionary<Utf8String, Symbol> exports = await host.ExportsAsync(symbol, cancellation).ConfigureAwait(false);
        if (symbol == symbols.GlobalThisSymbol)
            exports = exports.Where(p => (p.Value.Flags & S.BlockScoped) == 0
                && !((p.Value.Flags & S.ValueModule) != 0 && p.Value.Declarations.Length != 0
                    && p.Value.Declarations.All(
                        d => d is ModuleDeclarationNode { Name: StringLiteralNode }
                            or ModuleDeclarationNode { Keyword: SyntaxKind.GlobalKeyword })))
                .ToDictionary(p => p.Key, p => p.Value, Utf8StringComparer.Ordinal).AsReadOnly();
        await SetAsync(type, exports, [], [], [], cancellation).ConfigureAwait(false);
        bool anyBase = false;
        InterfaceType? classType = null;
        if ((symbol.Flags & S.Class) != 0)
        {
            classType = await scopes.ClassOrInterfaceAsync(symbol, cancellation).ConfigureAwait(false);
            var baseConstructor = await host.BaseConstructorAsync(classType, cancellation).ConfigureAwait(false);
            if ((baseConstructor.Flags & (TypeFlags.Object | TypeFlags.Intersection | TypeFlags.TypeVariable)) != 0)
            {
                var inherited = new Dictionary<Utf8String, Symbol>(exports, Utf8StringComparer.Ordinal);
                Inherit(inherited, await host.PropertiesAsync(baseConstructor, cancellation).ConfigureAwait(false));
                exports = inherited.AsReadOnly();
                await SetAsync(type, exports, [], [], [], cancellation).ConfigureAwait(false);
            }
            else
                anyBase = baseConstructor == context.AnyType;
        }
        var indexInfos = new List<IndexInfo>();
        if (exports.TryGetValue(Symbol.InternalIndex, out var indexSymbol))
            indexInfos.AddRange(await host.IndexInfosAsync(indexSymbol, exports.Values.ToArray(), cancellation).ConfigureAwait(false));
        else
        {
            if (anyBase)
                indexInfos.Add(anyBaseIndex);
            if ((symbol.Flags & S.Enum) != 0)
            {
                bool numeric = (await host.DeclaredTypeAsync(symbol, cancellation).ConfigureAwait(false)).Flags.HasFlag(TypeFlags.Enum);
                if (!numeric)
                    foreach (var property in type.Properties!)
                        if (((await host.SymbolTypeAsync(property, cancellation).ConfigureAwait(false)).Flags & TypeFlags.NumberLike) != 0)
                        {
                            numeric = true;
                            break;
                        }
                if (numeric)
                    indexInfos.Add(enumIndex);
            }
        }
        type.IndexInfos = indexInfos.AsReadOnly();
        if ((symbol.Flags & (S.Function | S.Method)) != 0)
            type.CallSignatures = await signatures.OfSymbolAsync(symbol, cancellation).ConfigureAwait(false);
        if (classType is not null)
        {
            var constructors = await signatures.OfSymbolAsync(
                symbol.Members.GetValueOrDefault(Symbol.InternalConstructor),
                cancellation).ConfigureAwait(false);
            type.ConstructSignatures = constructors.Count != 0
                ? constructors
                : await host.DefaultConstructorsAsync(classType, cancellation).ConfigureAwait(false);
        }
    }

    internal async ValueTask SetAsync(StructuredType type, IReadOnlyDictionary<Utf8String, Symbol>? members, IReadOnlyList<Signature> calls,
        IReadOnlyList<Signature> constructors, IReadOnlyList<IndexInfo> indexes, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Diagnostics.CompilationCapture.ProbeMark allocMark = Diagnostics.CompilationCapture.Mark();
        try
        {
            await SetCoreAsync(type, members, calls, constructors, indexes, cancellation).ConfigureAwait(false);
        }
        finally
        {
            Diagnostics.CompilationCapture.Report(Diagnostics.AllocationProbes.SetMembers, allocMark);
        }
    }

    private async ValueTask SetCoreAsync(StructuredType type, IReadOnlyDictionary<Utf8String, Symbol>? members, IReadOnlyList<Signature> calls,
        IReadOnlyList<Signature> constructors, IReadOnlyList<IndexInfo> indexes, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        // Alias resolution can re-enter member lookup for a sibling export.
        // ResolveAsync restores this provisional state if resolution fails.
        type.Members = members;
        type.ObjectFlags |= O.MembersResolved;
        var declared = new List<Symbol>();
        var inherited = new List<Symbol>();
        if (members is not null)
            foreach (var (name, symbol) in members)
            {
                if (!await NamedAsync(name, symbol, cancellation).ConfigureAwait(false))
                    continue;
                if (type.Symbol is { } owner && (owner.Flags & (S.Class | S.Interface)) != 0 && symbol.ValueDeclaration is { } value
                    && owner.Declarations.Any(d => value.Pos >= d.Pos && value.End <= d.End))
                    declared.Add(symbol);
                else
                    inherited.Add(symbol);
            }
        // TypeOrder implements IComparer<Symbol>: sorting through the interface avoids the delegate
        // (and the Comparer wrapper the delegate overload builds) that List.Sort(method group) would
        // allocate on every member table.
        declared.Sort(order);
        inherited.Sort(order);
        cancellation.ThrowIfCancellationRequested();
        type.Properties = Array.AsReadOnly<Symbol>([.. declared, .. inherited]);
        type.CallSignatures = calls;
        type.ConstructSignatures = constructors;
        type.IndexInfos = indexes;
    }

    private async ValueTask<IReadOnlyDictionary<Utf8String, Symbol>?> InstantiateTableAsync(
        IReadOnlyDictionary<Utf8String, Symbol>? table,
        TypeMapper mapper,
        CancellationToken cancellation)
    {
        if (table is null)
            return null;
        var result = new Dictionary<Utf8String, Symbol>();
        foreach (var (name, symbol) in table)
            if (await NamedAsync(name, symbol, cancellation).ConfigureAwait(false))
                result[name] = (await instantiation.SymbolAsync(symbol, mapper, cancellation).ConfigureAwait(false))!;
        return result.AsReadOnly();
    }

    private async ValueTask<IReadOnlyList<Signature>> InstantiateSignaturesAsync(
        IReadOnlyList<Signature> source,
        TypeMapper mapper,
        CancellationToken cancellation)
    {
        var result = new Signature[source.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = await instantiation.SignatureAsync(
                source[i],
                mapper,
                mapper == instantiation.PermissiveMapper,
                cancellation).ConfigureAwait(false);
        return Array.AsReadOnly(result);
    }

    private async ValueTask<IReadOnlyList<IndexInfo>> InstantiateIndexesAsync(
        IReadOnlyList<IndexInfo> source,
        TypeMapper mapper,
        CancellationToken cancellation)
    {
        var result = new IndexInfo[source.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = await instantiation.IndexInfoAsync(source[i], mapper, cancellation).ConfigureAwait(false);
        return Array.AsReadOnly(result);
    }

    internal async ValueTask<bool> NamedAsync(Utf8String name, Symbol symbol, CancellationToken cancellation)
    {
        if (name.Span.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal)
            && name.Length >= 2 && name[1] is not ((byte)'@' or (byte)'#'))
            return false;
        return (symbol.Flags & S.Value) != 0 || (symbol.Flags & S.Alias) != 0
            && (await aliases.FlagsAsync(symbol, excludeTypeOnly: true, cancellation: cancellation).ConfigureAwait(false) & S.Value) != 0;
    }

    private static void Inherit(Dictionary<Utf8String, Symbol> members, IReadOnlyList<Symbol> source)
    {
        foreach (var symbol in source)
            if (!(symbol.ValueDeclaration is { } declaration
                && SemanticSyntax.IsStatic(declaration)
                && (declaration as INamedNode)?.Name is PrivateIdentifierNode)
                && (!members.TryGetValue(symbol.Name, out var current) || (current.Flags & S.Value) == 0))
                members[symbol.Name] = symbol;
    }

    private static IReadOnlyList<T> Concatenate<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
        => right.Count == 0 ? left : left.Count == 0 ? right : Array.AsReadOnly<T>([.. left, .. right]);
}

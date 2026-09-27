using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface ITypeWideningHost
{
    bool IsArray(Type type);

    ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Symbol>> PropertiesAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Symbol>> ObjectPropertiesAsync(Type type, CancellationToken cancellation);

    ValueTask<Symbol?> ObjectPropertyAsync(Type type, TextSlice name, CancellationToken cancellation);

    ValueTask<Type> SymbolTypeAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    ValueTask<Type> EnumBaseAsync(Type type, CancellationToken cancellation);
}

internal sealed class TypeWidening(TypeContext context, TypeAlgebra algebra, TypeViews views, CheckerLinks links, ITypeWideningHost host)
{
    private readonly Dictionary<Type, Type> widened = [];
    private readonly Dictionary<Type, Type> literalBases = [];
    private readonly Dictionary<TextSlice, Symbol> undefinedProperties = [];

    private sealed class WideningContext(WideningContext? parent, TextSlice? name, IReadOnlyList<Type>? siblings)
    {
        internal WideningContext? Parent { get; } = parent;
        internal TextSlice? Name { get; } = name;
        internal IReadOnlyList<Type>? Siblings { get; set; } = siblings;
        internal IReadOnlyList<Symbol>? Properties { get; set; }
        internal Dictionary<Type, Type> Types { get; } = [];
        internal Dictionary<TextSlice, WideningContext> Children { get; } = [];

        internal WideningContext Child(TextSlice name)
        {
            if (!Children.TryGetValue(name, out var child))
                Children[name] = child = new(this, name, null);
            return child;
        }
    }

    internal ValueTask<Type> GetAsync(Type type, CancellationToken cancellation = default) => GetAsync(type, null, cancellation);

    private async ValueTask<Type> GetAsync(Type type, WideningContext? widening, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.ObjectFlags & ObjectFlags.RequiresWidening) == 0)
            return type;
        if (widening is null && widened.TryGetValue(type, out var cached))
            return cached;
        Type? result = null;
        if ((type.Flags & (TypeFlags.Any | TypeFlags.Nullable)) != 0)
            result = context.AnyType;
        else if ((type.ObjectFlags & ObjectFlags.ObjectLiteral) != 0)
            result = await ObjectAsync(type, widening, cancellation).ConfigureAwait(false);
        else if (type is UnionType union)
        {
            var unionContext = widening ?? new WideningContext(null, null, union.Types);
            var parts = new Type[union.Types.Count];
            for (int i = 0; i < parts.Length; i++)
                parts[i] = (union.Types[i].Flags & TypeFlags.Nullable) != 0 ? union.Types[i]
                    : await GetAsync(union.Types[i], unionContext, cancellation).ConfigureAwait(false);
            bool empty = false;
            foreach (var part in parts)
                if (await views.EmptyObjectAsync(part, cancellation).ConfigureAwait(false))
                {
                    empty = true;
                    break;
                }
            result = await algebra.UnionAsync(
                parts,
                empty ? UnionReduction.Subtype : UnionReduction.Literal,
                cancellation: cancellation).ConfigureAwait(false);
        }
        else if (type is IntersectionType intersection)
        {
            var parts = new Type[intersection.Types.Count];
            for (int i = 0; i < parts.Length; i++)
                parts[i] = await GetAsync(intersection.Types[i], cancellation).ConfigureAwait(false);
            result = await algebra.IntersectionAsync(parts, cancellation: cancellation).ConfigureAwait(false);
        }
        else if (host.IsArray(type) || type is TypeReference { Target: TupleType })
        {
            var reference = (TypeReference)type;
            var args = (await host.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false)).ToArray();
            for (int i = 0; i < args.Length; i++)
                args[i] = await GetAsync(args[i], cancellation).ConfigureAwait(false);
            result = context.CreateTypeReference((InterfaceType)reference.Target!, args);
        }
        cancellation.ThrowIfCancellationRequested();
        if (result is not null && widening is null)
            widened[type] = result;
        return result ?? type;
    }

    private async ValueTask<Type> ObjectAsync(Type type, WideningContext? widening, CancellationToken cancellation)
    {
        if (widening?.Types.TryGetValue(type, out var cached) == true)
            return cached;
        var members = new Dictionary<TextSlice, Symbol>();
        foreach (var property in await host.ObjectPropertiesAsync(type, cancellation).ConfigureAwait(false))
        {
            var next = property;
            if ((property.Flags & SymbolFlags.Property) != 0)
            {
                var original = await host.SymbolTypeAsync(property, cancellation).ConfigureAwait(false);
                var value = await GetAsync(original, widening?.Child(property.Name), cancellation).ConfigureAwait(false);
                if (value != original)
                    next = WithType(property, value);
            }
            members[property.Name] = next;
        }
        if (widening is not null)
            foreach (var property in await ContextPropertiesAsync(widening, cancellation).ConfigureAwait(false))
                if (!members.ContainsKey(property.Name))
                {
                    if (!undefinedProperties.TryGetValue(property.Name, out var undefined))
                    {
                        undefined = WithType(property, context.UndefinedOrMissingType);
                        undefined.Flags |= SymbolFlags.Optional;
                        undefinedProperties[property.Name] = undefined;
                    }
                    members[property.Name] = undefined;
                }
        var indexes = new List<IndexInfo>();
        foreach (var index in await host.IndexesAsync(type, cancellation).ConfigureAwait(false))
            indexes.Add(context.NewIndexInfo(index.KeyType, await GetAsync(index.ValueType, cancellation).ConfigureAwait(false),
                index.IsReadonly, index.Declaration, index.Components.ToArray()));
        var result = context.NewObjectType(ObjectFlags.Anonymous, type.Symbol);
        result.Members = members.AsReadOnly();
        result.Properties = members.Values.Order(algebra.Order).ToArray();
        result.CallSignatures = [];
        result.ConstructSignatures = [];
        result.IndexInfos = indexes.AsReadOnly();
        result.ObjectFlags |= ObjectFlags.MembersResolved | type.ObjectFlags & (ObjectFlags.JSLiteral | ObjectFlags.NonInferrableType);
        cancellation.ThrowIfCancellationRequested();
        if (widening?.Parent is not null)
            widening.Types[type] = result;
        return result;
    }

    private async ValueTask<IReadOnlyList<Symbol>> ContextPropertiesAsync(WideningContext widening, CancellationToken cancellation)
    {
        if (widening.Properties is { } cached)
            return cached;
        var properties = new Dictionary<TextSlice, Symbol>();
        foreach (var type in await SiblingsAsync(widening, cancellation).ConfigureAwait(false))
            if ((type.ObjectFlags & (ObjectFlags.ObjectLiteral | ObjectFlags.ContainsSpread)) == ObjectFlags.ObjectLiteral)
                foreach (var property in await host.PropertiesAsync(type, cancellation).ConfigureAwait(false))
                    properties[property.Name] = property;
        cancellation.ThrowIfCancellationRequested();
        return widening.Properties = properties.Values.ToArray();
    }

    private async ValueTask<IReadOnlyList<Type>> SiblingsAsync(WideningContext widening, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (widening.Siblings is { } cached)
            return cached;
        var siblings = new List<Type>();
        foreach (var type in await SiblingsAsync(widening.Parent!, cancellation).ConfigureAwait(false))
            if ((type.ObjectFlags & ObjectFlags.ObjectLiteral) != 0
                && await host.ObjectPropertyAsync(type, widening.Name!.Value, cancellation).ConfigureAwait(false) is { } property)
            {
                var value = await host.SymbolTypeAsync(property, cancellation).ConfigureAwait(false);
                siblings.AddRange(value is UnionType union ? union.Types : [value]);
            }
        cancellation.ThrowIfCancellationRequested();
        return widening.Siblings = siblings.AsReadOnly();
    }

    internal Symbol WithType(Symbol source, Type? type)
    {
        if (type is not null)
            context.RequireOwned(type);
        var result = new Symbol(source.Flags | SymbolFlags.Transient, source.Name)
        { CheckFlags = source.CheckFlags & CheckFlags.Readonly, Parent = source.Parent, ValueDeclaration = source.ValueDeclaration };
        result.DeclarationList = result.DeclarationList.AddRange(source.Declarations);
        var data = links.Values.Get(result);
        data.ResolvedType = type;
        data.Target = source;
        data.NameType = links.Values.Get(source).NameType;
        return result;
    }

    internal async ValueTask<Type> LiteralBaseAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.Flags & TypeFlags.EnumLike) != 0)
            return await host.EnumBaseAsync(type, cancellation).ConfigureAwait(false);
        if ((type.Flags & (TypeFlags.StringLiteral | TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0)
            return context.StringType;
        if ((type.Flags & TypeFlags.NumberLiteral) != 0)
            return context.NumberType;
        if ((type.Flags & TypeFlags.BigIntLiteral) != 0)
            return context.BigIntType;
        if ((type.Flags & TypeFlags.BooleanLiteral) != 0)
            return context.BooleanType;
        if (type is not UnionType union)
            return type;
        if (literalBases.TryGetValue(type, out var cached))
            return cached;
        var parts = new Type[union.Types.Count];
        for (int i = 0; i < parts.Length; i++)
            parts[i] = await LiteralBaseAsync(union.Types[i], cancellation).ConfigureAwait(false);
        var result = await algebra.UnionAsync(parts, cancellation: cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return literalBases[type] = result;
    }

    internal async ValueTask<Type> LiteralAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type is LiteralType literal && literal.IsFreshLiteral && (type.Flags & (TypeFlags.EnumLike | TypeFlags.Literal)) != 0)
            return await LiteralBaseAsync(type, cancellation).ConfigureAwait(false);
        return type is UnionType
            ? await algebra.MapAsync(
                type,
                async t => await LiteralAsync(t, cancellation).ConfigureAwait(false),
                cancellation: cancellation).ConfigureAwait(false) ?? context.NeverType
            : type;
    }
}

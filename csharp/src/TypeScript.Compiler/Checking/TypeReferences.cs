using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using S = TypeScript.Compiler.Binding.SymbolFlags;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal interface ITypeReferenceHost
{
    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    Symbol? AliasSymbol(SyntaxNode node);

    ValueTask<Type?> IntendedJsDocTypeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> ConstAssertionAsync(SyntaxNode expression, CancellationToken cancellation);

    ValueTask<bool> IdenticalAsync(Type first, Type second, CancellationToken cancellation);

    void TypeArgumentCount(SyntaxNode node, Symbol symbol, Type type, int minimum, int maximum, bool missingAugments);

    void NotGeneric(SyntaxNode node, Symbol symbol);

    void CircularArguments(SyntaxNode? node, InterfaceType target);
}

internal sealed class TypeReferences(TypeContext context, CheckerLinks links, CheckerSymbols symbols, TypeParameterScopes scopes,
    DeclaredTypes declared, EntityNames names, AliasResolver aliases, TypeAlgebra algebra, TypeConstraints constraints,
    TypeInstantiation instantiation, ObjectInstantiation objects, TypeResolutionStack resolutions, ITypeReferenceHost host)
{
    private readonly Dictionary<string, Symbol> unresolved = new(StringComparer.Ordinal);
    private readonly Dictionary<TypeCacheKey, Type> errors = [];

    internal async ValueTask<Type> FromNodeAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (node is TypeReferenceNode { TypeName: IdentifierNode { Text: "const" }, TypeArguments: null or { Count: 0 } }
            && node.Parent is AsExpressionNode or TypeAssertionNode)
        {
            var expression = node.Parent is AsExpressionNode assertion ? assertion.Expression : ((TypeAssertionNode)node.Parent).Expression;
            return await host.ConstAssertionAsync(expression!, cancellation).ConfigureAwait(false);
        }
        if (await host.IntendedJsDocTypeAsync(node, cancellation).ConfigureAwait(false) is { } intended)
            return intended;
        var symbol = await SymbolAsync(node, cancellation).ConfigureAwait(false);
        var type = await ReferenceAsync(node, symbol, cancellation).ConfigureAwait(false);
        if (type is TypeParameter { IsDistributed: false } parameter)
            for (var parent = node.Parent; parent is not null && !TypeNodeFlow.Statement(parent); parent = parent.Parent)
            {
                cancellation.ThrowIfCancellationRequested();
                if (parent is ConditionalTypeNode { CheckType: TypeReferenceNode { TypeName: IdentifierNode, TypeArguments: null } check }
                    && await SymbolAsync(check, cancellation).ConfigureAwait(false) == parameter.Symbol)
                {
                    if (parameter.DistributedType is null)
                        parameter.DistributedType = new(context, parameter.Symbol) { IsDistributed = true, Constraint = parameter };
                    return parameter.DistributedType;
                }
            }
        return type;
    }

    internal async ValueTask<Symbol> SymbolAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        var data = links.SymbolNodes.Get(node);
        return data.ResolvedSymbol ??= await ResolveNameAsync(node, S.Type, false, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Symbol> ResolveNameAsync(SyntaxNode node, S meaning, bool ignoreErrors, CancellationToken cancellation)
    {
        var name = Name(node);
        if (name is null)
            return symbols.UnknownSymbol;
        var symbol = await names.ResolveAsync(name, meaning, ignoreErrors, cancellation: cancellation).ConfigureAwait(false);
        if (symbol is not null && symbol != symbols.UnknownSymbol)
            return symbol;
        return ignoreErrors ? symbols.UnknownSymbol : Unresolved(name);
    }

    private Symbol Unresolved(SyntaxNode name)
    {
        var segments = new Stack<string>();
        while (true)
        {
            if (name is QualifiedNameNode qualified)
            {
                segments.Push(((IdentifierNode)qualified.Right!).Text);
                name = qualified.Left!;
            }
            else if (name is PropertyAccessExpressionNode access)
            {
                segments.Push(((IdentifierNode)access.Name!).Text);
                name = access.Expression!;
            }
            else
            {
                segments.Push(((IdentifierNode)name).Text);
                break;
            }
        }
        Symbol? parent = null;
        string path = "";
        while (segments.TryPop(out string? text))
        {
            if (text.Length == 0)
            {
                parent = symbols.UnknownSymbol;
                path = parent.Name;
                continue;
            }
            path = parent is null ? text : path + "." + text;
            if (!unresolved.TryGetValue(path, out var symbol))
            {
                symbol = new(S.TypeAlias | S.Transient, text) { Parent = parent, CheckFlags = CheckFlags.Unresolved };
                unresolved.Add(path, symbol);
                links.TypeAliases.Get(symbol).DeclaredType = context.UnresolvedType;
            }
            parent = symbol;
        }
        return parent ?? symbols.UnknownSymbol;
    }

    private async ValueTask<Type> ReferenceAsync(SyntaxNode node, Symbol symbol, CancellationToken cancellation)
    {
        if (symbol == symbols.UnknownSymbol)
            return context.ErrorType;
        if ((symbol.Flags & (S.Class | S.Interface)) != 0)
            return await ClassReferenceAsync(node, symbol, cancellation).ConfigureAwait(false);
        if ((symbol.Flags & S.TypeAlias) != 0)
            return await AliasReferenceAsync(node, symbol, cancellation).ConfigureAwait(false);
        var result = await declared.TryGetAsync(symbol, cancellation).ConfigureAwait(false);
        return result is not null && NoArguments(node, symbol)
            ? await algebra.RegularTypeAsync(result, cancellation).ConfigureAwait(false) : context.ErrorType;
    }

    private async ValueTask<Type> ClassReferenceAsync(SyntaxNode node, Symbol symbol, CancellationToken cancellation)
    {
        var type = await scopes.ClassOrInterfaceAsync(symbols.Merger.GetMergedSymbol(symbol)!, cancellation).ConfigureAwait(false);
        var parameters = type.AllTypeParameters.Skip(type.OuterTypeParameterCount)
            .Take(
                type.AllTypeParameters.Count - type.OuterTypeParameterCount - (type.ThisType is null
                    ? 0
                    : 1)).Cast<TypeParameter>().ToArray();
        if (parameters.Length == 0)
            return NoArguments(node, symbol) ? type : context.ErrorType;
        int count = Arguments(node)?.Count ?? 0, minimum = Minimum(parameters);
        bool js = IsJs(node);
        bool implicitAny = js && !symbols.Program.Configuration.Options.StrictOption("noImplicitAny");
        if (!implicitAny && (count < minimum || count > parameters.Length))
        {
            host.TypeArgumentCount(node, symbol, type, minimum, parameters.Length,
                js && node is ExpressionWithTypeArgumentsNode && node.Parent?.Kind != K.JSDocAugmentsTag);
            if (!js)
                return context.ErrorType;
        }
        if (node is TypeReferenceNode && await DeferredAsync(node, count != parameters.Length, cancellation).ConfigureAwait(false))
            return await objects.DeferredReferenceAsync(type, node, null, cancellation: cancellation).ConfigureAwait(false);
        var arguments = await EffectiveArgumentsAsync(node, parameters, cancellation).ConfigureAwait(false);
        return context.CreateTypeReference(
            type,
            [.. type.AllTypeParameters.Take(type.OuterTypeParameterCount), .. arguments],
            ObjectFlags.FromTypeNode);
    }

    private async ValueTask<Type> AliasReferenceAsync(SyntaxNode node, Symbol symbol, CancellationToken cancellation)
    {
        if ((symbol.CheckFlags & CheckFlags.Unresolved) != 0)
        {
            var alias = context.CreateAlias(symbol, await NodeArgumentsAsync(node, cancellation).ConfigureAwait(false));
            var key = TypeCacheKey.Instantiation([], alias, false);
            if (!errors.TryGetValue(key, out var error))
                errors.Add(key, error = new IntrinsicType(context, TypeFlags.Any, "error") { Alias = alias });
            return error;
        }
        var type = await declared.GetAsync(symbol, cancellation).ConfigureAwait(false);
        var parameters = links.TypeAliases.Get(symbol).TypeParameters;
        if (parameters is null or { Count: 0 })
            return NoArguments(node, symbol) ? type : context.ErrorType;
        int count = Arguments(node)?.Count ?? 0, minimum = Minimum(parameters);
        if (count < minimum || count > parameters.Count)
        {
            host.TypeArgumentCount(node, symbol, type, minimum, parameters.Count, false);
            return context.ErrorType;
        }
        var parentAlias = host.AliasSymbol(node);
        Symbol? newSymbol = parentAlias is not null && (LocalAlias(symbol) || !LocalAlias(parentAlias)) ? parentAlias : null;
        IReadOnlyList<Type>? aliasArguments = null;
        if (newSymbol is not null)
            aliasArguments = scopes.Local(newSymbol, cancellation);
        else if (node is TypeReferenceNode)
        {
            var aliasSymbol = await ResolveNameAsync(node, S.Alias, true, cancellation).ConfigureAwait(false);
            if (aliasSymbol != symbols.UnknownSymbol)
            {
                var target = await aliases.ResolveAsync(aliasSymbol, cancellation).ConfigureAwait(false);
                if ((target.Flags & S.TypeAlias) != 0)
                {
                    newSymbol = target;
                    aliasArguments = await NodeArgumentsAsync(node, cancellation).ConfigureAwait(false);
                }
            }
        }
        return await AliasInstantiationAsync(symbol, await NodeArgumentsAsync(node, cancellation).ConfigureAwait(false),
            newSymbol is null ? null : context.CreateAlias(newSymbol, aliasArguments!.ToArray()), cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> AliasInstantiationAsync(Symbol symbol, IReadOnlyList<Type> arguments, TypeAlias? alias = null,
        CancellationToken cancellation = default)
    {
        var type = await declared.GetAsync(symbol, cancellation).ConfigureAwait(false);
        if (type == context.IntrinsicMarkerType && arguments.Count == 1)
        {
            if (symbol.Name == "NoInfer")
                return await instantiation.NoInferAsync(arguments[0], cancellation).ConfigureAwait(false);
            if (symbol.Name is "Uppercase" or "Lowercase" or "Capitalize" or "Uncapitalize")
                return await algebra.StringMappingAsync(symbol, arguments[0], cancellation).ConfigureAwait(false);
        }
        var data = links.TypeAliases.Get(symbol);
        var key = TypeCacheKey.Instantiation(arguments.ToArray(), alias, false);
        if (data.Instantiations!.TryGetValue(key, out var cached))
            return cached;
        var parameters = data.TypeParameters!;
        var filled = await constraints.FillMissingArgumentsAsync(
            arguments,
            parameters,
            IsJs(symbol.ValueDeclaration),
            host.IdenticalAsync,
            cancellation).ConfigureAwait(false);
        var result = await instantiation.InstantiateAsync(
            type,
            TypeMapper.Create(parameters.ToArray(), filled.ToArray()),
            alias,
            cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Alias instantiation returned no type");
        return data.Instantiations[key] = result;
    }

    internal async ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference reference, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(reference);
        if (reference.ResolvedTypeArguments is { } cached)
            return cached;
        var target = (InterfaceType)reference.ReferencedType;
        int count = target.AllTypeParameters.Count - (target.ThisType is null ? 0 : 1);
        if (!resolutions.Push(reference, TypeSystemPropertyName.ResolvedTypeArguments))
            return Array.AsReadOnly(Enumerable.Repeat<Type>(context.ErrorType, count).ToArray());
        bool active = true;
        try
        {
            Type[] arguments = reference.Node switch
            {
                TypeReferenceNode node => [.. target.AllTypeParameters.Take(target.OuterTypeParameterCount),
                    .. await EffectiveArgumentsAsync(node, target.AllTypeParameters.Skip(target.OuterTypeParameterCount)
                        .Take(count - target.OuterTypeParameterCount).Cast<TypeParameter>().ToArray(), cancellation).ConfigureAwait(false)],
                ArrayTypeNode node => [await host.TypeFromNodeAsync(node.ElementType!, cancellation).ConfigureAwait(false)],
                TupleTypeNode node => await NodesAsync(node.Elements!, cancellation).ConfigureAwait(false),
                null => [],
                _ => throw new InvalidOperationException("Unsupported deferred reference node")
            };
            bool resolved = resolutions.Pop();
            active = false;
            if (resolved)
            {
                if (reference.ResolvedTypeArguments is null)
                    reference.ResolvedTypeArguments = reference.Mapper is null ? Array.AsReadOnly(arguments)
                        : await instantiation.TypesAsync(arguments, reference.Mapper, cancellation).ConfigureAwait(false);
            }
            else
            {
                reference.ResolvedTypeArguments ??= Array.AsReadOnly(Enumerable.Repeat<Type>(context.ErrorType, count).ToArray());
                host.CircularArguments(reference.Node, target);
            }
            return reference.ResolvedTypeArguments;
        }
        finally
        {
            if (active)
                resolutions.Pop();
        }
    }

    internal async ValueTask<bool> DeferredAsync(SyntaxNode node, bool hasDefaults, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (host.AliasSymbol(node) is not null)
            return true;
        var parent = node.Parent;
        while (parent?.Kind is K.ParenthesizedType or K.NamedTupleMember or K.TypeReference or K.UnionType or K.IntersectionType
            or K.IndexedAccessType or K.ConditionalType or K.TypeOperator or K.ArrayType or K.TupleType)
        {
            cancellation.ThrowIfCancellationRequested();
            parent = parent.Parent;
        }
        if (parent is not TypeAliasDeclarationNode)
            return false;
        if (node is TypeReferenceNode && hasDefaults)
            return true;
        IEnumerable<SyntaxNode> children = node switch
        {
            ArrayTypeNode array => [array.ElementType!],
            TupleTypeNode tuple => tuple.Elements!,
            TypeReferenceNode reference => reference.TypeArguments is { } args ? args : [],
            _ => throw new InvalidOperationException("Unsupported deferred reference kind")
        };
        foreach (var child in children)
            if (await MayResolveAliasAsync(child, cancellation).ConfigureAwait(false))
                return true;
        return false;
    }

    private async ValueTask<bool> MayResolveAliasAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var pending = new Stack<SyntaxNode>();
        pending.Push(node);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            switch (current)
            {
                case TypeReferenceNode:
                    if ((await ResolveNameAsync(current, S.Type, false, cancellation).ConfigureAwait(false)).Flags.HasFlag(S.TypeAlias))
                        return true;
                    break;
                case TypeQueryNode:
                    return true;
                case TypeOperatorNode { Operator: not K.UniqueKeyword } operation:
                    pending.Push(operation.Type!);
                    break;
                case ParenthesizedTypeNode or OptionalTypeNode or NamedTupleMemberNode:
                    pending.Push(((ITypedNode)current).Type!);
                    break;
                case RestTypeNode rest:
                    if (rest.Type is not ArrayTypeNode array)
                        return true;
                    pending.Push(array.ElementType!);
                    break;
                case UnionTypeNode union:
                    foreach (var type in union.Types!.Reverse())
                        pending.Push(type);
                    break;
                case IntersectionTypeNode intersection:
                    foreach (var type in intersection.Types!.Reverse())
                        pending.Push(type);
                    break;
                case IndexedAccessTypeNode indexed:
                    pending.Push(indexed.IndexType!);
                    pending.Push(indexed.ObjectType!);
                    break;
                case ConditionalTypeNode conditional:
                    pending.Push(conditional.FalseType!);
                    pending.Push(conditional.TrueType!);
                    pending.Push(conditional.ExtendsType!);
                    pending.Push(conditional.CheckType!);
                    break;
            }
        }
        return false;
    }

    private async ValueTask<IReadOnlyList<Type>> EffectiveArgumentsAsync(
        SyntaxNode node,
        IReadOnlyList<TypeParameter> parameters,
        CancellationToken cancellation)
        => await constraints.FillMissingArgumentsAsync(await NodeArgumentsAsync(node, cancellation).ConfigureAwait(false), parameters,
            IsJs(node), host.IdenticalAsync, cancellation).ConfigureAwait(false);

    private ValueTask<Type[]> NodeArgumentsAsync(SyntaxNode node, CancellationToken cancellation)
        => NodesAsync(Arguments(node) is { } arguments ? arguments : [], cancellation);

    private async ValueTask<Type[]> NodesAsync(IEnumerable<SyntaxNode> nodes, CancellationToken cancellation)
    {
        var result = new List<Type>();
        foreach (var node in nodes)
            result.Add(await host.TypeFromNodeAsync(node, cancellation).ConfigureAwait(false));
        return result.ToArray();
    }

    private bool NoArguments(SyntaxNode node, Symbol symbol)
    {
        if (Arguments(node) is null or { Count: 0 })
            return true;
        host.NotGeneric(node, symbol);
        return false;
    }

    internal static int Minimum(IReadOnlyList<TypeParameter> parameters)
    {
        int result = 0;
        for (int i = 0; i < parameters.Count; i++)
            if (parameters[i].Symbol?.Declarations.OfType<TypeParameterDeclarationNode>().Any(d => d.DefaultType is not null) != true)
                result = i + 1;
        return result;
    }

    private static bool IsJs(SyntaxNode? node) => node is not null && (node.Flags & NodeFlags.JavaScriptFile) != 0;

    private static bool LocalAlias(Symbol symbol) => symbol.Declarations.OfType<TypeAliasDeclarationNode>().Any(declaration =>
        {
            for (var parent = declaration.Parent; parent is not null; parent = parent.Parent)
                if (SemanticSyntax.FunctionDeclarationLike(parent))
                    return true;
            return false;
        });

    internal static NodeList? Arguments(SyntaxNode node) => node switch
    { TypeReferenceNode reference => reference.TypeArguments, ExpressionWithTypeArgumentsNode expression => expression.TypeArguments, _ => null };

    internal static SyntaxNode? Name(SyntaxNode node)
    {
        if (node is TypeReferenceNode reference)
            return reference.TypeName;
        if (node is ExpressionWithTypeArgumentsNode expression)
        {
            var root = expression.Expression;
            while (root is PropertyAccessExpressionNode { Name: IdentifierNode } access)
                root = access.Expression;
            if (root is IdentifierNode)
                return expression.Expression;
        }
        return null;
    }
}

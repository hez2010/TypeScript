using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using F = TypeScript.Compiler.Checking.TypeFlags;
using O = TypeScript.Compiler.Checking.ObjectFlags;

namespace TypeScript.Compiler.Checking;

internal interface IObjectInstantiationHost
{
    ValueTask<IReadOnlyList<Type>> OuterTypeParametersAsync(SyntaxNode declaration, CancellationToken cancellation);

    ValueTask<Symbol?> TypeReferenceSymbolAsync(TypeReferenceNode reference, CancellationToken cancellation);

    ValueTask<Symbol> ResolvedSymbolAsync(IdentifierNode identifier, CancellationToken cancellation);

    ValueTask<TypeAlias?> AliasForTypeNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<TypeParameter> MappedParameterAsync(MappedType type, CancellationToken cancellation);

    ValueTask<Type> InstantiateMappedAsync(MappedType type, TypeMapper mapper, TypeAlias? alias, CancellationToken cancellation);
}

// Captured outer parameters form the cache key. Members remain unresolved until
// requested; instantiated objects retain a target and mapper rather than copies.
internal sealed class ObjectInstantiation(
    TypeContext context,
    CheckerLinks links,
    TypeInstantiation instantiation,
    TypeVariables variables,
    IObjectInstantiationHost host)
{
    internal async ValueTask<Type> InstantiateAsync(ObjectType type, TypeMapper mapper, TypeAlias? alias = null,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        RequireAlias(alias);
        var declaration = type switch
        {
            TypeReference { Node: { } node } => node,
            InstantiationExpressionType { Node: { } node } => node,
            _ => type.Symbol?.Declarations.FirstOrDefault()
                ?? throw new InvalidOperationException("Object type has no declaration")
        };
        var data = links.TypeNodes.Get(declaration);
        var target = (type.ObjectFlags & O.Reference) != 0
            ? data.ResolvedType as ObjectType ?? throw new InvalidOperationException("Deferred type node has no resolved object type")
            : (type.ObjectFlags & O.Instantiated) != 0
                ? type.Target as ObjectType ?? throw new InvalidOperationException("Instantiated object has no target")
                : type;
        context.RequireOwned(target);
        var parameters = data.OuterTypeParameters;
        if (parameters is null)
        {
            parameters = await host.OuterTypeParametersAsync(declaration, cancellation).ConfigureAwait(false);
            foreach (var parameter in parameters)
                context.RequireOwned(parameter);
            bool filter = target.Alias is null || target.Alias.TypeArguments.Count == 0;
            if (filter && (type.ObjectFlags & (O.Reference | O.InstantiationExpressionType)) != 0)
                parameters = await FilterAsync(parameters, [declaration], cancellation).ConfigureAwait(false);
            else if (filter && (target.Symbol!.Flags & (SymbolFlags.Method | SymbolFlags.TypeLiteral)) != 0)
                parameters = await FilterAsync(parameters, type.Symbol!.Declarations, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            data.OuterTypeParameters = parameters = Array.AsReadOnly(parameters.ToArray());
        }
        foreach (var parameter in parameters)
            context.RequireOwned(parameter);
        if (parameters.Count == 0)
            return type;
        var arguments = new Type[parameters.Count];
        var combined = instantiation.Combine(type.Mapper, mapper);
        for (int i = 0; i < arguments.Length; i++)
            arguments[i] = await combined.MapTypeAsync(parameters[i], cancellation).ConfigureAwait(false);
        alias ??= await instantiation.AliasAsync(type.Alias, mapper, cancellation).ConfigureAwait(false);
        var key = TypeCacheKey.Instantiation(arguments, alias, (type.ObjectFlags & O.SingleSignatureType) != 0);
        var cache = target.Instantiations ??= new()
        {
            [TypeCacheKey.Instantiation(parameters.ToArray(), target.Alias, false)] = target
        };
        if (cache.TryGetValue(key, out var existing))
            return existing;
        var newMapper = TypeMapper.Create(parameters.ToArray(), arguments);
        if ((target.ObjectFlags & O.SingleSignatureType) != 0)
            newMapper = instantiation.Combine(newMapper, mapper);
        Type result = target switch
        {
            TypeReference when (target.ObjectFlags & O.Reference) != 0 =>
                await DeferredReferenceAsync(
                    ((TypeReference)type).ReferencedType,
                    declaration,
                    newMapper,
                    alias,
                    cancellation).ConfigureAwait(false),
            MappedType mapped => await host.InstantiateMappedAsync(mapped, newMapper, alias, cancellation).ConfigureAwait(false),
            _ => await AnonymousAsync(target, newMapper, alias, cancellation).ConfigureAwait(false)
        };
        context.RequireOwned(result);
        cache[key] = result;
        try
        {
            if ((result.Flags & F.ObjectFlagsType) != 0 && (result.ObjectFlags & O.CouldContainTypeVariablesComputed) == 0)
            {
                bool contains = false;
                foreach (var argument in arguments)
                    if (await variables.CouldContainAsync(argument, cancellation).ConfigureAwait(false))
                    {
                        contains = true;
                        break;
                    }
                if ((result.ObjectFlags & O.CouldContainTypeVariablesComputed) == 0)
                {
                    if ((result.ObjectFlags & (O.Mapped | O.Anonymous | O.Reference)) != 0)
                        result.ObjectFlags |= O.CouldContainTypeVariablesComputed | (contains ? O.CouldContainTypeVariables : 0);
                    else if (!contains)
                        result.ObjectFlags |= O.CouldContainTypeVariablesComputed;
                }
            }
            cancellation.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            if (cache.GetValueOrDefault(key) == result)
                cache.Remove(key);
            throw;
        }
    }

    private async ValueTask<IReadOnlyList<Type>> FilterAsync(IReadOnlyList<Type> parameters,
        IReadOnlyList<SyntaxNode> declarations, CancellationToken cancellation)
    {
        var result = new List<Type>();
        foreach (var parameter in parameters)
            foreach (var declaration in declarations)
                if (await PossiblyReferencedAsync((TypeParameter)parameter, declaration, cancellation).ConfigureAwait(false))
                {
                    result.Add(parameter);
                    break;
                }
        return result;
    }

    internal async ValueTask<TypeReference> DeferredReferenceAsync(Type target, SyntaxNode node, TypeMapper? mapper,
        TypeAlias? alias = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(target);
        if (alias is null)
        {
            alias = await host.AliasForTypeNodeAsync(node, cancellation).ConfigureAwait(false);
            if (alias is not null && mapper is not null)
                alias = await instantiation.AliasAsync(alias, mapper, cancellation).ConfigureAwait(false);
        }
        RequireAlias(alias);
        return new(context, O.Reference, target.Symbol) { Alias = alias, Target = target, Mapper = mapper, Node = node };
    }

    internal async ValueTask<ObjectType> AnonymousAsync(ObjectType type, TypeMapper mapper, TypeAlias? alias = null,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        RequireAlias(alias);
        var result = context.NewObjectType(
            type.ObjectFlags & ~(O.CouldContainTypeVariablesComputed | O.CouldContainTypeVariables | O.MembersResolved) | O.Instantiated,
            type.Symbol);
        if (type is MappedType mapped)
        {
            var copy = (MappedType)result;
            copy.Declaration = mapped.Declaration;
            var original = await host.MappedParameterAsync(mapped, cancellation).ConfigureAwait(false);
            var parameter = instantiation.CloneParameter(original);
            copy.TypeParameter = parameter;
            mapper = instantiation.Combine(TypeMapper.Create([original], [parameter]), mapper);
            parameter.Mapper = mapper;
        }
        else if (type is InstantiationExpressionType expression)
            ((InstantiationExpressionType)result).Node = expression.Node;
        alias ??= await instantiation.AliasAsync(type.Alias, mapper, cancellation).ConfigureAwait(false);
        result.Alias = alias;
        if (alias is { TypeArguments.Count: > 0 })
            result.ObjectFlags |= TypeContext.PropagatingFlags(alias.TypeArguments.ToArray());
        result.Target = type;
        result.Mapper = mapper;
        return result;
    }

    private void RequireAlias(TypeAlias? alias)
    {
        if (alias is not null)
            foreach (var argument in alias.TypeArguments)
                context.RequireOwned(argument);
    }

    internal async ValueTask<bool> PossiblyReferencedAsync(TypeParameter parameter, SyntaxNode node,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(parameter);
        if (parameter.Symbol is not { Declarations.Length: 1 } symbol)
            return true;
        var container = symbol.Declarations[0].Parent;
        for (var current = node; current != container; current = current.Parent)
        {
            cancellation.ThrowIfCancellationRequested();
            if (current is null || current.Kind == K.Block
                || current is ConditionalTypeNode { ExtendsType: { } extendsType }
                    && await ContainsReferenceAsync(parameter, extendsType, cancellation).ConfigureAwait(false))
                return true;
        }
        return await ContainsReferenceAsync(parameter, node, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<bool> ContainsReferenceAsync(TypeParameter parameter, SyntaxNode node, CancellationToken cancellation)
    {
        var pending = new Stack<SyntaxNode>();
        pending.Push(node);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            switch (current)
            {
                case { Kind: K.ThisType }:
                    if (parameter.IsThisType)
                        return true;
                    continue;
                case TypeReferenceNode reference:
                    if (!parameter.IsThisType && (reference.TypeArguments?.Count ?? 0) == 0
                        && await host.TypeReferenceSymbolAsync(reference, cancellation).ConfigureAwait(false) == parameter.Symbol)
                        return true;
                    break;
                case TypeQueryNode query:
                    var identifier = FirstIdentifier(query.ExprName!, cancellation);
                    if (identifier.Text == Utf8Literals.This)
                        return true;
                    var resolved = await host.ResolvedSymbolAsync(identifier, cancellation).ConfigureAwait(false);
                    var declaration = parameter.Symbol!.Declarations[0];
                    var scope = declaration is TypeParameterDeclarationNode ? declaration.Parent
                        : parameter.IsThisType ? declaration : null;
                    if (scope is null)
                        return true;
                    foreach (var valueDeclaration in resolved.Declarations)
                        for (var ancestor = valueDeclaration; ancestor is not null; ancestor = ancestor.Parent)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (ancestor == scope)
                                return true;
                        }
                    Push(query.TypeArguments);
                    continue;
                case IFunctionSignature signature when current.Kind is K.MethodDeclaration or K.MethodSignature:
                    if (signature.Type is null && SemanticSyntax.Body(current) is not null)
                        return true;
                    if (signature.Type is not null)
                        pending.Push(signature.Type);
                    Push(signature.Parameters);
                    Push(signature.TypeParameters);
                    continue;
            }
            for (int i = current.ChildCount - 1; i >= 0; i--)
                pending.Push(current.GetChild(i));
        }
        return false;

        void Push(NodeList? nodes)
        {
            if (nodes is not null)
                for (int i = nodes.Count - 1; i >= 0; i--)
                    pending.Push(nodes[i]);
        }
    }

    private static IdentifierNode FirstIdentifier(SyntaxNode node, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            switch (node)
            {
                case IdentifierNode identifier:
                    return identifier;
                case QualifiedNameNode qualified:
                    node = qualified.Left!;
                    break;
                case PropertyAccessExpressionNode access:
                    node = access.Expression!;
                    break;
                default:
                    throw new InvalidOperationException("Type query has an invalid entity name");
            }
        }
    }
}

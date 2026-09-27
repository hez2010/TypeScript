using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal interface ITypeNodeHost
{
    ValueTask<Type> ReferenceAsync(SyntaxNode node, CancellationToken cancellation);

    void InvalidThisType(SyntaxNode node);

    ValueTask<Type> LiteralExpressionAsync(SyntaxNode expression, CancellationToken cancellation);

    ValueTask<Type> TypeQueryAsync(TypeQueryNode node, CancellationToken cancellation);

    ValueTask<IReadOnlyDictionary<TextSlice, Symbol>> MembersAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<Type> IndexAsync(Type type, CancellationToken cancellation);

    ValueTask<Type> IndexedAccessAsync(Type objectType, Type indexType, SyntaxNode node, TypeAlias? alias, CancellationToken cancellation);

    ValueTask<bool> DeferredReferenceAsync(SyntaxNode node, bool hasDefaults, CancellationToken cancellation);

    Type ArrayTarget(bool isReadonly);

    ValueTask<Type> ConditionalAsync(ConditionalRoot root, CancellationToken cancellation);

    ValueTask<Type> ImportTypeAsync(ImportTypeNode node, CancellationToken cancellation);
}

internal sealed class TypeNodes(TypeContext context, CheckerLinks links, CheckerSymbols symbols, TypeParameterScopes scopes,
    TypeAlgebra algebra, TupleTypes tuples, ObjectInstantiation objects, MappedTypes mapped, TypeNodeFlow flow, ITypeNodeHost host)
{
    internal async ValueTask<Type> FromNodeAsync(SyntaxNode node, CancellationToken cancellation = default)
        => await flow.ApplyAsync(await WorkerAsync(node, cancellation).ConfigureAwait(false), node, cancellation).ConfigureAwait(false);

    internal async ValueTask<Type> WorkerAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        switch (node.Kind)
        {
            case K.AnyKeyword:
            case K.JSDocAllType:
                return context.AnyType;
            case K.UnknownKeyword:
                return context.UnknownType;
            case K.StringKeyword:
                return context.StringType;
            case K.NumberKeyword:
                return context.NumberType;
            case K.BigIntKeyword:
                return context.BigIntType;
            case K.BooleanKeyword:
                return context.BooleanType;
            case K.SymbolKeyword:
                return context.ESSymbolType;
            case K.VoidKeyword:
                return context.VoidType;
            case K.UndefinedKeyword:
                return context.UndefinedType;
            case K.NullKeyword:
                return context.NullType;
            case K.NeverKeyword:
                return context.NeverType;
            case K.ObjectKeyword:
                return context.NonPrimitiveType;
            case K.IntrinsicKeyword:
                return context.IntrinsicMarkerType;
            case K.ParenthesizedType:
            case K.JSDocNonNullableType:
                return await FromNodeAsync(((ITypedNode)node).Type!, cancellation).ConfigureAwait(false);
            case K.JSDocNullableType:
                var nullable = await FromNodeAsync(((ITypedNode)node).Type!, cancellation).ConfigureAwait(false);
                return context.StrictNullChecks
                    ? await algebra.UnionAsync([nullable, context.NullType], cancellation: cancellation).ConfigureAwait(false)
                    : nullable;
            case K.JSDocVariadicType:
                return await tuples.ArrayAsync(
                    await FromNodeAsync(((ITypedNode)node).Type!, cancellation).ConfigureAwait(false),
                    cancellation: cancellation).ConfigureAwait(false);
            case K.JSDocOptionalType:
                return await OptionalAsync(
                    await FromNodeAsync(((ITypedNode)node).Type!, cancellation).ConfigureAwait(false),
                    false,
                    cancellation).ConfigureAwait(false);
            case K.TypePredicate:
                return ((TypePredicateNode)node).AssertsModifier is null ? context.BooleanType : context.VoidType;
            case K.OptionalType:
                return await OptionalAsync(
                    await FromNodeAsync(((ITypedNode)node).Type!, cancellation).ConfigureAwait(false),
                    true,
                    cancellation).ConfigureAwait(false);
            case K.RestType:
                return await RestAsync(node, cancellation).ConfigureAwait(false);
            case K.LiteralType when ((LiteralTypeNode)node).Literal?.Kind == K.NullKeyword:
                return context.NullType;
        }
        var data = links.TypeNodes.Get(node);
        if (data.ResolvedType is { } cached)
            return cached;
        Type result;
        switch (node)
        {
            case { Kind: K.ThisType or K.ThisKeyword }:
                result = await ThisTypeAsync(node, cancellation).ConfigureAwait(false);
                break;
            case LiteralTypeNode literal:
                result = await algebra.RegularTypeAsync(
                    await host.LiteralExpressionAsync(literal.Literal!, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false);
                break;
            case TypeReferenceNode or ExpressionWithTypeArgumentsNode:
                result = await host.ReferenceAsync(node, cancellation).ConfigureAwait(false);
                break;
            case TypeQueryNode query:
                result = await host.TypeQueryAsync(query, cancellation).ConfigureAwait(false);
                break;
            case ArrayTypeNode or TupleTypeNode:
                result = await ArrayOrTupleAsync(node, cancellation).ConfigureAwait(false);
                break;
            case NamedTupleMemberNode member:
                result = member.DotDotDotToken is not null ? await RestAsync(member, cancellation).ConfigureAwait(false)
                    : await FromNodeAsync(member.Type!, cancellation).ConfigureAwait(false);
                if (member.DotDotDotToken is null && member.QuestionToken is not null)
                    result = await OptionalAsync(result, true, cancellation).ConfigureAwait(false);
                break;
            case UnionTypeNode union:
                result = await algebra.UnionAsync(await NodesAsync(union.Types!, cancellation).ConfigureAwait(false),
                    alias: Alias(node), cancellation: cancellation).ConfigureAwait(false);
                break;
            case IntersectionTypeNode intersection:
                var parts = await NodesAsync(intersection.Types!, cancellation).ConfigureAwait(false);
                bool preserve = false;
                if (parts.Length == 2)
                {
                    int index = Array.IndexOf(parts, context.EmptyTypeLiteralType);
                    if (index >= 0)
                    {
                        var part = parts[1 - index];
                        preserve = (part.Flags & (TypeFlags.String | TypeFlags.Number | TypeFlags.BigInt)) != 0
                            || part is TemplateLiteralType && TypeAlgebra.IsPatternLiteral(part);
                    }
                }
                result = await algebra.IntersectionAsync(parts, preserve ? IntersectionFlags.NoSupertypeReduction : 0,
                    Alias(node), cancellation).ConfigureAwait(false);
                break;
            case { Kind: K.TypeLiteral or K.FunctionType or K.ConstructorType }:
                var alias = Alias(node);
                var symbol = symbols.Binding(node)?.Get(node)?.Symbol;
                if (symbol is null || (await host.MembersAsync(symbol, cancellation).ConfigureAwait(false)).Count == 0 && alias is null)
                    result = context.EmptyTypeLiteralType;
                else
                {
                    result = context.NewObjectType(ObjectFlags.Anonymous, symbol);
                    result.Alias = alias;
                }
                break;
            case TypeOperatorNode operation:
                switch (operation.Operator)
                {
                    case K.KeyOfKeyword:
                        result = await host.IndexAsync(
                            await FromNodeAsync(operation.Type!, cancellation).ConfigureAwait(false),
                            cancellation).ConfigureAwait(false);
                        break;
                    case K.UniqueKeyword:
                        result = operation.Type?.Kind == K.SymbolKeyword ? UniqueSymbol(node.Parent) : context.ErrorType;
                        break;
                    case K.ReadonlyKeyword:
                        result = await FromNodeAsync(operation.Type!, cancellation).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown type operator");
                }
                break;
            case IndexedAccessTypeNode indexed:
                result = await host.IndexedAccessAsync(await FromNodeAsync(indexed.ObjectType!, cancellation).ConfigureAwait(false),
                    await FromNodeAsync(indexed.IndexType!, cancellation).ConfigureAwait(false),
                    node,
                    Alias(node),
                    cancellation).ConfigureAwait(false);
                break;
            case TemplateLiteralTypeNode template:
                var spans = template.TemplateSpans!;
                var texts = new TextSlice[spans.Count + 1];
                var types = new Type[spans.Count];
                texts[0] = ((TemplateHeadNode)template.Head!).Text;
                for (int i = 0; i < spans.Count; i++)
                {
                    var span = (TemplateLiteralTypeSpanNode)spans[i];
                    texts[i + 1] = span.Literal is TemplateMiddleNode middle ? middle.Text : ((TemplateTailNode)span.Literal!).Text;
                    types[i] = await FromNodeAsync(span.Type!, cancellation).ConfigureAwait(false);
                }
                result = await algebra.TemplateAsync(texts, types, cancellation).ConfigureAwait(false);
                break;
            case MappedTypeNode mappedNode:
                var mappedType = (MappedType)context.NewObjectType(ObjectFlags.Mapped, symbols.Binding(node)?.Get(node)?.Symbol);
                mappedType.Declaration = mappedNode;
                mappedType.Alias = Alias(node);
                data.ResolvedType = mappedType;
                try
                {
                    await mapped.ConstraintAsync(mappedType, cancellation).ConfigureAwait(false);
                    return mappedType;
                }
                catch
                {
                    if (data.ResolvedType == mappedType)
                        data.ResolvedType = null;
                    throw;
                }
            case InferTypeNode infer:
                result = scopes.Parameter(symbols.Declaration(infer.TypeParameter!)!);
                break;
            case ConditionalTypeNode conditional:
                var check = await FromNodeAsync(conditional.CheckType!, cancellation).ConfigureAwait(false);
                var conditionalAlias = Alias(node);
                var outer = await scopes.OuterAsync(node, cancellation: cancellation).ConfigureAwait(false);
                var captured = new List<TypeParameter>();
                foreach (var parameter in outer.Cast<TypeParameter>())
                    if (conditionalAlias is { TypeArguments.Count: > 0 }
                        || await objects.PossiblyReferencedAsync(parameter, node, cancellation).ConfigureAwait(false))
                        captured.Add(parameter);
                var root = new ConditionalRoot(
                    conditional,
                    check,
                    await FromNodeAsync(conditional.ExtendsType!, cancellation).ConfigureAwait(false),
                    check is TypeParameter)
                {
                    OuterTypeParameters = captured.Count == 0 ? null : captured.AsReadOnly(),
                    InferTypeParameters = symbols.Binding(node)?.Get(node)?.Locals.Values.Where(s => (s.Flags & SymbolFlags.TypeParameter) != 0)
                        .Select(s => scopes.Parameter(symbols.Merger.GetMergedSymbol(s)!)).ToArray(),
                    Alias = conditionalAlias
                };
                if (root.InferTypeParameters?.Count == 0)
                    root.InferTypeParameters = null;
                result = await host.ConditionalAsync(root, cancellation).ConfigureAwait(false);
                if (captured.Count != 0)
                    root.Instantiations = new() { [TypeCacheKey.Instantiation(captured.ToArray(), null, false)] = result };
                break;
            case ImportTypeNode import:
                result = await host.ImportTypeAsync(import, cancellation).ConfigureAwait(false);
                break;
            default:
                result = context.ErrorType;
                break;
        }
        context.RequireOwned(result);
        cancellation.ThrowIfCancellationRequested();
        return data.ResolvedType = result;
    }

    internal Symbol? AliasSymbol(SyntaxNode node)
    {
        var parent = node.Parent;
        while (parent is ParenthesizedTypeNode or TypeOperatorNode { Operator: K.ReadonlyKeyword })
            parent = parent.Parent;
        return parent is TypeAliasDeclarationNode ? symbols.Declaration(parent) : null;
    }

    private async ValueTask<Type> ThisTypeAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var container = node;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            container = container.Parent ?? throw new InvalidOperationException("This type has no containing source file");
            if (container is ComputedPropertyNameNode)
            {
                container = container.Parent!.Parent!;
                continue;
            }
            if (container is DecoratorNode)
            {
                if (container.Parent is ParameterDeclarationNode parameter
                    && parameter.Parent is { } member
                    && SemanticSyntax.ClassElement(member))
                    container = member;
                else if (container.Parent is { } memberNode && SemanticSyntax.ClassElement(memberNode))
                    container = memberNode;
                continue;
            }
            if (container.Kind is K.FunctionDeclaration or K.FunctionExpression or K.ModuleDeclaration or K.ClassStaticBlockDeclaration
                or K.PropertyDeclaration or K.PropertySignature or K.MethodDeclaration or K.MethodSignature or K.Constructor
                or K.GetAccessor or K.SetAccessor or K.CallSignature or K.ConstructSignature or K.IndexSignature or K.EnumDeclaration or K.SourceFile)
                break;
        }
        if (container.Parent is { } parent && (SemanticSyntax.ClassLike(parent) || parent is InterfaceDeclarationNode)
            && !SemanticSyntax.IsStatic(container))
        {
            bool inBody = container is not ConstructorDeclarationNode;
            if (!inBody && SemanticSyntax.Body(container) is { } body)
                for (var current = node; current is not null && current != container; current = current.Parent)
                    if (current == body)
                    {
                        inBody = true;
                        break;
                    }
            if (inBody)
                return (Type?)(await scopes.ClassOrInterfaceAsync(
                    symbols.Declaration(parent)!,
                    cancellation).ConfigureAwait(false)).ThisType ?? context.ErrorType;
        }
        host.InvalidThisType(node);
        return context.ErrorType;
    }

    internal TypeAlias? Alias(SyntaxNode node)
        => AliasSymbol(node) is { } symbol ? context.CreateAlias(symbol, scopes.Local(symbol).ToArray()) : null;

    internal async ValueTask<Type[]> NodesAsync(IEnumerable<SyntaxNode> nodes, CancellationToken cancellation = default)
    {
        var result = new List<Type>();
        foreach (var node in nodes)
            result.Add(await FromNodeAsync(node, cancellation).ConfigureAwait(false));
        return result.ToArray();
    }

    private async ValueTask<Type> ArrayOrTupleAsync(SyntaxNode node, CancellationToken cancellation)
    {
        bool readOnly = node.Parent is TypeOperatorNode { Operator: K.ReadonlyKeyword };
        var arrayElement = ArrayElementNode(node);
        var infos = node is TupleTypeNode { Elements: { } elements } ? elements.Select(ElementInfo).ToArray() : [];
        var target = arrayElement is not null ? host.ArrayTarget(readOnly)
            : await tuples.TargetAsync(infos, readOnly, cancellation).ConfigureAwait(false);
        if (target == context.EmptyGenericType)
            return context.EmptyObjectType;
        if (!infos.Any(i => (i.Flags & ElementFlags.Variadic) != 0)
            && await host.DeferredReferenceAsync(node, false, cancellation).ConfigureAwait(false))
            return node is TupleTypeNode { Elements.Count: 0 } ? target
                : await objects.DeferredReferenceAsync(target, node, null, cancellation: cancellation).ConfigureAwait(false);
        Type[] types = node is ArrayTypeNode array ? [await FromNodeAsync(array.ElementType!, cancellation).ConfigureAwait(false)]
            : await NodesAsync(((TupleTypeNode)node).Elements!, cancellation).ConfigureAwait(false);
        return target is TupleType tuple ? await tuples.NormalizeReferenceAsync(
            tuple,
            types,
            ObjectFlags.FromTypeNode,
            cancellation).ConfigureAwait(false)
            : context.CreateTypeReference((InterfaceType)target, types, ObjectFlags.FromTypeNode);
    }

    private async ValueTask<Type> RestAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var inner = ((ITypedNode)node).Type!;
        return await FromNodeAsync(ArrayElementNode(inner) ?? inner, cancellation).ConfigureAwait(false);
    }

    private ValueTask<Type> OptionalAsync(Type type, bool property, CancellationToken cancellation)
    {
        if (!context.StrictNullChecks)
            return ValueTask.FromResult(type);
        var missing = property ? context.UndefinedOrMissingType : context.UndefinedType;
        return type == missing || type is UnionType union && union.Types[0] == missing ? ValueTask.FromResult(type)
            : algebra.UnionAsync([type, missing], cancellation: cancellation);
    }

    internal Type UniqueSymbol(SyntaxNode? node)
    {
        while (node is ParenthesizedTypeNode)
            node = node.Parent;
        bool valid = node switch
        {
            VariableDeclarationNode { Name: IdentifierNode, Parent: VariableDeclarationListNode { Parent: VariableStatementNode } list }
                => (list.Flags & NodeFlags.Const) != 0,
            PropertyDeclarationNode property => SemanticSyntax.HasModifier(property, K.ReadonlyKeyword)
                && SemanticSyntax.IsStatic(property),
            PropertySignatureDeclarationNode property => SemanticSyntax.HasModifier(property, K.ReadonlyKeyword),
            _ => false
        };
        return valid && symbols.Declaration(node!) is { } symbol ? context.GetUniqueSymbolType(symbol) : context.ESSymbolType;
    }

    internal static SyntaxNode? ArrayElementNode(SyntaxNode node)
    {
        while (true)
        {
            if (node is ParenthesizedTypeNode parenthesized)
            {
                node = parenthesized.Type!;
                continue;
            }
            if (node is ArrayTypeNode array)
                return array.ElementType;
            if (node is TupleTypeNode { Elements.Count: 1 } tuple)
            {
                var element = tuple.Elements[0];
                if (element.Kind == K.RestType || element is NamedTupleMemberNode { DotDotDotToken: not null })
                {
                    node = ((ITypedNode)element).Type!;
                    continue;
                }
            }
            return null;
        }
    }

    internal static TupleElementInfo ElementInfo(SyntaxNode node)
    {
        var flags = node switch
        {
            OptionalTypeNode or NamedTupleMemberNode { QuestionToken: not null } => ElementFlags.Optional,
            RestTypeNode or NamedTupleMemberNode { DotDotDotToken: not null } => ArrayElementNode(((ITypedNode)node).Type!) is null
                ? ElementFlags.Variadic
                : ElementFlags.Rest,
            _ => ElementFlags.Required
        };
        return new(flags, node is NamedTupleMemberNode or ParameterDeclarationNode ? node : null);
    }
}

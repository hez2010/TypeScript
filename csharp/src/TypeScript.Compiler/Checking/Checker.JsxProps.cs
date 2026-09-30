using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    public async ValueTask<Type> DiscriminateJsxContextAsync(JsxAttributesNode node, UnionType type, CancellationToken cancellation)
        => await TypeDiscrimination.JsxAsync(node, type, await JsxPropertyNameAsync(Utf8Literals.ElementChildrenAttribute, node, cancellation),
            node.Parent?.Parent is JsxElementNode element && JsxSemanticChildren(element).Count != 0, cancellation);

    private async ValueTask<int> JsxReferenceKindAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (jsxReferenceKinds.TryGetValue(node, out int cached))
            return cached;
        int kind = 2;
        if (JsxTag(node) is { } tag && !IntrinsicJsx(tag))
        {
            var type = await Views.ApparentAsync(await Expressions.CheckAsync(tag, cancellation: cancellation), cancellation);
            if ((await SignaturesAsync(type, true, cancellation)).Count != 0)
                kind = 0;
            else if ((await SignaturesAsync(type, false, cancellation)).Count != 0)
                kind = 1;
        }
        return jsxReferenceKinds[node] = kind;
    }

    private async ValueTask<Type?> JsxInstantiateAsync(Symbol symbol, Type[] supplied, SyntaxNode node, CancellationToken cancellation)
    {
        var type = await Declared.GetAsync(symbol, cancellation);
        bool alias = (symbol.Flags & SymbolFlags.TypeAlias) != 0;
        IReadOnlyList<TypeParameter> parameters = alias ? links.TypeAliases.Get(symbol).TypeParameters ?? []
            : type is InterfaceType face ? face.AllTypeParameters.OfType<TypeParameter>().Where(p => !p.IsThisType).ToArray() : [];
        if (parameters.Count < supplied.Length || !alias && type is not InterfaceType)
            return null;
        var arguments = await Instantiation.Constraints.FillMissingArgumentsAsync(supplied, parameters,
            (node.Flags & NodeFlags.JavaScriptFile) != 0, IdenticalAsync, cancellation);
        if (alias)
            return arguments.Count == 0 ? type : await References.AliasInstantiationAsync(symbol, arguments, cancellation: cancellation);
        return context.CreateTypeReference((InterfaceType)type, arguments.ToArray());
    }

    private async ValueTask<Type> JsxPropsAsync(Signature signature, SyntaxNode node, CancellationToken cancellation)
    {
        bool classComponent = node is not JsxOpeningFragmentNode && await JsxReferenceKindAsync(node, cancellation) == 0;
        Type? props;
        if (classComponent)
        {
            var name = await JsxPropertyNameAsync(Utf8Literals.ElementAttributesProperty, node, cancellation);
            props = name is null ? signature.Parameters.Count == 0
                ? context.UnknownType
                : await Parameters.AtAsync(signature, 0, cancellation)
                : name.Value.Length == 0
                    ? await Signatures.ReturnAsync(signature, cancellation)
                    : await JsxMemberPropsAsync(signature, name.Value, cancellation);
            if (props is null)
            {
                if (JsxAttributes(node)?.Properties?.Count > 0)
                    Error(node, DiagnosticCode.JSXElementClassDoesNotSupportAttributesBecauseItDoesNotHaveA0Property, name!.Value);
                return context.UnknownType;
            }
        }
        else
            props = signature.Parameters.Count == 0 ? context.UnknownType : await Parameters.AtAsync(signature, 0, cancellation);
        var ns = await JsxNamespaceAsync(node, cancellation);
        if (ns is not null && program.Symbols.Lookup(ns.Exports, Utf8Literals.LibraryManagedAttributes, SymbolFlags.Type) is { } managed)
        {
            var constructor = node is JsxOpeningFragmentNode ? await JsxFragmentTypeAsync(node, cancellation)
                : IntrinsicJsx(JsxTag(node)) ? context.AnyType : await Expressions.CheckAsync(JsxTag(node)!, cancellation: cancellation);
            props = await JsxInstantiateAsync(managed, [constructor, props], node, cancellation) ?? props;
        }
        if (classComponent)
        {
            if ((props.Flags & TypeFlags.Any) != 0)
                return props;
            if (ns is not null && program.Symbols.Lookup(ns.Exports, Utf8Literals.IntrinsicClassAttributes, SymbolFlags.Type) is { } symbol)
            {
                var intrinsic = await Declared.GetAsync(symbol, cancellation);
                IReadOnlyList<TypeParameter> parameters = intrinsic.Symbol is { } intrinsicSymbol
                    ? program.Scopes.Local(intrinsicSymbol, cancellation).OfType<TypeParameter>().ToArray() : [];
                if (parameters.Count != 0)
                {
                    var arguments = await Instantiation.Constraints.FillMissingArgumentsAsync(
                        [await Signatures.ReturnAsync(signature, cancellation)],
                        parameters, (node.Flags & NodeFlags.JavaScriptFile) != 0, IdenticalAsync, cancellation);
                    intrinsic = (await Instantiation.Engine.InstantiateAsync(
                        intrinsic,
                        TypeMapper.Create(parameters.Cast<Type>().ToArray(), arguments.ToArray()),
                        cancellation: cancellation))!;
                }
                props = await Algebra.IntersectionAsync([intrinsic, props], cancellation: cancellation);
            }
        }
        var attributes = await JsxTypeAsync(Utf8Literals.IntrinsicAttributes, node, cancellation);
        return attributes == context.ErrorType ? props : await Algebra.IntersectionAsync([attributes, props], cancellation: cancellation);
    }

    private async ValueTask<Type?> JsxMemberPropsAsync(Signature signature, Utf8String name, CancellationToken cancellation)
    {
        var types = new List<Type>();
        foreach (var part in signature.Composite?.Signatures ?? [signature])
        {
            var instance = await Signatures.ReturnAsync(part, cancellation);
            if ((instance.Flags & TypeFlags.Any) != 0)
                return instance;
            if (await Properties.PropertyAsync(instance, name, cancellation: cancellation) is not { } property)
                return null;
            types.Add(await Values.GetAsync(property, cancellation));
        }
        return types.Count == 1 ? types[0] : await Algebra.IntersectionAsync(types, cancellation: cancellation);
    }

    private async ValueTask<Type?> JsxContextAsync(SyntaxNode node, ContextFlags flags, CancellationToken cancellation)
    {
        if (node.Parent is JsxAttributeNode attribute)
        {
            var type = await Contexts.ApparentAsync(attribute.Parent!, flags, cancellation);
            return type is null || (type.Flags & TypeFlags.Any) != 0 ? null
                : await ContextualPropertyAsync(type, JsxName(attribute.Name!), cancellation);
        }
        if (node.Parent is JsxSpreadAttributeNode spread)
            return await Contexts.GetAsync(spread.Parent!, flags, cancellation);
        if (node.Parent is JsxOpeningElementNode or JsxSelfClosingElementNode)
        {
            var opening = node.Parent;
            var applied = opening is JsxOpeningElementNode && flags == ContextFlags.IgnoreNodeInferences ? null
                : Contexts.AppliedContext(opening is JsxOpeningElementNode ? opening.Parent! : node, flags);
            if (applied is not null)
                return applied;
            var signature = links.Signatures.Get(opening).ResolvedSignature == CallSignatures.Resolving ? CallSignatures.Resolving
                : await CallResolution.GetAsync(opening, cancellation: cancellation);
            return await JsxPropsAsync(signature, opening, cancellation);
        }
        if (node.Parent is JsxExpressionNode expression)
            return await Contexts.GetAsync(expression, flags, cancellation);
        if (node.Parent is JsxElementNode element)
        {
            var props = await Contexts.ApparentAsync(element.OpeningElement!.Attributes!, flags, cancellation);
            var name = await JsxPropertyNameAsync(Utf8Literals.ElementChildrenAttribute, element, cancellation);
            if (props is null || (props.Flags & TypeFlags.Any) != 0 || name is null or { IsEmpty: true })
                return null;
            var type = await ContextualPropertyAsync(await Views.ApparentAsync(props, cancellation), name.Value, cancellation);
            if (type is null)
                return null;
            var children = JsxSemanticChildren(element);
            int index = children.IndexOf(node);
            if (children.Count == 1)
                return type;
            return await Algebra.MapAsync(type, async part => await ArrayLikeAsync(part, cancellation)
                ? await Indexed.GetAsync(part, context.GetNumberLiteralType(index), cancellation: cancellation) : part,
                true, cancellation);
        }
        return null;
    }
}

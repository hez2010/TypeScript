using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private Type? emptyJsxType;

    private static List<SyntaxNode> JsxSemanticChildren(SyntaxNode node)
    {
        var children = node switch { JsxElementNode element => element.Children, JsxFragmentNode fragment => fragment.Children, _ => null };
        return children?.Where(child => child is not JsxTextNode { ContainsOnlyTriviaWhiteSpaces: true }
            && child is not JsxExpressionNode { Expression: null }).ToList() ?? [];
    }

    private Type JsxObject(Symbol? symbol, Dictionary<string, Symbol> members, ObjectFlags flags = 0, bool fresh = false)
    {
        var type = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved | ObjectFlags.JsxAttributes | flags
            | (fresh ? ObjectFlags.FreshLiteral | ObjectFlags.ObjectLiteral | ObjectFlags.ContainsObjectOrArrayLiteral : 0), symbol);
        type.Members = members.AsReadOnly();
        type.Properties = members.Values.ToArray();
        type.CallSignatures = type.ConstructSignatures = [];
        type.IndexInfos = [];
        return type;
    }

    private async ValueTask<IReadOnlyList<Type>> JsxChildrenAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation)
    {
        var types = new List<Type>();
        foreach (var child in JsxSemanticChildren(node))
            types.Add(child is JsxTextNode ? context.StringType : await Contexts.MutableAsync(child, mode, cancellation));
        return types;
    }

    private async ValueTask<Type> JsxAttributesTypeAsync(SyntaxNode opening, CheckMode mode, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var attributes = JsxAttributes(opening);
        var parent = (SyntaxNode?)attributes ?? opening;
        var symbol = attributes is null ? null : program.Symbols.Declaration(attributes);
        if (attributes is not null && symbol is null)
            symbol = new Symbol(
                SymbolFlags.ObjectLiteral | SymbolFlags.Transient,
                Symbol.InternalPrefix + "jsxAttributes")
            { ValueDeclaration = attributes };
        var members = new Dictionary<string, Symbol>();
        var all = new Dictionary<string, Symbol>();
        emptyJsxType ??= JsxObject(null, []);
        Type spread = emptyJsxType;
        Type? invalidSpread = null;
        bool anySpread = false, explicitChildren = false;
        ObjectFlags flags = ObjectFlags.JsxAttributes;
        string? childrenName = await JsxPropertyNameAsync("ElementChildrenAttribute", opening, cancellation);
        Type? contextual = attributes is null ? null : await Contexts.GetAsync(attributes, cancellation: cancellation);
        if (attributes is not null)
            foreach (var declaration in attributes.Properties!)
            {
                if (declaration is JsxAttributeNode attribute)
                {
                    var type = await JsxAttributeTypeAsync(attribute, mode, cancellation);
                    flags |= type.ObjectFlags & ObjectFlags.PropagatingFlags;
                    var original = program.Symbols.Declaration(attribute);
                    var property = new Symbol(
                        SymbolFlags.Property | SymbolFlags.Transient | (original?.Flags ?? 0),
                        original?.Name ?? JsxName(attribute.Name!))
                    { Parent = original?.Parent ?? symbol, ValueDeclaration = original?.ValueDeclaration ?? attribute };
                    if (original is not null)
                        property.DeclarationList.AddRange(original.Declarations);
                    else
                        property.DeclarationList.Add(attribute);
                    links.Values.Get(property).ResolvedType = type;
                    links.Values.Get(property).Target = original;
                    members[property.Name] = property;
                    if (context.StrictNullChecks)
                        all[property.Name] = property;
                    explicitChildren |= property.Name == childrenName;
                    if (contextual is not null && (mode & CheckMode.Inferential) != 0 && (mode & CheckMode.SkipContextSensitive) == 0
                        && ContextSensitive(attribute) && attribute.Initializer is JsxExpressionNode { Expression: { } expression })
                        Contexts.InferenceFor(attributes)!.IntraExpressionSites.Add((expression, type));
                }
                else if (declaration is JsxSpreadAttributeNode spreadAttribute)
                {
                    if (members.Count != 0)
                    {
                        flags |= ObjectFlags.FreshLiteral;
                        spread = await ObjectSpreads.GetAsync(
                            spread,
                            JsxObject(symbol, members, flags, true),
                            symbol,
                            flags,
                            false,
                            cancellation);
                        members = [];
                    }
                    var type = await Views.ReducedAsync(
                        await Expressions.CheckAsync(spreadAttribute.Expression!, mode & CheckMode.Inferential, cancellation),
                        cancellation);
                    anySpread |= (type.Flags & TypeFlags.Any) != 0;
                    if (await Bindings.ValidSpreadAsync(type, cancellation))
                    {
                        spread = await ObjectSpreads.GetAsync(spread, type, symbol, flags, false, cancellation);
                        if (context.StrictNullChecks)
                            foreach (var property in await Properties.GetAsync(type, cancellation))
                                if ((property.Flags & SymbolFlags.Optional) == 0
                                    && (property.CheckFlags & CheckFlags.Partial) == 0
                                    && all.TryGetValue(property.Name, out var previous))
                                    SpreadOverride(previous.ValueDeclaration!, previous, spreadAttribute);
                    }
                    else
                    {
                        Error(spreadAttribute.Expression!, DiagnosticCode.SpreadTypesMayOnlyBeCreatedFromObjectTypes);
                        invalidSpread = invalidSpread is null
                            ? type
                            : await Algebra.IntersectionAsync([invalidSpread, type], cancellation: cancellation);
                    }
                }
            }
        if (!anySpread && members.Count != 0)
        {
            flags |= ObjectFlags.FreshLiteral;
            spread = await ObjectSpreads.GetAsync(spread, JsxObject(symbol, members, flags, true), symbol, flags, false, cancellation);
        }
        if (opening is JsxOpeningElementNode { Parent: JsxElementNode } or JsxOpeningFragmentNode { Parent: JsxFragmentNode }
            && JsxSemanticChildren(opening.Parent!).Count != 0)
        {
            var children = await JsxChildrenAsync(opening.Parent!, mode, cancellation);
            if (!anySpread && !string.IsNullOrEmpty(childrenName))
            {
                if (explicitChildren)
                    Error(parent, DiagnosticCode.X0AreSpecifiedTwiceTheAttributeNamed0WillBeOverwritten, childrenName);
                var childContext = contextual is null
                    ? null
                    : await ContextualPropertyAsync(await Views.ApparentAsync(contextual, cancellation), childrenName, cancellation);
                bool tuple = false;
                if (childContext is not null)
                    foreach (var part in childContext is UnionType union ? union.Types : [childContext])
                        if (await ArrayLiterals.TupleLikeAsync(part, cancellation))
                        {
                            tuple = true;
                            break;
                        }
                var childType = children.Count == 1 ? children[0] : tuple
                    ? await Instantiation.Tuples.CreateAsync(
                        children,
                        children.Select(_ => new TupleElementInfo(ElementFlags.Required)).ToArray(),
                        cancellation: cancellation)
                    : await ArrayAsync(await Algebra.UnionAsync(children, cancellation: cancellation), cancellation);
                var child = new Symbol(SymbolFlags.Property | SymbolFlags.Transient, childrenName);
                var declaration = new PropertySignatureDeclarationNode
                {
                    Name = new IdentifierNode { Text = childrenName },
                    Parent = parent,
                    Pos = parent.Pos,
                    End = parent.End
                };
                declaration.Name.Parent = declaration;
                child.ValueDeclaration = declaration;
                links.Values.Get(child).ResolvedType = childType;
                ObjectFlags childFlags = flags;
                foreach (var childValue in children)
                    childFlags |= childValue.ObjectFlags & ObjectFlags.PropagatingFlags;
                spread = await ObjectSpreads.GetAsync(
                    spread,
                    JsxObject(symbol, new() { [childrenName] = child }),
                    symbol,
                    childFlags,
                    false,
                    cancellation);
            }
        }
        if (anySpread)
            return context.AnyType;
        if (invalidSpread is not null)
            return spread == emptyJsxType ? invalidSpread
            : await Algebra.IntersectionAsync([invalidSpread, spread], cancellation: cancellation);
        return spread == emptyJsxType ? JsxObject(symbol, members, flags, true) : spread;
    }
}

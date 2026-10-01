using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class JsxTransformer
{
    private async ValueTask<SyntaxNode> ElementAsync(SyntaxNode opening, NodeList? children, EmitRange location)
    {
        var tag = TagName(opening);
        var attributes = (opening is JsxOpeningElementNode element ? element.Attributes : ((JsxSelfClosingElementNode)opening).Attributes)!.Properties!;
        if (importSource.Length == 0 || HasKeyAfterSpread(attributes))
        {
            var props = attributes.Count > 0 ? await AttributesAsync(attributes, null) : Keyword(K.NullKeyword);
            var callee = importSource.Length == 0 ? await FactoryAsync(opening) : await ImportAsync("createElement"u8);
            return Call(callee, [tag, props, .. await ChildrenAsync(children)], location);
        }
        var childrenProperty = await ChildrenPropertyAsync(children);
        List<SyntaxNode> filtered = [.. attributes];
        JsxAttributeNode? key = null;
        for (int i = 0; i < filtered.Count; i++)
            if (filtered[i] is JsxAttributeNode { Name: IdentifierNode { Text: var name } } attribute && name == "key"u8)
            {
                key = attribute;
                filtered.RemoveAt(i);
                break;
            }
        var properties = filtered.Count > 0 ? await AttributesAsync(filtered, childrenProperty)
            : Object(childrenProperty is null ? [] : [childrenProperty]);
        return await AutomaticCallAsync(tag, properties, key, children, location);
    }

    private async ValueTask<SyntaxNode> FragmentAsync(JsxFragmentNode fragment, EmitRange location)
    {
        if (importSource.Length == 0)
        {
            var tag = await FactoryAsync(fragment.OpeningFragment!, fragment: true);
            var callee = await FactoryAsync(fragment.OpeningFragment!);
            return Call(callee, [tag, Keyword(K.NullKeyword), .. await ChildrenAsync(fragment.Children)], location);
        }
        var property = await ChildrenPropertyAsync(fragment.Children);
        return await AutomaticCallAsync(await ImportAsync("Fragment"u8), Object(property is null ? [] : [property]), null, fragment.Children, location);
    }

    private async ValueTask<SyntaxNode> AutomaticCallAsync(SyntaxNode tag, SyntaxNode props, JsxAttributeNode? key, NodeList? children, EmitRange location)
    {
        var semantic = SemanticChildren(children);
        bool staticChildren = semantic.Count > 1 || semantic is [JsxExpressionNode { DotDotDotToken: not null }];
        List<SyntaxNode> arguments = [tag, props];
        if (key is not null) arguments.Add(await InitializerAsync(key.Initializer));
        if (options.Jsx == JsxEmit.ReactJSXDev && Context.MostOriginal(source!) is SourceFileNode original)
        {
            if (key is null) arguments.Add(Context.VoidZero());
            arguments.Add(Keyword(staticChildren ? K.TrueKeyword : K.FalseKeyword));
            var (line, _) = original.Source.GetLineAndCharacter(location.Pos);
            int column = 0;
            var text = original.Source.Text;
            for (int i = original.Source.LineStarts[line]; i < location.Pos;)
            {
                int point = Wtf8.Decode(text.Span[i..], out int width);
                i += width;
                column += point > 0xFFFF ? 2 : 1;
            }
            fileNameDeclaration ??= F.NewVariableDeclaration(Context.NewUniqueName("_jsxFileName"u8,
                new(GeneratedIdentifierFlags.Optimistic | GeneratedIdentifierFlags.FileLevel)), null, null, String(source!.FileName));
            arguments.Add(Object([Property("fileName"u8, fileNameDeclaration.Name!),
                Property("lineNumber"u8, F.NewNumericLiteral(TokenFacts.NumberText(line + 1), TokenFlags.None)),
                Property("columnNumber"u8, F.NewNumericLiteral(TokenFacts.NumberText(column + 1), TokenFlags.None))]));
            arguments.Add(Keyword(K.ThisKeyword));
        }
        var factory = options.Jsx == JsxEmit.ReactJSXDev ? "jsxDEV"u8 : staticChildren ? "jsxs"u8 : "jsx"u8;
        return Call(await ImportAsync(factory), arguments, location);
    }

    private static bool HasKeyAfterSpread(IEnumerable<SyntaxNode> attributes)
    {
        bool spread = false;
        foreach (var attribute in attributes)
        {
            if (attribute is JsxSpreadAttributeNode spreadAttribute && (spreadAttribute.Expression is not ObjectLiteralExpressionNode obj
                || obj.Properties!.Any(p => p is SpreadAssignmentNode))) spread = true;
            else if (spread && attribute is JsxAttributeNode { Name: IdentifierNode { Text: var name } } && name == "key"u8) return true;
        }
        return false;
    }

    private static List<SyntaxNode> SemanticChildren(NodeList? children) => children is null ? [] : children.Where(child => child switch
    {
        JsxExpressionNode expression => expression.Expression is not null,
        JsxTextNode text => !text.ContainsOnlyTriviaWhiteSpaces,
        _ => true
    }).ToList();

    private async ValueTask<SyntaxNode?> ChildAsync(SyntaxNode child)
    {
        bool saved = inChild;
        inChild = true;
        try { return await VisitAsync(child); }
        finally { inChild = saved; }
    }

    private async ValueTask<List<SyntaxNode>> ChildrenAsync(NodeList? children)
    {
        List<SyntaxNode> result = [];
        foreach (var child in children ?? new([]))
            if (await ChildAsync(child) is { } visited) result.Add(visited);
        if (result.Count > 1)
            foreach (var child in result) Context.AddFlags(child, EmitFlags.StartOnNewLine);
        return result;
    }

    private async ValueTask<SyntaxNode?> ChildrenPropertyAsync(NodeList? children)
    {
        var semantic = SemanticChildren(children);
        if (semantic.Count == 1 && semantic[0] is not JsxExpressionNode { DotDotDotToken: not null })
            return await ChildAsync(semantic[0]) is { } single ? Property("children"u8, single) : null;
        List<SyntaxNode> results = [];
        foreach (var child in semantic)
            if (await ChildAsync(child) is { } result)
            {
                Context.SetFlags(result, Context.GetFlags(result) & ~EmitFlags.StartOnNewLine);
                results.Add(result);
            }
        return results.Count == 0 ? null : Property("children"u8, F.NewArrayLiteralExpression(new(results.ToArray()), false));
    }

    private async ValueTask<SyntaxNode> AttributesAsync(IEnumerable<SyntaxNode> attributes, SyntaxNode? children)
    {
        List<SyntaxNode> properties = [], expressions = [];
        bool nativeSpread = options.EmitTargetYear >= 2018;
        foreach (var attribute in attributes)
        {
            if (attribute is not JsxSpreadAttributeNode spread)
            {
                var value = (JsxAttributeNode)attribute;
                properties.Add(F.NewPropertyAssignment(null, AttributeName(value.Name!), null, null, await InitializerAsync(value.Initializer)));
            }
            else if (spread.Expression is ObjectLiteralExpressionNode obj && !HasProto(obj))
            {
                foreach (var property in obj.Properties ?? new([]))
                    if (!nativeSpread && property is SpreadAssignmentNode spreadProperty)
                    {
                        Flush();
                        expressions.Add((await VisitAsync(spreadProperty.Expression))!);
                    }
                    else properties.Add((await VisitAsync(property))!);
            }
            else if (nativeSpread) properties.Add(F.NewSpreadAssignment(await VisitAsync(spread.Expression)));
            else
            {
                Flush();
                expressions.Add((await VisitAsync(spread.Expression))!);
            }
        }
        if (children is not null) properties.Add(children);
        if (nativeSpread) return Object(properties);
        Flush();
        if (expressions.Count > 0 && expressions[0] is not ObjectLiteralExpressionNode) expressions.Insert(0, Object([]));
        return expressions.Count == 1 ? expressions[0] : Context.MethodCall(F.NewIdentifier("Object"u8), "assign"u8, expressions.ToArray());

        void Flush()
        {
            if (properties.Count == 0) return;
            expressions.Add(Object(properties));
            properties = [];
        }
    }

    private static bool HasProto(ObjectLiteralExpressionNode node) => (node.Properties ?? new([])).Any(p => p is PropertyAssignmentNode property
        && property.Name is StringLiteralNode or IdentifierNode && SyntaxNameText.Get(property.Name) == "__proto__"u8);

    private SyntaxNode AttributeName(SyntaxNode node)
    {
        var text = JsxName(node);
        if (node is IdentifierNode && IsIdentifier(text)) return node;
        return String(text);
    }

    private static bool IsIdentifier(Utf8String text)
    {
        if (text.Length == 0) return false;
        int point = Wtf8.Decode(text.Span, out int width);
        if (!TokenFacts.IsIdentifierStart(point)) return false;
        for (int i = width; i < text.Length; i += width)
            if (!TokenFacts.IsIdentifierPart(Wtf8.Decode(text.Span[i..], out width))) return false;
        return true;
    }

    private async ValueTask<SyntaxNode> InitializerAsync(SyntaxNode? node)
    {
        if (node is null || node is JsxExpressionNode { Expression: null }) return Keyword(K.TrueKeyword);
        if (node is StringLiteralNode literal) return EmitContext.CopyRange(F.NewStringLiteral(DecodeEntities(literal.Text), literal.TokenFlags), literal);
        if (node is JsxExpressionNode expression) return (await VisitAsync(expression.Expression))!;
        inChild = false;
        return (await VisitAsync(node))!;
    }
}

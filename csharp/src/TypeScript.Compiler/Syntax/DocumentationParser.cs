using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

internal sealed class DocumentationParser(SourceText source, ScriptKind scriptKind, NodeFlags context = 0)
{
    private readonly NodeFactory factory = new();
    private readonly string text = source.Text;
    private readonly NodeFlags flags = context | NodeFlags.JSDoc | (scriptKind is ScriptKind.JS or ScriptKind.JSX ? NodeFlags.JavaScriptFile : 0);
    public List<Diagnostic> Diagnostics { get; } = [];
    public NodeFlags SourceFlags { get; private set; }

    public JSDocNode[] Leading(int start, int end)
    {
        var scanner = new Scanner(source, false);
        scanner.SetTextRange(Math.Max(0, start), Math.Min(source.Length, end));
        var comments = new List<JSDocNode>();
        while (true)
        {
            K kind = scanner.Scan();
            if (kind == K.MultiLineCommentTrivia && scanner.TokenText.StartsWith("/**", StringComparison.Ordinal) && !scanner.TokenText.StartsWith("/**/", StringComparison.Ordinal))
                comments.Add(Parse(scanner.TokenStart, scanner.Position));
            else if (kind is not (K.WhitespaceTrivia or K.NewLineTrivia or K.SingleLineCommentTrivia or K.MultiLineCommentTrivia)) break;
        }
        return comments.ToArray();
    }
    private T Finish<T>(T node, int start, int end) where T : SyntaxNode
    {
        node.Pos = start; node.End = end; node.Flags = flags;
        for (int i = 0; i < node.ChildCount; i++) node.GetChild(i).Parent = node;
        return node;
    }
    private JSDocNode Parse(int start, int end)
    {
        int contentStart = start + 3, contentEnd = text.AsSpan(start, end - start).EndsWith("*/", StringComparison.Ordinal) ? end - 2 : end;
        var positions = new List<int>();
        int ticks = 0; bool fenced = false, quoted = false;
        for (int i = contentStart; i < contentEnd; i++)
        {
            if (text[i] == '`')
            {
                ticks = 1; while (i + 1 < contentEnd && text[i + 1] == '`') { ticks++; i++; }
                if (ticks >= 3) fenced = !fenced; else if (!fenced) quoted = !quoted;
                continue;
            }
            if (fenced || quoted || text[i] != '@' || i > contentStart && text[i - 1] == '{') continue;
            if (i != contentStart && !char.IsWhiteSpace(text[i - 1]) && text[i - 1] != '*') continue;
            if (i + 1 == contentEnd || TokenFacts.IsIdentifierStart(text[i + 1]) || char.IsWhiteSpace(text[i + 1])) positions.Add(i);
        }
        NodeList? comment = Comments(contentStart, positions.Count == 0 ? contentEnd : positions[0]);
        var tags = new List<SyntaxNode>();
        for (int i = 0; i < positions.Count; i++) tags.Add(Tag(positions[i], i + 1 < positions.Count ? positions[i + 1] : contentEnd));
        GroupTags(tags);
        NodeList? tagList = tags.Count == 0 ? null : new(tags.ToArray(), tags[0].Pos, tags[^1].End);
        return Finish(factory.NewJSDoc(comment, tagList), start, end);
    }
    private int SkipSpace(int pos, int end, bool preserveTrailing = true)
    {
        int original = pos;
        bool newLine = false;
        while (pos < end)
        {
            char ch = text[pos];
            if (char.IsWhiteSpace(ch)) { newLine |= TokenFacts.IsLineBreak(ch); pos++; }
            else if (ch == '*' && newLine) { pos++; newLine = false; }
            else break;
        }
        return pos == end && preserveTrailing ? original : pos;
    }
    private IdentifierNode Identifier(ref int pos, int end)
    {
        int start = pos;
        while (pos < end)
        {
            int point = char.IsHighSurrogate(text[pos]) && pos + 1 < end && char.IsLowSurrogate(text[pos + 1]) ? char.ConvertToUtf32(text[pos], text[pos + 1]) : text[pos];
            if (!TokenFacts.IsIdentifierPart(point) && point != '-') break;
            pos += point > 0xFFFF ? 2 : 1;
        }
        if (pos == start) Diagnostics.Add(new(Messages.Identifier_expected, pos, 0, []));
        var node = Finish(factory.NewIdentifier(text[start..pos]), start, pos);
        if (pos == start) node.Flags |= NodeFlags.ThisNodeHasError;
        return node;
    }
    private SyntaxNode Name(ref int pos, int end)
    {
        int start = pos;
        SyntaxNode name = Identifier(ref pos, end);
        while (pos < end && text[pos] is '.' or '#')
        { pos++; name = Finish(factory.NewQualifiedName(name, Identifier(ref pos, end)), start, pos); }
        return name;
    }
    private JSDocTypeExpressionNode? Type(ref int pos, int end, bool optional, bool mayOmitBraces = false)
    {
        pos = SkipSpace(pos, end);
        if (optional && (pos == end || text[pos] != '{' || pos + 1 < end && text[pos + 1] == '@')) return null;
        var result = Parser.DocumentationType(source, scriptKind, pos, end, mayOmitBraces, context);
        pos = result.End; Diagnostics.AddRange(result.Diagnostics); SourceFlags |= result.SourceFlags;
        return result.Node;
    }
    private SyntaxNode Tag(int start, int end)
    {
        int pos = start + 1;
        IdentifierNode tagName = Identifier(ref pos, end);
        pos = SkipSpace(pos, end);
        JSDocTypeExpressionNode? type = null;
        SyntaxNode? name = null;
        NodeList? parameters = null;
        bool bracketed = false, nameFirst = false;
        string tag = tagName.Text;
        if (tag == "import")
        {
            var imported = Parser.DocumentationImport(source, scriptKind, start + 1, end);
            Diagnostics.AddRange(imported.Diagnostics);
            NodeList? importedComment = Comments(imported.End, end);
            return Finish(factory.NewJSDocImportTag(tagName, imported.Node?.ImportClause, imported.Node?.ModuleSpecifier, imported.Node?.Attributes, importedComment), start, end);
        }
        if (tag is "implements" or "augments" or "extends")
        {
            var parsedType = Type(ref pos, end, false, true);
            SyntaxNode? expression = parsedType?.Type;
            NodeList? arguments = null;
            if (expression is TypeReferenceNode reference) { expression = reference.TypeName; arguments = reference.TypeArguments; }
            SyntaxNode ToExpression(SyntaxNode node) => node is QualifiedNameNode { Left: { } left, Right: { } right }
                ? Finish(factory.NewPropertyAccessExpression(ToExpression(left), null, right, 0), node.Pos, node.End) : node;
            if (expression is not null) expression = ToExpression(expression);
            var className = Finish(factory.NewExpressionWithTypeArguments(expression, arguments), parsedType?.Type?.Pos ?? pos, parsedType?.Type?.End ?? pos);
            var comments = Comments(pos, end);
            return tag == "implements" ? Finish(factory.NewJSDocImplementsTag(tagName, className, comments), start, end)
                : Finish(factory.NewJSDocAugmentsTag(tagName, className, comments), start, end);
        }
        if (tag is "param" or "arg" or "argument" or "property" or "prop")
        {
            type = Type(ref pos, end, true); nameFirst = type is null;
            pos = SkipSpace(pos, end);
            bracketed = pos < end && text[pos] == '['; if (bracketed) pos = SkipSpace(pos + 1, end);
            bool backquoted = pos < end && text[pos] == '`'; if (backquoted) pos++;
            name = Name(ref pos, end);
            if (backquoted && pos < end && text[pos] == '`') pos++;
            if (bracketed)
            {
                pos = SkipSpace(pos, end);
                if (pos < end && text[pos] == '=')
                {
                    // The default is source text rather than part of the type graph.
                    int close = text.AsSpan(pos, end - pos).IndexOf(']'); pos = close < 0 ? end : pos + close;
                }
                if (pos < end && text[pos] == ']') pos++;
                else Diagnostics.Add(new(Messages.X_0_expected, pos, 0, ["]"]));
            }
            if (nameFirst) type = Type(ref pos, end, true);
        }
        else if (tag is "type" or "this") type = Type(ref pos, end, false, true);
        else if (tag is "returns" or "return" or "throws" or "exception" or "typedef") type = Type(ref pos, end, true);
        else if (tag == "satisfies") type = Type(ref pos, end, false);
        else if (tag == "template")
        {
            type = Type(ref pos, end, true);
            int parameterStart = SkipSpace(pos, end); pos = parameterStart;
            var list = new List<SyntaxNode>();
            while (pos < end)
            {
                int current = pos;
                bool optional = text[pos] == '['; if (optional) pos++;
                IdentifierNode id = Identifier(ref pos, end); pos = SkipSpace(pos, end);
                int parameterEnd = id.End;
                SyntaxNode? defaultType = null;
                if (pos < end && text[pos] == '=')
                { pos++; var expression = Type(ref pos, end, false, true); defaultType = expression?.Type; parameterEnd = pos; }
                if (optional && pos < end && text[pos] == ']') parameterEnd = ++pos;
                list.Add(Finish(factory.NewTypeParameterDeclaration(null, id, null, null, defaultType), current, parameterEnd));
                if (pos >= end || text[pos] != ',') break;
                pos = SkipSpace(pos + 1, end);
            }
            parameters = new(list.ToArray(), parameterStart, list.Count == 0 ? parameterStart : list[^1].End);
        }
        if (tag is "typedef" or "callback")
        { pos = SkipSpace(pos, end); name = Name(ref pos, end); }
        if (tag == "see")
        {
            int possibleName = pos;
            while (possibleName < end && !char.IsWhiteSpace(text[possibleName])) possibleName++;
            if (!text.AsSpan(pos, possibleName - pos).Contains("://", StringComparison.Ordinal) && pos < end && TokenFacts.IsIdentifierStart(text[pos]))
            { int nameStart = pos; name = Finish(factory.NewJSDocNameReference(Name(ref pos, end)), nameStart, pos); }
        }
        int nodeEnd = end;
        NodeList? comment = Comments(pos, end);
        if (tag == "typedef" && comment is null) nodeEnd = name?.End ?? type?.End ?? tagName.End;
        SyntaxNode result = tag switch
        {
            "type" => factory.NewJSDocTypeTag(tagName, type, comment),
            "this" => factory.NewJSDocThisTag(tagName, type, comment),
            "return" or "returns" => factory.NewJSDocReturnTag(tagName, type, comment),
            "throws" or "exception" => factory.NewJSDocThrowsTag(tagName, type, comment),
            "satisfies" => factory.NewJSDocSatisfiesTag(tagName, type, comment),
            "param" or "arg" or "argument" or "property" or "prop" => factory.NewJSDocParameterOrPropertyTag(tag is "property" or "prop" ? K.JSDocPropertyTag : K.JSDocParameterTag, tagName, name, bracketed, type, nameFirst, comment),
            "template" => factory.NewJSDocTemplateTag(tagName, type, parameters, comment),
            "typedef" => factory.NewJSDocTypedefTag(tagName, type, name, comment),
            "callback" => factory.NewJSDocCallbackTag(tagName, null, name, comment),
            "overload" => factory.NewJSDocOverloadTag(tagName, null, comment),
            "see" => factory.NewJSDocSeeTag(tagName, name, comment),
            "public" => factory.NewJSDocPublicTag(tagName, comment),
            "private" => factory.NewJSDocPrivateTag(tagName, comment),
            "protected" => factory.NewJSDocProtectedTag(tagName, comment),
            "readonly" => factory.NewJSDocReadonlyTag(tagName, comment),
            "override" => factory.NewJSDocOverrideTag(tagName, comment),
            "deprecated" => factory.NewJSDocDeprecatedTag(tagName, comment),
            _ => factory.NewJSDocUnknownTag(tagName, comment),
        };
        return Finish(result, start, nodeEnd);
    }
    private void GroupTags(List<SyntaxNode> tags)
    {
        for (int i = 0; i < tags.Count; i++)
        {
            SyntaxNode tag = tags[i];
            if (tag is JSDocParameterOrPropertyTagNode { TypeExpression: JSDocTypeExpressionNode { Type: { } declaredType } } parentTag && IsObject(declaredType))
            {
                string parentName = FullName(parentTag.Name);
                var children = new List<SyntaxNode>();
                while (i + 1 < tags.Count && tags[i + 1] is JSDocParameterOrPropertyTagNode child && child.Kind == parentTag.Kind && FullName(child.Name).StartsWith(parentName + ".", StringComparison.Ordinal))
                { children.Add(child); tags.RemoveAt(i + 1); }
                if (children.Count != 0)
                {
                    GroupTags(children);
                    var literal = Finish(factory.NewJSDocTypeLiteral(children.ToArray(), declaredType is ArrayTypeNode), children[0].Pos, children[^1].End);
                    parentTag.TypeExpression = Finish(factory.NewJSDocTypeExpression(literal), literal.Pos, literal.End);
                    parentTag.TypeExpression.Parent = parentTag; parentTag.IsNameFirst = true; parentTag.End = literal.End;
                }
            }
            if (tag is JSDocTypedefTagNode typeDef && (typeDef.TypeExpression is null || typeDef.TypeExpression is JSDocTypeExpressionNode { Type: KeywordTypeNode { Kind: K.ObjectKeyword } } or JSDocTypeExpressionNode { Type: TypeReferenceNode { TypeName: IdentifierNode { Text: "Object" } } }))
            {
                var children = new List<SyntaxNode>();
                while (i + 1 < tags.Count && tags[i + 1].Kind == K.JSDocPropertyTag) { children.Add(tags[i + 1]); tags.RemoveAt(i + 1); }
                if (children.Count != 0)
                { typeDef.TypeExpression = Finish(factory.NewJSDocTypeLiteral(children.ToArray(), false), children[0].Pos, children[^1].End); typeDef.TypeExpression.Parent = typeDef; typeDef.End = children[^1].End; }
            }
            if (tag is JSDocCallbackTagNode or JSDocOverloadTagNode)
            {
                var parameters = new List<SyntaxNode>(); SyntaxNode? result = null;
                while (i + 1 < tags.Count && tags[i + 1].Kind is K.JSDocParameterTag or K.JSDocThisTag or K.JSDocReturnTag)
                { var child = tags[i + 1]; tags.RemoveAt(i + 1); if (child.Kind == K.JSDocReturnTag) result = child; else parameters.Add(child); }
                GroupTags(parameters);
                int start = parameters.Count != 0 ? parameters[0].Pos : tag.End;
                int end = result?.End ?? (parameters.Count != 0 ? parameters[^1].End : tag.End);
                ((ITypeExpressionNode)tag).TypeExpression = Finish(factory.NewJSDocSignature(null, new(parameters.ToArray(), start, end), result), start, end);
                ((ITypeExpressionNode)tag).TypeExpression!.Parent = tag; tag.End = end;
            }
        }
    }
    private static bool IsObject(SyntaxNode type) => type is KeywordTypeNode { Kind: K.ObjectKeyword } or TypeReferenceNode { TypeName: IdentifierNode { Text: "Object" }, TypeArguments: null }
        || type is ArrayTypeNode { ElementType: { } element } && IsObject(element);
    private static string FullName(SyntaxNode? node) => node switch
    {
        IdentifierNode identifier => identifier.Text,
        QualifiedNameNode qualified => FullName(qualified.Left) + "." + FullName(qualified.Right),
        _ => "",
    };
    private NodeList? Comments(int start, int end)
    {
        start = SkipSpace(start, end, false);
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        if (start >= end) return null;
        var nodes = new List<SyntaxNode>(); int cursor = start;
        while (cursor < end)
        {
            int relative = text.AsSpan(cursor, end - cursor).IndexOf("{@link", StringComparison.Ordinal);
            if (relative < 0) { AddText(cursor, end); break; }
            int link = cursor + relative;
            AddText(cursor, link);
            int pos = link + 2; int nameStart = pos;
            while (pos < end && char.IsAsciiLetter(text[pos])) pos++;
            string kind = text[nameStart..pos];
            int close = text.IndexOf('}', pos, end - pos); if (close < 0) close = end;
            pos = SkipSpace(pos, close); SyntaxNode? target = null;
            int targetEnd = pos;
            while (targetEnd < close && !char.IsWhiteSpace(text[targetEnd]) && text[targetEnd] != '|') targetEnd++;
            if (pos < targetEnd && !text.AsSpan(pos, targetEnd - pos).Contains("://", StringComparison.Ordinal) && TokenFacts.IsIdentifierStart(text[pos])) target = Name(ref pos, targetEnd);
            string[] value = [text[pos..close]];
            SyntaxNode node = kind switch { "linkcode" => factory.NewJSDocLinkCode(target, value), "linkplain" => factory.NewJSDocLinkPlain(target, value), _ => factory.NewJSDocLink(target, value) };
            cursor = close < end ? close + 1 : end;
            nodes.Add(Finish(node, link, cursor));
        }
        return nodes.Count == 0 ? null : new(nodes.ToArray(), start, end);
        void AddText(int from, int to)
        {
            if (from == to) return;
            string raw = text[from..to].Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            string[] lines = raw.Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                int star = 0; while (star < lines[i].Length && lines[i][star] is ' ' or '\t') star++;
                if (star < lines[i].Length && lines[i][star] == '*')
                { star++; if (star < lines[i].Length && lines[i][star] == ' ') star++; lines[i] = lines[i][star..]; }
            }
            nodes.Add(Finish(factory.NewJSDocText([string.Join('\n', lines)]), from, to));
        }
    }
}

using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

internal sealed class DocumentationParser(
    SourceText source,
    ScriptKind scriptKind,
    NodeFlags context = 0,
    CancellationToken cancellation = default)
{
    private readonly NodeFactory factory = new();
    private readonly string text = source.Text;
    private readonly NodeFlags flags = context | NodeFlags.JSDoc | (scriptKind is ScriptKind.JS or ScriptKind.JSX
        ? NodeFlags.JavaScriptFile
        : 0);
    private Scanner? identifierScanner;
    public List<Diagnostic> Diagnostics { get; } = [];
    public NodeFlags SourceFlags { get; private set; }

    public JSDocNode[] Leading(int start, int end, K hostKind = K.Unknown) => Parser.RunParse(LeadingAsync(start, end, hostKind));

    public async ValueTask<JSDocNode[]> LeadingAsync(int start, int end, K hostKind = K.Unknown)
    {
        var scanner = new Scanner(source, false);
        scanner.SetTextRange(Math.Max(0, start), source.Length);
        bool collect = start == 0
            || hostKind is K.Parameter or K.TypeParameter or K.FunctionExpression or K.ArrowFunction or K.ParenthesizedExpression
                or K.VariableDeclaration or K.ExportSpecifier;
        var comments = new List<JSDocNode>();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            K kind = scanner.Scan();
            if (kind == K.NewLineTrivia)
                collect = true;
            if (scanner.Position > end)
                break;
            if (collect
                && kind == K.MultiLineCommentTrivia
                && scanner.TokenText.StartsWith("/**", StringComparison.Ordinal)
                && !scanner.TokenText.StartsWith("/**/", StringComparison.Ordinal))
                comments.Add(await ParseAsync(scanner.TokenStart, scanner.Position).ConfigureAwait(false));
            else if (kind is not (K.WhitespaceTrivia or K.NewLineTrivia or K.SingleLineCommentTrivia or K.MultiLineCommentTrivia))
                break;
        }
        return comments.ToArray();
    }

    private T Finish<T>(T node, int start, int end) where T : SyntaxNode
    {
        node.Pos = start;
        node.End = end;
        node.Flags = flags;
        for (int i = 0; i < node.ChildCount; i++)
            node.GetChild(i).Parent = node;
        return node;
    }

    private async ValueTask<JSDocNode> ParseAsync(int start, int end)
    {
        int contentStart = start + 3, contentEnd = text.AsSpan(start, end - start).EndsWith("*/", StringComparison.Ordinal) ? end - 2 : end;
        var positions = new List<int>();
        int ticks = 0;
        bool fenced = false, quoted = false;
        for (int i = contentStart; i < contentEnd; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!fenced && TokenFacts.IsLineBreak(text[i]))
                quoted = false;
            if (text[i] == '`')
            {
                ticks = 1;
                while (i + 1 < contentEnd && text[i + 1] == '`')
                {
                    ticks++;
                    i++;
                }
                if (ticks >= 3)
                    fenced = !fenced;
                else if (!fenced)
                    quoted = !quoted;
                continue;
            }
            if (fenced || quoted || text[i] != '@' || i > contentStart && text[i - 1] == '{')
                continue;
            if (i != contentStart && !char.IsWhiteSpace(text[i - 1]) && text[i - 1] != '*')
                continue;
            if (i + 1 == contentEnd || TokenFacts.IsIdentifierStart(text[i + 1]) || char.IsWhiteSpace(text[i + 1]))
                positions.Add(i);
        }
        NodeList? comment = Comments(contentStart, positions.Count == 0 ? contentEnd : positions[0], true);
        var tags = new List<SyntaxNode>();
        int commentIndent = Column(SkipSpace(contentStart, contentEnd, false));
        for (int i = 0; i < positions.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            tags.Add(
                await TagAsync(positions[i], i + 1 < positions.Count ? positions[i + 1] : contentEnd, commentIndent).ConfigureAwait(false));
        }
        GroupTags(tags);
        var singletons = new HashSet<K>();
        foreach (SyntaxNode tag in tags)
            if (tag.Kind is K.JSDocReturnTag or K.JSDocTypeTag && !singletons.Add(tag.Kind))
            {
                IdentifierNode tagName = tag is JSDocReturnTagNode returns ? returns.TagName! : ((JSDocTypeTagNode)tag).TagName!;
                Diagnostics.Add(
                    new(Messages.X_0_tag_already_specified, tagName.Pos, SkipSpace(tagName.End, tag.End) - tagName.Pos, [tagName.Text]));
                tag.Flags |= NodeFlags.ThisNodeHasError;
            }
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
            if (char.IsWhiteSpace(ch))
            {
                newLine |= TokenFacts.IsLineBreak(ch);
                pos++;
            }
            else if (ch == '*' && newLine)
            {
                pos++;
                newLine = false;
            }
            else
                break;
        }
        return pos == end && preserveTrailing ? original : pos;
    }

    private IdentifierNode Identifier(ref int pos, int end, bool reportMissing = true)
    {
        int start = pos;
        Scanner scanner = identifierScanner ??= new Scanner(source, false);
        scanner.SetTextRange(pos, end);
        scanner.Diagnostics.Clear();
        K kind = scanner.ScanJSDocToken();
        string value = "";
        if (kind == K.Identifier || kind is >= K.FirstKeyword and <= K.LastKeyword)
        {
            pos = scanner.Position;
            value = scanner.Value;
        }
        Diagnostics.AddRange(scanner.Diagnostics);
        if (pos == start && reportMissing && (Diagnostics.Count == 0 || Diagnostics[^1].Start != pos))
            Diagnostics.Add(new(Messages.Identifier_expected, pos, 0, []));
        var node = Finish(factory.NewIdentifier(value), start, pos);
        if (pos == start && reportMissing)
            node.Flags |= NodeFlags.ThisNodeHasError;
        return node;
    }

    private SyntaxNode Name(ref int pos, int end, bool arrayQualifiers = false, bool reportMissing = true, bool linkName = false)
    {
        int start = pos;
        SyntaxNode name = Identifier(ref pos, end, reportMissing);
        if (arrayQualifiers && pos + 1 < end && text[pos] == '[' && text[pos + 1] == ']')
            pos += 2;
        while (pos < end && (text[pos] == '.' || !arrayQualifiers && text[pos] == '#'))
        {
            pos++;
            var right = Identifier(ref pos, end, !linkName || pos >= end || text[pos] != '#');
            if (arrayQualifiers && pos + 1 < end && text[pos] == '[' && text[pos + 1] == ']')
                pos += 2;
            name = Finish(factory.NewQualifiedName(name, right), start, pos);
        }
        return name;
    }

    private async ValueTask<(JSDocTypeExpressionNode? Type, int Position)> TypeAsync(
        int pos,
        int end,
        bool optional,
        bool mayOmitBraces = false)
    {
        pos = SkipSpace(pos, end);
        if (optional && (pos == end || text[pos] != '{' || pos + 1 < end && text[pos + 1] == '@'))
            return (null, pos);
        var result = await Parser.DocumentationTypeAsync(
            source,
            scriptKind,
            pos,
            end,
            mayOmitBraces,
            context,
            cancellation).ConfigureAwait(false);
        pos = result.End;
        Diagnostics.AddRange(result.Diagnostics);
        SourceFlags |= result.SourceFlags;
        return (result.Node, pos);
    }

    private async ValueTask<SyntaxNode> TagAsync(int start, int end, int docIndent)
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
            var imported = await Parser.DocumentationImportAsync(source, scriptKind, start + 1, end, cancellation).ConfigureAwait(false);
            Diagnostics.AddRange(imported.Diagnostics);
            NodeList? importedComment = Comments(imported.End, end);
            return Finish(
                factory.NewJSDocImportTag(
                    tagName,
                    imported.Node?.ImportClause,
                    imported.Node?.ModuleSpecifier,
                    imported.Node?.Attributes,
                    importedComment),
                start,
                end);
        }
        if (tag is "implements" or "augments" or "extends")
        {
            var parsed = await Parser.DocumentationHeritageAsync(source, scriptKind, pos, end, context, cancellation).ConfigureAwait(false);
            Diagnostics.AddRange(parsed.Diagnostics);
            SourceFlags |= parsed.SourceFlags;
            pos = parsed.End;
            var className = parsed.Node;
            var comments = Comments(pos, end);
            return tag == "implements" ? Finish(factory.NewJSDocImplementsTag(tagName, className, comments), start, end)
                : Finish(factory.NewJSDocAugmentsTag(tagName, className, comments), start, end);
        }
        if (tag is "param" or "arg" or "argument" or "property" or "prop")
        {
            (type, pos) = await TypeAsync(pos, end, true).ConfigureAwait(false);
            nameFirst = type is null;
            pos = SkipSpace(pos, end);
            bracketed = pos < end && text[pos] == '[';
            if (bracketed)
                pos = SkipSpace(pos + 1, end);
            bool backquoted = pos < end && text[pos] == '`';
            if (backquoted)
                pos++;
            name = Name(ref pos, end, true, tag is "property" or "prop");
            if (backquoted && pos < end && text[pos] == '`')
                pos++;
            if (bracketed)
            {
                pos = SkipSpace(pos, end);
                if (pos < end && text[pos] == '=')
                {
                    // The default is source text rather than part of the type graph.
                    int close = text.AsSpan(pos, end - pos).IndexOf(']');
                    pos = close < 0 ? end : pos + close;
                }
                if (pos < end && text[pos] == ']')
                    pos++;
                else
                    Diagnostics.Add(new(Messages.X_0_expected, pos, 0, ["]"]));
            }
            if (nameFirst)
                (type, pos) = await TypeAsync(pos, end, true).ConfigureAwait(false);
        }
        else if (tag is "type" or "this")
            (type, pos) = await TypeAsync(pos, end, false, true).ConfigureAwait(false);
        else if (tag is "returns" or "return" or "throws" or "exception" or "typedef")
            (type, pos) = await TypeAsync(pos, end, true).ConfigureAwait(false);
        else if (tag == "satisfies")
            (type, pos) = await TypeAsync(pos, end, false).ConfigureAwait(false);
        else if (tag == "template")
        {
            (type, pos) = await TypeAsync(pos, end, true).ConfigureAwait(false);
            int parameterStart = SkipSpace(pos, end);
            pos = parameterStart;
            var list = new List<SyntaxNode>();
            while (pos < end)
            {
                var parsed = await Parser.DocumentationTypeParameterAsync(source, scriptKind, pos, end, cancellation).ConfigureAwait(false);
                Diagnostics.AddRange(parsed.Diagnostics);
                SourceFlags |= parsed.SourceFlags;
                if (parsed.Node is not null)
                    list.Add(parsed.Node);
                pos = SkipSpace(parsed.End, end);
                if (pos >= end || text[pos] != ',')
                    break;
                pos = SkipSpace(pos + 1, end);
            }
            parameters = new(list.ToArray(), parameterStart, list.Count == 0 ? parameterStart : list[^1].End);
        }
        if (tag is "typedef" or "callback")
        {
            pos = SkipSpace(pos, end);
            name = NamespaceName(Name(ref pos, end));
        }
        if (tag == "see")
        {
            int nameStart = pos;
            bool braces = pos < end && text[pos] == '{';
            int identifierStart = braces ? SkipSpace(pos + 1, end) : pos;
            int possibleName = identifierStart;
            while (possibleName < end && !char.IsWhiteSpace(text[possibleName]))
                possibleName++;
            if (!text.AsSpan(identifierStart, possibleName - identifierStart).Contains("://", StringComparison.Ordinal)
                && StartsIdentifier(identifierStart, end))
            {
                pos = identifierStart;
                SyntaxNode target = Name(ref pos, end, linkName: true);
                if (braces)
                {
                    pos = SkipSpace(pos, end);
                    if (pos < end && text[pos] == '}')
                        pos++;
                    else
                        Diagnostics.Add(new(Messages.X_0_expected, pos, 0, ["}"]));
                }
                name = Finish(factory.NewJSDocNameReference(target), nameStart, pos);
            }
        }
        int nodeEnd = end;
        bool commentOnNextLine = text.AsSpan(pos, SkipSpace(pos, end, false) - pos).IndexOfAny("\r\n\u2028\u2029") >= 0;
        int? commentIndent = tag is "typedef" or "callback" or "overload" || commentOnNextLine ? docIndent : null;
        NodeList? comment = Comments(
            pos,
            end,
            preserveLineIndentation: type is not null && (type.Flags & NodeFlags.ThisNodeHasError) != 0,
            baseIndent: commentIndent);
        if (tag == "typedef" && comment is null)
            nodeEnd = name?.End ?? type?.End ?? tagName.End;
        SyntaxNode result = tag switch
        {
            "type" => factory.NewJSDocTypeTag(tagName, type, comment),
            "this" => factory.NewJSDocThisTag(tagName, type, comment),
            "return" or "returns" => factory.NewJSDocReturnTag(tagName, type, comment),
            "throws" or "exception" => factory.NewJSDocThrowsTag(tagName, type, comment),
            "satisfies" => factory.NewJSDocSatisfiesTag(tagName, type, comment),
            "param" or "arg" or "argument" or "property" or "prop" => factory.NewJSDocParameterOrPropertyTag(
                tag is "property" or "prop" ? K.JSDocPropertyTag : K.JSDocParameterTag,
                tagName,
                name,
                bracketed,
                type,
                nameFirst,
                comment),
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

    private SyntaxNode NamespaceName(SyntaxNode name)
    {
        if (name is not QualifiedNameNode)
            return name;
        var parts = new List<IdentifierNode>();
        var pending = new Stack<SyntaxNode>();
        pending.Push(name);
        while (pending.TryPop(out SyntaxNode? part))
        {
            if (part is QualifiedNameNode { Left: { } left, Right: { } right })
            {
                pending.Push(right);
                pending.Push(left);
            }
            else if (part is IdentifierNode identifier)
                parts.Add(identifier);
        }
        SyntaxNode? result = parts[^1].Text.Length == 0 ? null : parts[^1];
        if (result is not null)
            result.Flags |= NodeFlags.IdentifierIsInJSDocNamespace;
        for (int i = parts.Count - 2; i >= 0; i--)
        {
            result = Finish(factory.NewModuleDeclaration(null, K.NamespaceKeyword, parts[i], null, result), parts[i].Pos, name.End);
            if (i != 0)
                result.Flags |= NodeFlags.NestedNamespace;
        }
        return result!;
    }

    private void GroupTags(List<SyntaxNode> tags)
    {
        var grouped = new List<SyntaxNode>();
        for (int i = 0; i < tags.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            SyntaxNode tag = tags[i];
            grouped.Add(tag);
            if (tag is JSDocTypedefTagNode typeDef
                && (typeDef.TypeExpression is null
                    || typeDef.TypeExpression is JSDocTypeExpressionNode { Type: { } type } && IsObject(type)))
            {
                var children = new List<SyntaxNode>();
                JSDocTypeTagNode? childType = null;
                bool hasChildren = false;
                while (i + 1 < tags.Count
                    && tags[i + 1].Kind is K.JSDocPropertyTag or K.JSDocThisTag or K.JSDocTypeTag or K.JSDocTemplateTag)
                {
                    SyntaxNode child = tags[++i];
                    hasChildren = true;
                    if (child is JSDocTemplateTagNode template)
                        Diagnostics.Add(
                            new(
                                Messages.A_JSDoc_template_tag_may_not_follow_a_typedef_callback_or_overload_tag,
                                template.TagName!.Pos,
                                template.TagName.End - template.TagName.Pos,
                                []));
                    else if (child is JSDocTypeTagNode typed)
                    {
                        if (childType is null)
                            childType = typed;
                        else
                            Diagnostics.Add(
                                new(
                                    Messages.A_JSDoc_typedef_comment_may_not_contain_multiple_type_tags,
                                    typed.Pos,
                                    typed.End - typed.Pos,
                                    []));
                    }
                    else
                        children.Add(child);
                }
                if (hasChildren)
                {
                    bool array = typeDef.TypeExpression is JSDocTypeExpressionNode { Type: ArrayTypeNode };
                    children = GroupPropertyTags(children);
                    typeDef.TypeExpression = childType?.TypeExpression is JSDocTypeExpressionNode { Type: { } declared } explicitType
                        && !IsObject(declared)
                        ? explicitType : Finish(
                            factory.NewJSDocTypeLiteral(children.ToArray(), array),
                            children.Count == 0 ? typeDef.Pos : children[0].Pos,
                            tags[i].End);
                    typeDef.TypeExpression.Parent = typeDef;
                    typeDef.End = typeDef.TypeExpression.End;
                    if (typeDef.Comment is null && childType?.Comment is { } description)
                    {
                        typeDef.Comment = description;
                        foreach (SyntaxNode part in description)
                            part.Parent = typeDef;
                    }
                }
            }
            else if (tag is JSDocCallbackTagNode or JSDocOverloadTagNode)
            {
                var parameters = new List<SyntaxNode>();
                SyntaxNode? result = null;
                while (i + 1 < tags.Count && tags[i + 1].Kind is K.JSDocParameterTag or K.JSDocThisTag or K.JSDocTemplateTag)
                {
                    SyntaxNode child = tags[++i];
                    if (child is JSDocTemplateTagNode template)
                        Diagnostics.Add(
                            new(
                                Messages.A_JSDoc_template_tag_may_not_follow_a_typedef_callback_or_overload_tag,
                                template.TagName!.Pos,
                                template.TagName.End - template.TagName.Pos,
                                []));
                    else
                        parameters.Add(child);
                }
                if (i + 1 < tags.Count && tags[i + 1].Kind == K.JSDocReturnTag)
                    result = tags[++i];
                parameters = GroupPropertyTags(parameters);
                int start = parameters.Count != 0 ? parameters[0].Pos : tag.End;
                int parametersEnd = parameters.Count != 0 ? parameters[^1].End : tag.End;
                int end = result?.End ?? parametersEnd;
                var signature = Finish(
                    factory.NewJSDocSignature(null, new(parameters.ToArray(), start, parametersEnd), result),
                    start,
                    end);
                ((ITypeExpressionNode)tag).TypeExpression = signature;
                signature.Parent = tag;
                tag.End = end;
            }
            else if (tag is JSDocParameterOrPropertyTagNode { Kind: K.JSDocParameterTag })
            {
                var properties = new List<SyntaxNode> { tag };
                while (i + 1 < tags.Count && tags[i + 1].Kind == K.JSDocParameterTag)
                    properties.Add(tags[++i]);
                grouped.RemoveAt(grouped.Count - 1);
                grouped.AddRange(GroupPropertyTags(properties));
            }
            else if (tag is JSDocParameterOrPropertyTagNode { Kind: K.JSDocPropertyTag } property)
                grouped[^1] = Finish(
                    factory.NewJSDocUnknownTag(property.TagName, Comments(property.TagName!.End, property.End)),
                    property.Pos,
                    property.End);
        }
        tags.Clear();
        tags.AddRange(grouped);
    }

    private List<SyntaxNode> GroupPropertyTags(List<SyntaxNode> tags)
    {
        var result = new List<SyntaxNode>();
        var parents = new Stack<(JSDocParameterOrPropertyTagNode Tag, SyntaxNode Type, string Prefix, List<SyntaxNode> Children)>();
        foreach (SyntaxNode tag in tags)
        {
            cancellation.ThrowIfCancellationRequested();
            string? name = tag is JSDocParameterOrPropertyTagNode property ? SyntaxNameText.Get(property.Name, false) : null;
            while (parents.TryPeek(out var parent)
                && (tag.Kind != parent.Tag.Kind
                    || name is null
                    || !name.StartsWith(parent.Prefix, StringComparison.Ordinal)
                    || name.AsSpan(parent.Prefix.Length).Contains('.')))
                Complete();
            (parents.TryPeek(out var current) ? current.Children : result).Add(tag);
            if (tag is JSDocParameterOrPropertyTagNode { TypeExpression: JSDocTypeExpressionNode { Type: { } declaredType } } parentTag
                && IsObject(declaredType))
                parents.Push((parentTag, declaredType, name + ".", []));
        }
        while (parents.Count != 0)
            Complete();
        return result;
        void Complete()
        {
            var parent = parents.Pop();
            if (parent.Children.Count == 0)
                return;
            var literal = Finish(
                factory.NewJSDocTypeLiteral(parent.Children.ToArray(), parent.Type is ArrayTypeNode),
                parent.Children[0].Pos,
                parent.Children[^1].End);
            parent.Tag.TypeExpression = Finish(factory.NewJSDocTypeExpression(literal), literal.Pos, literal.End);
            parent.Tag.TypeExpression.Parent = parent.Tag;
            parent.Tag.IsNameFirst = true;
            parent.Tag.End = literal.End;
        }
    }

    private static bool IsObject(SyntaxNode type)
    {
        while (type is ArrayTypeNode { ElementType: { } element })
            type = element;
        return type is KeywordTypeNode { Kind: K.ObjectKeyword }
            or TypeReferenceNode { TypeName: IdentifierNode { Text: "Object" }, TypeArguments: null };
    }

    private bool StartsIdentifier(int pos, int end)
    {
        if (pos >= end)
            return false;
        int point = char.IsHighSurrogate(text[pos]) && pos + 1 < end && char.IsLowSurrogate(text[pos + 1])
            ? char.ConvertToUtf32(text[pos], text[pos + 1])
            : text[pos];
        return point == '\\' || TokenFacts.IsIdentifierStart(point);
    }

    private int Column(int pos)
    {
        int start = pos;
        while (start > 0 && !TokenFacts.IsLineBreak(text[start - 1]))
            start--;
        return pos - start;
    }

    private NodeList? Comments(int start, int end, bool fullComment = false, bool preserveLineIndentation = false, int? baseIndent = null)
    {
        int untrimmedStart = start;
        start = SkipSpace(start, end, false);
        int margin = baseIndent ?? Column(start), leadingLines = 0;
        int initialPadding = baseIndent is not null
            && text.AsSpan(untrimmedStart, start - untrimmedStart).IndexOfAny("\r\n\u2028\u2029") >= 0
            ? Math.Max(0, Column(start) - margin)
            : 0;
        if (fullComment)
            for (int i = untrimmedStart; i < start; i++)
                if (TokenFacts.IsLineBreak(text[i]))
                {
                    leadingLines++;
                    if (text[i] == '\r' && i + 1 < start && text[i + 1] == '\n')
                        i++;
                }
        int rawEnd = end;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;
        if (start >= end)
            return null;
        var nodes = new List<SyntaxNode>();
        int cursor = start;
        while (cursor < end)
        {
            int relative = text.AsSpan(cursor, end - cursor).IndexOf("{@link", StringComparison.Ordinal);
            if (relative < 0)
            {
                AddText(cursor, end);
                break;
            }
            int link = cursor + relative;
            int pos = link + 2;
            int nameStart = pos;
            while (pos < end && char.IsAsciiLetter(text[pos]))
                pos++;
            string kind = text[nameStart..pos];
            if (kind is not ("link" or "linkcode" or "linkplain"))
            {
                AddText(cursor, pos);
                cursor = pos;
                continue;
            }
            AddText(cursor, link, true);
            int argumentsStart = pos;
            int close = text.IndexOf('}', pos, rawEnd - pos);
            bool terminated = close >= 0;
            if (!terminated)
                close = rawEnd;
            pos = SkipSpace(pos, close);
            SyntaxNode? target = null;
            int targetEnd = pos;
            while (targetEnd < close && !char.IsWhiteSpace(text[targetEnd]) && text[targetEnd] != '|')
                targetEnd++;
            if (pos < targetEnd
                && !text.AsSpan(pos, targetEnd - pos).Contains("://", StringComparison.Ordinal)
                && StartsIdentifier(pos, targetEnd))
                target = Name(ref pos, targetEnd, linkName: true);
            if (target is not null)
                pos = SkipSpace(pos, close);
            string[] value = [text[(!terminated && target is null ? argumentsStart : pos)..close]];
            SyntaxNode node = kind switch
            {
                "linkcode" => factory.NewJSDocLinkCode(target, value),
                "linkplain" => factory.NewJSDocLinkPlain(target, value),
                _ => factory.NewJSDocLink(target, value)
            };
            cursor = close < end ? close + 1 : end;
            nodes.Add(Finish(node, link, cursor));
        }
        if (fullComment && nodes.Count != 0 && nodes[^1].Kind is K.JSDocLink or K.JSDocLinkCode or K.JSDocLinkPlain)
            AddText(end, end, true);
        return nodes.Count == 0 ? null : new(nodes.ToArray(), start, end);
        void AddText(int from, int to, bool force = false)
        {
            if (from == to && !force)
                return;
            string raw = text[from..to].Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            string[] lines = raw.Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                int star = 0;
                while (star < lines[i].Length && lines[i][star] is ' ' or '\t')
                    star++;
                int prefix = star;
                if (star < lines[i].Length && lines[i][star] == '*')
                {
                    prefix++;
                    while (prefix < lines[i].Length && lines[i][prefix] is ' ' or '\t')
                        prefix++;
                    if (fullComment && !preserveLineIndentation)
                    {
                        string beforeStar = star > margin ? lines[i][margin..star] : "";
                        int skip = margin - star - 1;
                        if (skip < 0)
                            skip += prefix - star - 1;
                        skip = Math.Clamp(skip, 0, prefix - star - 1);
                        lines[i] = beforeStar + lines[i][(star + 1 + skip)..];
                    }
                    else
                    {
                        int remove = preserveLineIndentation
                            ? star + 1 + (star + 1 < lines[i].Length && lines[i][star + 1] == ' ' ? 1 : 0)
                            : Math.Max(star + 1, Math.Min(prefix, margin));
                        lines[i] = lines[i][remove..];
                    }
                }
                else
                    lines[i] = lines[i][Math.Min(prefix, margin)..];
            }
            string value = string.Join('\n', lines);
            if (nodes.Count == 0 && initialPadding != 0)
                value = new string(' ', initialPadding) + value;
            if (nodes.Count == 0 && leadingLines > 1)
                value = new string('\n', leadingLines - 1) + value;
            if (to == end)
                value = value.TrimEnd();
            if (value.Length == 0 && !force)
                return;
            nodes.Add(Finish(factory.NewJSDocText([value]), from, to));
        }
    }
}

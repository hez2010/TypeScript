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
    private readonly Utf8String text = source.Text;
    private readonly NodeFlags flags = context | NodeFlags.JSDoc | (scriptKind is ScriptKind.JS or ScriptKind.JSX
        ? NodeFlags.JavaScriptFile
        : 0);
    private Scanner? identifierScanner;
    public List<Diagnostic> Diagnostics { get; } = [];
    public NodeFlags SourceFlags { get; private set; }

    private int Point(int position) => Wtf8.Decode(text.Span[position..], out _);
    private int PreviousPoint(int position) => Wtf8.DecodeLast(text.Span[..position], out _);
    private static bool WhiteSpace(int point) => Rune.IsValid(point) && Rune.IsWhiteSpace(new Rune(point));

    public JSDocNode[] Leading(int start, int end, K hostKind = K.Unknown) => Parser.RunParse(LeadingAsync(start, end, hostKind));

    public async ValueTask<JSDocNode[]> LeadingAsync(int start, int end, K hostKind = K.Unknown)
    {
        var scanner = new Scanner(source, false);
        scanner.SetTextRange(Math.Max(0, start), source.Length);
        bool collect = start == 0
            || hostKind is K.Parameter or K.TypeParameter or K.FunctionExpression or K.ArrowFunction or K.ParenthesizedExpression
                or K.VariableDeclaration or K.ExportSpecifier;
        var comments = new List<JSDocNode>();
        int fullStart = Math.Max(0, start);
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
                && scanner.TokenText.StartsWith("/**"u8, StringComparison.Ordinal)
                && !scanner.TokenText.StartsWith("/**/"u8, StringComparison.Ordinal))
            {
                comments.Add(await ParseAsync(scanner.TokenStart, scanner.Position, fullStart).ConfigureAwait(false));
                fullStart = scanner.Position;
            }
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

    private async ValueTask<JSDocNode> ParseAsync(int start, int end, int fullStart)
    {
        int contentStart = start + 3, contentEnd = text.Span.Slice(start, end - start).EndsWith("*/"u8, StringComparison.Ordinal) ? end - 2 : end;
        var positions = new List<int>();
        int ticks = 0;
        bool fenced = false, quoted = false;
        for (int i = contentStart; i < contentEnd; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!fenced && TokenFacts.IsLineBreak(Point(i)))
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
            if (i != contentStart && !WhiteSpace(PreviousPoint(i)) && text[i - 1] != '*')
                continue;
            if (i + 1 == contentEnd || TokenFacts.IsIdentifierStart(Point(i + 1)) || WhiteSpace(Point(i + 1)))
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
        return Finish(factory.NewJSDoc(comment, tagList), fullStart, end);
    }

    private int SkipSpace(int pos, int end, bool preserveTrailing = true)
    {
        int original = pos;
        bool newLine = false;
        while (pos < end)
        {
            int ch = Wtf8.Decode(text.Span[pos..end], out int width);
            if (WhiteSpace(ch))
            {
                newLine |= TokenFacts.IsLineBreak(ch);
                pos += width;
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
        Utf8String value = default;
        if (kind == K.Identifier || kind is >= K.FirstKeyword and <= K.LastKeyword)
        {
            pos = scanner.Position;
            value = scanner.Value;
        }
        Diagnostics.AddRange(scanner.Diagnostics);
        if (pos == start && reportMissing && (Diagnostics.Count == 0 || Diagnostics[^1].Start != pos))
            Diagnostics.Add(new(Messages.Identifier_expected, scanner.TokenStart, scanner.Position - scanner.TokenStart, []));
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
        Utf8String tag = tagName.Text;
        if (tag == Utf8Literals.ImportKeyword)
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
                importedComment is null && imported.Diagnostics.Length == 0 && (end == text.Length || text[end] != '@') ? imported.End : end);
        }
        if (tag.Span.SequenceEqual("implements"u8) || tag.Span.SequenceEqual("augments"u8) || tag.Span.SequenceEqual("extends"u8))
        {
            var parsed = await Parser.DocumentationHeritageAsync(source, scriptKind, pos, end, context, cancellation).ConfigureAwait(false);
            Diagnostics.AddRange(parsed.Diagnostics);
            SourceFlags |= parsed.SourceFlags;
            pos = parsed.End;
            var className = parsed.Node;
            var comments = Comments(pos, end);
            return tag == Utf8Literals.Implements ? Finish(factory.NewJSDocImplementsTag(tagName, className, comments), start, end)
                : Finish(factory.NewJSDocAugmentsTag(tagName, className, comments), start, end);
        }
        if (tag.Span.SequenceEqual("param"u8) || tag.Span.SequenceEqual("arg"u8) || tag.Span.SequenceEqual("argument"u8) || tag.Span.SequenceEqual("property"u8) || tag.Span.SequenceEqual("prop"u8))
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
            name = Name(ref pos, end, true, tag.Span.SequenceEqual("property"u8) || tag.Span.SequenceEqual("prop"u8));
            if (backquoted && pos < end && text[pos] == '`')
                pos++;
            if (bracketed)
            {
                pos = SkipSpace(pos, end);
                if (pos < end && text[pos] == '=')
                {
                    // The default is source text rather than part of the type graph.
                    int close = text.Span.Slice(pos, end - pos).IndexOf((byte)']');
                    pos = close < 0 ? end : pos + close;
                }
                if (pos < end && text[pos] == ']')
                    pos++;
                else
                    Diagnostics.Add(new(Messages.X_0_expected, pos, 0, [Utf8Literals.CloseBracket]));
            }
            if (nameFirst)
                (type, pos) = await TypeAsync(pos, end, true).ConfigureAwait(false);
        }
        else if (tag.Span.SequenceEqual("type"u8) || tag.Span.SequenceEqual("this"u8))
            (type, pos) = await TypeAsync(pos, end, false, true).ConfigureAwait(false);
        else if (tag.Span.SequenceEqual("returns"u8) || tag.Span.SequenceEqual("return"u8) || tag.Span.SequenceEqual("throws"u8) || tag.Span.SequenceEqual("exception"u8) || tag.Span.SequenceEqual("typedef"u8))
            (type, pos) = await TypeAsync(pos, end, true).ConfigureAwait(false);
        else if (tag == Utf8Literals.SatisfiesKeyword)
            (type, pos) = await TypeAsync(pos, end, false).ConfigureAwait(false);
        else if (tag == Utf8Literals.Template)
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
        if (tag.Span.SequenceEqual("typedef"u8) || tag.Span.SequenceEqual("callback"u8))
        {
            pos = SkipSpace(pos, end);
            name = NamespaceName(Name(ref pos, end));
        }
        if (tag == Utf8Literals.See)
        {
            int nameStart = pos;
            bool braces = pos < end && text[pos] == '{';
            int identifierStart = braces ? SkipSpace(pos + 1, end) : pos;
            int possibleName = identifierStart;
            while (possibleName < end && !WhiteSpace(Point(possibleName)))
                possibleName++;
            if (!text.Span.Slice(identifierStart, possibleName - identifierStart).Contains("://"u8, StringComparison.Ordinal)
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
                        Diagnostics.Add(new(Messages.X_0_expected, pos, 0, [Utf8Literals.CloseBrace]));
                }
                name = Finish(factory.NewJSDocNameReference(target), nameStart, pos);
            }
        }
        int nodeEnd = end;
        bool commentOnNextLine = text.Span.Slice(pos, SkipSpace(pos, end, false) - pos).ContainsLineBreak();
        int? commentIndent = tag.Span.SequenceEqual("typedef"u8) || tag.Span.SequenceEqual("callback"u8) || tag.Span.SequenceEqual("overload"u8) || commentOnNextLine ? docIndent : null;
        NodeList? comment = Comments(
            pos,
            end,
            preserveLineIndentation: type is not null && (type.Flags & NodeFlags.ThisNodeHasError) != 0,
            baseIndent: commentIndent,
            rangeStart: (tag.Span.SequenceEqual("param"u8) || tag.Span.SequenceEqual("arg"u8) || tag.Span.SequenceEqual("argument"u8) || tag.Span.SequenceEqual("property"u8) || tag.Span.SequenceEqual("prop"u8)) && (type is null || !nameFirst)
                ? SkipSpace(pos, end, false) : null);
        if (tag == Utf8Literals.Typedef && comment is null)
            nodeEnd = name?.End ?? type?.End ?? tagName.End;
        SyntaxNode result = tag.Span switch
        {
            _ when tag.Span.SequenceEqual("type"u8) => factory.NewJSDocTypeTag(tagName, type, comment),
            _ when tag.Span.SequenceEqual("this"u8) => factory.NewJSDocThisTag(tagName, type, comment),
            _ when tag.Span.SequenceEqual("return"u8) || tag.Span.SequenceEqual("returns"u8) => factory.NewJSDocReturnTag(tagName, type, comment),
            _ when tag.Span.SequenceEqual("throws"u8) || tag.Span.SequenceEqual("exception"u8) => factory.NewJSDocThrowsTag(tagName, type, comment),
            _ when tag.Span.SequenceEqual("satisfies"u8) => factory.NewJSDocSatisfiesTag(tagName, type, comment),
            _ when tag.Span.SequenceEqual("param"u8) || tag.Span.SequenceEqual("arg"u8) || tag.Span.SequenceEqual("argument"u8) || tag.Span.SequenceEqual("property"u8) || tag.Span.SequenceEqual("prop"u8) => factory.NewJSDocParameterOrPropertyTag(
                            (tag.Span.SequenceEqual("property"u8) || tag.Span.SequenceEqual("prop"u8)) ? K.JSDocPropertyTag : K.JSDocParameterTag,
                            tagName,
                            name,
                            bracketed,
                            type,
                            nameFirst,
                            comment),
            _ when tag.Span.SequenceEqual("template"u8) => factory.NewJSDocTemplateTag(tagName, type, parameters, comment),
            _ when tag.Span.SequenceEqual("typedef"u8) => factory.NewJSDocTypedefTag(tagName, type, name, comment),
            _ when tag.Span.SequenceEqual("callback"u8) => factory.NewJSDocCallbackTag(tagName, null, name, comment),
            _ when tag.Span.SequenceEqual("overload"u8) => factory.NewJSDocOverloadTag(tagName, null, comment),
            _ when tag.Span.SequenceEqual("see"u8) => factory.NewJSDocSeeTag(tagName, name, comment),
            _ when tag.Span.SequenceEqual("public"u8) => factory.NewJSDocPublicTag(tagName, comment),
            _ when tag.Span.SequenceEqual("private"u8) => factory.NewJSDocPrivateTag(tagName, comment),
            _ when tag.Span.SequenceEqual("protected"u8) => factory.NewJSDocProtectedTag(tagName, comment),
            _ when tag.Span.SequenceEqual("readonly"u8) => factory.NewJSDocReadonlyTag(tagName, comment),
            _ when tag.Span.SequenceEqual("override"u8) => factory.NewJSDocOverrideTag(tagName, comment),
            _ when tag.Span.SequenceEqual("deprecated"u8) => factory.NewJSDocDeprecatedTag(tagName, comment),
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
                                    typed.TypeExpression!.End,
                                    1,
                                    [])
                                { RelatedInformation = [new(Messages.The_tag_was_first_specified_here, 0, 0, [])] });
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
        var parents = new Stack<(JSDocParameterOrPropertyTagNode Tag, SyntaxNode Type, Utf8String Prefix, List<SyntaxNode> Children)>();
        foreach (SyntaxNode tag in tags)
        {
            cancellation.ThrowIfCancellationRequested();
            Utf8String? name = tag is JSDocParameterOrPropertyTagNode property ? SyntaxNameText.Get(property.Name, false) : (Utf8String?)null;
            while (parents.TryPeek(out var parent)
                && (tag.Kind != parent.Tag.Kind
                    || name is null
                    || !name.Value.Span.StartsWith(parent.Prefix, StringComparison.Ordinal)
                    || name.Value.Span.Slice(parent.Prefix.Length).Contains((byte)'.')))
                Complete();
            (parents.TryPeek(out var current) ? current.Children : result).Add(tag);
            if (tag is JSDocParameterOrPropertyTagNode { TypeExpression: JSDocTypeExpressionNode { Type: { } declaredType } } parentTag
                && IsObject(declaredType))
                parents.Push((parentTag, declaredType, Utf8String.Concat(name!.Value, "."u8), []));
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
        return type is KeywordTypeNode { Kind: K.ObjectKeyword } || type is TypeReferenceNode { TypeName: IdentifierNode { Text.Span: var matchedText }, TypeArguments: null } && matchedText.SequenceEqual("Object"u8);
    }

    private bool StartsIdentifier(int pos, int end)
    {
        if (pos >= end)
            return false;
        int point = Point(pos);
        return point == '\\' || TokenFacts.IsIdentifierStart(point);
    }

    private int Column(int pos)
    {
        int start = pos;
        while (start > 0)
        {
            int point = Wtf8.DecodeLast(text.Span[..start], out int width);
            if (TokenFacts.IsLineBreak(point))
                break;
            start -= width;
        }
        return pos - start;
    }

    private NodeList? Comments(int start, int end, bool fullComment = false, bool preserveLineIndentation = false, int? baseIndent = null,
        int? rangeStart = null)
    {
        int untrimmedStart = start;
        start = SkipSpace(start, end, false);
        int margin = baseIndent ?? Column(start), leadingLines = 0;
        int initialPadding = baseIndent is not null
            && text.Span.Slice(untrimmedStart, start - untrimmedStart).ContainsLineBreak()
            ? Math.Max(0, Column(start) - margin)
            : 0;
        if (fullComment)
            for (int i = untrimmedStart; i < start;)
            {
                int point = Wtf8.Decode(text.Span[i..start], out int width);
                i += width;
                if (TokenFacts.IsLineBreak(point))
                {
                    leadingLines++;
                    if (point == '\r' && i < start && text[i] == '\n')
                        i++;
                }
            }
        int rawEnd = end;
        end = start + text.Span[start..end].TrimEnd().Length;
        if (start >= end)
            return null;
        var nodes = new List<SyntaxNode>();
        int cursor = start;
        while (cursor < end)
        {
            int relative = text.Span.Slice(cursor, end - cursor).IndexOf("{@link"u8, StringComparison.Ordinal);
            if (relative < 0)
            {
                AddText(cursor, end);
                break;
            }
            int link = cursor + relative;
            int pos = link + 2;
            int nameStart = pos;
            while (pos < end && Utf8Ascii.IsLetter(text[pos]))
                pos++;
            Utf8String kind = text[nameStart..pos];
            if (!(kind.Span.SequenceEqual("link"u8) || kind.Span.SequenceEqual("linkcode"u8) || kind.Span.SequenceEqual("linkplain"u8)))
            {
                AddText(cursor, pos);
                cursor = pos;
                continue;
            }
            AddText(cursor, link, true);
            int argumentsStart = pos;
            int close = text.Span.Slice(pos, rawEnd - pos).IndexOf((byte)'}');
            if (close >= 0)
                close += pos;
            bool terminated = close >= 0;
            if (!terminated)
                close = rawEnd;
            pos = SkipSpace(pos, close);
            SyntaxNode? target = null;
            int targetEnd = pos;
            while (targetEnd < close && !WhiteSpace(Point(targetEnd)) && text[targetEnd] != '|')
                targetEnd++;
            if (pos < targetEnd
                && !text.Span.Slice(pos, targetEnd - pos).Contains("://"u8, StringComparison.Ordinal)
                && StartsIdentifier(pos, targetEnd))
                target = Name(ref pos, targetEnd, linkName: true);
            if (target is not null)
                pos = SkipSpace(pos, close);
            Utf8String[] value = [text.Memory[(!terminated && target is null ? argumentsStart : pos)..close]];
            SyntaxNode node = kind.Span switch
            {
                _ when kind.Span.SequenceEqual("linkcode"u8) => factory.NewJSDocLinkCode(target, value),
                _ when kind.Span.SequenceEqual("linkplain"u8) => factory.NewJSDocLinkPlain(target, value),
                _ => factory.NewJSDocLink(target, value)
            };
            cursor = close < end ? close + 1 : end;
            nodes.Add(Finish(node, link, cursor));
        }
        if (fullComment && nodes.Count != 0 && nodes[^1].Kind is K.JSDocLink or K.JSDocLinkCode or K.JSDocLinkPlain)
            AddText(end, end, true);
        return nodes.Count == 0 ? null : new(nodes.ToArray(), rangeStart ?? (fullComment ? untrimmedStart - 3 : untrimmedStart), rawEnd);
        void AddText(int from, int to, bool force = false)
        {
            if (from == to && !force)
                return;
            ReadOnlySpan<byte> raw = text.Span.Slice(from, to - from);
            Utf8String value;
            if (!raw.ContainsAny((byte)'\r', (byte)'\n') && (nodes.Count != 0 || initialPadding == 0 && leadingLines <= 1))
                value = text.Memory.Slice(from, to == end ? raw.TrimEnd().Length : raw.Length);
            else
            {
                var result = new Utf8StringBuilder(raw.Length);
                if (nodes.Count == 0)
                {
                    if (leadingLines > 1)
                        result.Append((byte)'\n', leadingLines - 1);
                    result.Append((byte)' ', initialPadding);
                }
                bool first = true;
                while (true)
                {
                    int lineEnd = raw.IndexOfAny((byte)'\r', (byte)'\n');
                    ReadOnlySpan<byte> line = lineEnd < 0 ? raw : raw[..lineEnd];
                    if (!first)
                    {
                        result.Append((byte)'\n');
                        int star = 0;
                        while (star < line.Length && line[star] is (byte)' ' or (byte)'\t')
                            star++;
                        int prefix = star;
                        if (star < line.Length && line[star] == '*')
                        {
                            prefix++;
                            while (prefix < line.Length && line[prefix] is (byte)' ' or (byte)'\t')
                                prefix++;
                            if (fullComment && !preserveLineIndentation)
                            {
                                if (star > margin)
                                    result.Append(line[margin..star]);
                                int skip = margin - star - 1;
                                if (skip < 0)
                                    skip += prefix - star - 1;
                                skip = Math.Clamp(skip, 0, prefix - star - 1);
                                line = line[(star + 1 + skip)..];
                            }
                            else
                            {
                                int remove = preserveLineIndentation
                                    ? star + 1 + (star + 1 < line.Length && line[star + 1] == ' ' ? 1 : 0)
                                    : Math.Max(star + 1, Math.Min(prefix, margin));
                                line = line[remove..];
                            }
                        }
                        else
                            line = line[Math.Min(prefix, margin)..];
                    }
                    result.Append(line);
                    if (lineEnd < 0)
                        break;
                    int skipBreak = raw[lineEnd] == '\r' && lineEnd + 1 < raw.Length && raw[lineEnd + 1] == '\n' ? 2 : 1;
                    raw = raw[(lineEnd + skipBreak)..];
                    first = false;
                }
                if (to == end)
                    result.Length = result.WrittenSpan.TrimEnd().Length;
                value = Utf8String.FromBuilder(result);
            }
            if (value.Length == 0 && !force)
                return;
            int nodeStart = nodes.Count == 0 ? rangeStart ?? (fullComment ? untrimmedStart - 3 : untrimmedStart) : from;
            nodes.Add(Finish(factory.NewJSDocText([value]), nodeStart, to == end ? rawEnd : to));
        }
    }
}

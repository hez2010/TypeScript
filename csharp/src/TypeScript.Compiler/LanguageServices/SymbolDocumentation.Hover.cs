using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.LanguageServices;

internal delegate (Utf8String Uri, DocumentRange Range, MappingFidelity Fidelity) DocumentationLocationMapper(SourceFileNode file, int start, int end);

internal sealed partial class SymbolDocumentation
{
    internal async ValueTask<Utf8String> HoverAsync(Symbol? symbol, SyntaxNode node, SyntaxNode? declaration,
        bool markdown, bool commentOnly, DocumentationLocationMapper mapper, CancellationToken cancellation)
    {
        if (SymbolDisplay.CallOrNew(node) is { } call
            && (await checker.GetResolvedSignatureAsync(call, cancellation)).Declaration is CallSignatureDeclarationNode or ConstructSignatureDeclarationNode
            && await FromDeclarationAsync((await checker.GetResolvedSignatureAsync(call, cancellation)).Declaration, markdown, commentOnly, mapper, cancellation) is { IsEmpty: false } callDoc)
            return callDoc;
        if (symbol is not null && await checker.GetRootSymbolsAsync(symbol, cancellation) is { Count: > 1 } roots)
        {
            List<Utf8String> docs = [];
            foreach (var root in roots)
                foreach (var owner in root.Declarations.Count == 0 && root.ValueDeclaration is { } value ? [value] : root.Declarations.ToArray())
                    if (await FromDeclarationAsync(owner, markdown, commentOnly, mapper, cancellation) is { IsEmpty: false } doc && !docs.Contains(doc)) docs.Add(doc);
            if (docs.Count != 0) return Utf8String.Join("\n"u8, docs);
        }
        if (await FromDeclarationAsync(declaration, markdown, commentOnly, mapper, cancellation) is { IsEmpty: false } direct) return direct;
        if (symbol is not null && (symbol.Flags & SymbolFlags.Alias) != 0)
        {
            var target = await checker.GetAliasedSymbolAsync(symbol, cancellation);
            if (target != checker.UnknownSymbol)
                foreach (var candidate in target.ExportSymbol is { } exported ? new[] { target, exported } : [target])
                    if (await FromDeclarationAsync(candidate.ValueDeclaration ?? candidate.Declarations.FirstOrDefault(), markdown, commentOnly, mapper, cancellation) is { IsEmpty: false } doc)
                        return doc;
        }
        return default;
    }

    internal async ValueTask<Utf8String> FromDeclarationAsync(SyntaxNode? declaration, bool markdown, bool commentOnly,
        DocumentationLocationMapper mapper, CancellationToken cancellation)
    {
        if (declaration is null || await FindAsync(declaration, [], cancellation) is not { } doc
            || (declaration.Flags & NodeFlags.Reparsed) == 0 && doc is JSDocNode { Tags: { } tags }
                && tags.Any(tag => tag is JSDocTypedefTagNode or JSDocCallbackTagNode)) return default;
        var text = new Utf8StringBuilder();
        await WriteCommentsAsync(text, doc.DocumentationComment, markdown, mapper, cancellation);
        if (commentOnly || doc is not JSDocNode { Tags: { } allTags }) return text.ToUtf8String();
        foreach (var tag in allTags)
        {
            if (tag is JSDocTypeTagNode or JSDocTypedefTagNode or JSDocCallbackTagNode) continue;
            text.Append(markdown ? "\n\n*@"u8 : "\n\n@"u8);
            var tagName = tag.ChildCount > 0 && tag.GetChild(0) is IdentifierNode name ? name.Text : default;
            text.Append(tagName);
            if (markdown) text.Append("*"u8);
            void OptionalName(SyntaxNode? name)
            { if (name is not null) { text.Append(" "u8); WriteQuoted(text, EntityName(name), true); } }
            if (tag is JSDocParameterOrPropertyTagNode property) OptionalName(property.Name);
            else if (tag is JSDocAugmentsTagNode augments) OptionalName(augments.ClassName);
            else if (tag is JSDocTemplateTagNode { TypeParameters: { } parameters })
                for (int i = 0; i < parameters.Count; i++)
                { if (i != 0) text.Append(","u8); OptionalName(parameters[i].DeclarationName); }
            var comments = tag.DocumentationComment;
            if (tag is JSDocUnknownTagNode && tagName == "example"u8)
            {
                var commentText = Utf8String.Concat((comments ?? new([])).Select(node => node is JSDocTextNode part ? Utf8String.Concat(part.Text) : SourceText(node)));
                if (commentText.StartsWith("<caption>"u8, StringComparison.Ordinal) && commentText.IndexOf("</caption>"u8) is > 0 and var captionEnd)
                {
                    text.Append(" — "u8).Append(commentText[9..captionEnd]); commentText = commentText[(captionEnd + 10)..];
                    while (true)
                    {
                        var first = TrimStart(commentText, (byte)' ', (byte)'\t');
                        var second = TrimStart(first, (byte)'\r', (byte)'\n');
                        if (first.Length == second.Length) break;
                        commentText = second;
                    }
                }
                text.Append("\n"u8);
                if (commentText.Length > 6 && commentText.StartsWith("```"u8, StringComparison.Ordinal)
                    && commentText.EndsWith("```"u8, StringComparison.Ordinal) && commentText.Span.Contains((byte)'\n')) text.Append(commentText).Append("\n"u8);
                else WriteCode(text, "tsx"u8, commentText);
            }
            else if (tag is JSDocSeeTagNode { NameExpression: JSDocNameReferenceNode { Name: { } target } })
            {
                text.Append(" — "u8); await WriteNameLinkAsync(text, target, default, false, markdown, mapper, cancellation);
                if (comments is { Count: > 0 }) { text.Append(" "u8); await WriteCommentsAsync(text, comments, markdown, mapper, cancellation); }
            }
            else if (tag is JSDocThrowsTagNode { TypeExpression: { } expression })
            {
                text.Append(" — "u8).Append(SourceText(expression));
                if (comments is { Count: > 0 }) { text.Append(" "u8); await WriteCommentsAsync(text, comments, markdown, mapper, cancellation); }
            }
            else if (comments is { Count: > 0 })
            {
                text.Append(" "u8);
                if (comments[0] is not JSDocTextNode part || !Utf8String.Concat(part.Text).StartsWith("-"u8, StringComparison.Ordinal)) text.Append("— "u8);
                await WriteCommentsAsync(text, comments, markdown, mapper, cancellation);
            }
        }
        return text.ToUtf8String();
    }

    private async ValueTask WriteCommentsAsync(Utf8StringBuilder text, NodeList? comments, bool markdown,
        DocumentationLocationMapper mapper, CancellationToken cancellation)
    {
        foreach (var comment in comments ?? new([]))
        {
            cancellation.ThrowIfCancellationRequested();
            if (comment is JSDocTextNode part) { text.Append(Utf8String.Concat(part.Text)); continue; }
            var (name, pieces) = comment switch
            {
                JSDocLinkNode link => (link.Name, link.Text), JSDocLinkPlainNode link => (link.Name, link.Text),
                JSDocLinkCodeNode link => (link.Name, link.Text), _ => ((SyntaxNode?)null, Array.Empty<Utf8String>()),
            };
            var label = Utf8String.Concat(pieces).Trim((byte)' ');
            bool quote = comment is JSDocLinkCodeNode;
            if (name is null) { WriteQuoted(text, label, quote && markdown); continue; }
            if (name is IdentifierNode protocol && (protocol.Text == "http"u8 || protocol.Text == "https"u8) && label.StartsWith("://"u8, StringComparison.Ordinal))
            {
                var link = protocol.Text + label; int split = link.Span.IndexOfAny((byte)' ', (byte)'|');
                var uri = split < 0 ? link : link[..split];
                var display = split < 0 ? uri : TrimCommentPrefix(link[split..]);
                if (display.IsEmpty) display = uri;
                if (markdown) WriteMarkdownLink(text, display, uri, quote);
                else { text.Append(display); if (display != uri) text.Append(" ("u8).Append(uri).Append(")"u8); }
                continue;
            }
            await WriteNameLinkAsync(text, name, label, quote, markdown, mapper, cancellation);
        }
    }

    private async ValueTask WriteNameLinkAsync(Utf8StringBuilder text, SyntaxNode name, Utf8String label, bool quote, bool markdown,
        DocumentationLocationMapper mapper, CancellationToken cancellation)
    {
        var symbol = await checker.GetSymbolAtLocationAsync(name, cancellation);
        if (symbol is not null && (symbol.Flags & SymbolFlags.Alias) != 0) symbol = await checker.GetAliasedSymbolAsync(symbol, cancellation);
        if (symbol?.Declarations.FirstOrDefault() is { } declaration && SemanticSyntax.Source(declaration) is { } source)
        {
            var node = declaration.DeclarationName ?? declaration;
            var mapped = mapper(source, await SyntaxNavigation.GetStartAsync(node, source, cancellation: cancellation), node.End);
            int prefix = label.StartsWith("()"u8, StringComparison.Ordinal) ? 2 : 0;
            var display = TrimCommentPrefix(label[prefix..]);
            if (display.IsEmpty) display = EntityName(name) + label[..prefix];
            if (markdown && mapped.Fidelity is MappingFidelity.Exact or MappingFidelity.Atom)
            {
                var range = mapped.Range;
                var uri = Utf8String.ConcatMany(mapped.Uri, "#"u8, Utf8String.Format(range.Start.Line + 1), ","u8, Utf8String.Format(range.Start.Character + 1),
                    "-"u8, Utf8String.Format(range.End.Line + 1), ","u8, Utf8String.Format(range.End.Character + 1));
                WriteMarkdownLink(text, display, uri, quote);
            }
            else WriteQuoted(text, display, false);
        }
        else WriteQuoted(text, EntityName(name) + (label.IsEmpty ? default : (Utf8String)" "u8) + label, quote && markdown);
    }

    internal static void WriteCode(Utf8StringBuilder text, Utf8String language, Utf8String code)
    {
        if (code.IsEmpty) return;
        Utf8String ticks = "```"u8;
        while (code.Contains(ticks)) ticks += "`"u8;
        text.Append(ticks).Append(language).Append("\n"u8).Append(code).Append("\n"u8).Append(ticks).Append("\n"u8);
    }
    private static Utf8String TrimStart(Utf8String text, byte first, byte second)
    { int start = 0; while (start < text.Length && (text[start] == first || text[start] == second)) start++; return text[start..]; }
    private static void WriteQuoted(Utf8StringBuilder text, Utf8String value, bool quote)
    {
        quote &= !value.Span.Contains((byte)'`');
        if (quote) text.Append("`"u8);
        text.Append(value);
        if (quote) text.Append("`"u8);
    }
    private static void WriteMarkdownLink(Utf8StringBuilder text, Utf8String label, Utf8String uri, bool quote)
    { text.Append("["u8); WriteQuoted(text, label, quote); text.Append("]("u8).Append(uri).Append(")"u8); }
}

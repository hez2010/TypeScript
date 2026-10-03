using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.LanguageServices;

internal readonly record struct DocumentationTag(Utf8String Name, Utf8String Text);

internal sealed partial class SymbolDocumentation(Checker checker)
{
    internal async ValueTask<Utf8String> CommentAsync(Symbol symbol, CancellationToken cancellation)
    {
        List<Utf8String> comments = [];
        foreach (var declaration in symbol.Declarations.Distinct())
        {
            cancellation.ThrowIfCancellationRequested();
            var doc = await FindAsync(declaration, [], cancellation);
            if (doc is null || (declaration.Flags & NodeFlags.Reparsed) == 0 && doc is JSDocNode { Tags: { } tags }
                && tags.Any(tag => tag is JSDocTypedefTagNode or JSDocCallbackTagNode)) continue;
            var text = await RenderAsync(doc.DocumentationComment, cancellation);
            if (!text.IsEmpty && !comments.Contains(text)) comments.Add(text);
        }
        return Utf8String.Join("\n"u8, comments);
    }

    internal static async ValueTask<IReadOnlyList<DocumentationTag>> TagsAsync(Symbol symbol, CancellationToken cancellation)
    {
        List<DocumentationTag> result = [];
        foreach (var declaration in symbol.Declarations.Distinct())
        {
            if ((declaration.Flags & NodeFlags.JSDoc) != 0) continue;
            NodeList? tags = null;
            for (var current = declaration; current is not null; current = NextLocation(current))
            {
                if (await DirectAsync(current, cancellation) is { Tags: { } found }) { tags = found; break; }
            }
            if (tags is null || tags.Any(tag => tag is JSDocTypedefTagNode or JSDocCallbackTagNode)
                && !tags.Any(tag => tag.Kind is SyntaxKind.JSDocParameterTag or SyntaxKind.JSDocReturnTag)) continue;
            foreach (var tag in tags)
                result.Add(new(tag.ChildCount > 0 && tag.GetChild(0) is IdentifierNode name ? name.Text : default, TagText(tag)));
        }
        return result;
    }

    private static SyntaxNode? NextLocation(SyntaxNode node) => node.Parent switch
    {
        PropertyAssignmentNode or ExportAssignmentNode or PropertyDeclarationNode or VariableDeclarationNode
            or SatisfiesExpressionNode or ReturnStatementNode or VariableStatementNode or ExpressionStatementNode => node.Parent,
        VariableDeclarationListNode { Declarations.Count: > 0 } list when list.Declarations[0] == node => list,
        _ => null,
    };
    private static async ValueTask<JSDocNode?> DirectAsync(SyntaxNode node, CancellationToken cancellation) =>
        SemanticSyntax.Source(node) is { } source ? (await source.GetDocumentationAsync(node, cancellation)).LastOrDefault() : null;

    private async ValueTask<SyntaxNode?> FindAsync(SyntaxNode? node, HashSet<Symbol> seen, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (node is null) return null;
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (await DirectAsync(node, cancellation) is { } direct) return direct;
        if (node is ParameterDeclarationNode or TypeParameterDeclarationNode)
        {
            if (await FindAsync(node.Parent, seen, cancellation) is not JSDocNode { Tags: { } tags }) return null;
            var name = node.DeclarationName;
            if (node is ParameterDeclarationNode && name is BindingPatternNode)
            {
                var parameters = (node.Parent as IFunctionSignature)?.Parameters;
                int index = parameters is null ? -1 : Array.IndexOf(parameters.ToArray(), node);
                return index < 0 ? null : tags.Where(tag => tag.Kind == SyntaxKind.JSDocParameterTag).ElementAtOrDefault(index);
            }
            return tags.FirstOrDefault(tag => node is ParameterDeclarationNode
                ? tag is JSDocParameterOrPropertyTagNode { Kind: SyntaxKind.JSDocParameterTag, Name: IdentifierNode tagName }
                    && name is IdentifierNode id && tagName.Text == id.Text
                : tag is JSDocTemplateTagNode { TypeParameters: { } typeParameters }
                    && typeParameters.Any(parameter => parameter.DeclarationName is IdentifierNode tagName && name is IdentifierNode id && tagName.Text == id.Text));
        }
        if (node is VariableDeclarationNode && node.Parent is VariableDeclarationListNode { Declarations.Count: > 0 } list && list.Declarations[0] == node)
            return await FindAsync(list.Parent, seen, cancellation);
        if (node is FunctionExpressionNode or ArrowFunctionNode or ClassExpressionNode && node.Parent is { } owner
            && owner is VariableDeclarationNode or PropertyDeclarationNode or PropertyAssignmentNode)
            return await FindAsync(owner, seen, cancellation);
        if (node is BindingElementNode element && node.Parent is BindingPatternNode { Kind: SyntaxKind.ObjectBindingPattern } pattern
            && (element.PropertyName ?? element.Name) is IdentifierNode propertyName)
        {
            var type = await checker.GetTypeAtLocationAsync(pattern, cancellation);
            if (await checker.Properties.PropertyAsync(type, propertyName.Text, cancellation: cancellation) is { } property)
                foreach (var declaration in property.Declarations)
                    if (await DirectAsync(declaration, cancellation) is { } doc) return doc;
        }
        if (node.BindingSymbol is { } symbol && node.Parent is { } parent)
        {
            if (node is FunctionDeclarationNode or MethodDeclarationNode or MethodSignatureDeclarationNode or ConstructorDeclarationNode or ConstructSignatureDeclarationNode)
            {
                var first = symbol.Declarations.FirstOrDefault(declaration => declaration.HasFunctionSignature);
                if (first is not null && first != node && await FindAsync(first, seen, cancellation) is { } overloadDoc) return overloadDoc;
            }
            if (parent is ClassDeclarationNode or ClassExpressionNode or InterfaceDeclarationNode && parent.BindingSymbol is { } parentSymbol
                && await checker.Declared.GetAsync(parentSymbol, cancellation) is InterfaceType parentType)
            {
                IReadOnlyList<Type> bases = SemanticSyntax.IsStatic(node)
                    ? [await checker.Views.ApparentAsync(await checker.BaseConstructorAsync(parentType, cancellation), cancellation)]
                    : await checker.BaseTypesAsync(parentType, cancellation);
                foreach (var baseType in bases)
                    if (await checker.Properties.PropertyAsync(baseType, symbol.Name, cancellation: cancellation) is { ValueDeclaration: { } declaration } property
                        && seen.Add(property) && await FindAsync(declaration, seen, cancellation) is { } inherited) return inherited;
            }
        }
        return null;
    }

    private async ValueTask<Utf8String> RenderAsync(NodeList? comments, CancellationToken cancellation)
    {
        var result = new Utf8StringBuilder();
        foreach (var comment in comments ?? new([]))
        {
            cancellation.ThrowIfCancellationRequested();
            if (comment is JSDocTextNode text) { foreach (var part in text.Text) result.Append(part); continue; }
            var (name, parts) = comment switch
            {
                JSDocLinkNode link => (link.Name, link.Text), JSDocLinkPlainNode link => (link.Name, link.Text),
                JSDocLinkCodeNode link => (link.Name, link.Text), _ => ((SyntaxNode?)null, Array.Empty<Utf8String>()),
            };
            var label = Utf8String.Concat(parts).Trim((byte)' ');
            if (name is null) { result.Append(label); continue; }
            if (name is IdentifierNode protocol && (protocol.Text == "http"u8 || protocol.Text == "https"u8) && label.StartsWith("://"u8, StringComparison.Ordinal))
            {
                var link = protocol.Text + label; int split = link.Span.IndexOfAny((byte)' ', (byte)'|');
                var uri = split < 0 ? link : link[..split];
                var display = split < 0 ? uri : TrimCommentPrefix(link[split..]);
                if (display.IsEmpty) display = uri;
                result.Append(display);
                if (display != uri) { result.Append(" ("u8); result.Append(uri); result.Append(")"u8); }
                continue;
            }
            var symbol = await checker.GetSymbolAtLocationAsync(name, cancellation);
            if (symbol is not null && (symbol.Flags & SymbolFlags.Alias) != 0) symbol = await checker.GetAliasedSymbolAsync(symbol, cancellation);
            if (symbol?.Declarations.Count > 0)
            {
                int prefix = label.StartsWith("()"u8, StringComparison.Ordinal) ? 2 : 0;
                var display = TrimCommentPrefix(label[prefix..]);
                result.Append(display.IsEmpty ? EntityName(name) + label[..prefix] : display);
            }
            else
            {
                result.Append(EntityName(name));
                if (!label.IsEmpty) { result.Append(" "u8); result.Append(label); }
            }
        }
        return result.ToUtf8String();
    }

    private static Utf8String TrimCommentPrefix(Utf8String text)
    {
        text = text.TrimStart((byte)' ');
        return (text.StartsWith("|"u8, StringComparison.Ordinal) ? text[1..] : text).TrimStart((byte)' ');
    }
    private static Utf8String EntityName(SyntaxNode node)
    {
        var parts = new Stack<Utf8String>();
        while (true)
        {
            if (node is IdentifierNode id) { parts.Push(id.Text); break; }
            if (node is QualifiedNameNode name) { parts.Push("."u8 + EntityName(name.Right!)); node = name.Left!; }
            else if (node is PropertyAccessExpressionNode access) { parts.Push("."u8 + EntityName(access.Name!)); node = access.Expression!; }
            else if (node is ParenthesizedExpressionNode paren) node = paren.Expression!;
            else if (node is ExpressionWithTypeArgumentsNode expression) node = expression.Expression!;
            else if (node is JSDocNameReferenceNode reference) node = reference.Name!;
            else break;
        }
        return Utf8String.Concat(parts);
    }
    private static Utf8String SourceText(SyntaxNode? node)
    {
        if (node is null || SemanticSyntax.Source(node) is not { } source || node.Pos < 0 || node.End < node.Pos) return default;
        int start = new Scanner(source.Source).SkipTriviaAt(node.Pos, inJSDoc: (node.Flags & NodeFlags.JSDoc) != 0);
        return source.Source.Text[Math.Min(start, node.End)..node.End];
    }
    private static Utf8String TagText(SyntaxNode tag)
    {
        var comment = Utf8String.Concat((tag.DocumentationComment ?? new([])).Select(node => node is JSDocTextNode text
            ? Utf8String.Concat(text.Text) : SourceText(node)));
        comment = comment[..comment.Span.TrimEnd().Length];
        Utf8String prefix = tag switch
        {
            JSDocThrowsTagNode node => SourceText(node.TypeExpression), JSDocImplementsTagNode node => SourceText(node.ClassName),
            JSDocAugmentsTagNode node => SourceText(node.ClassName), JSDocTypeTagNode node => SourceText(node.TypeExpression),
            JSDocSatisfiesTagNode node => SourceText(node.TypeExpression), JSDocSeeTagNode node => SourceText(node.NameExpression),
            JSDocParameterOrPropertyTagNode node => SourceText(node.Name),
            JSDocTemplateTagNode node => SourceText(node.Constraint) + (node.Constraint is null || node.TypeParameters is not { Count: > 0 } ? default : (Utf8String)" "u8)
                + Utf8String.Join(", "u8, (node.TypeParameters ?? new([])).Select(SourceText)),
            _ => default,
        };
        return prefix.IsEmpty ? comment : comment.IsEmpty ? prefix : prefix + " "u8 + comment;
    }
}

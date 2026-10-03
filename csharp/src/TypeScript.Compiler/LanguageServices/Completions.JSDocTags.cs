using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class CompletionQuery
    {
        private async ValueTask<CompletionList?> DocumentationAsync(SyntaxNode token)
        {
            if (await InCommentAsync(File, position, token, cancellation))
            {
                documentationHandled = true;
                if (Ancestor(token, node => node is JSDocNode) is not null)
                {
                    if (position > 0 && File.Source.Text[position - 1] == '@') return await TagsAsync(token, true);
                    int lineStart = File.Source.LineStarts[File.Source.GetLineAndCharacter(position).Line];
                    bool prefix = true;
                    for (int i = lineStart; i < position;)
                    {
                        int point = Wtf8.Decode(File.Source.Text.Span[i..], out int width); i += width;
                        if (!TokenFacts.IsWhiteSpace(point) && point is not ('*' or '/' or '(' or ')' or '|')) { prefix = false; break; }
                    }
                    if (prefix) return await TagsAsync(token, false);
                }
                for (var node = token; node is not null && node is not JSDocNode; node = node.Parent)
                {
                    if (!IsTag(node) || node.Pos > position || node.End < position) continue;
                    var tagName = node.ChildCount != 0 ? node.GetChild(0) as IdentifierNode : null;
                    if (tagName is not null && tagName.Pos <= position && position <= tagName.End) return await TagsAsync(token, true);
                    insideJSDocImport = node.Kind == K.JSDocImportTag;
                    var expression = node switch
                    {
                        JSDocTemplateTagNode template => template.Constraint,
                        JSDocAugmentsTagNode augments => augments.ClassName,
                        JSDocImplementsTagNode implements => implements.ClassName,
                        ITypeExpressionNode typed => typed.TypeExpression, _ => null,
                    };
                    insideJSDocType = !insideJSDocImport && expression is not null && !QuerySyntax.DeclarationName(token)
                        && !(token.Parent is JSDocParameterOrPropertyTagNode { Kind: K.JSDocPropertyTag } property && property.Name == token)
                        && await EditingAsync(expression);
                    if (insideJSDocType || insideJSDocImport) { documentationHandled = false; return null; }
                    if (node is JSDocParameterOrPropertyTagNode { Kind: K.JSDocParameterTag, Name: { } name } parameter
                        && (name.Pos == name.End || name.Pos <= position && position <= name.End)) return ParameterNames(parameter);
                    break;
                }
                return null;
            }
            return null;
        }

        private static bool IsTag(SyntaxNode node) => node.Kind is >= K.FirstJSDocTagNode and <= K.LastJSDocTagNode;

        private CompletionList Defaults(IEnumerable<CompletionItem> entries)
        {
            Utf8String[] characters = ["."u8, ","u8, ";"u8];
            return new(entries.Select(item => capabilities.CommitCharacters && !capabilities.DefaultCommitCharacters
                ? item with { CommitCharacters = characters } : item).ToArray(), ItemDefaults:
                capabilities.CommitCharacters && capabilities.DefaultCommitCharacters ? new(characters) : null);
        }

        private async ValueTask<CompletionList> TagsAsync(SyntaxNode token, bool namesOnly)
        {
            List<CompletionItem> items = TagNames.Select(name => new CompletionItem(namesOnly ? name : "@"u8 + name, 14, SortText: "11"u8)).ToList();
            var doc = token is JSDocNode ? (JSDocNode)token : IsTag(token) ? token.Parent as JSDocNode : null;
            if (doc?.Parent is { } function && Signatures.FunctionLike(function) && function is IFunctionSignature { Parameters: { } parameters })
            {
                int annotated = 0;
                foreach (var tag in doc.Tags ?? new([]))
                    if (tag is JSDocParameterOrPropertyTagNode { Kind: K.JSDocParameterTag, Name: IdentifierNode }
                        && await SyntaxNavigation.GetStartAsync(tag, File, cancellation: cancellation) < position) annotated++;
                for (int i = annotated; i < parameters.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var parameter = (ParameterDeclarationNode)parameters[i];
                    Utf8String label;
                    if (parameter.Name is IdentifierNode identifier)
                        label = await AnnotationAsync(identifier.Text, parameter.Initializer, parameter.DotDotDotToken is not null, false);
                    else if (i == annotated)
                    {
                        var path = Utf8String.FromString($"param{i}");
                        label = File.ScriptKind is ScriptKind.JS or ScriptKind.JSX
                            ? await DestructuringAsync(path, parameter.Name!, parameter.Initializer, parameter.DotDotDotToken is not null)
                            : await AnnotationAsync(path, parameter.Initializer, parameter.DotDotDotToken is not null, false);
                    }
                    else continue;
                    items.Add(new(namesOnly ? label[1..] : label, 6, SortText: "11"u8));
                }
            }
            return Defaults(items);
        }

        private CompletionList ParameterNames(JSDocParameterOrPropertyTagNode tag)
        {
            if (tag.Name is not IdentifierNode prefix || tag.Parent is not JSDocNode doc
                || doc.Parent is not IFunctionSignature { Parameters: { } parameters }) return Defaults([]);
            HashSet<Utf8String> used = [];
            foreach (var other in doc.Tags ?? new([]))
                if (other != tag && other is JSDocParameterOrPropertyTagNode { Kind: K.JSDocParameterTag, Name: IdentifierNode name }) used.Add(name.Text);
            return Defaults(parameters.OfType<ParameterDeclarationNode>().Select(parameter => parameter.Name).OfType<IdentifierNode>()
                .Where(name => !used.Contains(name.Text) && name.Text.Span.StartsWith(prefix.Text.Span))
                .Select(name => new CompletionItem(name.Text, 6, SortText: "11"u8)));
        }

        private async ValueTask<Utf8String> AnnotationAsync(Utf8String name, SyntaxNode? initializer, bool rest, bool isObject)
        {
            if (initializer is not null)
            {
                var value = File.Source.Text[(await SyntaxNavigation.GetStartAsync(initializer, File, cancellation: cancellation))..initializer.End];
                value = Utf8String.FromString(value.ToString().Trim());
                name = "["u8 + name + (value.IndexOf("\n"u8) >= 0 || value.Length > 80 ? Utf8String.Empty : "="u8 + value) + "]"u8;
            }
            if (File.ScriptKind is not (ScriptKind.JS or ScriptKind.JSX)) return "@param "u8 + name + " "u8;
            Utf8String typeText = isObject ? "object"u8 : "*"u8;
            if (!isObject && initializer is not null)
            {
                var type = await checker.GetTypeAtLocationAsync(initializer.Parent!, cancellation);
                if ((type.Flags & (TypeFlags.Any | TypeFlags.Void)) == 0)
                {
                    var flags = RenameQuote(File, preferences) == "'"u8 ? NodeBuilderFlags.UseSingleQuotesForStringLiteralType : NodeBuilderFlags.None;
                    var enclosing = Ancestor(initializer, Signatures.FunctionLike);
                    if (await checker.TypeToTypeNodeAsync(type, enclosing, flags, cancellation) is { } typeNode)
                    {
                        var emit = new EmitContext(); emit.SetFlags(typeNode, EmitFlags.SingleLine);
                        typeText = new SyntaxPrinter(new() { RemoveComments = true }, emit).Print(typeNode, File, cancellation: cancellation);
                    }
                }
            }
            return "@param {"u8 + (rest && !isObject ? (Utf8String)"..."u8 : Utf8String.Empty) + typeText + "} "u8 + name + " "u8;
        }

        private async ValueTask<Utf8String> DestructuringAsync(Utf8String path, SyntaxNode pattern, SyntaxNode? initializer, bool rest)
        {
            List<Utf8String> tags = [];
            Stack<(Utf8String Path, SyntaxNode Pattern, SyntaxNode? Initializer, bool IsRest)> pending = [];
            pending.Push((path, pattern, initializer, rest));
            while (pending.TryPop(out var current))
            {
                cancellation.ThrowIfCancellationRequested();
                var elements = current.Pattern is BindingPatternNode { Kind: K.ObjectBindingPattern } obj && !current.IsRest ? obj.Elements : null;
                var children = elements?.OfType<BindingElementNode>().Select(element => (Element: element,
                    Name: PropertyName(element.PropertyName ?? element.Name!))).ToArray();
                bool expand = children is { Length: > 0 } && children.All(child => !child.Name.IsEmpty);
                tags.Add(await AnnotationAsync(current.Path, current.Initializer, current.IsRest, expand));
                if (!expand) continue;
                for (int i = children!.Length - 1; i >= 0; i--)
                {
                    var (element, name) = children[i];
                    pending.Push((current.Path + "."u8 + name, element.Name!, element.Initializer, element.DotDotDotToken is not null));
                }
            }
            return Utf8String.Join(options.NewLine == NewLineKind.CRLF ? "\r\n* "u8 : "\n* "u8, tags);
        }

        private static Utf8String PropertyName(SyntaxNode node) => node switch
        {
            IdentifierNode id => id.Text, StringLiteralNode text => text.Text, NumericLiteralNode number => number.Text,
            NoSubstitutionTemplateLiteralNode template => template.Text, _ => default,
        };

        internal static async ValueTask<bool> ValidTriggerAsync(SourceFileNode file, int position, Utf8String trigger, CancellationToken cancellation)
        {
            var previous = await SyntaxNavigation.FindPrecedingTokenAsync(file, position, cancellation: cancellation);
            if (previous is not null && previous.Kind is K.StringLiteral or K.NoSubstitutionTemplateLiteral or K.TemplateHead or K.TemplateMiddle or K.TemplateTail
                && await SyntaxNavigation.GetStartAsync(previous, file, cancellation: cancellation) < position
                && (position < previous.End || position == previous.End && Unterminated(previous))) return true;
            if (trigger == "."u8 || trigger == "@"u8) return true;
            if (trigger == "*"u8) return JSDocCompletion.ValidPosition(file, position);
            if (trigger == "\""u8 || trigger == "'"u8 || trigger == "`"u8)
                return previous is StringLiteralNode or NoSubstitutionTemplateLiteralNode or TemplateExpressionNode or TaggedTemplateExpressionNode
                    && position == await SyntaxNavigation.GetStartAsync(previous, file, cancellation: cancellation) + 1;
            if (trigger == "#"u8) return previous is PrivateIdentifierNode && Ancestor(previous, SemanticSyntax.ClassLike) is not null;
            if (trigger == "<"u8) return previous?.Kind == K.LessThanToken && (previous.Parent is not BinaryExpressionNode binary || binary.Left!.Pos == binary.Left.End);
            if (trigger == " "u8) return previous?.Kind == K.ImportKeyword && previous.Parent is SourceFileNode;
            if (trigger == "/"u8) return previous?.Kind == K.LessThanSlashToken && previous.Parent is JsxClosingElementNode
                || previous is StringLiteralNode or NoSubstitutionTemplateLiteralNode && previous.Parent is ImportDeclarationNode or ExportDeclarationNode;
            return false;
        }

        private static bool Unterminated(SyntaxNode node) => ((node switch
        {
            StringLiteralNode text => text.TokenFlags, NoSubstitutionTemplateLiteralNode template => template.TemplateFlags,
            TemplateHeadNode head => head.TemplateFlags, TemplateMiddleNode middle => middle.TemplateFlags,
            TemplateTailNode tail => tail.TemplateFlags, _ => TokenFlags.None,
        }) & TokenFlags.Unterminated) != 0;

        private static readonly Utf8String[] TagNames = "abstract access alias argument async augments author borrows callback class classdesc constant constructor constructs copyright default deprecated description emits enum event example exports extends external field file fileoverview fires function generator global hideconstructor host ignore implements import inheritdoc inner instance interface kind lends license link linkcode linkplain listens member memberof method mixes module name namespace overload override package param private prop property protected public readonly requires returns satisfies see since static summary template this throws todo tutorial type typedef var variation version virtual yields"
            .Split(' ').Select(Utf8String.FromString).ToArray();
    }
}

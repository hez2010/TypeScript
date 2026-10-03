using System.Text.Encodings.Web;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class CompletionQuery
    {
        private async ValueTask<CompletionList> EntriesAsync()
        {
            List<CompletionItem> items = [];
            Dictionary<Utf8String, bool> names = [];
            var closest = ClosestDeclaration(contextToken, location);
            foreach (var entry in symbols)
            {
                cancellation.ThrowIfCancellationRequested();
                var symbol = entry.Symbol;
                var (name, brackets) = DisplayName(entry);
                if (name.IsEmpty || names.GetValueOrDefault(name) && entry.ObjectMethod is null || kind == CompletionKind.Global && !await IncludeAsync(symbol, closest)) continue;
                if (!typeOnly && JavaScript)
                {
                    var resolved = await SkipAliasAsync(symbol);
                    var flags = resolved.Flags | (resolved.ExportSymbol?.Flags ?? 0);
                    if ((flags & S.Value) == 0 && (symbol.Declarations.Length == 0 || (symbol.Declarations[0].Flags & NodeFlags.JavaScriptFile) == 0 || (flags & S.Type) != 0)) continue;
                }
                if (await SymbolItemAsync(entry, name, brackets) is not { } item) continue;
                names[name] = !entry.This && !(symbol.Parent is null && !symbol.Declarations.Any(declaration => SemanticSyntax.Source(declaration) == File));
                items.Add(item);
            }
            await AutoImportItemsAsync(items, names);
            foreach (var keyword in Keywords())
            {
                if (typeOnly && TypeKeyword(TokenFacts.FromText(keyword.Label.Span)) || !typeOnly && ContextualExpressionKeyword(keyword.Label)
                    || !names.ContainsKey(keyword.Label)) { names[keyword.Label] = true; items.Add(keyword); }
            }
            if (contextToken is StringLiteralNode && contextToken.Parent is ImportDeclarationNode or ExportDeclarationNode
                && Line(contextToken.End) == Line(position) && !names.ContainsKey("assert"u8)) items.Add(new("assert"u8, 14, SortText: "15"u8));
            foreach (var literal in literals)
            {
                var label = LiteralName(literal);
                names[label] = true;
                items.Add(new(label, 21, SortText: "11"u8, CommitCharacters: []));
            }
            if (!Checked)
                foreach (var (name, declarationPosition) in await JavaScriptNamesAsync())
                    if (declarationPosition != position && !names.ContainsKey(name) && Identifier(name))
                    { names[name] = true; items.Add(new(name, 1, SortText: "18"u8, CommitCharacters: [])); }
            if (await SwitchSnippetAsync() is { } switchSnippet) items.Add(switchSnippet);
            return await ApplyDefaultsAsync(items, commitCharacters ?? (newIdentifier ? [] : AllCommitCharacters), location);
        }

        private async ValueTask<Dictionary<Utf8String, int>> JavaScriptNamesAsync()
        {
            Dictionary<Utf8String, int> names = [];
            Stack<SyntaxNode> pending = [];
            for (int i = File.ChildCount - 1; i >= 0; i--) pending.Push(File.GetChild(i));
            while (pending.TryPop(out var node))
            {
                cancellation.ThrowIfCancellationRequested();
                var parent = node.Parent;
                bool name = node is IdentifierNode && !(parent is not null && IsTag(parent) && parent.ChildCount > 0 && parent.GetChild(0) == node)
                    || node is PrivateIdentifierNode || node is StringLiteralNode or NoSubstitutionTemplateLiteralNode or NumericLiteralNode
                        && (QuerySyntax.DeclarationName(node) || parent is ExternalModuleReferenceNode
                            || parent is ElementAccessExpressionNode access && access.ArgumentExpression == node || parent is ComputedPropertyNameNode);
                var text = node is PrivateIdentifierNode id ? id.Text : PropertyName(node);
                if (name && !text.IsEmpty) names[text] = names.ContainsKey(text) ? -1 : node.Pos;
                foreach (var doc in await File.GetDocumentationAsync(node, cancellation))
                    for (int i = doc.ChildCount - 1; i >= 0; i--) pending.Push(doc.GetChild(i));
                for (int i = node.ChildCount - 1; i >= 0; i--) pending.Push(node.GetChild(i));
            }
            return names;
        }

        private (Utf8String Name, bool Brackets) DisplayName(CompletionSymbol entry)
        {
            var symbol = entry.Symbol;
            if (!entry.DisplayName.IsEmpty) return (entry.DisplayName, false);
            var name = symbol.ValueDeclaration?.DeclarationName is PrivateIdentifierNode privateName ? privateName.Text : symbol.Name;
            if (name.IsEmpty || (symbol.Flags & S.Module) != 0 && name[0] is (byte)'"' or (byte)'\'' || name.StartsWith(Symbol.InternalUnique)) return default;
            if (Identifier(name, jsxIdentifier) || symbol.ValueDeclaration?.DeclarationName is PrivateIdentifierNode) return (name, false);
            if ((symbol.Flags & S.Alias) != 0) return (name, true);
            return kind switch
            {
                CompletionKind.Member => default,
                CompletionKind.ObjectProperty => (Quote(name, single: false), false),
                CompletionKind.PropertyAccess or CompletionKind.Global => name[0] == ' ' ? default : (name, true), _ => (name, false),
            };
        }

        private async ValueTask<CompletionItem?> SymbolItemAsync(CompletionSymbol entry, Utf8String name, bool brackets)
        {
            var symbol = entry.Symbol;
            Utf8String insert = default;
            DocumentRange? replacement = null;
            if (entry.This)
            {
                Utf8String dot = entry.Nullable ? "?."u8 : "."u8;
                insert = brackets ? "this"u8 + (entry.Nullable ? (Utf8String)"?."u8 : Utf8String.Empty) + "["u8 + QuoteProperty(name) + "]"u8 : "this"u8 + dot + name;
            }
            else if (propertyAccess is not null && (brackets || entry.SymbolMember || entry.Nullable))
            {
                insert = brackets || entry.SymbolMember ? "["u8 + (brackets ? QuoteProperty(name) : name) + "]"u8 : name;
                if (entry.Nullable || propertyAccess.QuestionDotToken is not null) insert = "?."u8 + insert;
                var dot = await SyntaxNavigation.FindChildOfKindAsync(propertyAccess, K.DotToken, File, cancellation)
                    ?? await SyntaxNavigation.FindChildOfKindAsync(propertyAccess, K.QuestionDotToken, File, cancellation);
                if (dot is null) return null;
                int end = propertyAccess.Name is IdentifierNode partial && name.StartsWith(partial.Text) ? propertyAccess.End : dot.End;
                var mapped = projection.ToRange(await StartAsync(dot), end);
                if (mapped.Fidelity != MappingFidelity.Exact) return null;
                replacement = mapped.Range;
            }
            if (jsxInitializer)
            {
                insert = "{"u8 + (insert.IsEmpty ? name : insert) + "}"u8;
                if (jsxInitializerNode is not null)
                {
                    var mapped = projection.ToRange(await StartAsync(jsxInitializerNode), jsxInitializerNode.End);
                    if (mapped.Fidelity != MappingFidelity.Exact) return null;
                    replacement = mapped.Range;
                }
            }
            if (entry.Await && propertyAccess is not null)
            {
                if (insert.IsEmpty) insert = name;
                var preceding = await SyntaxNavigation.FindPrecedingTokenAsync(File, propertyAccess.Pos, cancellation: cancellation);
                Utf8String prefix = preceding?.Parent is { } parent && await SyntaxNavigation.IsAutomaticSemicolonCandidateAsync(preceding.End, parent, File, cancellation) ? ";"u8 : default;
                var expression = propertyAccess.Expression!;
                prefix += "(await "u8 + File.Source.Text[await StartAsync(expression)..expression.End] + ")"u8;
                insert = prefix + (brackets ? Utf8String.Empty : entry.Nullable ? (Utf8String)"?."u8 : "."u8) + insert;
                var wrap = propertyAccess.Parent is AwaitExpressionNode ? propertyAccess.Parent : expression;
                var mapped = projection.ToRange(await StartAsync(wrap), propertyAccess.End);
                if (mapped.Fidelity != MappingFidelity.Exact) return null;
                replacement = mapped.Range;
            }
            if (Ancestor(location, node => node is NamedImportsNode or NamedExportsNode) is { } bindings)
            {
                if (!Identifier(name))
                {
                    insert = QuoteProperty(name);
                    if (bindings is NamedImportsNode)
                    {
                        var scanner = new Scanner(File.Source); scanner.ResetPosition(position);
                        if (!(scanner.Scan() == K.AsKeyword && scanner.Scan() == K.Identifier)) insert += " as "u8 + IdentifierForString(name);
                    }
                }
                else if (bindings is NamedImportsNode && (TokenFacts.FromText(name.Span) is K.AwaitKeyword or >= K.FirstKeyword and < K.FirstContextualKeyword))
                    insert = name + " as "u8 + name + "_"u8;
            }
            bool snippet = false;
            ClassMemberSnippet? classMember = null;
            if (await ClassMemberSnippetEligibleAsync(symbol))
            {
                classMember = await ClassMemberSnippetAsync(symbol, name);
                if (classMember is null) return null;
            }
            if (classMember is not null) { insert = classMember.Text; snippet = capabilities.Snippets; }
            CompletionLabelDetails? labelDetails = null;
            if (entry.ObjectMethod is { } objectMethod)
            {
                insert = objectMethod.Text; snippet = capabilities.Snippets;
                if (capabilities.LabelDetails) labelDetails = new(objectMethod.Detail);
                else name += objectMethod.Detail;
            }
            if (jsxIdentifier && !jsxOpen && capabilities.Snippets && preferences.JsxAttributeCompletionStyle != "none"u8
                && location.Parent is not JsxAttributeNode { Initializer: not null })
            {
                bool braces = preferences.JsxAttributeCompletionStyle == "braces"u8;
                var type = await checker.GetTypeOfSymbolAtLocationAsync(symbol, location, cancellation);
                if (preferences.JsxAttributeCompletionStyle == "auto"u8 && (type.Flags & TypeFlags.BooleanLike) == 0
                    && !(type is UnionType union && union.Types.Any(part => (part.Flags & TypeFlags.BooleanLike) != 0)))
                {
                    bool stringLike = (type.Flags & TypeFlags.StringLike) != 0;
                    if (type is UnionType parts)
                    {
                        stringLike = true;
                        foreach (var part in parts.Types)
                            if ((part.Flags & (TypeFlags.StringLike | TypeFlags.Undefined)) == 0 && !await StringAndEmptyObjectAsync(part)) { stringLike = false; break; }
                    }
                    if (stringLike) { insert = EscapeSnippet(name) + "="u8 + Quote("$1"u8); snippet = true; }
                    else braces = true;
                }
                if (braces) { insert = EscapeSnippet(name) + "={$1}"u8; snippet = true; }
            }
            var filter = classMember is not null ? name : FilterText(insert, name);
            CompletionTextEdit? edit = replacement is { } range ? new(insert.IsEmpty ? name : insert, range) : null;
            bool member = kind is CompletionKind.Member or CompletionKind.ObjectProperty or CompletionKind.PropertyAccess;
            var label = name;
            if (member && (symbol.Flags & S.Optional) != 0)
            {
                if (insert.IsEmpty) insert = name;
                if (filter.IsEmpty || snippet) filter = name;
                label += "?"u8;
            }
            bool deprecated = await checker.IsDeprecatedCompletionAsync(symbol, cancellation);
            var sort = entry.SortText.IsEmpty ? (Utf8String)"11"u8 : entry.SortText;
            if (entry.ObjectMethod is not null) sort += "1"u8;
            Utf8String source = entry.This ? "ThisProperty/"u8 : await ObjectMemberNeedsCommaAsync() ? "ObjectLiteralMemberWithComma/"u8 : default;
            if (entry.ObjectMethod is not null) source = "ObjectLiteralMethodSnippet/"u8;
            if (classMember?.Edits is { Length: > 0 }) source = "ClassMemberSnippet/"u8;
            return new(label, await SymbolKindAsync(symbol), SortText: deprecated ? "z"u8 + sort : sort, TextEdit: edit,
                Data: new(projection.OriginalFileName, position, name, supplementalIndex, source),
                InsertText: insert.IsEmpty ? null : insert, FilterText: filter.IsEmpty ? null : filter, Tags: deprecated ? [1] : null,
                InsertTextFormat: snippet ? 2 : null, LabelDetails: labelDetails, AdditionalTextEdits: classMember?.Edits,
                Preselect: recommended is not null && (symbol == recommended || (symbol.Flags & S.ExportValue) != 0 && symbol.ExportSymbol == recommended) ? true : null)
                { Symbol = includeSymbols ? symbol : null };
        }

        private async ValueTask<int> SymbolKindAsync(Symbol symbol)
            => SymbolClassification.CompletionKind(await SymbolClassification.KindAsync(checker, symbol, location, cancellation));

        private async ValueTask<CompletionList> ApplyDefaultsAsync(List<CompletionItem> items, Utf8String[] characters, SyntaxNode? replacementNode, DocumentRange? optionalRange = null)
        {
            CompletionItemDefaults? defaults = capabilities.CommitCharacters && capabilities.DefaultCommitCharacters ? new(characters) : null;
            if (capabilities.CommitCharacters && !capabilities.DefaultCommitCharacters)
                for (int i = 0; i < items.Count; i++) if (items[i].CommitCharacters is null) items[i] = items[i] with { CommitCharacters = characters };
            if (replacementNode is IdentifierNode or PrivateIdentifierNode || optionalRange is not null)
            {
                var mapped = optionalRange is { } explicitRange ? (Range: explicitRange, Fidelity: MappingFidelity.Exact)
                    : projection.ToRange(await StartAsync(replacementNode!), replacementNode!.End);
                var cursor = projection.ToRange(position, position);
                if (mapped.Fidelity == MappingFidelity.Exact && cursor.Fidelity == MappingFidelity.Exact)
                {
                    var insert = new DocumentRange(mapped.Range.Start, cursor.Range.End);
                    if (capabilities.DefaultEditRange)
                    {
                        defaults = (defaults ?? new()) with { Insert = insert, Replace = mapped.Range };
                        for (int i = 0; i < items.Count; i++)
                            if (items[i] is { InsertText: { } text, TextEdit: null }) items[i] = items[i] with { TextEdit = new(text, insert, mapped.Range), InsertText = null };
                    }
                    else if (capabilities.InsertReplace)
                        for (int i = 0; i < items.Count; i++)
                            if (items[i].TextEdit is null) items[i] = items[i] with { TextEdit = new(items[i].InsertText ?? items[i].Label, insert, mapped.Range) };
                }
            }
            return new(items.ToArray(), ItemDefaults: defaults);
        }

        private static bool Identifier(Utf8String text, bool jsx = false)
        {
            if (text.IsEmpty) return false;
            for (int i = 0; i < text.Length;)
            {
                int point = Wtf8.Decode(text.Span[i..], out int width);
                if (!(i == 0 ? TokenFacts.IsIdentifierStart(point) : TokenFacts.IsIdentifierPart(point, jsx))) return false;
                i += width;
            }
            return true;
        }

        private static Utf8String IdentifierForString(Utf8String text)
        {
            var builder = new Utf8StringBuilder();
            bool underscore = false;
            for (int i = 0; i < text.Length;)
            {
                int point = Wtf8.Decode(text.Span[i..], out int width);
                if (i == 0 ? TokenFacts.IsIdentifierStart(point) : TokenFacts.IsIdentifierPart(point))
                {
                    if (underscore) builder.Append("_"u8);
                    builder.Append(text.Span.Slice(i, width)); underscore = false;
                }
                else underscore = true;
                i += width;
            }
            if (underscore || text.IsEmpty) builder.Append("_"u8);
            return builder.ToUtf8String();
        }

        private Utf8String QuoteProperty(Utf8String name) => name[0] is >= (byte)'0' and <= (byte)'9' ? name : Quote(name);
        private Utf8String Quote(Utf8String name, bool? single = null)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) JsonStrings.WriteString(writer, name);
            var quoted = Utf8String.Copy(buffer.ToArray());
            if (!(single ?? RenameQuote(File, preferences) == "'"u8)) return quoted;
            return "'"u8 + Utf8String.FromString(quoted[1..^1].ToString().Replace("\\\"", "\"", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)) + "'"u8;
        }

        private Utf8String FilterText(Utf8String insert, Utf8String label)
        {
            int wordStart = position, first = 0;
            while (wordStart > 0)
            {
                int point = Wtf8.DecodeLast(File.Source.Text.Span[..wordStart], out int width);
                if (point <= char.MaxValue && "`~!@%^&*()-=+[{]}\\|;:'\",.<>/?".Contains((char)point, StringComparison.Ordinal)
                    || System.Text.Rune.IsValid(point) && System.Text.Rune.IsWhiteSpace(new(point))) break;
                first = point; wordStart -= width;
            }
            if (label.StartsWith("#"u8))
            {
                if (insert.StartsWith("this.#"u8)) return first == '#' ? default : insert[6..];
                if (insert.IsEmpty) return first == '#' ? default : label[1..];
            }
            if (insert.StartsWith("this."u8)) return default;
            Utf8String dot = File.Source.Text[..wordStart].EndsWith("?."u8) ? "?."u8 : File.Source.Text[..wordStart].EndsWith("."u8) ? "."u8 : default;
            if (insert.StartsWith("["u8)) return dot + TrimBrackets(insert);
            if (insert.StartsWith("?."u8)) return dot + (insert.StartsWith("?.["u8) ? TrimBrackets(insert[2..]) : insert[2..]);
            return insert;

            static Utf8String TrimBrackets(Utf8String text)
            {
                text = text[1..]; if (text.EndsWith("]"u8)) text = text[..^1];
                if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'') text = text[1..^1];
                if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
                return text;
            }
        }

        private static bool ContextualExpressionKeyword(Utf8String name) => name == "abstract"u8 || name == "async"u8 || name == "await"u8
            || name == "declare"u8 || name == "module"u8 || name == "namespace"u8 || name == "type"u8 || name == "satisfies"u8 || name == "as"u8;
    }
}

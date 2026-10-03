using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class CompletionQuery
    {
        private SyntaxNode? stringToken;
        private readonly List<LiteralType> stringTypes = [];
        private sealed record StringCompletions(IReadOnlyList<Symbol>? Properties = null, IReadOnlyList<LiteralType>? Types = null, bool NewIdentifier = false);

        private async ValueTask<CompletionList?> StringCompletionsAsync()
        {
            if (previous is not (StringLiteralNode or NoSubstitutionTemplateLiteralNode) || position <= await StartAsync(previous)
                || position > previous.End || position == previous.End && !UnterminatedString(previous)) return null;
            var completions = await StringEntriesAsync(previous);
            if (completions is null) return null;
            stringToken = previous;
            var range = await StringContentRangeAsync(previous);
            newIdentifier = completions.NewIdentifier;
            commitCharacters = newIdentifier ? [] : AllCommitCharacters;
            if (completions.Properties is { } properties)
            {
                kind = CompletionKind.String;
                location = File;
                symbols.AddRange(properties.Select(symbol => new CompletionSymbol(symbol)));
                List<CompletionItem> items = [];
                HashSet<Utf8String> names = [];
                foreach (var entry in symbols)
                {
                    var (name, brackets) = DisplayName(entry);
                    if (name.IsEmpty || !names.Add(name)) continue;
                    if (await SymbolItemAsync(entry, name, brackets) is { } item)
                        items.Add(range is null ? item : item with { TextEdit = new(item.InsertText ?? item.Label, range.Value) });
                }
                return await ApplyDefaultsAsync(items, commitCharacters, null, range);
            }
            if (completions.Types is not { } types) return null;
            stringTypes.AddRange(types);
            int quote = previous is NoSubstitutionTemplateLiteralNode ? '`' : PropertyName(previous).StartsWith("'"u8) ? '\'' : '"';
            List<CompletionItem> literals = [];
            foreach (var type in types)
            {
                var name = Checker.QuoteSymbolText((Utf8String)type.Value!, quote, false)[1..^1];
                var filter = FilterText(default, name);
                literals.Add(new(name, 21, SortText: "11"u8, TextEdit: range is null ? null : new(name, range.Value), FilterText: filter.IsEmpty ? null : filter));
            }
            return await ApplyDefaultsAsync(literals, commitCharacters, null);
        }

        private async ValueTask<StringCompletions?> StringEntriesAsync(SyntaxNode node)
        {
            var parent = SkipParentheses(node.Parent);
            switch (parent)
            {
                case LiteralTypeNode:
                    return await UnionableStringTypeAsync(SkipParentheses(parent.Parent), parent);
                case PropertyAssignmentNode { Parent: ObjectLiteralExpressionNode obj } property when property.Name == node:
                    if (await checker.GetContextualTypeAsync(obj, cancellation: cancellation) is not { } contextual) return null;
                    return new(Properties: await checker.GetObjectCompletionPropertiesAsync(contextual,
                        await checker.GetContextualTypeAsync(obj, ContextFlags.IgnoreNodeInferences, cancellation), obj, cancellation), NewIdentifier: await HasIndexAsync(contextual));
                case PropertyAssignmentNode:
                    if (Ancestor(parent.Parent, CallLike) is not null)
                    {
                        var ordinary = await checker.GetStringCompletionTypesAsync(await checker.GetContextualTypeAsync(node, cancellation: cancellation), cancellation);
                        var inferred = await checker.GetStringCompletionTypesAsync(await checker.GetContextualTypeAsync(node, ContextFlags.IgnoreNodeInferences, cancellation), cancellation);
                        return FromTypes(ordinary.Concat(inferred).DistinctBy(type => type.Value).ToArray());
                    }
                    return await ContextualStringTypesAsync(node);
                case ElementAccessExpressionNode access:
                    return node == UnwrapParentheses(access.ArgumentExpression) ? await StringPropertiesAsync(await checker.GetTypeAtLocationAsync(access.Expression!, cancellation)) : null;
                case CallExpressionNode or NewExpressionNode or JsxAttributeNode:
                    if (parent is CallExpressionNode call && (call.Expression?.Kind == K.ImportKeyword || call.Expression is IdentifierNode { Text.Span: var text }
                        && text.SequenceEqual("require"u8) && call.Arguments is { Count: > 0 } arguments && arguments[0] == node)) return null;
                    if (await CompletionArgumentAsync(parent is JsxAttributeNode ? parent.Parent! : node) is not { } argument) return null;
                    var editing = parent is JsxAttributeNode ? parent : node;
                    List<LiteralType> types = [];
                    bool newName = false;
                    foreach (var signature in await checker.GetCandidateSignaturesForStringLiteralCompletionsAsync(argument.Invocation, editing, cancellation))
                    {
                        if (!signature.HasRestParameter && argument.Count > signature.Parameters.Count) continue;
                        var type = await checker.GetCompletionParameterTypeAsync(signature, argument.Index, cancellation);
                        if (parent is JsxAttributeNode attribute && await checker.Properties.PropertyAsync(type, PropertyName(attribute.Name!), cancellation: cancellation) is { } propertySymbol)
                            type = await checker.GetTypeOfSymbolAtLocationAsync(propertySymbol, attribute, cancellation);
                        newName |= (type.Flags & TypeFlags.String) != 0;
                        types.AddRange(await checker.GetStringCompletionTypesAsync(type, cancellation));
                    }
                    return types.Count == 0 ? await ContextualStringTypesAsync(node) : new(Types: types.DistinctBy(type => type.Value).ToArray(), NewIdentifier: newName);
                case ImportDeclarationNode or ExportDeclarationNode or ExternalModuleReferenceNode or JSDocImportTagNode:
                    return null;
                case CaseOrDefaultClauseNode { Kind: K.CaseClause } clause:
                    var fromContext = await ContextualStringTypesAsync(node, ContextFlags.IgnoreNodeInferences);
                    if (fromContext?.Types is not { } caseTypes) return null;
                    var existing = await CaseValuesAsync((CaseBlockNode)clause.Parent!);
                    return new(Types: caseTypes.Where(type => !existing.Contains(type.Value!)).ToArray());
                case ImportSpecifierNode or ExportSpecifierNode:
                    var aliasName = parent is ImportSpecifierNode import ? import.PropertyName : ((ExportSpecifierNode)parent).PropertyName;
                    if (aliasName is not null && aliasName != node) return null;
                    var bindings = parent.Parent;
                    var moduleNode = bindings is NamedImportsNode ? bindings.Parent?.Parent : bindings?.Parent;
                    if (moduleNode is null || await checker.GetSymbolAtLocationAsync(moduleNode, cancellation) is not { } moduleSymbol) return null;
                    var elements = bindings is NamedImportsNode namedImports ? namedImports.Elements : (bindings as NamedExportsNode)?.Elements;
                    HashSet<Utf8String> names = [];
                    foreach (var element in elements ?? new([])) names.Add(PropertyName(element switch
                    { ImportSpecifierNode i => i.PropertyName ?? i.Name!, ExportSpecifierNode e => e.PropertyName ?? e.Name!, _ => element }));
                    return new(Properties: (await checker.GetCompletionModuleExportsAsync(moduleSymbol, cancellation)).Where(symbol => symbol.Name != "default"u8 && !names.Contains(symbol.Name)).ToArray());
                case BinaryExpressionNode { OperatorToken.Kind: K.InKeyword, Right: { } right }:
                    return new(Properties: (await checker.GetPossibleCompletionPropertiesAsync(await checker.GetTypeAtLocationAsync(right, cancellation), cancellation))
                        .Where(symbol => symbol.ValueDeclaration?.DeclarationName is not PrivateIdentifierNode).ToArray());
                case BinaryExpressionNode: return await ContextualStringTypesAsync(node);
                default: return await ContextualStringTypesAsync(node, ContextFlags.IgnoreNodeInferences) ?? await ContextualStringTypesAsync(node);
            }
        }

        private async ValueTask<StringCompletions?> UnionableStringTypeAsync(SyntaxNode? grandparent, SyntaxNode literal)
        {
            HashSet<Utf8String> used = [];
            while (grandparent is UnionTypeNode union)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var node in union.Types ?? new([]))
                    if (node != literal && node is LiteralTypeNode { Literal: StringLiteralNode text }) used.Add(text.Text);
                grandparent = SkipParentheses(grandparent.Parent);
            }
            StringCompletions? result = null;
            if (grandparent is not null && (CallLike(grandparent) || grandparent is ExpressionWithTypeArgumentsNode or TypeReferenceNode))
            {
                if (Ancestor(literal, node => node.Parent == grandparent) is { } argument)
                    result = new(Types: await checker.GetStringCompletionTypesAsync(await checker.GetTypeArgumentConstraintAsync(argument, cancellation), cancellation));
            }
            else if (grandparent is IndexedAccessTypeNode { IndexType: { } index, ObjectType: { } obj } && index.Pos <= position && position <= index.End)
                result = await StringPropertiesAsync(await checker.GetTypeFromTypeNodeAsync(obj, cancellation));
            else if (grandparent is PropertySignatureDeclarationNode)
                result = new(Types: await checker.GetStringCompletionTypesAsync(await TypeArgumentPropertyConstraintAsync(grandparent), cancellation));
            if (used.Count == 0 || result is null) return result;
            return result with { Properties = result.Properties?.Where(symbol => !used.Contains(symbol.Name)).ToArray(), Types = result.Types?.Where(type => !used.Contains((Utf8String)type.Value!)).ToArray() };
        }

        private async ValueTask<StringCompletions?> ContextualStringTypesAsync(SyntaxNode node, ContextFlags flags = 0)
            => FromTypes(await checker.GetStringCompletionTypesAsync(await ContextualFromParentAsync(node, flags), cancellation));
        private static StringCompletions? FromTypes(IReadOnlyList<LiteralType> types) => types.Count == 0 ? null : new(Types: types);
        private async ValueTask<StringCompletions> StringPropertiesAsync(Type type)
            => new(Properties: (await checker.GetApparentPropertiesAsync(type, cancellation)).Where(symbol => symbol.ValueDeclaration?.DeclarationName is not PrivateIdentifierNode).ToArray(), NewIdentifier: await HasIndexAsync(type));
        private async ValueTask<bool> HasIndexAsync(Type type) => (await checker.IndexesAsync(type, cancellation)).Any(index => (index.KeyType.Flags & (TypeFlags.String | TypeFlags.Number)) != 0);
        private static SyntaxNode? SkipParentheses(SyntaxNode? node) { while (node is ParenthesizedExpressionNode or ParenthesizedTypeNode) node = node.Parent; return node; }
        private static SyntaxNode? UnwrapParentheses(SyntaxNode? node) { while (node is ParenthesizedExpressionNode parentheses) node = parentheses.Expression; return node; }
        private static bool CallLike(SyntaxNode node) => node is CallExpressionNode or NewExpressionNode or TaggedTemplateExpressionNode or DecoratorNode or JsxOpeningElementNode or JsxSelfClosingElementNode;
        private static bool UnterminatedString(SyntaxNode node) => ((node switch
        { StringLiteralNode text => text.TokenFlags, NoSubstitutionTemplateLiteralNode text => text.TemplateFlags, _ => 0 }) & TokenFlags.Unterminated) != 0;
        private async ValueTask<DocumentRange?> StringContentRangeAsync(SyntaxNode node)
        {
            int start = await StartAsync(node), end = node.End - 1;
            if (UnterminatedString(node)) { if (start == end) return null; end = Math.Min(position, node.End); }
            var range = projection.ToRange(start + 1, end);
            return range.Fidelity == MappingFidelity.Exact ? range.Range : null;
        }
    }
}

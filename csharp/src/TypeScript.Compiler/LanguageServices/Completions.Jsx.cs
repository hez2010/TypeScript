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
        private bool jsxIdentifier, jsxOpen, jsxClose, jsxInitializer;
        private SyntaxNode? jsxInitializerNode;

        private async ValueTask JsxContextAsync()
        {
            if (contextToken?.Parent is not { } parent || importStatement is not null) return;
            if (parent is PropertyAccessExpressionNode) { contextToken = parent; parent = parent.Parent!; }
            if (parent == location && (current.Kind == K.GreaterThanToken && parent is JsxElementNode or JsxOpeningElementNode
                || current.Kind == K.LessThanSlashToken && parent is JsxSelfClosingElementNode)) location = current;
            switch (parent)
            {
                case JsxClosingElementNode:
                    if (contextToken.Kind == K.LessThanSlashToken) { jsxClose = true; location = contextToken; }
                    break;
                case BinaryExpressionNode binary when binary.Left!.Pos != binary.Left.End: break;
                case BinaryExpressionNode or JsxSelfClosingElementNode or JsxElementNode or JsxOpeningElementNode:
                    jsxIdentifier = true;
                    if (contextToken.Kind == K.LessThanToken) { jsxOpen = true; location = contextToken; }
                    break;
                case JsxExpressionNode or JsxSpreadAttributeNode:
                    if (previous?.Kind == K.CloseBraceToken || previous is IdentifierNode { Parent: JsxAttributeNode }) jsxIdentifier = true;
                    break;
                case JsxAttributeNode attribute:
                    if (attribute.Initializer == previous && previous!.End < position) jsxIdentifier = true;
                    else if (previous?.Kind == K.EqualsToken || previous is IdentifierNode)
                    {
                        jsxInitializer = previous.Kind == K.EqualsToken;
                        jsxIdentifier = true;
                        if (parent != previous!.Parent && attribute.Initializer is null && await SyntaxNavigation.FindChildOfKindAsync(parent, K.EqualsToken, File, cancellation) is not null)
                            jsxInitializerNode = previous;
                    }
                    break;
            }
        }

        private async ValueTask<SyntaxNode?> JsxContainerAsync()
        {
            if (contextToken is null) return null;
            var parent = contextToken.Parent;
            switch (contextToken.Kind)
            {
                case K.GreaterThanToken: case K.LessThanSlashToken: case K.SlashToken: case K.Identifier:
                case K.PropertyAccessExpression: case K.JsxNamespacedName: case K.JsxAttributes: case K.JsxAttribute: case K.JsxSpreadAttribute:
                    if (parent is JsxSelfClosingElementNode or JsxOpeningElementNode)
                    {
                        var arguments = parent is JsxOpeningElementNode open ? open.TypeArguments : ((JsxSelfClosingElementNode)parent).TypeArguments;
                        if (contextToken.Kind == K.GreaterThanToken && (arguments is null or { Count: 0 }
                            || (await SyntaxNavigation.FindPrecedingTokenAsync(File, contextToken.Pos, cancellation: cancellation))?.Kind == K.SlashToken)) return null;
                        return parent;
                    }
                    if (parent is JsxNamespacedNameNode { Parent: JsxSelfClosingElementNode or JsxOpeningElementNode }) return parent.Parent;
                    return parent is JsxAttributeNode ? parent.Parent?.Parent : null;
                case K.StringLiteral: return parent is JsxAttributeNode or JsxSpreadAttributeNode ? parent.Parent?.Parent : null;
                case K.CloseBraceToken:
                    if (parent is JsxExpressionNode { Parent: JsxAttributeNode attribute }) return attribute.Parent?.Parent;
                    return parent is JsxSpreadAttributeNode ? parent.Parent?.Parent : null;
                default: return null;
            }
        }

        private async ValueTask<bool> JsxSymbolsAsync()
        {
            var container = await JsxContainerAsync();
            var attributes = container switch { JsxOpeningElementNode open => open.Attributes, JsxSelfClosingElementNode self => self.Attributes, _ => null };
            if (attributes is null || await checker.GetContextualTypeAsync(attributes, cancellation: cancellation) is not { } contextual) return false;
            var completionType = await checker.GetContextualTypeAsync(attributes, ContextFlags.IgnoreNodeInferences, cancellation);
            HashSet<Utf8String> existing = [], spread = [];
            foreach (var attribute in attributes.Properties ?? new([]))
            {
                if (await EditingAsync(attribute)) continue;
                if (attribute is JsxAttributeNode { Name: { } name }) existing.Add(PropertyName(name));
                else if (attribute is JsxSpreadAttributeNode { Expression: { } expression })
                    foreach (var symbol in await checker.GetPossibleCompletionPropertiesAsync(await checker.GetTypeAtLocationAsync(expression, cancellation), cancellation)) spread.Add(symbol.Name);
            }
            foreach (var symbol in await checker.GetObjectCompletionPropertiesAsync(contextual, completionType, attributes, cancellation))
                if (!existing.Contains(symbol.Name)) symbols.Add(new(symbol, spread.Contains(symbol.Name) ? "13"u8 : (symbol.Flags & SymbolFlags.Optional) != 0 ? "12"u8 : "11"u8));
            kind = CompletionKind.Member; newIdentifier = false;
            return true;
        }

        private async ValueTask<CompletionList?> JsxClosingAsync()
        {
            if (File.ScriptKind is not (ScriptKind.JSX or ScriptKind.TSX)) return null;
            SyntaxNode? node = location;
            while (node is not null && node is not JsxClosingElementNode)
            {
                if (node.Kind is not (K.LessThanSlashToken or K.GreaterThanToken or K.Identifier or K.PropertyAccessExpression)) return null;
                node = node.Parent;
            }
            if (node is not JsxClosingElementNode { TagName: { } closing, Parent: JsxElementNode { OpeningElement.TagName: { } opening } } element) return null;
            var label = File.Source.Text[await StartAsync(opening)..opening.End];
            if (await SyntaxNavigation.FindChildOfKindAsync(element, K.GreaterThanToken, File, cancellation) is null) label += ">"u8;
            var range = projection.ToRange(await StartAsync(closing), closing.End);
            return range.Fidelity != MappingFidelity.Exact ? null : await ApplyDefaultsAsync([new(label, 7, SortText: "11"u8)], AllCommitCharacters, null, range.Range);
        }

        private async ValueTask<bool> StringAndEmptyObjectAsync(Type type) => type is IntersectionType { Types.Count: 2 } intersection
            && ((intersection.Types[0].Flags & TypeFlags.String) != 0 && await checker.Views.EmptyAnonymousAsync(intersection.Types[1], cancellation)
                || (intersection.Types[1].Flags & TypeFlags.String) != 0 && await checker.Views.EmptyAnonymousAsync(intersection.Types[0], cancellation));
        private static Utf8String EscapeSnippet(Utf8String text) => Utf8String.FromString(text.ToString().Replace("$", "\\$", StringComparison.Ordinal));
    }
}

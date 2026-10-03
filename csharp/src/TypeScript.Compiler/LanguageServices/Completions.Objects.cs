using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class CompletionQuery
    {
        private async ValueTask<Type?> TypeArgumentPropertyConstraintAsync(SyntaxNode node)
        {
            Stack<SyntaxNode> pending = [];
            Type? constraint = null;
            for (var current = node; current is not null; current = current.Parent)
            {
                cancellation.ThrowIfCancellationRequested();
                constraint = await checker.GetTypeArgumentConstraintAsync(current, cancellation);
                if (constraint is not null) break;
                pending.Push(current);
            }
            while (constraint is not null && pending.TryPop(out var current))
            {
                cancellation.ThrowIfCancellationRequested();
                switch (current)
                {
                    case PropertySignatureDeclarationNode:
                        var property = QuerySyntax.Reparsed(current);
                        var name = property.BindingSymbol?.Name ?? (property.DeclarationName is { } declarationName ? PropertyName(declarationName) : default);
                        constraint = name.IsEmpty ? null : await checker.ContextualPropertyAsync(constraint, name, cancellation);
                        break;
                    case { Kind: K.ColonToken, Parent: PropertySignatureDeclarationNode }:
                    case { Kind: K.IntersectionType or K.TypeLiteral or K.UnionType }: break;
                    case { Kind: K.OpenBracketToken }: constraint = await checker.ArrayElementAsync(constraint, cancellation); break;
                    default: return null;
                }
            }
            return constraint;
        }

        private async ValueTask<bool?> TypeArgumentObjectSymbolsAsync()
        {
            var typeLiteral = contextToken?.Kind switch
            {
                K.OpenBraceToken when contextToken.Parent is TypeLiteralNode => contextToken.Parent,
                K.SemicolonToken or K.CommaToken or K.Identifier when contextToken.Parent is PropertySignatureDeclarationNode { Parent: TypeLiteralNode owner } => owner,
                _ => null,
            };
            if (typeLiteral is null) return null;
            var container = typeLiteral.Parent?.Kind == K.IntersectionType ? typeLiteral.Parent : typeLiteral;
            if (await TypeArgumentPropertyConstraintAsync(container) is not { } expected) return null;
            var actual = await checker.GetTypeFromTypeNodeAsync(container, cancellation);
            var names = (await checker.GetPossibleCompletionPropertiesAsync(actual, cancellation)).Select(symbol => symbol.Name).ToHashSet();
            foreach (var symbol in await checker.GetPossibleCompletionPropertiesAsync(expected, cancellation))
                if (!names.Contains(symbol.Name)) symbols.Add(new(symbol));
            kind = CompletionKind.ObjectProperty; newIdentifier = true;
            return true;
        }

        private async ValueTask<bool?> ObjectSymbolsAsync()
        {
            if (contextToken?.Kind == K.DotDotDotToken || await ObjectContainerAsync() is not { } container) return null;
            kind = CompletionKind.ObjectProperty;
            IReadOnlyList<Symbol> members = [];
            NodeList? existing = null;
            if (container is ObjectLiteralExpressionNode obj)
            {
                var contextual = await checker.GetContextualTypeAsync(obj, cancellation: cancellation);
                if (contextual is null)
                {
                    var parent = obj.Parent;
                    while (parent is ParenthesizedExpressionNode) parent = parent.Parent;
                    if (parent is BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken } binary && binary.Left == obj)
                        contextual = await checker.GetTypeAtLocationAsync(parent, cancellation);
                    else if (parent is not null && QuerySyntax.Expression(parent)) contextual = await checker.GetContextualTypeAsync(parent, cancellation: cancellation);
                }
                if (contextual is null) return (obj.Flags & NodeFlags.InWithStatement) != 0 ? false : null;
                var completions = await checker.GetContextualTypeAsync(obj, ContextFlags.IgnoreNodeInferences, cancellation);
                var indexes = await checker.IndexesAsync(completions ?? contextual, cancellation);
                newIdentifier = indexes.Any(index => (index.KeyType.Flags & (TypeFlags.String | TypeFlags.Number)) != 0);
                members = await checker.GetObjectCompletionPropertiesAsync(contextual, completions, obj, cancellation);
                existing = obj.Properties;
                if (members.Count == 0 && !indexes.Any(index => (index.KeyType.Flags & TypeFlags.Number) != 0)) return null;
            }
            else
            {
                newIdentifier = false;
                var root = SemanticSyntax.RootDeclaration(container.Parent!);
                bool typed = root is IInitializedNode { Initializer: not null } || root is ITypedNode { Type: not null } || root.Parent?.Parent?.Kind == K.ForOfStatement;
                if (!typed && root is ParameterDeclarationNode && root.Parent is { } function)
                {
                    if (function is FunctionExpressionNode or ArrowFunctionNode) typed = await checker.GetContextualTypeAsync(function, cancellation: cancellation) is not null;
                    else if (function is MethodDeclarationNode or SetAccessorDeclarationNode && function.Parent is ObjectLiteralExpressionNode owner)
                        typed = await checker.GetContextualTypeAsync(owner, cancellation: cancellation) is not null;
                }
                if (typed)
                {
                    var type = await checker.GetTypeAtLocationAsync(container, cancellation);
                    List<Symbol> accessible = [];
                    foreach (var property in await checker.Properties.GetAsync(type, cancellation))
                        if (await checker.IsCompletionPropertyAccessibleAsync(container, type, property, cancellation)) accessible.Add(property);
                    members = accessible;
                    existing = ((BindingPatternNode)container).Elements;
                }
            }
            var (names, spreadNames) = await ExistingObjectMembersAsync(existing);
            bool methodSnippets = container is ObjectLiteralExpressionNode && preferences.IncludeCompletionsWithObjectLiteralMethodSnippets == true;
            List<CompletionSymbol> methods = [];
            foreach (var member in members)
            {
                if (names.Contains(member.Name)) continue;
                var entry = new CompletionSymbol(member, spreadNames.Contains(member.Name) ? "13"u8 : (member.Flags & SymbolFlags.Optional) != 0 ? "12"u8 : "11"u8);
                if (methodSnippets && DisplayName(entry).Name is { IsEmpty: false } name)
                {
                    entry = entry with { SortText = entry.SortText + "\0"u8 + name + "\0"u8 };
                    if (await ObjectMethodSnippetAsync(member, container) is { } snippet) methods.Add(entry with { ObjectMethod = snippet });
                }
                symbols.Add(entry);
            }
            symbols.AddRange(methods);
            return true;
        }

        private async ValueTask<SyntaxNode?> ObjectContainerAsync()
        {
            if (contextToken?.Parent is not { } parent) return null;
            bool Object(SyntaxNode? node) => node is ObjectLiteralExpressionNode or BindingPatternNode { Kind: K.ObjectBindingPattern };
            switch (contextToken.Kind)
            {
                case K.OpenBraceToken: case K.CommaToken: return Object(parent) ? parent : null;
                case K.AsteriskToken: return parent is MethodDeclarationNode && parent.Parent is ObjectLiteralExpressionNode ? parent.Parent : null;
                case K.AsyncKeyword: return parent.Parent is ObjectLiteralExpressionNode ? parent.Parent : null;
                case K.Identifier:
                    if (PropertyName(contextToken) == "async"u8 && parent is ShorthandPropertyAssignmentNode) return parent.Parent;
                    if (parent.Parent is ObjectLiteralExpressionNode && (parent is SpreadAssignmentNode || parent is ShorthandPropertyAssignmentNode && Line(contextToken.End) != Line(position))) return parent.Parent;
                    break;
                default:
                    if (parent.Parent is MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode && parent.Parent.Parent is ObjectLiteralExpressionNode) return parent.Parent.Parent;
                    if (parent is SpreadAssignmentNode && parent.Parent is ObjectLiteralExpressionNode) return parent.Parent;
                    break;
            }
            var assignment = Ancestor(parent, node => node is PropertyAssignmentNode);
            return contextToken.Kind != K.ColonToken && assignment?.Parent is ObjectLiteralExpressionNode && await LastTokenAsync(assignment) == contextToken ? assignment.Parent : null;
        }

        private async ValueTask<(HashSet<Utf8String> Names, HashSet<Utf8String> SpreadNames)> ExistingObjectMembersAsync(NodeList? members)
        {
            HashSet<Utf8String> names = [], spreadNames = [];
            foreach (var member in members ?? new([]))
            {
                cancellation.ThrowIfCancellationRequested();
                if (member is not (PropertyAssignmentNode or ShorthandPropertyAssignmentNode or BindingElementNode or MethodDeclarationNode
                    or GetAccessorDeclarationNode or SetAccessorDeclarationNode or SpreadAssignmentNode) || await EditingAsync(member)) continue;
                if (member is SpreadAssignmentNode { Expression: { } expression })
                {
                    if (await checker.GetSymbolAtLocationAsync(expression, cancellation) is { } symbol)
                        foreach (var property in await checker.Properties.GetAsync(await checker.GetTypeOfSymbolAtLocationAsync(symbol, expression, cancellation), cancellation)) spreadNames.Add(property.Name);
                }
                else
                {
                    var name = member is BindingElementNode { PropertyName: { } propertyName } ? propertyName is IdentifierNode ? PropertyName(propertyName) : default
                        : member.DeclarationName is { } declarationName ? PropertyName(declarationName) : default;
                    if (!name.IsEmpty) names.Add(name);
                }
            }
            return (names, spreadNames);
        }

        private ValueTask<SyntaxNode?> LastTokenAsync(SyntaxNode node) => SyntaxNavigation.GetLastTokenAsync(node, File, cancellation);

        private async ValueTask<bool> ObjectMemberNeedsCommaAsync()
        {
            if (kind != CompletionKind.ObjectProperty || contextToken is null
                || (await SyntaxNavigation.FindPrecedingTokenAsync(File, contextToken.Pos, contextToken, cancellation: cancellation))?.Kind == K.CommaToken) return false;
            var parent = contextToken.Parent;
            if (parent?.Parent is MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode || parent is SpreadAssignmentNode
                || parent is ShorthandPropertyAssignmentNode && Line(contextToken.End) != Line(position)) return true;
            return Ancestor(parent, node => node is PropertyAssignmentNode) is { } assignment && await LastTokenAsync(assignment) == contextToken;
        }
    }
}

using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class SignatureHelpQuery
    {
        private int SkipTrivia(int position) => new Scanner(file.Source).SkipTriviaAt(position);
        private ValueTask<int> StartAsync(SyntaxNode node) => SyntaxNavigation.GetStartAsync(node, file, cancellation: cancellation);
        private ValueTask<SyntaxNode?> PrecedingAsync(int position) => SyntaxNavigation.FindPrecedingTokenAsync(file, position, cancellation: cancellation);

        private async ValueTask<bool> InsideStringAsync(SyntaxNode node, int position)
        {
            if (node.Kind is not (K.StringLiteral or K.NoSubstitutionTemplateLiteral or K.TemplateHead or K.TemplateMiddle or K.TemplateTail)) return false;
            return await StartAsync(node) < position && (position < node.End || position == node.End && Unterminated(node));
        }
        private static bool Unterminated(SyntaxNode node) => ((node switch
        {
            StringLiteralNode n => n.TokenFlags, NoSubstitutionTemplateLiteralNode n => n.TemplateFlags,
            TemplateHeadNode n => n.TemplateFlags, TemplateMiddleNode n => n.TemplateFlags, TemplateTailNode n => n.TemplateFlags, _ => 0,
        }) & TokenFlags.Unterminated) != 0;

        private async ValueTask<bool> ContainsPrecedingAsync(SyntaxNode token, SyntaxNode container)
        {
            for (var parent = token.Parent; parent is not null; parent = parent.Parent)
                if (await SyntaxNavigation.FindPrecedingTokenAsync(file, token.Pos, parent, true, cancellation) is { } preceding)
                    return container.Pos <= preceding.Pos && preceding.End <= container.End;
            return false;
        }
        private async ValueTask<bool> SyntacticOwnerAsync(SyntaxNode token, SyntaxNode node)
        {
            if (node is not (CallExpressionNode or NewExpressionNode)) return false;
            if (token.Kind == K.LessThanToken)
                return await ContainsPrecedingAsync(token, node is CallExpressionNode call ? call.Expression! : ((NewExpressionNode)node).Expression!);
            if (token.Kind is not (K.OpenParenToken or K.CommaToken) || node.ChildCount == 0) return false;
            return SyntaxNavigation.NonDocumentationChildren(node, file, cancellation).Contains(token);
        }

        private async ValueTask<ArgumentInfo?> ArgumentInfoAsync(SyntaxNode node, int position)
        {
            var parent = node.Parent;
            if (parent is CallExpressionNode or NewExpressionNode)
            {
                if (await ListInfoAsync(node) is not { } list) return null;
                var types = parent is CallExpressionNode call ? call.TypeArguments : ((NewExpressionNode)parent).TypeArguments;
                return new(parent, list.Start, list.End, list.Index, list.Count, types is not null && types.Pos == list.List?.Pos);
            }
            if (node is NoSubstitutionTemplateLiteralNode && parent is TaggedTemplateExpressionNode noSub)
                return await InsideStringAsync(node, position) ? TemplateInfo(noSub, 0) : null;
            if (node is TemplateHeadNode && parent?.Parent is TaggedTemplateExpressionNode headTag)
                return TemplateInfo(headTag, await InsideStringAsync(node, position) ? 0 : 1);
            if (parent is TemplateSpanNode span && parent.Parent?.Parent is TaggedTemplateExpressionNode tag)
            {
                bool inside = await InsideStringAsync(node, position);
                if (node is TemplateTailNode && !inside) return null;
                int index = ((TemplateExpressionNode)span.Parent!).TemplateSpans!.IndexOf(span);
                return TemplateInfo(tag, node.Kind is K.TemplateHead or K.TemplateMiddle or K.TemplateTail or K.NoSubstitutionTemplateLiteral
                    ? inside ? 0 : index + 2 : index + 1);
            }
            var attributes = parent switch { JsxOpeningElementNode n => n.Attributes, JsxSelfClosingElementNode n => n.Attributes, _ => null };
            if (attributes is not null) return new(parent!, attributes.Pos, SkipTrivia(attributes.End) - attributes.Pos, 0, 1);
            if (await PossibleTypeArgumentsAsync(node) is { } typeArguments)
                return new(typeArguments.Called, typeArguments.Called.Pos, node.End, typeArguments.Count, typeArguments.Count + 1, true, true);
            return null;
        }

        internal async ValueTask<(SyntaxNode Invocation, int Index, int Count)?> CompletionArgumentAsync(SyntaxNode node, int position)
            => await ArgumentInfoAsync(node, position) is { TypeArguments: false, IncompleteTypeArguments: false } info
                ? (info.Node, info.Index, info.Count) : null;

        private ArgumentInfo TemplateInfo(TaggedTemplateExpressionNode tag, int index)
        {
            var template = tag.Template!;
            int end = template.End;
            if (template is TemplateExpressionNode { TemplateSpans: { Count: > 0 } spans }
                && spans[^1] is TemplateSpanNode { Literal: { } tail } && tail.Pos == tail.End) end = SkipTrivia(end);
            int start = SkipTrivia(template.Pos);
            return new(tag, start, end - start, index, template is TemplateExpressionNode expression ? expression.TemplateSpans!.Count + 1 : 1);
        }

        private async ValueTask<ListInfo?> ListInfoAsync(SyntaxNode node)
        {
            NodeList? list;
            if (node.Kind is K.OpenParenToken or K.LessThanToken)
                list = node.Parent switch
                {
                    CallExpressionNode n => node.Kind == K.LessThanToken ? n.TypeArguments : n.Arguments,
                    NewExpressionNode n => node.Kind == K.LessThanToken ? n.TypeArguments : n.Arguments, _ => null,
                };
            else if ((list = await SyntaxNavigation.ContainingListAsync(node, file, cancellation)) is null) return null;
            var tokens = ListTokens(list, node.Parent);
            int start = list?.Pos ?? node.End, end = Math.Max(start + 1, SkipTrivia(list?.End ?? node.End));
            return new(list, start, end, node.Kind is K.OpenParenToken or K.LessThanToken ? 0 : await CountAsync(node), await CountAsync(null));

            async ValueTask<int> CountAsync(SyntaxNode? target)
            {
                int index = 0; bool skipComma = false;
                foreach (var token in tokens)
                {
                    if (token == target) return index + (!skipComma && token.Kind == K.CommaToken ? 1 : 0);
                    if (token is SpreadElementNode spread)
                    {
                        if (await checker.GetTypeAtLocationAsync(spread.Expression!, cancellation) is TypeReference { Target: TupleType tuple } && tuple.FixedLength != 0)
                        {
                            int count = 0;
                            foreach (var element in tuple.ElementInfos) { if ((element.Flags & ElementFlags.Required) == 0) break; count++; }
                            index += count;
                        }
                        skipComma = true;
                    }
                    else if (token.Kind != K.CommaToken) { index++; skipComma = true; }
                    else if (skipComma) skipComma = false;
                    else index++;
                }
                return index + (target is null && tokens.Count != 0 && tokens[^1].Kind == K.CommaToken ? 1 : 0);
            }
        }

        private List<SyntaxNode> ListTokens(NodeList? list, SyntaxNode? parent)
        {
            List<SyntaxNode> result = [];
            if (list is null || parent is null) return result;
            int left = list.Pos, index = 0;
            while (left < list.End)
            {
                cancellation.ThrowIfCancellationRequested();
                if (index < list.Count && left == list[index].Pos)
                { result.Add(list[index]); left = list[index++].End; }
                else
                {
                    var scanner = new Scanner(file.Source); scanner.ResetPosition(left); scanner.Scan();
                    result.Add(file.GetOrCreateToken(scanner.Kind, scanner.FullStart, scanner.Position, parent, scanner.Flags));
                    if (scanner.Position <= left) break;
                    left = scanner.Position;
                }
            }
            return result;
        }

        private async ValueTask<ArgumentInfo?> ContextualInfoAsync(SyntaxNode startingToken)
        {
            var node = startingToken;
            if (node.Kind is not (K.OpenParenToken or K.CommaToken))
            {
                node = node.Parent;
                while (node is not null and not ParameterDeclarationNode) node = node.Parent;
            }
            if (node?.Parent is not { } parent) return null;
            Type? contextual; int start, end, index, count;
            if (parent is ParenthesizedExpressionNode or MethodDeclarationNode or FunctionExpressionNode or ArrowFunctionNode)
            {
                if (await ListInfoAsync(node) is not { } list) return null;
                (start, end, index, count) = (list.Start, list.End, list.Index, list.Count);
                contextual = parent is MethodDeclarationNode ? await checker.GetContextualTypeForObjectLiteralElementAsync(parent, cancellation: cancellation)
                    : await checker.GetContextualTypeAsync(parent, cancellation: cancellation);
            }
            else if (parent is BinaryExpressionNode binary && node.Kind != K.OpenParenToken)
            {
                var highest = binary;
                while (highest.Parent is BinaryExpressionNode next) highest = next;
                contextual = await checker.GetContextualTypeAsync(highest, cancellation: cancellation);
                (start, end, index, count) = (parent.Pos, parent.End, BinaryCount(binary) - 1, BinaryCount(highest));
            }
            else return null;
            if (contextual is null) return null;
            contextual = await checker.NonNullableAsync(contextual, cancellation);
            if (contextual.Symbol is not { } symbol) return null;
            var signatures = await checker.SignaturesAsync(contextual, false, cancellation);
            if (signatures.Count == 0) return null;
            if (symbol.Name == Symbol.InternalType)
                foreach (var declaration in symbol.Declarations)
                    if (declaration is FunctionTypeNode && declaration.Parent?.BindingSymbol is { } better) { symbol = better; break; }
            return new(startingToken, start, end, index, count, Contextual: signatures[^1], Symbol: symbol);

            static int BinaryCount(BinaryExpressionNode binary)
            { int count = 2; while (binary.Left is BinaryExpressionNode left) { count++; binary = left; } return count; }
        }

        private async ValueTask<(SyntaxNode Called, int Count)?> PossibleTypeArgumentsAsync(SyntaxNode node)
        {
            if (!file.Source.Text.Span.Contains((byte)'<')) return null;
            int lessThan = 0, count = 0;
            for (SyntaxNode? token = node; token is not null; token = await PrecedingAsync(token.Pos))
            {
                cancellation.ThrowIfCancellationRequested();
                switch (token.Kind)
                {
                    case K.LessThanToken:
                        token = await PrecedingAsync(token.Pos);
                        if (token?.Kind == K.QuestionDotToken) token = await PrecedingAsync(token.Pos);
                        if (token is not IdentifierNode) return null;
                        if (lessThan == 0) return token.Parent?.DeclarationName == token ? null : (token, count);
                        lessThan--; break;
                    case K.GreaterThanGreaterThanGreaterThanToken: lessThan += 3; break;
                    case K.GreaterThanGreaterThanToken: lessThan += 2; break;
                    case K.GreaterThanToken: lessThan++; break;
                    case K.CloseBraceToken: case K.CloseParenToken: case K.CloseBracketToken:
                        token = await MatchingAsync(token, token.Kind == K.CloseBraceToken ? K.OpenBraceToken
                            : token.Kind == K.CloseParenToken ? K.OpenParenToken : K.OpenBracketToken);
                        if (token is null) return null;
                        break;
                    case K.CommaToken: count++; break;
                    case K.EqualsGreaterThanToken: case K.Identifier: case K.StringLiteral: case K.NumericLiteral:
                    case K.BigIntLiteral: case K.TrueKeyword: case K.FalseKeyword: case K.TypeOfKeyword: case K.ExtendsKeyword:
                    case K.KeyOfKeyword: case K.DotToken: case K.BarToken: case K.QuestionToken: case K.ColonToken: break;
                    default: if (!SemanticSyntax.TypeNode(token)) return null; break;
                }
            }
            return null;
        }

        private async ValueTask<SyntaxNode?> MatchingAsync(SyntaxNode token, K kind)
        {
            int guess = file.Source.Text.LastIndexOf(TokenFacts.Text(kind));
            if (guess < 0) return null;
            if (guess < token.Pos && file.Source.Text.LastIndexOf(TokenFacts.Text(token.Kind)) < guess
                && await PrecedingAsync(guess + 1) is { } guessed && guessed.Kind == kind) return guessed;
            int count = 0; var close = token.Kind;
            while (await PrecedingAsync(token.Pos) is { } preceding)
            {
                token = preceding;
                if (token.Kind == kind) { if (count-- == 0) return token; }
                else if (token.Kind == close) count++;
            }
            return null;
        }
    }
}

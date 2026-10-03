using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class CompletionQuery
    {
        private async ValueTask<bool> BlockedAsync()
        {
            var token = contextToken!;
            bool literal = token.Kind is K.RegularExpressionLiteral or K.StringLiteral or K.NoSubstitutionTemplateLiteral or K.TemplateHead or K.TemplateMiddle or K.TemplateTail;
            if (literal && token.Pos < position && position < token.End || position == token.End && (Unterminated(token) || token is RegularExpressionLiteralNode)) return true;
            if (token is BigIntLiteralNode || token is NumericLiteralNode && File.Source.Text[token.Pos..token.End].EndsWith("."u8)) return true;
            if (token.Kind == K.JsxText) return true;
            if (token.Kind == K.GreaterThanToken && !(token.Parent == location && location is JsxOpeningElementNode or JsxSelfClosingElementNode)
                && (token.Parent is JsxOpeningElementNode && location.Parent is not JsxOpeningElementNode
                    || token.Parent is JsxClosingElementNode or JsxSelfClosingElementNode && token.Parent.Parent is JsxElementNode)) return true;
            var parent = token.Parent;
            if (parent is null) return false;
            switch (token.Kind)
            {
                case K.CommaToken:
                    return parent is VariableDeclarationNode or VariableStatementNode or EnumDeclarationNode or InterfaceDeclarationNode or TypeAliasDeclarationNode
                        || parent is VariableDeclarationListNode && !await PossiblyTypeArgumentAsync(token)
                        || Signatures.FunctionLike(parent) && parent is not ConstructorDeclarationNode
                        || parent.Kind == K.ArrayBindingPattern || SemanticSyntax.ClassLike(parent) && TypeParameters(parent) is { } typeParameters && typeParameters.End >= token.Pos;
                case K.DotToken: case K.OpenBracketToken: return parent.Kind == K.ArrayBindingPattern;
                case K.ColonToken: return parent is BindingElementNode;
                case K.OpenParenToken: return parent is CatchClauseNode || Signatures.FunctionLike(parent) && parent is not ConstructorDeclarationNode;
                case K.OpenBraceToken: return parent is EnumDeclarationNode;
                case K.LessThanToken: return SemanticSyntax.ClassLike(parent) || parent is InterfaceDeclarationNode or TypeAliasDeclarationNode || Signatures.FunctionLike(parent);
                case K.StaticKeyword: return parent is PropertyDeclarationNode && !SemanticSyntax.ClassLike(parent.Parent);
                case K.DotDotDotToken: return parent is ParameterDeclarationNode || parent.Parent?.Kind == K.ArrayBindingPattern;
                case K.PublicKeyword: case K.PrivateKeyword: case K.ProtectedKeyword: return parent is ParameterDeclarationNode && parent.Parent is not ConstructorDeclarationNode;
                case K.AsKeyword: return parent is ImportSpecifierNode or ExportSpecifierNode or NamespaceImportNode;
                case K.GetKeyword: case K.SetKeyword: return !FromObjectType(token);
                case K.Identifier:
                    if (parent is ImportSpecifierNode or ExportSpecifierNode && parent.DeclarationName == token && PropertyName(token) == "type"u8) return false;
                    if (Ancestor(parent, node => node is VariableDeclarationNode) is not null && Line(token.End) < Line(position)) return false;
                    break;
                case K.ClassKeyword: case K.EnumKeyword: case K.InterfaceKeyword: case K.FunctionKeyword:
                case K.VarKeyword: case K.ImportKeyword: case K.LetKeyword: case K.ConstKeyword: case K.InferKeyword: return true;
                case K.TypeKeyword: return parent is not ImportSpecifierNode;
                case K.AsteriskToken: return Signatures.FunctionLike(parent) && parent is not MethodDeclarationNode;
            }
            var keyword = KeywordOf(token);
            if (ClassKeyword(keyword) && FromObjectType(token)) return false;
            if (ConstructorParameter(token) && (token is not IdentifierNode || ParameterPropertyKeyword(keyword) || await EditingAsync(token))) return false;
            if (keyword is K.AbstractKeyword or K.ClassKeyword or K.DeclareKeyword or K.EnumKeyword or K.FunctionKeyword or K.InterfaceKeyword or K.LetKeyword
                or K.PrivateKeyword or K.ProtectedKeyword or K.PublicKeyword or K.StaticKeyword or K.VarKeyword) return true;
            if (keyword == K.AsyncKeyword) return parent is PropertyDeclarationNode;
            bool Terminated(int point) => token.Kind != K.EqualsToken && (token.Kind == K.SemicolonToken || Line(token.End) != Line(point));
            if (Ancestor(parent, SemanticSyntax.ClassLike) is not null && token == previous && Terminated(position)) return false;
            if (Ancestor(parent, node => node is PropertyDeclarationNode) is PropertyDeclarationNode property && token != previous
                && SemanticSyntax.ClassLike(previous?.Parent?.Parent) && position <= previous!.End)
            {
                if (Terminated(previous.End)) return false;
                if (token.Kind != K.EqualsToken && (property.Initializer is not null || property.Type is not null)) return true;
            }
            if (keyword == K.ConstKeyword) return true;
            return QuerySyntax.DeclarationName(token) && parent is not (ShorthandPropertyAssignmentNode or JsxAttributeNode)
                && !((SemanticSyntax.ClassLike(parent) || parent is InterfaceDeclarationNode or TypeParameterDeclarationNode) && (token != previous || position > previous!.End));
        }

        private async ValueTask<bool> PossiblyTypeArgumentAsync(SyntaxNode? token)
        {
            if (token is null) return false;
            HashSet<SyntaxNode> seen = [];
            while (await PossibleTypeArgumentsAsync(token) is { } info && seen.Add(info.Called))
            {
                if (QuerySyntax.PartOfType(info.Called)) return true;
                var type = await checker.GetTypeAtLocationAsync(info.Called, cancellation);
                if (OptionalExpressions.Chain(info.Called.Parent))
                    type = OptionalExpressions.Root(info.Called.Parent) ? await checker.NonNullableAsync(type, cancellation) : checker.Optional.RemoveMarker(type);
                if ((await checker.SignaturesAsync(type, info.Called.Parent is NewExpressionNode, cancellation)).Any(signature => signature.TypeParameters.Count > 0 && signature.TypeParameters.Count >= info.Count)) return true;
                token = info.Called;
            }
            return false;
        }

        private async ValueTask<(SyntaxNode Called, int Count)?> PossibleTypeArgumentsAsync(SyntaxNode node)
        {
            if (!File.Source.Text.Span.Contains((byte)'<')) return null;
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
                        if (lessThan == 0) return QuerySyntax.DeclarationName(token) ? null : (token, count);
                        lessThan--; break;
                    case K.GreaterThanGreaterThanGreaterThanToken: lessThan += 3; break;
                    case K.GreaterThanGreaterThanToken: lessThan += 2; break;
                    case K.GreaterThanToken: lessThan++; break;
                    case K.CloseBraceToken: case K.CloseParenToken: case K.CloseBracketToken:
                        token = await MatchingAsync(token, token.Kind == K.CloseBraceToken ? K.OpenBraceToken : token.Kind == K.CloseParenToken ? K.OpenParenToken : K.OpenBracketToken);
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

        private async ValueTask<SyntaxNode?> MatchingAsync(SyntaxNode token, K open)
        {
            int depth = 0; var close = token.Kind;
            while (await PrecedingAsync(token.Pos) is { } previousToken)
            {
                token = previousToken;
                if (token.Kind == open) { if (depth-- == 0) return token; }
                else if (token.Kind == close) depth++;
            }
            return null;
        }

        private ValueTask<SyntaxNode?> PrecedingAsync(int point) => SyntaxNavigation.FindPrecedingTokenAsync(File, point, cancellation: cancellation);
    }
}

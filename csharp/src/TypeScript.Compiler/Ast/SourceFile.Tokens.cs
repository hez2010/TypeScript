using System.Collections.Concurrent;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Ast;

public sealed partial class SourceFileNode
{
    private ConcurrentDictionary<(SyntaxNode Parent, int Pos, int End), SyntaxNode>? tokens;

    internal SyntaxNode GetOrCreateToken(SyntaxKind kind, int pos, int end, SyntaxNode parent, TokenFlags flags)
    {
        if ((parent.Flags & NodeFlags.Reparsed) != 0) throw new InvalidOperationException("Cannot create tokens in reparsed syntax");
        if (tokens is null) Interlocked.CompareExchange(ref tokens, new(), null);
        var token = tokens.GetOrAdd((parent, pos, end), key =>
        {
            var text = Source.Text[key.Pos..key.End];
            var factory = new NodeFactory();
            SyntaxNode result = kind switch
            {
                SyntaxKind.NumericLiteral => factory.NewNumericLiteral(text, flags),
                SyntaxKind.BigIntLiteral => factory.NewBigIntLiteral(text, flags),
                SyntaxKind.StringLiteral => factory.NewStringLiteral(text, flags),
                SyntaxKind.JsxText or SyntaxKind.JsxTextAllWhiteSpaces => factory.NewJsxText(text, kind == SyntaxKind.JsxTextAllWhiteSpaces),
                SyntaxKind.RegularExpressionLiteral => factory.NewRegularExpressionLiteral(text, flags),
                SyntaxKind.NoSubstitutionTemplateLiteral => factory.NewNoSubstitutionTemplateLiteral(text, flags),
                SyntaxKind.TemplateHead => factory.NewTemplateHead(text, default, flags),
                SyntaxKind.TemplateMiddle => factory.NewTemplateMiddle(text, default, flags),
                SyntaxKind.TemplateTail => factory.NewTemplateTail(text, default, flags),
                SyntaxKind.Identifier => factory.NewIdentifier(text),
                SyntaxKind.PrivateIdentifier => factory.NewPrivateIdentifier(text),
                _ => factory.NewToken(kind),
            };
            result.Pos = key.Pos; result.End = key.End; result.Parent = key.Parent;
            return result;
        });
        if (token.Kind != kind && !(token is JsxTextNode && kind is SyntaxKind.JsxText or SyntaxKind.JsxTextAllWhiteSpaces))
            throw new InvalidOperationException($"Token cache mismatch: {token.Kind} != {kind}");
        return token;
    }
}

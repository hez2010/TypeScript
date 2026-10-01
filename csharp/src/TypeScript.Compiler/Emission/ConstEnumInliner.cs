using System.Numerics;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

internal sealed class ConstEnumInliner : SyntaxRewriter
{
    private readonly CompilerOptions options;
    private readonly Checker checker;

    internal ConstEnumInliner(EmitContext context, CompilerOptions options, Checker checker, CancellationToken cancellation = default)
        : base(context, cancellation)
    {
        if (options.IsolatedModules == true || options.VerbatimModuleSyntax == true)
            throw new ArgumentException("Const enums are not inlined under isolated modules", nameof(options));
        this.options = options;
        this.checker = checker;
    }

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        if (node is PropertyAccessExpressionNode or ElementAccessExpressionNode && Context.ParseNode(node) is { } parsed
            && await checker.GetConstantValueForEmitAsync(parsed, Cancellation) is { } value)
        {
            var replacement = value is BigInteger integer
                ? integer.Sign < 0 ? Context.Factory.NewPrefixUnaryExpression(SyntaxKind.MinusToken,
                    Context.Factory.NewBigIntLiteral(Utf8String.Format(BigInteger.Abs(integer)), TokenFlags.None))
                    : (SyntaxNode)Context.Factory.NewBigIntLiteral(Utf8String.Format(integer), TokenFlags.None)
                : Context.ConstantExpression(value);
            if (replacement is not null)
            {
                if (options.RemoveComments != true && parsed.Pos >= 0 && parsed.End >= 0 && SemanticSyntax.Source(parsed) is { } source)
                {
                    var text = TransformSyntax.SourceText(source, parsed).Replace("*/"u8, "*_/"u8);
                    Context.AddTrailingComment(replacement, new(SyntaxKind.MultiLineCommentTrivia, Utf8String.Concat(" "u8, text, " "u8)));
                }
                return replacement;
            }
        }
        return await VisitEachChildAsync(node);
    }
}

using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

internal sealed class TaggedTemplateTransformer(EmitContext context, CancellationToken cancellation = default) : SyntaxRewriter(context, cancellation)
{
    private SourceFileNode? source;
    private List<SyntaxNode> declarations = [];
    private NodeFactory F => Context.Factory;

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        if (node is SourceFileNode file)
        {
            var saved = (source, declarations);
            (source, declarations) = (file, []);
            try
            {
                var result = (SourceFileNode)(await VisitEachChildAsync(file))!;
                if (declarations.Count != 0)
                {
                    result = Context.Clone(result);
                    result.Statements = new([.. result.Statements ?? new([]),
                        F.NewVariableStatement(null, F.NewVariableDeclarationList(new(declarations.ToArray()), NodeFlags.None))], file.Statements?.Pos ?? -1, file.Statements?.End ?? -1);
                }
                foreach (var helper in Context.ReadHelpers()) Context.AddHelper(result, helper);
                return result;
            }
            finally { (source, declarations) = saved; }
        }
        if (node is TaggedTemplateExpressionNode tagged && Pieces(tagged.Template!).Any(p => (Data(p).Flags & TokenFlags.ContainsInvalidEscape) != 0))
            return await TaggedAsync(tagged);
        return await VisitEachChildAsync(node);
    }

    private async ValueTask<SyntaxNode> TaggedAsync(TaggedTemplateExpressionNode node)
    {
        var tag = await VisitAsync(node.Tag);
        var pieces = Pieces(node.Template!).ToArray();
        List<SyntaxNode> cooked = [], raw = [];
        foreach (var piece in pieces)
        {
            var data = Data(piece);
            cooked.Add((data.Flags & TokenFlags.IsInvalid) != 0 ? Context.VoidZero() : F.NewStringLiteral(data.Text, TokenFlags.None));
            var text = data.Raw;
            if (text.Length == 0)
            {
                text = TransformSyntax.SourceText(source!, piece);
                int suffix = piece.Kind is SyntaxKind.NoSubstitutionTemplateLiteral or SyntaxKind.TemplateTail ? 1 : 2;
                text = text[1..(text.Length - suffix)];
            }
            text = text.Replace("\r\n"u8, "\n"u8).Replace("\r"u8, "\n"u8);
            raw.Add(EmitContext.CopyRange(F.NewStringLiteral(text, TokenFlags.None), piece));
        }
        List<SyntaxNode> substitutions = [];
        if (node.Template is TemplateExpressionNode expression)
            foreach (TemplateSpanNode span in expression.TemplateSpans ?? new([])) substitutions.Add((await VisitAsync(span.Expression))!);
        Context.RequestHelper(EmitHelpers.MakeTemplateObject);
        var helperName = F.NewIdentifier("__makeTemplateObject"u8);
        Context.SetFlags(helperName, EmitFlags.HelperName);
        SyntaxNode template = F.NewCallExpression(helperName, null, null,
            new([F.NewArrayLiteralExpression(new(cooked.ToArray()), false), F.NewArrayLiteralExpression(new(raw.ToArray()), false)]), NodeFlags.None);
        if (source!.ExternalModuleIndicator is not null)
        {
            var name = Context.NewUniqueName("templateObject"u8);
            declarations.Add(F.NewVariableDeclaration(name, null, null, null));
            template = Context.Binary(name, SyntaxKind.BarBarToken, Context.Binary(name, SyntaxKind.EqualsToken, template));
        }
        return EmitContext.CopyRange(F.NewCallExpression(tag, null, null, new([template, .. substitutions]), NodeFlags.None), node);
    }

    private static IEnumerable<SyntaxNode> Pieces(SyntaxNode node)
    {
        if (node is NoSubstitutionTemplateLiteralNode) yield return node;
        else
        {
            var expression = (TemplateExpressionNode)node;
            yield return expression.Head!;
            foreach (TemplateSpanNode span in expression.TemplateSpans ?? new([])) yield return span.Literal!;
        }
    }

    private static (Utf8String Text, Utf8String Raw, TokenFlags Flags) Data(SyntaxNode node) => node switch
    {
        NoSubstitutionTemplateLiteralNode n => (n.Text, default, n.TemplateFlags),
        TemplateHeadNode n => (n.Text, n.RawText, n.TemplateFlags),
        TemplateMiddleNode n => (n.Text, n.RawText, n.TemplateFlags),
        TemplateTailNode n => (n.Text, n.RawText, n.TemplateFlags),
        _ => throw new ArgumentException("Expected a template literal", nameof(node))
    };
}

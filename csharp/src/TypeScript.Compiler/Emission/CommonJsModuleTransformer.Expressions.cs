using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class CommonJsModuleTransformer
{
    private async ValueTask<SyntaxNode> BinaryAsync(BinaryExpressionNode node, bool discard)
    {
        var kind = node.OperatorToken!.Kind;
        if (kind == K.EqualsToken && node.Left is ObjectLiteralExpressionNode or ArrayLiteralExpressionNode) return await DestructuringAsync(node);
        if (kind is >= K.FirstAssignment and <= K.LastAssignment && node.Left is IdentifierNode name && CanAssign(name))
        {
            var exports = await ExportsAsync(name);
            var result = (await VisitEachChildAsync(node))!;
            if (result is BinaryExpressionNode assignment && kind is K.EqualsToken or K.AmpersandAmpersandEqualsToken or K.BarBarEqualsToken or K.QuestionQuestionEqualsToken)
                assignment.Right = PreserveAssignedName(name, assignment.Left!, node.Right, assignment.Right);
            foreach (var export in exports) result = ExportExpression(export, result, new(node.Pos, node.End));
            return result;
        }
        if (kind == K.CommaToken)
        {
            var result = Context.Clone(node); result.Modifiers = null; result.Type = null;
            result.Left = await InAsync(node.Left, discard: true); result.Right = await InAsync(node.Right, discard: discard);
            return result;
        }
        return (await VisitEachChildAsync(node))!;
    }

    private async ValueTask<SyntaxNode> DestructuringAsync(BinaryExpressionNode node)
    {
        var result = Context.Clone(node); result.Modifiers = null; result.Type = null;
        result.Left = await new AssignmentPatternRewriter(this).VisitAsync(node.Left);
        result.Right = await VisitAsync(node.Right);
        return result;
    }

    private async ValueTask<SyntaxNode> PrefixAsync(PrefixUnaryExpressionNode node)
    {
        if (node.Operator is K.PlusPlusToken or K.MinusMinusToken && node.Operand is IdentifierNode name && (Context.GetFlags(name) & EmitFlags.LocalName) == 0)
        {
            var exports = await ExportsAsync(name);
            if (exports.Count > 0)
            {
                var prefix = Context.Clone(node); prefix.Operand = await VisitAsync(node.Operand);
                SyntaxNode result = prefix;
                foreach (var export in exports) result = Located(ExportExpression(export, result), node, false);
                return result;
            }
        }
        return (await VisitEachChildAsync(node))!;
    }

    private async ValueTask<SyntaxNode> PostfixAsync(PostfixUnaryExpressionNode node, bool discard)
    {
        if (node.Operator is K.PlusPlusToken or K.MinusMinusToken && node.Operand is IdentifierNode name && (Context.GetFlags(name) & EmitFlags.LocalName) == 0)
        {
            var exports = await ExportsAsync(name);
            if (exports.Count > 0)
            {
                var postfix = Context.Clone(node); postfix.Operand = await VisitAsync(node.Operand);
                SyntaxNode result = postfix;
                IdentifierNode? temp = null;
                if (!discard)
                {
                    temp = Context.NewTempVariable(); Context.AddVariableDeclaration(temp);
                    result = Located(Assign(temp, result), node, false);
                }
                result = Located(Context.Binary(result, K.CommaToken, await IdentifierAsync(Context.Clone(name))), node, false);
                foreach (var export in exports) result = Located(ExportExpression(export, result), node, false);
                if (temp is not null) result = Located(Context.Binary(result, K.CommaToken, temp), node, false);
                return result;
            }
        }
        return (await VisitEachChildAsync(node))!;
    }

    private async ValueTask<SyntaxNode> CallAsync(CallExpressionNode node)
    {
        bool import = node.Expression?.Kind == K.ImportKeyword;
        bool rewrite = options.RewriteRelativeImportExtensions == true && (import && node.Arguments is { Count: > 0 }
            || (node.Flags & NodeFlags.JavaScriptFile) != 0 && node.Expression is IdentifierNode { Text: var name } && name == "require"u8 && node.Arguments is { Count: 1 });
        bool transformImport = !(options.EmitModule is >= ModuleKind.Node16 and <= ModuleKind.NodeNext or ModuleKind.Preserve)
            && moduleFormat(source!) < ModuleKind.ES2015;
        if (import && transformImport)
        {
            if (options.EmitModule == ModuleKind.None && options.EmitTargetYear >= 2020) return (await VisitEachChildAsync(node))!;
            var moduleName = ModuleUtilities.ExternalModuleName(F, node);
            var first = await VisitAsync(node.Arguments is { Count: > 0 } ? node.Arguments[0] : null);
            SyntaxNode? argument = moduleName is StringLiteralNode module && (first is not StringLiteralNode literal || module.Text != literal.Text) ? moduleName
                : first is not null && rewrite ? first is StringLiteralNode ? ModuleUtilities.RewriteSpecifier(Context, first, options)
                    : Context.HelperCall(EmitHelpers.RewriteRelativeImportExtensions, "__rewriteRelativeImportExtension"u8,
                        options.Jsx == JsxEmit.Preserve ? [first, F.NewKeywordExpression(K.TrueKeyword)] : [first]) : first;
            bool sync = argument is not null && (argument is IdentifierNode || !TransformSyntax.SimpleCopiable(argument));
            var resolve = Call(Property(F.NewIdentifier("Promise"u8), "resolve"u8), sync ? [F.NewTemplateExpression(
                F.NewTemplateHead(default, default, TokenFlags.None), new([F.NewTemplateSpan(argument, F.NewTemplateTail(default, default, TokenFlags.None))]))] : []);
            var require = ImportStar(Call(F.NewIdentifier("require"u8), sync ? [F.NewIdentifier("s"u8)] : argument is null ? [] : [argument]));
            var arrow = F.NewArrowFunction(null, null,
                new(sync ? [F.NewParameterDeclaration(null, null, F.NewIdentifier("s"u8), null, null, null)] : []),
                null, null, F.NewToken(K.EqualsGreaterThanToken), require);
            return Call(Property(resolve, "then"u8), [arrow]);
        }
        if (rewrite)
        {
            var result = Context.Clone(node); result.Expression = await VisitAsync(node.Expression); result.TypeArguments = null;
            List<SyntaxNode> arguments = [];
            for (int i = 0; i < node.Arguments!.Count; i++)
            {
                var argument = (await VisitAsync(node.Arguments[i]))!;
                arguments.Add(i == 0 ? ModuleUtilities.RewriteDynamicSpecifier(Context, argument, options) : argument);
            }
            result.Arguments = new(arguments.ToArray(), node.Arguments.Pos, node.Arguments.End);
            return result;
        }
        if (CallTarget(node.Expression!) is IdentifierNode identifier)
        {
            var result = Context.Clone(node); result.Expression = await VisitAsync(node.Expression); result.TypeArguments = null;
            result.Arguments = await VisitListAsync(node.Arguments);
            if (CallTarget(result.Expression!) is not IdentifierNode && (Context.GetFlags(identifier) & EmitFlags.HelperName) == 0) Context.AddFlags(result, EmitFlags.IndirectCall);
            return result;
        }
        return (await VisitEachChildAsync(node))!;
    }

    private async ValueTask<SyntaxNode> TaggedAsync(TaggedTemplateExpressionNode node)
    {
        if (CallTarget(node.Tag!) is not IdentifierNode name) return (await VisitEachChildAsync(node))!;
        var result = Context.Clone(node); result.Tag = await VisitAsync(node.Tag); result.QuestionDotToken = null; result.TypeArguments = null;
        result.Template = await VisitAsync(node.Template);
        if (CallTarget(result.Tag!) is not IdentifierNode && (Context.GetFlags(name) & EmitFlags.HelperName) == 0) Context.AddFlags(result, EmitFlags.IndirectCall);
        return result;
    }

    private static SyntaxNode CallTarget(SyntaxNode node)
    {
        while (node is ParenthesizedExpressionNode or PartiallyEmittedExpressionNode) node = TransformSyntax.Expression(node)!;
        return node;
    }
}

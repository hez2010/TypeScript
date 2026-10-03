using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly List<Diagnostic> suggestionDiagnostics = [];

    internal IReadOnlyList<Diagnostic> SuggestionsForFile(SourceFileNode source)
        => SkipProgramFile(source) ? [] : DiagnosticCollection.SortAndDeduplicate(
            suggestionDiagnostics.Concat(program.SuggestionDiagnostics).Where(diagnostic => diagnostic.FileName == source.FileName));

    public async ValueTask DeprecatedSignatureAsync(SyntaxNode node, Signature signature, CancellationToken cancellation)
    {
        if ((signature.Flags & SignatureFlags.IsSignatureCandidateForOverloadFailure) != 0
            || signature.Declaration is not { } declaration || !program.Deprecations.Declaration(declaration)) return;
        var location = DeprecatedSuggestionNode(node);
        var name = InvokedName(node switch { JsxOpeningElementNode jsx => jsx.TagName, JsxSelfClosingElementNode jsx => jsx.TagName, _ => CallArguments.Target(node) });
        var diagnostic = CheckerDiagnostic.Create(location, name.IsEmpty ? Messages.X_0_is_deprecated
            : Messages.The_signature_0_of_1_is_deprecated, await TypeDisplay.GetSignatureAsync(signature, cancellation), name);
        ExpressionSuggestion(location, Deprecations.Related(diagnostic, [declaration]));
    }

    private async ValueTask DeprecatedTypeAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (await Nodes.FromNodeAsync(node, cancellation) == context.ErrorType) return;
        var symbol = links.SymbolNodes.TryGet(node)?.ResolvedSymbol;
        if (symbol?.Declarations.Any(declaration => (declaration is TypeParameterDeclarationNode or ClassDeclarationNode or InterfaceDeclarationNode
            or TypeAliasDeclarationNode or EnumDeclarationNode
            || declaration is ImportClauseNode && SemanticSyntax.TypeOnly(declaration)
            || declaration is ImportSpecifierNode or ExportSpecifierNode && SemanticSyntax.TypeOnly(declaration.Parent!.Parent!))
            && program.Deprecations.Declaration(declaration)) == true)
            program.DeprecatedSuggestion(DeprecatedSuggestionNode(node), symbol.Declarations, symbol.Name);
    }

    private static SyntaxNode DeprecatedSuggestionNode(SyntaxNode node)
    {
        while (true)
        {
            node = MemberAccessRules.SkipParentheses(node);
            var next = node switch
            {
                CallExpressionNode call => call.Expression, NewExpressionNode call => call.Expression,
                DecoratorNode decorator => decorator.Expression, TaggedTemplateExpressionNode template => template.Tag,
                JsxOpeningElementNode element => element.TagName, JsxSelfClosingElementNode element => element.TagName,
                _ => null
            };
            if (next is not null) { node = next; continue; }
            return node switch { PropertyAccessExpressionNode access => access.Name!, ElementAccessExpressionNode access => access.ArgumentExpression!,
                TypeReferenceNode { TypeName: QualifiedNameNode name } => name.Right!, _ => node };
        }
    }

    private static Utf8String InvokedName(SyntaxNode? node)
    {
        var suffixes = new Stack<Utf8String>();
        while (true)
        {
            if (node is PropertyAccessExpressionNode property)
            { suffixes.Push(SyntaxNameText.Get(property.Name!)); node = property.Expression; }
            else if (node is ElementAccessExpressionNode { ArgumentExpression: IdentifierNode or PrivateIdentifierNode or StringLiteralNode or NumericLiteralNode } element)
            { suffixes.Push(element.ArgumentExpression switch { StringLiteralNode literal => literal.Text, NumericLiteralNode literal => literal.Text, _ => SyntaxNameText.Get(element.ArgumentExpression) }); node = element.Expression; }
            else break;
        }
        if (node is not (IdentifierNode or JsxNamespacedNameNode)) return default;
        var text = new Utf8StringBuilder(SyntaxNameText.Get(node));
        while (suffixes.TryPop(out var name)) text.Append((byte)'.').Append(name);
        return text.ToUtf8String();
    }
}

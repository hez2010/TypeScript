using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : ITypeAssertionHost, IInstantiationExpressionHost, IValueExpressionHost
{
    internal TypeAssertions Assertions { get; }
    internal InstantiationExpressions InstantiationExpressions { get; }
    internal ValueExpressionChecks ValueExpressions { get; }
    internal TypeDisplay TypeDisplay { get; }
    internal Dictionary<SyntaxNode, TextSlice> InstantiationErrors { get; } = [];
    internal Action? BeforeInstantiationDiagnostic { get; set; }
    private Type? importMetaType;

    public async ValueTask<Type> ImportMetaTypeAsync(CancellationToken cancellation) =>
            importMetaType ??= await program.Globals.GetAsync("ImportMeta", 0, true, cancellation);

    public bool ErasableSyntaxOnly => program.Symbols.Program.Configuration.Options.ErasableSyntaxOnly == true;

    public async ValueTask InapplicableInstantiationAsync(SyntaxNode node, Type type, CancellationToken cancellation)
    {
        BeforeInstantiationDiagnostic?.Invoke();
        TextSlice text = await TypeDisplay.GetAsync(type, cancellation);
        cancellation.ThrowIfCancellationRequested();
        InstantiationErrors[node] = text;
        ListError(
            node,
            node is ExpressionWithTypeArgumentsNode expression ? expression.TypeArguments! : ((TypeQueryNode)node).TypeArguments!,
            DiagnosticCode.Type0HasNoSignaturesForWhichTheTypeArgumentListIsApplicable, text);
    }

    public void InstantiationGrammar(SyntaxNode node, NodeList? arguments)
    {
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        if (node is ExpressionWithTypeArgumentsNode { Expression.Kind: SyntaxKind.ImportKeyword } && arguments is not null)
            Error(node, DiagnosticCode.ThisUseOfImportIsInvalidImportCallsCanBeWrittenButTheyMustHaveParenthesesAndCannotHaveTypeArguments);
        else if (arguments?.HasTrailingComma == true)
            TrailingCommaError(node, arguments);
        else if (arguments?.Count == 0)
            EmptyTypeListError(node, arguments, DiagnosticCode.TypeArgumentListCannotBeEmpty);
    }
}

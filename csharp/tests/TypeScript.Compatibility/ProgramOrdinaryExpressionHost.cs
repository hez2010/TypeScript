using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal sealed partial class ProgramTypeHost : ITypeAssertionHost, IInstantiationExpressionHost, IValueExpressionHost
{
    internal TypeAssertions Assertions { get; }
    internal InstantiationExpressions InstantiationExpressions { get; }
    internal ValueExpressionChecks ValueExpressions { get; }
    internal TypeDisplay TypeDisplay { get; }
    internal Dictionary<SyntaxNode, string> InstantiationErrors { get; } = [];
    internal Action? BeforeInstantiationDiagnostic { get; set; }
    private Type? importMetaType;

    public async ValueTask<Type> ImportMetaTypeAsync(CancellationToken cancellation) =>
            importMetaType ??= await program.Globals.GetAsync("ImportMeta", 0, true, cancellation);

    public bool ErasableSyntaxOnly => program.Symbols.Program.Configuration.Options.Boolean("erasableSyntaxOnly") == true;

    public async ValueTask InapplicableInstantiationAsync(SyntaxNode node, Type type, CancellationToken cancellation)
    {
        BeforeInstantiationDiagnostic?.Invoke();
        string text = await TypeDisplay.GetAsync(type, cancellation);
        cancellation.ThrowIfCancellationRequested();
        InstantiationErrors[node] = text;
        Error(node, 2635);
    }

    public void InstantiationGrammar(SyntaxNode node, NodeList? arguments)
    {
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        if (node is ExpressionWithTypeArgumentsNode { Expression.Kind: SyntaxKind.ImportKeyword } && arguments is not null)
            Error(node, 1326);
        else if (arguments?.HasTrailingComma == true)
            Error(node, 1009);
        else if (arguments?.Count == 0)
            Error(node, 1099);
    }
}

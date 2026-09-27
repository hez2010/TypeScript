using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IEnumValueHost
{
    internal EnumValues EnumValues { get; }
    internal Action<SyntaxNode>? BeforeConstantReference { get; set; }
    public bool IsolatedModules => program.Symbols.Program.Configuration.Options.Boolean("isolatedModules") == true
        || program.Symbols.Program.Configuration.Options.Boolean("verbatimModuleSyntax") == true;

    public void EnumError(SyntaxNode node, int code) => Error(node, code);

    public ValueTask<bool> DeclaredBeforeUseAsync(SyntaxNode declaration, SyntaxNode use, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        BeforeConstantReference?.Invoke(declaration);
        return program.DeclarationOrder.BeforeUseAsync(declaration, use, cancellation);
    }

    public async ValueTask CheckComputedEnumAsync(EnumMemberNode member, CancellationToken cancellation)
    {
        var type = await LiteralExpressionAsync(member.Initializer!, cancellation);
        if (!await AssignableAsync(type, context.NumberType, cancellation))
            Error(member.Initializer!, 18033, await TypeDisplay.GetAsync(type, cancellation), "number");
    }
}

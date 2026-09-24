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
        if (member.Initializer is IdentifierNode or PropertyAccessExpressionNode)
        {
            var symbol = await program.EntityNames.ResolveAsync(member.Initializer, SymbolFlags.Value, cancellation: cancellation);
            if (symbol is not null && (symbol.Flags & SymbolFlags.EnumMember) != 0)
            {
                if (!await AssignableAsync(await Values.GetAsync(symbol, cancellation), context.NumberType, cancellation))
                    Error(member.Initializer, 18033);
                return;
            }
        }
        var type = await LiteralExpressionAsync(member.Initializer!, cancellation);
        if (!await AssignableAsync(type, context.NumberType, cancellation))
            Error(member.Initializer!, 18033);
    }
}

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

    public void EnumError(SyntaxNode node, int code) => Error(node, code,
        code == 18055 && node.Parent is EnumMemberNode member
            ? [SyntaxNameText.Get(((EnumDeclarationNode)member.Parent!).Name!) + "." + SyntaxNameText.Get(member.Name!)]
            : code == 2565 ? [node is ElementAccessExpressionNode element ? element.ArgumentExpression is StringLiteralNode text ? text.Text
                : CheckerDiagnostic.DeclarationName(element.ArgumentExpression!)
                : CheckerDiagnostic.DeclarationName(node is PropertyAccessExpressionNode access ? access.Name! : node)] : []);

    public ValueTask<bool> DeclaredBeforeUseAsync(SyntaxNode declaration, SyntaxNode use, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        BeforeConstantReference?.Invoke(declaration);
        return program.DeclarationOrder.BeforeUseAsync(declaration, use, cancellation);
    }

    public async ValueTask CheckComputedEnumAsync(EnumMemberNode member, CancellationToken cancellation)
    {
        var type = await LiteralExpressionAsync(member.Initializer!, cancellation);
        await RelationDiagnostics.CheckAsync(type, context.NumberType, RelationKind.Assignable,
            member.Initializer!, null, 18033, cancellation);
    }
}

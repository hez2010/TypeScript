using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IEnumValueHost
{
    internal EnumValues EnumValues { get; }
    internal Action<SyntaxNode>? BeforeConstantReference { get; set; }
    public bool IsolatedModules => program.Symbols.Program.Configuration.Options.IsolatedModules == true
        || program.Symbols.Program.Configuration.Options.VerbatimModuleSyntax == true;

    public void EnumError(SyntaxNode node, DiagnosticCode code) => Error(node, code,
        code == DiagnosticCode.X0HasAStringTypeButMustHaveSyntacticallyRecognizableStringSyntaxWhenIsolatedModulesIsEnabled
            && node.Parent is EnumMemberNode member
            ? [Utf8String.Concat(SyntaxNameText.Get(((EnumDeclarationNode)member.Parent!).Name!), "."u8, SyntaxNameText.Get(member.Name!))]
            : code == DiagnosticCode.Property0IsUsedBeforeBeingAssigned ? [node is ElementAccessExpressionNode element ? element.ArgumentExpression is StringLiteralNode text ? text.Text
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
            member.Initializer!, null, DiagnosticCode.Type0IsNotAssignableToType1AsRequiredForComputedEnumMemberValues, cancellation);
    }
}

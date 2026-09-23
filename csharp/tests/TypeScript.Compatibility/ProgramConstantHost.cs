using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compatibility;

internal sealed partial class ProgramTypeHost : IEnumValueHost
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
        if (declaration is not (EnumMemberNode or VariableDeclarationNode))
            throw new InvalidOperationException("Constant probe requires enum or constant declarations");
        if (SemanticSyntax.Source(declaration) != SemanticSyntax.Source(use))
            return ValueTask.FromResult(true);
        for (var parent = use; parent is not null; parent = parent.Parent)
            if ((parent.Flags & (NodeFlags.JSDoc | NodeFlags.Ambient)) != 0 || SemanticSyntax.TypeNode(parent))
                return ValueTask.FromResult(true);
        if (declaration.Pos <= use.Pos)
            return ValueTask.FromResult(true);
        for (var current = use; current is not null; current = current.Parent)
        {
            if (current is ClassStaticBlockDeclarationNode)
                return ValueTask.FromResult(false);
            if (SemanticSyntax.FunctionDeclarationLike(current) && !SemanticSyntax.ImmediatelyInvoked(current))
                return ValueTask.FromResult(true);
            if (current.Parent is PropertyDeclarationNode property && property.Initializer == current && !SemanticSyntax.IsStatic(property))
                return ValueTask.FromResult(true);
            if (declaration.Parent?.Parent == current || declaration.Parent == current)
                break;
        }
        return ValueTask.FromResult(false);
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

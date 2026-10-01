using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Emission;

internal sealed class UseStrictTransformer(EmitContext context, CompilerOptions options, Func<SourceFileNode, ModuleKind> moduleFormat, CancellationToken cancellation = default)
    : SyntaxRewriter(context, cancellation)
{
    protected override ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        if (node is not SourceFileNode file || file.ScriptKind == ScriptKind.JSON
            || file.ExternalModuleIndicator is not null && options.EmitModule >= ModuleKind.ES2015
                && (options.EmitModule == ModuleKind.Preserve || moduleFormat(file) >= ModuleKind.ES2015)
            || file.Statements is { Count: > 0 } && file.Statements[0] is ExpressionStatementNode { Expression: StringLiteralNode { Text: var text } } && text == "use strict"u8)
            return ValueTask.FromResult<SyntaxNode?>(node);
        var updated = Context.Clone(file);
        var directive = Context.Factory.NewExpressionStatement(Context.Factory.NewStringLiteral("use strict"u8, TokenFlags.None));
        updated.Statements = new([directive, .. file.Statements ?? new([])], file.Statements?.Pos ?? -1, file.Statements?.End ?? -1);
        return ValueTask.FromResult<SyntaxNode?>(updated);
    }
}

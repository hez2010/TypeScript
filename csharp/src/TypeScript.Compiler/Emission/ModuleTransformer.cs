using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;

namespace TypeScript.Compiler.Emission;

internal sealed class ModuleTransformer(EmitContext context, CompilerOptions options, Checker checker,
    Func<SourceFileNode, ModuleKind> moduleFormat, CancellationToken cancellation = default) : SyntaxRewriter(context, cancellation)
{
    private EsModuleTransformer? esm;
    private CommonJsModuleTransformer? commonJs;

    protected override ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        if (node is not SourceFileNode file || file.IsDeclarationFile) return ValueTask.FromResult<SyntaxNode?>(node);
        bool useEs = options.EmitModule == ModuleKind.Preserve || options.EmitModule is ModuleKind.ES2015 or ModuleKind.ES2020 or ModuleKind.ES2022
            or ModuleKind.ESNext or ModuleKind.Node16 or ModuleKind.Node18 or ModuleKind.Node20 or ModuleKind.NodeNext or ModuleKind.CommonJS
            && moduleFormat(file) >= ModuleKind.ES2015;
        return useEs ? (esm ??= new(Context, options, moduleFormat, Cancellation)).VisitAsync(file)
            : (commonJs ??= new(Context, options, checker, moduleFormat, Cancellation)).VisitAsync(file);
    }
}

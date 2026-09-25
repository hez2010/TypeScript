using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask<string> DisplayModuleSpecifierAsync(
        Symbol symbol,
        SymbolDisplayContext state,
        CancellationToken cancellation,
        ReferenceResolutionMode overrideMode = 0)
    {
        var key = (symbol, overrideMode);
        if (state.Modules.TryGetValue(key, out string? cached))
            return cached;
        var file = symbol.Declarations.OfType<SourceFileNode>().FirstOrDefault();
        if (file is null)
            foreach (var declaration in symbol.Declarations)
                if (await ExportEqualsContainerAsync(declaration, symbol, cancellation) is { } equivalent)
                {
                    file = equivalent.Declarations.OfType<SourceFileNode>().FirstOrDefault();
                    if (file is not null)
                        break;
                }
        if (file is null)
        {
            if (symbol.Declarations.OfType<ModuleDeclarationNode>().FirstOrDefault(d => d.Name is StringLiteralNode) is { Name: StringLiteralNode name })
                return state.Modules[key] = name.Text;
            if (Quoted(symbol.Name))
                return state.Modules[key] = symbol.Name[1..^1];
            foreach (var declaration in symbol.Declarations.OfType<ModuleDeclarationNode>())
                if (DeclarationOrder.Ancestor(
                    declaration,
                    n => n is ModuleDeclarationNode { Name: StringLiteralNode }) is ModuleDeclarationNode
                    { Name: StringLiteralNode ambient, Parent: SourceFileNode } container
                    && program.Symbols.Declaration(container)?.Exports.GetValueOrDefault("export=") is { } exported
                    && await SameSymbolReferenceAsync(exported, symbol, cancellation))
                    return state.Modules[key] = ambient.Text;
        }
        var enclosingFile = state.Enclosing is null ? null : SemanticSyntax.Source(state.Enclosing);
        if (enclosingFile is null)
            return state.Modules[key] = Quoted(symbol.Name) ? symbol.Name[1..^1]
                : file?.FileName ?? throw new InvalidOperationException("Module has no source file");
        if (file is null)
            throw new InvalidOperationException("Module has no source file or ambient name");
        var original = DisplayOriginalSpecifier(state.Enclosing!);
        var mode = overrideMode != 0 ? overrideMode : program.Symbols.Program.ResolutionModeForUsage(enclosingFile, original);
        var result = program.Symbols.Program.GetModuleSpecifiers(enclosingFile, file.FileName,
            new(Relative: "project-relative", Ending: mode == ReferenceResolutionMode.Import ? "js" : ""),
            mode: overrideMode,
            cancellation: cancellation);
        return state.Modules[key] = result.Specifiers.Count != 0 ? result.Specifiers[0]
            : throw new InvalidOperationException("No module specifier can name this source file");
    }

    private static SyntaxNode? DisplayOriginalSpecifier(SyntaxNode declaration)
    {
        SyntaxNode? result = declaration switch
        {
            VariableDeclarationNode variable => ImportInitializer(variable.Initializer),
            BindingElementNode binding => ImportInitializer(binding.Initializer),
            ImportDeclarationNode import => import.ModuleSpecifier,
            ExportDeclarationNode export => export.ModuleSpecifier,
            ImportEqualsDeclarationNode { ModuleReference: ExternalModuleReferenceNode module } => module.Expression,
            ImportClauseNode { Parent: ImportDeclarationNode import } => import.ModuleSpecifier,
            ImportClauseNode { Parent: JSDocImportTagNode import } => import.ModuleSpecifier,
            NamespaceExportNode { Parent: ExportDeclarationNode export } => export.ModuleSpecifier,
            NamespaceImportNode { Parent.Parent: ImportDeclarationNode import } => import.ModuleSpecifier,
            NamespaceImportNode { Parent.Parent: JSDocImportTagNode import } => import.ModuleSpecifier,
            ExportSpecifierNode { Parent.Parent: ExportDeclarationNode export } => export.ModuleSpecifier,
            ImportSpecifierNode { Parent.Parent.Parent: ImportDeclarationNode import } => import.ModuleSpecifier,
            ImportSpecifierNode { Parent.Parent.Parent: JSDocImportTagNode import } => import.ModuleSpecifier,
            ImportTypeNode { Argument: LiteralTypeNode type } => type.Literal,
            _ => null
        };
        return result is StringLiteralNode ? result : null;
        static SyntaxNode? ImportInitializer(SyntaxNode? initializer) => DeclarationOrder.Ancestor(initializer,
            n => n is CallExpressionNode call && (SemanticSyntax.RequireCall(call) || IsImportCall(call))) is CallExpressionNode
        { Arguments.Count: > 0 } moduleCall ? moduleCall.Arguments[0] : null;
    }
}

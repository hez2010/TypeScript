using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask CheckMergedExportsAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var symbol = program.Symbols.Binding(node)?.Get(node)?.LocalSymbol;
        if (symbol is null)
        {
            symbol = program.Symbols.Declaration(node);
            if (symbol?.ExportSymbol is null)
                return;
        }
        if (symbol.Declarations.FirstOrDefault(d => d.Kind == node.Kind) != node)
            return;
        DeclarationSpaces exported = 0, local = 0, defaultExport = 0;
        var spaces = new List<(SyntaxNode Node, DeclarationSpaces Spaces)>();
        foreach (var declaration in symbol.Declarations)
        {
            var space = await DeclarationSpacesAsync(declaration, cancellation).ConfigureAwait(false);
            spaces.Add((declaration, space));
            if (!Effective(declaration, SyntaxKind.ExportKeyword))
                local |= space;
            else if (Effective(declaration, SyntaxKind.DefaultKeyword))
                defaultExport |= space;
            else
                exported |= space;
        }
        var exportConflict = exported & local;
        var defaultConflict = defaultExport & (exported | local);
        foreach (var (declaration, space) in spaces)
            if ((space & defaultConflict) != 0)
                Error(SemanticSyntax.Name(declaration) ?? declaration, 2652, TypeDisplay.SymbolName(symbol));
            else if ((space & exportConflict) != 0)
                Error(SemanticSyntax.Name(declaration) ?? declaration, 2395, TypeDisplay.SymbolName(symbol));
    }

    private async ValueTask<DeclarationSpaces> DeclarationSpacesAsync(SyntaxNode node, CancellationToken cancellation)
    {
        DeclarationSpaces result = 0;
        var pending = new Stack<SyntaxNode>();
        var seen = new HashSet<SyntaxNode>();
        pending.Push(node);
        while (pending.TryPop(out var declaration))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!seen.Add(declaration))
                continue;
            switch (declaration)
            {
                case InterfaceDeclarationNode or TypeAliasDeclarationNode or MethodSignatureDeclarationNode
                    or PropertySignatureDeclarationNode:
                    result |= DeclarationSpaces.ExportType;
                    break;
                case ModuleDeclarationNode module:
                    result |= DeclarationSpaces.ExportNamespace;
                    if (AmbientModule(module) || Binder.ModuleState(module) != 0)
                        result |= DeclarationSpaces.ExportValue;
                    break;
                case ClassDeclarationNode or EnumDeclarationNode or EnumMemberNode:
                    result |= DeclarationSpaces.ExportType | DeclarationSpaces.ExportValue;
                    break;
                case SourceFileNode:
                    result |= DeclarationSpaces.ExportType | DeclarationSpaces.ExportValue | DeclarationSpaces.ExportNamespace;
                    break;
                case ImportEqualsDeclarationNode or NamespaceImportNode or ImportClauseNode:
                case ExportAssignmentNode or BinaryExpressionNode when (program.Symbols.Declaration(declaration)!.Flags & SymbolFlags.Alias) != 0:
                    var target = await program.Aliases.ResolveAsync(
                        program.Symbols.Declaration(declaration)!,
                        cancellation).ConfigureAwait(false);
                    foreach (var targetDeclaration in target.Declarations)
                        pending.Push(targetDeclaration);
                    break;
                case VariableDeclarationNode or BindingElementNode or FunctionDeclarationNode or ImportSpecifierNode
                    or ExportAssignmentNode or BinaryExpressionNode:
                    result |= DeclarationSpaces.ExportValue;
                    break;
                default:
                    throw new InvalidOperationException($"Checker requires declaration-space checking for {declaration.Kind}");
            }
        }
        return result;
    }
}

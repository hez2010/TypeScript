using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
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
        if (symbol.Declarations.Length == 1 && DirectDeclarationSpaces(node) is not null)
            return;
        bool firstOfKind = false;
        foreach (var declaration in symbol.Declarations)
            if (declaration.Kind == node.Kind)
            {
                firstOfKind = declaration == node;
                break;
            }
        if (!firstOfKind)
            return;
        DeclarationSpaces exported = 0, local = 0, defaultExport = 0;
        foreach (var declaration in symbol.Declarations)
        {
            var space = await DeclarationSpacesAsync(declaration, cancellation).ConfigureAwait(false);
            if (!Effective(declaration, SyntaxKind.ExportKeyword))
                local |= space;
            else if (Effective(declaration, SyntaxKind.DefaultKeyword))
                defaultExport |= space;
            else
                exported |= space;
        }
        var exportConflict = exported & local;
        var defaultConflict = defaultExport & (exported | local);
        if ((exportConflict | defaultConflict) == 0)
            return;
        foreach (var declaration in symbol.Declarations)
        {
            var space = await DeclarationSpacesAsync(declaration, cancellation).ConfigureAwait(false);
            if ((space & defaultConflict) != 0)
                Error(
                    SemanticSyntax.Name(declaration) ?? declaration,
                    DiagnosticCode.MergedDeclaration0CannotIncludeADefaultExportDeclarationConsiderAddingASeparateExportDefault0DeclarationInstead,
                    TypeDisplay.SymbolName(symbol));
            else if ((space & exportConflict) != 0)
                Error(
                    SemanticSyntax.Name(declaration) ?? declaration,
                    DiagnosticCode.IndividualDeclarationsInMergedDeclaration0MustBeAllExportedOrAllLocal,
                    TypeDisplay.SymbolName(symbol));
        }
    }

    private DeclarationSpaces? DirectDeclarationSpaces(SyntaxNode node) => node switch
    {
        InterfaceDeclarationNode or TypeAliasDeclarationNode or MethodSignatureDeclarationNode or PropertySignatureDeclarationNode
            => DeclarationSpaces.ExportType,
        ModuleDeclarationNode module => DeclarationSpaces.ExportNamespace
            | (AmbientModule(module) || Binder.ModuleState(module) != 0 ? DeclarationSpaces.ExportValue : 0),
        ClassDeclarationNode or EnumDeclarationNode or EnumMemberNode
            => DeclarationSpaces.ExportType | DeclarationSpaces.ExportValue,
        SourceFileNode => DeclarationSpaces.ExportType | DeclarationSpaces.ExportValue | DeclarationSpaces.ExportNamespace,
        VariableDeclarationNode or BindingElementNode or FunctionDeclarationNode or ImportSpecifierNode => DeclarationSpaces.ExportValue,
        ExportAssignmentNode or BinaryExpressionNode when (program.Symbols.Declaration(node)!.Flags & SymbolFlags.Alias) == 0
            => DeclarationSpaces.ExportValue,
        _ => null
    };

    private async ValueTask<DeclarationSpaces> DeclarationSpacesAsync(SyntaxNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (DirectDeclarationSpaces(node) is { } direct)
            return direct;
        DeclarationSpaces result = 0;
        var pending = new Stack<SyntaxNode>();
        var seen = new HashSet<SyntaxNode>();
        pending.Push(node);
        while (pending.TryPop(out var declaration))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!seen.Add(declaration))
                continue;
            if (DirectDeclarationSpaces(declaration) is { } space)
            {
                result |= space;
                continue;
            }
            switch (declaration)
            {
                case ImportEqualsDeclarationNode or NamespaceImportNode or ImportClauseNode:
                case ExportAssignmentNode or BinaryExpressionNode when (program.Symbols.Declaration(declaration)!.Flags & SymbolFlags.Alias) != 0:
                    var target = await program.Aliases.ResolveAsync(
                        program.Symbols.Declaration(declaration)!,
                        cancellation).ConfigureAwait(false);
                    foreach (var targetDeclaration in target.Declarations)
                        pending.Push(targetDeclaration);
                    break;
                default:
                    throw new InvalidOperationException($"Checker requires declaration-space checking for {declaration.Kind}");
            }
        }
        return result;
    }
}

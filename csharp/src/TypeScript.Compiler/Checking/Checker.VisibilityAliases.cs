using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask PrecalculateDeclarationEmitVisibilityAsync(SourceFileNode file, CancellationToken cancellation = default)
    {
        await VisibilityQueryAsync(file, async () =>
        {
            if (!visibilityFiles.Add(file))
                return false;
            visibilityChanges!.Files.Add(file);
            var pending = new Stack<SyntaxNode>();
            for (int i = file.ChildCount - 1; i >= 0; i--)
                pending.Push(file.GetChild(i));
            while (pending.TryPop(out var node))
            {
                cancellation.ThrowIfCancellationRequested();
                BeforeVisibilityNode?.Invoke(node);
                switch (node)
                {
                    case BinaryExpressionNode { Right: IdentifierNode identifier } when CommonJsVisibilityExport(node):
                        await MarkVisibilityAliasesAsync(identifier, cancellation);
                        break;
                    case ExportAssignmentNode { Expression: IdentifierNode identifier }:
                        await MarkVisibilityAliasesAsync(identifier, cancellation);
                        break;
                    case ExportSpecifierNode export:
                        await MarkVisibilityAliasesAsync(export.PropertyName ?? export.Name!, cancellation);
                        break;
                }
                for (int i = node.ChildCount - 1; i >= 0; i--)
                    pending.Push(node.GetChild(i));
            }
            return true;
        }, cancellation);
    }

    private async ValueTask MarkVisibilityAliasesAsync(SyntaxNode node, CancellationToken cancellation)
    {
        Symbol? symbol = null;
        const SymbolFlags meaning = SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Alias;
        if (node is IdentifierNode identifier && (node.Parent is ExportAssignmentNode || CommonJsVisibilityExport(node.Parent)))
            symbol = program.Symbols.NameResolver(cancellation).Resolve(node, identifier.Text, meaning);
        else if (node.Parent is ExportSpecifierNode export)
            symbol = await program.AliasTargets.ExportSpecifierAsync(export, meaning, false, cancellation);
        var visited = new HashSet<Symbol>();
        while (symbol is not null && visited.Add(symbol))
        {
            cancellation.ThrowIfCancellationRequested();
            Symbol? next = null;
            foreach (var declaration in symbol.Declarations)
            {
                SetDeclarationVisible(declaration, true);
                if (declaration is ImportEqualsDeclarationNode { ModuleReference: not ExternalModuleReferenceNode } import)
                {
                    var first = import.ModuleReference!;
                    while (first is QualifiedNameNode qualified)
                        first = qualified.Left!;
                    next = program.Symbols.NameResolver(cancellation).Resolve(import, ((IdentifierNode)first).Text, meaning);
                }
            }
            symbol = next;
        }
    }

    private bool CommonJsVisibilityExport(SyntaxNode? node) => node is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.EqualsToken } binary
        && binary.Parent is ExpressionStatementNode { Parent: SourceFileNode file }
        && program.Symbols.Binding(file)?.CommonJSModuleIndicator is not null
        && (ModuleExportsAccess(binary.Left) || ExportsPropertyAssignment(binary.Left!));
}

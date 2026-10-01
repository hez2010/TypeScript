using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Emission;

internal sealed class ImportElision : SyntaxRewriter
{
    private readonly Checker checker;
    private SourceFileNode? sourceFile;

    internal ImportElision(EmitContext context, CompilerOptions options, Checker checker, CancellationToken cancellation = default)
        : base(context, cancellation)
    {
        if (options.VerbatimModuleSyntax == true)
            throw new ArgumentException("Import elision requires verbatimModuleSyntax to be disabled", nameof(options));
        this.checker = checker;
    }

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        switch (node)
        {
            case SourceFileNode file:
                var previous = sourceFile;
                sourceFile = file;
                try
                {
                    await checker.MarkLinkedReferencesForEmitAsync((SourceFileNode)Context.MostOriginal(file), Cancellation);
                    return await VisitEachChildAsync(file);
                }
                finally { sourceFile = previous; }
            case ImportEqualsDeclarationNode import:
                bool referenced = await ShouldEmitAliasAsync(import);
                if (!referenced && import.ModuleReference is not ExternalModuleReferenceNode && sourceFile?.ExternalModuleIndicator is null
                    && Context.ParseNode(import) is { } original)
                    referenced = await checker.IsTopLevelValueImportEqualsAsync(original, Cancellation);
                return referenced ? await VisitEachChildAsync(import) : null;
            case ImportDeclarationNode import:
                if (import.ImportClause is null)
                    return await VisitEachChildAsync(import);
                var importClause = (ImportClauseNode?)await VisitAsync(import.ImportClause);
                if (importClause is null)
                    return null;
                var importResult = Context.Clone(import);
                importResult.ImportClause = importClause;
                importResult.Attributes = (ImportAttributesNode?)await VisitAsync(import.Attributes);
                return importResult;
            case ImportClauseNode clause:
                var name = await ShouldEmitAliasAsync(clause) ? clause.Name : null;
                var bindings = await VisitAsync(clause.NamedBindings);
                if (name is null && bindings is null)
                    return null;
                var clauseResult = Context.Clone(clause);
                clauseResult.Name = name;
                clauseResult.NamedBindings = bindings;
                return clauseResult;
            case NamespaceImportNode or ImportSpecifierNode:
                return await ShouldEmitAliasAsync(node) ? node : null;
            case NamedImportsNode imports:
                var importElements = await VisitListAsync(imports.Elements);
                if (importElements is not { Count: > 0 })
                    return null;
                var importsResult = Context.Clone(imports);
                importsResult.Elements = importElements;
                return importsResult;
            case ExportAssignmentNode:
                return await IsValueAliasAsync(node) ? await VisitEachChildAsync(node) : null;
            case ExportDeclarationNode export:
                var exportClause = await VisitAsync(export.ExportClause);
                if (export.ExportClause is not null && exportClause is null)
                    return null;
                var exportResult = Context.Clone(export);
                exportResult.Modifiers = null;
                exportResult.IsTypeOnly = false;
                exportResult.ExportClause = exportClause;
                exportResult.ModuleSpecifier = await VisitAsync(export.ModuleSpecifier);
                exportResult.Attributes = (ImportAttributesNode?)await VisitAsync(export.Attributes);
                return exportResult;
            case NamedExportsNode exports:
                var exportElements = await VisitListAsync(exports.Elements);
                if (exportElements is not { Count: > 0 })
                    return null;
                var exportsResult = Context.Clone(exports);
                exportsResult.Elements = exportElements;
                return exportsResult;
            case ExportSpecifierNode:
                return await IsValueAliasAsync(node) ? node : null;
            case ModuleDeclarationNode or ModuleBlockNode:
                return await VisitEachChildAsync(node);
            default:
                return node;
        }
    }

    private async ValueTask<bool> ShouldEmitAliasAsync(SyntaxNode node) => (node.Flags & NodeFlags.JavaScriptFile) != 0
        || Context.ParseNode(node) is not { } original || await checker.IsReferencedAliasForEmitAsync(original, Cancellation);

    private async ValueTask<bool> IsValueAliasAsync(SyntaxNode node) => Context.ParseNode(node) is not { } original
        || await checker.IsValueAliasForEmitAsync(original, Cancellation);
}

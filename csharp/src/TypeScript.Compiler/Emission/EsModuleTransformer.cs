using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed class EsModuleTransformer(EmitContext context, CompilerOptions options, Func<SourceFileNode, ModuleKind> moduleFormat,
    CancellationToken cancellation = default) : SyntaxRewriter(context, cancellation)
{
    private List<SyntaxNode>? requireStatements;
    private IdentifierNode? requireName;
    private NodeFactory F => Context.Factory;

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        switch (node)
        {
            case SourceFileNode file:
                if (file.IsDeclarationFile || !(file.ExternalModuleIndicator is not null || options.IsolatedModules == true || options.VerbatimModuleSyntax == true)) return file;
                requireStatements = null;
                requireName = null;
                try
                {
                    var visited = (SourceFileNode)(await VisitEachChildAsync(file))!;
                    foreach (var helper in Context.ReadHelpers()) Context.AddHelper(visited, helper);
                    var helpers = ModuleUtilities.HelpersImport(Context, visited, options, moduleFormat(file));
                    if (helpers is not null || requireStatements is not null)
                    {
                        List<SyntaxNode> statements = [.. visited.Statements ?? new([])];
                        int index = 0;
                        while (index < statements.Count && (statements[index] is ExpressionStatementNode { Expression: StringLiteralNode }
                            || (Context.GetFlags(statements[index]) & EmitFlags.CustomPrologue) != 0)) index++;
                        if (helpers is not null) statements.Insert(index++, (await VisitAsync(helpers))!);
                        if (requireStatements is not null) statements.InsertRange(index, requireStatements);
                        visited = Update(visited, statements);
                    }
                    if (visited.ExternalModuleIndicator is not null && options.EmitModule != ModuleKind.Preserve
                        && !(visited.Statements?.Any(ModuleUtilities.IsIndicator) ?? false))
                        visited = Update(visited, [.. visited.Statements ?? new([]), ModuleUtilities.EmptyExport(F)]);
                    return visited;
                }
                finally { requireStatements = null; requireName = null; }
            case ImportDeclarationNode import:
                if (options.RewriteRelativeImportExtensions != true) return import;
                var clause = await VisitAsync(import.ImportClause);
                var specifier = ModuleUtilities.RewriteSpecifier(Context, import.ModuleSpecifier, options);
                var attributes = await VisitAsync(import.Attributes);
                if (clause == import.ImportClause && specifier == import.ModuleSpecifier && attributes == import.Attributes && import.Modifiers is null) return import;
                var updatedImport = Context.Clone(import);
                updatedImport.Modifiers = null;
                updatedImport.ImportClause = (ImportClauseNode?)clause;
                updatedImport.ModuleSpecifier = specifier;
                updatedImport.Attributes = (ImportAttributesNode?)attributes;
                return updatedImport;
            case ImportEqualsDeclarationNode import:
                if (options.EmitModule < ModuleKind.Node16) return null;
                if (import.ModuleReference is not ExternalModuleReferenceNode) throw new InvalidOperationException("Internal import aliases must be transformed before module emission");
                var declaration = F.NewVariableStatement(null, F.NewVariableDeclarationList(new([F.NewVariableDeclaration(Context.Clone(import.Name!), null, null, Require(import))]), NodeFlags.Const));
                Context.SetOriginal(declaration, import);
                Context.SetCommentRange(declaration, new(import.Pos, import.End));
                Context.SetSourceMapRange(declaration, new(import.Pos, import.End));
                return import.Modifiers?.Any(modifier => modifier.Kind == K.ExportKeyword) == true
                    ? F.NewSyntaxList([declaration, F.NewExportDeclaration(null, false, F.NewNamedExports(new([F.NewExportSpecifier(false, null, Context.Clone(import.Name!))])), null, null)]) : declaration;
            case ExportAssignmentNode assignment:
                if (!assignment.IsExportEquals) return await VisitEachChildAsync(assignment);
                if (options.EmitModule != ModuleKind.Preserve) return null;
                var statement = F.NewExpressionStatement(Context.Binary(Property(F.NewIdentifier("module"u8), "exports"u8), K.EqualsToken, (await VisitAsync(assignment.Expression))!));
                Context.SetOriginal(statement, assignment);
                return statement;
            case ExportDeclarationNode export:
                if (export.ModuleSpecifier is null) return export;
                var module = ModuleUtilities.RewriteSpecifier(Context, export.ModuleSpecifier, options);
                var exportAttributes = (ImportAttributesNode?)await VisitAsync(export.Attributes);
                if (options.Module > ModuleKind.ES2015 || export.ExportClause is not NamespaceExportNode space)
                {
                    if (module == export.ModuleSpecifier && exportAttributes == export.Attributes && !export.IsTypeOnly && export.Modifiers is null) return export;
                    var updated = Context.Clone(export);
                    updated.Modifiers = null; updated.IsTypeOnly = false; updated.ModuleSpecifier = module; updated.Attributes = exportAttributes;
                    return updated;
                }
                var name = Context.NewGeneratedNameForNode(space.Name!);
                var namespaceImport = F.NewImportDeclaration(K.ImportDeclaration, null, F.NewImportClause(K.Unknown, null, F.NewNamespaceImport(name)), module, exportAttributes);
                Context.SetOriginal(namespaceImport, space);
                SyntaxNode namespaceExport = SyntaxNameText.Get(space.Name!) == "default"u8
                    ? F.NewExportAssignment(null, false, null, name)
                    : F.NewExportDeclaration(null, false, F.NewNamedExports(new([F.NewExportSpecifier(false, name, space.Name)])), null, null);
                Context.SetOriginal(namespaceExport, export);
                return F.NewSyntaxList([namespaceImport, namespaceExport]);
            case CallExpressionNode call when options.RewriteRelativeImportExtensions == true && call.Arguments is { Count: > 0 }
                && (call.Expression?.Kind == K.ImportKeyword || (call.Flags & NodeFlags.JavaScriptFile) != 0 && call.Expression is IdentifierNode { Text: var requireText } && requireText == "require"u8 && call.Arguments.Count == 1):
                var expression = await VisitAsync(call.Expression);
                List<SyntaxNode> arguments = [ModuleUtilities.RewriteDynamicSpecifier(Context, call.Arguments[0], options)];
                for (int i = 1; i < call.Arguments.Count; i++) arguments.Add((await VisitAsync(call.Arguments[i]))!);
                var updatedCall = Context.Clone(call);
                updatedCall.Expression = expression;
                updatedCall.TypeArguments = null;
                updatedCall.Arguments = new(arguments.ToArray(), call.Arguments.Pos, call.Arguments.End);
                return updatedCall;
            default: return await VisitEachChildAsync(node);
        }
    }

    private SyntaxNode Require(ImportEqualsDeclarationNode node)
    {
        var name = ModuleUtilities.ExternalModuleName(F, node);
        var arguments = name is null ? Array.Empty<SyntaxNode>() : [ModuleUtilities.RewriteSpecifier(Context, name, options)!];
        if (options.EmitModule == ModuleKind.Preserve) return Call(F.NewIdentifier("require"u8), arguments);
        if (requireStatements is null)
        {
            var createRequire = Context.NewUniqueName("_createRequire"u8, new(GeneratedIdentifierFlags.Optimistic | GeneratedIdentifierFlags.FileLevel));
            var import = F.NewImportDeclaration(K.ImportDeclaration, null, F.NewImportClause(K.Unknown, null,
                F.NewNamedImports(new([F.NewImportSpecifier(false, F.NewIdentifier("createRequire"u8), createRequire)]))), F.NewStringLiteral("module"u8, TokenFlags.None), null);
            requireName = Context.NewUniqueName("__require"u8, new(GeneratedIdentifierFlags.Optimistic | GeneratedIdentifierFlags.FileLevel));
            var declaration = F.NewVariableStatement(null, F.NewVariableDeclarationList(new([F.NewVariableDeclaration(requireName, null, null,
                Call(Context.Clone(createRequire), [Property(F.NewMetaProperty(K.ImportKeyword, F.NewIdentifier("meta"u8)), "url"u8)]))]), NodeFlags.Const));
            Context.AddFlags(import, EmitFlags.CustomPrologue);
            Context.AddFlags(declaration, EmitFlags.CustomPrologue);
            requireStatements = [import, declaration];
        }
        return Call(Context.Clone(requireName!), arguments);
    }

    private SourceFileNode Update(SourceFileNode file, List<SyntaxNode> statements)
    {
        var result = Context.Clone(file);
        result.Statements = new(statements.ToArray(), file.Statements?.Pos ?? -1, file.Statements?.End ?? -1);
        return result;
    }
    private CallExpressionNode Call(SyntaxNode target, SyntaxNode[] arguments) => F.NewCallExpression(target, null, null, new(arguments), NodeFlags.None);
    private PropertyAccessExpressionNode Property(SyntaxNode target, Utf8String name) => F.NewPropertyAccessExpression(target, null, F.NewIdentifier(name), NodeFlags.None);
}

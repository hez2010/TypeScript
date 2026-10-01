using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal static class ModuleUtilities
{
    internal static SyntaxNode? RewriteSpecifier(EmitContext context, SyntaxNode? node, CompilerOptions options)
    {
        if (options.RewriteRelativeImportExtensions != true || node is not StringLiteralNode literal) return node;
        var text = literal.Text;
        if (!(text.StartsWith("./"u8) || text.StartsWith("../"u8))
            || text.EndsWith(".d.ts"u8) || text.EndsWith(".d.mts"u8) || text.EndsWith(".d.cts"u8)) return node;
        int length;
        Utf8String extension;
        if (text.EndsWith(".tsx"u8)) { length = 4; extension = options.Jsx == JsxEmit.Preserve ? ".jsx"u8 : ".js"u8; }
        else if (text.EndsWith(".mts"u8)) { length = 4; extension = ".mjs"u8; }
        else if (text.EndsWith(".cts"u8)) { length = 4; extension = ".cjs"u8; }
        else if (text.EndsWith(".ts"u8)) { length = 3; extension = ".js"u8; }
        else return node;
        var result = context.Factory.NewStringLiteral(text[..^length] + extension, literal.TokenFlags);
        context.SetOriginal(result, node);
        context.SetCommentRange(result, new(node.Pos, node.End));
        context.SetSourceMapRange(result, new(node.Pos, node.End));
        return result;
    }

    internal static SyntaxNode RewriteDynamicSpecifier(EmitContext context, SyntaxNode argument, CompilerOptions options)
        => argument is StringLiteralNode or NoSubstitutionTemplateLiteralNode ? RewriteSpecifier(context, argument, options)!
            : context.HelperCall(EmitHelpers.RewriteRelativeImportExtensions, "__rewriteRelativeImportExtension"u8,
                options.Jsx == JsxEmit.Preserve ? [argument, context.Factory.NewKeywordExpression(K.TrueKeyword)] : [argument]);

    internal static SyntaxNode? ExternalModuleName(NodeFactory factory, SyntaxNode node)
    {
        var name = node switch
        {
            ImportDeclarationNode import => import.ModuleSpecifier,
            ExportDeclarationNode export => export.ModuleSpecifier,
            ImportEqualsDeclarationNode { ModuleReference: ExternalModuleReferenceNode reference } => reference.Expression,
            CallExpressionNode { Arguments.Count: > 0 } call => call.Arguments[0],
            _ => null
        };
        return name is StringLiteralNode literal ? factory.NewStringLiteral(literal.Text, TokenFlags.None) : null;
    }

    internal static SyntaxNode? HelpersImport(EmitContext context, SourceFileNode source, CompilerOptions options, ModuleKind format,
        bool exportStars = false, bool importStar = false, bool importDefault = false)
    {
        if (options.ImportHelpers != true || !(source.ExternalModuleIndicator is not null || options.IsolatedModules == true || options.VerbatimModuleSyntax == true)) return null;
        var helpers = context.GetHelpers(source).Where(helper => !helper.Scoped).ToArray();
        var f = context.Factory;
        SyntaxNode? result;
        if (format == ModuleKind.CommonJS || format == ModuleKind.None && options.EmitModule == ModuleKind.CommonJS)
        {
            var name = context.GetExternalHelpersModuleName(source);
            if (name is null && (helpers.Length > 0 || (exportStars || importStar || importDefault) && format < ModuleKind.System))
            {
                name = context.NewUniqueName("tslib"u8);
                context.SetExternalHelpersModuleName(source, name);
            }
            if (name is null) return null;
            result = f.NewImportEqualsDeclaration(null, false, name, f.NewExternalModuleReference(f.NewStringLiteral("tslib"u8, TokenFlags.None)));
        }
        else
        {
            var names = helpers.Select(helper => helper.ImportName).Where(name => name.Length > 0).Distinct().Order(Utf8StringComparer.Ordinal).ToArray();
            if (names.Length == 0) return null;
            var identifiers = context.MostOriginal(source).DescendantsAndSelf().OfType<IdentifierNode>().Select(node => node.Text).ToHashSet();
            var specifiers = names.Select(name =>
            {
                var identifier = f.NewIdentifier(name);
                bool conflict = identifiers.Contains(name);
                if (conflict) context.AddFlags(identifier, EmitFlags.HelperName);
                return (SyntaxNode)f.NewImportSpecifier(false, conflict ? f.NewIdentifier(name) : null, identifier);
            }).ToArray();
            context.AddFlags(context.MostOriginal(source), EmitFlags.ExternalHelpers);
            result = f.NewImportDeclaration(K.ImportDeclaration, null, f.NewImportClause(K.Unknown, null, f.NewNamedImports(new(specifiers))),
                f.NewStringLiteral("tslib"u8, TokenFlags.None), null);
        }
        context.AddFlags(result, EmitFlags.CustomPrologue);
        return result;
    }

    internal static bool IsIndicator(SyntaxNode node) => node is ImportDeclarationNode or ExportDeclarationNode or ExportAssignmentNode
        || node is ImportEqualsDeclarationNode { ModuleReference: ExternalModuleReferenceNode } || SemanticSyntax.HasModifier(node, K.ExportKeyword);
    internal static ExportDeclarationNode EmptyExport(NodeFactory f) => f.NewExportDeclaration(null, false, f.NewNamedExports(new([])), null, null);
}

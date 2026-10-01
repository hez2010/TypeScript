using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

internal sealed partial class CommonJsModuleTransformer
{
    private bool ReservedGenerated(IdentifierNode name) => Context.GetAutoGenerateInfo(name) is { Flags: var flags }
        && (flags & (GeneratedIdentifierFlags.FileLevel | GeneratedIdentifierFlags.Optimistic | GeneratedIdentifierFlags.ReservedInNestedScopes))
            == (GeneratedIdentifierFlags.FileLevel | GeneratedIdentifierFlags.Optimistic | GeneratedIdentifierFlags.ReservedInNestedScopes);
    private bool CanAssign(IdentifierNode name) => (Context.GetAutoGenerateInfo(name) is null || ReservedGenerated(name))
        && (Context.GetFlags(name) & EmitFlags.LocalName) == 0;

    private async ValueTask<SyntaxNode> IdentifierAsync(IdentifierNode node)
    {
        if (Context.GetAutoGenerateInfo(node) is { Flags: var flags } && (flags & GeneratedIdentifierFlags.AllowNameSubstitution) == 0
            || (Context.GetFlags(node) & (EmitFlags.HelperName | EmitFlags.LocalName)) != 0) return node;
        var original = (IdentifierNode)Context.MostOriginal(node);
        if (original.Parent is EnumDeclarationNode or ModuleDeclarationNode && original.Parent.DeclarationName == original) return node;
        var container = await checker.GetReferencedExportContainerForEmitAsync(original, (Context.GetFlags(node) & EmitFlags.ExportName) != 0, Cancellation);
        SyntaxNode? reference = null;
        if (container is SourceFileNode)
            reference = F.NewPropertyAccessExpression(F.NewIdentifier("exports"u8), null, Context.Clone(node), NodeFlags.None);
        else
        {
            var import = await checker.GetReferencedImportForEmitAsync(original, Cancellation);
            if (import is ImportClauseNode)
                reference = Property(Context.NewGeneratedNameForNode(import.Parent!), "default"u8);
            else if (import is ImportSpecifierNode specifier)
            {
                var key = specifier.PropertyName ?? specifier.Name!;
                var declaration = specifier.Parent;
                while (declaration is not null and not ImportDeclarationNode) declaration = declaration.Parent;
                var target = Context.NewGeneratedNameForNode(declaration ?? specifier);
                if (key is StringLiteralNode) reference = F.NewElementAccessExpression(target, null, Context.StringLiteralFromNode(key), NodeFlags.None);
                else
                {
                    var name = Context.Clone(key);
                    Context.AddFlags(name, EmitFlags.NoSourceMap | EmitFlags.NoComments);
                    reference = F.NewPropertyAccessExpression(target, null, name, NodeFlags.None);
                }
            }
        }
        if (reference is null) return node;
        Context.AssignCommentAndSourceMapRanges(reference, node);
        return EmitContext.CopyRange(reference, node);
    }

    private async ValueTask<IReadOnlyList<SyntaxNode>> ExportsAsync(IdentifierNode name)
    {
        if (Context.GetAutoGenerateInfo(name) is null)
        {
            var original = (IdentifierNode)Context.MostOriginal(name);
            var import = await checker.GetReferencedImportForEmitAsync(original, Cancellation);
            if (import is not null) return info.Bindings.GetValueOrDefault(import) ?? [];
            var declarations = await checker.GetReferencedValuesForEmitAsync(original, Cancellation);
            HashSet<SyntaxNode> seen = new(ReferenceEqualityComparer.Instance);
            List<SyntaxNode> bindings = [];
            foreach (var declaration in declarations)
                foreach (var binding in info.Bindings.GetValueOrDefault(declaration) ?? []) if (seen.Add(binding)) bindings.Add(binding);
            return bindings;
        }
        return ReservedGenerated(name) && info.Specifiers.TryGetValue(name.Text, out var specifiers)
            ? specifiers.Select(specifier => specifier.Name!).ToArray() : [];
    }
    private async ValueTask<SyntaxNode> ShorthandAsync(ShorthandPropertyAssignmentNode node)
    {
        var value = await IdentifierAsync((IdentifierNode)node.Name!);
        var initializer = await VisitAsync(node.ObjectAssignmentInitializer);
        if (value != node.Name)
        {
            if (initializer is not null) value = Assign(value, initializer);
            return EmitContext.CopyRange(Located(F.NewPropertyAssignment(null, node.Name, null, null, value), node, false), node);
        }
        var result = Context.Clone(node);
        result.Modifiers = null; result.PostfixToken = null; result.Type = null; result.Name = value; result.ObjectAssignmentInitializer = initializer;
        return result;
    }
}

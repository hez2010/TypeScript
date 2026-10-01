using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

internal sealed class MetadataTransformer(EmitContext context, CompilerOptions options, Checker checker, CancellationToken cancellation = default)
    : SyntaxRewriter(context, cancellation)
{
    private SyntaxNode? container, lexicalScope;
    private NodeList? visitedModifiers;

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        var saved = (container, lexicalScope);
        if (node is SourceFileNode or ModuleBlockNode or BlockNode or CaseBlockNode)
            lexicalScope = node;
        if (node is ClassDeclarationNode or ClassExpressionNode)
            container = node;
        try
        {
            bool legacy = options.ExperimentalDecorators == true;
            bool decorated = node is ClassDeclarationNode or ClassExpressionNode ? DecoratorSyntax.ClassDecorated(legacy, node)
                : container is not null && node is PropertyDeclarationNode or MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode
                    && DecoratorSyntax.ElementDecorated(legacy, node, container);
            SyntaxNode? result;
            if (legacy && decorated)
            {
                var updated = Context.Clone(node);
                var modifiers = await VisitListAsync(node.ModifierList);
                var metadata = await TypeMetadataAsync(node);
                ((IModifiedNode)updated).Modifiers = Inject(modifiers, metadata, node is ClassDeclarationNode or ClassExpressionNode);
                // The modifiers were visited before serializing metadata, matching declaration evaluation order.
                var savedModifiers = visitedModifiers;
                visitedModifiers = updated.ModifierList;
                try { result = await VisitEachChildAsync(updated); }
                finally { visitedModifiers = savedModifiers; }
            }
            else result = await VisitEachChildAsync(node);
            if (node is SourceFileNode && result is not null)
                foreach (var helper in Context.ReadHelpers()) Context.AddHelper(result, helper);
            return result;
        }
        finally { (container, lexicalScope) = saved; }
    }

    protected override ValueTask<NodeList?> VisitListAsync(NodeList? nodes) => nodes is not null && nodes == visitedModifiers
        ? ValueTask.FromResult<NodeList?>(nodes) : base.VisitListAsync(nodes);

    private async ValueTask<SyntaxNode[]> TypeMetadataAsync(SyntaxNode node)
    {
        var serializer = new MetadataSerializer(Context, checker, options, lexicalScope!, container, Cancellation);
        List<SyntaxNode> metadata = [];
        if (node is MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode or PropertyDeclarationNode)
            metadata.Add(Metadata("design:type"u8, await serializer.TypeOfNodeAsync(node, container)));
        if (node is MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode
            || node is ClassDeclarationNode or ClassExpressionNode && DecoratorSyntax.Constructor(node) is not null)
            metadata.Add(Metadata("design:paramtypes"u8, await serializer.ParameterTypesAsync(node, container)));
        if (node is MethodDeclarationNode)
            metadata.Add(Metadata("design:returntype"u8, await serializer.ReturnTypeAsync(node)));
        return metadata.ToArray();
    }

    private SyntaxNode Metadata(Utf8String key, SyntaxNode value)
    {
        Context.RequestHelper(EmitHelpers.Metadata);
        var name = Context.Factory.NewIdentifier("__metadata"u8);
        Context.SetFlags(name, EmitFlags.HelperName);
        var call = Context.Factory.NewCallExpression(name, null, null, new([Context.Factory.NewStringLiteral(key, TokenFlags.None), value]), NodeFlags.None);
        return Context.Factory.NewDecorator(call);
    }

    private static NodeList? Inject(NodeList? modifiers, SyntaxNode[] metadata, bool classDeclaration)
    {
        if (metadata.Length == 0) return modifiers;
        List<SyntaxNode> result = [];
        int prefix = 0;
        if (classDeclaration && modifiers is not null)
            while (prefix < Math.Min(2, modifiers.Count) && modifiers[prefix].Kind is SyntaxKind.ExportKeyword or SyntaxKind.DefaultKeyword)
                result.Add(modifiers[prefix++]);
        if (modifiers is not null) result.AddRange(modifiers.OfType<DecoratorNode>());
        result.AddRange(metadata);
        if (modifiers is not null) result.AddRange(modifiers.Skip(prefix).Where(n => n is not DecoratorNode));
        return new(result.ToArray(), modifiers?.Pos ?? -1, modifiers?.End ?? -1);
    }
}

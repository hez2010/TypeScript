using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly Dictionary<SyntaxNode, bool> argumentsReferences = [];

    public async ValueTask CheckFullSignatureAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (node is IFullSignatureNode { FullSignature: { } annotation }
            && await FunctionContexts.CallAsync(
                await Nodes.FromNodeAsync(annotation, cancellation).ConfigureAwait(false),
                node,
                cancellation).ConfigureAwait(false) is null)
            Error(annotation, 8030);
    }

    private async ValueTask<Type?> DocumentationTypeReferenceAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if ((node.Flags & NodeFlags.JSDoc) == 0 || node is not TypeReferenceNode { TypeName: IdentifierNode name } reference)
            return null;
        var arguments = reference.TypeArguments;
        Type? result = name.Text switch
        {
            "String" => context.StringType,
            "Number" => context.NumberType,
            "BigInt" => context.BigIntType,
            "Boolean" => context.BooleanType,
            "Void" => context.VoidType,
            "Undefined" => context.UndefinedType,
            "Null" => context.NullType,
            "Function" or "function" => program.Globals.Types["Function"],
            _ => null
        };
        if (result is not null)
        {
            if (arguments is { Count: > 0 })
                Error(node, 2315);
            return result;
        }
        if (name.Text == "Object")
        {
            if (arguments is { Count: 2 })
            {
                if (await program.Globals.AliasAsync("Record", 2, Declared, cancellation).ConfigureAwait(false) is { } record)
                {
                    var key = await Nodes.FromNodeAsync(arguments[0], cancellation).ConfigureAwait(false);
                    if (await Instantiation.Members.ValidIndexKeyAsync(key, cancellation).ConfigureAwait(false))
                        return await References.AliasInstantiationAsync(
                            record,
                            [key, await Nodes.FromNodeAsync(arguments[1], cancellation).ConfigureAwait(false)],
                            cancellation: cancellation).ConfigureAwait(false);
                }
                return context.AnyType;
            }
            if (!NoImplicitAny)
            {
                if (arguments is { Count: > 0 })
                    Error(node, 2315);
                return context.AnyType;
            }
        }
        else if (!NoImplicitAny && arguments is null or { Count: 0 })
        {
            if (name.Text == "array")
                return program.Globals.AnyArrayType;
            if (name.Text == "promise")
                return await PromiseResultAsync(node, context.AnyType, false, cancellation).ConfigureAwait(false);
        }
        return null;
    }

    private async ValueTask CheckUnmatchedDocumentationParametersAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if ((node.Flags & NodeFlags.JSDoc) != 0)
            return;
        IReadOnlyList<SyntaxNode>? tags = null;
        for (var current = node; current is not null; current = NextDocumentationLocation(current))
        {
            if ((current.Flags & NodeFlags.HasJSDoc) == 0)
                continue;
            var comments = await SemanticSyntax.Source(current)!.GetDocumentationAsync(current, cancellation).ConfigureAwait(false);
            if (comments.Count == 0 || comments[^1].Tags is not { } found)
                continue;
            tags = found;
            break;
        }
        if (tags is null)
            return;
        var documented = tags.OfType<JSDocParameterOrPropertyTagNode>().Where(t => t.Kind == SyntaxKind.JSDocParameterTag
            && t.Name is not IdentifierNode { Text.Length: 0 }).ToArray();
        if (documented.Length == 0)
            return;
        var parameters = (node as IFunctionSignature)?.Parameters ?? (node as IndexSignatureDeclarationNode)?.Parameters;
        if (parameters is null)
            return;
        var names = new HashSet<string>();
        var excluded = new HashSet<int>();
        for (int i = 0; i < parameters.Count; i++)
            if (SemanticSyntax.Name(parameters[i]) is IdentifierNode name)
                names.Add(name.Text);
            else if (SemanticSyntax.Name(parameters[i]) is BindingPatternNode)
                excluded.Add(i);
        bool javaScript = (node.Flags & NodeFlags.JavaScriptFile) != 0;
        if (ContainsArgumentsReference(node, cancellation))
        {
            var last = documented[^1];
            if (!javaScript || last.Name is not IdentifierNode name || excluded.Contains(documented.Length - 1) || names.Contains(name.Text)
                || last.TypeExpression is not ITypedNode { Type: { } annotation })
                return;
            var type = await Nodes.FromNodeAsync(annotation, cancellation).ConfigureAwait(false);
            if (type is TypeReference reference && (reference.Target == ArrayTarget(false) || reference.Target == ArrayTarget(true)))
                return;
            Error(name, 8029);
        }
        else
            for (int i = 0; i < documented.Length; i++)
            {
                var tag = documented[i];
                if (excluded.Contains(i) || tag.Name is IdentifierNode name && names.Contains(name.Text))
                    continue;
                if (tag.Name is QualifiedNameNode)
                {
                    if (javaScript)
                        Error(tag.Name, 8032);
                }
                else if (!tag.IsNameFirst)
                {
                    if (javaScript)
                        Error(tag.Name!, 8024);
                    else
                        ExpressionSuggestion(tag.Name!, 8024);
                }
            }
    }

    private static SyntaxNode? NextDocumentationLocation(SyntaxNode node) => node.Parent switch
    {
        PropertyAssignmentNode or ExportAssignmentNode or PropertyDeclarationNode or VariableDeclarationNode or SatisfiesExpressionNode
            or ReturnStatementNode or VariableStatementNode or ExpressionStatementNode => node.Parent,
        VariableDeclarationListNode list when list.Declarations![0] == node => list,
        _ => null
    };

    private bool ContainsArgumentsReference(SyntaxNode node, CancellationToken cancellation)
    {
        if (SemanticSyntax.Body(node) is not { } body)
            return false;
        if (argumentsReferences.TryGetValue(node, out bool cached))
            return cached;
        var pending = new Stack<SyntaxNode>();
        pending.Push(body);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if (current is IdentifierNode { Text: "arguments" } identifier
                && program.ReferenceSymbols.Resolve(identifier, cancellation) == program.Symbols.ArgumentsSymbol)
                return argumentsReferences[node] = true;
            switch (current)
            {
                case PropertyDeclarationNode or MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode:
                    if (SemanticSyntax.Name(current) is ComputedPropertyNameNode computed)
                        pending.Push(computed);
                    continue;
                case PropertyAccessExpressionNode access:
                    pending.Push(access.Expression!);
                    continue;
                case ElementAccessExpressionNode access:
                    pending.Push(access.Expression!);
                    continue;
                case PropertyAssignmentNode property:
                    pending.Push(property.Initializer!);
                    continue;
            }
            if (current is ConstructorDeclarationNode or FunctionExpressionNode or FunctionDeclarationNode or ArrowFunctionNode
                or ModuleDeclarationNode or SourceFileNode || SemanticSyntax.TypeNode(current))
                continue;
            for (int i = current.ChildCount - 1; i >= 0; i--)
                pending.Push(current.GetChild(i));
        }
        return argumentsReferences[node] = false;
    }
}

using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed class MissingNamePrefixes(CheckerSymbols symbols, SymbolTypes values, DeclaredTypes declared, TypeProperties properties,
    Action<SyntaxNode, DiagnosticCode, Symbol?> report)
{
    internal async ValueTask<bool> CheckAsync(SyntaxNode location, TextSlice name, CancellationToken cancellation = default)
    {
        if (location is not IdentifierNode identifier || identifier.Text != name || DeclarationOrder.InTypeQuery(location))
            return false;
        var reference = location;
        while (reference.Parent is QualifiedNameNode)
            reference = reference.Parent;
        if (reference.Parent is TypeReferenceNode)
            return false;
        var container = ThisContainer(location, false, false);
        for (var current = container; current.Parent is not null; current = current.Parent)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!SemanticSyntax.ClassLike(current.Parent))
                continue;
            var symbol = symbols.Declaration(current.Parent);
            if (symbol is null)
                break;
            var constructor = await values.GetAsync(symbol, cancellation).ConfigureAwait(false);
            if (await properties.PropertyAsync(constructor, name, cancellation: cancellation).ConfigureAwait(false) is not null)
            {
                report(location, DiagnosticCode.CannotFindName0DidYouMeanTheStaticMember10, symbol);
                return true;
            }
            if (current == container && !SemanticSyntax.IsStatic(current))
            {
                var instance = (InterfaceType)await declared.GetAsync(symbol, cancellation).ConfigureAwait(false);
                if (await properties.PropertyAsync(instance.ThisType!, name, cancellation: cancellation).ConfigureAwait(false) is not null)
                {
                    report(location, DiagnosticCode.CannotFindName0DidYouMeanTheInstanceMemberThis0, symbol);
                    return true;
                }
            }
        }
        return false;
    }

    internal static SyntaxNode ThisContainer(SyntaxNode node, bool includeArrows, bool includeComputedNames)
    {
        while (true)
        {
            node = node.Parent ?? throw new InvalidOperationException("This container has no containing source file");
            switch (node)
            {
                case ComputedPropertyNameNode:
                    if (includeComputedNames && SemanticSyntax.ClassLike(node.Parent?.Parent))
                        return node;
                    node = node.Parent!.Parent!;
                    break;
                case DecoratorNode:
                    if (node.Parent is ParameterDeclarationNode && SemanticSyntax.ClassElement(node.Parent.Parent))
                        node = node.Parent.Parent!;
                    else if (SemanticSyntax.ClassElement(node.Parent))
                        node = node.Parent!;
                    break;
                case ArrowFunctionNode:
                    if (includeArrows)
                        return node;
                    break;
                case FunctionDeclarationNode or FunctionExpressionNode or ModuleDeclarationNode or ClassStaticBlockDeclarationNode
                    or PropertyDeclarationNode or PropertySignatureDeclarationNode or MethodDeclarationNode or MethodSignatureDeclarationNode
                    or ConstructorDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode or CallSignatureDeclarationNode
                    or ConstructSignatureDeclarationNode or IndexSignatureDeclarationNode or EnumDeclarationNode or SourceFileNode:
                    return node;
            }
        }
    }
}

using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IAccessFlowHost
{
    bool StrictPropertyInitialization { get; }

    ValueTask<Type> AutoPropertyFlowAsync(SyntaxNode node, Symbol? property, CancellationToken cancellation);

    ValueTask AccessErrorAsync(
        SyntaxNode node,
        DiagnosticCode code,
        CancellationToken cancellation,
        Type? type = null,
        Symbol? symbol = null,
        Type? index = null,
        Symbol? related = null);
}

internal sealed class AccessFlow(TypeContext context, TypeAlgebra algebra, SymbolTypes values, TypeWidening widening,
    ReferenceTypeNarrowing references, FlowTypes flows, IAccessFlowHost host)
{
    internal async ValueTask<Type> GetAsync(SyntaxNode node, Symbol? property, Type type, SyntaxNode errorNode,
        CheckMode mode = 0, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        int assignment = ReferenceSyntax.AssignmentKind(node);
        if (assignment == 1)
            return values.NonMissing(type, property is not null && (property.Flags & SymbolFlags.Optional) != 0);
        if (property is not null && (property.Flags & (SymbolFlags.Variable | SymbolFlags.Property | SymbolFlags.Accessor)) == 0
            && !((property.Flags & SymbolFlags.Method) != 0 && type is UnionType))
            return type;
        if (type == context.AutoType)
            return await host.AutoPropertyFlowAsync(node, property, cancellation).ConfigureAwait(false);
        type = await references.GetAsync(type, node, mode, cancellation).ConfigureAwait(false);
        bool uninitialized = false;
        if (context.StrictNullChecks && property?.ValueDeclaration is { } declaration)
        {
            if (host.StrictPropertyInitialization && node is PropertyAccessExpressionNode or ElementAccessExpressionNode
                && FlowReferences.Receiver(node)?.Kind == SyntaxKind.ThisKeyword && WithoutInitializer(declaration) && !SemanticSyntax.IsStatic(declaration))
            {
                var container = IdentifierTypes.Container(node);
                uninitialized = container is ConstructorDeclarationNode
                    && container.Parent == declaration.Parent
                    && (declaration.Flags & NodeFlags.Ambient) == 0;
            }
            else if (declaration is BinaryExpressionNode { Left: PropertyAccessExpressionNode }
                && IdentifierTypes.Container(node) == IdentifierTypes.Container(declaration))
                uninitialized = true;
        }
        var initial = uninitialized && context.StrictNullChecks
            ? await algebra.UnionAsync([type, context.UndefinedType], cancellation: cancellation).ConfigureAwait(false)
            : type;
        var result = await flows.GetAsync(node, type, initial, cancellation: cancellation).ConfigureAwait(false);
        if (uninitialized && !ContainsUndefined(type) && ContainsUndefined(result))
        {
            await host.AccessErrorAsync(
                errorNode,
                DiagnosticCode.Property0IsUsedBeforeBeingAssigned,
                cancellation,
                symbol: property).ConfigureAwait(false);
            return type;
        }
        return assignment != 0 ? await widening.LiteralBaseAsync(result, cancellation).ConfigureAwait(false) : result;
    }

    internal static bool WithoutInitializer(SyntaxNode node) => node is PropertyDeclarationNode { Initializer: null }
        && ((PropertyDeclarationNode)node).PostfixToken?.Kind != SyntaxKind.ExclamationToken
        && !SemanticSyntax.HasModifier(node, SyntaxKind.AbstractKeyword);

    private static bool ContainsUndefined(Type type) =>
        ((type is UnionType union ? union.Types[0] : type).Flags & TypeFlags.Undefined) != 0;
}

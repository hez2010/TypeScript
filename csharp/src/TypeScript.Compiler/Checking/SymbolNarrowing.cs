using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface ISymbolNarrowingHost
{
    ValueTask<Type?> BindingParentAsync(SyntaxNode declaration, CancellationToken cancellation);

    ValueTask<Type> BindingFromParentAsync(BindingElementNode element, Type parent, bool noTupleBoundsCheck, CancellationToken cancellation);

    ValueTask<bool> SomeAssignedAsync(SyntaxNode declaration, CancellationToken cancellation);

    bool ContextSensitiveFunction(SyntaxNode node);

    ValueTask<Signature?> ContextualSignatureAsync(SyntaxNode node, CancellationToken cancellation);

    TypeMapper? NonFixingMapper(SyntaxNode node);

    ValueTask<Type> IndexedAccessAsync(Type type, Type index, CancellationToken cancellation);

    FlowNode? FlowOf(SyntaxNode node);
}

internal sealed class SymbolNarrowing(TypeContext context, CheckerLinks links, SymbolTypes values, TypeAlgebra algebra,
    TypeConstraints constraints, TypeInstantiation instantiation, TypeViews views, FlowTypes flows, ISymbolNarrowingHost host)
{
    internal async ValueTask<Type> GetAsync(Symbol symbol, SyntaxNode location, CancellationToken cancellation = default)
    {
        var type = await values.GetAsync(symbol, cancellation).ConfigureAwait(false);
        var declaration = symbol.ValueDeclaration;
        if (declaration is BindingElementNode { Initializer: null, DotDotDotToken: null } element
            && ((BindingPatternNode)element.Parent!).Elements!.Count >= 2)
        {
            var root = SemanticSyntax.RootDeclaration(element);
            var initializer = (root as IInitializedNode)?.Initializer;
            if (initializer is not null && DeclarationOrder.Ancestor(location, n => n == initializer) is not null
                && IdentifierTypes.Container(element) == IdentifierTypes.Container(location))
                return type;
            var parent = element.Parent!.Parent!;
            if (!(root is VariableDeclarationNode && AssignmentMarks.ConstLike(root) || root is ParameterDeclarationNode))
                return type;
            var data = links.Nodes.Get(parent);
            if ((data.Flags & NodeCheckFlags.InCheckIdentifier) != 0)
                return type;
            Type? parentType;
            data.Flags |= NodeCheckFlags.InCheckIdentifier;
            try
            {
                parentType = await host.BindingParentAsync(parent, cancellation).ConfigureAwait(false);
                if (parentType is not null)
                    parentType = await algebra.MapAsync(
                        parentType,
                        async t => await constraints.BaseConstraintOrTypeAsync(t, cancellation).ConfigureAwait(false),
                        cancellation: cancellation).ConfigureAwait(false);
            }
            finally
            {
                data.Flags &= ~NodeCheckFlags.InCheckIdentifier;
            }
            if (parentType is UnionType
                && !(root is ParameterDeclarationNode && await host.SomeAssignedAsync(root, cancellation).ConfigureAwait(false)))
            {
                var narrowed = await flows.GetAsync(
                    element.Parent,
                    parentType,
                    parentType,
                    flow: host.FlowOf(location),
                    cancellation: cancellation).ConfigureAwait(false);
                return (narrowed.Flags & TypeFlags.Never) != 0 ? context.NeverType
                    : await host.BindingFromParentAsync(element, narrowed, true, cancellation).ConfigureAwait(false);
            }
        }
        else if (declaration is ParameterDeclarationNode { Type: null, Initializer: null, DotDotDotToken: null } parameter)
        {
            var function = parameter.Parent!;
            var parameters = ((IFunctionSignature)function).Parameters!;
            if (parameters.Count >= 2 && host.ContextSensitiveFunction(function)
                && await host.ContextualSignatureAsync(
                    function,
                    cancellation).ConfigureAwait(false) is { Parameters.Count: 1, HasRestParameter: true } contextual)
            {
                var instantiated = await instantiation.InstantiateAsync(
                    await values.GetAsync(contextual.Parameters[0], cancellation).ConfigureAwait(false),
                    host.NonFixingMapper(function),
                    cancellation: cancellation).ConfigureAwait(false);
                var rest = await views.ReducedApparentAsync(instantiated!, cancellation).ConfigureAwait(false);
                if (rest is UnionType union && union.Types.All(t => t is TypeReference { Target: TupleType }))
                {
                    foreach (var item in parameters)
                        if (await host.SomeAssignedAsync(item, cancellation).ConfigureAwait(false))
                            return type;
                    var narrowed = await flows.GetAsync(
                        function,
                        rest,
                        rest,
                        flow: host.FlowOf(location),
                        cancellation: cancellation).ConfigureAwait(false);
                    int index = parameters.IndexOf(parameter) - (parameters[0] is ParameterDeclarationNode { Name: IdentifierNode { Text.Span: "this" } }
                        ? 1
                        : 0);
                    return await host.IndexedAccessAsync(narrowed, context.GetNumberLiteralType(index), cancellation).ConfigureAwait(false);
                }
            }
        }
        return type;
    }
}

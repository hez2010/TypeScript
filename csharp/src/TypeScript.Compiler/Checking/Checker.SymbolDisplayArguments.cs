using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask<IReadOnlyList<SyntaxNode>> SymbolDisplayArgumentsAsync(Symbol symbol, Symbol next,
        TypeSyntaxContext state, CancellationToken cancellation)
    {
        if ((next.CheckFlags & Binding.CheckFlags.Instantiated) != 0)
        {
            var target = symbol;
            if ((target.Flags & SymbolFlags.Alias) != 0 && ClassLike(target) is null)
                target = await program.Aliases.ResolveAsync(target, cancellation);
            if (ClassLike(target) is { } declaration)
            {
                var parameters = (await program.Scopes.OuterAsync(declaration, false, cancellation))
                    .Concat(program.Scopes.Local(target, cancellation));
                var mapper = links.Values.TryGet(next)?.Mapper;
                var arguments = new List<SyntaxNode>();
                foreach (var parameter in parameters)
                    arguments.Add(await TypeSyntaxAsync(mapper is null ? parameter : await mapper.MapAsync(parameter, cancellation),
                        state, cancellation));
                if (arguments.Count != 0)
                    return arguments;
            }
        }
        var original = (symbol.CheckFlags & Binding.CheckFlags.Instantiated) != 0
            ? links.Values.TryGet(symbol)?.Target ?? symbol : symbol;
        IReadOnlyList<Type> typeParameters;
        if ((original.Flags & (SymbolFlags.Class | SymbolFlags.Interface | SymbolFlags.Alias)) != 0)
            typeParameters = program.Scopes.Local(symbol, cancellation);
        else if ((original.Flags & SymbolFlags.Function) != 0
            && symbol.ValueDeclaration is IFunctionSignature { TypeParameters: { } nodes })
            typeParameters = nodes.Select(n => (Type)program.Scopes.Parameter(program.Symbols.Declaration(n)!)).ToArray();
        else
            return [];
        var result = new List<SyntaxNode>();
        foreach (TypeParameter parameter in typeParameters)
        {
            var constraint = await Instantiation.Constraints.ConstraintAsync(parameter, cancellation);
            result.Add(await TypeParameterSyntaxAsync(parameter,
                constraint is null ? null : await ConstraintSyntaxAsync(parameter, constraint, state, cancellation), state, cancellation));
        }
        return result;

        static SyntaxNode? ClassLike(Symbol value) => (value.Flags & (SymbolFlags.Class | SymbolFlags.Function)) != 0
            ? value.ValueDeclaration
            : value.Declarations.FirstOrDefault(d => d is InterfaceDeclarationNode
                or VariableDeclarationNode { Initializer: FunctionExpressionNode or ArrowFunctionNode });
    }

    private async ValueTask<SyntaxNode> ConstraintSyntaxAsync(TypeParameter parameter, Type constraint,
        TypeSyntaxContext state, CancellationToken cancellation)
    {
        if (TypeConstraints.ConstraintDeclaration(parameter) is { } annotation
            && await Instantiation.Engine.InstantiateAsync(await Nodes.FromNodeAsync(annotation, cancellation), state.Mapper,
                cancellation: cancellation) == constraint
            && await ReuseTypeAnnotationSyntaxAsync(annotation, state, cancellation) is { } reused)
            return reused;
        return await TypeSyntaxAsync(constraint, state, cancellation);
    }

    private async ValueTask<SyntaxNode?> ReuseTypeAnnotationSyntaxAsync(SyntaxNode annotation, TypeSyntaxContext state,
        CancellationToken cancellation)
    {
        Dictionary<SyntaxNode, TypeParameter> parameters = [];
        foreach (var node in annotation.DescendantsAndSelf())
        {
            cancellation.ThrowIfCancellationRequested();
            if (node is TypeOperatorNode { Operator: Syntax.SyntaxKind.UniqueKeyword, Type.Kind: Syntax.SyntaxKind.SymbolKeyword })
            {
                var enclosing = state.Symbols.Enclosing;
                while (enclosing is not null && (generatedParameterScopes.Contains(enclosing) || valueParameterScopes.Contains(enclosing)))
                    enclosing = enclosing.Parent;
                bool sameScope = false;
                for (var ancestor = node; ancestor is not null; ancestor = ancestor.Parent)
                    if (ancestor == enclosing)
                    {
                        sameScope = true;
                        break;
                    }
                if (!sameScope)
                    return null;
            }
            if (node is ImportTypeNode or ThisTypeNode or ComputedPropertyNameNode
                || node.Kind is >= Syntax.SyntaxKind.FirstJSDocNode and <= Syntax.SyntaxKind.LastJSDocNode)
                return null;
            if (node is TypeParameterDeclarationNode { Name: { } parameterName } declaration
                && program.Symbols.Declaration(declaration) is { } parameterSymbol)
                parameters[parameterName] = program.Scopes.Parameter(parameterSymbol);
            if (node is not TypeReferenceNode reference)
                continue;
            var first = reference.TypeName;
            while (first is QualifiedNameNode qualified)
                first = qualified.Left;
            if (first is not IdentifierNode identifier)
                return null;
            var meaning = reference.TypeName is QualifiedNameNode ? SymbolFlags.Namespace : SymbolFlags.Type;
            var original = await program.EntityNames.ResolveAsync(identifier, meaning, true, true, cancellation: cancellation);
            if (original is not null && (original.Flags & SymbolFlags.TypeParameter) != 0)
            {
                var parameter = program.Scopes.Parameter(original);
                if (state.Mapper is not null && await state.Mapper.MapAsync(parameter, cancellation) != parameter)
                    return null;
                parameters[identifier] = parameter;
                continue;
            }
            if (state.Symbols.Enclosing is null)
                continue;
            var current = await program.EntityNames.ResolveAsync(identifier, meaning, true, true, state.Symbols.Enclosing, cancellation);
            if (current == UnknownSymbol
                || original is not null && (current is null || !await SameSymbolReferenceAsync(current, original, cancellation))
                || current is not null && (await SymbolAccessibilityAsync(
                    current,
                    state.Symbols.Enclosing,
                    meaning,
                    false,
                    true,
                    cancellation)).Accessibility
                    != SymbolAccessibility.Accessible)
                return null;
        }
        var result = await ReuseGeneratedAnnotationSyntaxAsync(annotation, parameters, state, cancellation);
        AddReusedSyntaxLength(annotation, state);
        return result;
    }
}

using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IFunctionDeclarationHost
{
    bool NoImplicitAny { get; }
    int TargetYear { get; }
    Type AnyReadonlyArray { get; }
    Type AutoArray { get; }
    Type AnyArray { get; }

    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> CheckedFunctionTypeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> CachedExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> LiteralNameTypeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask CheckLiteralAssignableAsync(Type source, Type target, SyntaxNode node, SyntaxNode expression, CancellationToken cancellation);

    ValueTask SignatureEnvironmentAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask CheckFunctionReturnAsync(SyntaxNode node, SyntaxNode annotation, Type type, CancellationToken cancellation);

    ValueTask ParameterEnvironmentAsync(ParameterDeclarationNode node, CancellationToken cancellation);

    ValueTask<bool> BindingEnvironmentAsync(BindingElementNode node, CancellationToken cancellation);

    ValueTask NonNullBindingAsync(Type type, SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> BindingIterationAsync(Type type, SyntaxNode? node, bool outOfBounds, CancellationToken cancellation);

    ValueTask<bool> FunctionModifiersAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask TypeParameterModifiersAsync(TypeParameterDeclarationNode node, CancellationToken cancellation);

    void ExpressionError(SyntaxNode node, int code);

    void DeferExpression(SyntaxNode node);

    void RegisterUnused(SyntaxNode node);
}

internal sealed class FunctionDeclarations(TypeContext context, CheckerSymbols symbols, TypeParameterScopes scopes,
    TypeConstraints constraints, TypeInstantiation instantiation, BaseTypes bases, TypeRelations relations,
    SymbolTypes values, VariableTypes variables, BindingTypes bindings, TypeProperties properties,
    MemberAccessRules access, MemberAccessibility accessibility, IFunctionDeclarationHost host)
{
    internal async ValueTask GrammarAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        if (await host.FunctionModifiersAsync(node, cancellation).ConfigureAwait(false))
            return;
        var signature = (IFunctionSignature)node;
        bool grammar = SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0;
        void Error(SyntaxNode at, int code)
        {
            if (grammar)
                host.ExpressionError(at, code);
        }
        if (signature.TypeParameters is { Count: 0 })
        {
            Error(node, 1098);
            return;
        }
        bool optional = false;
        var parameters = signature.Parameters!;
        for (int i = 0; i < parameters.Count; i++)
        {
            var parameter = (ParameterDeclarationNode)parameters[i];
            if (parameter.DotDotDotToken is not null)
            {
                if (i != parameters.Count - 1)
                {
                    Error(parameter.DotDotDotToken, 1014);
                    return;
                }
                if ((parameter.Flags & NodeFlags.Ambient) == 0 && parameters.HasTrailingComma)
                    Error(parameter, 1013);
                if (parameter.QuestionToken is not null)
                {
                    Error(parameter.QuestionToken, 1047);
                    return;
                }
                if (parameter.Initializer is not null)
                {
                    Error(parameter.Name!, 1048);
                    return;
                }
            }
            else if (parameter.QuestionToken is not null)
            {
                optional = true;
                if ((parameter.QuestionToken.Flags & NodeFlags.Reparsed) == 0 && parameter.Initializer is not null)
                {
                    Error(parameter.Name!, 1015);
                    return;
                }
            }
            else if (optional && parameter.Initializer is null)
            {
                Error(parameter.Name!, 1016);
                return;
            }
        }
        var file = SemanticSyntax.Source(node);
        if (node is ArrowFunctionNode arrow && file is not null)
        {
            if (arrow.TypeParameters is { Count: 1 } typeParameters && !typeParameters.HasTrailingComma
                && ((TypeParameterDeclarationNode)typeParameters[0]).Constraint is null
                && (file.FileName.EndsWith(".mts", StringComparison.OrdinalIgnoreCase)
                    || file.FileName.EndsWith(".cts", StringComparison.OrdinalIgnoreCase)))
                Error(typeParameters[0], 7060);
            var token = arrow.EqualsGreaterThanToken!;
            string text = file.Source.Text[file.Source.ToUtf16Position(token.Pos)..file.Source.ToUtf16Position(token.End)];
            if (text.Any(c => c is '\n' or '\r' or '\u2028' or '\u2029'))
            {
                Error(token, 1200);
                return;
            }
        }
        if (host.TargetYear >= 2016 && file is not null && SemanticSyntax.Body(node) is BlockNode { Statements: { } statements })
        {
            foreach (var statement in statements)
            {
                if (statement is not ExpressionStatementNode { Expression: StringLiteralNode literal })
                    break;
                if (literal.End < 12)
                    continue;
                var raw = file.Source.Bytes.Span[(literal.End - 12)..literal.End];
                if (!raw.SequenceEqual("\"use strict\""u8) && !raw.SequenceEqual("'use strict'"u8))
                    continue;
                var nonSimple = parameters.OfType<ParameterDeclarationNode>().Where(
                    p => p.Initializer is not null || p.Name is BindingPatternNode || p.DotDotDotToken is not null).ToArray();
                if (nonSimple.Length == 0)
                    break;
                foreach (var parameter in nonSimple)
                    host.ExpressionError(parameter, 1346);
                host.ExpressionError(statement, 1347);
                return;
            }
        }
    }

    internal async ValueTask CheckAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        await host.SignatureEnvironmentAsync(node, cancellation).ConfigureAwait(false);
        var declaration = node as IFunctionSignature;
        var declaredParameters = node is IndexSignatureDeclarationNode index ? index.Parameters! : declaration!.Parameters!;
        bool sawDefault = false;
        var typeParameters = declaration?.TypeParameters;
        for (int i = 0; i < (typeParameters?.Count ?? 0); i++)
        {
            var parameter = (TypeParameterDeclarationNode)typeParameters![i];
            await TypeParameterAsync(parameter, cancellation).ConfigureAwait(false);
            if (parameter.DefaultType is { } defaultNode)
            {
                sawDefault = true;
                foreach (var reference in defaultNode.DescendantsAndSelf().OfType<TypeReferenceNode>())
                    if (await host.TypeFromNodeAsync(reference, cancellation).ConfigureAwait(false) is TypeParameter type)
                        for (int j = i; j < typeParameters.Count; j++)
                            if (type.Symbol == symbols.Declaration(typeParameters[j]))
                                host.ExpressionError(reference, 2744);
            }
            else if (sawDefault)
                host.ExpressionError(parameter, 2706);
            for (int j = 0; j < i; j++)
                if (symbols.Binding(typeParameters[j])?.Get(typeParameters[j])?.Symbol == symbols.Binding(parameter)?.Get(parameter)?.Symbol)
                    host.ExpressionError(parameter.Name!, 2300);
        }
        foreach (ParameterDeclarationNode parameter in declaredParameters)
        {
            await host.ParameterEnvironmentAsync(parameter, cancellation).ConfigureAwait(false);
            await VariableAsync(parameter, cancellation).ConfigureAwait(false);
            if (parameter.Initializer is null && parameter.QuestionToken is not null && parameter.Name is BindingPatternNode
                && SemanticSyntax.Body(node) is not null)
                host.ExpressionError(parameter, 2463);
            if (parameter.Name is IdentifierNode { Text: "this" or "new" })
            {
                if (declaredParameters[0] != parameter)
                    host.ExpressionError(parameter, 2680);
                if (node is ConstructorDeclarationNode or ConstructSignatureDeclarationNode or ConstructorTypeNode)
                    host.ExpressionError(parameter, 2681);
                if (node is ArrowFunctionNode)
                    host.ExpressionError(parameter, 2730);
                if (node is GetAccessorDeclarationNode or SetAccessorDeclarationNode)
                    host.ExpressionError(parameter, 2784);
            }
            if (parameter.DotDotDotToken is not null && parameter.Name is not BindingPatternNode
                && !await relations.RelatedAsync(await values.GetAsync(symbols.Declaration(parameter)!, cancellation).ConfigureAwait(false),
                    host.AnyReadonlyArray, RelationKind.Assignable, cancellation).ConfigureAwait(false))
                host.ExpressionError(parameter, 2370);
        }
        if (node is ITypedNode { Type: { } annotation })
            await host.CheckFunctionReturnAsync(
                node,
                annotation,
                await host.CheckedFunctionTypeAsync(annotation, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
        else if (host.NoImplicitAny && node is ConstructSignatureDeclarationNode or CallSignatureDeclarationNode)
            host.ExpressionError(node, node is ConstructSignatureDeclarationNode ? 7013 : 7020);
        if (node is not IndexSignatureDeclarationNode)
            host.RegisterUnused(node);
    }

    internal async ValueTask TypeParameterAsync(TypeParameterDeclarationNode node, CancellationToken cancellation)
    {
        await host.TypeParameterModifiersAsync(node, cancellation).ConfigureAwait(false);
        if (node.Expression is not null && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            host.ExpressionError(node.Expression, 1110);
        if (node.Constraint is { } constraintNode)
            await host.CheckedFunctionTypeAsync(constraintNode, cancellation).ConfigureAwait(false);
        if (node.DefaultType is { } defaultNode)
            await host.CheckedFunctionTypeAsync(defaultNode, cancellation).ConfigureAwait(false);
        var parameter = scopes.Parameter(symbols.Declaration(node)!);
        await constraints.BaseConstraintAsync(parameter, cancellation).ConfigureAwait(false);
        if (await constraints.ResolvedDefaultAsync(parameter, cancellation).ConfigureAwait(false) == context.CircularConstraintType)
            host.ExpressionError(node.DefaultType!, 2716);
        var constraint = await constraints.ConstraintAsync(parameter, cancellation).ConfigureAwait(false);
        var defaultType = await constraints.DefaultAsync(parameter, cancellation).ConfigureAwait(false);
        if (constraint is not null && defaultType is not null)
        {
            var target = await bases.WithThisAsync((await instantiation.InstantiateAsync(constraint,
                TypeMapper.Create([parameter], [defaultType]),
                cancellation: cancellation).ConfigureAwait(false))!,
                defaultType,
                cancellation: cancellation).ConfigureAwait(false);
            if (!await relations.RelatedAsync(defaultType, target, RelationKind.Assignable, cancellation).ConfigureAwait(false))
                host.ExpressionError(node.DefaultType!, 2344);
        }
        if (node.Name!.Text is "any" or "unknown" or "never" or "number" or "string" or "boolean" or "bigint" or "symbol" or "void"
            or "object" or "undefined")
            host.ExpressionError(node.Name, 2368);
        host.DeferExpression(node);
    }

    internal async ValueTask VariableAsync(SyntaxNode node, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var name = ((INamedNode)node).Name;
        if (name is null)
            return;
        var initializer = (node as IInitializedNode)?.Initializer;
        if (node is not BindingElementNode && node is ITypedNode { Type: { } annotation })
            await host.CheckedFunctionTypeAsync(annotation, cancellation).ConfigureAwait(false);
        if (node is BindingElementNode element)
        {
            if (await host.BindingEnvironmentAsync(element, cancellation).ConfigureAwait(false))
                return;
            var parent = node.Parent!.Parent!;
            var parentType = await bindings.ParentAsync(
                parent,
                element.DotDotDotToken is null ? CheckMode.Normal : CheckMode.RestBindingElement,
                cancellation).ConfigureAwait(false);
            var propertyName = element.PropertyName ?? name;
            if (parentType is not null && propertyName is not BindingPatternNode)
            {
                var key = await host.LiteralNameTypeAsync(propertyName, cancellation).ConfigureAwait(false);
                if ((key.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0
                    && await properties.PropertyAsync(
                        parentType,
                        MappedMembers.PropertyName(key),
                        cancellation: cancellation).ConfigureAwait(false) is { } property)
                {
                    access.MarkReferenced(property, node, node, null, cancellation);
                    await accessibility.CheckAsync(node, (parent as IInitializedNode)?.Initializer?.Kind == SyntaxKind.SuperKeyword,
                        false, parentType, property, cancellation: cancellation).ConfigureAwait(false);
                }
            }
        }
        if (name is BindingPatternNode pattern)
            foreach (var child in pattern.Elements!.OfType<BindingElementNode>())
                await VariableAsync(child, cancellation).ConfigureAwait(false);
        var root = SemanticSyntax.RootDeclaration(node);
        var function = root.Parent!;
        bool parameterDeclaration = root is ParameterDeclarationNode;
        if (initializer is not null && parameterDeclaration && SemanticSyntax.Body(function) is null)
        {
            host.ExpressionError(node, 2371);
            return;
        }
        if (name is BindingPatternNode binding)
        {
            if ((node.Flags & NodeFlags.Ambient) != 0 || parameterDeclaration && SemanticSyntax.Body(function) is null)
                return;
            bool empty = !binding.Elements!.OfType<BindingElementNode>().Any(e => e.Name is not null);
            if (initializer is not null || empty)
            {
                var widened = await variables.GetAsync(node, false, cancellation).ConfigureAwait(false);
                if (initializer is not null)
                {
                    var value = await host.CachedExpressionAsync(initializer, 0, cancellation).ConfigureAwait(false);
                    if (context.StrictNullChecks && empty)
                        await host.NonNullBindingAsync(value, node, cancellation).ConfigureAwait(false);
                    else
                        await host.CheckLiteralAssignableAsync(
                            value,
                            await variables.GetAsync(node, false, cancellation).ConfigureAwait(false),
                            node,
                            initializer,
                            cancellation).ConfigureAwait(false);
                }
                if (empty)
                {
                    if (binding.Kind == SyntaxKind.ArrayBindingPattern)
                        await host.BindingIterationAsync(widened, node, false, cancellation).ConfigureAwait(false);
                    else if (context.StrictNullChecks)
                        await host.NonNullBindingAsync(widened, node, cancellation).ConfigureAwait(false);
                }
            }
            return;
        }
        var symbol = symbols.Declaration(node)!;
        var type = await values.GetAsync(symbol, cancellation).ConfigureAwait(false);
        if (type == context.AutoType)
            type = context.AnyType;
        else if (type == host.AutoArray)
            type = host.AnyArray;
        if (node != symbol.ValueDeclaration)
        {
            var declarationType = await variables.GetAsync(node, false, cancellation).ConfigureAwait(false);
            if (declarationType == context.AutoType)
                declarationType = context.AnyType;
            else if (declarationType == host.AutoArray)
                declarationType = host.AnyArray;
            if (type != context.ErrorType && declarationType != context.ErrorType && (symbol.Flags & SymbolFlags.Assignment) == 0
                && !await relations.RelatedAsync(type, declarationType, RelationKind.Identity, cancellation).ConfigureAwait(false))
                host.ExpressionError(name, node is PropertyDeclarationNode or PropertySignatureDeclarationNode ? 2717 : 2403);
            type = declarationType;
        }
        if (initializer is not null && node.Parent?.Parent?.Kind != SyntaxKind.ForInStatement)
            await host.CheckLiteralAssignableAsync(
                await host.CachedExpressionAsync(initializer, 0, cancellation).ConfigureAwait(false),
                type,
                node,
                initializer,
                cancellation).ConfigureAwait(false);
    }
}

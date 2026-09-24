using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using Checking = TypeScript.Compiler.Checking;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask<Type> AssignedTypeAsync(SyntaxNode node, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        switch (node.Parent)
        {
            case ForInOrOfStatementNode loop:
                return loop.Kind == SyntaxKind.ForInStatement ? context.StringType : await ForOfElementAsync(loop, cancellation);
            case BinaryExpressionNode binary:
                if (binary.Parent is ArrayLiteralExpressionNode pattern && IdentifierTypes.DestructuringTarget(pattern)
                    || binary.Parent is PropertyAssignmentNode assignment && IdentifierTypes.DestructuringTarget(assignment.Parent!))
                    return await AssignmentDefaultAsync(await AssignedTypeAsync(binary, cancellation), binary.Right, cancellation);
                return await ExpressionAsync(binary.Right!, cancellation);
            case DeleteExpressionNode:
                return context.UndefinedType;
            case ArrayLiteralExpressionNode array:
                return await Bindings.InitialArrayElementAsync(
                    await AssignedTypeAsync(array, cancellation),
                    array.Elements!.ToList().IndexOf(node),
                    cancellation);
            case SpreadElementNode spread:
                return await ArrayAsync(
                    await BindingIterationAsync(await AssignedTypeAsync(spread.Parent!, cancellation), null, false, cancellation),
                    cancellation);
            case PropertyAssignmentNode property:
                return await Bindings.DestructuredPropertyAsync(
                    await AssignedTypeAsync(property.Parent!, cancellation),
                    property.Name!,
                    cancellation);
            case ShorthandPropertyAssignmentNode shorthand:
                return await AssignmentDefaultAsync(
                    await Bindings.DestructuredPropertyAsync(
                    await AssignedTypeAsync(shorthand.Parent!, cancellation),
                    shorthand.Name!, cancellation), shorthand.ObjectAssignmentInitializer, cancellation);
            default:
                return context.ErrorType;
        }
    }

    private async ValueTask<Type> AssignmentDefaultAsync(Type type, SyntaxNode? initializer, CancellationToken cancellation)
        => initializer is null ? type : await Algebra.UnionAsync([await NonUndefinedAsync(type, cancellation),
            await ExpressionAsync(initializer, cancellation)], cancellation: cancellation);

    private async ValueTask<Type> CheckDestructuringAsync(SyntaxNode node, Type source, CheckMode mode, bool rightIsThis,
        CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var target = node;
        if (node is ShorthandPropertyAssignmentNode shorthand)
        {
            if (shorthand.ObjectAssignmentInitializer is { } initializer)
            {
                if (context.StrictNullChecks && await Facts.GetAsync(await Expressions.CheckAsync(initializer, cancellation: cancellation),
                    TypeFacts.IsUndefined, cancellation) == 0)
                    source = await Facts.FilterAsync(source, TypeFacts.NEUndefined, cancellation);
                var left = await Expressions.CheckAsync(shorthand.Name!, mode, cancellation);
                var right = await Expressions.CheckAsync(initializer, mode, cancellation);
                await AssignmentAsync(shorthand.Name!, SyntaxKind.EqualsToken, initializer, left, right, cancellation);
            }
            target = shorthand.Name!;
        }
        if (target is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.EqualsToken } binary)
        {
            await Binary.CheckAsync(binary, mode, cancellation);
            target = binary.Left!;
            if (context.StrictNullChecks)
                source = await Facts.FilterAsync(source, TypeFacts.NEUndefined, cancellation);
        }
        if (target is ObjectLiteralExpressionNode literal)
        {
            if (context.StrictNullChecks && literal.Properties!.Count == 0)
                return await NonNullAsync(source, literal, cancellation);
            for (int i = 0; i < literal.Properties!.Count; i++)
                await DestructuringPropertyAsync(literal, source, i, rightIsThis, cancellation);
            return source;
        }
        if (target is ArrayLiteralExpressionNode array)
        {
            var outOfBounds = await BindingIterationAsync(source, array, true, cancellation);
            Type? inBounds = NoUncheckedIndexedAccess ? null : outOfBounds;
            for (int i = 0; i < array.Elements!.Count; i++)
            {
                var element = array.Elements[i];
                if (element.Kind == SyntaxKind.OmittedExpression)
                    continue;
                if (element is SpreadElementNode spread)
                {
                    inBounds ??= await BindingIterationAsync(source, array, false, cancellation);
                    if (i != array.Elements.Count - 1)
                        Error(element, 2462);
                    else if (spread.Expression is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.EqualsToken } restDefault)
                        Error(restDefault.OperatorToken!, 1186);
                    else
                    {
                        DestructuringTrailingComma(array.Elements, array);
                        var rest = (source is UnionType union ? union.Types : [source]).All(t => t is TypeReference { Target: TupleType })
                            ? (await Algebra.MapAsync(source, async part => await Instantiation.Tuples.SliceAsync((TypeReference)part, i,
                                cancellation: cancellation), cancellation: cancellation))!
                            : await ArrayAsync(inBounds, cancellation);
                        await CheckDestructuringAsync(spread.Expression!, rest, mode, false, cancellation);
                    }
                }
                else
                {
                    var elementType = outOfBounds;
                    if (await ArrayLikeAsync(source, cancellation))
                    {
                        var index = context.GetNumberLiteralType(i);
                        var flags = Checking.AccessFlags.ExpressionPosition | (ObjectLiterals.DefaultValue(element)
                            ? Checking.AccessFlags.AllowMissing
                            : 0);
                        elementType = await Indexed.TryGetAsync(source, index, flags, CallArguments.Synthetic(element, index),
                            cancellation: cancellation) ?? context.ErrorType;
                        if (ObjectLiterals.DefaultValue(element))
                            elementType = await Facts.FilterAsync(elementType, TypeFacts.NEUndefined, cancellation);
                        elementType = await Bindings.DestructuringFlowAsync(element, elementType, cancellation);
                    }
                    await CheckDestructuringAsync(element, elementType, mode, false, cancellation);
                }
            }
            return source;
        }
        return await AssignmentChecks.ReferenceAsync(target, source, mode, cancellation);
    }

    private async ValueTask DestructuringPropertyAsync(ObjectLiteralExpressionNode node, Type source, int index, bool rightIsThis,
        CancellationToken cancellation)
    {
        var property = node.Properties![index];
        if (property is PropertyAssignmentNode or ShorthandPropertyAssignmentNode)
        {
            var name = SemanticSyntax.Name(property)!;
            if (name is PrivateIdentifierNode && SemanticSyntax.Source(name)?.ParseDiagnostics.Count == 0)
                Error(name, 18064);
            var key = await LiteralNameTypeAsync(name, cancellation);
            if ((key.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0
                && await Properties.PropertyAsync(source, MappedMembers.PropertyName(key), cancellation: cancellation) is { } symbol)
            {
                var receiver = rightIsThis && node.Parent is BinaryExpressionNode binary ? binary.Right! : node;
                MemberAccess.MarkReferenced(symbol, property, receiver, source.Symbol, cancellation);
                await AccessibilityAsync(property, false, true, source, symbol, cancellation);
            }
            var element = await Indexed.GetAsync(source, key,
                Checking.AccessFlags.ExpressionPosition | (ObjectLiterals.DefaultValue(property) ? Checking.AccessFlags.AllowMissing : 0),
                name,
                cancellation: cancellation);
            var type = await Bindings.DestructuringFlowAsync(property, element, cancellation);
            await CheckDestructuringAsync(property is PropertyAssignmentNode assignment ? assignment.Initializer! : property,
                type, 0, false, cancellation);
        }
        else if (property is SpreadAssignmentNode spread)
        {
            if (index != node.Properties.Count - 1)
            {
                Error(property, 2462);
                return;
            }
            if (TargetYear < 2018)
                await ExternalHelpersAsync(property, ["__rest"], cancellation);
            var excluded = node.Properties.Where(p => p is not SpreadAssignmentNode).Select(p => SemanticSyntax.Name(p)!).ToArray();
            var rest = await Bindings.RestAsync(source, excluded, source.Symbol, cancellation);
            DestructuringTrailingComma(node.Properties, node);
            await CheckDestructuringAsync(spread.Expression!, rest, 0, false, cancellation);
        }
        else
            Error(property, 1136);
    }

    private void DestructuringTrailingComma(NodeList nodes, SyntaxNode node)
    {
        if (nodes.HasTrailingComma && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            Error(node, 1013);
    }
}

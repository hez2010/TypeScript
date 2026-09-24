using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IAccessExpressionHost : IAccessFlowHost
{
    bool NoImplicitAny { get; }
    bool NoUncheckedIndexedAccess { get; }
    bool NoPropertyAccessFromIndexSignature { get; }

    ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> NonNullAsync(Type type, SyntaxNode node, CancellationToken cancellation);

    ValueTask MarkPropertyAliasAsync(SyntaxNode node, Symbol? property, Type parentType, CancellationToken cancellation);

    ValueTask<(Symbol? Property, Type? Result)> PrivatePropertyAsync(
        SyntaxNode node,
        Type leftType,
        Type apparentType,
        PrivateIdentifierNode name,
        bool anyLike,
        int assignment,
        CancellationToken cancellation);

    ValueTask<bool> UncheckedJsAsync(SyntaxNode node, Symbol? symbol, CancellationToken cancellation);

    ValueTask<bool> JsLiteralAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> ExtendingInterfaceAsync(SyntaxNode node, CancellationToken cancellation);

    void MissingProperty(SyntaxNode node, Type type, bool suggestion);

    ValueTask PropertyDeprecatedAsync(Symbol property, SyntaxNode node, SyntaxNode errorNode, CancellationToken cancellation);

    ValueTask IndexDeprecatedAsync(IndexInfo index, SyntaxNode node, CancellationToken cancellation);

    ValueTask PropertyBeforeDeclarationAsync(Symbol property, SyntaxNode node, SyntaxNode name, CancellationToken cancellation);

    ValueTask MarkPropertyAsync(Symbol property, SyntaxNode node, SyntaxNode receiver, Symbol? parent, CancellationToken cancellation);

    ValueTask AccessibilityAsync(SyntaxNode node, bool super, bool writing, Type type, Symbol property, CancellationToken cancellation);

    ValueTask<bool> ReadonlyAssignmentAsync(SyntaxNode node, Symbol property, int assignment, CancellationToken cancellation);

    ValueTask<bool> AutoConstructorPropertyAsync(SyntaxNode node, Symbol property, CancellationToken cancellation);

    ValueTask<bool> NumericForInAsync(SyntaxNode index, CancellationToken cancellation);

    ValueTask<Type> ValidateIndexAsync(Type type, SyntaxNode node, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);
}

internal sealed class AccessExpressions(TypeContext context, CheckerLinks links, CheckerSymbols symbols, AliasResolver aliases,
    TypeAlgebra algebra, TypeWidening widening, TypeViews views, TypeProperties properties, SymbolTypes values, IndexSignatures indexes,
    IndexedTypes indexed, MappedTypes mapped, OptionalExpressions optional, AccessFlow flow, FlowTypes flows, IAccessExpressionHost host)
{
    internal async ValueTask<Type> PropertyAsync(
        PropertyAccessExpressionNode node,
        CheckMode mode = 0,
        bool writeOnly = false,
        CancellationToken cancellation = default)
    {
        var left = await host.CheckExpressionAsync(node.Expression!, 0, cancellation).ConfigureAwait(false);
        bool chain = (node.Flags & NodeFlags.OptionalChain) != 0;
        var receiver = chain ? await optional.ReceiverAsync(left, node.Expression!, cancellation).ConfigureAwait(false) : left;
        var type = await PropertyWorkerAsync(
            node,
            node.Expression!,
            await host.NonNullAsync(receiver, node.Expression!, cancellation).ConfigureAwait(false),
            node.Name!, mode, chain ? false : writeOnly, cancellation).ConfigureAwait(false);
        return chain ? await optional.PropagateAsync(type, node, receiver != left, cancellation).ConfigureAwait(false) : type;
    }

    internal async ValueTask<Type> QualifiedAsync(QualifiedNameNode node, CheckMode mode = 0, CancellationToken cancellation = default)
    {
        var left = await host.CheckExpressionAsync(node.Left!, 0, cancellation).ConfigureAwait(false);
        return await PropertyWorkerAsync(node, node.Left!, await host.NonNullAsync(left, node.Left!, cancellation).ConfigureAwait(false),
            node.Right!, mode, false, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> NonNullChainAsync(
        NonNullExpressionNode node,
        TypeFactQueries facts,
        CancellationToken cancellation = default)
    {
        var left = await host.CheckExpressionAsync(node.Expression!, 0, cancellation).ConfigureAwait(false);
        var receiver = await optional.ReceiverAsync(left, node.Expression!, cancellation).ConfigureAwait(false);
        return await optional.PropagateAsync(
            await facts.NonNullableAsync(receiver, cancellation).ConfigureAwait(false),
            node,
            receiver != left,
            cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> PropertyWorkerAsync(SyntaxNode node, SyntaxNode left, Type leftType, SyntaxNode right,
        CheckMode mode, bool writeOnly, CancellationToken cancellation)
    {
        var parent = links.SymbolNodes.Get(left).ResolvedSymbol;
        int assignment = ReferenceSyntax.AssignmentKind(node);
        var widened = assignment != 0 || MethodCall(node)
            ? await widening.GetAsync(leftType, cancellation).ConfigureAwait(false)
            : leftType;
        var apparent = await views.ApparentAsync(widened, cancellation).ConfigureAwait(false);
        bool anyLike = (apparent.Flags & TypeFlags.Any) != 0 || apparent == context.SilentNeverType;
        Symbol? property;
        if (right is PrivateIdentifierNode privateName)
        {
            var result = await host.PrivatePropertyAsync(
                node,
                leftType,
                apparent,
                privateName,
                anyLike,
                assignment,
                cancellation).ConfigureAwait(false);
            if (result.Result is not null)
                return result.Result;
            property = result.Property;
        }
        else
        {
            if (anyLike)
            {
                if (left is IdentifierNode && parent is not null)
                    await host.MarkPropertyAliasAsync(node, null, leftType, cancellation).ConfigureAwait(false);
                return ErrorType(apparent) ? context.ErrorType : apparent;
            }
            property = await properties.PropertyAsync(
                apparent,
                SyntaxNameText.Get(right),
                ConstEnum(apparent),
                node is QualifiedNameNode,
                cancellation).ConfigureAwait(false);
        }
        await host.MarkPropertyAliasAsync(node, property, leftType, cancellation).ConfigureAwait(false);
        Type type;
        if (property is null)
        {
            IndexInfo? index = null;
            if (right is not PrivateIdentifierNode && (assignment == 0
                || ((await mapped.GenericFlagsAsync(leftType, cancellation).ConfigureAwait(false)) & ObjectFlags.IsGenericObjectType) == 0
                || leftType is TypeParameter { IsThisType: true }))
                index = await indexes.ApplicableAsync(await host.IndexesAsync(apparent, cancellation).ConfigureAwait(false),
                    context.GetStringLiteralType(SyntaxNameText.Get(right)), cancellation).ConfigureAwait(false);
            if (index is null)
            {
                bool suggestion = await host.UncheckedJsAsync(node, leftType.Symbol, cancellation).ConfigureAwait(false);
                if (!suggestion && await host.JsLiteralAsync(leftType, cancellation).ConfigureAwait(false))
                    return context.AnyType;
                if (leftType.Symbol == symbols.GlobalThisSymbol)
                {
                    var global = symbols.GlobalThisSymbol.Exports.GetValueOrDefault(SyntaxNameText.Get(right));
                    if (global is not null && (global.Flags & SymbolFlags.BlockScoped) != 0)
                        host.AccessError(right, 2339, leftType);
                    else if (host.NoImplicitAny)
                        host.AccessError(right, 7017, leftType);
                    return context.AnyType;
                }
                if (SyntaxNameText.Get(right).Length != 0 && !await host.ExtendingInterfaceAsync(node, cancellation).ConfigureAwait(false))
                    host.MissingProperty(right, leftType is TypeParameter { IsThisType: true } ? apparent : leftType, suggestion);
                return context.ErrorType;
            }
            ReadonlyIndex(index, apparent, node);
            type = index.ValueType;
            if (host.NoUncheckedIndexedAccess && assignment != 1)
                type = await algebra.UnionAsync([type, context.MissingType], cancellation: cancellation).ConfigureAwait(false);
            if (host.NoPropertyAccessFromIndexSignature && node is PropertyAccessExpressionNode)
                host.AccessError(right, 4111);
            if (index.Declaration is not null)
                await host.IndexDeprecatedAsync(index, right, cancellation).ConfigureAwait(false);
        }
        else
        {
            var target = await aliases.WithDeprecationAsync(property, right, cancellation).ConfigureAwait(false);
            await host.PropertyDeprecatedAsync(target, node, right, cancellation).ConfigureAwait(false);
            await host.PropertyBeforeDeclarationAsync(property, node, right, cancellation).ConfigureAwait(false);
            await host.MarkPropertyAsync(property, node, left, parent, cancellation).ConfigureAwait(false);
            links.SymbolNodes.Get(node).ResolvedSymbol = property;
            await host.AccessibilityAsync(
                node,
                left.Kind == SyntaxKind.SuperKeyword,
                ReferenceSyntax.AccessKind(node) != 0,
                apparent,
                property,
                cancellation).ConfigureAwait(false);
            if (await host.ReadonlyAssignmentAsync(node, property, assignment, cancellation).ConfigureAwait(false))
            {
                host.AccessError(right, 2540, symbol: property);
                return context.ErrorType;
            }
            type = await host.AutoConstructorPropertyAsync(node, property, cancellation).ConfigureAwait(false) ? context.AutoType
                : writeOnly || ReferenceSyntax.AccessKind(node) == 1 ? await values.WriteAsync(property, cancellation).ConfigureAwait(false)
                : await values.GetAsync(property, cancellation).ConfigureAwait(false);
        }
        return await flow.GetAsync(node, property, type, right, mode, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> ElementAsync(
        ElementAccessExpressionNode node,
        CheckMode mode = 0,
        CancellationToken cancellation = default)
    {
        var left = await host.CheckExpressionAsync(node.Expression!, 0, cancellation).ConfigureAwait(false);
        bool chain = (node.Flags & NodeFlags.OptionalChain) != 0;
        var receiver = chain ? await optional.ReceiverAsync(left, node.Expression!, cancellation).ConfigureAwait(false) : left;
        var result = await ElementWorkerAsync(
            node,
            await host.NonNullAsync(receiver, node.Expression!, cancellation).ConfigureAwait(false),
            mode,
            cancellation).ConfigureAwait(false);
        return chain ? await optional.PropagateAsync(result, node, receiver != left, cancellation).ConfigureAwait(false) : result;
    }

    private async ValueTask<Type> ElementWorkerAsync(
        ElementAccessExpressionNode node,
        Type receiver,
        CheckMode mode,
        CancellationToken cancellation)
    {
        int assignment = ReferenceSyntax.AssignmentKind(node);
        var type = assignment != 0 || MethodCall(node) ? await widening.GetAsync(receiver, cancellation).ConfigureAwait(false) : receiver;
        var index = await host.CheckExpressionAsync(node.ArgumentExpression!, 0, cancellation).ConfigureAwait(false);
        if (ErrorType(type) || type == context.SilentNeverType)
            return type;
        if (ConstEnum(type) && node.ArgumentExpression is not (StringLiteralNode or NoSubstitutionTemplateLiteralNode))
        {
            host.AccessError(node.ArgumentExpression!, 2476);
            return context.ErrorType;
        }
        if (await host.NumericForInAsync(node.ArgumentExpression!, cancellation).ConfigureAwait(false))
            index = context.NumberType;
        AccessFlags flags = assignment == 0 ? AccessFlags.ExpressionPosition : AccessFlags.Writing
            | (assignment == 2 ? AccessFlags.ExpressionPosition : 0)
            | (((await mapped.GenericFlagsAsync(type, cancellation).ConfigureAwait(false)) & ObjectFlags.IsGenericObjectType) != 0
                && type is not TypeParameter { IsThisType: true } ? AccessFlags.NoIndexSignatures : 0);
        var indexedType = await indexed.TryGetAsync(
            type,
            index,
            flags,
            node,
            cancellation: cancellation).ConfigureAwait(false) ?? context.ErrorType;
        var result = await flow.GetAsync(
            node,
            links.SymbolNodes.Get(node).ResolvedSymbol,
            indexedType,
            node.ArgumentExpression!,
            mode,
            cancellation).ConfigureAwait(false);
        return await host.ValidateIndexAsync(result, node, cancellation).ConfigureAwait(false);
    }

    internal void ReadonlyIndex(IndexInfo? index, Type type, SyntaxNode? node)
    {
        if (index?.IsReadonly == true && node is not null && (ReferenceSyntax.AssignmentTarget(node) is not null || DeleteTarget(node)))
            host.AccessError(node, 2542, type);
    }

    internal async ValueTask<Type?> ElementPropertyAsync(Symbol property, Type objectType, ElementAccessExpressionNode node,
        AccessFlags flags, CancellationToken cancellation = default)
    {
        await host.MarkPropertyAsync(property, node, node.Expression!, objectType.Symbol, cancellation).ConfigureAwait(false);
        int assignment = ReferenceSyntax.AssignmentKind(node);
        if (await host.ReadonlyAssignmentAsync(node, property, assignment, cancellation).ConfigureAwait(false))
        {
            host.AccessError(node.ArgumentExpression!, 2540, symbol: property);
            return null;
        }
        if ((flags & AccessFlags.CacheSymbol) != 0)
            links.SymbolNodes.Get(node).ResolvedSymbol = property;
        if (await host.AutoConstructorPropertyAsync(node, property, cancellation).ConfigureAwait(false))
            return context.AutoType;
        var type = (flags & AccessFlags.Writing) != 0 ? await values.WriteAsync(property, cancellation).ConfigureAwait(false)
            : await values.GetAsync(property, cancellation).ConfigureAwait(false);
        return assignment != 1 ? await flows.GetAsync(node, type, cancellation: cancellation).ConfigureAwait(false) : type;
    }

    private bool ErrorType(Type type) => type == context.ErrorType || (type.Flags & TypeFlags.Any) != 0 && type.Alias is not null;

    internal static bool MethodCall(SyntaxNode node)
    {
        while (node.Parent is ParenthesizedExpressionNode)
            node = node.Parent;
        return node.Parent is CallExpressionNode call && call.Expression == node
            || node.Parent is NewExpressionNode construct && construct.Expression == node;
    }

    internal static bool DeleteTarget(SyntaxNode node)
    {
        if (node is not (PropertyAccessExpressionNode or ElementAccessExpressionNode))
            return false;
        while (node.Parent is ParenthesizedExpressionNode)
            node = node.Parent;
        return node.Parent is DeleteExpressionNode;
    }

    internal static bool ConstEnum(Type type) => (type.ObjectFlags & ObjectFlags.Anonymous) != 0 && type.Symbol is { Flags: var flags }
        && (flags & SymbolFlags.ConstEnum) != 0;
}

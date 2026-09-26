using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IBindingPatternHost, IExpressionContextHost, IArrayLiteralHost, IObjectLiteralHost, ITypeDiscriminationHost, IExcessPropertyHost, ILateMemberHost
{
    internal BindingPatterns BindingPatterns { get; }
    internal ExpressionContexts Contexts { get; }
    internal ContextualProperties ContextualProperties { get; }
    internal ArrayLiterals ArrayLiterals { get; }
    internal ObjectSpreads ObjectSpreads { get; }
    internal ObjectLiterals ObjectLiterals { get; }
    internal TypeDiscrimination TypeDiscrimination { get; }
    internal ExcessProperties ExcessProperties { get; }
    internal LateMembers LateMembers { get; }

    public ValueTask<Type> ComputedNameAsync(ComputedPropertyNameNode node, CancellationToken cancellation) =>
        ObjectLiterals.ComputedAsync(node, cancellation);

    public ValueTask<bool> LateIndexTypeAsync(Type type, CancellationToken cancellation) =>
        AssignableAsync(type, context.StringNumberSymbolType, cancellation);

    public ValueTask<IReadOnlyDictionary<string, Symbol>> ModuleExportsAsync(Symbol symbol, CancellationToken cancellation) =>
        program.ModuleExports.ResolveAsync(symbol, cancellation);

    public ValueTask<Type> ObjectMethodAsync(MethodDeclarationNode node, CheckMode mode, CancellationToken cancellation) =>
        Functions.CheckAsync(node, mode, cancellation);

    public async ValueTask CheckLiteralAssignableAsync(
        Type source,
        Type target,
        SyntaxNode node,
        SyntaxNode expression,
        CancellationToken cancellation) =>
        await RelationDiagnostics.CheckAsync(source, target, RelationKind.Assignable, node, expression, cancellation: cancellation);

    public void SpreadOverride(SyntaxNode node, Symbol property, SyntaxNode spread)
        => Error(node, CheckerDiagnostic.Create(node, Messages.X_0_is_specified_more_than_once_so_this_usage_will_be_overwritten,
            property.Name) with
        { RelatedInformation = [CheckerDiagnostic.Create(spread, Messages.This_spread_always_overwrites_this_property)] });

    public bool ContextSensitive(SyntaxNode node) => program.IsContextSensitive(node);

    public ValueTask<Type> SpreadElementAsync(Type type, SyntaxNode node, CancellationToken cancellation) =>
        Iteration.CheckAsync(IterationUse.Spread, type, context.UndefinedType, node, cancellation);

    public ValueTask<Type> PatternInitializerAsync(BindingElementNode element, Type contextual, CancellationToken cancellation) =>
        DeclarationInitializerWithContextAsync(element, 0, contextual, cancellation);

    public async ValueTask<Type> IterableOfAsync(Type element, CancellationToken cancellation)
    {
        var target = await program.Globals.GetAsync("Iterable", 3, true, cancellation);
        return target == context.EmptyGenericType ? context.EmptyObjectType
            : context.CreateTypeReference((InterfaceType)target, [element, context.VoidType, context.UndefinedType]);
    }

    public async ValueTask<Type?> StaticPropertyContextAsync(
        PropertyDeclarationNode node,
        ContextFlags flags,
        CancellationToken cancellation) =>
        node.Parent is ClassExpressionNode && await Contexts.GetAsync(node.Parent, flags, cancellation) is { } parent
            ? await ContextualProperties.GetAsync(parent, program.Symbols.Declaration(node)!.Name, cancellation: cancellation) : null;

    public async ValueTask<Type?> ObjectElementContextAsync(SyntaxNode node, ContextFlags flags, CancellationToken cancellation)
    {
        if (node is ITypedNode { Type: { } annotation } && node is not MethodDeclarationNode)
            return await Nodes.FromNodeAsync(annotation, cancellation);
        if (await Contexts.ApparentAsync(node.Parent!, flags, cancellation) is not { } type)
            return null;
        if (await BindableNameAsync(node, cancellation))
        {
            var symbol = program.Symbols.Declaration(node)!;
            return await ContextualProperties.GetAsync(type, symbol.Name, links.Values.Get(symbol).NameType, cancellation);
        }
        if (node is INamedNode { Name: ComputedPropertyNameNode computed })
        {
            var key = await Expressions.CheckAsync(computed.Expression!, cancellation: cancellation);
            if ((key.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0
                && await ContextualProperties.GetAsync(type, MappedMembers.PropertyName(key), cancellation: cancellation) is { } property)
                return property;
        }
        if (node is INamedNode { Name: { } name })
        {
            var key = await LiteralNameTypeAsync(name, cancellation);
            return await Algebra.MapAsync(type, async part => part is StructuredType structured
                ? (await IndexSignatures.ApplicableAsync(
                    (await Members.ResolveAsync(structured, cancellation)).IndexInfos,
                    key,
                    cancellation))?.ValueType
                : null, true, cancellation);
        }
        return null;
    }

    public ValueTask<Type> DiscriminateObjectContextAsync(ObjectLiteralExpressionNode node, UnionType type, CancellationToken cancellation) =>
        TypeDiscrimination.ObjectAsync(node, type, cancellation);

    public ValueTask<Type?> IteratedContextAsync(Type type, CancellationToken cancellation) =>
        Iteration.TryAsync(IterationUse.Element, type, context.UndefinedType, cancellation: cancellation);

    public async ValueTask<bool> ConstArgumentAsync(SyntaxNode node, CancellationToken cancellation)
    {
        while (node is ParenthesizedExpressionNode parentheses)
            node = parentheses.Expression!;
        if (node.Kind is SyntaxKind.StringLiteral or SyntaxKind.NoSubstitutionTemplateLiteral or SyntaxKind.NumericLiteral
            or SyntaxKind.BigIntLiteral
            or SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword or SyntaxKind.ArrayLiteralExpression or SyntaxKind.ObjectLiteralExpression or SyntaxKind.TemplateExpression)
            return true;
        if (node is PrefixUnaryExpressionNode prefix)
            return prefix.Operator == SyntaxKind.MinusToken && prefix.Operand is NumericLiteralNode or BigIntLiteralNode
                || prefix.Operator == SyntaxKind.PlusToken && prefix.Operand is NumericLiteralNode;
        SyntaxNode? receiver = node switch
        {
            PropertyAccessExpressionNode p => p.Expression,
            ElementAccessExpressionNode e => e.Expression,
            _ => null
        };
        while (receiver is ParenthesizedExpressionNode parentheses)
            receiver = parentheses.Expression;
        return receiver is not null && ConstantEvaluator.EntityName(receiver)
            && await program.EntityNames.ResolveAsync(receiver, SymbolFlags.Value, true, cancellation: cancellation) is { Flags: var flags }
            && (flags & SymbolFlags.Enum) != 0;
    }

    public bool InlineImportAttributes(SyntaxNode node)
    {
        if (node is not ObjectLiteralExpressionNode || node.Parent is not PropertyAssignmentNode property || property.Initializer != node
            || property.Name is not IdentifierNode and not StringLiteralNode and not NoSubstitutionTemplateLiteralNode
            || SyntaxNameText.Get(property.Name) != "with" || property.Parent is not ObjectLiteralExpressionNode options)
            return false;
        var call = DeclarationOrder.Ancestor(
            options,
            n => n is CallExpressionNode { Expression.Kind: SyntaxKind.ImportKeyword }) as CallExpressionNode;
        if (call?.Arguments is not { Count: > 1 } arguments)
            return false;
        var expression = arguments[1];
        while (expression is ParenthesizedExpressionNode parentheses)
            expression = parentheses.Expression!;
        return expression == options;
    }
}

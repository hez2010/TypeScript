using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed class PropertyInitialization(TypeContext context, Checker checker)
{
    internal static NodeList Members(SyntaxNode node) => node switch
    {
        ClassDeclarationNode declaration => declaration.Members!,
        ClassExpressionNode expression => expression.Members!,
        _ => throw new ArgumentException("Expected a class", nameof(node))
    };

    internal static ConstructorDeclarationNode? Constructor(SyntaxNode node) =>
        Members(node).OfType<ConstructorDeclarationNode>().FirstOrDefault(c => c.Body is not null);

    internal async ValueTask<Type?> InferAsync(PropertyDeclarationNode property, CancellationToken cancellation = default)
    {
        var symbol = checker.Symbols.Declaration(property)!;
        if (!SemanticSyntax.IsStatic(property))
        {
            if (Constructor(property.Parent!) is { } constructor)
                return await InferInAsync(symbol, constructor, cancellation).ConfigureAwait(false);
        }
        else
        {
            var blocks = Members(property.Parent!).OfType<ClassStaticBlockDeclarationNode>().ToArray();
            if (blocks.Length != 0)
            {
                foreach (var block in blocks)
                    if (await InferInAsync(symbol, block, cancellation).ConfigureAwait(false) is { } type)
                        return type;
                return null;
            }
        }
        return SemanticSyntax.HasModifier(property, SyntaxKind.DeclareKeyword)
            ? await BaseAsync(symbol, cancellation).ConfigureAwait(false)
            : null;
    }

    internal async ValueTask<Type?> InferInAsync(Symbol property, SyntaxNode container, CancellationToken cancellation)
    {
        SyntaxNode name = property.Name.Span.StartsWith(Symbol.InternalPrivatePrefix, StringComparison.Ordinal)
            ? new PrivateIdentifierNode { Text = property.Name[(property.Name.Span.IndexOf((byte)'@') + 1)..] } : new IdentifierNode { Text = property.Name };
        var reference = Reference(name, container);
        var type = await FlowAsync(
            reference,
            property,
            checker.Symbols.Binding(container)?.Get(container)?.ReturnFlow,
            cancellation).ConfigureAwait(false);
        if (checker.NoImplicitAny && (type == context.AutoType || type == checker.AutoArray))
            checker.ExpressionError(property.ValueDeclaration!, DiagnosticCode.Member0ImplicitlyHasAn1Type);
        bool nullable = true;
        foreach (var part in type is UnionType union ? union.Types : [type])
            if (await checker.Facts.GetAsync(part, TypeFacts.IsUndefinedOrNull, cancellation).ConfigureAwait(false) == 0)
            {
                nullable = false;
                break;
            }
        return nullable ? null : type == context.AutoType ? context.AnyType : type == checker.AutoArray ? checker.AnyArray : type;
    }

    internal async ValueTask<Type> FlowAsync(
        SyntaxNode reference,
        Symbol? property,
        FlowNode? flow = null,
        CancellationToken cancellation = default)
    {
        Type initial = context.UndefinedType;
        if (property?.ValueDeclaration is { } declaration
            && (!checker.MemberAccess.AutoTyped(property) || SemanticSyntax.HasModifier(declaration, SyntaxKind.DeclareKeyword)))
            initial = await BaseAsync(property, cancellation).ConfigureAwait(false) ?? initial;
        return await checker.FlowTypes.GetAsync(
            reference,
            context.AutoType,
            initial,
            flow: flow,
            cancellation: cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type?> BaseAsync(Symbol property, CancellationToken cancellation = default)
    {
        var declaring = checker.Symbols.Parent(property);
        if (declaring is null || (declaring.Flags & SymbolFlags.Class) == 0)
            return null;
        var type = (InterfaceType)await checker.Declared.GetAsync(
            declaring,
            cancellation).ConfigureAwait(false);
        var bases = await checker.Bases.GetAsync(type, cancellation).ConfigureAwait(false);
        return bases.Count != 0
            && await checker.Properties.PropertyAsync(
                bases[0],
                property.Name,
                cancellation: cancellation).ConfigureAwait(false) is { } inherited
            ? await checker.Values.GetAsync(inherited, cancellation).ConfigureAwait(false) : null;
    }

    internal async ValueTask<bool> AssignedAsync(SyntaxNode name, Type type, SyntaxNode container, CancellationToken cancellation = default)
    {
        if (!context.StrictNullChecks)
            throw new InvalidOperationException("Definite property assignment requires strict null checks");
        var reference = Reference(name, container);
        var initial = await checker.Algebra.UnionAsync([type, context.UndefinedType], cancellation: cancellation).ConfigureAwait(false);
        var result = await checker.FlowTypes.GetAsync(
            reference,
            type,
            initial,
            flow: checker.Symbols.Binding(container)?.Get(container)?.ReturnFlow,
            cancellation: cancellation).ConfigureAwait(false);
        return !checker.Predicates.Maybe(result, TypeFlags.Undefined, cancellation);
    }

    internal async ValueTask<bool> BeforeStaticUseAsync(
        PropertyDeclarationNode declaration,
        SyntaxNode usage,
        SyntaxNode initializer,
        CancellationToken cancellation)
    {
        var type = await checker.Values.GetAsync(checker.Symbols.Declaration(declaration)!, cancellation).ConfigureAwait(false);
        foreach (var block in Members(declaration.Parent!).OfType<ClassStaticBlockDeclarationNode>())
            if (block.Pos >= declaration.Parent!.Pos && block.Pos <= initializer.Pos
                && await AssignedAsync(declaration.Name!, type, block, cancellation).ConfigureAwait(false))
                return true;
        return false;
    }

    internal async ValueTask CheckAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        if (!context.StrictNullChecks || !checker.StrictPropertyInitialization || (node.Flags & NodeFlags.Ambient) != 0)
            return;
        var constructor = Constructor(node);
        foreach (var property in Members(node).OfType<PropertyDeclarationNode>())
        {
            if (SemanticSyntax.HasModifier(property, SyntaxKind.DeclareKeyword)
                || SemanticSyntax.IsStatic(property)
                || !AccessFlow.WithoutInitializer(property))
                continue;
            if (property.Name is not IdentifierNode and not PrivateIdentifierNode and not ComputedPropertyNameNode)
                continue;
            var type = await checker.Values.GetAsync(checker.Symbols.Declaration(property)!, cancellation).ConfigureAwait(false);
            if ((type.Flags & TypeFlags.AnyOrUnknown) == 0 && !checker.Predicates.Maybe(type, TypeFlags.Undefined, cancellation)
                && (constructor is null || !await AssignedAsync(property.Name, type, constructor, cancellation).ConfigureAwait(false)))
                checker.ExpressionError(property.Name, DiagnosticCode.Property0HasNoInitializerAndIsNotDefinitelyAssignedInTheConstructor);
        }
    }

    private static SyntaxNode Reference(SyntaxNode name, SyntaxNode container)
    {
        var receiver = new TokenNode(SyntaxKind.ThisKeyword);
        SyntaxNode reference = name is ComputedPropertyNameNode computed
            ? new ElementAccessExpressionNode { Expression = receiver, ArgumentExpression = computed.Expression, Parent = container }
            : new PropertyAccessExpressionNode { Expression = receiver, Name = name, Parent = container };
        receiver.Parent = reference;
        if (name.Parent is null)
            name.Parent = reference;
        return reference;
    }
}

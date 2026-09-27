using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private enum ThisAssignmentKind
    {
        None,
        Typed,
        Constructor,
        Method
    }

    private readonly Dictionary<Symbol, (ThisAssignmentKind Kind, SyntaxNode? Location)> thisAssignments = [];

    private (ThisAssignmentKind Kind, SyntaxNode? Location) ThisAssignment(Symbol symbol, CancellationToken cancellation)
    {
        if (symbol.ValueDeclaration is not BinaryExpressionNode)
            return default;
        if (thisAssignments.TryGetValue(symbol, out var cached))
            return cached;
        SyntaxNode? annotation = null, constructor = null;
        foreach (var declaration in symbol.Declarations)
        {
            cancellation.ThrowIfCancellationRequested();
            if (declaration is not BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.EqualsToken } binary
                || (declaration.Flags & NodeFlags.JavaScriptFile) == 0
                || !(binary.Left is PropertyAccessExpressionNode { Expression.Kind: SyntaxKind.ThisKeyword }
                    || binary.Left is ElementAccessExpressionNode
                    {
                        Expression.Kind: SyntaxKind.ThisKeyword, ArgumentExpression: StringLiteralNode
                        or NumericLiteralNode or NoSubstitutionTemplateLiteralNode
                    }))
                return thisAssignments[symbol] = default;
            if (binary.Type is not null)
                annotation = binary.Type;
            if (constructor is null && MissingNamePrefixes.ThisContainer(declaration, false, false) is ConstructorDeclarationNode found)
                constructor = found;
        }
        return thisAssignments[symbol] = annotation is not null ? (ThisAssignmentKind.Typed, annotation)
            : constructor is not null ? (ThisAssignmentKind.Constructor, constructor) : (ThisAssignmentKind.Method, null);
    }

    private async ValueTask<Type> AssignmentDeclarationTypeAsync(Symbol symbol, CancellationToken cancellation)
    {
        var (kind, location) = ThisAssignment(symbol, cancellation);
        Type? type = kind switch
        {
            ThisAssignmentKind.Typed => await Nodes.FromNodeAsync(location!, cancellation).ConfigureAwait(false),
            ThisAssignmentKind.Constructor => await PropertyInitializers.InferInAsync(
                symbol,
                location!,
                cancellation).ConfigureAwait(false),
            ThisAssignmentKind.Method => await PropertyInitializers.BaseAsync(symbol, cancellation).ConfigureAwait(false),
            _ => null
        };
        if (type is null)
        {
            var types = new List<Type>();
            for (int i = 0; i < symbol.Declarations.Count; i++)
            {
                var declaration = symbol.Declarations[i];
                if (declaration is BinaryExpressionNode { Type: { } annotation })
                {
                    type = await Nodes.FromNodeAsync(annotation, cancellation).ConfigureAwait(false);
                    break;
                }
                if (await AssignmentInitializerTypeAsync(declaration, cancellation).ConfigureAwait(false) is { } assigned
                    && !(i == 0 && symbol.Declarations.Count > 1 && (assigned.Flags & TypeFlags.Undefined) != 0
                        && declaration is BinaryExpressionNode binary && ExportsPropertyAssignment(binary.Left!))
                    && !types.Contains(assigned))
                    types.Add(assigned);
            }
            if (kind == ThisAssignmentKind.Method
                && types.Count != 0
                && context.StrictNullChecks
                && !types.Contains(context.UndefinedOrMissingType))
                types.Add(context.UndefinedOrMissingType);
            type ??= types.Count == 0 ? context.AnyType : await Algebra.UnionAsync(types, cancellation: cancellation).ConfigureAwait(false);
        }
        type = await Widening.GetAsync(type, cancellation).ConfigureAwait(false);
        if (symbol.ValueDeclaration is { } value && (value.Flags & NodeFlags.JavaScriptFile) != 0
            && (type is UnionType union ? union.Types : [type]).All(t => (t.Flags & ~TypeFlags.Nullable) == 0))
        {
            await ReportImplicitAnyAsync(value, context.AnyType, cancellation).ConfigureAwait(false);
            return context.AnyType;
        }
        return type;
    }

    private async ValueTask<Type?> AssignmentInitializerTypeAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (node is BinaryExpressionNode binary)
        {
            Type type;
            if (ExportsPropertyAssignment(binary.Left!) || (node.Flags & NodeFlags.JavaScriptFile) != 0 && ModuleExportsAccess(binary.Left))
            {
                var expression = binary.Right!;
                while (expression is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.EqualsToken } next)
                    expression = next.Right!;
                type = await Algebra.RegularTypeAsync(
                    await CachedExpressionAsync(expression, 0, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false);
            }
            else
            {
                if ((node.Flags & NodeFlags.JavaScriptFile) != 0 && FlowReferences.Receiver(binary.Left!)?.Kind == SyntaxKind.ThisKeyword)
                {
                    var pending = new Stack<SyntaxNode>();
                    pending.Push(binary.Right!);
                    while (pending.TryPop(out var current))
                    {
                        if (await FlowReferences.MatchesAsync(binary.Left!, current, cancellation).ConfigureAwait(false))
                            return null;
                        if (current is IFunctionSignature)
                            continue;
                        for (int i = current.ChildCount - 1; i >= 0; i--)
                            pending.Push(current.GetChild(i));
                    }
                }
                type = await Contexts.MutableAsync(binary.Right!, 0, cancellation).ConfigureAwait(false);
            }
            if (Instantiation.IsArrayType(type)
                && (await References.TypeArgumentsAsync(
                    (TypeReference)type,
                    cancellation).ConfigureAwait(false))[0] == (context.StrictNullChecks
                        ? context.ImplicitNeverType
                        : context.UndefinedWideningType))
            {
                var owner = program.Symbols.Declaration(node)?.Parent?.ValueDeclaration;
                var annotated = owner is FunctionExpressionNode or ArrowFunctionNode && owner.Parent is { } parent
                    && program.Symbols.Declaration(parent)?.ValueDeclaration is ITypedNode { Type: not null };
                if (!annotated)
                {
                    await ReportImplicitAnyAsync(node, AnyArray, cancellation).ConfigureAwait(false);
                    return AnyArray;
                }
            }
            return type;
        }
        if (node is CallExpressionNode call)
            return await PropertyDescriptorTypeAsync(call.Arguments![2], cancellation).ConfigureAwait(false);
        return null;
    }

    private async ValueTask<Type> PropertyDescriptorTypeAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var type = await CachedExpressionAsync(node, 0, cancellation).ConfigureAwait(false);
        if (await PropertyTypeAsync(type, "value", cancellation).ConfigureAwait(false) is { } value)
            return value;
        if (await PropertyTypeAsync(type, "get", cancellation).ConfigureAwait(false) is { } getter
            && await SignaturesAsync(getter, false, cancellation).ConfigureAwait(false) is { Count: 1 } getSignatures)
            return await Signatures.ReturnAsync(getSignatures[0], cancellation).ConfigureAwait(false);
        if (await PropertyTypeAsync(type, "set", cancellation).ConfigureAwait(false) is { } setter
            && await SignaturesAsync(setter, false, cancellation).ConfigureAwait(false) is { Count: 1 } setSignatures)
            return await Parameters.AtAsync(setSignatures[0], 0, cancellation).ConfigureAwait(false);
        return context.AnyType;
    }

    private async ValueTask<bool> ReadonlyDescriptorAsync(CallExpressionNode declaration)
    {
        var type = await CachedExpressionAsync(declaration.Arguments![2], 0, default).ConfigureAwait(false);
        if (await PropertyTypeAsync(type, "value", default).ConfigureAwait(false) is not null)
        {
            var property = await Properties.PropertyAsync(type, "writable").ConfigureAwait(false);
            if (property is null)
                return true;
            var writable = property.ValueDeclaration is PropertyAssignmentNode assigned
                ? await Expressions.CheckAsync(assigned.Initializer!).ConfigureAwait(false)
                : await Values.GetAsync(property).ConfigureAwait(false);
            return (writable.Flags & TypeFlags.BooleanLiteral) != 0 && writable is LiteralType { Value: false };
        }
        return await PropertyTypeAsync(type, "set", default).ConfigureAwait(false) is null;
    }

    private async ValueTask<Type?> PropertyTypeAsync(Type type, string name, CancellationToken cancellation)
        => await Properties.PropertyAsync(type, name, cancellation: cancellation).ConfigureAwait(false) is { } property
            ? await Values.GetAsync(property, cancellation).ConfigureAwait(false) : null;

    private async ValueTask<Type?> AssignmentContextAsync(BinaryExpressionNode binary, CancellationToken cancellation)
    {
        var left = binary.Left!;
        var target = left;
        while (target is PropertyAccessExpressionNode or ElementAccessExpressionNode)
            target = FlowReferences.Receiver(target)!;
        if (target is IdentifierNode root && (program.ReferenceSymbols.Resolve(root, cancellation).Flags & SymbolFlags.ModuleExports) != 0)
            return null;
        if (left is PropertyAccessExpressionNode or ElementAccessExpressionNode)
        {
            var expression = FlowReferences.Receiver(left)!;
            var declaration = program.Symbols.Declaration(binary);
            if (expression is IdentifierNode identifier)
            {
                var owner = program.Symbols.ExportedValue(program.ReferenceSymbols.Resolve(identifier, cancellation))!;
                if ((owner.Flags & SymbolFlags.ModuleExports) != 0)
                    return null;
                if (declaration is not null)
                {
                    if (owner.ValueDeclaration is VariableDeclarationNode { Type: { } annotation })
                    {
                        var type = await Nodes.FromNodeAsync(annotation, cancellation).ConfigureAwait(false);
                        if (left is PropertyAccessExpressionNode property)
                            return await ContextualPropertyAsync(
                                type,
                                SyntaxNameText.Get(property.Name),
                                cancellation).ConfigureAwait(false);
                        var key = await CachedExpressionAsync(
                            ((ElementAccessExpressionNode)left).ArgumentExpression!,
                            0,
                            cancellation).ConfigureAwait(false);
                        if ((key.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0)
                            return await ContextualPropertyAsync(type, MappedMembers.PropertyName(key), cancellation).ConfigureAwait(false);
                        return await ExpressionAsync(left, cancellation).ConfigureAwait(false);
                    }
                    return null;
                }
            }
            else if (expression is PropertyAccessExpressionNode or ElementAccessExpressionNode && declaration is not null)
                return null;
            else if (expression.Kind == SyntaxKind.ThisKeyword)
            {
                var type = await ExpressionAsync(expression, cancellation).ConfigureAwait(false);
                string? name = left is PropertyAccessExpressionNode access
                    ? access.Name is PrivateIdentifierNode privateName && type.Symbol is { } symbol
                        ? PrivateAccess.Name(symbol, privateName.Text)
                        : SyntaxNameText.Get(access.Name)
                    : null;
                if (left is ElementAccessExpressionNode element)
                {
                    var key = await CachedExpressionAsync(element.ArgumentExpression!, 0, cancellation).ConfigureAwait(false);
                    if ((key.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0)
                        name = MappedMembers.PropertyName(key);
                }
                var property = name is null
                    ? null
                    : await Properties.PropertyAsync(type, name, cancellation: cancellation).ConfigureAwait(false);
                if (property?.ValueDeclaration is PropertyDeclarationNode { Type: null, Initializer: null }
                    or PropertySignatureDeclarationNode { Type: null })
                    return null;
                if (declaration?.ValueDeclaration is ITypedNode { Type: null })
                    return null;
            }
        }
        return await ExpressionAsync(left, cancellation).ConfigureAwait(false);
    }

    private static bool ModuleExportsAccess(SyntaxNode? node) => node is
        PropertyAccessExpressionNode { Expression: IdentifierNode { Text: "module" }, Name: IdentifierNode { Text: "exports" } }
        or ElementAccessExpressionNode
        {
            Expression: IdentifierNode { Text: "module" }, ArgumentExpression: StringLiteralNode { Text: "exports" }
            or NoSubstitutionTemplateLiteralNode { Text: "exports" }
        };

    private static bool BindableStaticName(SyntaxNode? node, bool excludeThis)
    {
        while (node is PropertyAccessExpressionNode or ElementAccessExpressionNode)
        {
            var receiver = FlowReferences.Receiver(node)!;
            if (node is PropertyAccessExpressionNode property)
            {
                if (!excludeThis && receiver.Kind == SyntaxKind.ThisKeyword)
                    return true;
                if (property.Name is not IdentifierNode)
                    return false;
            }
            else
            {
                if (((ElementAccessExpressionNode)node).ArgumentExpression is not (StringLiteralNode or NumericLiteralNode
                    or NoSubstitutionTemplateLiteralNode))
                    return false;
                if (!excludeThis && receiver.Kind == SyntaxKind.ThisKeyword)
                    return true;
            }
            node = receiver;
            excludeThis = true;
        }
        return node is IdentifierNode;
    }

    private async ValueTask CheckDocumentationBaseAsync(
        SyntaxNode node,
        ExpressionWithTypeArgumentsNode baseNode,
        Type baseType,
        CancellationToken cancellation)
    {
        if ((node.Flags & NodeFlags.JavaScriptFile) == 0)
            return;
        foreach (var comment in await SemanticSyntax.Source(node)!.GetDocumentationAsync(node, cancellation).ConfigureAwait(false))
            if (comment.Tags is { } tags)
                foreach (var tag in tags.OfType<JSDocAugmentsTagNode>())
                {
                    var annotated = tag.ClassName!;
                    if (await IdenticalAsync(
                        await Nodes.FromNodeAsync(annotated, cancellation).ConfigureAwait(false),
                        baseType,
                        cancellation).ConfigureAwait(false))
                        continue;
                    static IdentifierNode? Name(SyntaxNode? value) => value switch
                    {
                        IdentifierNode name => name,
                        PropertyAccessExpressionNode { Name: IdentifierNode name } => name,
                        _ => null
                    };
                    if (Name(baseNode.Expression) is { } baseName && Name(annotated.Expression) is { } name)
                        Error(name, 8023, tag.TagName!.Text, name.Text, baseName.Text);
                }
    }
}

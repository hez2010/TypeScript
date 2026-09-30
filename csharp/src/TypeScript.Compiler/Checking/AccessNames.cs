using TypeScript.Compiler.Text;
using System.Globalization;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Semantics;

namespace TypeScript.Compiler.Checking;

internal interface IAccessNameHost
{
    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> ExpressionAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> LiteralNameTypeAsync(SyntaxNode node, CancellationToken cancellation);
}

internal sealed class AccessNames(EntityNames names, DeclarationOrder order, IAccessNameHost host)
{
    internal async ValueTask<Utf8String?> GetAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (node is PropertyAccessExpressionNode property)
            return Syntax.SyntaxNameText.Get(property.Name);
        if (node is ElementAccessExpressionNode element)
        {
            var argument = element.ArgumentExpression!;
            if (LiteralName(argument) is { } literal)
                return literal;
            return ConstantEvaluator.EntityName(argument) ? await EntityAsync(argument, cancellation).ConfigureAwait(false) : null;
        }
        if (node is ParameterDeclarationNode parameter)
            return Utf8String.Format(((IFunctionSignature)parameter.Parent!).Parameters!.IndexOf(parameter));
        SyntaxNode? name = node switch
        {
            BindingElementNode binding when binding.Parent!.Kind == Syntax.SyntaxKind.ObjectBindingPattern => binding.PropertyName ?? binding.Name,
            PropertyAssignmentNode assignment => assignment.Name,
            ShorthandPropertyAssignmentNode shorthand => shorthand.Name,
            _ => null
        };
        if (name is not null)
        {
            var type = await host.LiteralNameTypeAsync(name, cancellation).ConfigureAwait(false);
            return (type.Flags & TypeFlags.StringOrNumberLiteral) != 0 ? MappedMembers.PropertyName(type) : (Utf8String?)null;
        }
        var elements = node.Parent switch
        {
            ArrayLiteralExpressionNode array => array.Elements,
            BindingPatternNode pattern when pattern.Kind == Syntax.SyntaxKind.ArrayBindingPattern => pattern.Elements,
            _ => null
        };
        return elements is null ? (Utf8String?)null : Utf8String.Format(elements.IndexOf(node));
    }

    private async ValueTask<Utf8String?> EntityAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var symbol = await names.ResolveAsync(node, SymbolFlags.Value, true, cancellation: cancellation).ConfigureAwait(false);
        if (symbol is null || !(AssignmentMarks.Constant(symbol) || (symbol.Flags & SymbolFlags.EnumMember) != 0)
            || symbol.ValueDeclaration is not { } declaration)
            return null;
        if (declaration is ITypedNode { Type: { } annotation }
            && NameFromType(await host.TypeFromNodeAsync(annotation, cancellation).ConfigureAwait(false)) is { } name)
            return name;
        if (declaration is VariableDeclarationNode or ParameterDeclarationNode or PropertyDeclarationNode or PropertyAssignmentNode
            or EnumMemberNode
            && await order.BeforeUseAsync(declaration, node, cancellation).ConfigureAwait(false))
        {
            if (declaration is IInitializedNode { Initializer: { } initializer })
                return NameFromType(await host.ExpressionAsync(initializer, cancellation).ConfigureAwait(false));
            if (declaration is EnumMemberNode member)
                return LiteralName(member.Name!) ?? (member.Name as IdentifierNode)?.Text;
        }
        return null;
    }

    private static Utf8String? NameFromType(Type type) =>
        (type.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0 ? MappedMembers.PropertyName(type) : (Utf8String?)null;

    private static Utf8String? LiteralName(SyntaxNode node) => node switch
    {
        StringLiteralNode value => value.Text,
        NumericLiteralNode value => value.Text,
        NoSubstitutionTemplateLiteralNode value => value.Text,
        _ => (Utf8String?)null
    };
}

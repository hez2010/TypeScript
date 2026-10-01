using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal static class DeclarationDiagnostics
{
    internal static SyntaxNode? Name(SyntaxNode node) => SemanticSyntax.Name(node) ?? node switch
    {
        BinaryExpressionNode { Left: PropertyAccessExpressionNode property } => property.Name,
        BinaryExpressionNode { Left: ElementAccessExpressionNode element } => element.ArgumentExpression,
        ElementAccessExpressionNode element => element.ArgumentExpression,
        CallExpressionNode { Arguments: { Count: > 1 } arguments } => arguments[1],
        _ => null
    };
    internal sealed record Info(SyntaxNode? Node, DiagnosticMessage Message, SyntaxNode? Name = null);

    private static bool Static(SyntaxNode node) => SemanticSyntax.HasModifier(node, K.StaticKeyword);
    private static bool Private(SyntaxNode node) => SemanticSyntax.HasModifier(node, K.PrivateKeyword);
    private static DiagnosticMessage Select(SymbolAccessibilityResult result, DiagnosticMessage external,
        DiagnosticMessage module, DiagnosticMessage local) => result.ErrorModuleName.Length == 0 ? local
            : result.Accessibility == SymbolAccessibility.CannotBeNamed ? external : module;
    private static DiagnosticMessage SelectNamed(SymbolAccessibilityResult result, DiagnosticMessage module,
        DiagnosticMessage local) => result.ErrorModuleName.Length == 0 ? local : module;

    internal static Info? ForNode(SyntaxNode node, SymbolAccessibilityResult result, bool nameOnly = false)
    {
        var name = Name(node);
        DiagnosticMessage? message;
        if (nameOnly && node is GetAccessorDeclarationNode or SetAccessorDeclarationNode)
            return new(node, AccessorName(node, result)!, name);
        if (nameOnly && node is MethodDeclarationNode or MethodSignatureDeclarationNode)
            return new(node, MethodName(node, result)!, name);
        switch (node)
        {
            case VariableDeclarationNode or PropertyDeclarationNode or PropertySignatureDeclarationNode or BindingElementNode
                or PropertyAccessExpressionNode or ElementAccessExpressionNode or BinaryExpressionNode or ConstructorDeclarationNode:
                message = VariableType(node, result);
                return message is null ? null : new(node, message, name);
            case GetAccessorDeclarationNode or SetAccessorDeclarationNode:
                return new(name, AccessorType(node, result)!, name);
            case ConstructSignatureDeclarationNode or CallSignatureDeclarationNode or MethodDeclarationNode
                or MethodSignatureDeclarationNode or FunctionDeclarationNode or IndexSignatureDeclarationNode:
                return new(name ?? node, ReturnType(node, result)!);
            case ParameterDeclarationNode:
                message = node.Parent is ConstructorDeclarationNode && Private(node.Parent)
                    && node is IModifiedNode { Modifiers: { } modifiers } && modifiers.Any(m => m.Kind is K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.ReadonlyKeyword)
                    ? VariableType(node, result) : ParameterType(node, result);
                return message is null ? null : new(node, message, name);
            case TypeParameterDeclarationNode:
                return new(node, Constraint(node, result)!, name);
            case ExpressionWithTypeArgumentsNode:
                var owner = node.Parent!.Parent!;
                name = SemanticSyntax.Name(owner);
                message = owner is ClassDeclarationNode
                    ? node.Parent is HeritageClauseNode { Token: K.ImplementsKeyword }
                        ? Messages.Implements_clause_of_exported_class_0_has_or_is_using_private_name_1
                        : name is null ? Messages.X_extends_clause_of_exported_class_has_or_is_using_private_name_0
                            : Messages.X_extends_clause_of_exported_class_0_has_or_is_using_private_name_1
                    : Messages.X_extends_clause_of_exported_interface_0_has_or_is_using_private_name_1;
                return new(node, message, name);
            case ImportEqualsDeclarationNode:
                return new(node, Messages.Import_declaration_0_is_using_private_name_1, name);
            case TypeAliasDeclarationNode alias:
                return new(alias.Type, SelectNamed(result,
                    Messages.Exported_type_alias_0_has_or_is_using_private_name_1_from_module_2,
                    Messages.Exported_type_alias_0_has_or_is_using_private_name_1), name);
            case CallExpressionNode { Arguments: { Count: > 1 } arguments }:
                return new(arguments[1], Select(result,
                    Messages.Exported_variable_0_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                    Messages.Exported_variable_0_has_or_is_using_name_1_from_private_module_2,
                    Messages.Exported_variable_0_has_or_is_using_private_name_1), arguments[1]);
            default:
                throw new InvalidOperationException($"No declaration diagnostic context for {node.Kind}");
        }
    }

    private static DiagnosticMessage? AccessorName(SyntaxNode node, SymbolAccessibilityResult result)
    {
        if (Static(node))
        {
            return Select(
                result,
                Messages.Public_static_property_0_of_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                Messages.Public_static_property_0_of_exported_class_has_or_is_using_name_1_from_private_module_2,
                Messages.Public_static_property_0_of_exported_class_has_or_is_using_private_name_1);
        }
        else if (node.Parent!.Kind == K.ClassDeclaration)
        {
            return Select(
                result,
                Messages.Public_property_0_of_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                Messages.Public_property_0_of_exported_class_has_or_is_using_name_1_from_private_module_2,
                Messages.Public_property_0_of_exported_class_has_or_is_using_private_name_1);
        }
        else
        {
            return SelectNamed(
                result,
                Messages.Property_0_of_exported_interface_has_or_is_using_name_1_from_private_module_2,
                Messages.Property_0_of_exported_interface_has_or_is_using_private_name_1);
        }
    }

    private static DiagnosticMessage? MethodName(SyntaxNode node, SymbolAccessibilityResult result)
    {
        if (Static(node))
        {
            return Select(
                result,
                Messages.Public_static_method_0_of_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                Messages.Public_static_method_0_of_exported_class_has_or_is_using_name_1_from_private_module_2,
                Messages.Public_static_method_0_of_exported_class_has_or_is_using_private_name_1);
        }
        else if (node.Parent!.Kind == K.ClassDeclaration)
        {
            return Select(
                result,
                Messages.Public_method_0_of_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                Messages.Public_method_0_of_exported_class_has_or_is_using_name_1_from_private_module_2,
                Messages.Public_method_0_of_exported_class_has_or_is_using_private_name_1);
        }
        else
        {
            return SelectNamed(
                result,
                Messages.Method_0_of_exported_interface_has_or_is_using_name_1_from_private_module_2,
                Messages.Method_0_of_exported_interface_has_or_is_using_private_name_1);
        }
    }

    private static DiagnosticMessage? VariableType(SyntaxNode node, SymbolAccessibilityResult result)
    {
        if (node.Kind == K.VariableDeclaration || node.Kind == K.BindingElement)
        {
            return Select(
                result,
                Messages.Exported_variable_0_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                Messages.Exported_variable_0_has_or_is_using_name_1_from_private_module_2,
                Messages.Exported_variable_0_has_or_is_using_private_name_1);

        }

        else if (node.Kind == K.PropertyDeclaration || node.Kind == K.PropertyAccessExpression || node.Kind == K.ElementAccessExpression || node.Kind == K.BinaryExpression || node.Kind == K.PropertySignature || (node.Kind == K.Parameter && Private(node.Parent!)))

        {

            if (Static(node))

            {
                return Select(
                    result,
                    Messages.Public_static_property_0_of_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                    Messages.Public_static_property_0_of_exported_class_has_or_is_using_name_1_from_private_module_2,
                    Messages.Public_static_property_0_of_exported_class_has_or_is_using_private_name_1);
            }
            else if (node.Parent!.Kind == K.ClassDeclaration || node.Kind == K.Parameter)
            {
                return Select(
                    result,
                    Messages.Public_property_0_of_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                    Messages.Public_property_0_of_exported_class_has_or_is_using_name_1_from_private_module_2,
                    Messages.Public_property_0_of_exported_class_has_or_is_using_private_name_1);
            }
            else
            {

                return SelectNamed(
                    result,
                    Messages.Property_0_of_exported_interface_has_or_is_using_name_1_from_private_module_2,
                    Messages.Property_0_of_exported_interface_has_or_is_using_private_name_1);
            }
        }
        return null;
    }

    private static DiagnosticMessage? AccessorType(SyntaxNode node, SymbolAccessibilityResult result)
    {
        if (node.Kind == K.SetAccessor)
        {

            if (Static(node))

            {
                return SelectNamed(
                    result,
                    Messages.Parameter_type_of_public_static_setter_0_from_exported_class_has_or_is_using_name_1_from_private_module_2,
                    Messages.Parameter_type_of_public_static_setter_0_from_exported_class_has_or_is_using_private_name_1);
            }
            else
            {
                return SelectNamed(
                    result,
                    Messages.Parameter_type_of_public_setter_0_from_exported_class_has_or_is_using_name_1_from_private_module_2,
                    Messages.Parameter_type_of_public_setter_0_from_exported_class_has_or_is_using_private_name_1);
            }
        }
        else
        {
            if (Static(node))
            {
                return Select(
                    result,
                    Messages.Return_type_of_public_static_getter_0_from_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                    Messages.Return_type_of_public_static_getter_0_from_exported_class_has_or_is_using_name_1_from_private_module_2,
                    Messages.Return_type_of_public_static_getter_0_from_exported_class_has_or_is_using_private_name_1);
            }
            else
            {
                return Select(
                    result,
                    Messages.Return_type_of_public_getter_0_from_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                    Messages.Return_type_of_public_getter_0_from_exported_class_has_or_is_using_name_1_from_private_module_2,
                    Messages.Return_type_of_public_getter_0_from_exported_class_has_or_is_using_private_name_1);
            }
        }
    }

    private static DiagnosticMessage? ReturnType(SyntaxNode node, SymbolAccessibilityResult result)
    {
        switch (node.Kind)
        {
        case K.ConstructSignature:

            return SelectNamed(
                result,
                Messages.Return_type_of_constructor_signature_from_exported_interface_has_or_is_using_name_0_from_private_module_1,
                Messages.Return_type_of_constructor_signature_from_exported_interface_has_or_is_using_private_name_0);
        case K.CallSignature:

            return SelectNamed(
                result,
                Messages.Return_type_of_call_signature_from_exported_interface_has_or_is_using_name_0_from_private_module_1,
                Messages.Return_type_of_call_signature_from_exported_interface_has_or_is_using_private_name_0);
        case K.IndexSignature:

            return SelectNamed(
                result,
                Messages.Return_type_of_index_signature_from_exported_interface_has_or_is_using_name_0_from_private_module_1,
                Messages.Return_type_of_index_signature_from_exported_interface_has_or_is_using_private_name_0);

        case K.MethodDeclaration:
        case K.MethodSignature:
            if (Static(node))
            {
                return Select(
                    result,
                    Messages.Return_type_of_public_static_method_from_exported_class_has_or_is_using_name_0_from_external_module_1_but_cannot_be_named,
                    Messages.Return_type_of_public_static_method_from_exported_class_has_or_is_using_name_0_from_private_module_1,
                    Messages.Return_type_of_public_static_method_from_exported_class_has_or_is_using_private_name_0);
            }
            else if (node.Parent!.Kind == K.ClassDeclaration)
            {
                return Select(
                    result,
                    Messages.Return_type_of_public_method_from_exported_class_has_or_is_using_name_0_from_external_module_1_but_cannot_be_named,
                    Messages.Return_type_of_public_method_from_exported_class_has_or_is_using_name_0_from_private_module_1,
                    Messages.Return_type_of_public_method_from_exported_class_has_or_is_using_private_name_0);
            }
            else
            {

                return SelectNamed(
                    result,
                    Messages.Return_type_of_method_from_exported_interface_has_or_is_using_name_0_from_private_module_1,
                    Messages.Return_type_of_method_from_exported_interface_has_or_is_using_private_name_0);
            }
        case K.FunctionDeclaration:
            return Select(
                result,
                Messages.Return_type_of_exported_function_has_or_is_using_name_0_from_external_module_1_but_cannot_be_named,
                Messages.Return_type_of_exported_function_has_or_is_using_name_0_from_private_module_1,
                Messages.Return_type_of_exported_function_has_or_is_using_private_name_0);
        default:
            throw new InvalidOperationException("Unexpected declaration kind");
        }
    }

    private static DiagnosticMessage? ParameterType(SyntaxNode node, SymbolAccessibilityResult result)
    {
        switch (node.Parent!.Kind)
        {
        case K.Constructor:
            return Select(
                result,
                Messages.Parameter_0_of_constructor_from_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                Messages.Parameter_0_of_constructor_from_exported_class_has_or_is_using_name_1_from_private_module_2,
                Messages.Parameter_0_of_constructor_from_exported_class_has_or_is_using_private_name_1);

        case K.ConstructSignature:
        case K.ConstructorType:

            return SelectNamed(
                result,
                Messages.Parameter_0_of_constructor_signature_from_exported_interface_has_or_is_using_name_1_from_private_module_2,
                Messages.Parameter_0_of_constructor_signature_from_exported_interface_has_or_is_using_private_name_1);

        case K.CallSignature:

            return SelectNamed(
                result,
                Messages.Parameter_0_of_call_signature_from_exported_interface_has_or_is_using_name_1_from_private_module_2,
                Messages.Parameter_0_of_call_signature_from_exported_interface_has_or_is_using_private_name_1);

        case K.IndexSignature:

            return SelectNamed(
                result,
                Messages.Parameter_0_of_index_signature_from_exported_interface_has_or_is_using_name_1_from_private_module_2,
                Messages.Parameter_0_of_index_signature_from_exported_interface_has_or_is_using_private_name_1);

        case K.MethodDeclaration:
        case K.MethodSignature:
            if (Static(node.Parent!))
            {
                return Select(
                    result,
                    Messages.Parameter_0_of_public_static_method_from_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                    Messages.Parameter_0_of_public_static_method_from_exported_class_has_or_is_using_name_1_from_private_module_2,
                    Messages.Parameter_0_of_public_static_method_from_exported_class_has_or_is_using_private_name_1);
            }
            else if (node.Parent!.Parent!.Kind == K.ClassDeclaration)
            {
                return Select(
                    result,
                    Messages.Parameter_0_of_public_method_from_exported_class_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                    Messages.Parameter_0_of_public_method_from_exported_class_has_or_is_using_name_1_from_private_module_2,
                    Messages.Parameter_0_of_public_method_from_exported_class_has_or_is_using_private_name_1);
            }
            else
            {

                return SelectNamed(
                    result,
                    Messages.Parameter_0_of_method_from_exported_interface_has_or_is_using_name_1_from_private_module_2,
                    Messages.Parameter_0_of_method_from_exported_interface_has_or_is_using_private_name_1);
            }

        case K.FunctionDeclaration:
        case K.FunctionType:
        case K.ArrowFunction:
        case K.FunctionExpression:
            return Select(
                result,
                Messages.Parameter_0_of_exported_function_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                Messages.Parameter_0_of_exported_function_has_or_is_using_name_1_from_private_module_2,
                Messages.Parameter_0_of_exported_function_has_or_is_using_private_name_1);
        case K.SetAccessor:
        case K.GetAccessor:
            return Select(
                result,
                Messages.Parameter_0_of_accessor_has_or_is_using_name_1_from_external_module_2_but_cannot_be_named,
                Messages.Parameter_0_of_accessor_has_or_is_using_name_1_from_private_module_2,
                Messages.Parameter_0_of_accessor_has_or_is_using_private_name_1);

        default:
            throw new InvalidOperationException("Unexpected declaration kind");
        }
    }

    private static DiagnosticMessage? Constraint(SyntaxNode node, SymbolAccessibilityResult result)
    {
        switch (node.Parent!.Kind)
        {
        case K.ClassDeclaration:
            return Messages.Type_parameter_0_of_exported_class_has_or_is_using_private_name_1;
        case K.InterfaceDeclaration:
            return Messages.Type_parameter_0_of_exported_interface_has_or_is_using_private_name_1;
        case K.MappedType:
            return Messages.Type_parameter_0_of_exported_mapped_object_type_is_using_private_name_1;
        case K.ConstructorType:
        case K.ConstructSignature:
            return Messages.Type_parameter_0_of_constructor_signature_from_exported_interface_has_or_is_using_private_name_1;
        case K.CallSignature:
            return Messages.Type_parameter_0_of_call_signature_from_exported_interface_has_or_is_using_private_name_1;
        case K.MethodDeclaration:
        case K.MethodSignature:
            if (Static(node.Parent!))
            {
                return Messages.Type_parameter_0_of_public_static_method_from_exported_class_has_or_is_using_private_name_1;
            }
            else if (node.Parent!.Parent!.Kind == K.ClassDeclaration)
            {
                return Messages.Type_parameter_0_of_public_method_from_exported_class_has_or_is_using_private_name_1;
            }
            else
            {
                return Messages.Type_parameter_0_of_method_from_exported_interface_has_or_is_using_private_name_1;
            }
        case K.FunctionType:
        case K.FunctionDeclaration:
            return Messages.Type_parameter_0_of_exported_function_has_or_is_using_private_name_1;

        case K.InferType:
            return Messages.Extends_clause_for_inferred_type_0_has_or_is_using_private_name_1;

        case K.TypeAliasDeclaration:
        case K.JSTypeAliasDeclaration:
            return Messages.Type_parameter_0_of_exported_type_alias_has_or_is_using_private_name_1;

        default:
            throw new InvalidOperationException("Unexpected declaration kind");
        }
    }
}

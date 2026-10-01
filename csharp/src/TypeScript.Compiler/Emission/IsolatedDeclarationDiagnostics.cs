using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal static class IsolatedDeclarationDiagnostics
{
    internal static async ValueTask<Diagnostic> CreateAsync(SyntaxNode node, Checker checker, CancellationToken cancellation)
    {
        for (var ancestor = node; ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor is HeritageClauseNode)
                return Create(node, Messages.Extends_clause_can_t_contain_an_expression_with_isolatedDeclarations);
        if (QuerySyntax.PartOfType(node) || node is TypeQueryNode || ConstantEvaluator.EntityName(node))
            return ParentSuggestion(node, Create(node, Messages.Type_containing_private_name_0_can_t_be_used_with_isolatedDeclarations, Text(node)));
        switch (node)
        {
            case GetAccessorDeclarationNode or SetAccessorDeclarationNode:
                return Accessor(node, checker);
            case ComputedPropertyNameNode or ShorthandPropertyAssignmentNode or SpreadAssignmentNode
                or ArrayLiteralExpressionNode or SpreadElementNode:
                return ParentSuggestion(node, Create(node, Error(node.Kind)!));
            case MethodDeclarationNode or ConstructSignatureDeclarationNode or FunctionExpressionNode or ArrowFunctionNode or FunctionDeclarationNode:
                return Related(ParentSuggestion(node, Create(node, Error(node.Kind)!)), Create(node, Suggestion(node.Kind)!));
            case BindingElementNode:
                return Create(node, Messages.Binding_elements_with_initializers_can_t_be_exported_directly_with_isolatedDeclarations);
            case PropertyDeclarationNode or VariableDeclarationNode:
                return Related(Create(node, Error(node.Kind)!), Create(node, Suggestion(node.Kind)!, Text(SemanticSyntax.Name(node))));
            case ParameterDeclarationNode parameter:
                if (parameter.Parent is SetAccessorDeclarationNode setter)
                    return Accessor(setter, checker);
                bool undefined = await checker.RequiresImplicitUndefinedForEmitAsync(node, null, null, cancellation);
                if (!undefined && parameter.Initializer is { } initializer)
                    return Expression(initializer);
                return Related(Create(node, undefined
                    ? Messages.Declaration_emit_for_this_parameter_requires_implicitly_adding_undefined_to_its_type_This_is_not_supported_with_isolatedDeclarations
                    : Error(node.Kind)!), Create(node, Suggestion(node.Kind)!, Text(parameter.Name)));
            case PropertyAssignmentNode property:
                return Expression(property.Initializer!);
            case ClassExpressionNode:
                return Expression(node, Messages.Inference_from_class_expressions_is_not_supported_with_isolatedDeclarations);
            default:
                return Expression(node);
        }
    }

    private static Diagnostic Accessor(SyntaxNode node, Checker checker)
    {
        var declarations = checker.Symbols.Declaration(node)?.Declarations ?? [];
        var target = node is SetAccessorDeclarationNode { Parameters: { Count: > 0 } parameters } ? parameters[0] : node;
        var result = Create(target, Error(node.Kind)!);
        if (declarations.OfType<SetAccessorDeclarationNode>().FirstOrDefault() is { } setter)
            result = Related(result, Create(setter, Suggestion(setter.Kind)!));
        if (declarations.OfType<GetAccessorDeclarationNode>().FirstOrDefault() is { } getter)
            result = Related(result, Create(getter, Suggestion(getter.Kind)!));
        return result;
    }

    private static SyntaxNode? NearestDeclaration(SyntaxNode node)
    {
        SyntaxNode? current = node;
        while (current is not null && current is not (ExportAssignmentNode or VariableDeclarationNode or PropertyDeclarationNode or ParameterDeclarationNode)
            && !TypeNodeFlow.Statement(current))
            current = current.Parent;
        if (current is ExportAssignmentNode)
            return current;
        if (current is ReturnStatementNode)
        {
            while (current is not null && !(SemanticSyntax.FunctionDeclarationLike(current) && current is not ConstructorDeclarationNode))
                current = current.Parent;
            return current;
        }
        return current is not null && !TypeNodeFlow.Statement(current) ? current : null;
    }

    private static Diagnostic ParentSuggestion(SyntaxNode node, Diagnostic diagnostic)
    {
        var parent = NearestDeclaration(node);
        return parent is null ? diagnostic : Related(diagnostic, Create(parent, Suggestion(parent.Kind)!,
            parent is ExportAssignmentNode ? Utf8String.Empty : Text(SemanticSyntax.Name(parent))));
    }

    private static Diagnostic Expression(SyntaxNode node, DiagnosticMessage? message = null)
    {
        var declaration = NearestDeclaration(node);
        if (declaration is null)
            return Create(node, message ?? Messages.Expression_type_can_t_be_inferred_with_isolatedDeclarations);
        var parent = node.Parent;
        while (parent is ParenthesizedExpressionNode or AsExpressionNode or TypeAssertionNode)
            parent = parent.Parent;
        if (parent is not ExportAssignmentNode && parent is not null && TypeNodeFlow.Statement(parent))
            parent = null;
        bool direct = declaration == parent;
        var result = Create(node, message ?? (direct ? Error(declaration.Kind)! : Messages.Expression_type_can_t_be_inferred_with_isolatedDeclarations));
        result = Related(result, Create(declaration, Suggestion(declaration.Kind)!,
            declaration is ExportAssignmentNode ? Utf8String.Empty : Text(SemanticSyntax.Name(declaration))));
        return direct ? result : Related(result, Create(node,
            Messages.Add_satisfies_and_a_type_assertion_to_this_expression_satisfies_T_as_T_to_make_the_type_explicit));
    }

    private static Utf8String Text(SyntaxNode? node) => node is null ? Utf8String.Empty : CheckerDiagnostic.DeclarationName(node);
    private static Diagnostic Create(SyntaxNode node, DiagnosticMessage message, params Utf8String[] arguments) =>
        CheckerDiagnostic.Create(node, message, arguments);
    private static Diagnostic Related(Diagnostic diagnostic, Diagnostic related) =>
        diagnostic with { RelatedInformation = [.. diagnostic.RelatedInformation, related] };

    private static DiagnosticMessage? Suggestion(SyntaxKind kind) => kind switch
    {
        K.ArrowFunction => Messages.Add_a_return_type_to_the_function_expression,
        K.FunctionExpression => Messages.Add_a_return_type_to_the_function_expression,
        K.MethodDeclaration => Messages.Add_a_return_type_to_the_method,
        K.GetAccessor => Messages.Add_a_return_type_to_the_get_accessor_declaration,
        K.SetAccessor => Messages.Add_a_type_to_parameter_of_the_set_accessor_declaration,
        K.FunctionDeclaration => Messages.Add_a_return_type_to_the_function_declaration,
        K.ConstructSignature => Messages.Add_a_return_type_to_the_function_declaration,
        K.Parameter => Messages.Add_a_type_annotation_to_the_parameter_0,
        K.VariableDeclaration => Messages.Add_a_type_annotation_to_the_variable_0,
        K.PropertyDeclaration => Messages.Add_a_type_annotation_to_the_property_0,
        K.PropertySignature => Messages.Add_a_type_annotation_to_the_property_0,
        K.ExportAssignment => Messages.Move_the_expression_in_default_export_to_a_variable_and_add_a_type_annotation_to_it,
        _ => null
    };

    private static DiagnosticMessage? Error(SyntaxKind kind) => kind switch
    {
        K.FunctionExpression => Messages.Function_must_have_an_explicit_return_type_annotation_with_isolatedDeclarations,
        K.FunctionDeclaration => Messages.Function_must_have_an_explicit_return_type_annotation_with_isolatedDeclarations,
        K.ArrowFunction => Messages.Function_must_have_an_explicit_return_type_annotation_with_isolatedDeclarations,
        K.MethodDeclaration => Messages.Method_must_have_an_explicit_return_type_annotation_with_isolatedDeclarations,
        K.ConstructSignature => Messages.Method_must_have_an_explicit_return_type_annotation_with_isolatedDeclarations,
        K.GetAccessor => Messages.At_least_one_accessor_must_have_an_explicit_type_annotation_with_isolatedDeclarations,
        K.SetAccessor => Messages.At_least_one_accessor_must_have_an_explicit_type_annotation_with_isolatedDeclarations,
        K.Parameter => Messages.Parameter_must_have_an_explicit_type_annotation_with_isolatedDeclarations,
        K.VariableDeclaration => Messages.Variable_must_have_an_explicit_type_annotation_with_isolatedDeclarations,
        K.PropertyDeclaration => Messages.Property_must_have_an_explicit_type_annotation_with_isolatedDeclarations,
        K.PropertySignature => Messages.Property_must_have_an_explicit_type_annotation_with_isolatedDeclarations,
        K.ComputedPropertyName => Messages.Computed_property_names_on_class_or_object_literals_cannot_be_inferred_with_isolatedDeclarations,
        K.SpreadAssignment => Messages.Objects_that_contain_spread_assignments_can_t_be_inferred_with_isolatedDeclarations,
        K.ShorthandPropertyAssignment => Messages.Objects_that_contain_shorthand_properties_can_t_be_inferred_with_isolatedDeclarations,
        K.ArrayLiteralExpression => Messages.Only_const_arrays_can_be_inferred_with_isolatedDeclarations,
        K.ExportAssignment => Messages.Default_exports_can_t_be_inferred_with_isolatedDeclarations,
        K.SpreadElement => Messages.Arrays_with_spread_elements_can_t_inferred_with_isolatedDeclarations,
        _ => null
    };
}

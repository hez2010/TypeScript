using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.LanguageServices;

internal sealed partial class CodeFixQuery
{
    private static readonly int[] IsolatedErrorCodes = [
        (int)Messages.Function_must_have_an_explicit_return_type_annotation_with_isolatedDeclarations.Code,
        (int)Messages.Method_must_have_an_explicit_return_type_annotation_with_isolatedDeclarations.Code,
        (int)Messages.At_least_one_accessor_must_have_an_explicit_type_annotation_with_isolatedDeclarations.Code,
        (int)Messages.Variable_must_have_an_explicit_type_annotation_with_isolatedDeclarations.Code,
        (int)Messages.Parameter_must_have_an_explicit_type_annotation_with_isolatedDeclarations.Code,
        (int)Messages.Property_must_have_an_explicit_type_annotation_with_isolatedDeclarations.Code,
        (int)Messages.Expression_type_can_t_be_inferred_with_isolatedDeclarations.Code,
        (int)Messages.Binding_elements_with_initializers_can_t_be_exported_directly_with_isolatedDeclarations.Code,
        (int)Messages.Computed_property_names_on_class_or_object_literals_cannot_be_inferred_with_isolatedDeclarations.Code,
        (int)Messages.Computed_properties_must_be_number_or_string_literals_variables_or_dotted_expressions_with_isolatedDeclarations.Code,
        (int)Messages.Enum_member_initializers_must_be_computable_without_references_to_external_symbols_with_isolatedDeclarations.Code,
        (int)Messages.Extends_clause_can_t_contain_an_expression_with_isolatedDeclarations.Code,
        (int)Messages.Objects_that_contain_shorthand_properties_can_t_be_inferred_with_isolatedDeclarations.Code,
        (int)Messages.Objects_that_contain_spread_assignments_can_t_be_inferred_with_isolatedDeclarations.Code,
        (int)Messages.Arrays_with_spread_elements_can_t_inferred_with_isolatedDeclarations.Code,
        (int)Messages.Default_exports_can_t_be_inferred_with_isolatedDeclarations.Code,
        (int)Messages.Only_const_arrays_can_be_inferred_with_isolatedDeclarations.Code,
        (int)Messages.Assigning_properties_to_functions_without_declaring_them_is_not_supported_with_isolatedDeclarations_Add_an_explicit_declaration_for_the_properties_assigned_to_this_function.Code,
        (int)Messages.Declaration_emit_for_this_parameter_requires_implicitly_adding_undefined_to_its_type_This_is_not_supported_with_isolatedDeclarations.Code,
        (int)Messages.Type_containing_private_name_0_can_t_be_used_with_isolatedDeclarations.Code,
        (int)Messages.Add_satisfies_and_a_type_assertion_to_this_expression_satisfies_T_as_T_to_make_the_type_explicit.Code,
    ];

    private async ValueTask<IReadOnlyList<CodeFix>> IsolatedAsync(int start, int end)
    {
        List<CodeFix> result = [];
        foreach (bool inline in new[] { false, true })
            foreach (var mode in Enum.GetValues<AnnotationMode>())
            {
                var fixer = new IsolatedDeclarationFixer(projection, checker, preferences, mode, cancellation);
                var description = inline ? await fixer.InlineAsync(start, end) : await fixer.AnnotateAsync(start);
                await AddAsync(fixer, description);
            }
        var extract = new IsolatedDeclarationFixer(projection, checker, preferences, AnnotationMode.Full, cancellation);
        await AddAsync(extract, await extract.ExtractAsync(start, end));
        return result;

        async ValueTask AddAsync(IsolatedDeclarationFixer fixer, Utf8String description)
        {
            if (!description.IsEmpty && await fixer.ChangesAsync() is { Length: > 0 } changes) result.Add(new(description, changes));
        }
    }

    private async ValueTask<CodeFix?> AllIsolatedAsync()
    {
        var fixer = new IsolatedDeclarationFixer(projection, checker, preferences, AnnotationMode.Full, cancellation);
        foreach (var diagnostic in await DiagnosticsAsync())
            if (Matches(diagnostic, IsolatedErrorCodes)) await fixer.AnnotateAsync(diagnostic.Start);
        var changes = await fixer.ChangesAsync();
        return changes.Length == 0 ? null : new(Messages.Add_all_missing_type_annotations.Format(preferences.Locale), changes);
    }
}
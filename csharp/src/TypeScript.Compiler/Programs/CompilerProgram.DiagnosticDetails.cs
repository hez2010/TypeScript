using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;

namespace TypeScript.Compiler.Programs;

public sealed partial class CompilerProgram
{
    internal (DiagnosticMessage Message, Utf8String[] Arguments) ModuleNotFoundDetails(
        ResolvedModule? resolved, Utf8String moduleName, Utf8String packageName)
    {
        Utf8String mangled = ModuleResolver.Mangle(packageName);
        if (resolved is { AlternateResult.IsEmpty: false })
            return (Messages.There_are_types_at_0_but_this_result_could_not_be_resolved_when_respecting_package_json_exports_The_1_library_may_need_to_update_its_package_json_or_typings,
                [resolved.AlternateResult, resolved.AlternateResult.Contains("/node_modules/@types/"u8, StringComparison.Ordinal) ? "@types/"u8 + mangled : packageName]);
        bool hasTypes = false, typedPackage = false;
        foreach (var file in SourceFiles)
            foreach (var reference in file.Resolutions)
                if (!reference.TypeReference && reference.Resolution.PackageId is { Name.Length: > 0 } package)
                {
                    hasTypes |= package.Name == "@types/"u8 + mangled;
                    typedPackage |= package.Name == packageName && reference.Resolution.Extension == Utf8Literals.DTs;
                }
        if (hasTypes)
            return (Messages.If_the_0_package_actually_exposes_this_module_consider_sending_a_pull_request_to_amend_https_Colon_Slash_Slashgithub_com_SlashDefinitelyTyped_SlashDefinitelyTyped_Slashtree_Slashmaster_Slashtypes_Slash_1, [packageName, mangled]);
        if (typedPackage)
            return (Messages.If_the_0_package_actually_exposes_this_module_try_adding_a_new_declaration_d_ts_file_containing_declare_module_1, [packageName, moduleName]);
        return (Messages.Try_npm_i_save_dev_types_Slash_1_if_it_exists_or_add_a_new_declaration_d_ts_file_containing_declare_module_0, [moduleName, mangled]);
    }

    internal static (DiagnosticMessage Message, Utf8String[] Arguments) ModeMismatchDetails(ProgramFile file)
    {
        Utf8String extension = CompilerPath.Extension(file.Syntax.FileName) switch
        { var ext when ext == ".ts"u8 => Utf8Literals.Mts, var ext when ext == ".js"u8 => Utf8Literals.Mjs, _ => Utf8String.Empty };
        if (!file.PackageDirectory.IsEmpty && file.PackageType.IsEmpty)
            return extension.IsEmpty
                ? (Messages.To_convert_this_file_to_an_ECMAScript_module_add_the_field_type_Colon_module_to_0,
                    [CompilerPath.Combine(file.PackageDirectory, Utf8Literals.PackageJson)])
                : (Messages.To_convert_this_file_to_an_ECMAScript_module_change_its_file_extension_to_0_or_add_the_field_type_Colon_module_to_1,
                    [extension, CompilerPath.Combine(file.PackageDirectory, Utf8Literals.PackageJson)]);
        return extension.IsEmpty
            ? (Messages.To_convert_this_file_to_an_ECMAScript_module_create_a_local_package_json_file_with_type_Colon_module, [])
            : (Messages.To_convert_this_file_to_an_ECMAScript_module_change_its_file_extension_to_0_or_create_a_local_package_json_file_with_type_Colon_module, [extension]);
    }

    internal Diagnostic RepopulateDiagnostic(Diagnostic root)
    {
        if (root.Repopulation.Kind == 0 && root.MessageChain.Count == 0 && root.RelatedInformation.Count == 0)
            return root.FromBuildInfo ? root with { FromBuildInfo = false } : root;
        var pending = new Stack<(Diagnostic Diagnostic, bool Exit)>();
        var results = new Dictionary<Diagnostic, Diagnostic>(ReferenceEqualityComparer.Instance);
        pending.Push((root, false));
        while (pending.TryPop(out var entry))
        {
            var diagnostic = entry.Diagnostic;
            if (!entry.Exit)
            {
                pending.Push((diagnostic, true));
                foreach (var child in diagnostic.MessageChain) pending.Push((child, false));
                foreach (var child in diagnostic.RelatedInformation) pending.Push((child, false));
                continue;
            }
            var updated = diagnostic.FromBuildInfo ? diagnostic with { FromBuildInfo = false } : diagnostic;
            if (diagnostic.Repopulation is { Kind: not 0 } info && diagnostic.FileName is { } path && GetFile(path) is { } file)
            {
                var details = info.Kind == 1 ? ModeMismatchDetails(file) : ModuleNotFoundDetails(
                    file.Resolutions.FirstOrDefault(reference => reference.Specifier == info.ModuleReference && (int)reference.Mode == info.Mode)?.Resolution,
                    info.ModuleReference, info.PackageName.IsEmpty ? info.ModuleReference : info.PackageName);
                updated = updated with { Message = details.Message, Arguments = details.Arguments, Repopulation = default };
            }
            if (diagnostic.MessageChain.Any(child => !ReferenceEquals(child, results[child])))
                updated = updated with { MessageChain = diagnostic.MessageChain.Select(child => results[child]).ToArray() };
            if (diagnostic.RelatedInformation.Any(child => !ReferenceEquals(child, results[child])))
                updated = updated with { RelatedInformation = diagnostic.RelatedInformation.Select(child => results[child]).ToArray() };
            results[diagnostic] = updated;
        }
        return results[root];
    }
}

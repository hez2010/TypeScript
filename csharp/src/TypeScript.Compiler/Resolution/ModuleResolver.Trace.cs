using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.Resolution;

public sealed partial class ModuleResolver
{
    private sealed partial class Request
    {
        internal readonly List<Diagnostic> traceMessages = [];

        private void Message(DiagnosticMessage message, params Utf8String[] arguments)
        {
            if (resolver.TraceEnabled) traceMessages.Add(new(message, 0, 0, arguments));
        }

        internal void StartTrace(Utf8String containingFile)
        {
            if (types)
                Message(Messages.Resolving_type_reference_directive_0_containing_file_1_root_directory_2,
                    name, containingFile, Utf8String.Join(","u8, resolver.TypeRoots()));
            else Message(Messages.Resolving_module_0_from_1, name, containingFile);
            if (!resolver.RedirectConfig.IsEmpty)
                Message(Messages.Using_compiler_options_of_project_reference_redirect_0, resolver.RedirectConfig);
            if (types) return;
            Utf8String kind = resolver.ResolutionKind == Utf8Literals.Bundler ? "Bundler"u8
                : resolver.ResolutionKind == Utf8Literals.Nodenext ? "NodeNext"u8 : "Node16"u8;
            Message(options.ModuleResolution == options.EmitModuleResolutionKind
                ? Messages.Explicitly_specified_module_resolution_kind_Colon_0
                : Messages.Module_resolution_kind_is_not_specified_using_0, kind);
            Message(Messages.Resolving_in_0_mode_with_conditions_1, esm ? "ESM"u8 : "CJS"u8,
                Utf8String.Join(", "u8, conditions.Select(c => "'"u8 + c + "'"u8)));
        }

        internal void FinishTrace(ResolvedModule result)
        {
            if (!types)
            {
                if (!result.IsResolved) Message(Messages.Module_name_0_was_not_resolved, name);
                else if (result.PackageId is { } package)
                    Message(Messages.Module_name_0_was_successfully_resolved_to_1_with_Package_ID_2, name, result.FileName, package.ToUtf8String());
                else Message(Messages.Module_name_0_was_successfully_resolved_to_1, name, result.FileName);
            }
            else if (!result.IsResolved) Message(Messages.Type_reference_directive_0_was_not_resolved, name);
            else if (result.PackageId is { } package)
                Message(Messages.Type_reference_directive_0_was_successfully_resolved_to_1_with_Package_ID_2_primary_Colon_3,
                    name, result.FileName, package.ToUtf8String(), result.Primary ? "true"u8 : "false"u8);
            else Message(Messages.Type_reference_directive_0_was_successfully_resolved_to_1_primary_Colon_2,
                name, result.FileName, result.Primary ? "true"u8 : "false"u8);
        }

        private static Utf8String ExtensionNames(Extensions extensions)
        {
            var names = new List<Utf8String>(4);
            if ((extensions & Extensions.TypeScript) != 0) names.Add("TypeScript"u8);
            if ((extensions & Extensions.JavaScript) != 0) names.Add("JavaScript"u8);
            if ((extensions & Extensions.Declaration) != 0) names.Add("Declaration"u8);
            if ((extensions & Extensions.Json) != 0) names.Add("JSON"u8);
            return Utf8String.Join(", "u8, names);
        }
    }
}

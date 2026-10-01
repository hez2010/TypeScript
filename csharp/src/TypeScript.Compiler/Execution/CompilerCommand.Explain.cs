using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Resolution;

namespace TypeScript.Compiler.Execution;

public sealed partial class CompilerCommand
{
    private void ExplainFiles(CompilerProgram program)
    {
        var config = program.Configuration;
        Utf8String Relative(Utf8String path) => CompilerPath.Relative(currentDirectory, path, fileSystem.CaseSensitive);
        void Explain(DiagnosticMessage message, params Utf8String[] args) => output.Write("   " + message.Format(config.Options.Locale, args).ToString() + "\n");
        foreach (var file in program.ExplanationFiles)
        {
            var path = file.Syntax.FileName;
            output.Write(Relative(path).ToString() + "\n");
            foreach (var reason in program.ReasonsForFile(path))
                output.Write("   " + program.IncludeReasonDiagnostic(reason, path, relative: true).Format(config.Options.Locale).ToString() + "\n");
            if (program.ProjectReferences.Outputs.TryGetValue(path, out var redirect)
                && program.Redirects.TryGetValue(redirect.Source, out var redirectedOutput) && redirectedOutput == path)
                Explain(Messages.File_is_output_of_project_reference_source_0, Relative(redirect.Source));
            if (program.Redirects.TryGetValue(path, out var target)) Explain(Messages.File_redirects_to_file_0, Relative(target));
            if (file.Binding.IsModule && (config.Options.EmitModule is >= ModuleKind.Node16 and <= ModuleKind.NodeNext
                || path.EndsWith(".mts"u8, StringComparison.Ordinal) || path.EndsWith(".cts"u8, StringComparison.Ordinal)))
            {
                if (file.ImpliedFormat == ReferenceResolutionMode.Import && file.PackageType == "module"u8)
                    Explain(Messages.File_is_ECMAScript_module_because_0_has_field_type_with_value_module, Relative(CompilerPath.Combine(file.PackageDirectory, "package.json"u8)));
                else if (file.ImpliedFormat == ReferenceResolutionMode.Require)
                {
                    if (!file.PackageType.IsEmpty) Explain(Messages.File_is_CommonJS_module_because_0_has_field_type_whose_value_is_not_module, Relative(CompilerPath.Combine(file.PackageDirectory, "package.json"u8)));
                    else if (!file.PackageDirectory.IsEmpty) Explain(Messages.File_is_CommonJS_module_because_0_does_not_have_field_type, Relative(CompilerPath.Combine(file.PackageDirectory, "package.json"u8)));
                    else Explain(Messages.File_is_CommonJS_module_because_package_json_was_not_found);
                }
            }
        }
    }
}

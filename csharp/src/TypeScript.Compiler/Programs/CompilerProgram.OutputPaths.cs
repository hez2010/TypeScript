using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Incremental;

namespace TypeScript.Compiler.Programs;

public sealed partial class CompilerProgram
{
    private readonly HashSet<Utf8String> blockedEmitPaths;

    public bool IsEmitBlocked(Utf8String path) => blockedEmitPaths.Contains(CompilerPath.Resolve(CurrentDirectory, path));

    private IEnumerable<Diagnostic> VerifyOutputPaths()
    {
        var options = Configuration.Options;
        var buildInfoPath = IncrementalOptions.GetBuildInfoFileName(Configuration, CurrentDirectory, UseCaseSensitiveFileNames);
        if (!buildInfoPath.IsEmpty && options.SuppressOutputPathCheck != true)
            foreach (var project in ProjectReferences.Projects.Values)
                foreach (var reference in project.References)
                {
                    var path = reference.Path.EndsWith(".json"u8, StringComparison.OrdinalIgnoreCase) ? reference.Path
                        : CompilerPath.Combine(reference.Path, "tsconfig.json"u8);
                    if (!ProjectReferences.Projects.TryGetValue(path, out var child) || files.Comparer.Equals(child.FileName, Configuration.FileName)) continue;
                    var childBuildInfo = IncrementalOptions.GetBuildInfoFileName(child, CurrentDirectory, UseCaseSensitiveFileNames, options.Build == true);
                    if (!files.Comparer.Equals(buildInfoPath, childBuildInfo)) continue;
                    blockedEmitPaths.Add(buildInfoPath);
                    yield return new(Messages.Cannot_write_file_0_because_it_will_overwrite_tsbuildinfo_file_generated_by_referenced_project_1,
                        reference.SourceStart, reference.SourceLength, [buildInfoPath, reference.Path])
                    { FileName = project.SourceFile is null ? null : project.FileName };
                }
        if (options.NoEmit == true || options.SuppressOutputPathCheck == true) yield break;
        var emitted = new HashSet<Utf8String>(files.Comparer);
        foreach (var file in SourceFiles)
        {
            if (!SourceFileMayBeEmitted(file.Syntax)) continue;
            var paths = GetOutputPaths(file.Syntax);
            foreach (var path in new[] { paths.JavaScript, paths.SourceMap, paths.Declaration, paths.DeclarationMap })
            {
                if (path.Length == 0) continue;
                var absolute = CompilerPath.Resolve(CurrentDirectory, path);
                if (files.ContainsKey(absolute))
                {
                    blockedEmitPaths.Add(absolute);
                    yield return new(Messages.Cannot_write_file_0_because_it_would_overwrite_input_file, -1, 0, [path])
                    {
                        MessageChain = Configuration.FileName.Length == 0
                            ? [new(Messages.Adding_a_tsconfig_json_file_will_help_organize_projects_that_contain_both_TypeScript_and_JavaScript_files_Learn_more_at_https_Colon_Slash_Slashaka_ms_Slashtsconfig, -1, 0, [])] : []
                    };
                }
                if (!emitted.Add(absolute))
                {
                    blockedEmitPaths.Add(absolute);
                    yield return new(Messages.Cannot_write_file_0_because_it_would_be_overwritten_by_multiple_input_files, -1, 0, [path]);
                }
            }
        }
    }
}

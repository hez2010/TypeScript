using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;

namespace TypeScript.Compiler.Programs;

public sealed partial class CompilerProgram
{
    private readonly HashSet<Utf8String> blockedEmitPaths;

    public bool IsEmitBlocked(Utf8String path) => blockedEmitPaths.Contains(CompilerPath.Resolve(CurrentDirectory, path));

    private IEnumerable<Diagnostic> VerifyOutputPaths()
    {
        var options = Configuration.Options;
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
                    yield return new(Messages.Cannot_write_file_0_because_it_would_overwrite_input_file, 0, 0, [path])
                    {
                        MessageChain = Configuration.FileName.Length == 0
                            ? [new(Messages.Adding_a_tsconfig_json_file_will_help_organize_projects_that_contain_both_TypeScript_and_JavaScript_files_Learn_more_at_https_Colon_Slash_Slashaka_ms_Slashtsconfig, 0, 0, [])] : []
                    };
                }
                if (!emitted.Add(absolute))
                {
                    blockedEmitPaths.Add(absolute);
                    yield return new(Messages.Cannot_write_file_0_because_it_would_be_overwritten_by_multiple_input_files, 0, 0, [path]);
                }
            }
        }
    }
}

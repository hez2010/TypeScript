using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;

namespace TypeScript.Compiler.Programs;

public sealed partial class CompilerProgram
{
    private sealed partial class Builder
    {
        private void VerifyOutputPaths(IReadOnlyList<ProgramFile> ordered)
        {
            var options = config.Options;
            if (options.NoEmit == true || options.SuppressOutputPathCheck == true)
                return;
            var inputs = ordered.Where(f => !f.Library && !f.Syntax.IsDeclarationFile && f.Mapping is null
                && loadedDepth.GetValueOrDefault(f.Syntax.FileName) == 0).ToArray();
            string root = options.RootDir ?? (options.Composite == true ? CompilerPath.DirectoryName(config.FileName)
                : ProjectReferences.CommonDirectory(inputs.Select(f => f.Syntax.FileName), fs.CaseSensitive));
            var emitted = new HashSet<string>(files.Comparer);
            foreach (var file in inputs)
            {
                string source = file.Syntax.FileName, extension = ModuleResolver.Extension(source);
                string Output(string suffix, string? directory)
                {
                    string name = source[..^extension.Length] + suffix;
                    return directory is null ? name : CompilerPath.Resolve(directory, CompilerPath.Relative(root, name, fs.CaseSensitive));
                }
                void Check(string path)
                {
                    if (files.ContainsKey(path))
                        diagnostics.Add(new(Messages.Cannot_write_file_0_because_it_would_overwrite_input_file, 0, 0, [path]));
                    if (!emitted.Add(path))
                        diagnostics.Add(
                            new(Messages.Cannot_write_file_0_because_it_would_be_overwritten_by_multiple_input_files, 0, 0, [path]));
                }
                if (extension == ".json")
                {
                    if (options.OutDir is { } outDir)
                    {
                        string output = Output(extension, outDir);
                        if (!output.Equals(source, fs.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                            Check(output);
                    }
                    continue;
                }
                if (options.EmitDeclarationOnly != true)
                {
                    string suffix = extension is ".mts" or ".mjs" ? ".mjs" : extension is ".cts" or ".cjs" ? ".cjs"
                        : extension is ".tsx" or ".jsx" && options.Jsx == JsxEmit.Preserve ? ".jsx" : ".js";
                    string output = Output(suffix, options.OutDir);
                    Check(output);
                    if (options.SourceMap == true)
                        Check(output + ".map");
                }
                if (options.Declaration == true || options.Composite == true)
                {
                    string suffix = extension is ".mts" or ".mjs" ? ".d.mts" : extension is ".cts" or ".cjs" ? ".d.cts" : ".d.ts";
                    string output = Output(suffix, options.DeclarationDir ?? options.OutDir);
                    Check(output);
                    if (options.DeclarationMap == true)
                        Check(output + ".map");
                }
            }
        }
    }
}

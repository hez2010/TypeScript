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
            Utf8String root = options.RootDir ?? (options.Composite == true ? CompilerPath.DirectoryName(config.FileName)
                : ProjectReferences.CommonDirectory(inputs.Select(f => f.Syntax.FileName), fs.CaseSensitive));
            var emitted = new HashSet<Utf8String>(files.Comparer);
            foreach (var file in inputs)
            {
                Utf8String source = file.Syntax.FileName, extension = ModuleResolver.Extension(source);
                Utf8String Output(Utf8String suffix, Utf8String? directory)
                {
                    Utf8String name = source[..^extension.Length] + suffix;
                    return directory is null ? name : CompilerPath.Resolve(directory.Value, CompilerPath.Relative(root, name, fs.CaseSensitive));
                }
                void Check(Utf8String path)
                {
                    if (files.ContainsKey(path))
                        diagnostics.Add(new(Messages.Cannot_write_file_0_because_it_would_overwrite_input_file, 0, 0, [path]));
                    if (!emitted.Add(path))
                        diagnostics.Add(
                            new(Messages.Cannot_write_file_0_because_it_would_be_overwritten_by_multiple_input_files, 0, 0, [path]));
                }
                if (extension == Utf8Literals.Json)
                {
                    if (options.OutDir is { } outDir)
                    {
                        Utf8String output = Output(extension, outDir);
                        if (!output.Equals(source, fs.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                            Check(output);
                    }
                    continue;
                }
                if (options.EmitDeclarationOnly != true)
                {
                    Utf8String suffix = (extension == ".mts"u8 || extension == ".mjs"u8) ? Utf8Literals.Mjs : (extension == ".cts"u8 || extension == ".cjs"u8) ? Utf8Literals.Cjs
                        : (extension == ".tsx"u8 || extension == ".jsx"u8) && options.Jsx == JsxEmit.Preserve ? Utf8Literals.Jsx : Utf8Literals.Js;
                    Utf8String output = Output(suffix, options.OutDir);
                    Check(output);
                    if (options.SourceMap == true)
                        Check(output + Utf8Literals.Map);
                }
                if (options.Declaration == true || options.Composite == true)
                {
                    Utf8String suffix = (extension == ".mts"u8 || extension == ".mjs"u8) ? Utf8Literals.DMts : (extension == ".cts"u8 || extension == ".cjs"u8) ? Utf8Literals.DCts : Utf8Literals.DTs;
                    Utf8String output = Output(suffix, options.DeclarationDir ?? options.OutDir);
                    Check(output);
                    if (options.DeclarationMap == true)
                        Check(output + Utf8Literals.Map);
                }
            }
        }
    }
}

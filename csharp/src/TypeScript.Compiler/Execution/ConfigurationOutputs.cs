using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Resolution;

namespace TypeScript.Compiler.Execution;

internal static class ConfigurationOutputs
{
    internal static IEnumerable<Utf8String> GetFiles(ParsedConfig config, Utf8String currentDirectory, bool caseSensitive)
    {
        var options = config.Options;
        var common = options.RootDir ?? (config.FileName.Length != 0 ? CompilerPath.DirectoryName(config.FileName)
            : ProjectReferences.CommonDirectory(config.FileNames.Where(file => !CompilerPath.IsDeclarationFile(file)), caseSensitive));
        Utf8String InDirectory(Utf8String source, Utf8String? directory) => directory is { IsEmpty: false } output
            ? CompilerPath.Resolve(output, CompilerPath.Relative(common, source, caseSensitive)) : source;
        foreach (var file in config.FileNames)
        {
            if (CompilerPath.IsDeclarationFile(file)) continue;
            Utf8String extension = ModuleResolver.Extension(file);
            bool json = extension == ".json"u8;
            var mappedExtension = config.ContentMappers.SelectMany(mapper => mapper.Extensions)
                .Where(extension => file.EndsWith(extension, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(extension => extension.Length).FirstOrDefault();
            if (options.EmitDeclarationOnly != true && mappedExtension.IsEmpty)
            {
                var input = InDirectory(file, options.OutDir);
                var suffix = json ? ".json"u8 : options.Jsx == JsxEmit.Preserve && (extension == ".tsx"u8 || extension == ".jsx"u8) ? ".jsx"u8
                    : extension == ".mts"u8 || extension == ".mjs"u8 ? ".mjs"u8 : extension == ".cts"u8 || extension == ".cjs"u8 ? ".cjs"u8 : ".js"u8;
                var output = input[..^CompilerPath.Extension(input).Length] + suffix;
                if (!json || CompilerPath.Relative(CompilerPath.Resolve(currentDirectory, file), CompilerPath.Resolve(currentDirectory, output), caseSensitive).Length != 0)
                {
                    yield return output;
                    if (!json && options.SourceMap == true && options.InlineSourceMap != true) yield return output + ".map"u8;
                }
            }
            if (!json && (options.Declaration == true || options.Composite == true))
            {
                var input = InDirectory(file, options.DeclarationDir ?? options.OutDir);
                var suffix = extension == ".mts"u8 || extension == ".mjs"u8 ? ".d.mts"u8
                    : extension == ".cts"u8 || extension == ".cjs"u8 ? ".d.cts"u8 : ".d.ts"u8;
                var output = mappedExtension.Length != 0 ? input[..^mappedExtension.Length] + ".d"u8 + mappedExtension + ".ts"u8
                    : input[..^CompilerPath.Extension(input).Length] + suffix;
                yield return output;
                if (options.DeclarationMap == true && mappedExtension.IsEmpty) yield return output + ".map"u8;
            }
        }
    }
}

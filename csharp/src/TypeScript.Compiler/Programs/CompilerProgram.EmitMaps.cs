using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Programs;

public sealed partial class CompilerProgram
{
    private sealed partial class FileEmitter
    {
        private async ValueTask PrintAsync(SourceFileNode tree, SyntaxPrinter printer, Utf8String outputPath, Utf8String mapPath, bool sourceMap, bool inline)
        {
            SourceMapGenerator? generator = (sourceMap || inline) && source.ScriptKind != ScriptKind.JSON
                ? new(CompilerPath.BaseName(outputPath), options.SourceRoot is { Length: > 0 } root
                    ? CompilerPath.EnsureTrailingSeparator(CompilerPath.NormalizeSlashes(root)) : Utf8String.Empty,
                    MapDirectory(outputPath), program.UseCaseSensitiveFileNames, program.CurrentDirectory) : null;
            var writer = new EmitTextWriter(NewLine);
            printer.Write(tree, tree, writer, generator, cancellation);
            int mapPosition = -1;
            if (generator is not null)
            {
                maps.Add(new(outputPath, generator.Sources.ToArray(), generator.ToSourceMap()));
                var url = inline ? generator.ToSourceMap().ToDataUrl() : SourceMappingUrl(outputPath, mapPath);
                if (url.Length != 0)
                {
                    if (!writer.AtLineStart) writer.RawWrite(NewLine);
                    mapPosition = writer.Position;
                    writer.WriteComment("//# sourceMappingURL="u8);
                    writer.WriteComment(url);
                }
                if (mapPath.Length != 0) await WriteAsync(mapPath, generator.ToSourceMap().ToJson(), new(source, 0), outputPath);
            }
            else writer.WriteLine();
            var text = writer.Text;
            if (options.EmitBOM == true) text = "\uFEFF"u8 + text;
            await WriteAsync(outputPath, text, new(source, mapPosition, DiagnosticCollection.SortAndDeduplicate(diagnostics)), outputPath);
        }

        private async ValueTask WriteAsync(Utf8String path, Utf8String text, EmitWriteFileData data, Utf8String diagnosticPath)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (emitOptions.WriteFile is { } write) await write(path, text, data, cancellation);
                else program.fileSystem.WriteFile(path, text.Span);
                if (!data.SkippedDeclarationWrite) emitted.Add(path);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                diagnostics.Add(new(Messages.Could_not_write_file_0_Colon_1, -1, 0, [diagnosticPath, Utf8String.FromString(error.Message)]));
            }
        }

        private Utf8String MapDirectory(Utf8String path)
        {
            if (options.SourceRoot is { Length: > 0 }) return program.CommonSourceDirectory;
            if (options.MapRoot is not { Length: > 0 } mapRoot) return CompilerPath.DirectoryName(CompilerPath.Normalize(path));
            var directory = CompilerPath.DirectoryName(program.SourceInOutputDirectory(source.FileName, CompilerPath.NormalizeSlashes(mapRoot)));
            return CompilerPath.IsAbsolute(directory) ? directory : CompilerPath.Combine(program.CommonSourceDirectory, directory);
        }

        private Utf8String SourceMappingUrl(Utf8String path, Utf8String mapPath)
        {
            var name = CompilerPath.BaseName(mapPath);
            if (options.MapRoot is { Length: > 0 } root)
            {
                var directory = CompilerPath.DirectoryName(program.SourceInOutputDirectory(source.FileName, CompilerPath.NormalizeSlashes(root)));
                if (CompilerPath.IsAbsolute(directory)) return EncodeUri(CompilerPath.Combine(directory, name));
                var map = CompilerPath.Combine(program.CommonSourceDirectory, directory, name);
                var relative = CompilerPath.Relative(CompilerPath.DirectoryName(CompilerPath.Resolve(program.CurrentDirectory, path)),
                    CompilerPath.Resolve(program.CurrentDirectory, map), program.UseCaseSensitiveFileNames);
                if (CompilerPath.IsAbsolute(relative) && !CompilerPath.IsUrl(relative))
                    relative = (relative.StartsWith((byte)'/') ? "file://"u8 : "file:///"u8) + relative;
                return EncodeUri(relative);
            }
            return EncodeUri(name);
        }

        private static Utf8String EncodeUri(Utf8String value)
        {
            var result = new Utf8StringBuilder();
            foreach (byte item in value.Span)
                if (item is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                    || ";,/?:@&=+$-_.!~*'()#"u8.Contains(item)) result.Append(item);
                else
                {
                    result.Append((byte)'%');
                    result.Append("0123456789ABCDEF"u8[item >> 4]);
                    result.Append("0123456789ABCDEF"u8[item & 15]);
                }
            return result.ToUtf8String();
        }
    }
}

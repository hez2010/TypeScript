using System.Globalization;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Execution;

public static partial class DiagnosticReporter
{
    public static void WritePlain(TextWriter writer, Diagnostic diagnostic, IFileSystem fileSystem, Utf8String currentDirectory,
        CompilerProgram? program = null, Utf8String? locale = null)
    {
        var message = diagnostic.Format(locale);
        if (diagnostic.FileName is { IsEmpty: false } fileName)
        {
            var file = program?.GetFile(fileName);
            var source = file?.Syntax.Source ?? new SourceText(SourceEncoding.Decode(fileSystem.ReadFile(fileName) ?? []));
            int position = diagnostic.Start;
            if (file?.Mapping is { } mapping)
            {
                if (diagnostic.Source is not null) source = mapping.Original;
                else if (!diagnostic.IsMapperFailure)
                {
                    var span = mapping.Map.VirtualToOriginalSpan(diagnostic.Start, diagnostic.Start + diagnostic.Length);
                    if (span.Fidelity != MappingFidelity.None) { position = span.Start; source = mapping.Original; }
                    else message += "\n  "u8 + Messages.This_location_is_in_virtual_code_produced_by_the_content_mapper_0_and_has_no_corresponding_location_in_the_original_file
                        .Format(locale, mapping.MapperIdentity);
                }
            }
            position = Math.Clamp(position, 0, source.Length);
            int line = source.GetLineAndCharacter(position).Line;
            int column = EmitTextWriter.Utf16Length(source.Bytes.Span.Slice(source.LineStarts[line], position - source.LineStarts[line]));
            writer.Write(CompilerPath.Relative(currentDirectory, fileName, fileSystem.CaseSensitive).ToString());
            writer.Write(string.Create(CultureInfo.InvariantCulture, $"({line + 1},{column + 1}): "));
        }
        writer.Write(diagnostic.Message.Category switch
        {
            DiagnosticCategory.Warning => "warning", DiagnosticCategory.Error => "error",
            DiagnosticCategory.Suggestion => "suggestion", _ => "message"
        });
        writer.Write(' ');
        writer.Write((diagnostic.Source ?? "TS"u8).ToString());
        writer.Write(((int)diagnostic.Code).ToString(CultureInfo.InvariantCulture));
        writer.Write(": "); writer.Write(message.ToString()); writer.Write('\n');
    }

    public static void WriteStatus(TextWriter writer, Diagnostic diagnostic, DateTime time, Utf8String? locale = null, bool pretty = false)
    {
        if (pretty) writer.Write("[\u001b[90m");
        writer.Write(time.ToString("hh:mm:ss tt", CultureInfo.InvariantCulture));
        writer.Write(pretty ? "\u001b[0m] " : " - "); writer.Write(diagnostic.Format(locale).ToString()); writer.Write("\n\n");
    }
}

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
    private const string Reset = "\u001b[0m", Grey = "\u001b[90m", Red = "\u001b[91m", Yellow = "\u001b[93m", Cyan = "\u001b[96m", Gutter = "\u001b[7m";

    public static void Write(TextWriter writer, Diagnostic diagnostic, IFileSystem fileSystem, Utf8String currentDirectory,
        CompilerProgram? program = null, Utf8String? locale = null, bool pretty = false)
    {
        if (pretty) WritePretty(writer, diagnostic, fileSystem, currentDirectory, program, locale);
        else WritePlain(writer, diagnostic, fileSystem, currentDirectory, program, locale);
    }

    private readonly record struct Location(Utf8String FileName, SourceText? Source, int Start, int Length, Utf8String Message);
    private static Location Resolve(Diagnostic diagnostic, IFileSystem fileSystem, CompilerProgram? program, Utf8String? locale)
    {
        var message = diagnostic.Format(locale);
        var name = diagnostic.FileName ?? default;
        if (name.IsEmpty) return new(name, null, 0, 0, message);
        var file = program?.GetFile(name);
        var source = file?.Syntax.Source ?? new SourceText(SourceEncoding.Decode(fileSystem.ReadFile(name) ?? []));
        int start = diagnostic.Start, length = diagnostic.Length;
        if (file?.Mapping is { } mapping)
        {
            if (diagnostic.Source is not null) source = mapping.Original;
            else if (!diagnostic.IsMapperFailure)
            {
                var span = mapping.Map.VirtualToOriginalSpan(start, start + length);
                if (span.Fidelity != MappingFidelity.None) { start = span.Start; length = span.End - span.Start; source = mapping.Original; }
                else message += "\n  "u8 + Messages.This_location_is_in_virtual_code_produced_by_the_content_mapper_0_and_has_no_corresponding_location_in_the_original_file
                    .Format(locale, mapping.MapperIdentity);
            }
        }
        start = Math.Clamp(start, 0, source.Length); length = Math.Clamp(length, 0, source.Length - start);
        return new(name, source, start, length, message);
    }

    private static (int Line, int Column) Position(SourceText source, int position)
    {
        int line = source.GetLineAndCharacter(position).Line;
        return (line, EmitTextWriter.Utf16Length(source.Bytes.Span.Slice(source.LineStarts[line], position - source.LineStarts[line])));
    }

    private static void WriteLocation(TextWriter writer, Location location, IFileSystem fileSystem, Utf8String currentDirectory)
    {
        var (line, column) = Position(location.Source!, location.Start);
        writer.Write(Cyan); writer.Write(CompilerPath.Relative(currentDirectory, location.FileName, fileSystem.CaseSensitive).ToString());
        writer.Write(Reset + ":" + Yellow); writer.Write((line + 1).ToString(CultureInfo.InvariantCulture));
        writer.Write(Reset + ":" + Yellow); writer.Write((column + 1).ToString(CultureInfo.InvariantCulture)); writer.Write(Reset);
    }

    public static void WritePretty(TextWriter writer, Diagnostic diagnostic, IFileSystem fileSystem, Utf8String currentDirectory,
        CompilerProgram? program = null, Utf8String? locale = null)
    {
        var location = Resolve(diagnostic, fileSystem, program, locale);
        if (location.Source is not null) { WriteLocation(writer, location, fileSystem, currentDirectory); writer.Write(" - "); }
        var (category, color) = diagnostic.Message.Category switch
        {
            DiagnosticCategory.Error => ("error", Red), DiagnosticCategory.Warning => ("warning", Yellow),
            DiagnosticCategory.Suggestion => ("suggestion", Grey), _ => ("message", "\u001b[94m")
        };
        writer.Write(color + category + Reset + Grey + " " + (diagnostic.Source ?? "TS"u8).ToString());
        writer.Write(((int)diagnostic.Code).ToString(CultureInfo.InvariantCulture)); writer.Write(": " + Reset); writer.Write(location.Message.ToString());
        if (location.Source is not null && diagnostic.Code != Messages.File_appears_to_be_binary.Code)
        { writer.Write('\n'); Snippet(writer, location, color, ""); writer.Write('\n'); }
        foreach (var related in diagnostic.RelatedInformation)
        {
            var item = Resolve(related, fileSystem, program, locale);
            if (item.Source is not null)
            {
                writer.Write("\n  "); WriteLocation(writer, item, fileSystem, currentDirectory); writer.Write(" - "); writer.Write(item.Message.ToString());
                Snippet(writer, item, Cyan, "    ");
            }
            writer.Write('\n');
        }
        writer.Write('\n');
    }

    private static void Snippet(TextWriter writer, Location location, string color, string indent)
    {
        var source = location.Source!;
        var (first, firstColumn) = Position(source, location.Start);
        var (last, lastColumn) = Position(source, location.Start + location.Length);
        if (location.Length == 0) lastColumn++;
        bool elide = last - first >= 4;
        int width = (last + 1).ToString(CultureInfo.InvariantCulture).Length;
        if (elide) width = Math.Max(3, width);
        for (int line = first; line <= last; line++)
        {
            writer.Write('\n');
            if (elide && line > first + 1 && line < last - 1)
            { writer.Write(indent + Gutter + "...".PadLeft(width) + Reset + " \n"); line = last - 1; }
            int start = source.LineStarts[line];
            int end = line + 1 < source.LineStarts.Length ? source.LineStarts[line + 1] : source.Length;
            string text = source.Text[start..end].ToString().TrimEnd().Replace('\t', ' ');
            writer.Write(indent + Gutter + (line + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width) + Reset + " " + text + "\n");
            writer.Write(indent + Gutter + new string(' ', width) + Reset + " " + color);
            int column = line == first ? firstColumn : 0;
            int finish = line == last ? lastColumn : text.Length;
            writer.Write(new string(' ', column)); writer.Write(new string('~', Math.Max(0, finish - column))); writer.Write(Reset);
        }
    }

    public static void WriteSummary(TextWriter writer, IReadOnlyList<Diagnostic> diagnostics, IFileSystem fileSystem,
        Utf8String currentDirectory, CompilerProgram? program = null, Utf8String? locale = null)
    {
        var errors = diagnostics.Where(item => item.Message.Category == DiagnosticCategory.Error).ToArray();
        if (errors.Length == 0) return;
        var groups = errors.Where(item => item.FileName is { IsEmpty: false }).GroupBy(item => item.FileName!.Value)
            .OrderBy(group => group.Key, Utf8StringComparer.Ordinal).ToArray();
        Utf8String Count(int value) => Utf8String.FromString(value.ToString(CultureInfo.InvariantCulture));
        Utf8String Path(Diagnostic item)
        {
            var location = Resolve(item, fileSystem, program, locale);
            int line = location.Source!.GetLineAndCharacter(location.Start).Line;
            return CompilerPath.Relative(currentDirectory, location.FileName, fileSystem.CaseSensitive) + Utf8String.FromString(Grey + ":" + (line + 1).ToString(CultureInfo.InvariantCulture) + Reset);
        }
        var first = groups.Length == 0 ? default : Path(groups[0].First());
        var message = errors.Length == 1
            ? groups.Length == 0 ? Messages.Found_1_error.Format(locale) : Messages.Found_1_error_in_0.Format(locale, first)
            : groups.Length == 0 ? Messages.Found_0_errors.Format(locale, Count(errors.Length))
            : groups.Length == 1 ? Messages.Found_0_errors_in_the_same_file_starting_at_Colon_1.Format(locale, Count(errors.Length), first)
            : Messages.Found_0_errors_in_1_files.Format(locale, Count(errors.Length), Count(groups.Length));
        writer.Write('\n'); writer.Write(message.ToString()); writer.Write("\n\n");
        if (groups.Length <= 1) return;
        string header = Messages.Errors_Files.Format(locale).ToString();
        int headingWidth = header.IndexOf(' '), countWidth = groups.Max(group => group.Count()).ToString(CultureInfo.InvariantCulture).Length;
        int width = Math.Max(headingWidth, countWidth);
        writer.Write(new string(' ', Math.Max(0, countWidth - headingWidth)) + header + "\n");
        foreach (var group in groups) writer.Write(group.Count().ToString(CultureInfo.InvariantCulture).PadLeft(width) + "  " + Path(group.First()).ToString() + "\n");
        writer.Write('\n');
    }
}

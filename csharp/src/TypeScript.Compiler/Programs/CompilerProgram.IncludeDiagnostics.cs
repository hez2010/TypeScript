using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Programs;

public sealed partial class CompilerProgram
{
    private Diagnostic ExplainIncludeDiagnostic(Diagnostic diagnostic, Utf8String path, FileIncludeReason? preferred = null)
    {
        IReadOnlyList<FileIncludeReason> reasons = path.IsEmpty ? preferred is null ? [] : [preferred]
            : diagnostic.Code is DiagnosticCode.AlreadyIncludedFileName0DiffersFromFileName1OnlyInCasing or DiagnosticCode.FileName0DiffersFromAlreadyIncludedFileName1OnlyInCasing
                ? IncludeReasons.Where(pair => pair.Key.Equals(path, StringComparison.OrdinalIgnoreCase)).SelectMany(pair => pair.Value).Distinct().ToArray()
                : IncludeReasons.GetValueOrDefault(path) ?? [];
        bool HasLocation(FileIncludeReason reason) => reason.ContainingFile.Length != 0 && reason.Length > 0;
        if (preferred is null || !HasLocation(preferred)) preferred = reasons.FirstOrDefault(HasLocation);
        var chain = new List<Diagnostic>();
        if (reasons.Count != 0 && (preferred is null || reasons.Count != 1))
            chain.Add(new(Messages.The_file_is_in_the_program_because_Colon, 0, 0, [])
            { MessageChain = reasons.Select(reason => IncludeReasonDiagnostic(reason, path.IsEmpty ? reason.FileName : path)).ToArray() });
        var related = reasons.Where(reason => reason != preferred).Select(RelatedIncludeDiagnostic).OfType<Diagnostic>().ToArray();
        if (preferred is not null)
        {
            var span = ReferenceSpan(preferred);
            diagnostic = diagnostic with { FileName = preferred.ContainingFile, Start = span.Start, Length = span.Length };
        }
        return diagnostic with { MessageChain = chain, RelatedInformation = related };
    }

    private (int Start, int Length) ReferenceSpan(FileIncludeReason reason)
    {
        int start = reason.Position, end = reason.Position + reason.Length;
        if (reason.Kind == FileIncludeKind.Import && GetFile(reason.ContainingFile) is { } file)
        {
            var scanner = new Scanner(file.Syntax.Source);
            scanner.ResetPosition(Math.Max(0, start)); scanner.Scan(); start = scanner.TokenStart;
        }
        return (start, Math.Max(0, end - start));
    }

    internal Diagnostic IncludeReasonDiagnostic(FileIncludeReason reason, Utf8String path, bool relative = false)
    {
        Utf8String FileName(Utf8String value) => relative ? CompilerPath.Relative(CurrentDirectory, value, UseCaseSensitiveFileNames) : value;
        Diagnostic Message(DiagnosticMessage message, params Utf8String[] args) => new(message, 0, 0, args);
        var config = Configuration;
        var containing = GetFile(reason.ContainingFile);
        var import = containing?.Resolutions.FirstOrDefault(item => item.Resolution.FileName == path && (item.Node is null || item.Node.Pos == reason.Position));
        var text = containing is not null && reason.Length > 0
            ? containing.Syntax.Source.Text[reason.Position..(reason.Position + reason.Length)].Trim() : import?.Specifier ?? reason.Specifier;
        var package = reason.PackageId ?? import?.Resolution.PackageId;
        switch (reason.Kind)
        {
            case FileIncludeKind.Root:
                if (!config.FileName.IsEmpty)
                {
                    var directory = CompilerPath.DirectoryName(config.FileName);
                    if (config.LiteralFiles.Any(name => files.Comparer.Equals(CompilerPath.Resolve(directory, name), reason.FileName)))
                        return Message(Messages.Part_of_files_list_in_tsconfig_json);
                    var include = config.DisplayIncludes.FirstOrDefault(pattern => new FilePattern(CompilerPath.Resolve(directory, pattern), UseCaseSensitiveFileNames).Matches(reason.FileName));
                    if (!include.IsEmpty) return include == "**/*"u8 ? Message(Messages.Matched_by_default_include_pattern_Asterisk_Asterisk_Slash_Asterisk)
                        : Message(Messages.Matched_by_include_pattern_0_in_1, include, FileName(config.FileName));
                }
                return Message(Messages.Root_file_specified_for_compilation);
            case FileIncludeKind.Import:
                return package is not null ? Message(Messages.Imported_via_0_from_file_1_with_packageId_2, text, FileName(reason.ContainingFile), package.ToUtf8String())
                    : Message(Messages.Imported_via_0_from_file_1, text, FileName(reason.ContainingFile));
            case FileIncludeKind.PathReference:
                return Message(Messages.Referenced_via_0_from_file_1, text, FileName(reason.ContainingFile));
            case FileIncludeKind.TypeReference:
                return package is not null ? Message(Messages.Type_library_referenced_via_0_from_file_1_with_packageId_2, text, FileName(reason.ContainingFile), package.ToUtf8String())
                    : Message(Messages.Type_library_referenced_via_0_from_file_1, text, FileName(reason.ContainingFile));
            case FileIncludeKind.AutomaticType:
                bool wildcard = config.Options.Types?.Contains("*"u8) == true;
                return package is not null ? Message(wildcard ? Messages.Entry_point_for_implicit_type_library_0_with_packageId_1
                    : Messages.Entry_point_of_type_library_0_specified_in_compilerOptions_with_packageId_1, text, package.ToUtf8String())
                    : Message(wildcard ? Messages.Entry_point_for_implicit_type_library_0 : Messages.Entry_point_of_type_library_0_specified_in_compilerOptions, text);
            case FileIncludeKind.Library:
                if (!reason.ContainingFile.IsEmpty) return Message(Messages.Library_referenced_via_0_from_file_1, text, FileName(reason.ContainingFile));
                if (config.Options.Lib is not null) return Message(Messages.Library_0_specified_in_compilerOptions,
                    reason.Specifier.IsEmpty ? CompilerPath.BaseName(path) : reason.Specifier.StartsWith("lib."u8, StringComparison.Ordinal)
                        ? reason.Specifier : "lib."u8 + reason.Specifier + ".d.ts"u8);
                return Message(Messages.Default_library_for_target_0, config.Options.EmitTargetYear == int.MaxValue ? "ESNext"u8
                    : config.Options.EmitTargetYear == 2009 ? "ES5"u8 : "ES"u8 + Utf8String.Format(config.Options.EmitTargetYear));
            case FileIncludeKind.MapperSupplemental:
                return Message(Messages.Supplemental_virtual_file_produced_by_the_content_mapper_for_file_0, FileName(reason.ContainingFile));
            default:
                return Message(Messages.File_is_output_of_project_reference_source_0, FileName(reason.ContainingFile));
        }
    }

    internal IReadOnlyList<FileIncludeReason> ReasonsForFile(Utf8String path) => IncludeReasons.GetValueOrDefault(path)
        ?? (ProjectReferences.Outputs.TryGetValue(path, out var redirect) ? IncludeReasons.GetValueOrDefault(redirect.Source) : null) ?? [];

    private Diagnostic? RelatedIncludeDiagnostic(FileIncludeReason reason)
    {
        if (!reason.ContainingFile.IsEmpty && reason.Length > 0)
        {
            var span = ReferenceSpan(reason);
            return new(reason.Kind switch
            {
                FileIncludeKind.Import => Messages.File_is_included_via_import_here,
                FileIncludeKind.TypeReference => Messages.File_is_included_via_type_library_reference_here,
                FileIncludeKind.Library => Messages.File_is_included_via_library_reference_here,
                _ => Messages.File_is_included_via_reference_here
            }, span.Start, span.Length, []) { FileName = reason.ContainingFile };
        }
        if (Configuration.SourceFile is not { } source) return null;
        var root = (source.Statements?.FirstOrDefault() as ExpressionStatementNode)?.Expression;
        SyntaxNode? node = null;
        DiagnosticMessage? message = null;
        if (reason.Kind == FileIncludeKind.Root)
        {
            var directory = CompilerPath.DirectoryName(Configuration.FileName);
            var fileList = Property(root, "files"u8) as ArrayLiteralExpressionNode;
            node = fileList?.Elements?.OfType<StringLiteralNode>().FirstOrDefault(text => files.Comparer.Equals(CompilerPath.Resolve(directory, text.Text), reason.FileName));
            message = Messages.File_is_matched_by_files_list_specified_here;
            if (node is null)
            {
                node = (Property(root, "include"u8) as ArrayLiteralExpressionNode)?.Elements?.OfType<StringLiteralNode>()
                    .FirstOrDefault(text => new FilePattern(CompilerPath.Resolve(directory, text.Text), UseCaseSensitiveFileNames).Matches(reason.FileName));
                message = Messages.File_is_matched_by_include_pattern_specified_here;
            }
        }
        if (node is null || message is null) return null;
        var scanner = new Scanner(source.Source); scanner.ResetPosition(Math.Max(0, node.Pos)); scanner.Scan();
        return new(message, scanner.TokenStart, node.End - scanner.TokenStart, []) { FileName = source.FileName };
    }

    private static SyntaxNode? Property(SyntaxNode? node, Utf8String name) => (node as ObjectLiteralExpressionNode)?.Properties?
        .OfType<PropertyAssignmentNode>().FirstOrDefault(property => property.Name is StringLiteralNode text && text.Text == name
            || property.Name is IdentifierNode id && id.Text == name)?.Initializer;

    private sealed partial class Builder
    {
        private Diagnostic MapperOptionDiagnostic(MapperOptionDiagnostic diagnostic)
        {
            var source = diagnostic.Mapper.SourceFile;
            var node = diagnostic.Mapper.OptionsSyntax;
            foreach (var segment in diagnostic.Path)
            {
                var next = segment.ValueKind == System.Text.Json.JsonValueKind.String ? Property(node, JsonStrings.GetString(segment))
                    : segment.TryGetInt32(out int index) && node is ArrayLiteralExpressionNode { Elements: { } elements }
                        && (uint)index < (uint)elements.Count ? elements[index] : null;
                if (next is null) break;
                node = next;
            }
            int start = 0, length = 0;
            if (node is not null && source is not null)
            {
                var scanner = new Scanner(source.Source); scanner.ResetPosition(Math.Max(0, node.Pos)); scanner.Scan();
                start = scanner.TokenStart; length = node.End - start;
            }
            return new(new(diagnostic.Code, DiagnosticCategory.Error, default, diagnostic.Message), start, length, [])
            { FileName = source?.FileName, Source = diagnostic.Source };
        }
    }
}

using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    public async ValueTask<IReadOnlyList<WorkspaceDocumentChange>> GetEditsForFileRenameAsync(ProjectSnapshot project, Utf8String oldPath,
        Utf8String newPath, UserPreferences? preferences = null, CancellationToken cancellation = default)
    {
        using var request = new ProjectRequest(cancellation);
        using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request);
        return await new FileRename(this, project.Program!, lease.Checker, oldPath, newPath, preferences ?? new(), cancellation).RunAsync();
    }

    private sealed class FileRename(LanguageServiceDocument service, CompilerProgram program, Checker checker, Utf8String oldPath,
        Utf8String newPath, UserPreferences preferences, CancellationToken cancellation)
    {
        private readonly Dictionary<Utf8String, List<(SourceFileNode File, DocumentTextEdit Edit)>> changes = [];
        private readonly HashSet<Utf8String> unmappable = [];
        private readonly ModuleSpecifierPreferences specifierPreferences = new(preferences.ImportModuleSpecifierPreference, preferences.ImportModuleSpecifierEnding);
        private StringComparison Comparison => program.UseCaseSensitiveFileNames ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        internal async ValueTask<IReadOnlyList<WorkspaceDocumentChange>> RunAsync()
        {
            await ConfigAsync();
            var moved = program.SourceFiles.Select(file => (File: file.Syntax, NewName: Update(service.ReferenceOriginalFileName(program, file.Syntax))))
                .Where(item => item.NewName is not null).ToArray();
            foreach (var source in program.SourceFiles)
            {
                cancellation.ThrowIfCancellationRequested();
                var file = source.Syntax;
                var original = service.ReferenceOriginalFileName(program, file);
                var updated = Update(original);
                var importingPath = updated ?? original;
                foreach (var reference in file.ReferencedFiles)
                {
                    if (!ModuleResolver.Relative(reference.FileName)) continue;
                    var text = UpdateRelative(original, importingPath, reference.FileName);
                    if (text != reference.FileName) Add(file, reference.Pos, reference.End, text);
                }
                foreach (var import in file.Imports)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (await checker.GetSymbolAtLocationAsync(import, cancellation) is { } symbol
                        && symbol.Declarations.Any(node => node is ModuleDeclarationNode { Name: StringLiteralNode })) continue;
                    var oldSpecifier = ReferenceNavigation.Text(import);
                    var resolved = source.Resolutions.FirstOrDefault(item => item.Node == import)?.Resolution;
                    Utf8String text = default;
                    if (resolved is { IsResolved: true })
                    {
                        var target = Update(resolved.FileName);
                        if (target is null && !(updated is not null && ModuleResolver.Relative(oldSpecifier))) continue;
                        text = program.UpdateModuleSpecifier(file, import, importingPath, target ?? resolved.FileName, specifierPreferences, cancellation);
                    }
                    else
                    {
                        foreach (var candidate in moved)
                            if (program.UpdateModuleSpecifier(file, import, importingPath, candidate.File.FileName, specifierPreferences, cancellation) == oldSpecifier)
                            {
                                text = program.UpdateModuleSpecifier(file, import, importingPath, candidate.NewName!.Value, specifierPreferences, cancellation);
                                break;
                            }
                        if ((text.IsEmpty || text == oldSpecifier) && ModuleResolver.Relative(oldSpecifier)) text = UpdateRelative(file.FileName, importingPath, oldSpecifier);
                    }
                    if (!text.IsEmpty && text != oldSpecifier)
                        Add(file, await SyntaxNavigation.GetStartAsync(import, file, cancellation: cancellation) + 1, import.End - 1, text);
                }
            }
            List<WorkspaceDocumentChange> result = [];
            if (CompilerPath.IsDeclarationFile(oldPath) && CompilerPath.IsDeclarationFile(newPath))
            {
                var extension = RenameExtension(oldPath);
                Utf8String[] originals = extension == ".d.mts"u8 ? [".mts"u8, ".mjs"u8] : extension == ".d.cts"u8 ? [".cts"u8, ".cjs"u8]
                    : extension != ".d.ts"u8 ? ["."u8 + extension[3..^3]] : [".tsx"u8, ".ts"u8, ".jsx"u8, ".js"u8];
                foreach (var original in originals)
                {
                    var oldOriginal = oldPath[..^extension.Length] + original;
                    if (program.FileSystem.FileExists(oldOriginal))
                        result.Add(new RenameFileChange(DocumentUris.FromFileName(oldOriginal), DocumentUris.FromFileName(newPath[..^RenameExtension(newPath).Length] + original)));
                }
            }
            foreach (var (file, edits) in changes)
            {
                if (unmappable.Contains(file)) continue;
                bool multiple = edits.Select(item => item.File).Distinct().Skip(1).Any();
                var ordered = edits.Select(item => item.Edit).OrderBy(item => item.Range.Start.Line).ThenBy(item => item.Range.Start.Character)
                    .ThenBy(item => item.Range.End.Line).ThenBy(item => item.Range.End.Character);
                var items = (multiple ? ordered.Distinct() : ordered).ToArray();
                bool conflict = false;
                for (int i = 0; i + 1 < items.Length; i++)
                    if (Compare(items[i].Range.End, items[i + 1].Range.Start) > 0 || multiple && items[i].Range.Start == items[i].Range.End
                        && items[i].Range == items[i + 1].Range && items[i].NewText != items[i + 1].NewText) { conflict = true; break; }
                if (conflict)
                {
                    if (multiple) continue;
                    throw new InvalidOperationException("File rename edits overlap");
                }
                result.Add(new TextDocumentChange(DocumentUris.FromFileName(file), items));
            }
            return result;
        }

        private Utf8String? Update(Utf8String path)
        {
            if (CompilerPath.Normalize(path).Equals(CompilerPath.Normalize(oldPath), Comparison)) return newPath;
            var trimmed = CompilerPath.RemoveTrailingSeparator(CompilerPath.NormalizeSlashes(oldPath));
            var candidate = CompilerPath.NormalizeSlashes(path);
            // Compare components, retaining the candidate's byte offset when case folding changes UTF-8 length.
            int offset = 0;
            foreach (var component in trimmed.Split((byte)'/'))
            {
                int end = candidate.IndexOf((byte)'/', offset);
                if (end < 0 || ModuleSpecifierGenerator.CanonicalFileName(candidate[offset..end], program.UseCaseSensitiveFileNames)
                    != ModuleSpecifierGenerator.CanonicalFileName(component, program.UseCaseSensitiveFileNames)) return null;
                offset = end + 1;
            }
            return newPath + path[(offset - 1)..];
        }
        private Utf8String Relative(Utf8String fromDirectory, Utf8String target) => CompilerPath.Relative(fromDirectory, target, program.UseCaseSensitiveFileNames);
        private Utf8String UpdateRelative(Utf8String oldImporter, Utf8String newImporter, Utf8String specifier)
        {
            var absolute = CompilerPath.Normalize(CompilerPath.Combine(CompilerPath.DirectoryName(oldImporter), specifier));
            return ModuleSpecifierPaths.NonModulePath(Relative(CompilerPath.DirectoryName(newImporter), Update(absolute) ?? absolute));
        }
        private void Add(SourceFileNode file, int start, int end, Utf8String text)
        {
            var projection = service.ProjectionForFile(program, file);
            var mapped = projection.ToRange(start, end);
            if (mapped.Fidelity != MappingFidelity.Exact) { unmappable.Add(projection.OriginalFileName); return; }
            if (!changes.TryGetValue(projection.OriginalFileName, out var edits)) changes.Add(projection.OriginalFileName, edits = []);
            edits.Add((file, new(mapped.Range, text)));
        }
        private static int Compare(DocumentPosition a, DocumentPosition b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Character.CompareTo(b.Character);

        private async ValueTask ConfigAsync()
        {
            var config = program.Configuration;
            if (config.SourceFile is not { Statements.Count: > 0 } file || file.Statements[0] is not ExpressionStatementNode { Expression: ObjectLiteralExpressionNode root }) return;
            var directory = CompilerPath.DirectoryName(file.FileName);
            foreach (var property in root.Properties?.OfType<PropertyAssignmentNode>() ?? [])
            {
                var name = ReferenceNavigation.Text(property.Name!);
                if (name == "files"u8 || name == "include"u8 || name == "exclude"u8)
                {
                    bool found = await UpdatePropertyAsync(property);
                    if (!found && name == "include"u8 && property.Initializer is ArrayLiteralExpressionNode { Elements.Count: > 0 } array
                        && config.Includes.Any(pattern => new FilePattern(pattern, program.UseCaseSensitiveFileNames).Matches(oldPath))
                        && !config.Includes.Any(pattern => new FilePattern(pattern, program.UseCaseSensitiveFileNames).Matches(newPath)))
                    {
                        var last = array.Elements[^1];
                        var literal = new StringLiteralNode { Text = Relative(directory, newPath), Pos = -1, End = -1 };
                        int trivia = new Scanner(file.Source).SkipTriviaAt(last.End, stopAfterLineBreak: true);
                        int end = trivia != last.End && file.Source.Text[trivia - 1] is (byte)'\r' or (byte)'\n' ? trivia : last.End;
                        var text = await SourceFormatter.FormatNodeForInsertionAsync(literal, file, end, preferences.FormatCodeSettings, cancellation);
                        if (file.Source.LineStarts[file.Source.GetLineAndCharacter(end).Line] != end) text = new(text.Span.TrimStart(" \t\r\n"u8));
                        Add(file, end, end, ", "u8 + text);
                    }
                }
                else if (name == "compilerOptions"u8 && property.Initializer is ObjectLiteralExpressionNode options)
                    foreach (var option in options.Properties?.OfType<PropertyAssignmentNode>() ?? [])
                    {
                        var optionName = ReferenceNavigation.Text(option.Name!);
                        var definition = OptionDefinitions.Find(optionName);
                        if (definition?.IsFilePath == true || definition is { Kind: OptionKind.List, ElementIsFilePath: true }) await UpdatePropertyAsync(option);
                        else if (optionName == "paths"u8 && option.Initializer is ObjectLiteralExpressionNode paths)
                            foreach (var mapping in paths.Properties?.OfType<PropertyAssignmentNode>() ?? [])
                                if (mapping.Initializer is ArrayLiteralExpressionNode) await UpdatePropertyAsync(mapping);
                    }
            }
            async ValueTask<bool> UpdatePropertyAsync(PropertyAssignmentNode property)
            {
                bool found = false;
                IEnumerable<SyntaxNode> elements = property.Initializer is ArrayLiteralExpressionNode { Elements: { } list } ? list : [property.Initializer!];
                foreach (var node in elements)
                {
                    if (node is not StringLiteralNode str || Update(CompilerPath.Normalize(CompilerPath.Combine(directory, str.Text))) is not { } updated) continue;
                    found = true;
                    Add(file, await SyntaxNavigation.GetStartAsync(str, file, cancellation: cancellation) + 1, str.End - 1, Relative(directory, updated));
                }
                return found;
            }
        }
    }
}

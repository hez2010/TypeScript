using TypeScript.Compiler.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Programs;

public sealed partial class CompilerProgram
{
    private IEnumerable<Diagnostic> ModulePathOptionDiagnostics()
    {
        var options = Configuration.Options;
        var source = Configuration.SourceFile;
        var root = (source?.Statements?.FirstOrDefault() as ExpressionStatementNode)?.Expression as ObjectLiteralExpressionNode;
        var compilerProperty = Property(root, Utf8Literals.CompilerOptions);
        var optionSyntax = compilerProperty?.Initializer as ObjectLiteralExpressionNode;
        if (options.Get(Utf8Literals.Paths) is { ValueKind: JsonValueKind.Object } paths)
            foreach (var entry in PackageJson.Properties(paths))
            {
                if (entry.Value.ValueKind != JsonValueKind.Array)
                    continue;
                int index = 0;
                foreach (var value in entry.Value.EnumerateArray())
                {
                    Utf8String text = value.ValueKind == JsonValueKind.String ? JsonStrings.GetString(value) : Utf8String.Empty;
                    if (!(text == "."u8 || text == ".."u8
                        || text.StartsWith("./"u8, StringComparison.Ordinal)
                        || text.StartsWith("../"u8, StringComparison.Ordinal)
                        || CompilerPath.IsAbsolute(text)))
                    {
                        var pathSyntax = Property(
                            Property(optionSyntax, Utf8Literals.Paths)?.Initializer as ObjectLiteralExpressionNode,
                            JsonStrings.GetName(entry))?.Initializer as ArrayLiteralExpressionNode;
                        SyntaxNode? node = pathSyntax?.Elements is { } elements && index < elements.Count ? elements[index] : null;
                        yield return Create(Messages.Non_relative_paths_are_not_allowed_Did_you_forget_a_leading_Slash, node);
                    }
                    index++;
                }
            }
        bool declarations = options.Declaration == true || options.Composite == true;
        if (options.DeclarationDir is { Length: > 0 } && !declarations)
            yield return Create(Messages.Option_0_cannot_be_specified_without_specifying_option_1_or_option_2,
                Property(optionSyntax, Utf8Literals.DeclarationDir, Utf8Literals.DeclarationOption)?.Name, Utf8Literals.DeclarationDir, Utf8Literals.DeclarationOption, Utf8Literals.Composite);

        if (options.NoEmit == true || options.Composite == true || options.RootDir is { Length: > 0 }
            || Configuration.FileName.Length == 0 || !(options.OutDir is { Length: > 0 }
                || declarations && options.DeclarationDir is { Length: > 0 } || options.OutFile is { Length: > 0 }))
            yield break;
        Utf8String[] emitted = SourceFiles.Where(f => SourceFileMayBeEmitted(f.Syntax)).Select(f => f.Syntax.FileName).ToArray();
        Utf8String computed = emitted.Length == 0 ? CurrentDirectory : ProjectReferences.CommonDirectory(emitted, fileSystem.CaseSensitive);
        if (computed.Length == 0)
            yield break;
        computed = CompilerPath.EnsureTrailingSeparator(computed);
        if (ModuleSpecifierGenerator.CanonicalFileName(computed, fileSystem.CaseSensitive)
            == ModuleSpecifierGenerator.CanonicalFileName(CommonSourceDirectory, fileSystem.CaseSensitive))
            yield break;
        Utf8String option = options.OutFile is { Length: > 0 }
            ? Utf8Literals.OutFile
            : options.OutDir is { Length: > 0 } ? Utf8Literals.OutDir : Utf8Literals.DeclarationDir;
        Utf8String relative = ModuleSpecifierPaths.NonModulePath(
            CompilerPath.Relative(CompilerPath.DirectoryName(Configuration.FileName), computed, fileSystem.CaseSensitive));
        yield return Create(
            Messages.The_common_source_directory_of_0_is_1_The_rootDir_setting_must_be_explicitly_set_to_this_or_another_path_to_adjust_your_output_s_file_layout,
            Property(optionSyntax, option, option == Utf8Literals.OutDir ? Utf8Literals.DeclarationDir : Utf8String.Empty)?.Name,
            CompilerPath.BaseName(Configuration.FileName),
            relative) with
        {
            MessageChain = [new(Messages.Visit_https_Colon_Slash_Slashaka_ms_Slashts6_for_migration_information, -1, 0, [])]
        };

        Diagnostic Create(DiagnosticMessage message, SyntaxNode? node, params Utf8String[] arguments)
        {
            node ??= compilerProperty?.Name;
            if (node is null || source is null)
                return new(message, -1, 0, arguments);
            var scanner = new Scanner(source.Source);
            scanner.ResetPosition(Math.Max(0, node.Pos));
            scanner.Scan();
            int start = scanner.TokenStart;
            return new(message, start, Math.Max(0, node.End - start), arguments) { FileName = source.FileName };
        }
    }

    private static PropertyAssignmentNode? Property(ObjectLiteralExpressionNode? obj, Utf8String name, Utf8String alternate = default) =>
        obj?.Properties?.OfType<PropertyAssignmentNode>().FirstOrDefault(p =>
            p.Name is StringLiteralNode text && (text.Text == name || text.Text == alternate)
            || p.Name is IdentifierNode identifier && (identifier.Text == name || identifier.Text == alternate));
}

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
        var compilerProperty = Property(root, "compilerOptions");
        var optionSyntax = compilerProperty?.Initializer as ObjectLiteralExpressionNode;
        if (options.Get("paths") is { ValueKind: JsonValueKind.Object } paths)
            foreach (var entry in PackageJson.Properties(paths))
            {
                if (entry.Value.ValueKind != JsonValueKind.Array)
                    continue;
                int index = 0;
                foreach (var value in entry.Value.EnumerateArray())
                {
                    string text = value.ValueKind == JsonValueKind.String ? JsonStrings.GetString(value) : "";
                    if (!(text is "." or ".."
                        || text.StartsWith("./", StringComparison.Ordinal)
                        || text.StartsWith("../", StringComparison.Ordinal)
                        || CompilerPath.IsAbsolute(text)))
                    {
                        var pathSyntax = Property(
                            Property(optionSyntax, "paths")?.Initializer as ObjectLiteralExpressionNode,
                            entry.Name)?.Initializer as ArrayLiteralExpressionNode;
                        SyntaxNode? node = pathSyntax?.Elements is { } elements && index < elements.Count ? elements[index] : null;
                        yield return Create(Messages.Non_relative_paths_are_not_allowed_Did_you_forget_a_leading_Slash, node);
                    }
                    index++;
                }
            }
        bool declarations = options.Boolean("declaration") == true || options.Boolean("composite") == true;
        if (options.String("declarationDir") is { Length: > 0 } && !declarations)
            yield return Create(Messages.Option_0_cannot_be_specified_without_specifying_option_1_or_option_2,
                Property(optionSyntax, "declarationDir", "declaration")?.Name, "declarationDir", "declaration", "composite");

        if (options.Boolean("noEmit") == true || options.Boolean("composite") == true || options.String("rootDir") is { Length: > 0 }
            || Configuration.FileName.Length == 0 || !(options.String("outDir") is { Length: > 0 }
                || declarations && options.String("declarationDir") is { Length: > 0 } || options.String("outFile") is { Length: > 0 }))
            yield break;
        string[] emitted = SourceFiles.Where(f => SourceFileMayBeEmitted(f.Syntax)).Select(f => f.Syntax.FileName).ToArray();
        string computed = emitted.Length == 0 ? CurrentDirectory : ProjectReferences.CommonDirectory(emitted, fileSystem.CaseSensitive);
        if (computed.Length == 0)
            yield break;
        computed = CompilerPath.EnsureTrailingSeparator(computed);
        if (ModuleSpecifierGenerator.CanonicalFileName(computed, fileSystem.CaseSensitive)
            == ModuleSpecifierGenerator.CanonicalFileName(CommonSourceDirectory, fileSystem.CaseSensitive))
            yield break;
        string option = options.String("outFile") is { Length: > 0 }
            ? "outFile"
            : options.String("outDir") is { Length: > 0 } ? "outDir" : "declarationDir";
        string relative = ModuleSpecifierPaths.NonModulePath(
            CompilerPath.Relative(CompilerPath.DirectoryName(Configuration.FileName), computed, fileSystem.CaseSensitive));
        yield return Create(
            Messages.The_common_source_directory_of_0_is_1_The_rootDir_setting_must_be_explicitly_set_to_this_or_another_path_to_adjust_your_output_s_file_layout,
            Property(optionSyntax, option, option == "outDir" ? "declarationDir" : "")?.Name,
            CompilerPath.BaseName(Configuration.FileName),
            relative) with
        {
            MessageChain = [new(Messages.Visit_https_Colon_Slash_Slashaka_ms_Slashts6_for_migration_information, -1, 0, [])]
        };

        Diagnostic Create(DiagnosticMessage message, SyntaxNode? node, params TextSlice[] arguments)
        {
            node ??= compilerProperty?.Name;
            if (node is null || source is null)
                return new(message, -1, 0, arguments);
            var scanner = new Scanner(source.Source);
            scanner.ResetPosition(source.Source.ToUtf16Position(Math.Max(0, node.Pos)));
            scanner.Scan();
            int start = source.Source.ToBytePosition(scanner.TokenStart);
            return new(message, start, Math.Max(0, node.End - start), arguments) { FileName = source.FileName };
        }
    }

    private static PropertyAssignmentNode? Property(ObjectLiteralExpressionNode? obj, string name, string alternate = "") =>
        obj?.Properties?.OfType<PropertyAssignmentNode>().FirstOrDefault(p =>
            p.Name is StringLiteralNode text && (text.Text == name || text.Text == alternate)
            || p.Name is IdentifierNode identifier && (identifier.Text == name || identifier.Text == alternate));
}

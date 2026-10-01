using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

internal sealed partial class DeclarationTransformer
{
    private IReadOnlyList<FileReference> References(SourceFileNode source)
    {
        var program = checker.Symbols.Program;
        var outputDirectory = CompilerPath.DirectoryName(declarationFilePath.Length == 0 ? program.DeclarationOutputPath(source.FileName) : declarationFilePath);
        List<FileReference> result = [];
        foreach (var reference in source.ReferencedFiles)
        {
            Cancellation.ThrowIfCancellationRequested();
            if (!reference.Preserve || program.SourceFromReference(source, reference) is not { } target) continue;
            var path = target.IsDeclarationFile ? target.FileName : program.GetOutputPaths(target, forceDeclarations: true).Declaration;
            result.Add(reference with { FileName = CompilerPath.Relative(CompilerPath.Resolve(program.CurrentDirectory, outputDirectory),
                CompilerPath.Resolve(program.CurrentDirectory, path), program.UseCaseSensitiveFileNames), Pos = -1, End = -1 });
        }
        return result;
    }
}

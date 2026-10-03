using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Programs;

public sealed partial class CompilerProgram
{
    // A single-file replacement does not change the reference server's project diagnostic
    // publication state. Preserve that distinction even though this host rebuilds resolution.
    internal bool CanReplaceFileForDiagnostics(CompilerProgram next, Utf8String path)
    {
        if (GetFileByPath(path) is not { } before || next.GetFileByPath(path) is not { } after
            || Redirects.ContainsKey(path) || Redirects.Values.Contains(path)) return false;
        if (!Replace(before, after) || !before.SupplementalSourceFiles.SequenceEqual(after.SupplementalSourceFiles)) return false;
        return before.SupplementalSourceFiles.All(name => GetFile(name) is { } first && next.GetFile(name) is { } second && Replace(first, second));

        bool Replace(ProgramFile first, ProgramFile second)
        {
            var a = first.Syntax; var b = second.Syntax;
            if (first.ParseOptions != second.ParseOptions || a.ScriptKind != b.ScriptKind || first.Binding.IsModule != second.Binding.IsModule
                || first.Resolutions.Any(SyntheticImport) || second.Resolutions.Any(SyntheticImport)
                || !a.AmbientModuleNames.SequenceEqual(b.AmbientModuleNames) || a.CheckJsDirective?.Enabled != b.CheckJsDirective?.Enabled) return false;
            if (a.Imports.Count != b.Imports.Count || a.ModuleAugmentations.Count != b.ModuleAugmentations.Count) return false;
            for (int i = 0; i < a.Imports.Count; i++)
                if (a.Imports[i].Kind != b.Imports[i].Kind || a.Imports[i] is StringLiteralNode oldText && oldText.Text != ((StringLiteralNode)b.Imports[i]).Text
                    || ResolutionModeForUsage(a, a.Imports[i]) != next.ResolutionModeForUsage(b, b.Imports[i])) return false;
            for (int i = 0; i < a.ModuleAugmentations.Count; i++)
                if (a.ModuleAugmentations[i].Kind != b.ModuleAugmentations[i].Kind
                    || AugmentationName(a.ModuleAugmentations[i]) != AugmentationName(b.ModuleAugmentations[i])) return false;
            return References(a.ReferencedFiles, b.ReferencedFiles) && References(a.TypeReferenceDirectives, b.TypeReferenceDirectives)
                && References(a.LibReferenceDirectives, b.LibReferenceDirectives);
        }
        static Utf8String AugmentationName(SyntaxNode node) => node is StringLiteralNode text ? text.Text : SyntaxNameText.Get(node);
        static bool SyntheticImport(ModuleReference value) => value.Node is null && !value.TypeReference && !value.Augmentation;
        static bool References(IReadOnlyList<FileReference> first, IReadOnlyList<FileReference> second) =>
            first.Select(reference => (reference.FileName, reference.ResolutionMode, reference.Preserve))
                .SequenceEqual(second.Select(reference => (reference.FileName, reference.ResolutionMode, reference.Preserve)));
    }

    internal IReadOnlyList<Diagnostic> ProgramDiagnostics => Diagnostics.Where(diagnostic =>
        !Configuration.Diagnostics.Contains(diagnostic, DiagnosticEqualityComparer.Instance)).ToArray();

    internal IReadOnlyList<Diagnostic> SyntacticDiagnostics(SourceFileNode source, CancellationToken cancellation)
    {
        var diagnostics = source.ParseDiagnostics.Concat(source.JSDiagnostics)
            .Select(diagnostic => diagnostic with { FileName = source.FileName }).ToList();
        if ((source.Flags & NodeFlags.JavaScriptFile) != 0
            && !(source.CheckJsDirective?.Enabled ?? Configuration.Options.CheckJs ?? false)
            && Configuration.Options.ExperimentalDecorators != true)
            foreach (var node in source.DescendantsAndSelf())
            {
                cancellation.ThrowIfCancellationRequested();
                if (node is ParameterDeclarationNode parameter
                    && parameter.Modifiers?.FirstOrDefault(modifier => modifier is DecoratorNode) is { } decorator)
                    diagnostics.Add(new(Messages.Decorators_are_not_valid_here, decorator.Pos, decorator.End - decorator.Pos, [])
                    { FileName = source.FileName });
            }
        return DiagnosticCollection.SortAndDeduplicate(diagnostics);
    }
}

using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask<Symbol?> ResolveImportModuleAsync(SyntaxNode location, SyntaxNode? specifier, Type? attributes,
        CancellationToken cancellation, bool implicitImport = false, int missingModuleCode = 2307)
    {
        cancellation.ThrowIfCancellationRequested();
        string? name = specifier switch
        {
            StringLiteralNode text => text.Text,
            NoSubstitutionTemplateLiteralNode template => template.Text,
            _ => null
        };
        if (name is null)
            return null;
        if (name.StartsWith("@types/", StringComparison.Ordinal))
            Error(specifier!, 6137);
        var file = program.Symbols.Binding(location)!.SourceFile;
        var reference = program.Symbols.Program.GetFile(file.FileName)!.Resolutions.FirstOrDefault(
            r => implicitImport ? r.Node is null && r.Specifier == name : r.Node == specifier);
        var module = program.Symbols.Globals.GetValueOrDefault('"' + name + '"');
        if (module is null && reference?.Resolution.IsResolved == true)
        {
            if (!implicitImport)
                CheckResolvedImport(location, specifier!, name, file, reference);
            module = program.Symbols.Program.GetFile(reference.Resolution.FileName)?.Binding.Symbol;
        }
        attributes ??= context.EmptyObjectType;
        if (module is null || !await Views.EmptyAnonymousAsync(attributes, cancellation))
        {
            var candidates = new List<(PatternModule Module, Type Type)>();
            foreach (var pattern in program.Symbols.PatternModules)
            {
                int star = pattern.Pattern.IndexOf('*');
                if (star < 0 || name.Length < pattern.Pattern.Length - 1
                    || !name.StartsWith(pattern.Pattern[..star], StringComparison.Ordinal)
                    || !name.EndsWith(pattern.Pattern[(star + 1)..], StringComparison.Ordinal))
                    continue;
                var required = await ModuleImportAttributesAsync(pattern.Symbol, cancellation);
                if (await AssignableAsync(attributes, required, cancellation))
                    candidates.Add((pattern, required));
            }
            var best = new List<PatternModule>();
            for (int i = 0; i < candidates.Count; i++)
            {
                bool wider = false;
                for (int j = 0; j < candidates.Count; j++)
                    if (i != j
                        && await Relations.RelatedAsync(candidates[j].Type, candidates[i].Type, RelationKind.StrictSubtype, cancellation)
                        && !await Relations.RelatedAsync(candidates[j].Type, candidates[i].Type, RelationKind.Identity, cancellation))
                    {
                        wider = true;
                        break;
                    }
                if (!wider)
                    best.Add(candidates[i].Module);
            }
            if (best.Count != 0)
            {
                var pattern = best.OrderByDescending(p => p.Pattern.IndexOf('*')).First();
                var target = program.Symbols.Merger.GetMergedSymbol(pattern.Symbol)!;
                module = program.Symbols.PatternTargets.GetValueOrDefault(name) == target
                    ? program.Symbols.PatternAugmentations.GetValueOrDefault(name) ?? target : target;
            }
        }
        if (module is null)
            program.AliasDiagnostic(
                reference?.Resolution.IsResolved == true ? 2306 : missingModuleCode,
                implicitImport ? location : specifier!);
        return program.Symbols.Merger.GetMergedSymbol(module);
    }

    private void CheckResolvedImport(SyntaxNode location, SyntaxNode specifier, string name, SourceFileNode source,
        Programs.ModuleReference reference)
    {
        var options = program.Symbols.Program.Configuration.Options;
        if (JsxMode == 0 && reference.Resolution.Extension is ".tsx" or ".jsx")
            Error(specifier, 6142);
        var import = DeclarationOrder.Ancestor(
            location,
            n => n is ImportDeclarationNode or ExportDeclarationNode or ImportEqualsDeclarationNode or ImportTypeNode
            || n is CallExpressionNode call && IsImportCall(call));
        bool emitted = import switch
        {
            ImportDeclarationNode declaration => declaration.ImportClause is { } clause && !SemanticSyntax.TypeOnly(clause),
            ExportDeclarationNode declaration => !declaration.IsTypeOnly,
            ImportEqualsDeclarationNode declaration => !declaration.IsTypeOnly,
            CallExpressionNode => true,
            _ => false
        };
        bool declarationExtension = name.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".d.mts", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".d.cts", StringComparison.OrdinalIgnoreCase);
        if (reference.Resolution.UsingTsExtension && emitted)
        {
            if (declarationExtension)
                Error(specifier, 2846);
            else if (!source.IsDeclarationFile && options.Boolean("allowImportingTsExtensions") != true
                && options.Boolean("rewriteRelativeImportExtensions") != true)
                Error(specifier, 5097);
        }
        var target = program.Symbols.Program.GetFile(reference.Resolution.FileName);
        if (target is null || ModuleKind is not (100 or 101) || target.ImpliedFormat != ReferenceResolutionMode.Import)
            return;
        bool sync = import is ImportEqualsDeclarationNode || import is not CallExpressionNode
            && program.Symbols.Program.GetFile(source.FileName)!.ImpliedFormat == ReferenceResolutionMode.Require;
        var attributes = import is ImportTypeNode importType
            ? importType.Attributes
            : import is null ? null : AliasTargets.Attributes(import);
        bool mode = attributes?.Attributes?.OfType<ImportAttributeNode>().Any(a => ImportAttributeName(a.Name!) == "resolution-mode"
            && a.Value is StringLiteralNode { Text: "import" or "require" }) == true;
        if (!sync || mode)
            return;
        Error(specifier, import switch
        {
            ImportEqualsDeclarationNode => 1471,
            ImportTypeNode => 1542,
            ImportDeclarationNode { ImportClause: { } clause } when SemanticSyntax.TypeOnly(clause) => 1541,
            _ => 1479
        });
    }
}

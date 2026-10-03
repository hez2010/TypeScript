using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal sealed partial class ImportEditor
{
    internal async ValueTask<Utf8String> PromoteAsync(SyntaxNode declaration, Utf8String name)
    {
        SyntaxNode promoted;
        switch (declaration)
        {
            case ImportSpecifierNode specifier when specifier.IsTypeOnly:
            {
                var named = (NamedImportsNode)specifier.Parent!;
                var elements = named.Elements!;
                bool moved = false;
                if (elements.Count > 1)
                {
                    var replacement = F.NewImportSpecifier(false, specifier.PropertyName is { } property ? F.NewIdentifier(SyntaxNameText.Get(property)) : null,
                        F.NewIdentifier(SyntaxNameText.Get(specifier.Name)));
                    var (compare, _) = ImportSorter.DetectSpecifiers((ImportDeclarationNode)named.Parent!.Parent!, file, preferences);
                    int index = ImportSorter.InsertionIndex(elements, replacement, compare);
                    if (index != elements.ToList().IndexOf(specifier))
                    {
                        await tracker.DeleteInListAsync(specifier, elements);
                        await tracker.InsertSpecifierAsync(named, index, replacement);
                        moved = true;
                    }
                }
                if (!moved) tracker.ReplaceText(Start(specifier), Start(specifier.PropertyName ?? specifier.Name!), default);
                promoted = specifier;
                break;
            }
            case ImportSpecifierNode specifier:
                promoted = specifier.Parent!.Parent!;
                await PromoteClauseAsync((ImportClauseNode)promoted, declaration);
                break;
            case ImportClauseNode clause:
                promoted = clause;
                await PromoteClauseAsync(clause, declaration);
                break;
            case NamespaceImportNode ns:
                promoted = ns.Parent!;
                await PromoteClauseAsync((ImportClauseNode)promoted, declaration);
                break;
            case ImportEqualsDeclarationNode import:
            {
                var scan = new Scanner(file.Source); scan.ResetPosition(import.Pos); scan.Scan();
                DeleteTypeKeyword(scan.Position);
                promoted = import;
                break;
            }
            default: throw new InvalidOperationException("Unexpected type-only alias declaration");
        }
        return promoted is ImportSpecifierNode spec
            ? Messages.Remove_type_from_import_of_0_from_1.Format(preferences.Locale, name, ModuleText(spec.Parent!.Parent!))
            : Messages.Remove_type_from_import_declaration_from_0.Format(preferences.Locale, ModuleText(promoted));
    }

    private async ValueTask PromoteClauseAsync(ImportClauseNode clause, SyntaxNode declaration)
    {
        if (clause.PhaseModifier == K.TypeKeyword) DeleteTypeKeyword(clause.Pos);
        if (options.VerbatimModuleSyntax != true || clause.NamedBindings is not NamedImportsNode { Elements.Count: > 1 } named) return;
        var (_, sorted) = ImportSorter.DetectSpecifiers((ImportDeclarationNode)clause.Parent!, file, preferences);
        if (sorted != false && declaration is ImportSpecifierNode && named.Elements.ToList().IndexOf(declaration) > 0)
        {
            await tracker.DeleteInListAsync(declaration, named.Elements);
            await tracker.InsertSpecifierAsync(named, 0, declaration);
        }
        foreach (ImportSpecifierNode specifier in named.Elements)
            if (specifier != declaration && !specifier.IsTypeOnly) tracker.InsertText(Start(specifier), "type "u8);
    }

    private void DeleteTypeKeyword(int position)
    {
        var scan = new Scanner(file.Source); scan.ResetPosition(position);
        if (scan.Scan() != K.TypeKeyword) return;
        int end = scan.Position;
        while (end < file.Source.Length && file.Source.Text[end] is (byte)' ' or (byte)'\t') end++;
        tracker.ReplaceText(scan.TokenStart, end, default);
    }

    private Utf8String ModuleText(SyntaxNode declaration)
    {
        var module = declaration is ImportEqualsDeclarationNode import
            ? import.ModuleReference is ExternalModuleReferenceNode external ? external.Expression : import.ModuleReference
            : (declaration.Parent as ImportDeclarationNode)?.ModuleSpecifier;
        return module is StringLiteralNode text ? text.Text : module is null ? default : file.Source.Text[Start(module)..module.End];
    }
}

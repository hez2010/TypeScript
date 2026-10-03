using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Resolution;

namespace TypeScript.Compiler.LanguageServices;

public enum AutoImportFixKind { UseNamespace, JsdocTypeImport, AddToExisting, AddNew, PromoteTypeOnly }
public enum ImportKind { Named, Default, Namespace, CommonJS }
public enum AddAsTypeOnly { Allowed = 1, Required = 2, NotAllowed = 4 }

public sealed record AutoImportFix(AutoImportFixKind Kind, Utf8String Name, ImportKind ImportKind, bool UseRequire,
    AddAsTypeOnly AddAsTypeOnly, Utf8String ModuleSpecifier, int ImportIndex = 0, DocumentPosition? UsagePosition = null, Utf8String NamespacePrefix = default);

internal sealed record ImportFix(AutoImportFix Data, ModuleSpecifierKind ModuleSpecifierKind = ModuleSpecifierKind.None,
    bool IsReExport = false, Utf8String ModuleFileName = default, SyntaxNode? TypeOnlyAliasDeclaration = null)
{
    internal async ValueTask<(DocumentTextEdit[] Edits, Utf8String Description, bool Safe)> EditsAsync(DocumentProjection projection,
        CompilerOptions options, UserPreferences preferences, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var tracker = new SourceEditTracker(projection, preferences.FormatCodeSettings, cancellation);
        var editor = new ImportEditor(tracker, projection.File, options, preferences, cancellation);
        var fix = Data;
        Utf8String description;
        switch (fix.Kind)
        {
            case AutoImportFixKind.UseNamespace:
            case AutoImportFixKind.JsdocTypeImport:
            {
                Utf8String quote = editor.SingleQuotes() ? "'"u8 : "\""u8;
                var prefix = fix.Kind == AutoImportFixKind.UseNamespace ? fix.NamespacePrefix + "."u8
                    : "import("u8 + quote + fix.ModuleSpecifier + quote + ")."u8;
                tracker.InsertAtOriginalPosition(fix.UsagePosition ?? throw new InvalidOperationException("Missing import usage position"), prefix);
                description = Messages.Change_0_to_1.Format(preferences.Locale, fix.Name, prefix + fix.Name);
                break;
            }
            case AutoImportFixKind.AddToExisting:
                await editor.AddExistingAsync(ImportEditor.ExistingTarget(projection.File, fix.ImportIndex),
                    fix.ImportKind == ImportKind.Default ? new(fix.ImportKind, fix.Name, fix.AddAsTypeOnly) : null,
                    fix.ImportKind == ImportKind.Named ? [new(fix.ImportKind, fix.Name, fix.AddAsTypeOnly)] : []);
                description = Messages.Update_import_from_0.Format(preferences.Locale, fix.ModuleSpecifier);
                break;
            case AutoImportFixKind.AddNew:
                editor.InsertDeclarations(editor.NewDeclarations(fix.ModuleSpecifier, fix.UseRequire,
                    fix.ImportKind == ImportKind.Default ? new(fix.ImportKind, fix.Name, fix.AddAsTypeOnly) : null,
                    fix.ImportKind == ImportKind.Named ? [new(fix.ImportKind, fix.Name, fix.AddAsTypeOnly)] : [],
                    fix.ImportKind is ImportKind.Namespace or ImportKind.CommonJS ? new(fix.ImportKind, fix.Name, 0) : null));
                description = Messages.Add_import_from_0.Format(preferences.Locale, fix.ModuleSpecifier);
                break;
            case AutoImportFixKind.PromoteTypeOnly:
                description = await editor.PromoteAsync(TypeOnlyAliasDeclaration ?? throw new InvalidOperationException("Missing type-only declaration"), fix.Name);
                break;
            default: throw new InvalidOperationException("Unknown import fix kind");
        }
        var edits = await tracker.GetChangesAsync();
        return (edits, description, tracker.IsMappable);
    }
}

internal sealed record ImportBinding(ImportKind Kind, Utf8String Name, AddAsTypeOnly TypeOnly, Utf8String PropertyName = default);

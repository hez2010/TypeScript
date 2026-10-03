using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;

namespace TypeScript.Compiler.LanguageServices;

internal sealed class ImportAdder(DocumentProjection projection, CompilerOptions options, UserPreferences preferences,
    Func<Symbol, bool, CancellationToken, ValueTask<ImportFix?>> resolve, CancellationToken cancellation)
{
    private sealed class Imports(bool require = false)
    {
        internal ImportBinding? Default, Namespace;
        internal readonly Dictionary<Utf8String, ImportBinding> Named = [];
        internal bool Require { get; } = require;
        internal ImportBinding[] SortedNames => Named.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
    }
    private readonly List<AutoImportFix> namespaceFixes = [], documentationFixes = [];
    private readonly Dictionary<SyntaxNode, Imports> existing = [];
    private readonly Dictionary<(Utf8String Module, bool TypeOnly), Imports> added = [];

    internal bool HasFixes => namespaceFixes.Count != 0 || documentationFixes.Count != 0 || existing.Count != 0 || added.Count != 0;
    internal async ValueTask AddExportAsync(Symbol symbol, bool typeOnlyUse = true)
    {
        if (await resolve(symbol, typeOnlyUse, cancellation) is { } fix) Add(fix.Data);
    }

    internal async ValueTask<SyntaxNode?> RewriteTypeAsync(SyntaxNode? node, IReadOnlyDictionary<SyntaxNode, Symbol> identifierSymbols, NodeFactory factory)
    {
        if (node is null) return null;
        var rewritten = ImportTypeRewriter.Rewrite(node, identifierSymbols, factory, cancellation);
        foreach (var symbol in rewritten.Imports) await AddExportAsync(symbol.Parent is not null ? symbol.ExportSymbol ?? symbol : symbol);
        return rewritten.Node;
    }

    internal void Add(AutoImportFix fix)
    {
        switch (fix.Kind)
        {
            case AutoImportFixKind.UseNamespace: namespaceFixes.Add(fix); return;
            case AutoImportFixKind.JsdocTypeImport: documentationFixes.Add(fix); return;
            case AutoImportFixKind.PromoteTypeOnly: return;
            case AutoImportFixKind.AddToExisting:
            {
                var clause = ImportEditor.ExistingTarget(projection.File, fix.ImportIndex);
                if (!existing.TryGetValue(clause, out var entry)) existing[clause] = entry = new();
                if (fix.ImportKind == ImportKind.Named) entry.Named[fix.Name] = Merge(entry.Named.GetValueOrDefault(fix.Name), fix);
                else entry.Default = Merge(entry.Default, fix);
                return;
            }
            case AutoImportFixKind.AddNew:
            {
                var entry = NewEntry(fix);
                if (entry.Require != fix.UseRequire) throw new InvalidOperationException("Cannot combine import and require for one module");
                switch (fix.ImportKind)
                {
                    case ImportKind.Default: entry.Default = Merge(entry.Default, fix); break;
                    case ImportKind.Named: entry.Named[fix.Name] = Merge(entry.Named.GetValueOrDefault(fix.Name), fix); break;
                    case ImportKind.CommonJS when options.VerbatimModuleSyntax == true:
                        entry.Named[fix.Name] = Merge(entry.Named.GetValueOrDefault(fix.Name), fix); break;
                    default: entry.Namespace = new(fix.ImportKind, fix.Name, fix.AddAsTypeOnly); break;
                }
                return;
            }
            default: throw new InvalidOperationException("Unknown import fix kind");
        }
    }

    private Imports NewEntry(AutoImportFix fix)
    {
        added.TryGetValue((fix.ModuleSpecifier, true), out var typeEntry);
        added.TryGetValue((fix.ModuleSpecifier, false), out var valueEntry);
        if (fix.ImportKind == ImportKind.Default && fix.AddAsTypeOnly == AddAsTypeOnly.Required)
        {
            if (typeEntry is not null) return typeEntry;
            return added[(fix.ModuleSpecifier, true)] = new(fix.UseRequire);
        }
        if (fix.AddAsTypeOnly == AddAsTypeOnly.Allowed && (typeEntry ?? valueEntry) is { } preferred) return preferred;
        return valueEntry ?? (added[(fix.ModuleSpecifier, false)] = new(fix.UseRequire));
    }

    private static ImportBinding Merge(ImportBinding? existing, AutoImportFix fix)
        => new(fix.ImportKind, fix.Name, (AddAsTypeOnly)Math.Max((int)(existing?.TypeOnly ?? 0), (int)fix.AddAsTypeOnly));

    internal async ValueTask<DocumentTextEdit[]> EditsAsync()
    {
        var tracker = new SourceEditTracker(projection, preferences.FormatCodeSettings, cancellation);
        var editor = new ImportEditor(tracker, projection.File, options, preferences, cancellation);
        foreach (var fix in namespaceFixes) tracker.InsertAtOriginalPosition(fix.UsagePosition!.Value, fix.NamespacePrefix + "."u8);
        foreach (var fix in documentationFixes)
        {
            Utf8String quote = editor.SingleQuotes() ? "'"u8 : "\""u8;
            tracker.InsertAtOriginalPosition(fix.UsagePosition!.Value, "import("u8 + quote + fix.ModuleSpecifier + quote + ")."u8);
        }
        foreach (var pair in existing) await editor.AddExistingAsync(pair.Key, pair.Value.Default, pair.Value.SortedNames);
        List<SyntaxNode> declarations = [];
        foreach (var pair in added)
            declarations.AddRange(editor.NewDeclarations(pair.Key.Module, pair.Value.Require, pair.Value.Default, pair.Value.SortedNames, pair.Value.Namespace));
        editor.InsertDeclarations(declarations);
        return await tracker.GetChangesAsync();
    }
}

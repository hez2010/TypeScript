using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal sealed class ImportOrganizer(DocumentProjection projection, Checker checker, CompilerOptions options,
    UserPreferences preferences, CancellationToken cancellation)
{
    private SourceFileNode File => projection.File;
    private readonly SourceEditTracker tracker = new(projection, preferences.FormatCodeSettings, cancellation);
    private Comparison<Utf8String> modules = null!;
    private Comparison<SyntaxNode> specifiers = null!;
    private bool remove, combine, preserveJsx;

    internal async ValueTask<DocumentTextEdit[]> OrganizeAsync(Utf8String kind)
    {
        remove = kind != "source.sortImports.ts"u8;
        combine = kind != "source.removeUnusedImports.ts"u8;
        preserveJsx = options.Jsx is JsxEmit.React or JsxEmit.ReactNative
            && File.DescendantsAndSelf().Any(node => node is JsxElementNode or JsxSelfClosingElementNode or JsxFragmentNode);
        var statements = File.Statements ?? new([]);
        var imports = statements.OfType<ImportDeclarationNode>().ToArray();
        var groups = Groups(imports).ToArray();
        (modules, specifiers) = ImportSorter.OrganizeComparers(groups, imports, preferences);
        foreach (var group in groups) await ImportsAsync(group);
        if (combine)
            foreach (var group in ExportGroups(statements)) await ExportsAsync(group);
        foreach (var module in statements.OfType<ModuleDeclarationNode>())
        {
            if (module.Name is not StringLiteralNode || module.Body is not ModuleBlockNode { Statements: { } body }) continue;
            foreach (var group in Groups(body.OfType<ImportDeclarationNode>())) await ImportsAsync(group);
            if (combine) await ExportsAsync(body.OfType<ExportDeclarationNode>().ToArray());
        }
        return await tracker.GetChangesAsync();
    }

    private IEnumerable<T[]> Groups<T>(IEnumerable<T> declarations) where T : SyntaxNode
    {
        List<T> group = [];
        var scanner = new Scanner(File.Source, skipTrivia: false);
        foreach (var declaration in declarations)
        {
            cancellation.ThrowIfCancellationRequested();
            if (group.Count > 0 && declaration.Pos >= 0 && declaration.Pos < File.Source.Text.Length)
            {
                int start = SmartIndenter.Start(declaration, File), lines = 0;
                scanner.SetTextRange(declaration.Pos, start);
                while (scanner.Scan() is { } token && token != K.EndOfFile)
                    if (token == K.NewLineTrivia && ++lines == 2) { yield return [.. group]; group.Clear(); break; }
            }
            group.Add(declaration);
        }
        if (group.Count > 0) yield return [.. group];
    }

    private IEnumerable<ExportDeclarationNode[]> ExportGroups(NodeList statements)
    {
        List<ExportDeclarationNode> group = [];
        for (int i = 0; i < statements.Count; i++)
        {
            if (statements[i] is ExportDeclarationNode export)
            {
                group.Add(export);
                if (export.ModuleSpecifier is not null) continue;
                while (i + 1 < statements.Count && statements[i + 1] is ExportDeclarationNode next) { group.Add(next); i++; }
            }
            if (group.Count == 0) continue;
            foreach (var contiguous in Groups(group)) yield return contiguous;
            group.Clear();
        }
        foreach (var contiguous in Groups(group)) yield return contiguous;
    }

    private async ValueTask ImportsAsync(ImportDeclarationNode[] original)
    {
        var processed = remove ? await RemoveUnusedAsync(original) : original;
        List<ImportDeclarationNode> result = [];
        if (combine)
        {
            var groups = processed.GroupBy(ImportSorter.ModuleName)
                .OrderBy(group => group.Key, Comparer<Utf8String>.Create((a, b) =>
                {
                    int comparison = a.IsEmpty.CompareTo(b.IsEmpty);
                    if (comparison == 0) comparison = ImportSorter.Relative(a).CompareTo(ImportSorter.Relative(b));
                    return comparison != 0 ? comparison : modules(a, b);
                }));
            foreach (var group in groups)
                result.AddRange(CoalesceImports(group).OrderBy(node => node,
                    Comparer<ImportDeclarationNode>.Create((a, b) => ImportSorter.CompareDeclarations(a, b, modules))));
        }
        else result.AddRange(processed);
        await tracker.ReplaceDeclarationsAsync(original, result);
    }

    private async ValueTask<ImportDeclarationNode[]> RemoveUnusedAsync(ImportDeclarationNode[] imports)
    {
        List<ImportDeclarationNode> result = [];
        foreach (var import in imports)
        {
            cancellation.ThrowIfCancellationRequested();
            if (import.ImportClause is not { } clause) { result.Add(import); continue; }
            var name = clause.Name;
            if (name is not null && !await UsedAsync(name)) name = null;
            var bindings = clause.NamedBindings;
            if (bindings is NamespaceImportNode { Name: { } namespaceName } && !await UsedAsync(namespaceName)) bindings = null;
            if (bindings is NamedImportsNode { Elements: { } elements } named)
            {
                List<SyntaxNode> used = [];
                foreach (var element in elements.OfType<ImportSpecifierNode>())
                    if (element.Name is IdentifierNode identifier && await UsedAsync(identifier)) used.Add(element);
                bindings = used.Count == 0 ? null : used.Count == elements.Count ? named : NamedImports(named, new([.. used]));
                if (bindings is not null) PreserveLines(bindings, named);
            }
            if (name is not null || bindings is not null) result.Add(Import(import, Clause(clause, name, bindings)));
            else if (import.ModuleSpecifier is StringLiteralNode module
                && File.ModuleAugmentations.OfType<StringLiteralNode>().Any(augmentation => augmentation.Text == module.Text))
                result.Add(File.IsDeclarationFile ? Import(import, null) : import);
        }
        return [.. result];
    }

    private ValueTask<bool> UsedAsync(IdentifierNode identifier) => checker.IsImportUsedAsync(File, identifier, preserveJsx, cancellation);

    private IEnumerable<ImportDeclarationNode> CoalesceImports(IEnumerable<ImportDeclarationNode> imports)
    {
        foreach (var attributes in imports.GroupBy(import => AttributesKey(import.Attributes)))
        {
            if (attributes.FirstOrDefault(import => import.ImportClause is null) is { } sideEffect) yield return sideEffect;
            foreach (bool typeOnly in new[] { false, true })
            {
                var group = attributes.Where(import => import.ImportClause is { } clause && (clause.PhaseModifier == K.TypeKeyword) == typeOnly).ToArray();
                var defaults = group.Where(import => import.ImportClause!.Name is not null).ToArray();
                var namespaces = group.Where(import => import.ImportClause!.NamedBindings is NamespaceImportNode).ToArray();
                var named = group.Where(import => import.ImportClause!.NamedBindings is NamedImportsNode).ToArray();
                if (!typeOnly && defaults.Length == 1 && namespaces.Length == 1 && named.Length == 0)
                {
                    yield return Import(defaults[0], Clause(defaults[0].ImportClause!, defaults[0].ImportClause!.Name, namespaces[0].ImportClause!.NamedBindings));
                    continue;
                }
                foreach (var import in namespaces.OrderBy(import => ReferenceNavigation.Text(import.ImportClause!.NamedBindings!.DeclarationName), Comparer<Utf8String>.Create(modules)))
                    yield return Import(import, Clause(import.ImportClause!, null, import.ImportClause!.NamedBindings));
                var firstDefault = defaults.FirstOrDefault();
                var firstNamed = named.FirstOrDefault();
                var template = firstDefault ?? firstNamed;
                if (template is null) continue;
                IdentifierNode? defaultName = defaults.Length == 1 ? defaults[0].ImportClause!.Name : null;
                List<SyntaxNode> elements = [];
                if (defaults.Length != 1)
                    foreach (var import in defaults) elements.Add(tracker.Factory.NewImportSpecifier(false,
                        tracker.Factory.NewIdentifier("default"u8), import.ImportClause!.Name));
                foreach (var import in named)
                    foreach (var element in ((NamedImportsNode)import.ImportClause!.NamedBindings!).Elements!.OfType<ImportSpecifierNode>())
                    {
                        if (element.PropertyName is { } property && ReferenceNavigation.Text(property) == ReferenceNavigation.Text(element.Name))
                        { var normalized = tracker.Context.Clone(element); normalized.PropertyName = null; elements.Add(normalized); }
                        else elements.Add(element);
                    }
                elements = elements.OrderBy(element => element, Comparer<SyntaxNode>.Create(specifiers)).ToList();
                NamedImportsNode? bindings;
                var originalBindings = firstNamed?.ImportClause!.NamedBindings as NamedImportsNode;
                if (elements.Count == 0) bindings = defaultName is not null ? null : NamedImports(null, new([]));
                else
                {
                    var oldList = originalBindings?.Elements;
                    var sorted = oldList?.HasTrailingComma == true ? new NodeList([.. elements], oldList.Pos, oldList.End, trailingComma: true) : new([.. elements]);
                    bindings = NamedImports(originalBindings, sorted);
                }
                if (bindings is not null && originalBindings is not null) PreserveLines(bindings, originalBindings);
                if (typeOnly && defaultName is not null && bindings is not null)
                {
                    yield return Import(template, NewClause(template.ImportClause!.PhaseModifier, defaultName, null));
                    var namedTemplate = firstNamed ?? template;
                    yield return Import(namedTemplate, NewClause(namedTemplate.ImportClause!.PhaseModifier, null, bindings));
                }
                else yield return Import(template, Clause(template.ImportClause!, defaultName, bindings));
            }
        }
    }

    private async ValueTask ExportsAsync(ExportDeclarationNode[] original)
    {
        List<ExportDeclarationNode> result = [];
        foreach (var group in original.GroupBy(ImportSorter.ModuleName).OrderBy(group => group.Key,
            Comparer<Utf8String>.Create((a, b) => a.IsEmpty != b.IsEmpty ? a.IsEmpty.CompareTo(b.IsEmpty) : modules(a, b))))
        {
            if (group.FirstOrDefault(export => export.ExportClause is null) is { } star) result.Add(star);
            foreach (bool typeOnly in new[] { false, true })
            {
                var exports = group.Where(export => export.ExportClause is not null && export.IsTypeOnly == typeOnly).ToArray();
                if (exports.Length == 0) continue;
                var export = tracker.Context.Clone(exports[0]);
                if (export.ExportClause is NamedExportsNode named)
                {
                    var elements = exports.Select(export => export.ExportClause).OfType<NamedExportsNode>()
                        .SelectMany(clause => clause.Elements ?? new([])).OrderBy(element => element, Comparer<SyntaxNode>.Create(specifiers)).ToArray();
                    var updated = tracker.Context.Clone(named); updated.Elements = new(elements);
                    PreserveLines(updated, named); export.ExportClause = updated;
                }
                result.Add(export);
            }
        }
        await tracker.ReplaceDeclarationsAsync(original, result);
    }

    private static Utf8String AttributesKey(ImportAttributesNode? attributes)
    {
        if (attributes is null) return default;
        return Utf8String.Format((int)attributes.Token) + " "u8 + Utf8String.Join(" "u8,
            (attributes.Attributes ?? new([])).OfType<ImportAttributeNode>().OrderBy(attribute => ReferenceNavigation.Text(attribute.Name))
                .Select(attribute => ReferenceNavigation.Text(attribute.Name) + ":"u8 + (attribute.Value is StringLiteralNode or NoSubstitutionTemplateLiteralNode
                    ? "\""u8 + ReferenceNavigation.Text(attribute.Value) + "\""u8 : ReferenceNavigation.Text(attribute.Value))));
    }

    private ImportDeclarationNode Import(ImportDeclarationNode original, ImportClauseNode? clause)
    { var result = tracker.Context.Clone(original); result.ImportClause = clause; return result; }
    private ImportClauseNode Clause(ImportClauseNode original, IdentifierNode? name, SyntaxNode? bindings)
    { var result = tracker.Context.Clone(original); result.Name = name; result.NamedBindings = bindings; return result; }
    private ImportClauseNode NewClause(K phase, IdentifierNode? name, SyntaxNode? bindings)
        => tracker.Factory.NewImportClause(phase, name, bindings);
    private NamedImportsNode NamedImports(NamedImportsNode? original, NodeList elements)
    {
        var result = original is null ? tracker.Factory.NewNamedImports(elements) : tracker.Context.Clone(original);
        result.Elements = elements; return result;
    }
    private void PreserveLines(SyntaxNode updated, SyntaxNode original)
    {
        if (original.Pos >= 0 && File.Source.GetLineAndCharacter(original.Pos).Line != File.Source.GetLineAndCharacter(original.End).Line)
            tracker.Context.AddFlags(updated, EmitFlags.MultiLine);
    }
}

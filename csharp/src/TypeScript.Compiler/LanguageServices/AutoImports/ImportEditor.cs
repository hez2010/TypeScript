using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal sealed partial class ImportEditor(SourceEditTracker tracker, SourceFileNode file, CompilerOptions options,
    UserPreferences preferences, CancellationToken cancellation)
{
    private NodeFactory F => tracker.Factory;

    internal async ValueTask AddExistingAsync(SyntaxNode target, ImportBinding? defaultImport, IReadOnlyList<ImportBinding> namedImports)
    {
        cancellation.ThrowIfCancellationRequested();
        if (target is BindingPatternNode { Kind: K.ObjectBindingPattern } pattern)
        {
            if (defaultImport is not null) await AddBindingAsync(pattern, defaultImport.Name, "default"u8);
            foreach (var binding in namedImports) await AddBindingAsync(pattern, binding.Name, default);
            return;
        }
        var clause = (ImportClauseNode)target;
        bool typeOnly = clause.PhaseModifier == K.TypeKeyword;
        bool promote = typeOnly && (defaultImport?.TypeOnly == AddAsTypeOnly.NotAllowed || namedImports.Any(binding => binding.TypeOnly == AddAsTypeOnly.NotAllowed));
        var named = clause.NamedBindings as NamedImportsNode;
        var existing = named?.Elements ?? new([]);
        if (defaultImport is not null) tracker.Insert(Start(clause), F.NewIdentifier(defaultImport.Name), new(Suffix: ", "u8));
        if (namedImports.Count != 0)
        {
            var (compare, sorted) = ImportSorter.DetectSpecifiers(clause.Parent as ImportDeclarationNode, file, preferences);
            var added = namedImports.Select(binding => F.NewImportSpecifier((!typeOnly || promote) && TypeOnly(binding.TypeOnly),
                binding.PropertyName.IsEmpty ? null : F.NewIdentifier(binding.PropertyName), F.NewIdentifier(binding.Name)))
                .OrderBy(node => node, Comparer<SyntaxNode>.Create(compare)).ToArray();
            if (existing.Count != 0)
            {
                IReadOnlyList<SyntaxNode> comparison = promote ? existing.Cast<ImportSpecifierNode>()
                    .Select(node => (SyntaxNode)F.NewImportSpecifier(true, node.PropertyName, node.Name)).ToArray() : existing;
                foreach (var node in added)
                    if (sorted != false) await tracker.InsertSpecifierAsync(named!, ImportSorter.InsertionIndex(comparison, node, compare), node);
                    else await tracker.InsertInListAfterAsync(existing[^1], node, existing);
            }
            else
            {
                var bindings = F.NewNamedImports(new([.. added]));
                if (clause.NamedBindings is { } old) tracker.ReplaceNode(old, bindings);
                else tracker.InsertAfter(clause.Name!, [bindings]);
            }
        }
        if (promote)
        {
            var scan = new Scanner(file.Source); scan.ResetPosition(clause.Pos);
            if (scan.Scan() != K.TypeKeyword) throw new InvalidOperationException("Expected a type-only import clause");
            tracker.ReplaceText(scan.TokenStart, scan.SkipTriviaAt(scan.Position, stopAfterLineBreak: true), default);
            foreach (ImportSpecifierNode specifier in existing)
                if (!specifier.IsTypeOnly) tracker.InsertText(Start(specifier), "type "u8);
        }
    }

    private async ValueTask AddBindingAsync(BindingPatternNode pattern, Utf8String name, Utf8String property)
    {
        var element = F.NewBindingElement(null, null, F.NewIdentifier(name), property.IsEmpty ? null : F.NewIdentifier(property));
        if (pattern.Elements is { Count: > 0 } elements) await tracker.InsertInListAfterAsync(elements[^1], element, elements);
        else tracker.ReplaceNode(pattern, F.NewBindingPattern(K.ObjectBindingPattern, new([element])));
    }

    internal IReadOnlyList<SyntaxNode> NewDeclarations(Utf8String module, bool require, ImportBinding? defaultImport,
        IReadOnlyList<ImportBinding> named, ImportBinding? namespaceImport)
    {
        cancellation.ThrowIfCancellationRequested();
        List<SyntaxNode> result = [];
        var literal = F.NewStringLiteral(module, SingleQuotes() ? TokenFlags.SingleQuote : 0);
        if (require)
        {
            if (defaultImport is not null || named.Count > 0)
            {
                List<SyntaxNode> bindings = [];
                if (defaultImport is not null) bindings.Add(F.NewBindingElement(null, F.NewIdentifier("default"u8), F.NewIdentifier(defaultImport.Name), null));
                foreach (var binding in named) bindings.Add(F.NewBindingElement(null, binding.PropertyName.IsEmpty ? null : F.NewIdentifier(binding.PropertyName), F.NewIdentifier(binding.Name), null));
                result.Add(Require(F.NewBindingPattern(K.ObjectBindingPattern, new([.. bindings]))));
            }
            if (namespaceImport is not null) result.Add(Require(F.NewIdentifier(namespaceImport.Name)));
            return result;
        }
        if (defaultImport is not null || named.Count > 0)
        {
            bool topLevelTypeOnly = (defaultImport is null || defaultImport.TypeOnly == AddAsTypeOnly.Required)
                && named.All(binding => binding.TypeOnly == AddAsTypeOnly.Required)
                || (options.VerbatimModuleSyntax == true || preferences.PreferTypeOnlyAutoImports == true)
                && defaultImport?.TypeOnly != AddAsTypeOnly.NotAllowed && named.All(binding => binding.TypeOnly != AddAsTypeOnly.NotAllowed);
            var bindings = named.Select(binding => (SyntaxNode)F.NewImportSpecifier(!topLevelTypeOnly && TypeOnly(binding.TypeOnly),
                binding.PropertyName.IsEmpty ? null : F.NewIdentifier(binding.PropertyName), F.NewIdentifier(binding.Name))).ToArray();
            result.Add(F.NewImportDeclaration(K.ImportDeclaration, null, F.NewImportClause(topLevelTypeOnly ? K.TypeKeyword : K.Unknown,
                defaultImport is null ? null : F.NewIdentifier(defaultImport.Name), bindings.Length == 0 ? null : F.NewNamedImports(new(bindings))), literal, null));
        }
        if (namespaceImport is not null)
            result.Add(namespaceImport.Kind == ImportKind.CommonJS
                ? F.NewImportEqualsDeclaration(null, TypeOnly(namespaceImport.TypeOnly), F.NewIdentifier(namespaceImport.Name), F.NewExternalModuleReference(literal))
                : F.NewImportDeclaration(K.ImportDeclaration, null, F.NewImportClause(TypeOnly(namespaceImport.TypeOnly) ? K.TypeKeyword : K.Unknown,
                    null, F.NewNamespaceImport(F.NewIdentifier(namespaceImport.Name))), literal, null));
        return result;

        SyntaxNode Require(SyntaxNode name) => F.NewVariableStatement(null, F.NewVariableDeclarationList(new([
            F.NewVariableDeclaration(name, null, null, F.NewCallExpression(F.NewIdentifier("require"u8), null, null, new([literal]), 0))]), NodeFlags.Const));
    }

    internal void InsertDeclarations(IReadOnlyList<SyntaxNode> declarations)
    {
        cancellation.ThrowIfCancellationRequested();
        if (declarations.Count == 0) return;
        var existing = (file.Statements ?? new([])).Where(node => declarations[0] is VariableStatementNode
            ? RequireStatement(node) : node is ImportDeclarationNode or ImportEqualsDeclarationNode).ToArray();
        var (compare, sorted) = ImportSorter.DetectModules(existing, preferences);
        int Compare(SyntaxNode left, SyntaxNode right) => ImportSorter.CompareDeclarations(left, right, compare);
        var ordered = declarations.OrderBy(node => node, Comparer<SyntaxNode>.Create(Compare)).ToArray();
        if (existing.Length == 0) { tracker.InsertAtTop(ordered, true); return; }
        if (!sorted) { tracker.InsertAfter(existing[^1], ordered); return; }
        foreach (var node in ordered)
        {
            int index = ImportSorter.InsertionIndex(existing, node, Compare);
            if (index == 0) tracker.InsertBefore(existing[0], node, excludeLeadingTrivia: file.Statements![0] == existing[0]);
            else tracker.InsertAfter(existing[index - 1], [node]);
        }
    }

    internal static bool RequireStatement(SyntaxNode node) => node is VariableStatementNode { DeclarationList.Declarations: { Count: 1 } declarations }
        && declarations[0] is VariableDeclarationNode { Initializer: CallExpressionNode { Expression: IdentifierNode { Text: var name }, Arguments.Count: 1 } } && name == "require"u8;
    internal static SyntaxNode? ImportFromSpecifier(SyntaxNode node) => node.Parent switch
    {
        ImportDeclarationNode or ExportDeclarationNode or JSDocImportTagNode or CallExpressionNode or ImportTypeNode => node.Parent,
        ExternalModuleReferenceNode { Parent: ImportEqualsDeclarationNode parent } => parent,
        LiteralTypeNode { Parent: ImportTypeNode import } => import,
        _ => null,
    };

    internal static SyntaxNode ExistingTarget(SourceFileNode file, int index)
        => ImportFromSpecifier(file.Imports[index]) switch
        {
            ImportDeclarationNode { ImportClause: { } clause } => clause,
            CallExpressionNode { Parent: VariableDeclarationNode { Name: BindingPatternNode pattern } } => pattern,
            _ => throw new InvalidOperationException("Expected an import clause or require binding pattern"),
        };

    internal bool SingleQuotes() => !preferences.QuotePreference.IsEmpty && preferences.QuotePreference != "auto"u8
        ? preferences.QuotePreference == "single"u8
        : file.Imports.OfType<StringLiteralNode>().FirstOrDefault(node => node.Parent?.Pos >= 0) is { } literal && (literal.TokenFlags & TokenFlags.SingleQuote) != 0;
    private bool TypeOnly(AddAsTypeOnly typeOnly) => typeOnly == AddAsTypeOnly.Required || typeOnly != AddAsTypeOnly.NotAllowed && preferences.PreferTypeOnlyAutoImports == true;
    private int Start(SyntaxNode node) => SmartIndenter.Start(node, file);
}

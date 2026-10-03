using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class CompletionQuery
    {
        private ImportStatementInfo? importStatement;
        private readonly record struct ImportStatementInfo(K Keyword, bool KeywordOnly, bool NewIdentifier,
            DocumentRange? Replacement, bool TypeOnlySpecifier, bool TopLevelTypeOnly);

        private async ValueTask<ImportStatementInfo> ImportStatementAsync(SyntaxNode token)
        {
            K keyword = K.Unknown;
            bool keywordOnly = false;
            SyntaxNode? candidate = null;
            var parent = token.Parent;
            switch (parent)
            {
                case ImportEqualsDeclarationNode import:
                    if (token is IdentifierNode && await LastTokenAsync(parent) != token) { keyword = K.FromKeyword; keywordOnly = true; }
                    else
                    {
                        if (token.Kind != K.TypeKeyword) keyword = K.TypeKeyword;
                        if (MissingModule(import.ModuleReference)) candidate = parent;
                    }
                    break;
                case ImportSpecifierNode specifier when CouldBeTypeOnlySpecifier(specifier, token) && await CanCompleteNamedImportsAsync(specifier.Parent):
                    candidate = parent;
                    break;
                case NamedImportsNode or NamespaceImportNode:
                    if (parent.Parent is ImportClauseNode { PhaseModifier: not K.TypeKeyword }
                        && token.Kind is K.OpenBraceToken or K.ImportKeyword or K.CommaToken) keyword = K.TypeKeyword;
                    if (await CanCompleteNamedImportsAsync(parent))
                    {
                        if (token.Kind == K.CloseBraceToken || token is IdentifierNode) { keywordOnly = true; keyword = K.FromKeyword; }
                        else candidate = parent.Parent?.Parent;
                    }
                    break;
                case ExportDeclarationNode when token.Kind == K.AsteriskToken:
                case NamedExportsNode when token.Kind == K.CloseBraceToken:
                    keywordOnly = true; keyword = K.FromKeyword;
                    break;
                default:
                    if (token.Kind == K.ImportKeyword)
                    {
                        if (parent is SourceFileNode) { keyword = K.TypeKeyword; candidate = token; }
                        else if (parent is ImportDeclarationNode import)
                        { keyword = K.TypeKeyword; if (MissingModule(import.ModuleSpecifier)) candidate = parent; }
                    }
                    break;
            }
            return new(keyword, keywordOnly, candidate is not null || keyword == K.TypeKeyword,
                candidate is null ? null : await ImportReplacementAsync(candidate), candidate is not null && CouldBeTypeOnlySpecifier(candidate, token),
                candidate is ImportDeclarationNode { ImportClause.PhaseModifier: K.TypeKeyword } or ImportEqualsDeclarationNode { IsTypeOnly: true });
        }

        private async ValueTask<DocumentRange?> ImportReplacementAsync(SyntaxNode node)
        {
            node = Ancestor(node, candidate => candidate is ImportDeclarationNode or ImportEqualsDeclarationNode or JSDocImportTagNode) ?? node;
            int start = await StartAsync(node), end = node.End;
            if (Line(start) != Line(end))
            {
                SyntaxNode? split = node switch
                {
                    ImportDeclarationNode import => await InvalidImportAsync(import.ImportClause?.NamedBindings) ?? import.ModuleSpecifier,
                    JSDocImportTagNode import => await InvalidImportAsync(import.ImportClause?.NamedBindings) ?? import.ModuleSpecifier,
                    ImportEqualsDeclarationNode import => import.ModuleReference, _ => null,
                };
                if (split is null || Line(start) != Line(split.Pos)) return null;
                end = split.Pos;
            }
            var mapped = projection.ToRange(start, end);
            return mapped.Fidelity == MappingFidelity.Exact ? mapped.Range : null;
        }

        private async ValueTask<bool> CanCompleteNamedImportsAsync(SyntaxNode? bindings)
        {
            if (bindings?.Parent is not ImportClauseNode { Name: null } clause || !MissingModule(ModuleSpecifier(clause.Parent))) return false;
            if (bindings is not NamedImportsNode named) return true;
            int valid = named.Elements?.Count ?? 0;
            if (await InvalidImportAsync(bindings) is { } invalid)
                for (int i = 0; i < valid; i++) if (named.Elements![i] == invalid) { valid = i; break; }
            return valid < 2;
        }

        private async ValueTask<SyntaxNode?> InvalidImportAsync(SyntaxNode? bindings)
        {
            if (bindings is NamedImportsNode named)
                foreach (var element in named.Elements ?? new([]))
                    if (element is ImportSpecifierNode { PropertyName: null, Name: { } name }
                        && KeywordOf(name) is >= K.FirstKeyword and < K.FirstContextualKeyword
                        && (await SyntaxNavigation.FindPrecedingTokenAsync(File, name.Pos, cancellation: cancellation))?.Kind != K.CommaToken) return element;
            return null;
        }

        private static bool CouldBeTypeOnlySpecifier(SyntaxNode node, SyntaxNode token) => node is ImportSpecifierNode specifier
            && (specifier.IsTypeOnly || token == specifier.Name && KeywordOf(token) == K.TypeKeyword);
        private static bool TypeOnlyImportOrExport(SyntaxNode node) => SemanticSyntax.TypeOnly(node) || node switch
        {
            ImportSpecifierNode { Parent.Parent: ImportClauseNode clause } => SemanticSyntax.TypeOnly(clause),
            NamespaceImportNode { Parent: ImportClauseNode clause } => SemanticSyntax.TypeOnly(clause),
            ExportSpecifierNode { Parent.Parent: ExportDeclarationNode declaration } => declaration.IsTypeOnly,
            NamespaceExportNode { Parent: ExportDeclarationNode declaration } => declaration.IsTypeOnly,
            _ => false,
        };
        private static bool MissingModule(SyntaxNode? node)
        {
            if (node is null || node.Pos == node.End) return true;
            if (node is ExternalModuleReferenceNode external) node = external.Expression;
            return node is not (StringLiteralNode or NoSubstitutionTemplateLiteralNode) || PropertyName(node).IsEmpty;
        }
        private static SyntaxNode? ModuleSpecifier(SyntaxNode? node) => node switch
        {
            ImportDeclarationNode import => import.ModuleSpecifier, ExportDeclarationNode export => export.ModuleSpecifier,
            JSDocImportTagNode import => import.ModuleSpecifier, _ => null,
        };

        private async ValueTask<bool?> ImportSymbolsAsync()
        {
            if (importStatement is not null) { newIdentifier = true; await CollectAutoImportsAsync(); return true; }
            if (contextToken is null) return null;
            bool typeKeyword = KeywordOf(contextToken) == K.TypeKeyword;
            var bindings = contextToken.Kind is K.OpenBraceToken or K.CommaToken ? contextToken.Parent : typeKeyword ? contextToken.Parent?.Parent : null;
            if (bindings is NamedImportsNode or NamedExportsNode)
            {
                if (!typeKeyword) keywords = KeywordFilter.TypeKeyword;
                var module = ModuleSpecifier(bindings is NamedImportsNode ? bindings.Parent?.Parent : bindings.Parent);
                if (module is null)
                {
                    newIdentifier = true;
                    if (bindings is NamedImportsNode) return false;
                }
                else
                {
                    var moduleSymbol = await checker.GetSymbolAtLocationAsync(module, cancellation);
                    if (moduleSymbol is null) { newIdentifier = true; return false; }
                    kind = CompletionKind.Member; newIdentifier = false;
                    HashSet<Utf8String> existing = [];
                    var elements = bindings is NamedImportsNode imports ? imports.Elements : ((NamedExportsNode)bindings).Elements;
                    foreach (var element in elements ?? new([]))
                        if (!await EditingAsync(element)) existing.Add(PropertyName(element switch
                        { ImportSpecifierNode specifier => specifier.PropertyName ?? specifier.Name!, ExportSpecifierNode specifier => specifier.PropertyName ?? specifier.Name!, _ => element }));
                    foreach (var symbol in await checker.GetCompletionModuleExportsAsync(moduleSymbol, cancellation))
                        if (symbol.Name != "default"u8 && !existing.Contains(symbol.Name)) symbols.Add(new(symbol));
                    if (symbols.Count == 0) keywords = KeywordFilter.None;
                    return true;
                }
            }
            var attributes = contextToken.Kind is K.OpenBraceToken or K.CommaToken ? contextToken.Parent : contextToken.Kind == K.ColonToken ? contextToken.Parent?.Parent : null;
            if (attributes is ImportAttributesNode importAttributes)
            {
                HashSet<Utf8String> existing = [];
                foreach (var attribute in importAttributes.Attributes ?? new([])) if (attribute is ImportAttributeNode { Name: { } name }) existing.Add(PropertyName(name));
                foreach (var symbol in await checker.GetApparentPropertiesAsync(await checker.GetTypeAtLocationAsync(attributes, cancellation), cancellation))
                    if (!existing.Contains(symbol.Name)) symbols.Add(new(symbol));
                return true;
            }
            if (contextToken.Kind is K.OpenBraceToken or K.CommaToken && contextToken.Parent is NamedExportsNode exports
                && Ancestor(exports, node => node is SourceFileNode or ModuleDeclarationNode) is { } container)
            {
                kind = CompletionKind.None; newIdentifier = false;
                foreach (var (symbol, exported) in await checker.GetLocalExportCompletionsAsync(container, cancellation)) symbols.Add(new(symbol, exported ? "12"u8 : "11"u8));
                return true;
            }
            return null;
        }
    }
}

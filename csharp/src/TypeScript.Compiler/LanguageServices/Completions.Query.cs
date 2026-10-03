using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class CompletionQuery(Checker checker, DocumentProjection projection, int position,
        CompletionCapabilities capabilities, UserPreferences preferences, CompilerProgram program, int? supplementalIndex, Func<SourceFileNode, DocumentProjection> project, CancellationToken cancellation,
        AutoImportCache? autoImportCache = null, bool includeSymbols = false, bool autoImportsAvailable = true)
    {
        private SourceFileNode File => projection.File;
        private CompilerOptions options => program.Configuration.Options;
        private bool JavaScript => File.ScriptKind is ScriptKind.JS or ScriptKind.JSX;
        private bool Checked => !JavaScript || (File.CheckJsDirective?.Enabled ?? options.CheckJs) == true;
        private SyntaxNode current = null!, location = null!;
        private SyntaxNode? previous, contextToken;
        private bool documentationHandled, simpleDetails, insideJSDocType, insideJSDocImport, typeOnly, newIdentifier;
        private CompletionKind kind;
        private KeywordFilter keywords;
        private Utf8String[]? commitCharacters;
        private readonly List<CompletionSymbol> symbols = [];
        private PropertyAccessExpressionNode? propertyAccess;
        private readonly record struct CompletionSymbol(Symbol Symbol, Utf8String SortText = default, bool This = false, bool Nullable = false,
            bool Await = false, bool SymbolMember = false, ObjectMethodSnippet? ObjectMethod = null, Utf8String DisplayName = default);
        private enum CompletionKind { None, Global, PropertyAccess, Member, ObjectProperty, String }

        internal async ValueTask<CompletionList?> GetAsync()
        {
            current = await SyntaxNavigation.GetTokenAtPositionAsync(File, position, cancellation);
            previous = await SyntaxNavigation.FindPrecedingTokenAsync(File, position, cancellation: cancellation);
            contextToken = previous is not null && position <= previous.End && (previous is IdentifierNode or PrivateIdentifierNode || Keyword(previous.Kind))
                ? await SyntaxNavigation.FindPrecedingTokenAsync(File, previous.Pos, cancellation: cancellation) : previous;
            location = await SyntaxNavigation.GetTouchingPropertyNameAsync(File, position, cancellation);
            if (await ModulePathCompletionsAsync() is { } paths) return paths;
            if (await StringCompletionsAsync() is { } strings) return strings;
            var documentation = await DocumentationAsync(current);
            if (documentationHandled) { simpleDetails = documentation is not null; return documentation; }
            if (previous?.Parent is { Kind: K.BreakStatement or K.ContinueStatement } jump && (previous is IdentifierNode || previous.Kind is K.BreakKeyword or K.ContinueKeyword))
                return await LabelsAsync(jump);
            if (contextToken is not null)
            {
                var import = await ImportStatementAsync(contextToken);
                if (import.KeywordOnly)
                {
                    simpleDetails = true;
                    return await ApplyDefaultsAsync([new(TokenFacts.Text(import.Keyword), 14, SortText: "15"u8)], import.NewIdentifier ? [] : AllCommitCharacters, previous);
                }
                if (import.Keyword == K.TypeKeyword) keywords = KeywordFilter.TypeKeyword;
                if (import.Replacement is not null && preferences.IncludeCompletionsForImportStatements == true) { importStatement = import; newIdentifier = import.NewIdentifier; }
                if (import.Replacement is null && await BlockedAsync())
                {
                    if (keywords == KeywordFilter.None) return null;
                    simpleDetails = true;
                    return await ApplyDefaultsAsync(Keywords().ToList(), CommitCharacters().NewIdentifier ? [] : AllCommitCharacters, location);
                }
            }
            typeOnly = insideJSDocType || insideJSDocImport || importStatement is not null && location.Parent is { } importParent && TypeOnlyImportOrExport(importParent)
                || !ContextValueLocation(contextToken)
                && (QuerySyntax.PartOfType(location) || ContextTypeLocation(contextToken) || await PossiblyTypeArgumentAsync(contextToken));
            if (contextToken?.Kind is K.DotToken or K.QuestionDotToken)
            {
                if (!await MembersAsync()) return null;
            }
            else
            {
                await JsxContextAsync();
                if (jsxOpen)
                {
                    foreach (var symbol in await checker.GetCompletionJsxTagsAsync(location, cancellation)) symbols.Add(new(symbol));
                    await GlobalsAsync(); keywords = KeywordFilter.None;
                }
                else if (jsxClose)
                {
                    var tag = ((JsxElementNode)contextToken!.Parent!.Parent!).OpeningElement!.TagName!;
                    if (await checker.GetSymbolAtLocationAsync(tag, cancellation) is { } symbol) symbols.Add(new(symbol));
                    kind = CompletionKind.Global; keywords = KeywordFilter.None;
                }
                else
                {
                    var objectResult = await TypeArgumentObjectSymbolsAsync() ?? await ObjectSymbolsAsync();
                    objectResult ??= await ImportSymbolsAsync();
                    if (objectResult == false)
                    {
                        if (keywords == KeywordFilter.None) return null;
                        simpleDetails = true;
                        return await ApplyDefaultsAsync(Keywords().ToList(), newIdentifier ? [] : AllCommitCharacters, location);
                    }
                    if (objectResult is null && !await DeclarationKeywordsAsync() && !await JsxSymbolsAsync()) await GlobalsAsync();
                }
            }
            if (await JsxClosingAsync() is { } closing) return closing;
            await ContextualCompletionsAsync();
            await FilterCaseValuesAsync();
            if (Checked && !newIdentifier && symbols.Count == 0 && autoImports.Count == 0 && keywords == KeywordFilter.None) return null;
            return await EntriesAsync();
        }

        private async ValueTask GlobalsAsync()
        {
            kind = CompletionKind.Global;
            keywords = InFunctionBody(contextToken) ? KeywordFilter.FunctionBody : KeywordFilter.All;
            (newIdentifier, commitCharacters) = CommitCharacters();
            int adjusted = previous != contextToken ? await StartAsync(previous!) : position;
            var scope = contextToken;
            while (scope is not null && !(scope.Pos <= adjusted && (adjusted < scope.End || !await SyntaxNavigation.IsCompletedNodeAsync(scope, File, cancellation))))
                scope = scope.Parent;
            scope ??= File;
            var meaning = SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Alias | (typeOnly ? 0 : SymbolFlags.Value);
            foreach (var symbol in await checker.GetSymbolsInScopeAsync(scope, meaning, cancellation))
                symbols.Add(new(symbol));
            for (int i = 0; i < symbols.Count; i++)
                if (!checker.IsArgumentsSymbol(symbols[i].Symbol) && !symbols[i].Symbol.Declarations.Any(declaration => SemanticSyntax.Source(declaration) == File))
                    symbols[i] = symbols[i] with { SortText = "15"u8 };
            if (scope is not SourceFileNode && await checker.GetCompletionThisTypeAsync(scope, SemanticSyntax.ClassLike(scope.Parent) ? scope : null, cancellation) is { } thisType
                && !await checker.IsGlobalCompletionTypeAsync(thisType, File, cancellation))
                foreach (var symbol in await checker.GetPossibleCompletionPropertiesAsync(thisType, cancellation)) symbols.Add(new(symbol, "14"u8, This: true));
            if (typeOnly) keywords = contextToken?.Parent is AsExpressionNode or TypeAssertionNode ? KeywordFilter.TypeAssertion : KeywordFilter.Type;
            await CollectAutoImportsAsync();
        }

        private async ValueTask<CompletionList?> LabelsAsync(SyntaxNode jump)
        {
            List<CompletionItem> entries = [];
            HashSet<Utf8String> names = [];
            for (SyntaxNode? node = jump; node is not null && !Signatures.FunctionLike(node); node = node.Parent)
                if (node is LabeledStatementNode { Label: { } label } && names.Add(label.Text)) entries.Add(new(label.Text, 10, SortText: "11"u8));
            return entries.Count == 0 ? null : await ApplyDefaultsAsync(entries, AllCommitCharacters, previous);
        }

        internal async ValueTask<CompletionItem> ResolveAsync(CompletionItem item)
        {
            if (item.Data?.AutoImport is { } fix)
            {
                if (item.Data.IsImportStatementCompletion) return item;
                var edits = await new ImportFix(fix).EditsAsync(projection, options, preferences, cancellation);
                return edits.Safe ? item with { Detail = edits.Description, AdditionalTextEdits = edits.Edits } : item;
            }
            forItemResolve = true;
            var completions = await GetAsync();
            var name = item.Data!.Name;
            if (simpleDetails) return !documentationHandled && completions?.Items.Any(entry => entry.Label == name) != true || item.Detail is not null ? item : item with { Detail = name };
            if (stringTypes.Any(type => type.Value is Utf8String value && value == name)) return item.Detail is null ? item with { Detail = name } : item;
            if (literals.Any(literal => LiteralName(literal) == name)) return item.Detail is null ? item with { Detail = name } : item;
            foreach (var entry in symbols)
            {
                var (displayName, _) = DisplayName(entry);
                if (displayName != name || item.Data.Source != "ObjectLiteralMemberWithComma/"u8
                    && item.Data.Source != "ObjectLiteralMethodSnippet/"u8 && item.Data.Source != "ClassMemberSnippet/"u8
                    && item.Data.Source != (entry.This ? (Utf8String)"ThisProperty/"u8 : Utf8String.Empty)) continue;
                var detailLocation = stringToken ?? location;
                var info = await SymbolDisplay.QuickInfoAsync(checker, entry.Symbol, detailLocation, new(0, 0), false, cancellation);
                var docs = await new SymbolDocumentation(checker).HoverAsync(entry.Symbol, detailLocation, info.Declaration, capabilities.Markdown, false, MapDocumentation, cancellation);
                return item with { Detail = item.Detail ?? (info.Text.IsEmpty ? null : info.Text), Documentation = docs.IsEmpty ? item.Documentation
                    : new(docs.TrimStart((byte)'\n'), capabilities.Markdown ? "markdown"u8 : "plaintext"u8) };
            }
            return Keyword(TokenFacts.FromText(name.Span)) && item.Detail is null ? item with { Detail = name } : item;
        }

        private (Utf8String, DocumentRange, MappingFidelity) MapDocumentation(SourceFileNode file, int start, int end)
        {
            var mapped = project(file);
            var range = mapped.ToRange(start, end);
            return (DocumentUris.FromFileName(mapped.OriginalFileName), range.Range, range.Fidelity);
        }

        private ValueTask<int> StartAsync(SyntaxNode node) => SyntaxNavigation.GetStartAsync(node, File, cancellation: cancellation);
        private async ValueTask<bool> EditingAsync(SyntaxNode node) => await StartAsync(node) <= position && position <= node.End;
        private static bool Keyword(K kind) => kind is >= K.FirstKeyword and <= K.LastKeyword;
        private static K KeywordOf(SyntaxNode node) => node is IdentifierNode id ? TokenFacts.FromText(id.Text.Span) : node.Kind;
        private int Line(int offset) => File.Source.GetLineAndCharacter(offset).Line;
        private static readonly Utf8String[] AllCommitCharacters = ["."u8, ","u8, ";"u8];
    }
}

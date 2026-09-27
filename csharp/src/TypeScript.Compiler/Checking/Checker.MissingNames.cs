using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly Dictionary<string, Symbol> primitiveSuggestions = new(StringComparer.Ordinal);
    internal Dictionary<SyntaxNode, Symbol> SuggestedNameDeclarations { get; } = [];

    internal async ValueTask MissingQualifiedAsync(
        SyntaxNode name,
        SyntaxNode right,
        Symbol parent,
        S meaning,
        CancellationToken cancellation)
    {
        string namespaceName = await FullyQualifiedNameAsync(parent, null, cancellation);
        string memberName = CheckerDiagnostic.DeclarationName(right);
        var exports = await ExportsAsync(parent, cancellation);
        if (await SymbolSuggestions.FindAsync(memberName, exports.Values, S.ModuleMember, cancellation) is { } suggestion)
        {
            Error(right, 2724, namespaceName, memberName, TypeDisplay.SymbolName(suggestion));
            return;
        }
        if (name is QualifiedNameNode)
        {
            var containing = name;
            while (containing.Parent is QualifiedNameNode outer)
                containing = outer;
            if (program.Globals.Types.ContainsKey("Object")
                && (meaning & S.Type) != 0
                && containing.Parent?.Kind != SyntaxKind.TypeOfExpression
                && await QualifiedNameValueAsync(containing, cancellation) is not null)
            {
                Error(containing, 2749, SyntaxNameText.Get(containing));
                return;
            }
        }
        if ((meaning & S.Namespace) != 0 && name.Parent is QualifiedNameNode qualified
            && program.Symbols.Lookup(exports, memberName, S.Type) is { } exportedType)
        {
            Error(qualified.Right!, 2713, TypeDisplay.SymbolName(exportedType), SyntaxNameText.Get(qualified.Right!));
            return;
        }
        Error(right, 2694, namespaceName, memberName);
    }

    private async ValueTask<Symbol?> QualifiedNameValueAsync(SyntaxNode node, CancellationToken cancellation)
    {
        while (node is QualifiedNameNode qualified)
            node = qualified.Left!;
        if (node is not IdentifierNode identifier)
            return null;
        var symbol = program.Symbols.NameResolver(cancellation).Resolve(identifier, identifier.Text, S.Value, isUse: true);
        while (symbol is not null && node.Parent is QualifiedNameNode parent)
        {
            symbol = await Properties.PropertyAsync(await Values.GetAsync(symbol, cancellation),
                SyntaxNameText.Get(parent.Right!), cancellation: cancellation);
            node = parent;
        }
        return symbol;
    }

    internal async ValueTask FailedNameAsync(SyntaxNode? location, string name, S meaning, DiagnosticMessage message)
    {
        if (location is not null)
        {
            if (name == "const"
                && location.Parent is TypeReferenceNode { Parent: { } assertion }
                && SemanticSyntax.ConstAssertion(assertion)
                || location.Parent?.Kind == SyntaxKind.JSDocLink)
                return;
            if (await MissingNames.CheckAsync(location, name).ConfigureAwait(false)
                || await ExtendingInterfaceAsync(location, default).ConfigureAwait(false)
                || await WrongNameMeaningAsync(location, name, meaning).ConfigureAwait(false))
                return;
        }
        if (LibraryFeatures.NameLibrary(name) is { } library)
        {
            program.Error(location, message, name, library);
            return;
        }
        var suggestion = program.Symbols.NameResolver(lookup: SuggestionLookup).Resolve(location, name, meaning);
        if (suggestion is not null && suggestion.ValueDeclaration is not ModuleDeclarationNode { Keyword: SyntaxKind.GlobalKeyword })
        {
            bool uncheckedJs = location is not null && UncheckedNameSuggestion(location, suggestion);
            int code = meaning == S.Namespace ? 2833 : uncheckedJs ? 2570 : 2552;
            if (uncheckedJs)
                program.Suggestion(location!, code, suggestion.Name);
            else
                program.Error(location, meaning == S.Namespace ? Messages.Cannot_find_namespace_0_Did_you_mean_1
                : Messages.Cannot_find_name_0_Did_you_mean_1, name, suggestion.Name);
            if (location is not null && suggestion.ValueDeclaration is not null)
                SuggestedNameDeclarations[location] = suggestion;
            return;
        }
        program.Error(location, message, name);
    }

    private async ValueTask<bool> WrongNameMeaningAsync(SyntaxNode location, string name, S meaning)
    {
        async ValueTask<Symbol?> Find(S flags) => await program.Aliases.SymbolAsync(
            program.Symbols.NameResolver().Resolve(location, name, flags)).ConfigureAwait(false);
        if (meaning == S.Namespace && await Find(S.Type & ~S.Namespace) is { } typeSymbol)
        {
            if (location.Parent is QualifiedNameNode qualified
                && await Properties.PropertyAsync(
                    await Declared.GetAsync(typeSymbol).ConfigureAwait(false),
                    ((IdentifierNode)qualified.Right!).Text).ConfigureAwait(false) is not null)
                Error(qualified, 2713, name, SyntaxNameText.Get(qualified.Right!));
            else
                Error(location, 2702, name);
            return true;
        }
        bool primitive = name is "any" or "string" or "number" or "boolean" or "never" or "unknown";
        if (primitive && location.Parent is ExportSpecifierNode)
        {
            Error(location, 2661, name);
            return true;
        }
        if ((meaning & (S.Value & ~S.Type)) != 0)
        {
            if (await Find(S.NamespaceModule) is not null)
            {
                if (!ExportAssignmentName(location))
                    Error(location, 2708, name);
                return true;
            }
        }
        else if ((meaning & (S.Type & ~S.Value)) != 0 && await Find(S.Module) is not null)
        {
            Error(location, 2709, name);
            return true;
        }
        if ((meaning & S.Value) != 0)
        {
            if (primitive)
            {
                if (location.Parent?.Parent is HeritageClauseNode heritage)
                {
                    if (heritage.Parent is InterfaceDeclarationNode && heritage.Token == SyntaxKind.ExtendsKeyword)
                        Error(location, 2840, name);
                    else if (SemanticSyntax.ClassLike(heritage.Parent))
                        Error(location, heritage.Token == SyntaxKind.ExtendsKeyword ? 2863 : 2864, name);
                }
                else
                    Error(location, 2693, name);
                return true;
            }
            if (await Find(S.Type & ~S.Value) is { } symbol
                && (await program.Aliases.FlagsAsync(symbol).ConfigureAwait(false) & S.Value) == 0)
            {
                if (ExportAssignmentName(location))
                    return true;
                int code = 2693;
                if (name is "Promise" or "Symbol" or "Map" or "WeakMap" or "Set" or "WeakSet")
                    code = 2585;
                else
                {
                    var parent = location.Parent;
                    while (parent is ComputedPropertyNameNode or PropertySignatureDeclarationNode)
                        parent = parent.Parent;
                    if (parent is TypeLiteralNode { Members.Count: 1 }
                        && await Declared.GetAsync(symbol).ConfigureAwait(false) is UnionType union)
                    {
                        bool mapped = true;
                        foreach (var part in union.Types)
                            if (!await Predicates.AssignableAsync(part, TypeFlags.StringOrNumberLiteral, strict: true))
                            {
                                mapped = false;
                                break;
                            }
                        if (mapped)
                            code = 2690;
                    }
                }
                Error(location, code, code == 2690 ? [name, name == "K" ? "P" : "K"] : [name]);
                return true;
            }
        }
        if ((meaning & (S.Type & ~S.Namespace)) != 0 && await Find(S.Value & ~S.Type) is { } valueSymbol
            && (valueSymbol.Flags & S.Namespace) == 0)
        {
            Error(location, 2749, name);
            return true;
        }
        return false;
    }

    private Symbol? SuggestionLookup(IReadOnlyDictionary<string, Symbol>? table, string name, S meaning)
    {
        if (program.Symbols.Lookup(table, name, meaning) is { } exact)
            return exact;
        if (table is null)
            return null;
        IEnumerable<Symbol> candidates = table.Values;
        if ((meaning & S.GlobalLookup) != 0)
        {
            var extras = new List<Symbol>();
            foreach (string builtin in new[] { "String", "Number", "Boolean", "Object", "BigInt", "Symbol" })
                if (table.ContainsKey(builtin))
                {
                    if (!primitiveSuggestions.TryGetValue(builtin, out var symbol))
                        primitiveSuggestions[builtin] = symbol = new(S.TypeAlias | S.Transient, builtin.ToLowerInvariant());
                    extras.Add(symbol);
                }
            candidates = candidates.Concat(extras);
        }
        return SymbolSuggestions.FindAsync(name, candidates, meaning).GetAwaiter().GetResult();
    }

    private bool UncheckedNameSuggestion(SyntaxNode location, Symbol suggestion)
    {
        var file = SemanticSyntax.Source(location)!;
        if (file.ScriptKind is not (ScriptKind.JS or ScriptKind.JSX) || file.CheckJsDirective is not null
            || program.Symbols.Program.Configuration.Options.Boolean("checkJs") is not null)
            return false;
        var declarationFile = SemanticSyntax.Source(suggestion.Declarations.FirstOrDefault());
        return declarationFile is null || declarationFile == file || program.Symbols.Binding(declarationFile)?.IsModule == true;
    }

    private static bool ExportAssignmentName(SyntaxNode node)
    {
        while (node.Parent is PropertyAccessExpressionNode or QualifiedNameNode)
            node = node.Parent;
        return node.Parent is ExportAssignmentNode assignment && assignment.Expression == node;
    }
}

using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

// Real program/binding inputs with declared ES module dependencies. Unsupported
// expression, interop, computed-name and relation queries fail explicitly.
internal sealed partial class CheckerEnvironment(TypeContext context, CheckerLinks links) : ICheckerSymbolHost, ITypeParameterScopeHost,
    IAliasResolverHost, IEntityNameHost, IAliasTargetHost, IModuleExportHost, IValueUseHost, IDeclarationOrderHost
{
    internal DeclarationOrder DeclarationOrder { get; private set; } = null!;
    internal Deprecations Deprecations { get; private set; } = null!;
    internal ReferenceSymbols ReferenceSymbols { get; private set; } = null!;
    internal ValueUseChecks ValueUses { get; private set; } = null!;
    internal List<DiagnosticCode> ValueSuggestions { get; } = [];
    internal List<Diagnostic> SuggestionDiagnostics { get; } = [];
    private readonly HashSet<(SyntaxNode Node, DiagnosticCode Code, Utf8String Name)> reportedSuggestions = [];

    internal void Suggestion(SyntaxNode node, DiagnosticCode code, Utf8String name)
        => Suggestion(node, CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), name));

    internal void Suggestion(SyntaxNode node, Diagnostic diagnostic)
    {
        if (reportedSuggestions.Add((node, diagnostic.Code, DiagnosticEqualityComparer.Identity(diagnostic))))
        {
            ValueSuggestions.Add(diagnostic.Code);
            SuggestionDiagnostics.Add(diagnostic with { Message = diagnostic.Message with { Category = DiagnosticCategory.Suggestion } });
        }
    }

    internal void DeprecatedSuggestion(SyntaxNode node, IReadOnlyList<SyntaxNode> declarations, Utf8String name)
        => Suggestion(node, Deprecations.Related(CheckerDiagnostic.Create(node, Messages.X_0_is_deprecated, name), declarations));

    internal CheckerSymbols Symbols { get; private set; } = null!;
    internal TypeParameterScopes Scopes { get; private set; } = null!;
    internal GlobalTypes Globals { get; private set; } = null!;
    internal AliasResolver Aliases { get; private set; } = null!;
    internal AliasTargets AliasTargets { get; private set; } = null!;
    internal EntityNames EntityNames { get; private set; } = null!;
    internal TypeResolutionStack AliasResolutions { get; private set; } = null!;
    internal ModuleTypes ModuleTypes { get; private set; } = null!;
    internal ModuleExports ModuleExports { get; private set; } = null!;
    internal Checker? SemanticChecker { get; set; }
    private readonly HashSet<(SyntaxNode? Node, DiagnosticCode Code, Utf8String Arguments)> reported = [];
    internal List<DiagnosticCode> Diagnostics { get; } = [];
    internal List<(SyntaxNode? Node, Diagnostic Diagnostic)> DiagnosticFiles { get; } = [];

    private void AddDiagnostic(SyntaxNode? node, DiagnosticMessage message, Utf8String[] arguments)
    {
        Diagnostics.Add(message.Code);
        DiagnosticFiles.Add((node, CheckerDiagnostic.Create(node, message, arguments)));
    }

    private void AddDiagnostic(SyntaxNode? node, DiagnosticCode code)
        => AddDiagnostic(node, DiagnosticLocalization.GetMessage(code), []);

    internal Action? BeforeGlobalTypes { get; set; }
    internal Action? BeforeResolveType { get; set; }
    internal Action? BeforeValueResolution { get; set; }
    internal Func<SyntaxNode, Utf8String, ValueTask<bool>>? MissingPrefixCheck { get; set; }
    internal Func<SyntaxNode, ValueTask<bool>>? ExtendingInterfaceCheck { get; set; }
    internal Func<Symbol, Symbol>? LateMemberSymbol { get; set; }
    internal Func<Symbol, CancellationToken, ValueTask<Signature?>>? CallSignature { get; set; }
    internal Func<PropertyDeclarationNode, SyntaxNode, SyntaxNode, CancellationToken, ValueTask<bool>>? StaticInitialization { get; set; }
    internal Dictionary<SyntaxNode, Signature> ContextualSignatures { get; } = [];

    public void Bind(CheckerSymbols symbols)
    {
        Symbols = symbols;
        AliasResolutions = new(links);
        Aliases = new(symbols, links, AliasResolutions, this);
        EntityNames = new(symbols, Aliases, this);
        DeclarationOrder = new(symbols.Program.Configuration.Options, this);
        ValueUses = new(symbols, Aliases, DeclarationOrder, this);
        ReferenceSymbols = new(symbols, links);
        Deprecations = new(symbols);
        AliasTargets = new(symbols, Aliases, EntityNames, this);
        ModuleTypes = new(context, links, Aliases, new(symbols.Program.SourceFiles.Select(f => f.Syntax).ToArray()));
        ModuleExports = new(links, Aliases, AliasTargets, this);
        Scopes = new(context, links, symbols, this);
        Globals = new(context, links, symbols, Scopes, (node, message, arguments) => Error(node, message, arguments));
    }

    public Symbol ResolveSymbol(Symbol symbol)
        => Aliases.SymbolAsync(symbol).GetAwaiter().GetResult()!;

    public Symbol LateBoundSymbol(Symbol symbol)
        =>
            LateMemberSymbol is { } resolve ? resolve(symbol)
                : (symbol.Flags & SymbolFlags.ClassMember) == 0 || symbol.Name != Symbol.InternalComputed
                ? symbol
                : throw new InvalidOperationException("Checker requires computed member binding");

    public SymbolFlags GetSymbolFlags(Symbol symbol) => Aliases.FlagsAsync(symbol).GetAwaiter().GetResult();

    public void MergeConflict(Symbol target, Symbol source, bool namespaceConflict)
    {
        static SyntaxNode? Location(SyntaxNode? declaration) => declaration is null ? null : LateMembers.Name(declaration) ?? declaration;
        if (namespaceConflict)
        {
            Error(
                Location(source.Declarations.FirstOrDefault()),
                Messages.Cannot_augment_module_0_with_value_exports_because_it_resolves_to_a_non_module_entity,
                target.Name);
            return;
        }
        var flags = target.Flags | source.Flags;
        var message = (flags & SymbolFlags.Enum) != 0 ? Messages.Enum_declarations_can_only_merge_with_namespace_or_other_enum_declarations
            : (flags & SymbolFlags.BlockScopedVariable) != 0
                ? Messages.Cannot_redeclare_block_scoped_variable_0
                : Messages.Duplicate_identifier_0;
        Report(source, target);
        Report(target, source);
        void Report(Symbol symbol, Symbol other)
        {
            var file = SemanticSyntax.Source(symbol.Declarations.FirstOrDefault());
            if (file is { ScriptKind: ScriptKind.JS or ScriptKind.JSX, CheckJsDirective: null }
                && Symbols.Program.Configuration.Options.CheckJs is null)
                return;
            foreach (var declaration in symbol.Declarations)
            {
                var node = Location(declaration)!;
                var sourceName = SemanticSyntax.Name(source.ValueDeclaration ?? source.Declarations.FirstOrDefault());
                Utf8String name = sourceName is ComputedPropertyNameNode or StringLiteralNode or NumericLiteralNode
                    ? CheckerDiagnostic.DeclarationName(sourceName) : source.Name.Length == 0 ? Utf8Literals.MissingDisplay : source.Name;
                var related = new List<Diagnostic>();
                var locations = new HashSet<SyntaxNode>();
                foreach (var otherDeclaration in other.Declarations)
                {
                    var otherNode = Location(otherDeclaration)!;
                    if (otherNode != node && related.Count < 5 && locations.Add(otherNode))
                        related.Add(related.Count == 0
                            ? CheckerDiagnostic.Create(otherNode, Messages.X_0_was_also_declared_here, name)
                            : CheckerDiagnostic.Create(otherNode, Messages.X_and_here));
                }
                Diagnostics.Add(message.Code);
                DiagnosticFiles.Add((node, CheckerDiagnostic.Create(node, message, name) with { RelatedInformation = related }));
            }
        }
    }

    public void Error(SyntaxNode? node, DiagnosticMessage message, params Utf8String[] arguments)
    {
        Utf8String key = Utf8String.Frame(arguments);
        if (reported.Add((node, message.Code, key)))
            AddDiagnostic(node, message, arguments);
    }

    public async ValueTask InitializeGlobalTypesAsync(CheckerSymbols symbols, CancellationToken cancellation)
    {
        BeforeGlobalTypes?.Invoke();
        await Globals.InitializeAsync(cancellation).ConfigureAwait(false);
    }

    public ValueTask<Type> ImportAttributesTypeAsync(Symbol symbol, CancellationToken cancellation)
    {
        if (SemanticChecker is { } checker)
            return checker.ModuleImportAttributesAsync(symbol, cancellation);
        if (symbol.Declarations.OfType<ModuleDeclarationNode>().Any(d => d.Attributes is not null))
            throw new InvalidOperationException("Checker requires import attribute type resolution");
        return ValueTask.FromResult<Type>(context.EmptyObjectType);
    }

    public ValueTask<bool> IdenticalTypesAsync(Type first, Type second, CancellationToken cancellation)
        => SemanticChecker is { } checker ? checker.Relations.RelatedAsync(first, second, RelationKind.Identity, cancellation)
            : first == context.EmptyObjectType && second == context.EmptyObjectType ? ValueTask.FromResult(true)
            : throw new InvalidOperationException("Checker requires import attribute type identity");

    public ValueTask<Symbol?> ResolveAugmentationAsync(SyntaxNode moduleName, bool reportNotFound, CancellationToken cancellation)
    {
        if (SemanticChecker is { } checker)
            return checker.ResolveImportModuleAsync(moduleName, moduleName, null, cancellation,
                missingModuleCode: DiagnosticCode.InvalidModuleNameInAugmentationModule0CannotBeFound, reportUnresolved: reportNotFound);
        var file = Symbols.Binding(moduleName)!.SourceFile;
        var programFile = Symbols.Program.GetFile(file.FileName)!;
        var resolution = programFile.Resolutions.FirstOrDefault(r => r.Node == moduleName)?.Resolution;
        Symbol? result = resolution is { IsResolved: true } ? Symbols.Program.GetFile(resolution.FileName)?.Binding.Symbol : null;
        Utf8String name = ((StringLiteralNode)moduleName).Text;
        result ??= Symbols.Globals.GetValueOrDefault(Utf8String.Concat("\""u8, name, "\""u8));
        if (result is null)
        {
            int longest = -1;
            foreach (var pattern in Symbols.PatternModules)
            {
                int star = pattern.Pattern.Span.IndexOf((byte)'*');
                if (star > longest && name.Length >= pattern.Pattern.Length - 1
                    && name.Span.StartsWith(pattern.Pattern.Span.Slice(0, star), StringComparison.Ordinal)
                    && name.Span.EndsWith(pattern.Pattern.Span.Slice(star + 1), StringComparison.Ordinal))
                {
                    longest = star;
                    result = pattern.Symbol;
                }
            }
        }
        if (result is null && reportNotFound)
            AddDiagnostic(moduleName, Messages.Invalid_module_name_in_augmentation_module_0_cannot_be_found, [name]);
        return ValueTask.FromResult(Symbols.Merger.GetMergedSymbol(result));
    }

    public ValueTask<Symbol> ExternalModuleSymbolAsync(Symbol symbol, CancellationToken cancellation)
        => RequiredExternalModuleAsync(symbol, cancellation);

    private async ValueTask<Symbol> RequiredExternalModuleAsync(Symbol symbol, CancellationToken cancellation)
        => await AliasTargets.ExternalModuleAsync(symbol, false, cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Present module resolved to no symbol");

    public ValueTask<IReadOnlyDictionary<Utf8String, Symbol>> ResolvedExportsAsync(Symbol symbol, CancellationToken cancellation)
        => ExportsAsync(symbol, cancellation);

    public bool InvalidInitializer(SyntaxNode? location, Utf8String name, SyntaxNode declaration, Symbol? result)
        => ValueUses.InvalidInitializer(location, name, declaration, result);

    public void FailedResolution(SyntaxNode? location, Utf8String name, SymbolFlags meaning, DiagnosticMessage message)
    {
        if (SemanticChecker is { } checker)
        {
            checker.FailedNameAsync(location, name, meaning, message).GetAwaiter().GetResult();
            return;
        }
        if (location is not null && MissingPrefixCheck is not null && MissingPrefixCheck(location, name).GetAwaiter().GetResult())
            return;
        if (location is not null && ExtendingInterfaceCheck is not null && ExtendingInterfaceCheck(location).GetAwaiter().GetResult())
            return;
        if (location is not null && (meaning & SymbolFlags.Value) != 0)
        {
            var current = location;
            while (current.Parent is PropertyAccessExpressionNode or QualifiedNameNode)
                current = current.Parent;
            if (current.Parent is ExportAssignmentNode export && export.Expression == current)
            {
                // Export assignments can name types and uninstantiated namespaces.
                // Their value/type restrictions are checked by the module pass.
                var target = Symbols.NameResolver().Resolve(
                    location,
                    name,
                    SymbolFlags.NamespaceModule | SymbolFlags.Type & ~SymbolFlags.Value);
                if (Aliases.SymbolAsync(target).GetAwaiter().GetResult() is not null)
                    return;
            }
        }
        Error(location, message, name);
    }

    public void SuccessfulResolution(
        SyntaxNode? location,
        Symbol symbol,
        SymbolFlags meaning,
        SyntaxNode? last,
        SyntaxNode? declaration,
        bool deferred)
    {
        BeforeValueResolution?.Invoke();
        ValueUses.ResolvedAsync(location, symbol, meaning, last, declaration, deferred).GetAwaiter().GetResult();
    }

    public void ValueUseError(SyntaxNode? node, DiagnosticMessage message, params Utf8String[] arguments) => Error(node, message, arguments);

    public void ValueUseSuggestion(SyntaxNode node, DiagnosticMessage message, Utf8String name) => Suggestion(node, message.Code, name);

    public void DeclarationRelatedInfo(SyntaxNode? location, DiagnosticCode code, SyntaxNode declaration, bool typeOnly, Utf8String name)
    {
        var message = !typeOnly ? Messages.X_0_is_declared_here
            : declaration is ExportSpecifierNode or ExportDeclarationNode or NamespaceExportNode
                ? Messages.X_0_was_exported_here : Messages.X_0_was_imported_here;
        var note = CheckerDiagnostic.Create(declaration, message, name);
        for (int i = DiagnosticFiles.Count - 1; i >= 0; i--)
            if (DiagnosticFiles[i].Node == location && DiagnosticFiles[i].Diagnostic.Code == code)
            {
                var diagnostic = DiagnosticFiles[i].Diagnostic;
                if (!diagnostic.RelatedInformation.Any(d => d.Code == note.Code && d.Start == note.Start && d.FileName == note.FileName))
                    DiagnosticFiles[i] = (location, diagnostic with { RelatedInformation = [.. diagnostic.RelatedInformation, note] });
                return;
            }
    }

    public bool ValidTypeOnlyUse(SyntaxNode node) => ReferenceSyntax.ValidTypeOnlyUse(node);

    public bool MissingPrefix(SyntaxNode node, Utf8String name) => MissingPrefixCheck is { } check ? check(node, name).GetAwaiter().GetResult()
        : throw new InvalidOperationException("Checker requires missing-prefix diagnostics");

    public ValueTask<bool> InitializedInStaticBlocksAsync(PropertyDeclarationNode declaration, SyntaxNode usage,
        SyntaxNode initializer, CancellationToken cancellation)
        => StaticInitialization is { } check ? check(declaration, usage, initializer, cancellation)
            : declaration.Parent!.DescendantsAndSelf().OfType<ClassStaticBlockDeclarationNode>().Any()
            ? throw new InvalidOperationException("Checker requires static property initialization flow") : ValueTask.FromResult(false);

    public bool IsContextSensitive(SyntaxNode declaration)
    {
        if (ContextualSignatures.ContainsKey(declaration))
            return true;
        return FunctionSyntax.Sensitive(declaration, Symbols);
    }

    public ValueTask<Signature?> FirstCallSignatureAsync(Symbol symbol, CancellationToken cancellation)
        => ContextualSignatures.TryGetValue(symbol.Declarations[0], out var signature) ? ValueTask.FromResult<Signature?>(signature)
            : CallSignature?.Invoke(symbol, cancellation) ?? throw new InvalidOperationException("Checker requires function signatures");

    public async ValueTask<Symbol?> ResolveTypeNameAsync(SyntaxNode name, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        BeforeResolveType?.Invoke();
        return await EntityNames.ResolveAsync(name, SymbolFlags.Type, true, cancellation: cancellation).ConfigureAwait(false);
    }
}

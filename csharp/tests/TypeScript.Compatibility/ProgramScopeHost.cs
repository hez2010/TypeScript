using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

// Real program/binding inputs with declared ES module dependencies. Unsupported
// expression, interop, computed-name and relation queries fail explicitly.
internal sealed partial class ProgramScopeHost(TypeContext context, CheckerLinks links) : ICheckerSymbolHost, ITypeParameterScopeHost,
    IAliasResolverHost, IEntityNameHost, IAliasTargetHost, IModuleExportHost, IValueUseHost, IDeclarationOrderHost
{
    internal DeclarationOrder DeclarationOrder { get; private set; } = null!;
    internal ReferenceSymbols ReferenceSymbols { get; private set; } = null!;
    internal ValueUseChecks ValueUses { get; private set; } = null!;
    internal List<int> ValueSuggestions { get; } = [];
    internal CheckerSymbols Symbols { get; private set; } = null!;
    internal TypeParameterScopes Scopes { get; private set; } = null!;
    internal GlobalTypes Globals { get; private set; } = null!;
    internal AliasResolver Aliases { get; private set; } = null!;
    internal AliasTargets AliasTargets { get; private set; } = null!;
    internal EntityNames EntityNames { get; private set; } = null!;
    internal TypeResolutionStack AliasResolutions { get; private set; } = null!;
    internal ModuleTypes ModuleTypes { get; private set; } = null!;
    internal ModuleExports ModuleExports { get; private set; } = null!;
    private readonly HashSet<(SyntaxNode? Node, int Code, string Arguments)> reported = [];
    internal List<int> Diagnostics { get; } = [];
    internal Action? BeforeGlobalTypes { get; set; }
    internal Action? BeforeResolveType { get; set; }
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
            (symbol.Flags & SymbolFlags.ClassMember) == 0 || symbol.Name != Symbol.InternalPrefix + "computed"
                ? symbol
                : throw new InvalidOperationException("Probe requires computed member binding");

    public SymbolFlags GetSymbolFlags(Symbol symbol) => Aliases.FlagsAsync(symbol).GetAwaiter().GetResult();

    public void MergeConflict(Symbol target, Symbol source, bool namespaceConflict)
        => throw new InvalidOperationException("Probe requires checker merge diagnostic attribution");

    public void Error(SyntaxNode? node, DiagnosticMessage message, params string[] arguments)
    {
        string key = string.Concat(arguments.Select(s => s.Length + ":" + s));
        if (reported.Add((node, message.Code, key)))
            Diagnostics.Add(message.Code);
    }

    public async ValueTask InitializeGlobalTypesAsync(CheckerSymbols symbols, CancellationToken cancellation)
    {
        BeforeGlobalTypes?.Invoke();
        await Globals.InitializeAsync(cancellation).ConfigureAwait(false);
    }

    public ValueTask<Type> ImportAttributesTypeAsync(Symbol symbol, CancellationToken cancellation)
    {
        if (symbol.Declarations.OfType<ModuleDeclarationNode>().Any(d => d.Attributes is not null))
            throw new InvalidOperationException("Probe requires import attribute type resolution");
        return ValueTask.FromResult<Type>(context.EmptyObjectType);
    }

    public ValueTask<bool> IdenticalTypesAsync(Type first, Type second, CancellationToken cancellation)
        => first == context.EmptyObjectType && second == context.EmptyObjectType ? ValueTask.FromResult(true)
            : throw new InvalidOperationException("Probe requires import attribute type identity");

    public ValueTask<Symbol?> ResolveAugmentationAsync(SyntaxNode moduleName, bool reportNotFound, CancellationToken cancellation)
    {
        var file = Symbols.Binding(moduleName)!.SourceFile;
        var programFile = Symbols.Program.GetFile(file.FileName)!;
        var resolution = programFile.Resolutions.FirstOrDefault(r => r.Node == moduleName)?.Resolution;
        Symbol? result = resolution is { IsResolved: true } ? Symbols.Program.GetFile(resolution.FileName)?.Binding.Symbol : null;
        string name = ((StringLiteralNode)moduleName).Text;
        result ??= Symbols.Globals.GetValueOrDefault('"' + name + '"');
        if (result is null)
        {
            int longest = -1;
            foreach (var pattern in Symbols.PatternModules)
            {
                int star = pattern.Pattern.IndexOf('*');
                if (star > longest && name.Length >= pattern.Pattern.Length - 1
                    && name.StartsWith(pattern.Pattern[..star], StringComparison.Ordinal)
                    && name.EndsWith(pattern.Pattern[(star + 1)..], StringComparison.Ordinal))
                {
                    longest = star;
                    result = pattern.Symbol;
                }
            }
        }
        if (result is null && reportNotFound)
            Diagnostics.Add(2664);
        return ValueTask.FromResult(Symbols.Merger.GetMergedSymbol(result));
    }

    public ValueTask<Symbol> ExternalModuleSymbolAsync(Symbol symbol, CancellationToken cancellation)
        => RequiredExternalModuleAsync(symbol, cancellation);

    private async ValueTask<Symbol> RequiredExternalModuleAsync(Symbol symbol, CancellationToken cancellation)
        => await AliasTargets.ExternalModuleAsync(symbol, false, cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Present module resolved to no symbol");

    public ValueTask<IReadOnlyDictionary<string, Symbol>> ResolvedExportsAsync(Symbol symbol, CancellationToken cancellation)
        => ExportsAsync(symbol, cancellation);

    public bool InvalidInitializer(SyntaxNode? location, string name, SyntaxNode declaration, Symbol? result)
        => ValueUses.InvalidInitializer(location, name, declaration, result);

    public void FailedResolution(SyntaxNode? location, string name, SymbolFlags meaning, DiagnosticMessage message)
        => Error(location, message, name);

    public void SuccessfulResolution(
        SyntaxNode? location,
        Symbol symbol,
        SymbolFlags meaning,
        SyntaxNode? last,
        SyntaxNode? declaration,
        bool deferred)
        => ValueUses.ResolvedAsync(location, symbol, meaning, last, declaration, deferred).GetAwaiter().GetResult();

    public void ValueUseError(SyntaxNode? node, DiagnosticMessage message, params string[] arguments) => Error(node, message, arguments);

    public void ValueUseSuggestion(SyntaxNode node, DiagnosticMessage message, string name) => ValueSuggestions.Add(message.Code);

    public void DeclarationRelatedInfo(SyntaxNode declaration, bool typeOnly, string name) { }

    public bool ValidTypeOnlyUse(SyntaxNode node) => ReferenceSyntax.ValidTypeOnlyUse(node);

    public bool MissingPrefix(SyntaxNode node, string name) =>
        throw new InvalidOperationException("Probe requires missing-prefix diagnostics");

    public ValueTask<bool> InitializedInStaticBlocksAsync(PropertyDeclarationNode declaration, SyntaxNode usage,
        SyntaxNode initializer, CancellationToken cancellation)
        => declaration.Parent!.DescendantsAndSelf().OfType<ClassStaticBlockDeclarationNode>().Any()
            ? throw new InvalidOperationException("Probe requires static property initialization flow") : ValueTask.FromResult(false);

    public bool IsContextSensitive(SyntaxNode declaration)
    {
        if (ContextualSignatures.ContainsKey(declaration))
            return true;
        if (declaration is IFunctionSignature { TypeParameters.Count: > 0 })
            return false;
        throw new InvalidOperationException("Probe requires contextual function analysis");
    }

    public ValueTask<Signature?> FirstCallSignatureAsync(Symbol symbol, CancellationToken cancellation)
        => ValueTask.FromResult<Signature?>(ContextualSignatures[symbol.Declarations[0]]);

    public async ValueTask<Symbol?> ResolveTypeNameAsync(SyntaxNode name, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        BeforeResolveType?.Invoke();
        return await EntityNames.ResolveAsync(name, SymbolFlags.Type, true, cancellation: cancellation).ConfigureAwait(false);
    }
}

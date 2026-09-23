using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using S = TypeScript.Compiler.Binding.SymbolFlags;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal interface ICheckerSymbolHost
{
    void Bind(CheckerSymbols symbols);

    Symbol ResolveSymbol(Symbol symbol);

    Symbol LateBoundSymbol(Symbol symbol);

    S GetSymbolFlags(Symbol symbol);

    void MergeConflict(Symbol target, Symbol source, bool namespaceConflict);

    void Error(SyntaxNode? node, DiagnosticMessage message, params string[] arguments);

    ValueTask InitializeGlobalTypesAsync(CheckerSymbols symbols, CancellationToken cancellation);

    ValueTask<Type> ImportAttributesTypeAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<bool> IdenticalTypesAsync(Type first, Type second, CancellationToken cancellation);

    ValueTask<Symbol?> ResolveAugmentationAsync(SyntaxNode moduleName, bool reportNotFound, CancellationToken cancellation);

    ValueTask<Symbol> ExternalModuleSymbolAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<IReadOnlyDictionary<string, Symbol>> ResolvedExportsAsync(Symbol symbol, CancellationToken cancellation);

    bool InvalidInitializer(SyntaxNode? location, string name, SyntaxNode declaration, Symbol? result);

    void FailedResolution(SyntaxNode? location, string name, S meaning, DiagnosticMessage message);

    void SuccessfulResolution(SyntaxNode? location, Symbol symbol, S meaning, SyntaxNode? last, SyntaxNode? declaration, bool deferred);
}

internal sealed record PatternModule(string Pattern, Symbol Symbol);

// A checker owns these tables. Program syntax and bindings remain shared and
// immutable; a canceled initialization is discarded before publication.
internal sealed class CheckerSymbols
{
    private readonly CompilerProgram program;
    private readonly ICheckerSymbolHost host;
    private readonly CheckerLinks links;
    private readonly Dictionary<SyntaxNode, BoundSourceFile> bindings = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Symbol, S> references = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, Symbol> globals;
    private readonly List<PatternModule> patterns = [];
    private readonly Dictionary<string, Symbol> patternAugmentations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Symbol> patternTargets = new(StringComparer.Ordinal);
    internal Symbol UndefinedSymbol { get; } = new(S.Property | S.Transient, "undefined");
    internal Symbol ArgumentsSymbol { get; } = new(S.Property | S.Transient, "arguments");
    internal Symbol RequireSymbol { get; } = new(S.Property | S.Transient, "require");
    internal Symbol UnknownSymbol { get; } = new(S.Property | S.Transient, "unknown");
    internal Symbol GlobalThisSymbol { get; } = new(S.Module | S.Transient, "globalThis") { CheckFlags = CheckFlags.Readonly };
    internal SymbolMerger Merger { get; }
    internal IReadOnlyDictionary<string, Symbol> Globals { get; }
    internal IReadOnlyList<PatternModule> PatternModules => patterns.AsReadOnly();
    internal IReadOnlyDictionary<string, Symbol> PatternAugmentations => patternAugmentations.AsReadOnly();
    internal IReadOnlyDictionary<string, Symbol> PatternTargets => patternTargets.AsReadOnly();
    internal CompilerProgram Program => program;

    private CheckerSymbols(CompilerProgram program, CheckerLinks links, ICheckerSymbolHost host)
    {
        this.program = program;
        this.links = links;
        this.host = host;
        globals = GlobalThisSymbol.ExportTable;
        globals.Add(GlobalThisSymbol.Name, GlobalThisSymbol);
        Globals = globals.AsReadOnly();
        Merger = new(UnknownSymbol, GlobalThisSymbol, host.ResolveSymbol, host.MergeConflict);
        foreach (var file in program.SourceFiles)
            bindings.Add(file.Syntax, file.Binding);
    }

    internal static async ValueTask<CheckerSymbols> CreateAsync(CompilerProgram program,
        CheckerLinks links, ICheckerSymbolHost host, CancellationToken cancellation = default)
    {
        var result = new CheckerSymbols(program, links, host);
        host.Bind(result);
        await result.InitializeAsync(cancellation).ConfigureAwait(false);
        return result;
    }

    internal BoundSourceFile? Binding(SyntaxNode node)
    {
        if (bindings.TryGetValue(node, out var binding))
            return binding;
        var path = new List<SyntaxNode>();
        SyntaxNode? current = node;
        while (current is not null && !bindings.TryGetValue(current, out binding))
        {
            path.Add(current);
            current = current.Parent;
        }
        if (current is null)
            return null;
        foreach (var child in path)
            bindings.Add(child, binding!);
        return binding;
    }

    internal Symbol? Declaration(SyntaxNode node)
        => Binding(node)?.Get(node)?.Symbol is { } symbol ? Merger.GetMergedSymbol(host.LateBoundSymbol(symbol)) : null;

    internal Symbol? Parent(Symbol symbol)
        => symbol.Parent is { } parent ? Merger.GetMergedSymbol(host.LateBoundSymbol(parent)) : null;

    internal Symbol? ExportedValue(Symbol? symbol)
        => Merger.GetMergedSymbol(symbol is { ExportSymbol: { } exported } && (symbol.Flags & S.ExportValue) != 0 ? exported : symbol);

    internal Symbol? Lookup(IReadOnlyDictionary<string, Symbol>? table, string name, S meaning)
    {
        if ((meaning & S.All) == 0)
            return null;
        string key = name.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal) ? Symbol.InternalPrefix + name : name;
        var symbol = Merger.GetMergedSymbol(table?.GetValueOrDefault(key));
        return symbol is not null && ((symbol.Flags & meaning) != 0
            || (symbol.Flags & S.Alias) != 0 && (host.GetSymbolFlags(symbol) & meaning) != 0) ? symbol : null;
    }

    internal S ReferenceKinds(Symbol symbol) => references.GetValueOrDefault(symbol);

    internal NameResolver NameResolver(CancellationToken cancellation = default) => new(program.Configuration.Options, Binding)
    {
        Globals = Globals,
        ArgumentsSymbol = ArgumentsSymbol,
        RequireSymbol = RequireSymbol,
        GetSymbolOfDeclaration = Declaration,
        Lookup = Lookup,
        Error = (node, message, arguments) => host.Error(node, message, arguments),
        SymbolReferenced = (symbol, meaning) => references[symbol] = references.GetValueOrDefault(symbol) | meaning,
        GetRequiresScopeChangeCache = node => links.Nodes.Get(node).DeclarationRequiresScopeChange,
        SetRequiresScopeChangeCache = (node, value) => links.Nodes.Get(node).DeclarationRequiresScopeChange = value,
        OnPropertyWithInvalidInitializer = host.InvalidInitializer,
        OnFailedToResolveSymbol = host.FailedResolution,
        OnSuccessfullyResolvedSymbol = host.SuccessfulResolution,
        Cancellation = cancellation
    };

    private async ValueTask InitializeAsync(CancellationToken cancellation)
    {
        var ambient = new List<Symbol>();
        foreach (var file in program.SourceFiles)
        {
            cancellation.ThrowIfCancellationRequested();
            var bound = file.Binding;
            if (!bound.IsModule)
            {
                if (bound.Locals.TryGetValue("globalThis", out var conflicting))
                    foreach (var declaration in conflicting.Declarations)
                        host.Error(declaration, Messages.Declaration_name_conflicts_with_built_in_global_identifier_0, "globalThis");
                foreach (var symbol in bound.Locals.Values)
                    if ((symbol.Flags & S.Module) != 0 && symbol.Name.StartsWith('"'))
                        ambient.Add(symbol);
                    else
                        await MergeGlobalAsync(symbol, cancellation).ConfigureAwait(false);
            }
            foreach (var node in file.Syntax.DescendantsAndSelf().OfType<ModuleDeclarationNode>())
            {
                if (node.Name is not StringLiteralNode name || file.Syntax.ModuleAugmentations.Contains(name))
                    continue;
                int star = name.Text.IndexOf('*');
                if (star >= 0 && name.Text.IndexOf('*', star + 1) < 0 && bound.Get(node)?.Symbol is { } symbol)
                    patterns.Add(new(name.Text, symbol));
            }
            if (bound.Symbol is not null)
                foreach (var (name, symbol) in bound.GlobalExports)
                    globals.TryAdd(name, symbol);
        }
        foreach (var file in program.SourceFiles)
            foreach (var name in file.Syntax.ModuleAugmentations)
                if (GlobalAugmentation(name.Parent))
                    await MergeAugmentationAsync(name, cancellation).ConfigureAwait(false);
        if (globals.TryGetValue("undefined", out var undefined))
        {
            foreach (var declaration in undefined.Declarations)
                if (!TypeDeclaration(declaration))
                    host.Error(declaration, Messages.Declaration_name_conflicts_with_built_in_global_identifier_0, "undefined");
        }
        else
            globals.Add("undefined", UndefinedSymbol);
        await host.InitializeGlobalTypesAsync(this, cancellation).ConfigureAwait(false);
        foreach (var symbol in ambient)
            await MergeGlobalAsync(symbol, cancellation).ConfigureAwait(false);
        await MergePatternsAsync(cancellation).ConfigureAwait(false);
        foreach (var file in program.SourceFiles)
            foreach (var name in file.Syntax.ModuleAugmentations)
                if (!GlobalAugmentation(name.Parent))
                    await MergeAugmentationAsync(name, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
    }

    private async ValueTask MergeGlobalAsync(Symbol symbol, CancellationToken cancellation)
        => globals[symbol.Name] = globals.TryGetValue(symbol.Name, out var existing)
            ? await Merger.MergeAsync(existing, symbol, cancellation: cancellation).ConfigureAwait(false)
            : Merger.GetMergedSymbol(symbol)!;

    private async ValueTask MergePatternsAsync(CancellationToken cancellation)
    {
        var grouped = new List<PatternModule>();
        foreach (var pattern in patterns)
        {
            cancellation.ThrowIfCancellationRequested();
            var attributes = await host.ImportAttributesTypeAsync(pattern.Symbol, cancellation).ConfigureAwait(false);
            int match = -1;
            for (int i = 0; i < grouped.Count; i++)
                if (grouped[i].Pattern == pattern.Pattern
                    && await host.IdenticalTypesAsync(attributes,
                        await host.ImportAttributesTypeAsync(grouped[i].Symbol, cancellation).ConfigureAwait(false),
                        cancellation).ConfigureAwait(false))
                {
                    match = i;
                    break;
                }
            if (match < 0)
                grouped.Add(pattern);
            else
                grouped[match] = grouped[match] with
                {
                    Symbol = await Merger.MergeAsync(
                        grouped[match].Symbol,
                        pattern.Symbol,
                        cancellation: cancellation).ConfigureAwait(false)
                };
        }
        foreach (var pattern in patterns)
            if (globals.ContainsKey(pattern.Symbol.Name))
                globals[pattern.Symbol.Name] = Merger.GetMergedSymbol(pattern.Symbol)!;
        patterns.Clear();
        patterns.AddRange(grouped);
    }

    private async ValueTask MergeAugmentationAsync(SyntaxNode name, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var node = name.Parent as ModuleDeclarationNode ?? throw new InvalidOperationException("Augmentation has no module declaration");
        var augmentation = Binding(node)?.Get(node)?.Symbol ?? throw new InvalidOperationException("Augmentation has no bound symbol");
        if (augmentation.Declarations[0] != node)
            return;
        if (GlobalAugmentation(node))
        {
            await Merger.MergeTableAsync(globals, augmentation.Exports, cancellation: cancellation).ConfigureAwait(false);
            return;
        }
        var parent = node.Parent!;
        var parentFlags = Binding(parent)?.Get(parent)?.Flags ?? parent.Flags;
        var main = await host.ResolveAugmentationAsync(name, (parentFlags & NodeFlags.Ambient) == 0, cancellation).ConfigureAwait(false);
        if (main is null)
            return;
        main = await host.ExternalModuleSymbolAsync(main, cancellation).ConfigureAwait(false);
        if ((main.Flags & S.Namespace) == 0)
        {
            host.Error(name, Messages.Cannot_augment_module_0_because_it_resolves_to_a_non_module_entity, ((StringLiteralNode)name).Text);
            return;
        }
        if (patterns.Any(pattern => Merger.GetMergedSymbol(pattern.Symbol) == main))
        {
            string text = ((StringLiteralNode)name).Text;
            patternAugmentations[text] = await Merger.MergeAsync(augmentation, main, true, cancellation).ConfigureAwait(false);
            patternTargets[text] = main;
            return;
        }
        if (main.Exports.ContainsKey(Symbol.InternalPrefix + "export") && augmentation.Exports.Count != 0)
        {
            var resolved = await host.ResolvedExportsAsync(main, cancellation).ConfigureAwait(false);
            foreach (var (key, value) in augmentation.Exports)
                if (!main.Exports.ContainsKey(key) && resolved.TryGetValue(key, out var target))
                    await Merger.MergeAsync(target, value, cancellation: cancellation).ConfigureAwait(false);
        }
        await Merger.MergeAsync(main, augmentation, cancellation: cancellation).ConfigureAwait(false);
    }

    private static bool GlobalAugmentation(SyntaxNode? node) => node is ModuleDeclarationNode { Keyword: K.GlobalKeyword };

    private static bool TypeDeclaration(SyntaxNode node) => node.Kind switch
    {
        K.TypeParameter or K.ClassDeclaration or K.InterfaceDeclaration or K.TypeAliasDeclaration or K.JSTypeAliasDeclaration
            or K.EnumDeclaration => true,
        K.ImportClause => SemanticSyntax.TypeOnly(node),
        K.ImportSpecifier or K.ExportSpecifier => node.Parent?.Parent is { } parent && SemanticSyntax.TypeOnly(parent),
        _ => false
    };
}

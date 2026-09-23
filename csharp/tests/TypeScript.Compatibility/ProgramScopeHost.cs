using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

// Real program/binding inputs; semantic services outside this symbol/header probe
// are rejected explicitly. No expression checker or alias fallback is supplied.
internal sealed class ProgramScopeHost(TypeContext context, CheckerLinks links) : ICheckerSymbolHost, ITypeParameterScopeHost
{
    internal CheckerSymbols Symbols { get; private set; } = null!;
    internal TypeParameterScopes Scopes { get; private set; } = null!;
    internal GlobalTypes Globals { get; private set; } = null!;
    internal List<int> Diagnostics { get; } = [];
    internal Action? BeforeGlobalTypes { get; set; }
    internal Action? BeforeResolveType { get; set; }
    internal Dictionary<SyntaxNode, Signature> ContextualSignatures { get; } = [];

    public void Bind(CheckerSymbols symbols)
    {
        Symbols = symbols;
        Scopes = new(context, links, symbols, this);
        Globals = new(context, links, symbols, Scopes, (node, message, arguments) => Error(node, message, arguments));
    }

    public Symbol ResolveSymbol(Symbol symbol)
        => (symbol.Flags & SymbolFlags.Alias) == 0 ? symbol : throw new InvalidOperationException("Probe requires alias target resolution");

    public Symbol LateBoundSymbol(Symbol symbol)
        =>
            symbol.Name != Symbol.InternalPrefix + "computed"
                ? symbol
                : throw new InvalidOperationException("Probe requires computed member binding");

    public SymbolFlags GetSymbolFlags(Symbol symbol) => ResolveSymbol(symbol).Flags;

    public void MergeConflict(Symbol target, Symbol source, bool namespaceConflict)
        => throw new InvalidOperationException("Probe requires checker merge diagnostic attribution");

    public void Error(SyntaxNode? node, DiagnosticMessage message, params string[] arguments) => Diagnostics.Add(message.Code);

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
    {
        if (symbol.Exports.ContainsKey("export="))
            throw new InvalidOperationException("Probe requires export-assignment resolution");
        return ValueTask.FromResult(Symbols.Merger.GetMergedSymbol(symbol)!);
    }

    public ValueTask<IReadOnlyDictionary<string, Symbol>> ResolvedExportsAsync(Symbol symbol, CancellationToken cancellation)
        => throw new InvalidOperationException("Probe requires export-star resolution");

    public bool InvalidInitializer(SyntaxNode? location, string name, SyntaxNode declaration, Symbol? result)
        => throw new InvalidOperationException("Probe requires initializer checking");

    public void FailedResolution(SyntaxNode? location, string name, SymbolFlags meaning, DiagnosticMessage message)
        => Error(location, message, name);

    public void SuccessfulResolution(
        SyntaxNode? location,
        Symbol symbol,
        SymbolFlags meaning,
        SyntaxNode? last,
        SyntaxNode? declaration,
        bool deferred)
    {
        if (location is not null && meaning == SymbolFlags.Value)
            throw new InvalidOperationException("Probe requires value-use checking");
    }

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
        if (name is IdentifierNode identifier)
            return ResolveIfPresent(
                Symbols.NameResolver(cancellation).Resolve(
                    identifier,
                    identifier.Text,
                    TypeScript.Compiler.Binding.SymbolFlags.Type | TypeScript.Compiler.Binding.SymbolFlags.Namespace));
        SyntaxNode left, right;
        if (name is QualifiedNameNode qualified)
            (left, right) = (qualified.Left!, qualified.Right!);
        else if (name is PropertyAccessExpressionNode access)
            (left, right) = (access.Expression!, access.Name!);
        else
            throw new InvalidOperationException("Probe requires a non-entity heritage expression");
        var parent = await ResolveTypeNameAsync(left, cancellation).ConfigureAwait(false);
        return parent is null
            ? null
            : ResolveIfPresent(
                Symbols.Lookup(
                    parent.Exports,
                    ((IdentifierNode)right).Text,
                    TypeScript.Compiler.Binding.SymbolFlags.Type | TypeScript.Compiler.Binding.SymbolFlags.Namespace));
    }

    private Symbol? ResolveIfPresent(Symbol? symbol) => symbol is null ? null : ResolveSymbol(symbol);
}

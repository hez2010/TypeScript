using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerAliasTests
{
    private sealed class Host : IAliasResolverHost
    {
        internal Dictionary<SyntaxNode, Func<CancellationToken, ValueTask<Symbol?>>> Targets { get; } = [];
        internal HashSet<Symbol> Deprecated { get; } = [];
        internal List<Symbol> Warnings { get; } = [];
        internal List<Symbol> Cycles { get; } = [];

        public ValueTask<Symbol?> TargetAsync(SyntaxNode declaration, CancellationToken cancellation) => Targets[declaration](cancellation);

        public void CircularAlias(Symbol symbol, SyntaxNode declaration) => Cycles.Add(symbol);

        public bool IsDeprecated(Symbol symbol) => Deprecated.Contains(symbol);

        public void DeprecatedAlias(SyntaxNode location, Symbol symbol) => Warnings.Add(symbol);
    }

    private sealed class ExportHost : IModuleExportHost
    {
        internal Dictionary<ExportDeclarationNode, Symbol> Modules { get; } = [];
        internal Action? BeforeResolve { get; set; }
        internal List<(string Specifier, string Name)> Conflicts { get; } = [];

        public ValueTask<Symbol?> ExportStarModuleAsync(ExportDeclarationNode declaration, CancellationToken cancellation)
        {
            BeforeResolve?.Invoke();
            cancellation.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Symbol?>(Modules[declaration]);
        }

        public void AmbiguousExport(ExportDeclarationNode declaration, string earlierSpecifierText, string name)
                    => Conflicts.Add((earlierSpecifierText, name));
    }

    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Alias assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/input.ts"] = Wtf8.Encode("namespace N { export interface I {} }") }), "/project",
            new("/project/tsconfig.json", options, ["/project/input.ts"], [], [], []));
        var context = new TypeContext();
        var links = new CheckerLinks();
        var programHost = new ProgramScopeHost(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, programHost);
        var host = new Host();
        var resolutions = new TypeResolutionStack(links);
        var aliases = new AliasResolver(symbols, links, resolutions, host);
        Symbol Alias(string name, Symbol? target, SymbolFlags extra = 0)
        {
            var symbol = new Symbol(SymbolFlags.Alias | extra, name);
            var declaration = new ImportEqualsDeclarationNode { Name = new IdentifierNode { Text = name } };
            symbol.DeclarationList.Add(declaration);
            host.Targets[declaration] = _ => ValueTask.FromResult(target);
            return symbol;
        }
        var value = new Symbol(SymbolFlags.BlockScopedVariable, "value");
        var first = Alias("First", value);
        var second = Alias("Second", first);
        var typeOnly = new ImportClauseNode { PhaseModifier = SyntaxKind.TypeKeyword, Name = new IdentifierNode { Text = "First" } };
        links.Aliases.Get(first).TypeOnlyDeclaration = typeOnly;
        Check(await aliases.ResolveAsync(second) == value);
        Check(await aliases.ImmediateAsync(second) == first);
        Check(await aliases.TypeOnlyAsync(second) == typeOnly && await aliases.TypeOnlyAsync(second, SymbolFlags.Value) == typeOnly);
        Check((await aliases.FlagsAsync(second) & SymbolFlags.Value) != 0);
        Check(await aliases.FlagsAsync(second, excludeTypeOnly: true) == SymbolFlags.Alias);
        Check(await aliases.FlagsAsync(second, excludeLocal: true) == value.Flags);
        var mixed = Alias("Mixed", value, SymbolFlags.TypeAlias);
        var outer = Alias("Outer", mixed);
        Check(await aliases.ResolveAsync(outer) == mixed);
        Check((await aliases.FlagsAsync(outer) & (SymbolFlags.TypeAlias | SymbolFlags.BlockScopedVariable))
            == (SymbolFlags.TypeAlias | SymbolFlags.BlockScopedVariable));
        Check(await aliases.SymbolAsync(mixed) == mixed && await aliases.SymbolAsync(first, true) == first);

        var a = Alias("A", null);
        var b = Alias("B", a);
        host.Targets[a.Declarations[0]] = _ => ValueTask.FromResult<Symbol?>(b);
        Check(await aliases.ResolveAsync(a) == symbols.UnknownSymbol);
        Check(host.Cycles.Count == 2 && resolutions.Count == 0);
        Check(await aliases.FlagsAsync(a) == SymbolFlags.All);
        Check(await aliases.ResolveAsync(b) == symbols.UnknownSymbol && host.Cycles.Count == 2);

        var failed = Alias("Failed", value);
        host.Targets[failed.Declarations[0]] = _ =>
        {
            links.Aliases.Get(failed).TypeOnlyDeclaration = typeOnly;
            throw new OperationCanceledException();
        };
        try
        {
            await aliases.ResolveAsync(failed);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(
            links.Aliases.Get(failed).AliasTarget is null
                && links.Aliases.Get(failed).TypeOnlyDeclaration is null
                && resolutions.Count == 0);
        host.Targets[failed.Declarations[0]] = _ => ValueTask.FromResult<Symbol?>(value);
        Check(await aliases.ResolveAsync(failed) == value);
        var probing = Alias("Probing", value);
        host.Targets[probing.Declarations[0]] = async cancellation =>
        {
            Check(await aliases.TryResolveAsync(probing, cancellation) is null);
            return value;
        };
        Check(await aliases.ResolveAsync(probing) == value && resolutions.Count == 0);

        var deprecated = Alias("Deprecated", value);
        var exposed = Alias("Exposed", deprecated);
        host.Deprecated.Add(deprecated);
        Check(await aliases.WithDeprecationAsync(exposed, new IdentifierNode()) == value && host.Warnings.SequenceEqual([deprecated]));
        var chain = value;
        for (int i = 0; i < 20_000; i++)
            chain = Alias("A" + i, chain);
        Check(await aliases.ResolveAsync(chain) == value && resolutions.Count == 0);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await aliases.ResolveAsync(chain, cancelled.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }

        var module = new Symbol(SymbolFlags.ValueModule, "module");
        module.ExportTable.Add("value", value);
        module.DeclarationList.Add(program.SourceFiles[0].Syntax);
        var memberType = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved, module);
        memberType.Members = module.Exports;
        memberType.CallSignatures = [context.NewSignature(0, null, [], null, [], context.AnyType, null, 0)];
        memberType.ConstructSignatures = [context.NewSignature(SignatureFlags.Construct, null, [], null, [], context.AnyType, null, 0)];
        memberType.IndexInfos = [context.NewIndexInfo(context.StringType, context.NumberType, true)];
        var cloner = new ModuleTypes(context, links, aliases, new([]));
        var import = new ImportDeclarationNode(SyntaxKind.ImportDeclaration);
        var clone = await cloner.CloneAsync(module, memberType, import);
        Check(clone != module && clone.Exports["value"] == value && clone.Declarations[0] == module.Declarations[0]);
        var cloneType = (ObjectType)links.Values.Get(clone).ResolvedType!;
        Check(cloneType.CallSignatures.Count == 0 && cloneType.ConstructSignatures.Count == 0 && memberType.CallSignatures.Count == 1);
        Check(cloneType.IndexInfos == memberType.IndexInfos && cloneType.Properties!.SequenceEqual([value]));
        Check(links.ExportTypes.Get(clone).Target == module && links.ExportTypes.Get(clone).OriginatingImport == import);
        clone.ExportTable.Add("extra", value);
        Check(!module.Exports.ContainsKey("extra"));
        var exportHost = new ExportHost();
        var exports = new ModuleExports(links, aliases, programHost.AliasTargets, exportHost);
        var left = new Symbol(SymbolFlags.ValueModule, "left");
        var right = new Symbol(SymbolFlags.ValueModule, "right");
        left.ExportTable.Add("shared", new(SymbolFlags.BlockScopedVariable, "shared"));
        right.ExportTable.Add("shared", new(SymbolFlags.BlockScopedVariable, "shared"));
        var exportFile = Parser.ParseSourceFile(
            new("/exports.ts"),
            new SourceText("export * from /*first*/ './left'; export * from './right';"));
        var declarations = exportFile.Statements!.Cast<ExportDeclarationNode>().ToArray();
        var parent = new Symbol(SymbolFlags.ValueModule, "parent");
        var stars = new Symbol(SymbolFlags.ExportStar, Symbol.InternalPrefix + "export");
        stars.DeclarationList.AddRange(declarations);
        parent.ExportTable.Add(stars.Name, stars);
        exportHost.Modules.Add(declarations[0], left);
        exportHost.Modules.Add(declarations[1], right);
        int visits = 0;
        exportHost.BeforeResolve = () =>
        {
            if (++visits == 2)
                throw new OperationCanceledException();
        };
        try
        {
            await exports.ResolveAsync(parent);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(links.Modules.Get(parent).ResolvedExports is null && links.Modules.Get(parent).TypeOnlyExportStars is null);
        exportHost.BeforeResolve = null;
        var mergedExports = await exports.ResolveAsync(parent);
        Check(mergedExports["shared"] == left.Exports["shared"] && !parent.Exports.ContainsKey("shared"));
        Check(exportHost.Conflicts.SequenceEqual([("'./left'", "shared")]));
        Check(await exports.ResolveAsync(parent) == mergedExports && exportHost.Conflicts.Count == 1);
        Console.WriteLine($"{checks} alias/cycle/cancellation/module-export assertions; alias chain depth 20000");
    }
}

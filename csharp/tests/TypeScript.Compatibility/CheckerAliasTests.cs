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
        internal List<(Utf8String Specifier, Utf8String Name)> Conflicts { get; } = [];

        public ValueTask<Symbol?> ExportStarModuleAsync(ExportDeclarationNode declaration, CancellationToken cancellation)
        {
            BeforeResolve?.Invoke();
            cancellation.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Symbol?>(Modules[declaration]);
        }

        public void AmbiguousExport(ExportDeclarationNode declaration, Utf8String earlierSpecifierText, Utf8String name)
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
        options.SetRaw("noLib"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        { ["/project/input.ts"u8] = Wtf8.Encode("namespace N { export interface I {} }") }), "/project"u8,
            new("/project/tsconfig.json"u8, options, ["/project/input.ts"u8], [], [], []));
        var context = new TypeContext();
        var links = new CheckerLinks();
        var programHost = new CheckerEnvironment(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, programHost);
        var host = new Host();
        var resolutions = new TypeResolutionStack(links);
        var aliases = new AliasResolver(symbols, links, resolutions, host);
        Symbol Alias(Utf8String name, Symbol? target, SymbolFlags extra = 0)
        {
            var symbol = new Symbol(SymbolFlags.Alias | extra, name);
            var declaration = new ImportEqualsDeclarationNode { Name = new IdentifierNode { Text = name } };
            symbol.DeclarationList = symbol.DeclarationList.Add(declaration);
            host.Targets[declaration] = _ => ValueTask.FromResult(target);
            return symbol;
        }
        var value = new Symbol(SymbolFlags.BlockScopedVariable, "value"u8);
        var first = Alias("First"u8, value);
        var second = Alias("Second"u8, first);
        var typeOnly = new ImportClauseNode { PhaseModifier = SyntaxKind.TypeKeyword, Name = new IdentifierNode { Text = "First"u8 } };
        links.Aliases.Get(first).TypeOnlyDeclaration = typeOnly;
        Check(await aliases.ResolveAsync(second) == value);
        Check(await aliases.ImmediateAsync(second) == first);
        Check(await aliases.TypeOnlyAsync(second) == typeOnly && await aliases.TypeOnlyAsync(second, SymbolFlags.Value) == typeOnly);
        Check((await aliases.FlagsAsync(second) & SymbolFlags.Value) != 0);
        Check(await aliases.FlagsAsync(second, excludeTypeOnly: true) == SymbolFlags.Alias);
        Check(await aliases.FlagsAsync(second, excludeLocal: true) == value.Flags);
        var mixed = Alias("Mixed"u8, value, SymbolFlags.TypeAlias);
        var outer = Alias("Outer"u8, mixed);
        Check(await aliases.ResolveAsync(outer) == mixed);
        Check((await aliases.FlagsAsync(outer) & (SymbolFlags.TypeAlias | SymbolFlags.BlockScopedVariable))
            == (SymbolFlags.TypeAlias | SymbolFlags.BlockScopedVariable));
        Check(await aliases.SymbolAsync(mixed) == mixed && await aliases.SymbolAsync(first, true) == first);

        var a = Alias("A"u8, null);
        var b = Alias("B"u8, a);
        host.Targets[a.Declarations[0]] = _ => ValueTask.FromResult<Symbol?>(b);
        Check(await aliases.ResolveAsync(a) == symbols.UnknownSymbol);
        Check(host.Cycles.Count == 2 && resolutions.Count == 0);
        Check(await aliases.FlagsAsync(a) == SymbolFlags.All);
        Check(await aliases.ResolveAsync(b) == symbols.UnknownSymbol && host.Cycles.Count == 2);

        var failed = Alias("Failed"u8, value);
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
        var probing = Alias("Probing"u8, value);
        host.Targets[probing.Declarations[0]] = async cancellation =>
        {
            Check(await aliases.TryResolveAsync(probing, cancellation) is null);
            return value;
        };
        Check(await aliases.ResolveAsync(probing) == value && resolutions.Count == 0);

        var deprecated = Alias("Deprecated"u8, value);
        var exposed = Alias("Exposed"u8, deprecated);
        host.Deprecated.Add(deprecated);
        Check(await aliases.WithDeprecationAsync(exposed, new IdentifierNode()) == value && host.Warnings.SequenceEqual([deprecated]));
        var chain = value;
        for (int i = 0; i < 20_000; i++)
            chain = Alias(Utf8String.Copy("A"u8) + i, chain);
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

        var module = new Symbol(SymbolFlags.ValueModule, "module"u8);
        module.ExportTable.Add("value"u8, value);
        module.DeclarationList = module.DeclarationList.Add(program.SourceFiles[0].Syntax);
        var memberType = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved, module);
        memberType.Members = module.Exports;
        memberType.CallSignatures = [context.NewSignature(0, null, [], null, [], context.AnyType, null, 0)];
        memberType.ConstructSignatures = [context.NewSignature(SignatureFlags.Construct, null, [], null, [], context.AnyType, null, 0)];
        memberType.IndexInfos = [context.NewIndexInfo(context.StringType, context.NumberType, true)];
        var cloner = new ModuleTypes(context, links, aliases, new([]));
        var import = new ImportDeclarationNode(SyntaxKind.ImportDeclaration);
        var clone = await cloner.CloneAsync(module, memberType, import);
        Check(clone != module && clone.Exports["value"u8] == value && clone.Declarations[0] == module.Declarations[0]);
        var cloneType = (ObjectType)links.Values.Get(clone).ResolvedType!;
        Check(cloneType.CallSignatures.Count == 0 && cloneType.ConstructSignatures.Count == 0 && memberType.CallSignatures.Count == 1);
        Check(cloneType.IndexInfos == memberType.IndexInfos && cloneType.Properties!.SequenceEqual([value]));
        Check(links.ExportTypes.Get(clone).Target == module && links.ExportTypes.Get(clone).OriginatingImport == import);
        clone.ExportTable.Add("extra"u8, value);
        Check(!module.Exports.ContainsKey("extra"u8));
        var exportHost = new ExportHost();
        var exports = new ModuleExports(links, aliases, programHost.AliasTargets, exportHost);
        var left = new Symbol(SymbolFlags.ValueModule, "left"u8);
        var right = new Symbol(SymbolFlags.ValueModule, "right"u8);
        left.ExportTable.Add("shared"u8, new(SymbolFlags.BlockScopedVariable, "shared"u8));
        right.ExportTable.Add("shared"u8, new(SymbolFlags.BlockScopedVariable, "shared"u8));
        var exportFile = Parser.ParseSourceFile(
            new("/exports.ts"u8),
            new SourceText("export * from /*first*/ './left'; export * from './right';"u8));
        var declarations = exportFile.Statements!.Cast<ExportDeclarationNode>().ToArray();
        var parent = new Symbol(SymbolFlags.ValueModule, "parent"u8);
        var stars = new Symbol(SymbolFlags.ExportStar, Symbol.InternalPrefix + "export"u8);
        stars.DeclarationList = stars.DeclarationList.AddRange(declarations);
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
        Check(mergedExports["shared"u8] == left.Exports["shared"u8] && !parent.Exports.ContainsKey("shared"u8));
        Check(exportHost.Conflicts.SequenceEqual([(Utf8String.Copy("'./left'"u8), Utf8String.Copy("shared"u8))]));
        Check(await exports.ResolveAsync(parent) == mergedExports && exportHost.Conflicts.Count == 1);
        Console.WriteLine($"{checks} alias/cycle/cancellation/module-export assertions; alias chain depth 20000");
    }
}

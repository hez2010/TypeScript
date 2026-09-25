using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Numerics;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerProgramTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Checker program assertion {checks + 1}");
            checks++;
        }
        static async ValueTask<CompilerProgram> Build(
            Dictionary<string, string> sources,
            CompilerProgram? previous = null,
            CompilerOptions? configuredOptions = null)
        {
            var files = sources.ToDictionary(p => p.Key, p => Wtf8.Encode(p.Value));
            var options = configuredOptions ?? new CompilerOptions();
            options.SetRaw("noLib", "true");
            return await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project",
                new("/project/tsconfig.json", options, sources.Keys.ToArray(), [], [], []), previous, concurrency: 4);
        }
        var sources = new Dictionary<string, string>
        {
            ["/project/a.ts"] = "interface I<T> { a: T } namespace N { export interface A {} }",
            ["/project/b.ts"] = "interface I<T> { b: T; self: this } namespace N { export interface B {} }"
        };
        var program = await Build(sources);
        var original = program.SourceFiles[0].Binding.Locals["I"];
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var host = new CheckerEnvironment(context, links);
        var environment = await CheckerSymbols.CreateAsync(program, links, host);
        var merged = environment.Globals["I"];
        Check(merged != original && merged.Declarations.Count == 2 && original.Declarations.Count == 1);
        Check(merged.Members.ContainsKey("a") && merged.Members.ContainsKey("b") && !original.Members.ContainsKey("b"));
        Check(ReferenceEquals(environment.Globals["globalThis"].Exports["I"], merged));
        var declaration = program.SourceFiles[1].Syntax.DescendantsAndSelf().OfType<InterfaceDeclarationNode>().First();
        Check(environment.Declaration(declaration) == merged);
        var type = await host.Scopes.ClassOrInterfaceAsync(merged);
        Check(type.ThisType is { IsThisType: true } && type.ThisType.Constraint == type && type.Target == type);
        Check(type.AllTypeParameters.Count == 2 && type.ResolvedTypeArguments!.Count == 1);
        Check(await host.Scopes.ClassOrInterfaceAsync(merged) == type);
        var updated = await Build(sources, program);
        Check(updated.ReusedSourceFiles == 2 && ReferenceEquals(updated.SourceFiles[0].Binding, program.SourceFiles[0].Binding));
        var otherContext = new TypeContext(true, true);
        var otherLinks = new CheckerLinks();
        var otherHost = new CheckerEnvironment(otherContext, otherLinks);
        var otherEnvironment = await CheckerSymbols.CreateAsync(updated, otherLinks, otherHost);
        Check(otherEnvironment.Globals["I"] != merged && original.Declarations.Count == 1);
        Check((await otherHost.Scopes.ClassOrInterfaceAsync(otherEnvironment.Globals["I"])).Context == otherContext);
        Check(links.Values.Get(environment.UndefinedSymbol).ResolvedType == context.UndefinedWideningType);
        Check(host.Globals.AnyArrayType == context.EmptyObjectType && host.Globals.AutoArrayType != context.EmptyObjectType);

        var cancelledLinks = new CheckerLinks();
        var cancelledHost = new CheckerEnvironment(new(true, true), cancelledLinks);
        using var cancellation = new CancellationTokenSource();
        cancelledHost.BeforeGlobalTypes = cancellation.Cancel;
        try
        {
            await CheckerSymbols.CreateAsync(program, cancelledLinks, cancelledHost, cancellation.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(original.Declarations.Count == 1 && !original.Members.ContainsKey("b"));
        var recoveredLinks = new CheckerLinks();
        var recoveredHost = new CheckerEnvironment(new(true, true), recoveredLinks);
        var recovered = await CheckerSymbols.CreateAsync(program, recoveredLinks, recoveredHost);
        Check(recovered.Globals["I"].Declarations.Count == 2);

        var retryProgram = await Build(
            new() { ["/project/rollback.ts"] = "interface Finished {} interface Stop {} interface Root extends Finished, Stop {}" });
        var retryContext = new TypeContext();
        var retryLinks = new CheckerLinks();
        var retryHost = new CheckerEnvironment(retryContext, retryLinks);
        var retryEnvironment = await CheckerSymbols.CreateAsync(retryProgram, retryLinks, retryHost);
        int resolutions = 0;
        retryHost.BeforeResolveType = () =>
        {
            if (++resolutions == 2)
                throw new OperationCanceledException();
        };
        try
        {
            await retryHost.Scopes.ClassOrInterfaceAsync(retryEnvironment.Globals["Root"]);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(retryLinks.DeclaredTypes.Get(retryEnvironment.Globals["Root"]).DeclaredType is null);
        Check(retryLinks.DeclaredTypes.Get(retryEnvironment.Globals["Finished"]).DeclaredType is null);
        retryHost.BeforeResolveType = null;
        Check((await retryHost.Scopes.ClassOrInterfaceAsync(retryEnvironment.Globals["Root"])).ThisType is null);

        var contextualProgram = await Build(new() { ["/project/contextual.ts"] = "function outer<T>() { const f = value => value; }" });
        var contextualContext = new TypeContext();
        var contextualLinks = new CheckerLinks();
        var contextualHost = new CheckerEnvironment(contextualContext, contextualLinks);
        var contextualEnvironment = await CheckerSymbols.CreateAsync(contextualProgram, contextualLinks, contextualHost);
        var arrow = contextualProgram.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<ArrowFunctionNode>().Single();
        var parameter = contextualContext.NewTypeParameter(new(SymbolFlags.TypeParameter, "Contextual"));
        var signature = contextualContext.NewSignature(0, arrow, [parameter], null, [], contextualContext.UnknownType, null, 0);
        contextualHost.ContextualSignatures[arrow] = signature;
        var scope = await contextualHost.Scopes.OuterAsync(arrow.Body!);
        Check(scope.Count == 2 && scope[1] == parameter && scope[0].Symbol?.Name == "T");

        const int depth = 20_000;
        var source = new StringBuilder();
        for (int i = 0; i < depth - 1; i++)
            source.Append("interface I").Append(i).Append(" extends I").Append(i + 1).Append(" {}\n");
        source.Append("interface I").Append(depth - 1).Append(" { self: this }");
        var deepProgram = await Build(new() { ["/project/deep.ts"] = source.ToString() });
        var deepContext = new TypeContext();
        var deepLinks = new CheckerLinks();
        var deepHost = new CheckerEnvironment(deepContext, deepLinks);
        var deepEnvironment = await CheckerSymbols.CreateAsync(deepProgram, deepLinks, deepHost);
        Check((await deepHost.Scopes.ClassOrInterfaceAsync(deepEnvironment.Globals["I0"])).ThisType is not null);
        Check(deepLinks.DeclaredTypes.Count == depth);
        SyntaxNode nested = deepProgram.SourceFiles[0].Syntax;
        for (int i = 0; i < depth; i++)
            nested = new BlockNode { Parent = nested };
        Check(deepEnvironment.Binding(nested) == deepProgram.SourceFiles[0].Binding);
        Check((await deepHost.Scopes.OuterAsync(nested)).Count == 0);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            await deepHost.Scopes.OuterAsync(nested, cancellation: stop.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        var queryProgram = await Build(new() { ["/project/query.ts"] = "const value = 1;" });
        var queryNode = queryProgram.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<NumericLiteralNode>().Single();
        var firstChecker = await queryProgram.CreateCheckerAsync();
        var secondChecker = await queryProgram.CreateCheckerAsync();
        var firstType = await firstChecker.GetExpressionTypeAsync(queryNode);
        var secondType = await secondChecker.GetExpressionTypeAsync(queryNode);
        Check(firstType is LiteralType { Value: 1d } && secondType is LiteralType { Value: 1d });
        Check(firstType.Context != secondType.Context && firstType != secondType);
        Check(await firstChecker.GetExpressionTypeAsync(queryNode) == firstType);
        using var queryCancellation = new CancellationTokenSource();
        queryCancellation.Cancel();
        try
        {
            await queryProgram.CreateCheckerAsync(queryCancellation.Token);
            throw new InvalidOperationException("Cancelled checker initialization completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        try
        {
            await firstChecker.GetExpressionTypeAsync(new NumericLiteralNode { Text = "1" });
            throw new InvalidOperationException("Foreign syntax accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        using var enteredQuery = new ManualResetEventSlim();
        using var releaseQuery = new ManualResetEventSlim();
        firstChecker.BeforeExpressionFinish = () =>
        {
            enteredQuery.Set();
            if (!releaseQuery.Wait(TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Checker query was not released");
        };
        var activeQuery = Task.Run(async () => await firstChecker.GetExpressionTypeAsync(queryNode));
        try
        {
            Check(enteredQuery.Wait(TimeSpan.FromSeconds(30)));
            using var queuedCancellation = new CancellationTokenSource();
            var queuedQuery = firstChecker.GetExpressionTypeAsync(queryNode, queuedCancellation.Token);
            Check(!queuedQuery.IsCompleted);
            queuedCancellation.Cancel();
            try
            {
                await queuedQuery;
                throw new InvalidOperationException("Queued query cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        finally
        {
            releaseQuery.Set();
        }
        Check(await activeQuery == firstType);
        firstChecker.BeforeExpressionFinish = null;
        Check(await firstChecker.GetExpressionTypeAsync(queryNode) == firstType);
        var libraryOptions = new CompilerOptions();
        libraryOptions.SetRaw("strict", "true");
        libraryOptions.SetRaw("lib", "[\"es5\"]");
        var libraryProgram = await CompilerProgram.CreateAsync(new LibraryFileSystem(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/library.ts"] = Wtf8.Encode("const values = [1,2,3].map(value => value + 1);")
        })), "/project", new("/project/tsconfig.json", libraryOptions, ["/project/library.ts"], [], [], []));
        Check(libraryProgram.SourceFiles.Any(f => f.Library));
        var libraryChecker = await libraryProgram.CreateCheckerAsync();
        var mapped = libraryProgram.GetFile("/project/library.ts")!.Syntax.DescendantsAndSelf().OfType<VariableDeclarationNode>().Single().Initializer!;
        var mappedType = await libraryChecker.GetExpressionTypeAsync(mapped);
        Check(mappedType is TypeReference reference && reference.Target == libraryChecker.ArrayTarget(false)
            && (await libraryChecker.TypeArgumentsAsync(reference, default)).Single() == libraryChecker.Context.NumberType);
        Check(libraryChecker.Diagnostics.Count == 0 && libraryChecker.Environment.Diagnostics.Count == 0);
        var semanticProgram = await Build(new()
        {
            ["/project/check.ts"] = "const f=()=>{void absent;return 1;}; let value:number='bad';"
        });
        var semanticChecker = await semanticProgram.CreateCheckerAsync();
        await semanticChecker.CheckProgramAsync();
        Check(
            semanticChecker.CheckedFileCount == 1
                && semanticChecker.Diagnostics.Contains(2322)
                && semanticChecker.Environment.Diagnostics.Contains(2304));
        Check(semanticChecker.CurrentSourceNode is null && semanticChecker.Instantiation.Engine.Depth == 0);
        int diagnosticCount = semanticChecker.Diagnostics.Count + semanticChecker.Environment.Diagnostics.Count;
        semanticChecker.BeforeSourceElement = _ => throw new InvalidOperationException("Completed source was checked again");
        await semanticChecker.CheckProgramAsync();
        Check(semanticChecker.Diagnostics.Count + semanticChecker.Environment.Diagnostics.Count == diagnosticCount);
        var cancelledChecker = await semanticProgram.CreateCheckerAsync();
        using var sourceCancellation = new CancellationTokenSource();
        cancelledChecker.BeforeSourceElement = _ => sourceCancellation.Cancel();
        try
        {
            await cancelledChecker.CheckProgramAsync(sourceCancellation.Token);
            throw new InvalidOperationException("Source cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(cancelledChecker.CheckedFileCount == 0 && cancelledChecker.CurrentSourceNode is null);
        bool invalidated = false;
        try
        {
            await cancelledChecker.GetExpressionTypeAsync(
                semanticProgram.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<NumericLiteralNode>().Single());
        }
        catch (InvalidOperationException)
        {
            invalidated = true;
        }
        Check(invalidated);
        var recoveredChecker = await semanticProgram.CreateCheckerAsync();
        await recoveredChecker.CheckProgramAsync();
        Check(recoveredChecker.CheckedFileCount == 1 && recoveredChecker.Diagnostics.SequenceEqual(semanticChecker.Diagnostics));
        var deepStatements = await Build(new() { ["/project/statements.ts"] = new string('{', depth) + "1;" + new string('}', depth) });
        var statementChecker = await deepStatements.CreateCheckerAsync();
        await statementChecker.CheckProgramAsync();
        Check(
            statementChecker.CheckedFileCount == 1
                && statementChecker.Diagnostics.Count == 0
                && statementChecker.CurrentSourceNode is null);
        var moduleProgram = await Build(new()
        {
            ["/project/dep.ts"] = "export const bad=absent; export const value=1;",
            ["/project/main.ts"] = "import {bad,value} from './dep';bad;const text:string=value;"
        });
        var moduleChecker = await moduleProgram.CreateCheckerAsync();
        var mainFile = moduleProgram.GetFile("/project/main.ts")!.Syntax;
        var depFile = moduleProgram.GetFile("/project/dep.ts")!.Syntax;
        await moduleChecker.CheckSourceFileAsync(mainFile);
        Check(moduleChecker.DiagnosticCodesForFile(mainFile).SequenceEqual([2322]));
        Check(moduleChecker.DiagnosticCodesForFile(depFile).SequenceEqual([2304]));
        await moduleChecker.CheckSourceFileAsync(depFile);
        Check(moduleChecker.DiagnosticCodesForFile(depFile).SequenceEqual([2304]));
        Check(moduleChecker.CheckedFileCount == 2);
        var moduleDiagnostics = moduleChecker.DiagnosticCodesForFile(mainFile).ToArray();
        await moduleChecker.CheckProgramAsync();
        Check(moduleChecker.DiagnosticCodesForFile(mainFile).SequenceEqual(moduleDiagnostics));
        var independentModuleChecker = await moduleProgram.CreateCheckerAsync();
        await independentModuleChecker.CheckProgramAsync();
        Check(independentModuleChecker.DiagnosticCodesForFile(mainFile).SequenceEqual(moduleDiagnostics)
            && independentModuleChecker.DiagnosticCodesForFile(depFile).SequenceEqual([2304]));
        var finalOptions = new CompilerOptions();
        finalOptions.SetRaw("strict", "true");
        finalOptions.SetRaw("noUnusedLocals", "true");
        finalOptions.SetRaw("noUnusedParameters", "true");
        var finalProgram = await Build(new()
        {
            ["/project/dep.ts"] = "export const bad={}.absent;",
            ["/project/main.ts"] = "import {bad} from './dep';export function f<T>(unused:number){const value={};value.missing;let orphan=1;return bad;}"
        }, configuredOptions: finalOptions);
        var finalChecker = await finalProgram.CreateCheckerAsync();
        var finalMain = finalProgram.GetFile("/project/main.ts")!.Syntax;
        var finalDep = finalProgram.GetFile("/project/dep.ts")!.Syntax;
        await finalChecker.CheckSourceFileAsync(finalMain);
        Check(finalChecker.DiagnosticCodesForFile(finalMain).SequenceEqual([2339, 6133, 6133, 6196]));
        Check(finalChecker.DeferredMissingProperties.Count == 1 && finalChecker.DiagnosticCodesForFile(finalDep).Count == 0);
        await finalChecker.CheckSourceFileAsync(finalDep);
        Check(finalChecker.DiagnosticCodesForFile(finalDep).SequenceEqual([2339]));
        Check(finalChecker.DeferredMissingProperties.Count == 0 && finalChecker.CheckedFileCount == 2);
        await finalChecker.CheckProgramAsync();
        Check(finalChecker.DiagnosticCodesForFile(finalMain).SequenceEqual([2339, 6133, 6133, 6196]));
        var finalIndependent = await finalProgram.CreateCheckerAsync();
        await finalIndependent.CheckProgramAsync();
        Check(finalIndependent.DiagnosticCodesForFile(finalMain).SequenceEqual(finalChecker.DiagnosticCodesForFile(finalMain))
            && finalIndependent.DiagnosticCodesForFile(finalDep).SequenceEqual([2339]));
        var directiveProgram = await Build(new()
        {
            ["/project/directives.ts"] = "// 日本語 😀\n// @ts-ignore\nlet first:number='bad';\n// @ts-expect-error\n\n// comment\nlet second:number='bad';\n// @ts-expect-error\nlet unused=1;\n// @ts-ignore\n/* barrier */\nlet barrier:number='bad';"
        });
        var directiveChecker = await directiveProgram.CreateCheckerAsync();
        await directiveChecker.CheckProgramAsync();
        var directiveFile = directiveProgram.SourceFiles[0].Syntax;
        Check(directiveChecker.DiagnosticCodesForFile(directiveFile).SequenceEqual([2322, 2322, 2322]));
        Check(directiveChecker.DiagnosticCodesForProgramFile(directiveFile).SequenceEqual([2322, 2578]));
        Check(directiveFile.CommentDirectives.Count(d => d.ExpectError) == 2);
        var duplicateProgram = await Build(new() { ["/project/duplicate.ts"] = "const duplicate=1;const duplicate=2;" });
        var duplicateChecker = await duplicateProgram.CreateCheckerAsync();
        await duplicateChecker.CheckProgramAsync();
        Check(duplicateChecker.DiagnosticCodesForProgramFile(duplicateProgram.SourceFiles[0].Syntax).SequenceEqual([2451, 2451]));
        var noCheckOptions = new CompilerOptions();
        noCheckOptions.SetRaw("noCheck", "true");
        var noCheckProgram = await Build(new() { ["/project/unchecked.ts"] = "absent;" }, configuredOptions: noCheckOptions);
        var noCheckChecker = await noCheckProgram.CreateCheckerAsync();
        await noCheckChecker.CheckProgramAsync();
        Check(
            noCheckChecker.CheckedFileCount == 0
                && noCheckChecker.DiagnosticCodesForProgramFile(noCheckProgram.SourceFiles[0].Syntax).Count == 0);
        var noCheckDirectiveProgram = await Build(new() { ["/project/unchecked.ts"] = "// @ts-nocheck\nabsent;" });
        var noCheckDirectiveChecker = await noCheckDirectiveProgram.CreateCheckerAsync();
        await noCheckDirectiveChecker.CheckProgramAsync();
        Check(
            noCheckDirectiveChecker.CheckedFileCount == 0
                && noCheckDirectiveChecker.DiagnosticCodesForProgramFile(noCheckDirectiveProgram.SourceFiles[0].Syntax).Count == 0);
        var loopProgram = await Build(new()
        {
            ["/project/loop.ts"] = "type Candidate={mode:'a';output:unknown}|{mode:'b'};export function run():never{let lastCandidate:Candidate|null=null;while(true){const candidate:Candidate={mode:'a',output:lastCandidate} as const;lastCandidate=candidate;}}"
        });
        var loopChecker = await loopProgram.CreateCheckerAsync();
        // Guard the corpus reproduction that previously recursed without terminating.
        using var loopCancellation = new CancellationTokenSource();
        using var loopFinished = new ManualResetEventSlim();
        var loopGuard = new Thread(() =>
        {
            if (!loopFinished.Wait(TimeSpan.FromSeconds(10)))
                loopCancellation.Cancel();
        })
        { IsBackground = true };
        loopGuard.Start();
        try
        {
            await loopChecker.CheckProgramAsync(loopCancellation.Token);
        }
        finally
        {
            loopFinished.Set();
            loopGuard.Join();
        }
        Check(loopChecker.CheckedFileCount == 1 && loopChecker.DiagnosticCodesForFile(loopProgram.SourceFiles[0].Syntax).Count == 0);
        Check(loopChecker.FlowTypes.ActiveLoopCount == 0);
        checks += await DisposableSafety();
        checks += await ImportSafety();
        checks += await DeclarationSafety();
        checks += await ImportPathSafety();
        checks += await ContextGrammarSafety();
        checks += await DiagnosticDetailsSafety();
        checks += await CheckerDisplayTests.Safety();
        checks += await CheckerQueryTests.Safety();
        checks += await CheckerQueryTests.SymbolSafety();
        checks += await CheckerQueryTests.ScopeSafety();
        checks += await CheckerContextQueryTests.Safety();
        checks += await CheckerVisibilityTests.Safety();
        checks += await CheckerSymbolChainTests.Safety();
        checks += await CheckerAccessibilityTests.Safety();
        Console.WriteLine($"{checks} program/checker ownership assertions; interface and scope depth 20000");
    }

    private static async Task<int> DiagnosticDetailsSafety()
    {
        const string source = """
            // 多字节 😀
            const value = absent;
            // @ts-ignore
            absent;
            // @ts-expect-error
            const valid = 1;
            const named = (x: number) => {
                return x;
            };
            class C { protected constructor() {} }
            const missing = class {};
            const empty = () => {};
            const satisfied = value satisfies number;
            switch (value) { case 1: break; default: break; }
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts")!.Syntax;
        await checker.CheckProgramAsync();
        var diagnostics = checker.DetailedDiagnosticsForProgramFile(file);
        var missing = diagnostics.Single(d => d.Code == 2304);
        if (missing.Arguments is not ["absent"] || missing.FileName != file.FileName
            || missing.Start != file.Source.ToBytePosition(source.IndexOf("absent", StringComparison.Ordinal))
            || missing.Length != 6 || missing.Format() != "Cannot find name 'absent'.")
            throw new InvalidOperationException("Complete missing-name diagnostic");
        if (diagnostics.Count != 2 || diagnostics.Single(d => d.Code == 2578).Format() != "Unused '@ts-expect-error' directive.")
            throw new InvalidOperationException("Diagnostic directive filtering");
        var repeat = checker.DetailedDiagnosticsForProgramFile(file);
        if (!repeat.SequenceEqual(diagnostics))
            throw new InvalidOperationException("Detailed diagnostics changed on repeated read");
        var nodes = file.DescendantsAndSelf().ToArray();
        string Span(SyntaxNode node)
        {
            var (start, end) = CheckerDiagnostic.ErrorRange(file, node);
            return file.Source.Text[file.Source.ToUtf16Position(start)..file.Source.ToUtf16Position(end)];
        }
        if (Span(nodes.OfType<VariableDeclarationNode>().First()) != "value"
            || Span(nodes.OfType<ReturnStatementNode>().Single()) != "return"
            || Span(nodes.OfType<ArrowFunctionNode>().First()) != "(x: number) => {"
            || Span(nodes.OfType<ConstructorDeclarationNode>().Single()) != "protected constructor"
            || Span(nodes.OfType<ClassExpressionNode>().Single()) != "class"
            || Span(nodes.OfType<SatisfiesExpressionNode>().Single()) != "satisfies"
            || Span(nodes.OfType<CaseOrDefaultClauseNode>().First()) != "case 1:")
            throw new InvalidOperationException("Checker error ranges");
        string[] arguments = ["original"];
        var owned = CheckerDiagnostic.Create(nodes.OfType<VariableDeclarationNode>().First(), Messages.Cannot_find_name_0, arguments);
        arguments[0] = "changed";
        if (owned.Arguments is not ["original"])
            throw new InvalidOperationException("Diagnostic did not retain its arguments");
        var chain = owned with { MessageChain = [owned with { MessageChain = [owned] }] };
        if (chain.Format() != "Cannot find name 'original'.\n  Cannot find name 'original'.\n    Cannot find name 'original'.")
            throw new InvalidOperationException("Diagnostic message-chain formatting");
        return 6;
    }

    private static async Task<int> ContextGrammarSafety()
    {
        const string source = """
            import { value } from 'pkg' with { type: 1, active: true };
            import { value as other } from 'pkg' with { type: {} };
            try {} catch ({ message }: any) { const text = message; }
            try {} catch (error: number) {}
            try {} catch (error = 0) {}
            try {} catch (error) { let error; }
            declare let invalid = 1;
            declare const typed: number = 1;
            declare const good = -1;
            declare const bad = 1 + 2;
            declare enum E { A = 0 }
            declare const member = E.A;
            declare const index = E['A'];
            declare const aliasMember = member;
            declare class C { readonly item = E['A']; mutable = 1; readonly bad = aliasMember; }
            """;
        const string globals = """
            interface ImportAttributes { [key: string]: string | number | boolean; }
            declare module 'pkg' { export const value: number; }
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("target", "\"esnext\"");
        options.SetRaw("module", "\"preserve\"");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source), ["/project/globals.d.ts"] = Wtf8.Encode(globals) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts", "/project/globals.d.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts")!.Syntax;
        await checker.CheckSourceFileAsync(file);
        var codes = checker.DiagnosticCodesForFile(file);
        if (!codes.SequenceEqual([1039, 1039, 1039, 1196, 1197, 1254, 1254, 1254, 2322, 2492, 2858, 2858, 2858]))
            throw new InvalidOperationException($"Context grammar diagnostics: {string.Join(',', codes)}");
        return 1;
    }

    private static async Task<int> ImportPathSafety()
    {
        const string source = """
            import { value } from './dep';
            import { view } from './view';
            import './absent';
            import './data.json';
            import './dep.ts';
            import './script.js';
            import './theme.asset';
            import fs = require('fs');
            import 'fs';
            import untyped from 'untyped';
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("module", "\"node16\"");
        options.SetRaw("jsx", "\"preserve\"");
        options.SetRaw("strict", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.mts"] = Wtf8.Encode(source),
            ["/project/globals.d.ts"] = Wtf8.Encode("declare module '*.asset' {}"),
            ["/project/dep.ts"] = Wtf8.Encode("export const value = 1;"),
            ["/project/dep.mts"] = Wtf8.Encode("export const value = 1;"),
            ["/project/view.tsx"] = Wtf8.Encode("export const view = 1;"),
            ["/project/script.ts"] = Wtf8.Encode("const value = 1;"),
            ["/project/node_modules/untyped/index.js"] = Wtf8.Encode("exports.value = 1;"),
            ["/project/node_modules/untyped/package.json"] = Wtf8.Encode(
                "{\"name\":\"untyped\",\"version\":\"1.0.0\",\"main\":\"index.js\"}"),
            ["/project/data.json"] = Wtf8.Encode("{}")
        }), "/project", new("/project/tsconfig.json", options, ["/project/main.mts", "/project/globals.d.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.mts")!.Syntax;
        await checker.CheckSourceFileAsync(file);
        var codes = checker.DiagnosticCodesForFile(file);
        if (!codes.SequenceEqual([2591, 2732, 2834, 2835, 2835, 2882, 7016]))
            throw new InvalidOperationException($"Import path diagnostics: {string.Join(',', codes)}");
        if (checker.SuggestedImportExtension("/project/dep") != ".mjs")
            throw new InvalidOperationException("Import extension priority changed");
        if (checker.SuggestedImportExtension("/project/view") != ".jsx")
            throw new InvalidOperationException("Preserved JSX extension changed");
        return 3;
    }

    private static async Task<int> DeclarationSafety()
    {
        static async ValueTask<CompilerProgram> Build(Dictionary<string, string> files, bool noEmit = false)
        {
            var options = new CompilerOptions();
            options.SetRaw("noLib", "true");
            options.SetRaw("target", "\"es2015\"");
            options.SetRaw("module", "\"commonjs\"");
            options.SetRaw("noEmit", noEmit ? "true" : "false");
            options.SetRaw("allowJs", "true");
            return await CompilerProgram.CreateAsync(new MemoryFileSystem(files.ToDictionary(p => p.Key, p => Wtf8.Encode(p.Value))),
                "/project", new("/project/tsconfig.json", options, files.Keys.ToArray(), [], [], []));
        }
        var duplicates = await Build(new()
        {
            ["/project/a.ts"] = "class Duplicate {} let repeated: number; enum Choice {}",
            ["/project/b.ts"] = "class Duplicate {} let repeated: number; interface Choice {}"
        });
        var duplicateChecker = await duplicates.CreateCheckerAsync();
        foreach (var file in duplicates.SourceFiles)
            if (!duplicateChecker.DiagnosticCodesForFile(file.Syntax).SequenceEqual([2300, 2451, 2567]))
                throw new InvalidOperationException("Merge diagnostics lost declaration file attribution");
        var related = duplicateChecker.Environment.MergeRelatedDeclarations;
        if (related.Count != 6 || related.Any(p => p.Value.Count != 1
            || SemanticSyntax.Source(p.Key.Node) == SemanticSyntax.Source(p.Value[0])))
            throw new InvalidOperationException("Merge diagnostics lost related declarations");
        var plainJs = await Build(new()
        {
            ["/project/a.js"] = "class Duplicate {}",
            ["/project/b.ts"] = "class Duplicate {}"
        });
        var jsChecker = await plainJs.CreateCheckerAsync();
        if (jsChecker.DiagnosticCodesForFile(plainJs.GetFile("/project/a.js")!.Syntax).Count != 0
            || !jsChecker.DiagnosticCodesForFile(plainJs.GetFile("/project/b.ts")!.Syntax).SequenceEqual([2300]))
            throw new InvalidOperationException("Plain JavaScript merge suppression affected the TypeScript declaration");
        const string source = """
            export {};
            const require = 0;
            const { exports } = { exports: 0 };
            function parameters(require: number, exports: number) { return require + exports; }
            function Reflect() {}
            declare class Base { static value(): number; }
            class Derived extends Base { static result = super.value(); }
            const WeakMap = 0;
            class Private { #value = 0; }
            """;
        foreach (bool noEmit in new[] { false, true })
        {
            var names = await Build(new() { ["/project/main.ts"] = source }, noEmit);
            var checker = await names.CreateCheckerAsync();
            var file = names.GetFile("/project/main.ts")!.Syntax;
            await checker.CheckSourceFileAsync(file);
            int[] expected = noEmit ? [] : [2441, 2441, 2818, 18027];
            var codes = checker.DiagnosticCodesForFile(file);
            if (!codes.SequenceEqual(expected))
                throw new InvalidOperationException($"Declaration collision diagnostics (noEmit={noEmit}): {string.Join(',', codes)}");
        }
        return 6;
    }

    private static async Task<int> ImportSafety()
    {
        const string globals = """
            interface Array<T> { length: number; [n: number]: T; }
            interface Promise<T> { then(onfulfilled: (value: T) => unknown): unknown; }
            declare const Promise: any;
            interface ImportAttributes { [key: string]: string; }
            interface ImportCallOptions { with?: ImportAttributes; }
            declare module '*.asset' { const value: number; export default value; }
            declare module '*.asset' with { type: 'text' } { const value: string; export default value; }
            """;
        const string source = """
            import type { Box } from './dep';
            type Typed = import('./dep').Box<number>;
            type Factory = typeof import('./dep').make<string>;
            declare let typed: Typed;
            const number: number = typed.value;
            declare let make: Factory;
            const text: string = make('text');
            const dynamic = import('./dep');
            const promised: Promise<typeof import('./dep')> = dynamic;
            import(123);
            type Invalid = import('./dep');
            import asset from './file.asset' with { type: 'text' };
            const assetText: string = asset;
            type Asset = typeof import('./file.asset', { with: { type: 'text' } });
            declare let projected: Asset;
            const projectedText: string = projected.default;
            import('./file.asset', { with: { type: 1 } });
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        options.SetRaw("module", "\"preserve\"");
        options.SetRaw("target", "\"esnext\"");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.ts"] = Wtf8.Encode(source),
            ["/project/globals.d.ts"] = Wtf8.Encode(globals),
            ["/project/dep.ts"] = Wtf8.Encode(
                "export class Box<T> { constructor(public value: T) {} } export function make<T>(value: T) { return value; }")
        }), "/project", new("/project/tsconfig.json", options, ["/project/main.ts", "/project/globals.d.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts")!.Syntax;
        var nodes = file.DescendantsAndSelf().ToArray();
        var parents = nodes.Select(n => n.Parent).ToArray();
        await checker.CheckSourceFileAsync(file);
        var codes = checker.DiagnosticCodesForFile(file);
        if (!codes.SequenceEqual([1340, 2322, 7036]))
            throw new InvalidOperationException($"Import diagnostics: {string.Join(',', codes)}");
        if (!nodes.Select(n => n.Parent).SequenceEqual(parents))
            throw new InvalidOperationException("Import checking changed source parents");
        var initializer = nodes.OfType<VariableDeclarationNode>().Single(n => n.Name is IdentifierNode { Text: "dynamic" }).Initializer!;
        var type = await checker.GetExpressionTypeAsync(initializer);
        if (type is not TypeReference reference || reference.Target?.Symbol?.Name != "Promise")
            throw new InvalidOperationException("Dynamic import did not return Promise");
        var arguments = await checker.TypeArgumentsAsync(reference, default);
        if (await checker.Properties.PropertyAsync(arguments.Single(), "make") is null)
            throw new InvalidOperationException("Dynamic import lost module exports");
        return 4;
    }

    private static async Task<int> DisposableSafety()
    {
        const string library = """
            interface Array<T> { length: number; [n: number]: T; }
            interface SymbolConstructor { readonly dispose: unique symbol; readonly asyncDispose: unique symbol; }
            declare const Symbol: SymbolConstructor;
            interface Disposable { [Symbol.dispose](): void; }
            interface AsyncDisposable { [Symbol.asyncDispose](): PromiseLike<void>; }
            interface PromiseLike<T> { then(onfulfilled: (value: T) => unknown): unknown; }
            interface Promise<T> extends PromiseLike<T> { }
            declare const Promise: any;
            """;
        const string source = """
            export {};
            function sync() {
                using good = { [Symbol.dispose]() {} };
                using bad = 1;
                switch (0) { case 0: using invalid = null; }
            }
            async function asynchronous() {
                await using good = { [Symbol.asyncDispose]() { return null as any; } };
                await using bad = 1;
            }
            function nonAsync() { await using invalid = null; }
            class C { static { await using invalid = null; } }
            using { x } = { x: null };
            """;
        static async ValueTask<(Checker Checker, SourceFileNode File)> Create(
            string text,
            string library,
            string? helpers,
            bool importHelpers)
        {
            var options = new CompilerOptions();
            options.SetRaw("noLib", "true");
            options.SetRaw("strict", "true");
            options.SetRaw("target", "\"es2015\"");
            options.SetRaw("module", "\"esnext\"");
            options.SetRaw("importHelpers", importHelpers ? "true" : "false");
            var files = new Dictionary<string, byte[]>
            { ["/project/main.ts"] = Wtf8.Encode(text), ["/project/globals.d.ts"] = Wtf8.Encode(library) };
            if (helpers is not null)
                files.Add("/project/node_modules/tslib/index.d.ts", Wtf8.Encode(helpers));
            var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project",
                new("/project/tsconfig.json", options, ["/project/main.ts", "/project/globals.d.ts"], [], [], []));
            return (await program.CreateCheckerAsync(), program.GetFile("/project/main.ts")!.Syntax);
        }
        var (checker, file) = await Create(source, library, null, false);
        await checker.CheckSourceFileAsync(file);
        int[] expected = [1492, 1547, 2850, 2851, 2852, 18054];
        var codes = checker.DiagnosticCodesForFile(file);
        if (!codes.SequenceEqual(expected))
            throw new InvalidOperationException($"Disposable diagnostics: {string.Join(',', codes)}");
        int checks = 1;
        foreach (string? helpers in new string?[]
        {
            null,
            "export {};",
            "export declare const __addDisposableResource: any, __disposeResources: any;"
        })
        {
            var (helperChecker, helperFile) = await Create("export {}; using first = null; using second = null;", library, helpers, true);
            await helperChecker.CheckSourceFileAsync(helperFile);
            int[] helperExpected = helpers is null ? [2354] : helpers == "export {};" ? [2343, 2343] : [];
            var helperCodes = helperChecker.DiagnosticCodesForFile(helperFile);
            if (!helperCodes.SequenceEqual(helperExpected))
                throw new InvalidOperationException($"Disposable helper diagnostics: {string.Join(',', helperCodes)}");
            checks++;
        }
        return checks;
    }

    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                Process(input.RootElement, writer).GetAwaiter().GetResult();
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    private static async Task Process(JsonElement input, Utf8JsonWriter writer)
    {
        var files = input.GetProperty("files").EnumerateObject().ToDictionary(
            p => p.Name,
            p => Convert.FromBase64String(p.Value.GetString()!));
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        if (input.TryGetProperty("options", out var supplied))
            foreach (var property in supplied.EnumerateObject())
                options.Set(property.Name, property.Value);
        var roots = input.GetProperty("roots").EnumerateArray().Select(p => p.GetString()!).ToArray();
        int concurrency = input.GetProperty("concurrency").GetInt32();
        var config = new ParsedConfig("/project/tsconfig.json", options, roots, [], [], []);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project", config, concurrency: concurrency);
        Checker? typeHost = input.TryGetProperty("typeNodes", out var typeOption) && typeOption.GetBoolean()
            ? await program.CreateCheckerAsync() : null;
        var context = typeHost?.Context ?? new TypeContext(options.StrictOption("strictNullChecks"),
            options.Boolean("exactOptionalPropertyTypes") ?? false);
        var links = typeHost?.Links ?? new CheckerLinks();
        var host = typeHost?.Environment ?? new CheckerEnvironment(context, links);
        var environment = typeHost?.Symbols ?? await CheckerSymbols.CreateAsync(program, links, host);
        if (input.TryGetProperty("typeDisplays", out var displayOption) && displayOption.GetBoolean())
        {
            writer.WriteStartObject();
            writer.WriteStartArray("typeDisplays");
            foreach (var declaration in program.GetFile("/project/main.ts")!.Syntax.DescendantsAndSelf().OfType<VariableDeclarationNode>())
                if (declaration.Name is IdentifierNode name
                    && name.Text.StartsWith("show", StringComparison.Ordinal)
                    && declaration.Type is not null)
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(name.Text);
                    writer.WriteStringValue(
                        await typeHost!.TypeDisplay.GetAsync(await typeHost.GetTypeFromTypeNodeAsync(declaration.Type)));
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
            writer.WriteEndObject();
            return;
        }
        if (input.TryGetProperty("semantic", out var semanticOption) && semanticOption.GetBoolean())
        {
            await typeHost!.CheckSourceFileAsync(program.GetFile("/project/main.ts")!.Syntax);
            writer.WriteStartObject();
            writer.WriteStartArray("semanticDiagnostics");
            foreach (int code in typeHost!.DiagnosticCodesForFile(program.GetFile("/project/main.ts")!.Syntax))
                writer.WriteNumberValue(code);
            writer.WriteEndArray();
            if (input.TryGetProperty("semanticDetails", out var detailsOption) && detailsOption.GetBoolean())
            {
                writer.WritePropertyName("semanticDiagnosticDetails");
                CheckerCorpusTests.WriteDiagnostics(
                    writer,
                    typeHost.DetailedDiagnosticsForFile(program.GetFile("/project/main.ts")!.Syntax));
            }
            writer.WriteEndObject();
            return;
        }
        var nodes = program.SourceFiles.SelectMany(file => file.Syntax.DescendantsAndSelf()).ToArray();
        var nodeIds = nodes.Select((node, i) => (node, i)).ToDictionary(p => p.node, p => p.i + 1);
        int Node(SyntaxNode? node) => node is null ? 0 : nodeIds.GetValueOrDefault(node);
        var symbolIds = new Dictionary<Symbol, int>(ReferenceEqualityComparer.Instance);
        var symbols = new List<Symbol>();
        int SymbolId(Symbol? symbol)
        {
            if (symbol is null)
                return 0;
            if (!symbolIds.TryGetValue(symbol, out int id))
            {
                symbolIds.Add(symbol, id = symbols.Count + 1);
                symbols.Add(symbol);
            }
            return id;
        }
        var typeIds = new Dictionary<Type, int>();
        var types = new List<Type>();
        int TypeId(Type? type)
        {
            if (type is null)
                return 0;
            if (!typeIds.TryGetValue(type, out int id))
            {
                typeIds.Add(type, id = types.Count + 1);
                types.Add(type);
            }
            return id;
        }
        var privateOwners = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes.Where(SemanticSyntax.ClassLike))
            if (environment.Binding(node)?.Get(node)?.Symbol is { } owner)
                foreach (string key in owner.Members.Keys.Concat(owner.Exports.Keys))
                    if (key.StartsWith(Symbol.InternalPrefix + "#", StringComparison.Ordinal) && key.IndexOf('@') is > 0 and var end)
                        privateOwners[key[..end]] = Node(node);
        string CanonicalName(string name)
        {
            if (name.StartsWith(Symbol.InternalPrefix + "@", StringComparison.Ordinal))
                foreach (var unique in context.UniqueSymbols)
                    if (unique.Name == name && unique.Symbol?.Declarations.FirstOrDefault() is { } declaration)
                        return Symbol.InternalPrefix + "@" + unique.Symbol.Name + "@node" + Node(declaration).ToString(CultureInfo.InvariantCulture);
            int end = name.IndexOf('@');
            return end > 0 && privateOwners.TryGetValue(name[..end], out int owner)
                ? Symbol.InternalPrefix + "#node" + owner.ToString(CultureInfo.InvariantCulture) + name[end..] : name;
        }
        void Name(string text) => writer.WriteBase64StringValue(Wtf8.Encode(Symbol.EscapeName(CanonicalName(text))));
        void Table(IReadOnlyDictionary<string, Symbol> table)
        {
            writer.WriteStartArray();
            foreach (var (name, symbol) in table.OrderBy(p => CanonicalName(p.Key), Comparer<string>.Create(TypeOrder.CompareSymbolNames)))
            {
                writer.WriteStartArray();
                Name(name);
                writer.WriteNumberValue(SymbolId(symbol));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        void TypeIds(IEnumerable<Type>? values)
        {
            if (values is null)
            {
                writer.WriteNullValue();
                return;
            }
            writer.WriteStartArray();
            foreach (var type in values)
                writer.WriteNumberValue(TypeId(type));
            writer.WriteEndArray();
        }
        IReadOnlyList<Type>? OrderedInferences(IReadOnlyList<Type>? values)
        {
            if (values is null)
                return null;
            var result = values.ToArray();
            var groups = new Dictionary<SyntaxNode, List<int>>();
            for (int i = 0; i < values.Count; i++)
            {
                var declaration = values[i].Symbol?.Declarations.FirstOrDefault();
                if (declaration?.Parent is not InferTypeNode infer)
                    continue;
                for (var owner = infer.Parent; owner is not null; owner = owner.Parent)
                    if (owner is ConditionalTypeNode)
                    {
                        if (!groups.TryGetValue(owner, out var positions))
                            groups[owner] = positions = [];
                        positions.Add(i);
                        break;
                    }
            }
            foreach (var positions in groups.Values)
            {
                var parameters = positions.Select(i => values[i]).OrderBy(t => Node(t.Symbol!.Declarations[0])).ToArray();
                for (int i = 0; i < positions.Count; i++)
                    result[positions[i]] = parameters[i];
            }
            return result;
        }
        writer.WriteStartObject();
        writer.WriteStartArray("files");
        foreach (var file in program.SourceFiles)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(file.Syntax.FileName);
            writer.WriteBooleanValue(file.Binding.IsModule);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("globals");
        Table(environment.Globals);
        writer.WriteStartArray("patterns");
        foreach (var pattern in environment.PatternModules)
        {
            writer.WriteStartArray();
            Name(pattern.Pattern);
            writer.WriteNumberValue(SymbolId(pattern.Symbol));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("augmentations");
        Table(environment.PatternAugmentations);
        writer.WritePropertyName("augmentationTargets");
        Table(environment.PatternTargets);
        writer.WriteStartArray("globalTypes");
        foreach (var (name, type) in host.Globals.Types.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(name);
            writer.WriteNumberValue(TypeId(type));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("specialTypes");
        foreach (var symbol in new[]
        {
            environment.UndefinedSymbol,
            environment.ArgumentsSymbol,
            environment.UnknownSymbol,
            environment.GlobalThisSymbol
        })
            writer.WriteNumberValue(TypeId(links.Values.Get(symbol).ResolvedType));
        writer.WriteNumberValue(TypeId(host.Globals.AnyArrayType));
        writer.WriteNumberValue(TypeId(host.Globals.AutoArrayType));
        writer.WriteNumberValue(TypeId(host.Globals.AnyReadonlyArrayType));
        writer.WriteEndArray();
        writer.WriteStartArray("declarations");
        foreach (var node in nodes)
            if (environment.Binding(node)?.Get(node)?.Symbol is not null)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(Node(node));
                writer.WriteNumberValue(
                    SymbolId(
                    input.TryGetProperty("references", out var referenceDeclarations) && referenceDeclarations.GetBoolean()
                    ? environment.Merger.GetMergedSymbol(environment.Binding(node)!.Get(node)!.Symbol) : environment.Declaration(node)));
                writer.WriteEndArray();
            }
        writer.WriteEndArray();
        writer.WriteStartArray("classes");
        foreach (var node in nodes.Where(
            n => n.Kind is SyntaxKind.ClassDeclaration or SyntaxKind.ClassExpression or SyntaxKind.InterfaceDeclaration))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Node(node));
            writer.WriteNumberValue(TypeId(await host.Scopes.ClassOrInterfaceAsync(environment.Declaration(node)!)));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("scopes");
        foreach (var node in nodes.Where(n => n.Kind is SyntaxKind.TypeReference or SyntaxKind.ThisType or SyntaxKind.TypeParameter))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Node(node));
            TypeIds(OrderedInferences(await host.Scopes.OuterAsync(node)));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        if (input.TryGetProperty("aliases", out var aliasOption) && aliasOption.GetBoolean())
        {
            writer.WriteStartArray("aliases");
            var seenAliases = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
            foreach (var node in nodes)
            {
                var symbol = environment.Declaration(node);
                if (symbol is null || (symbol.Flags & SymbolFlags.Alias) == 0 || !seenAliases.Add(symbol))
                    continue;
                writer.WriteStartArray();
                writer.WriteNumberValue(SymbolId(symbol));
                writer.WriteNumberValue(SymbolId(await host.Aliases.ResolveAsync(symbol)));
                writer.WriteNumberValue(SymbolId(await host.Aliases.ImmediateAsync(symbol)));
                writer.WriteNumberValue((uint)await host.Aliases.FlagsAsync(symbol));
                writer.WriteNumberValue((uint)await host.Aliases.FlagsAsync(symbol, excludeTypeOnly: true));
                writer.WriteNumberValue((uint)await host.Aliases.FlagsAsync(symbol, excludeLocal: true));
                writer.WriteNumberValue(Node(await host.Aliases.TypeOnlyAsync(symbol)));
                writer.WriteNumberValue(Node(await host.Aliases.TypeOnlyAsync(symbol, SymbolFlags.Value)));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("symbolDisplay", out var symbolDisplayOption) && symbolDisplayOption.GetBoolean())
            await CheckerSymbolDisplayTests.WriteAsync(writer, nodes, typeHost!, SymbolId, Node);
        if (input.TryGetProperty("accessibility", out var accessibilityOption) && accessibilityOption.GetBoolean())
            await CheckerAccessibilityTests.WriteAsync(writer, nodes, typeHost!, SymbolId, Node);
        if (input.TryGetProperty("symbolChains", out var symbolChainOption) && symbolChainOption.GetBoolean())
            await CheckerSymbolChainTests.WriteAsync(writer, nodes, typeHost!, SymbolId, Node);
        if (input.TryGetProperty("declarationVisibility", out var visibilityOption) && visibilityOption.GetBoolean())
            await CheckerVisibilityTests.WriteAsync(writer, nodes, typeHost!, SymbolId, Node);
        if (input.TryGetProperty("contextQueries", out var contextQueryOption) && contextQueryOption.GetBoolean())
            await CheckerContextQueryTests.WriteAsync(writer, nodes, typeHost!, TypeId, SymbolId, Node);
        if (input.TryGetProperty("scopeServices", out var servicesOption) && servicesOption.GetBoolean())
        {
            writer.WriteStartArray("serviceQueries");
            var seenModules = new HashSet<Symbol>();
            var seenAliases = new HashSet<Symbol>();
            void OrderedSymbols(IReadOnlyList<Symbol> source)
            {
                writer.WriteStartArray();
                foreach (var symbol in source.OrderBy(s => CanonicalName(s.Name), Comparer<string>.Create(TypeOrder.CompareSymbolNames)))
                    writer.WriteNumberValue(SymbolId(symbol));
                writer.WriteEndArray();
            }
            foreach (var node in nodes)
            {
                if (SemanticSyntax.Source(node)?.FileName.StartsWith("/project/main.", StringComparison.Ordinal) != true)
                    continue;
                foreach (var meaning in new[]
                {
                    SymbolFlags.Value,
                    SymbolFlags.Type,
                    SymbolFlags.Namespace,
                    SymbolFlags.Alias,
                    SymbolFlags.All
                })
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(0);
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue((uint)meaning);
                    OrderedSymbols(await typeHost!.GetSymbolsInScopeAsync(node, meaning));
                    writer.WriteEndArray();
                }
                var symbol = await typeHost!.GetSymbolAtLocationAsync(node);
                if (symbol is not null)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(1);
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue(SymbolId(symbol));
                    writer.WriteNumberValue(TypeId(await typeHost.GetTypeOfSymbolAtLocationAsync(symbol, node)));
                    writer.WriteNumberValue(TypeId(await typeHost.GetTypeOfSymbolAtLocationAsync(symbol, null)));
                    writer.WriteEndArray();
                    if ((symbol.Flags & SymbolFlags.Alias) != 0 && seenAliases.Add(symbol))
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue(3);
                        writer.WriteNumberValue(Node(node));
                        writer.WriteNumberValue(SymbolId(symbol));
                        writer.WriteNumberValue(SymbolId(await typeHost.GetAliasedSymbolAsync(symbol)));
                        writer.WriteEndArray();
                    }
                }
                if (QuerySyntax.Declaration(node) && environment.Declaration(node) is { } module
                    && (module.Flags & SymbolFlags.Module) != 0 && seenModules.Add(module))
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(2);
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue(SymbolId(module));
                    OrderedSymbols(await typeHost.GetExportsOfModuleAsync(module));
                    writer.WriteEndArray();
                }
                if (node is ExportSpecifierNode or ShorthandPropertyAssignmentNode)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(node is ExportSpecifierNode ? 4 : 5);
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue(SymbolId(node is ExportSpecifierNode
                        ? await typeHost.GetExportSpecifierLocalTargetSymbolAsync(node) : await typeHost.GetShorthandAssignmentValueSymbolAsync(node)));
                    writer.WriteEndArray();
                }
                if (DeclarationOrder.ParameterProperty(node) && node is ParameterDeclarationNode { Name: IdentifierNode name } parameter)
                {
                    var pair = await typeHost.GetSymbolsOfParameterPropertyDeclarationAsync(parameter, name.Text);
                    writer.WriteStartArray();
                    writer.WriteNumberValue(6);
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue(SymbolId(pair.Parameter));
                    writer.WriteNumberValue(SymbolId(pair.Property));
                    writer.WriteEndArray();
                }
            }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("symbolLocations", out var symbolLocationsOption) && symbolLocationsOption.GetBoolean())
        {
            writer.WriteStartArray("symbolLocationQueries");
            foreach (var node in nodes)
                if (SemanticSyntax.Source(node)?.FileName.StartsWith("/project/main.", StringComparison.Ordinal) == true)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue(SymbolId(await typeHost!.GetSymbolAtLocationAsync(node)));
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("documentationSymbols", out var documentationSymbolsOption) && documentationSymbolsOption.GetBoolean())
        {
            writer.WriteStartArray("documentationSymbolQueries");
            foreach (var owner in nodes)
                if (SemanticSyntax.Source(owner) is { } file && file.FileName.StartsWith("/project/main.", StringComparison.Ordinal))
                    foreach (var comment in await file.GetDocumentationAsync(owner))
                        foreach (var node in comment.DescendantsAndSelf())
                        {
                            writer.WriteStartArray();
                            writer.WriteNumberValue(Node(owner));
                            writer.WriteNumberValue((int)node.Kind);
                            writer.WriteNumberValue(node.Pos);
                            writer.WriteNumberValue(node.End);
                            writer.WriteNumberValue(SymbolId(await typeHost!.GetSymbolAtLocationAsync(node)));
                            writer.WriteEndArray();
                        }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("locations", out var locationsOption) && locationsOption.GetBoolean())
        {
            writer.WriteStartArray("locationQueries");
            foreach (var node in nodes)
                if (SemanticSyntax.Source(node)?.FileName.StartsWith("/project/main.", StringComparison.Ordinal) == true)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue(TypeId(await typeHost!.GetTypeAtLocationAsync(node)));
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
        }
        if (typeHost is not null)
        {
            writer.WriteStartArray("typeQueries");
            foreach (var node in nodes)
            {
                Type? result = null;
                if (node is TypeAliasDeclarationNode or EnumDeclarationNode)
                    result = await typeHost.Declared.GetAsync(environment.Declaration(node)!);
                else if (node is ITypedNode { Type: { } annotation }
                    && (node is IFunctionSignature
                        || node.Kind is SyntaxKind.VariableDeclaration or SyntaxKind.PropertyDeclaration or SyntaxKind.PropertySignature
                            or SyntaxKind.Parameter or SyntaxKind.IndexSignature))
                    result = await typeHost.GetTypeFromTypeNodeAsync(annotation);
                if (result is null)
                    continue;
                writer.WriteStartArray();
                writer.WriteNumberValue(Node(node));
                writer.WriteNumberValue(TypeId(result));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("access", out var accessOption) && accessOption.GetBoolean())
        {
            writer.WriteStartArray("accessQueries");
            foreach (var call in nodes.OfType<CallExpressionNode>())
                if (call.Expression is IdentifierNode { Text: "__access" })
                    foreach (var argument in call.Arguments!)
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue(Node(argument));
                        writer.WriteNumberValue(TypeId(await typeHost!.GetExpressionTypeAsync(argument)));
                        writer.WriteEndArray();
                    }
            writer.WriteEndArray();
            writer.WriteStartArray("accessSymbols");
            foreach (var node in nodes.Where(n => n is PropertyAccessExpressionNode or ElementAccessExpressionNode or QualifiedNameNode))
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(Node(node));
                writer.WriteNumberValue(SymbolId(links.SymbolNodes.Get(node).ResolvedSymbol));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("accessSuggestions");
            foreach (int code in host.ValueSuggestions.Concat(typeHost!.Suggestions).Order())
                writer.WriteNumberValue(code);
            writer.WriteEndArray();
            writer.WriteNumber("deferredAccessDiagnostics", typeHost.DeferredMissingProperties.Count);
        }
        if (input.TryGetProperty("identifiers", out var identifierOption) && identifierOption.GetBoolean())
        {
            writer.WriteStartArray("identifierQueries");
            foreach (var call in nodes.OfType<CallExpressionNode>())
                if (call.Expression is IdentifierNode { Text: "__expr" })
                    foreach (var argument in call.Arguments!)
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue(Node(argument));
                        writer.WriteNumberValue(TypeId(await typeHost!.GetExpressionTypeAsync(argument)));
                        writer.WriteEndArray();
                    }
            writer.WriteEndArray();
            writer.WriteStartArray("identifierAliasReferences");
            var seen = new HashSet<Symbol>();
            foreach (var node in nodes)
                if (environment.Declaration(node) is { } symbol && (symbol.Flags & SymbolFlags.Alias) != 0 && seen.Add(symbol))
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(SymbolId(symbol));
                    writer.WriteBooleanValue(links.Aliases.Get(symbol).Referenced);
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("flow", out var flowOption) && flowOption.GetBoolean())
        {
            writer.WriteStartArray("flowQueries");
            foreach (var call in nodes.OfType<CallExpressionNode>())
                if (call.Expression is IdentifierNode { Text: "__flow" })
                    foreach (var argument in call.Arguments!.OfType<IdentifierNode>())
                    {
                        var declared = await typeHost!.Values.GetAsync(host.ReferenceSymbols.Resolve(argument));
                        writer.WriteStartArray();
                        writer.WriteNumberValue(Node(argument));
                        writer.WriteNumberValue(TypeId(declared));
                        writer.WriteNumberValue(TypeId(await typeHost.FlowTypes.GetAsync(argument, declared)));
                        var flowNode = typeHost.FlowOf(argument);
                        writer.WriteBooleanValue(flowNode is null || await typeHost.FlowTypes.Reachability.ReachableAsync(flowNode));
                        writer.WriteEndArray();
                    }
            writer.WriteEndArray();
            writer.WriteStartArray("assignmentMarks");
            foreach (var declaration in nodes.Where(n => n is VariableDeclarationNode or ParameterDeclarationNode))
                if (environment.Declaration(declaration) is { } symbol && typeHost!.Assignments.ParameterOrMutableLocal(symbol))
                {
                    var mark = await typeHost.Assignments.GetAsync(symbol);
                    writer.WriteStartArray();
                    writer.WriteNumberValue(Node(declaration));
                    writer.WriteNumberValue(mark.LastPosition);
                    writer.WriteBooleanValue(mark.Definite);
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
            writer.WriteStartArray("flowState");
            writer.WriteNumberValue(typeHost!.FlowTypes.LoopCacheCount);
            writer.WriteNumberValue(typeHost.FlowTypes.ActiveLoopCount);
            writer.WriteNumberValue(typeHost.FlowTypes.SharedCount);
            writer.WriteBooleanValue(typeHost.FlowTypes.AnalysisDisabled);
            writer.WriteNumberValue(typeHost.FlowTypes.Reachability.ReachableCacheCount);
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("references", out var referenceOption) && referenceOption.GetBoolean())
        {
            writer.WriteStartArray("declarationOrder");
            foreach (var call in nodes.OfType<CallExpressionNode>())
                if (call.Expression is IdentifierNode { Text: "__order" })
                    foreach (var access in call.Arguments!.OfType<PropertyAccessExpressionNode>())
                    {
                        var owner = DeclarationOrder.ContainingClass(access)!;
                        var declaration = owner.DescendantsAndSelf().First(n =>
                            (n is PropertyDeclarationNode or MethodDeclarationNode || DeclarationOrder.ParameterProperty(n))
                            && SemanticSyntax.Name(n) is IdentifierNode name && name.Text == ((IdentifierNode)access.Name!).Text);
                        writer.WriteStartArray();
                        writer.WriteNumberValue(Node(access));
                        writer.WriteNumberValue(Node(declaration));
                        writer.WriteBooleanValue(await host.DeclarationOrder.BeforeUseAsync(declaration, access.Name!));
                        writer.WriteEndArray();
                    }
            writer.WriteEndArray();
            writer.WriteStartArray("referenceSyntax");
            foreach (var node in nodes)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(Node(node));
                writer.WriteBooleanValue(ReferenceSyntax.IsExpression(node));
                writer.WriteBooleanValue(ReferenceSyntax.ValidTypeOnlyUse(node));
                writer.WriteNumberValue(ReferenceSyntax.AccessKind(node));
                writer.WriteNumberValue(Node(ReferenceSyntax.AssignmentTarget(node)));
                writer.WriteNumberValue(ReferenceSyntax.AssignmentKind(node));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("referenceQueries");
            foreach (var call in nodes.OfType<CallExpressionNode>())
                if (call.Expression is IdentifierNode { Text: "__use" })
                    foreach (var argument in call.Arguments!.OfType<IdentifierNode>())
                    {
                        var symbol = host.ReferenceSymbols.Resolve(argument);
                        writer.WriteStartArray();
                        writer.WriteNumberValue(Node(argument));
                        writer.WriteNumberValue(SymbolId(symbol));
                        writer.WriteEndArray();
                    }
            writer.WriteEndArray();
            writer.WriteStartArray("referenceSuggestions");
            foreach (int code in host.ValueSuggestions.Order())
                writer.WriteNumberValue(code);
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("awaited", out var awaitedOption) && awaitedOption.GetBoolean())
        {
            writer.WriteStartArray("awaitedQueries");
            foreach (var node in nodes)
                if (node is TypeAliasDeclarationNode or InterfaceDeclarationNode && node is INamedNode { Name: IdentifierNode name }
                    && name.Text.Length > 1 && name.Text[0] == 'A' && char.IsAsciiDigit(name.Text[1]))
                {
                    var type = await typeHost!.Declared.GetAsync(environment.Declaration(node)!);
                    writer.WriteStartArray();
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue(TypeId(type));
                    writer.WriteNumberValue(TypeId((await typeHost.Awaited.PromisedAsync(type)).Type));
                    writer.WriteNumberValue(TypeId(await typeHost.Awaited.NoAliasAsync(type)));
                    writer.WriteNumberValue(TypeId(await typeHost.Awaited.GetAsync(type)));
                    writer.WriteBooleanValue(await typeHost.Awaited.NeededAsync(type));
                    writer.WriteBooleanValue(await typeHost.Awaited.ThenableAsync(type));
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("expressions", out var expressionOption) && expressionOption.GetBoolean())
        {
            writer.WriteStartArray("expressionQueries");
            foreach (var declaration in nodes.OfType<VariableDeclarationNode>())
                if (declaration.Initializer is { } expression)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(Node(expression));
                    writer.WriteNumberValue(TypeId(await typeHost!.GetExpressionTypeAsync(expression)));
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
            writer.WriteStartArray("suggestions");
            foreach (int code in typeHost!.Suggestions.Order())
                writer.WriteNumberValue(code);
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("constants", out var constantOption) && constantOption.GetBoolean())
        {
            writer.WriteStartArray("constantQueries");
            foreach (var member in nodes.OfType<EnumMemberNode>())
            {
                var result = await typeHost!.EnumValues.GetAsync(member);
                writer.WriteStartArray();
                writer.WriteNumberValue(Node(member));
                if (result.Value is null)
                    writer.WriteNullValue();
                else
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(result.Value is string ? "string" : "number");
                    if (result.Value is string text)
                        writer.WriteBase64StringValue(Wtf8.Encode(text));
                    else
                        writer.WriteStringValue(
                            BitConverter.DoubleToUInt64Bits((double)result.Value).ToString("x16", CultureInfo.InvariantCulture));
                    writer.WriteEndArray();
                }
                writer.WriteBooleanValue(result.IsSyntacticallyString);
                writer.WriteBooleanValue(result.ResolvedOtherFiles);
                writer.WriteBooleanValue(result.HasExternalReferences);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("indexing", out var indexingOption) && indexingOption.GetBoolean())
            await CheckerIndexTests.WriteAsync(writer, nodes, typeHost!, TypeId, Node);
        if (input.TryGetProperty("members", out var memberOption) && memberOption.GetBoolean())
            await CheckerMemberTests.WriteAsync(writer, nodes, environment, typeHost!, TypeId, SymbolId, Node,
                input.TryGetProperty("values", out var valueOption) && valueOption.GetBoolean(),
                input.TryGetProperty("signatures", out var signatureOption) && signatureOption.GetBoolean(),
                input.TryGetProperty("calls", out var callOption) && callOption.GetBoolean());
        if (input.TryGetProperty("properties", out var propertyOption) && propertyOption.GetBoolean())
            await CheckerPropertyTests.WriteAsync(writer, nodes, environment, typeHost!, TypeId, SymbolId, Node);
        if (input.TryGetProperty("identity", out var identityOption) && identityOption.GetBoolean())
            await CheckerRelationTests.WriteAsync(writer, nodes, environment, typeHost!, TypeId, Node,
                input.TryGetProperty("assignability", out var assignabilityOption) && assignabilityOption.GetBoolean());
        if (input.TryGetProperty("assertions", out var assertionOption) && assertionOption.GetBoolean())
            foreach (var assertion in typeHost!.Assertions.CheckedNodes.OrderBy(Node).ToArray())
                await typeHost.Assertions.DeferredAsync(assertion);
        var variableQueries = typeHost is not null
            && input.TryGetProperty("awaited", out var classifyVariables)
            && classifyVariables.GetBoolean()
            ? new TypeVariables(typeHost.References.TypeArgumentsAsync) : null;
        writer.WriteStartArray("types");
        for (int i = 0; i < types.Count; i++)
        {
            var type = types[i];
            var parameter = type as TypeParameter;
            var intf = type as InterfaceType;
            if (typeHost is not null && type is TypeReference lazy && (type.ObjectFlags & ObjectFlags.Reference) != 0)
                await typeHost.References.TypeArgumentsAsync(lazy);
            bool containsVariables = variableQueries is not null && await variableQueries.CouldContainAsync(type);
            writer.WriteStartArray();
            writer.WriteNumberValue((uint)type.Flags);
            writer.WriteNumberValue((uint)type.ObjectFlags);
            writer.WriteNumberValue(SymbolId(type.Symbol));
            writer.WriteNumberValue(TypeId(type is ObjectType obj ? obj.Target : parameter?.Target));
            TypeIds(type is TypeReference reference ? reference.ResolvedTypeArguments : null);
            TypeIds(intf?.AllTypeParameters);
            writer.WriteNumberValue(intf?.OuterTypeParameterCount ?? 0);
            writer.WriteNumberValue(TypeId(intf?.ThisType));
            writer.WriteBooleanValue(parameter?.IsThisType ?? false);
            writer.WriteNumberValue(TypeId(parameter?.Constraint));
            writer.WriteStringValue(type is IntrinsicType intrinsic ? intrinsic.IntrinsicName : "");
            if (typeHost is not null)
            {
                writer.WriteStartObject();
                if (variableQueries is not null)
                    writer.WriteBoolean("couldContainTypeVariables", containsVariables);
                if (type.Alias is { } typeAlias)
                {
                    writer.WriteStartArray("alias");
                    writer.WriteNumberValue(SymbolId(typeAlias.Symbol));
                    TypeIds(typeAlias.TypeArguments);
                    writer.WriteEndArray();
                }
                if (type is ConstrainedType constrained)
                    writer.WriteNumber("baseConstraint", TypeId(constrained.ResolvedBaseConstraint));
                switch (type)
                {
                    case LiteralType literal:
                        writer.WriteNumber("fresh", TypeId(literal.FreshType));
                        writer.WriteNumber("regular", TypeId(literal.RegularType));
                        writer.WritePropertyName("value");
                        switch (literal.Value)
                        {
                            case string value:
                                writer.WriteBase64StringValue(Wtf8.Encode(value));
                                break;
                            case double value:
                                writer.WriteStringValue(
                                    BitConverter.DoubleToUInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture));
                                break;
                            case BigInteger value:
                                writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
                                break;
                            case bool value:
                                writer.WriteBooleanValue(value);
                                break;
                            default:
                                writer.WriteNullValue();
                                break;
                        }
                        break;
                    case UnionOrIntersectionType composite:
                        writer.WritePropertyName("parts");
                        TypeIds(composite.Types);
                        if (type is UnionType union)
                            writer.WriteNumber("origin", TypeId(union.Origin));
                        break;
                    case TypeParameter:
                        writer.WriteBoolean("distributed", parameter!.IsDistributed);
                        writer.WriteNumber("default", TypeId(parameter.ResolvedDefaultType));
                        writer.WriteNumber("distributedType", TypeId(parameter.DistributedType));
                        break;
                    case IndexType index:
                        writer.WriteNumber("target", TypeId(index.Target));
                        writer.WriteNumber("indexFlags", (uint)index.IndexFlags);
                        break;
                    case IndexedAccessType indexed:
                        writer.WriteNumber("object", TypeId(indexed.ObjectType));
                        writer.WriteNumber("index", TypeId(indexed.IndexType));
                        writer.WriteNumber("accessFlags", (uint)indexed.AccessFlags);
                        break;
                    case TemplateLiteralType template:
                        writer.WriteStartArray("texts");
                        foreach (string text in template.Texts)
                            writer.WriteBase64StringValue(Wtf8.Encode(text));
                        writer.WriteEndArray();
                        writer.WritePropertyName("parts");
                        TypeIds(template.Types);
                        break;
                    case StringMappingType mapping:
                        writer.WriteNumber("target", TypeId(mapping.Target));
                        break;
                    case SubstitutionType substitution:
                        writer.WriteNumber("base", TypeId(substitution.BaseType));
                        writer.WriteNumber("constraint", TypeId(substitution.Constraint));
                        break;
                    case ConditionalType conditional:
                        writer.WriteStartArray("root");
                        writer.WriteNumberValue(Node(conditional.Root.Node));
                        writer.WriteNumberValue(TypeId(conditional.Root.CheckType));
                        writer.WriteNumberValue(TypeId(conditional.Root.ExtendsType));
                        writer.WriteBooleanValue(conditional.Root.IsDistributive);
                        TypeIds(OrderedInferences(conditional.Root.OuterTypeParameters));
                        TypeIds(OrderedInferences(conditional.Root.InferTypeParameters));
                        writer.WriteEndArray();
                        writer.WriteNumber("check", TypeId(conditional.CheckType));
                        writer.WriteNumber("extends", TypeId(conditional.ExtendsType));
                        writer.WriteNumber("true", TypeId(conditional.ResolvedTrueType));
                        writer.WriteNumber("false", TypeId(conditional.ResolvedFalseType));
                        writer.WriteNumber("inferredTrue", TypeId(conditional.ResolvedInferredTrueType));
                        writer.WriteNumber("defaultConstraint", TypeId(conditional.ResolvedDefaultConstraint));
                        writer.WriteNumber("distributiveConstraint", TypeId(conditional.ResolvedConstraintOfDistributive));
                        break;
                }
                if (type is TypeReference referenceType)
                    writer.WriteNumber("node", Node(referenceType.Node));
                if (type is InstantiationExpressionType instantiatedExpression)
                    writer.WriteNumber("node", Node(instantiatedExpression.Node));
                if (type is TupleType tuple)
                {
                    writer.WriteStartArray("tuple");
                    writer.WriteStartArray();
                    foreach (var info in tuple.ElementInfos)
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue((uint)info.Flags);
                        writer.WriteNumberValue(Node(info.LabeledDeclaration));
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                    writer.WriteNumberValue(tuple.MinLength);
                    writer.WriteNumberValue(tuple.FixedLength);
                    writer.WriteNumberValue((uint)tuple.CombinedFlags);
                    writer.WriteBooleanValue(tuple.IsReadonly);
                    writer.WriteEndArray();
                }
                if (type is MappedType mapped)
                {
                    writer.WriteNumber("parameter", TypeId(mapped.TypeParameter));
                    writer.WriteNumber("constraint", TypeId(mapped.ConstraintType));
                    writer.WriteNumber("template", TypeId(mapped.TemplateType));
                    writer.WriteNumber("name", TypeId(mapped.NameType));
                }
                if (type is ReverseMappedType reverse)
                {
                    writer.WriteStartArray("reverse");
                    writer.WriteNumberValue(TypeId(reverse.Source));
                    writer.WriteNumberValue(TypeId(reverse.MappedType));
                    writer.WriteNumberValue(TypeId(reverse.ConstraintType));
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        if (input.TryGetProperty("access", out var accessState) && accessState.GetBoolean())
        {
            writer.WriteStartArray("privateReferences");
            for (int i = 0; i < symbols.Count; i++)
            {
                var symbol = symbols[i];
                if (symbol.ValueDeclaration is not { } declaration
                    || !(SemanticSyntax.HasModifier(declaration, SyntaxKind.PrivateKeyword)
                        || SemanticSyntax.Name(declaration) is PrivateIdentifierNode))
                    continue;
                writer.WriteStartArray();
                writer.WriteNumberValue(i + 1);
                writer.WriteNumberValue((uint)environment.ReferenceKinds(symbol));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        writer.WriteStartArray("symbols");
        for (int i = 0; i < symbols.Count; i++)
        {
            var symbol = symbols[i];
            writer.WriteStartArray();
            Name(symbol.Name);
            writer.WriteNumberValue((uint)symbol.Flags);
            writer.WriteNumberValue((uint)symbol.CheckFlags);
            writer.WriteNumberValue(SymbolId(symbol.Parent));
            writer.WriteStartArray();
            foreach (var declaration in symbol.Declarations)
                writer.WriteNumberValue(Node(declaration));
            writer.WriteEndArray();
            writer.WriteNumberValue(Node(symbol.ValueDeclaration));
            Table(symbol.Members);
            Table(symbol.Exports);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("diagnostics");
        IEnumerable<int> diagnostics = host.Diagnostics;
        if (typeHost is not null)
            diagnostics = diagnostics.Concat(typeHost.Diagnostics).Concat(typeHost.Instantiation.Diagnostics)
                .Concat(typeHost.Instantiation.ConstraintDiagnostics).Concat(typeHost.AlgebraDiagnostics);
        foreach (int code in diagnostics.Order())
            writer.WriteNumberValue(code);
        writer.WriteEndArray();
        if (identifierOption.ValueKind == JsonValueKind.True)
        {
            writer.WriteStartArray("assignmentHints");
            foreach (int code in typeHost!.AssignmentHints.Select(h => h.Construct ? 6213 : 6212).Order())
                writer.WriteNumberValue(code);
            writer.WriteEndArray();
            writer.WriteStartArray("identifierSuggestions");
            foreach (int code in host.ValueSuggestions.Concat(typeHost!.Suggestions).Order())
                writer.WriteNumberValue(code);
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("identifiers", out var instantiationOption) && instantiationOption.GetBoolean())
        {
            writer.WriteStartArray("instantiationErrors");
            foreach (var error in typeHost!.InstantiationErrors.OrderBy(p => Node(p.Key)))
                writer.WriteStringValue(error.Value);
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("numberStrings", out var numberStrings))
        {
            writer.WriteStartArray("numberStrings");
            foreach (var item in numberStrings.EnumerateArray())
            {
                double number = TypeScript.Compiler.Semantics.JsNumber.FromString(Wtf8.DecodeString(item.GetBytesFromBase64()));
                writer.WriteStringValue(
                    double.IsNaN(number) ? "nan" : BitConverter.DoubleToUInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture));
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}

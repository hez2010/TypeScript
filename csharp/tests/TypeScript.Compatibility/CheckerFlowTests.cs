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

internal static class CheckerFlowTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Flow assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T} declare function __flow(value:unknown):void; function f(x:string|number,c:boolean){while(c){x=1;}__flow(x);const callback=(z:number)=>z;} function g(y:string|number,c:boolean){if(c){y=1;} (()=>{y='a';})();} ";
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }),
            "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scope = new CheckerEnvironment(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scope);
        var host = new Checker(context, links, scope);
        var nodes = program.SourceFiles[0].Syntax.DescendantsAndSelf().ToArray();
        var function = nodes.OfType<FunctionDeclarationNode>().Single(n => n.Name?.Text == "f");
        var reference = nodes.OfType<CallExpressionNode>().Single(n => n.Expression is IdentifierNode { Text: "__flow" }).Arguments![0];
        var symbol = scope.ReferenceSymbols.Resolve((IdentifierNode)reference);
        var declared = await host.Values.GetAsync(symbol);
        using (var cancellation = new CancellationTokenSource())
        {
            host.BeforeFlowExpression = _ =>
            {
                if (host.FlowTypes.ActiveLoopCount != 0)
                    cancellation.Cancel();
            };
            try
            {
                await host.FlowTypes.GetAsync(reference, declared, context.StringType, function, cancellation: cancellation.Token);
                throw new InvalidOperationException("Back-edge cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        Check(host.FlowTypes.ActiveLoopCount == 0 && host.FlowTypes.SharedCount == 0);
        Check(host.FlowTypes.LoopCacheCount == 0);
        host.BeforeFlowExpression = null;
        Check(await host.FlowTypes.GetAsync(reference, declared, context.StringType, function) == declared);
        int cacheCount = host.FlowTypes.LoopCacheCount;
        Check(cacheCount > 0 && await host.FlowTypes.GetAsync(reference, declared, context.StringType, function) == declared);
        Check(host.FlowTypes.LoopCacheCount == cacheCount && host.FlowTypes.ActiveLoopCount == 0);
        await host.FlowTypes.ExpressionAsync(reference, async () => await host.FlowTypes.GetAsync(reference, declared));
        var expressionCache = host.FlowTypes.ExpressionCache;
        Check(expressionCache is not null && expressionCache[reference] == declared);
        try
        {
            await host.FlowTypes.StableAsync(() =>
            {
                Check(host.FlowTypes.ExpressionCache is null && host.FlowTypes.ActiveLoopCount == 0);
                throw new OperationCanceledException();
            });
            throw new InvalidOperationException("Stable expression failure ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(host.FlowTypes.ExpressionCache == expressionCache);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await host.FlowTypes.GetAsync(reference, declared, cancellation: cancelled.Token);
            throw new InvalidOperationException("Cached flow cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(host.FlowTypes.SharedCount == 0 && !host.FlowTypes.AnalysisDisabled);

        var unrelated = new NumericLiteralNode { Text = "0" };
        var start = new FlowNode(FlowFlags.Start);
        FlowNode linear = start;
        for (int i = 0; i < 20_000; i++)
            linear = new(FlowFlags.Assignment, unrelated, linear);
        Check(await host.FlowTypes.GetAsync(reference, declared, context.StringType, flow: linear) == context.StringType);
        Check(await host.FlowTypes.Reachability.ReachableAsync(linear));
        FlowNode deep = start;
        for (int i = 0; i < 1999; i++)
            deep = new(FlowFlags.TrueCondition, new KeywordExpressionNode(SyntaxKind.FalseKeyword), deep);
        Check(await host.FlowTypes.GetAsync(reference, declared, context.StringType, flow: deep) == context.StringType);
        Check(!host.FlowTypes.AnalysisDisabled);
        deep = new(FlowFlags.TrueCondition, new KeywordExpressionNode(SyntaxKind.FalseKeyword), deep);
        Check(await host.FlowTypes.GetAsync(reference, declared, context.StringType, flow: deep) == context.ErrorType);
        Check(
            host.FlowTypes.AnalysisDisabled
                && host.Diagnostics.Contains(DiagnosticCode.TheContainingFunctionOrModuleBodyIsTooLargeForControlFlowAnalysis)
                && host.FlowTypes.SharedCount == 0);
        Check(await host.FlowTypes.GetAsync(reference, declared, flow: start) == context.ErrorType);
        host.FlowTypes.AnalysisDisabled = false;
        Check(await host.FlowTypes.GetAsync(reference, declared, context.StringType, flow: start) == context.StringType);

        var unreachable = new FlowNode(FlowFlags.Unreachable);
        var branch = new FlowNode(FlowFlags.BranchLabel);
        branch.AntecedentList.Add(start);
        branch.AntecedentList.Add(unreachable);
        Check(await host.FlowTypes.Reachability.ReachableAsync(branch));
        var reduced = new FlowNode(
            FlowFlags.ReduceLabel,
            antecedent: branch)
        { ReducedTarget = branch, ReducedAntecedents = [unreachable] };
        Check(!await host.FlowTypes.Reachability.ReachableAsync(reduced));
        Check(await host.FlowTypes.Reachability.ReachableAsync(branch));
        var super = new FlowNode(
            FlowFlags.Call,
            new CallExpressionNode { Expression = new KeywordExpressionNode(SyntaxKind.SuperKeyword) },
            start);
        Check(await host.FlowTypes.Reachability.PostSuperAsync(super));
        Check(!await host.FlowTypes.Reachability.PostSuperAsync(start));
        Check(await host.FlowTypes.Reachability.PostSuperAsync(unreachable));
        var superBranches = new FlowNode(FlowFlags.BranchLabel);
        superBranches.AntecedentList.Add(start);
        superBranches.AntecedentList.Add(super);
        Check(!await host.FlowTypes.Reachability.PostSuperAsync(superBranches));
        var reduceSuper = new FlowNode(
            FlowFlags.ReduceLabel,
            antecedent: superBranches)
        { ReducedTarget = superBranches, ReducedAntecedents = [super] };
        Check(await host.FlowTypes.Reachability.PostSuperAsync(reduceSuper));

        var evolving = host.FlowTypes.Evolving(context.NeverType);
        Check(host.FlowTypes.Evolving(context.NeverType) == evolving && await host.FlowTypes.FinalizeAsync(evolving) == host.AutoArray);
        var numberArray = host.FlowTypes.Evolving(context.NumberType);
        var finalized = await host.FlowTypes.FinalizeAsync(numberArray);
        Check(finalized == await host.FlowTypes.FinalizeAsync(numberArray) && finalized != host.AutoArray);
        Check(await host.FlowTypes.AssignmentReducedAsync(declared, context.NumberType) == context.NumberType);
        Check(await host.FlowTypes.AssignmentReducedAsync(declared, context.BooleanType) == declared);
        Check(await host.FlowTypes.AssignmentReducedAsync(declared, context.NeverType) == context.NeverType);
        Check(await host.FlowNarrowing.NarrowedAsync(declared, context.StringType, true) == context.StringType);
        Check(await host.FlowNarrowing.NarrowedAsync(declared, context.StringType, false) == context.NumberType);
        Check(await host.FlowNarrowing.TypeNameAsync(context.UnknownType, "string", true) == context.StringType);
        Check(host.FlowNarrowing.InlineLevel == 0 && host.ExplicitValues.ResolvingCount == 0);
        var thisName = new IdentifierNode { Text = "this" };
        var propertyName = new IdentifierNode { Text = "this" };
        var query = new TypeQueryNode { ExprName = new QualifiedNameNode { Left = thisName, Right = propertyName } };
        query.SetParents();
        Check(FlowReferences.ThisInQuery(thisName) && !FlowReferences.ThisInQuery(propertyName));
        Check(await host.FlowReferences.MatchesAsync(thisName, new KeywordExpressionNode(SyntaxKind.ThisKeyword)));

        var freshLinks = new CheckerLinks();
        var freshContext = new TypeContext(true, true);
        var freshScope = new CheckerEnvironment(freshContext, freshLinks);
        var freshSymbols = await CheckerSymbols.CreateAsync(program, freshLinks, freshScope);
        var assignments = new AssignmentMarks(freshLinks, freshSymbols, freshScope.ReferenceSymbols, freshScope.EntityNames);
        var x = freshSymbols.Declaration((SyntaxNode)((IFunctionSignature)function).Parameters![0])!;
        using (var cancellation = new CancellationTokenSource())
        {
            var nestedParameter = nodes.OfType<ParameterDeclarationNode>().Single(n => n.Name is IdentifierNode { Text: "z" });
            freshScope.BeforeValueResolution = () =>
            {
                assignments.GetAsync(freshSymbols.Declaration(nestedParameter)!).GetAwaiter().GetResult();
                cancellation.Cancel();
            };
            try
            {
                await assignments.GetAsync(x, cancellation.Token);
                throw new InvalidOperationException("Assignment scan cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        Check((freshLinks.Nodes.Get(function).Flags & NodeCheckFlags.AssignmentsMarked) == 0);
        var nestedFunction = nodes.OfType<ArrowFunctionNode>().Single(
            n => n.Parameters is { Count: > 0 } && n.Parameters[0] is ParameterDeclarationNode { Name: IdentifierNode { Text: "z" } });
        Check((freshLinks.Nodes.Get(nestedFunction).Flags & NodeCheckFlags.AssignmentsMarked) == 0);
        freshScope.BeforeValueResolution = null;
        var mark = await assignments.GetAsync(x);
        Check(mark.Definite && mark.LastPosition > 0 && mark.LastPosition != int.MaxValue);
        Check(await assignments.PastLastAsync(x, reference));
        var yDeclaration = nodes.OfType<ParameterDeclarationNode>().Single(n => n.Name is IdentifierNode { Text: "y" });
        var y = freshSymbols.Declaration(yDeclaration)!;
        Check((await assignments.GetAsync(y)).LastPosition == int.MaxValue && await assignments.DefiniteAsync(y));
        Check(!await assignments.PastLastAsync(y, reference));
        Check(host.FlowTypes.ActiveLoopCount == 0 && host.FlowTypes.SharedCount == 0);
        checks += await ConstructorAndCallSafety();
        Console.WriteLine(
            $"{checks} flow/cache/reachability/assignment/cancellation assertions; 20,000-node traversal and exact 2,000 recursion limit.");
    }

    private static async Task<int> ConstructorAndCallSafety()
    {
        const string source = """
            class A { value = 0; }
            class B { value = 0; }
            function narrow(value: A | B) {
                if (value.constructor === A) { const equal = value; }
                if (value.constructor !== A) { const unequal = value; }
            }
            declare const predicate: ((value: unknown) => value is string) | undefined;
            function optional(value: unknown) {
                if (predicate?.(value)) { const text: string = value; }
            }
            const assert = (value: unknown): asserts value => {};
            function assertion(value: unknown) { assert(value); }
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.ts"] = Wtf8.Encode(source),
            ["/project/globals.d.ts"] = Wtf8.Encode("interface Object { constructor: Function; } interface Function { prototype: any; }")
        }), "/project", new("/project/tsconfig.json", options, ["/project/main.ts", "/project/globals.d.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts")!.Syntax;
        await checker.CheckSourceFileAsync(file);
        var codes = checker.DiagnosticCodesForFile(file);
        if (!codes.SequenceEqual([DiagnosticCode.AssertionsRequireEveryNameInTheCallTargetToBeDeclaredWithAnExplicitTypeAnnotation]))
            throw new InvalidOperationException($"Constructor/call diagnostics: {string.Join(',', codes)}");
        var declarations = file.DescendantsAndSelf().OfType<VariableDeclarationNode>()
            .Where(n => n.Name is IdentifierNode).ToDictionary(n => ((IdentifierNode)n.Name!).Text);
        if ((await checker.GetExpressionTypeAsync(declarations["equal"].Initializer!)).Symbol?.Name != "A")
            throw new InvalidOperationException("Constructor identity did not select the matching class");
        if (await checker.GetExpressionTypeAsync(declarations["unequal"].Initializer!) is not UnionType { Types.Count: 2 })
            throw new InvalidOperationException("Constructor inequality narrowed a structural union");
        if (await checker.GetExpressionTypeAsync(declarations["text"].Initializer!) != checker.Context.StringType)
            throw new InvalidOperationException("Optional call lost its predicate");
        if (checker.AssertionRelatedDeclarations.Count != 1
            || checker.AssertionRelatedDeclarations.Values.Single().Single().Symbol.Name != "assert")
            throw new InvalidOperationException("Assertion diagnostic lost the unannotated declaration");
        return 5;
    }
}

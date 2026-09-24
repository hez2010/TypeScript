using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class CheckerExpressionTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Expression assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}enum E{A=1,B=A+2,C='x'}const a=3;let b=3;let c;const d=typeof 1;const e=void absent;const f=`x${'a'}`;";
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
        var enumNode = program.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<EnumDeclarationNode>().Single();
        var enumMembers = enumNode.Members!.OfType<EnumMemberNode>().ToArray();
        host.BeforeConstantReference = _ => throw new OperationCanceledException();
        try
        {
            await host.EnumValues.GetAsync(enumMembers[1]);
            throw new InvalidOperationException("Enum cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check((links.Nodes.Get(enumNode).Flags & NodeCheckFlags.EnumValuesComputed) == 0);
        host.BeforeConstantReference = null;
        Check((await host.EnumValues.GetAsync(enumMembers[0])).Value is 1d);
        Check((await host.EnumValues.GetAsync(enumMembers[1])).Value is 3d);
        Check((await host.EnumValues.GetAsync(enumMembers[2])).IsSyntacticallyString);
        Check((links.Nodes.Get(enumNode).Flags & NodeCheckFlags.EnumValuesComputed) != 0);
        var declarations = program.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<VariableDeclarationNode>().ToDictionary(n => ((IdentifierNode)n.Name!).Text);
        var constant = await host.Values.GetAsync(symbols.Declaration(declarations["a"])!);
        Check(constant is LiteralType { Value: 3d, IsFreshLiteral: true });
        Check(await host.Values.GetAsync(symbols.Declaration(declarations["b"])!) == context.NumberType);
        Check(await host.Values.GetAsync(symbols.Declaration(declarations["c"])!) == context.AutoType);
        Check(await host.Values.GetAsync(symbols.Declaration(declarations["d"])!) is UnionType { Types.Count: 8 });
        Check(await host.Values.GetAsync(symbols.Declaration(declarations["e"])!) == context.UndefinedType);
        Check(host.DeferredExpressions.Contains(declarations["e"].Initializer!));
        Check(await host.Values.GetAsync(symbols.Declaration(declarations["f"])!) is LiteralType { Value: "xa" });
        host.BeforeExpressionFinish = () => throw new InvalidOperationException("Expression finish failure");
        try
        {
            await host.Expressions.CheckAsync(new NumericLiteralNode { Text = "1" });
            throw new InvalidOperationException("Expression failure ignored");
        }
        catch (InvalidOperationException error) when (error.Message == "Expression finish failure")
        {
            checks++;
        }
        finally
        {
            host.BeforeExpressionFinish = null;
        }
        Check(host.Expressions.CurrentNode is null);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await host.EnumValues.GetAsync(enumMembers[0], cancelled.Token);
            throw new InvalidOperationException("Cached enum ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check((await host.EnumValues.GetAsync(enumMembers[1])).Value is 3d);
        var evaluator = new ConstantEvaluator((node, _, _) => ValueTask.FromResult(node is IdentifierNode { Text: "ext" }
            ? new ConstantResult(4d, false, true, true) : default));
        var binary = new BinaryExpressionNode
        {
            Left = new IdentifierNode { Text = "ext" },
            OperatorToken = new TokenNode(SyntaxKind.PlusToken),
            Right = new StringLiteralNode { Text = "x" }
        };
        var evaluated = await evaluator.EvaluateAsync(binary);
        Check(evaluated is { Value: "4x", IsSyntacticallyString: true, ResolvedOtherFiles: true, HasExternalReferences: true });
        var negative = new PrefixUnaryExpressionNode { Operator = SyntaxKind.MinusToken, Operand = new NumericLiteralNode { Text = "0" } };
        Check(BitConverter.DoubleToUInt64Bits((double)(await evaluator.EvaluateAsync(negative)).Value!) == 0x8000000000000000);
        var assertion = new AsExpressionNode
        {
            Expression = new NumericLiteralNode { Text = "1" },
            Type = new TokenNode(SyntaxKind.NumberKeyword)
        };
        Check((await evaluator.EvaluateAsync(assertion)).Value is null);
        var skipping = new ConstantEvaluator(
            (_, _, _) => ValueTask.FromResult(default(ConstantResult)),
            OuterExpressionKinds.TypeAssertions);
        Check((await skipping.EvaluateAsync(assertion)).Value is 1d);
        SyntaxNode deep = new NumericLiteralNode { Text = "1" };
        for (int i = 0; i < 20_000; i++)
            deep = new ParenthesizedExpressionNode { Expression = deep };
        Check((await evaluator.EvaluateAsync(deep)).Value is 1d);
        Check(await host.Expressions.CheckAsync(deep) is LiteralType { Value: 1d });
        Check(host.Expressions.CurrentNode is null);
        deep = new NumericLiteralNode { Text = "1" };
        for (int i = 0; i < 20_000; i++)
            deep = new PrefixUnaryExpressionNode { Operator = SyntaxKind.TildeToken, Operand = deep };
        Check((await evaluator.EvaluateAsync(deep)).Value is 1d);
        checks += await LiteralSafety();
        checks += await OrdinarySafety();
        checks += await ConditionSafety();
        checks += await JsxSafety();
        checks += await WithAndTemplateSafety();
        checks += await UnicodeLiteralSafety();
        checks += await RegularExpressionSafety();
        Console.WriteLine(
            $"{checks} expression/literal/context/enum/cache/cancellation assertions; 20000-level expression, constant and context traversal");
    }

    private static async Task<int> RegularExpressionSafety()
    {
        const string source = """
            /* 😀 */ const duplicate = /x/ggg;
            const unmatched = /)/u;
            const property = /\p{Script=Hiragan}/u;
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("target", "\"es2018\"");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.SourceFiles[0].Syntax;
        await checker.CheckSourceFileAsync(file);
        var diagnostics = checker.DetailedDiagnosticsForFile(file);
        var duplicates = diagnostics.Where(d => d.Code == 1500).ToArray();
        int flags = source.IndexOf("ggg", StringComparison.Ordinal);
        if (duplicates.Length != 2 || duplicates[0].Start != file.Source.ToBytePosition(flags + 1)
            || duplicates[1].Start != file.Source.ToBytePosition(flags + 2) || duplicates.Any(d => d.Length != 1))
            throw new InvalidOperationException("Regular-expression duplicate flags lost distinct byte ranges");
        if (!diagnostics.Any(
            d => d.RelatedInformation.Count != 0
                && d.RelatedInformation.All(r => r.Message.Category == TypeScript.Compiler.Diagnostics.DiagnosticCategory.Message)))
            throw new InvalidOperationException("Regular-expression spelling suggestion lost related information");
        foreach (var literal in file.DescendantsAndSelf().OfType<RegularExpressionLiteralNode>())
            await checker.GetExpressionTypeAsync(literal);
        if (checker.DetailedDiagnosticsForFile(file).Count != diagnostics.Count)
            throw new InvalidOperationException("Regular-expression checking emitted duplicate diagnostics");
        return 3;
    }

    private static async Task<int> UnicodeLiteralSafety()
    {
        const string source = "/* 😀😀😀😀😀😀😀😀 */ const large = 9007199254740993; const pair = (0, 1);";
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        await checker.CheckProgramAsync();
        if (!checker.DiagnosticCodesForFile(program.SourceFiles[0].Syntax).SequenceEqual([2695]))
            throw new InvalidOperationException("Unicode literal/comma diagnostics changed");
        if (!checker.Suggestions.Contains(80008))
            throw new InvalidOperationException("Unsafe integer suggestion was lost");
        return 2;
    }

    private static async Task<int> WithAndTemplateSafety()
    {
        const string source = """
            const text = `${(value: number) => value}`;
            const other = `${text}`;
            enum Values { value = ({ value: 'text' }).value }
            with (missing) { unknownInsideWith = 1; }
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts")!.Syntax;
        await checker.CheckSourceFileAsync(file);
        var codes = checker.DiagnosticCodesForFile(file);
        if (!codes.SequenceEqual([2304, 2410, 18033]))
            throw new InvalidOperationException($"With/template diagnostics: {string.Join(',', codes)}");
        foreach (var template in file.DescendantsAndSelf().OfType<TemplateExpressionNode>())
            if (await checker.GetExpressionTypeAsync(template) != checker.Context.StringType)
                throw new InvalidOperationException("Template substitution lost string result");
        return 3;
    }

    private static async Task<int> JsxSafety()
    {
        const string library = """
            interface Array<T> { length: number; [n: number]: T; }
            interface ReadonlyArray<T> { readonly length: number; readonly [n: number]: T; }
            type Partial<T> = { [P in keyof T]?: T[P] };
            declare namespace JSX {
                interface Element { value: unknown; }
                interface IntrinsicElements { box: { kind: 'number'; onValue?: (value: number) => void; children?: string }; }
                interface ElementAttributesProperty { props: {}; }
                interface ElementChildrenAttribute { children: {}; }
                type LibraryManagedAttributes<C, P> = C extends { defaults: true } ? Partial<P> : P;
            }
            declare module 'custom/jsx-runtime' {
                export namespace JSX {
                    interface Element { runtime: true; }
                    interface IntrinsicElements { span: { label: string }; }
                }
            }
            """;
        const string source = """
            declare function Choice(props: { kind: 'number'; onValue: (value: number) => void } | { kind: 'string'; onValue: (value: string) => void }): JSX.Element;
            declare function List<T>(props: { values: T[]; onValue: (value: T) => void }): JSX.Element;
            declare function Variant(props: { kind: 'number' } | { kind: 'string' }): JSX.Element;
            function renderVariant<K extends 'number' | 'string'>(kind: K) { return <Variant kind={kind} />; }
            declare class Component { static defaults: true; props: { required: number }; }
            <box kind='number' onValue={value => { const intrinsicValue: number = value; }}>text</box>;
            <Choice kind='number' onValue={value => { const chosen: number = value; }} />;
            <List values={[1]} onValue={value => { const inferred: number = value; }} />;
            <Component />;
            <box kind='other' />;
            <box kind='number' extra />;
            <box kind='number'>one{'two'}</box>;
            """;
        int checks = 0;
        foreach (bool automatic in new[] { false, true })
        {
            var options = new CompilerOptions();
            options.SetRaw("noLib", "true");
            options.SetRaw("strict", "true");
            options.SetRaw("target", "\"esnext\"");
            options.SetRaw("module", "\"preserve\"");
            options.SetRaw("jsx", automatic ? "\"react-jsx\"" : "\"preserve\"");
            if (automatic)
                options.SetRaw("jsxImportSource", "\"custom\"");
            var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
            {
                ["/project/main.tsx"] = Wtf8.Encode(automatic ? "<span label='text' />;" : source),
                ["/project/globals.d.ts"] = Wtf8.Encode(library)
            }), "/project", new("/project/tsconfig.json", options, ["/project/main.tsx", "/project/globals.d.ts"], [], [], []));
            var checker = await program.CreateCheckerAsync();
            var file = program.GetFile("/project/main.tsx")!.Syntax;
            var nodes = file.DescendantsAndSelf().ToArray();
            var parents = nodes.Select(n => n.Parent).ToArray();
            await checker.CheckSourceFileAsync(file);
            var codes = checker.DiagnosticCodesForFile(file);
            int[] expected = automatic ? [] : [2322, 2322, 2746];
            if (!codes.SequenceEqual(expected))
                throw new InvalidOperationException($"JSX diagnostics: {string.Join(',', codes)}");
            if (!nodes.Select(n => n.Parent).SequenceEqual(parents))
                throw new InvalidOperationException("JSX checking changed source parents");
            checks += 2;
            if (automatic)
            {
                var type = await checker.GetExpressionTypeAsync(nodes.OfType<JsxSelfClosingElementNode>().Single());
                if (await checker.Properties.PropertyAsync(type, "runtime") is null)
                    throw new InvalidOperationException("JSX runtime namespace was not selected");
                checks++;
            }
            else
                foreach (var variable in nodes.OfType<VariableDeclarationNode>().Where(
                    v => v.Name is IdentifierNode { Text: "intrinsicValue" or "chosen" or "inferred" }))
                {
                    if (await checker.GetExpressionTypeAsync(variable.Initializer!) != checker.Context.NumberType)
                        throw new InvalidOperationException("JSX callback parameter was not inferred as number");
                    checks++;
                }
        }
        if (Parser.ParseIsolatedEntityName("Element.createElement=") is not null
            || Parser.ParseIsolatedEntityName("React.createElement") is not QualifiedNameNode)
            throw new InvalidOperationException("JSX factory name parser accepted invalid syntax");
        return checks + 1;
    }

    private static async Task<int> ConditionSafety()
    {
        const string source = """
            interface Object { }
            interface Function { readonly name: string; }
            interface Array<T> { length: number; [n: number]: T; }
            interface I { method(): void; }
            interface SymbolConstructor { readonly hasInstance: unique symbol; }
            declare const Symbol: SymbolConstructor;
            declare const a: I, b: I;
            declare const callable: () => void;
            if (callable) { }
            if (callable) { callable(); }
            if (a.method) { b.method(); a.method(); }
            if (a.method) { b.method(); }
            function assertion(x: unknown) { if (((x as I)).method) { } }
            function presence(x: { a: string } | { b: number }) {
                if ('a' in x) { const text: string = x.a; }
                else { const number: number = x.b; }
            }
            function unknownPresence(x: unknown) { if (x && typeof x === 'object' && 'a' in x) { x.a; } }
            type Record<K extends keyof any, T> = { [P in K]: T };
            class C { #field = 1; test(x: C | string) { if (#field in x) { const c: C = x; } } }
            1 instanceof C;
            true in {};
            'a' in 1;
            declare const accept: { [Symbol.hasInstance](value: { value: string }): boolean };
            declare const invalid: { [Symbol.hasInstance](value: object): string };
            ({ value: 1 }) instanceof accept;
            ({ }) instanceof invalid;
            declare const predicate: { [Symbol.hasInstance](value: unknown): value is { value: string } };
            function customGuard(value: { value: string } | number) {
                if (value instanceof predicate) { const text: string = value.value; }
                else { const number: number = value; }
            }
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        options.SetRaw("target", "\"esnext\"");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.SourceFiles[0].Syntax;
        await checker.CheckSourceFileAsync(file);
        var codes = checker.DiagnosticCodesForFile(file).Order().ToArray();
        int[] expected = [2322, 2322, 2322, 2322, 2358, 2774, 2774, 2860, 2861];
        if (!codes.SequenceEqual(expected))
            throw new InvalidOperationException($"Condition diagnostics: {string.Join(',', codes)}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var condition = file.DescendantsAndSelf().OfType<IfStatementNode>().First();
            await checker.KnownTruthyAsync(checker.Context.AnyType, condition.Expression!, condition.ThenStatement, cancellation.Token);
            throw new InvalidOperationException("Condition cancellation ignored");
        }
        catch (OperationCanceledException) { }
        return 2;
    }

    private static async Task<int> OrdinarySafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Ordinary expression assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T} interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T} declare function f<T>(value:T):T; f<number>; f<number,string>; 1 as string; 2 as number;";
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        var program = await CompilerProgram.CreateAsync(
            new MemoryFileSystem(new Dictionary<string, byte[]> { ["/project/main.ts"] = Wtf8.Encode(source) }),
            "/project", new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scope = new CheckerEnvironment(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scope);
        var host = new Checker(context, links, scope);
        var allNodes = program.SourceFiles[0].Syntax.DescendantsAndSelf().ToArray();
        var instantiations = allNodes.OfType<ExpressionWithTypeArgumentsNode>().ToArray();
        var assertions = allNodes.OfType<AsExpressionNode>().ToArray();
        var function = await host.Values.GetAsync(symbols.Globals["f"]);
        var specialized = await host.InstantiationExpressions.GetAsync(function, instantiations[0]);
        Check(specialized is InstantiationExpressionType { Node: var node } && node == instantiations[0]);
        Check(
            await host.InstantiationExpressions.GetAsync(function, instantiations[0]) == specialized
                && host.InstantiationExpressions.CacheCount == 1);
        var signature = (await host.SignaturesAsync(specialized, false, default)).Single();
        Check(
            await host.Signatures.ReturnAsync(signature) == context.NumberType
                && await host.Parameters.AtAsync(signature, 0) == context.NumberType);
        host.BeforeInstantiationDiagnostic = () => throw new OperationCanceledException();
        try
        {
            await host.InstantiationExpressions.GetAsync(function, instantiations[1]);
            throw new InvalidOperationException("Instantiation diagnostic cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(host.InstantiationExpressions.CacheCount == 1 && host.InstantiationErrors.Count == 0);
        host.BeforeInstantiationDiagnostic = null;
        await host.InstantiationExpressions.GetAsync(function, instantiations[1]);
        Check(host.InstantiationErrors.Values.Single() == "<T>(value: T) => T" && host.Diagnostics.Contains(2635));
        try
        {
            await host.InstantiationExpressions.GetAsync(new TypeContext().NumberType, instantiations[0]);
            throw new InvalidOperationException("Foreign instantiation accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Check(await host.Assertions.CheckAsync(assertions[0]) == context.StringType && host.Assertions.OperandCount == 1);
        Check(!host.Diagnostics.Contains(2352));
        await host.Assertions.DeferredAsync(assertions[0]);
        Check(host.Diagnostics.Contains(2352));
        host.BeforeExpressionFinish = () => throw new OperationCanceledException();
        try
        {
            await host.Assertions.CheckAsync(assertions[1]);
            throw new InvalidOperationException("Assertion cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(host.Assertions.OperandCount == 1 && !host.DeferredExpressions.Contains(assertions[1]));
        host.BeforeExpressionFinish = null;
        Check(await host.Assertions.CheckAsync(assertions[1]) == context.NumberType && host.Assertions.OperandCount == 2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await host.InstantiationExpressions.GetAsync(function, instantiations[0], cancellation.Token);
            throw new InvalidOperationException("Cancelled cached instantiation accepted");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        return checks;
    }

    private static async Task<int> LiteralSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Literal assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}const left='left';const right='right';const object={[left]:1,[right]:2};const array=[1,2];function f({value=1,nested:{text='a'}}){}";
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
        var literal = nodes.OfType<ObjectLiteralExpressionNode>().Single();
        var rawObject = symbols.Binding(literal)!.Get(literal)!.Symbol!;
        int oldTables = host.LateMembers.CachedTableCount;
        using (var cancellation = new CancellationTokenSource())
        {
            host.BeforeExpressionFinish = () =>
            {
                if (host.Expressions.CurrentNode is IdentifierNode { Text: "right" })
                    cancellation.Cancel();
            };
            try
            {
                await host.LateMembers.TableAsync(rawObject, cancellation: cancellation.Token);
                throw new InvalidOperationException("Late member cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        host.BeforeExpressionFinish = null;
        Check(host.LateMembers.CachedTableCount == oldTables);
        Check(literal.Properties!.All(n => links.SymbolNodes.Get(n).ResolvedSymbol is null));
        Check(nodes.OfType<ComputedPropertyNameNode>().Last() is { } lastName && links.TypeNodes.Get(lastName).ResolvedType is null);
        var table = await host.LateMembers.TableAsync(rawObject);
        Check(table.ContainsKey("left") && table.ContainsKey("right"));
        Check((table["left"].CheckFlags & CheckFlags.Late) != 0 && table["left"].Parent == rawObject);
        Check(symbols.Binding(literal.Properties![0])!.Get(literal.Properties[0])!.Symbol!.Name == Symbol.InternalPrefix + "computed");
        Check(symbols.Declaration(literal.Properties[0]) == table["left"]);
        var array = nodes.OfType<ArrayLiteralExpressionNode>().Single();
        var inference = host.Inference.Create([context.NewTypeParameter()]);
        inference.Inferences[0].Candidates.Add(context.NumberType);
        inference.IntraExpressionSites.Add((array, context.NumberType));
        host.BeforeExpressionFinish = () =>
        {
            inference.Inferences[0].Candidates.Add(context.StringType);
            throw new InvalidOperationException("context-failure");
        };
        try
        {
            await host.Contexts.CheckWithAsync(array, host.AnyArray, inference);
            throw new InvalidOperationException("Context failure ignored");
        }
        catch (InvalidOperationException error) when (error.Message == "context-failure")
        {
            checks++;
        }
        host.BeforeExpressionFinish = null;
        Check(host.Contexts.ContextDepth == 0 && host.Contexts.InferenceDepth == 0 && host.Expressions.CurrentNode is null);
        Check(inference.Inferences[0].Candidates.SequenceEqual([context.NumberType]));
        Check(inference.IntraExpressionSites is [var site] && site.Node == array && site.Type == context.NumberType);
        Check(await host.Contexts.CheckWithAsync(array, host.AnyArray) is TypeReference);
        Check(host.Contexts.ContextDepth == 0 && host.Contexts.InferenceDepth == 0);
        var pattern = nodes.OfType<BindingPatternNode>().First();
        using (var cancellation = new CancellationTokenSource())
        {
            host.BeforeInitializer = _ => cancellation.Cancel();
            try
            {
                await host.BindingPatterns.GetAsync(pattern, true, cancellation: cancellation.Token);
                throw new InvalidOperationException("Binding pattern cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        host.BeforeInitializer = null;
        Check(host.BindingPatterns.ActiveCount == 0 && host.Contexts.ContextDepth == 0);
        var implied = await host.BindingPatterns.GetAsync(pattern, true);
        Check(host.InferencePatterns[implied] == pattern && host.BindingPatterns.ActiveCount == 0);
        var fresh = await host.ObjectLiterals.CheckAsync(literal);
        var regular = await host.ObjectLiterals.RegularAsync(fresh);
        Check(
            fresh != regular
                && (fresh.ObjectFlags & ObjectFlags.FreshLiteral) != 0
                && (regular.ObjectFlags & ObjectFlags.FreshLiteral) == 0);
        Check(await host.ObjectLiterals.RegularAsync(fresh) == regular);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await host.ObjectLiterals.ComputedAsync(nodes.OfType<ComputedPropertyNameNode>().First(), cancelled.Token);
            throw new InvalidOperationException("Cached computed name cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        try
        {
            await host.ObjectLiterals.RegularAsync(new TypeContext(true, true).EmptyObjectType);
            throw new InvalidOperationException("Foreign object accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        var leaf = new NumericLiteralNode { Text = "1" };
        SyntaxNode nested = leaf;
        for (int i = 0; i < 20_000; i++)
            nested = new ParenthesizedExpressionNode { Expression = nested };
        var declaration = new VariableDeclarationNode
        {
            Name = new IdentifierNode { Text = "deep" },
            Type = new TokenNode(SyntaxKind.NumberKeyword),
            Initializer = nested
        };
        declaration.SetParents();
        Check(await host.Contexts.GetAsync(leaf) == context.NumberType);
        Check(host.Contexts.ContextDepth == 0 && host.Contexts.InferenceDepth == 0 && host.Instantiation.Resolutions.Count == 0);
        return checks;
    }
}

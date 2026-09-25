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

internal static class CheckerAccessTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Access assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}declare function __access(value:unknown):void;interface I{a:number;b?:string;readonly r:number}function f(x:I){__access(x.a);__access(x.b);__access(x.r=1);__access(x['a']);}class B<T>{p:T}class D extends B<string>{}class C{readonly p:number;constructor(){this.p=1;}method():void{this.p=2;}}";
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        options.SetRaw("exactOptionalPropertyTypes", "true");
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
        var accesses = nodes.OfType<CallExpressionNode>().Where(
            c => c.Expression is IdentifierNode { Text: "__access" }).Select(c => c.Arguments![0]).ToArray();
        Check(await host.Expressions.CheckAsync(accesses[0]) == context.NumberType);
        var optional = await host.Expressions.CheckAsync(accesses[1]);
        Check(optional is UnionType union && union.Types.Contains(context.MissingType) && union.Types.Contains(context.StringType));
        Check(await host.Expressions.CheckAsync(accesses[2]) is LiteralType { Value: 1d } && host.Diagnostics.Contains(2540));
        Check(await host.Expressions.CheckAsync(accesses[3]) == context.NumberType);
        Check(
            links.SymbolNodes.Get(accesses[0]).ResolvedSymbol?.Name == "a"
                && links.SymbolNodes.Get(accesses[3]).ResolvedSymbol?.Name == "a");

        var derived = (InterfaceType)await host.Declared.GetAsync(symbols.Globals["D"]);
        using (var cancellation = new CancellationTokenSource())
        {
            scope.BeforeValueResolution = cancellation.Cancel;
            try
            {
                await host.ClassBases.ConstructorAsync(derived, cancellation.Token);
                throw new InvalidOperationException("Base constructor cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        scope.BeforeValueResolution = null;
        Check(derived.ResolvedBaseConstructorType is null && host.Instantiation.Resolutions.Count == 0);
        var constructor = await host.ClassBases.ConstructorAsync(derived);
        Check(constructor.Symbol == symbols.Globals["B"]);
        var baseTypes = await host.Bases.GetAsync(derived);
        Check(
            baseTypes.Count == 1
                && baseTypes[0] is TypeReference reference
                && (await host.References.TypeArgumentsAsync(reference))[0] == context.StringType);
        Check(await host.Bases.HasBaseAsync(derived, await host.Declared.GetAsync(symbols.Globals["B"])));
        var constructors = await host.ClassBases.DefaultsAsync(derived);
        Check(constructors.Count == 1 && await host.Signatures.ReturnAsync(constructors[0]) == derived);
        Check(await host.ClassBases.ConstructorAsync(derived) == constructor);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await host.ClassBases.ConstructorAsync(derived, cancelled.Token);
            throw new InvalidOperationException("Cached base ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }

        var field = nodes.OfType<PropertyDeclarationNode>().Single(
            p => p.Name is IdentifierNode { Text: "p" } && p.Parent is ClassDeclarationNode { Name.Text: "C" });
        var property = symbols.Declaration(field)!;
        var targets = nodes.OfType<PropertyAccessExpressionNode>().Where(p => p.Expression?.Kind == SyntaxKind.ThisKeyword).ToArray();
        Check(!host.MemberAccess.ReadonlyAssignment(targets[0], property, 1));
        Check(host.MemberAccess.ReadonlyAssignment(targets[1], property, 1));
        Check(!host.MemberAccess.ReadonlyAssignment(targets[1], property, 0));

        var receiver = new IdentifierNode { Text = "x" };
        var inner = new PropertyAccessExpressionNode
        {
            Expression = receiver,
            Name = new IdentifierNode { Text = "a" },
            QuestionDotToken = new TokenNode(SyntaxKind.QuestionDotToken),
            Flags = NodeFlags.OptionalChain
        };
        var outer = new PropertyAccessExpressionNode
        {
            Expression = inner,
            Name = new IdentifierNode { Text = "b" },
            Flags = NodeFlags.OptionalChain
        };
        outer.SetParents();
        var nullable = await host.Algebra.UnionAsync([context.StringType, context.NullType, context.UndefinedType]);
        Check(await host.Optional.ReceiverAsync(nullable, receiver) == context.StringType);
        var marked = await host.Optional.PropagateAsync(context.NumberType, inner, true);
        Check(
            marked is UnionType markedUnion
                && markedUnion.Types.Contains(context.OptionalType)
                && !markedUnion.Types.Contains(context.UndefinedType));
        Check(await host.Optional.ReceiverAsync(marked, inner) == context.NumberType);
        var final = await host.Optional.PropagateAsync(context.NumberType, outer, true);
        Check(
            final is UnionType finalUnion
                && finalUnion.Types.Contains(context.UndefinedType)
                && !finalUnion.Types.Contains(context.OptionalType));
        Check(await host.Optional.PropagateAsync(context.NumberType, outer, false) == context.NumberType);
        try
        {
            await host.Optional.PropagateAsync(marked, inner, true, cancelled.Token);
            throw new InvalidOperationException("Optional propagation ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(host.FlowTypes.ActiveLoopCount == 0 && host.FlowTypes.SharedCount == 0);

        Check(GoUnicode.Lower(0x130) == 'i');
        Check(GoUnicode.EqualFold(GoUnicode.Runes("K"), GoUnicode.Runes("K")));
        Check(GoUnicode.Runes("\ud800").SequenceEqual([0xfffd, 0xfffd, 0xfffd]));
        Check(
            await SpellingSuggestions.FindAsync(
                "abcde",
                new[] { "abcdf", "abcda" },
                s => ValueTask.FromResult<string?>(s),
                StringComparer.Ordinal.Compare) == "abcda");
        Check(
            await SpellingSuggestions.FindAsync(
                "ixx",
                new[] { "İxx" },
                s => ValueTask.FromResult<string?>(s),
                StringComparer.Ordinal.Compare) == "İxx");
        Check(
            await SpellingSuggestions.FindAsync(
                "go",
                new[] { "gz" },
                s => ValueTask.FromResult<string?>(s),
                StringComparer.Ordinal.Compare) is null);
        Check(
            await SpellingSuggestions.FindAsync(
                "abcde",
                new[] { "abcdf", "abcda" },
                s => ValueTask.FromResult<string?>(s),
                StringComparer.Ordinal.Compare,
                1) is null);
        try
        {
            await SpellingSuggestions.FindAsync(
                "name",
                new[] { "Name" },
                s => ValueTask.FromResult<string?>(s),
                StringComparer.Ordinal.Compare,
                cancellation: cancelled.Token);
            throw new InvalidOperationException("Spelling search ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }

        SyntaxNode deep = new IdentifierNode { Text = "deep" };
        var start = deep;
        for (int i = 0; i < 20_000; i++)
            deep = new ParenthesizedExpressionNode { Expression = deep };
        var call = new CallExpressionNode { Expression = deep };
        call.SetParents();
        Check(AccessExpressions.MethodCall(start));
        Check(MemberAccessRules.SkipParentheses(deep) == start);
        var current = new PropertyAccessExpressionNode { Expression = start, Name = new IdentifierNode { Text = "p" } };
        SyntaxNode wrapped = current;
        for (int i = 0; i < 20_000; i++)
            wrapped = new ParenthesizedExpressionNode { Expression = wrapped };
        var deletion = new DeleteExpressionNode { Expression = wrapped };
        deletion.SetParents();
        Check(AccessExpressions.DeleteTarget(current));
        checks += await ClassSafety();
        checks += await JavaScriptPropertySafety();
        checks += await DeclarationGrammarSafety();
        checks += await PropertyDiagnosticSafety();
        Console.WriteLine($"{checks} access/optional/member/class/cancellation/spelling assertions; 20,000-level traversal.");
    }

    private static async Task<int> PropertyDiagnosticSafety()
    {
        string source = "\n" + """
            interface Array<T> { length: number; [n: number]: T; }
            interface String { length: number; }
            interface Promise<T> { then(onfulfilled: (value: T) => any): any; }
            declare const object: { length: number };
            object.unknown;
            object.lenght;
            type Union = { left: number } | { right: string };
            declare const union: Union;
            union.left;
            union.other;
            class Static { static value = 1; }
            declare const instance: Static;
            instance.value;
            declare const promised: Promise<{ value: number }>;
            promised.value;
            declare const text: string;
            text.includes('x');
            interface HTMLButtonElement {}
            declare const element: HTMLButtonElement;
            element.click;
            type Never = { kind: 'a' } & { kind: 'b' };
            declare const never: Never;
            never.value;
            object.\u0075nknown;
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        // Complete pinned-reference records, including union and suggestion explanations.
        const string reference = """
            [{"arguments":["unknown","{ length: number; }"],"category":1,"chain":[],"code":2339,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_2339","length":7,"related":[],"start":210},{"arguments":["lenght","{ length: number; }","length"],"category":1,"chain":[],"code":2551,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_Did_you_mean_2_2551","length":6,"related":[{"arguments":["length"],"category":3,"chain":[],"code":2728,"file":"/project/main.ts","key":"_0_is_declared_here_2728","length":6,"related":[],"start":185}],"start":226},{"arguments":["left","Union"],"category":1,"chain":[{"arguments":["left","{ right: string; }"],"category":1,"chain":[],"code":2339,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_2339","length":4,"related":[],"start":319}],"code":2339,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_2339","length":4,"related":[],"start":319},{"arguments":["other","Union"],"category":1,"chain":[{"arguments":["other","{ left: number; }"],"category":1,"chain":[],"code":2339,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_2339","length":5,"related":[],"start":331}],"code":2339,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_2339","length":5,"related":[],"start":331},{"arguments":["value","Static","Static.value"],"category":1,"chain":[],"code":2576,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_Did_you_mean_to_access_the_static_member_2_instead_2576","length":5,"related":[],"start":414},{"arguments":["value","Promise<{ value: number; }>"],"category":1,"chain":[],"code":2339,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_2339","length":5,"related":[{"arguments":[],"category":1,"chain":[],"code":2773,"file":"/project/main.ts","key":"Did_you_forget_to_use_await_2773","length":5,"related":[],"start":482}],"start":482},{"arguments":["includes","string","es2015"],"category":1,"chain":[],"code":2550,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_Do_you_need_to_change_your_target_library_Try_changing_the_lib_c_2550","length":8,"related":[],"start":522},{"arguments":["click","HTMLButtonElement"],"category":1,"chain":[],"code":2812,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_Try_changing_the_lib_compiler_option_to_include_dom_2812","length":5,"related":[],"start":618},{"arguments":["value","never"],"category":1,"chain":[{"arguments":["Never","kind"],"category":1,"chain":[],"code":18031,"file":"/project/main.ts","key":"The_intersection_0_was_reduced_to_never_because_property_1_has_conflicting_types_in_some_constituent_18031","length":5,"related":[],"start":703}],"code":2339,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_2339","length":5,"related":[],"start":703},{"arguments":["\\u0075nknown","{ length: number; }"],"category":1,"chain":[],"code":2339,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_2339","length":12,"related":[],"start":717}]
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        options.SetRaw("noErrorTruncation", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        await checker.CheckProgramAsync();
        var file = program.GetFile("/project/main.ts")!.Syntax;
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
            CheckerCorpusTests.WriteDiagnostics(writer, checker.DetailedDiagnosticsForFile(file).OrderBy(d => d.Start));
        using var actual = System.Text.Json.JsonDocument.Parse(stream.ToArray());
        using var expected = System.Text.Json.JsonDocument.Parse(reference);
        if (actual.RootElement.GetArrayLength() != expected.RootElement.GetArrayLength())
            throw new InvalidOperationException("Property diagnostic count");
        for (int i = 0; i < actual.RootElement.GetArrayLength(); i++)
            if (!System.Text.Json.JsonElement.DeepEquals(actual.RootElement[i], expected.RootElement[i]))
                throw new InvalidOperationException($"Property diagnostic {i}: {actual.RootElement[i].GetRawText()}");
        return actual.RootElement.GetArrayLength();
    }

    private static async Task<int> DeclarationGrammarSafety()
    {
        const string source = """
            namespace Ns { export interface Base {} }
            class Derived extends Ns.Base {}
            private function top() {}
            interface T { private f(): void; }
            const value = { public f() {} };
            async enum E {}
            accessor interface I {}
            function nested() { export const x = 1; }
            declare namespace Outer { declare const v: number; }
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.SourceFiles[0].Syntax;
        await checker.CheckSourceFileAsync(file);
        var codes = checker.DiagnosticCodesForFile(file);
        if (!codes.SequenceEqual([1038, 1042, 1042, 1044, 1070, 1184, 1184, 1275, 2689]))
            throw new InvalidOperationException($"Declaration/heritage diagnostics: {string.Join(',', codes)}");
        return 1;
    }

    private static async Task<int> JavaScriptPropertySafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"JavaScript property assertion {checks + 1}");
            checks++;
        }
        const string source = """
            class C {
                /** @param {boolean} flag */
                constructor(flag) { this.value = 1; if (flag) this.partial = 'text'; }
                method() { this.methodOnly = true; }
            }
            const object = {};
            Object.defineProperty(object, 'fixed', { value: 1, writable: false });
            Object.defineProperty(object, 'accessed', { get() { return 'text'; } });
            const callback = () => {};
            callback.value = 1;
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        options.SetRaw("allowJs", "true");
        options.SetRaw("checkJs", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.js"] = Wtf8.Encode(source)
        }), "/project", new("/project/tsconfig.json", options, ["/project/main.js"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.SourceFiles[0].Syntax;
        var nodes = file.DescendantsAndSelf().ToArray();
        var parents = nodes.Select(n => n.Parent).ToArray();
        var declaration = nodes.OfType<ClassDeclarationNode>().Single();
        var type = await checker.Declared.GetAsync(checker.Symbols.Declaration(declaration)!);
        var value = (await checker.Properties.PropertyAsync(type, "value"))!;
        checker.BeforeFlowExpression = _ => throw new OperationCanceledException();
        try
        {
            await checker.Values.GetAsync(value);
            throw new InvalidOperationException("JavaScript constructor inference cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        checker.BeforeFlowExpression = null;
        Check(
            checker.Links.Values.Get(value).ResolvedType is null
                && checker.Instantiation.Resolutions.Count == 0
                && checker.FlowTypes.ActiveLoopCount == 0);
        Check(await checker.Values.GetAsync(value) == checker.Context.NumberType);
        var partial = await checker.Values.GetAsync((await checker.Properties.PropertyAsync(type, "partial"))!);
        Check(
            partial is UnionType partialUnion
                && partialUnion.Types.Contains(checker.Context.StringType)
                && partialUnion.Types.Contains(checker.Context.UndefinedType));
        var methodOnly = await checker.Values.GetAsync((await checker.Properties.PropertyAsync(type, "methodOnly"))!);
        Check(
            checker.Predicates.Maybe(methodOnly, TypeFlags.BooleanLike, default)
                && checker.Predicates.Maybe(methodOnly, TypeFlags.Undefined, default));
        var definitions = nodes.OfType<CallExpressionNode>().Where(
            c => c.Expression is PropertyAccessExpressionNode { Name: IdentifierNode { Text: "defineProperty" } }).ToArray();
        var fixedProperty = checker.Symbols.Declaration(definitions[0])!;
        Check(await checker.Values.GetAsync(fixedProperty) == checker.Context.NumberType && checker.IsReadonly(fixedProperty));
        var accessed = checker.Symbols.Declaration(definitions[1])!;
        Check(await checker.Values.GetAsync(accessed) == checker.Context.StringType && checker.IsReadonly(accessed));
        var callback = nodes.OfType<BinaryExpressionNode>().Single(
            n => n.Left is PropertyAccessExpressionNode { Expression: IdentifierNode { Text: "callback" } });
        Check(await checker.Values.GetAsync(checker.Symbols.Declaration(callback)!) == checker.Context.NumberType);
        Check(nodes.Select(n => n.Parent).SequenceEqual(parents));
        return checks;
    }

    private static async Task<int> ClassSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Class assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}class C{value;#text;partial:number;constructor(flag:boolean){this.value=1;this.#text='text';if(flag)this.partial=1;}static value;static{this.value=true;}}";
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        var program = await CompilerProgram.CreateAsync(
            new MemoryFileSystem(new Dictionary<string, byte[]> { ["/project/main.ts"] = Wtf8.Encode(source) }),
            "/project", new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var nodes = program.SourceFiles[0].Syntax.DescendantsAndSelf().ToArray();
        var parents = nodes.Select(n => n.Parent).ToArray();
        var properties = nodes.OfType<PropertyDeclarationNode>().ToArray();
        var constructor = nodes.OfType<ConstructorDeclarationNode>().Single();
        var symbol = checker.Symbols.Declaration(properties[0])!;
        checker.BeforeFlowExpression = _ => throw new OperationCanceledException();
        try
        {
            await checker.Values.GetAsync(symbol);
            throw new InvalidOperationException("Constructor inference cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        checker.BeforeFlowExpression = null;
        Check(
            checker.Links.Values.Get(symbol).ResolvedType is null
                && checker.Instantiation.Resolutions.Count == 0
                && checker.FlowTypes.ActiveLoopCount == 0);
        Check(await checker.Values.GetAsync(symbol) == checker.Context.NumberType);
        Check(await checker.Values.GetAsync(checker.Symbols.Declaration(properties[1])!) == checker.Context.StringType);
        Check(await checker.Values.GetAsync(checker.Symbols.Declaration(properties[3])!) == checker.Context.BooleanType);
        Check(await checker.PropertyInitializers.AssignedAsync(properties[0].Name!, checker.Context.NumberType, constructor));
        Check(!await checker.PropertyInitializers.AssignedAsync(properties[2].Name!, checker.Context.NumberType, constructor));
        await checker.CheckSourceFileAsync(program.SourceFiles[0].Syntax);
        Check(checker.Diagnostics.Contains(2564) && !checker.Diagnostics.Contains(7008));
        Check(nodes.Select(n => n.Parent).SequenceEqual(parents));
        Check(checker.CheckedFileCount == 1 && checker.CurrentSourceNode is null && checker.Instantiation.Resolutions.Count == 0);
        return checks;
    }
}

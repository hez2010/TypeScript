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

internal static class CheckerSignatureTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Signature assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T} interface ReadonlyArray<T>{readonly length:number;readonly [n:number]:T} type A=(x:string,y:void)=>number; type R=(...args:[first:string,second?:number])=>void; type V=(head:string,...args:[first:number,...rest:boolean[]])=>void; type C=<T=string>(x:T)=>T; type D=<U=number>(x:U)=>U; type L=(this:boolean,x:string,y:number)=>void; type M=(this:number,x:number,y:string)=>void; type X=(...args:[string,number])=>void; type Y=(...args:[number,string])=>void; type Z=(...args:[boolean,boolean])=>void; function f(x:string='',y?:number):void {} interface Base{(x:string):string} interface One extends Base{} interface Two extends Base{}";
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
        async Task<Signature> Get(string name)
        {
            var type = (ObjectType)await host.Declared.GetAsync(symbols.Globals[name]);
            await host.Members.ResolveAsync(type);
            return type.CallSignatures.Single();
        }
        var a = await Get("A");
        Check(await host.Parameters.CountAsync(a) == 2 && a.ResolvedMinArgumentCount == -1);
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Parameters.MinimumAsync(a);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(a.ResolvedMinArgumentCount == -1 && host.Instantiation.Resolutions.Count == 0);
        host.BeforeNode = null;
        Check(await host.Parameters.MinimumAsync(a) == 1 && a.ResolvedMinArgumentCount == 1);
        Check(await host.Parameters.MinimumAsync(a, voidIsRequired: true) == 2 && a.ResolvedMinArgumentCount == 1);
        var f = (await host.Signatures.OfSymbolAsync(symbols.Globals["f"])).Single();
        Check(await host.Values.GetAsync(f.Parameters[0]) == context.StringType);
        Check(
            await host.Parameters.ParameterAsync(f.Parameters[0]) is UnionType optional && optional.Types.Contains(context.UndefinedType));
        Check(await host.Values.GetAsync(f.Parameters[0]) == context.StringType);

        var r = await Get("R");
        Check(
            await host.Parameters.CountAsync(r) == 2
                && await host.Parameters.MinimumAsync(r) == 1
                && !await host.Parameters.HasRestAsync(r));
        Check(await host.Parameters.TryAtAsync(r, 2) is null && await host.Parameters.AtAsync(r, 2) == context.AnyType);
        Check(await host.Parameters.NameAsync(r, 0) == "first" && await host.Parameters.NameAsync(r, 1) == "second");
        var sliced = (TypeReference)await host.Parameters.RestAtAsync(r, 1, true);
        Check(sliced.Target is TupleType { IsReadonly: true, FixedLength: 1 } && await host.Parameters.EffectiveRestAsync(r) is null);
        var v = await Get("V");
        Check(
            await host.Parameters.CountAsync(v) == 3
                && await host.Parameters.MinimumAsync(v) == 2
                && await host.Parameters.HasRestAsync(v));
        Check(await host.Parameters.AtAsync(v, 5) == context.BooleanType);
        Check(await host.Parameters.NameAsync(v, 2) == "rest");
        Check(await host.Parameters.EffectiveRestAsync(v) is TypeReference array && host.Instantiation.IsArrayType(array));

        var c = await Get("C");
        var d = await Get("D");
        Check(await host.SignatureComparison.CompareAsync(c, d) == Ternary.False);
        Check(await host.SignatureComparison.TypeParametersAsync(c.TypeParameters, d.TypeParameters));
        var combined = (await host.SignatureComposition.UnionAsync([[c], [d]])).Single();
        Check(combined.TypeParameters[0] == c.TypeParameters[0] && combined.Composite is { IsUnion: true, Signatures.Count: 2 });
        Check(await host.Signatures.ReturnAsync(combined) == c.TypeParameters[0]);

        var left = await Get("L");
        var right = await Get("M");
        foreach (var signature in new[] { left, right })
        {
            foreach (var parameter in signature.Parameters)
                await host.Values.GetAsync(parameter);
            await host.Parameters.MinimumAsync(signature);
            links.Values.Get(signature.ThisParameter!);
        }
        int count = links.Values.Count;
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.SignatureComposition.CombineAsync(left, right, true);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(links.Values.Count == count && host.Instantiation.Resolutions.Count == 0);
        host.BeforeNode = null;
        var joined = await host.SignatureComposition.CombineAsync(left, right, true);
        Check(joined.Parameters.Count == 2 && await host.Values.GetAsync(joined.Parameters[0]) == context.NeverType);
        Check(await host.Values.GetAsync(joined.ThisParameter!) == context.NeverType);
        host.CompareTypes = (_, _, _, _) => ValueTask.FromResult(Ternary.Maybe);
        Check(await host.SignatureComparison.CompareAsync(left, right) == Ternary.Maybe);
        host.CompareTypes = null;

        var inputs = new[] { await Get("X"), await Get("Y"), await Get("Z") };
        foreach (var signature in inputs)
        {
            await host.Parameters.MinimumAsync(signature);
            for (int i = 0; i < 2; i++)
                await host.Parameters.AtAsync(signature, i);
        }
        count = links.Values.Count;
        host.CompareTypes = (_, _, _, _) => ValueTask.FromResult(Ternary.False);
        host.BeforeSignatureIndex = (_, _) =>
        {
            if (links.Values.Count > count)
                throw new OperationCanceledException();
        };
        try
        {
            await host.SignatureComposition.UnionAsync(inputs.Select(s => (IReadOnlyList<Signature>)new[] { s }).ToArray());
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(links.Values.Count == count && host.Instantiation.Resolutions.Count == 0);
        host.CompareTypes = null;
        host.BeforeSignatureIndex = null;
        Check(
            (await host.SignatureComposition.UnionAsync(
                inputs.Select(s => (IReadOnlyList<Signature>)new[] { s }).ToArray())).Single().Composite?.Signatures.Count == 3);

        var one = (ObjectType)await host.Declared.GetAsync(symbols.Globals["One"]);
        var two = (ObjectType)await host.Declared.GetAsync(symbols.Globals["Two"]);
        await host.Members.ResolveAsync(one);
        await host.Members.ResolveAsync(two);
        Check(ReferenceEquals(one.CallSignatures, two.CallSignatures));
        var parameterSymbol = new Symbol(SymbolFlags.FunctionScopedVariable | SymbolFlags.Transient, "x");
        links.Values.Get(parameterSymbol).ResolvedType = context.NumberType;
        var js = context.NewSignature(
            SignatureFlags.IsUntypedSignatureInJSFile,
            null,
            [],
            null,
            [parameterSymbol],
            context.VoidType,
            null,
            1);
        Check(await host.Parameters.MinimumAsync(js) == 0 && js.ResolvedMinArgumentCount == -1);
        Check(await host.Parameters.MinimumAsync(js, strongUntypedJs: true) == 1 && js.ResolvedMinArgumentCount == 1);
        Check(await host.Parameters.MinimumAsync(js, voidIsRequired: true) == 0);
        var restAny = new Symbol(SymbolFlags.FunctionScopedVariable | SymbolFlags.Transient, "args");
        links.Values.Get(restAny).ResolvedType = context.AnyType;
        var anySignature = context.NewSignature(SignatureFlags.HasRestParameter, null, [], null, [restAny], context.VoidType, null, 0);
        Check(await host.Parameters.EffectiveRestAsync(anySignature) == host.AnyArray);
        Check(await host.Parameters.RestOrAnyAsync(anySignature, 0) == context.AnyType);
        Check(await host.Parameters.RestAtAsync(anySignature, 3) == host.AnyArray);

        SyntaxNode name = new IdentifierNode { Text = "tail" };
        for (int i = 0; i < 20_000; i++)
            name = new BindingPatternNode(SyntaxKind.ArrayBindingPattern)
            {
                Elements = new(
                [new BindingElementNode { DotDotDotToken = new TokenNode(SyntaxKind.DotDotDotToken), Name = name }])
            };
        var declaration = new ParameterDeclarationNode { DotDotDotToken = new TokenNode(SyntaxKind.DotDotDotToken), Name = name };
        var restSymbol = new Symbol(SymbolFlags.FunctionScopedVariable, "args") { ValueDeclaration = declaration };
        Check(SignatureParameters.Label(new(ElementFlags.Rest), restSymbol, 0) == "tail");
        try
        {
            await host.SignatureComparison.CompareAsync(new TypeContext().NewSignature(0, null, [], null, [], null, null, 0), a);
            throw new InvalidOperationException("Foreign signature accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        var foreign = new TypeContext();
        var foreignSignature = foreign.NewSignature(0, null, [], null, [], null, null, 0);
        try
        {
            await host.SignatureComposition.UnionAsync([[foreignSignature]]);
            throw new InvalidOperationException("Foreign signature accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        var foreignParameter = foreign.NewTypeParameter();
        try
        {
            await host.SignatureComparison.TypeParametersAsync([foreignParameter], [foreignParameter]);
            throw new InvalidOperationException("Foreign parameter accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        checks += await FunctionSafety();
        checks += await CallSafety();
        checks += await IterationSafety();
        checks += await DocumentationSafety();
        checks += await DecoratorSafety();
        checks += await VarianceSafety();
        checks += await PredicateSafety();
        checks += await SignatureDiagnosticSafety();
        checks += await CallArityDiagnosticSafety();
        checks += await OverloadDiagnosticSafety();
        Console.WriteLine(
            $"{checks} signature/function/call/iteration/inference/context/cancellation assertions; binding and return traversal depth 20000");
    }

    private static async Task<int> OverloadDiagnosticSafety()
    {
        string source = "\n" + """
            interface Array<T> { length: number; [n: number]: T; }
            function pick(value: number): number;
            function pick(value: string): string;
            function pick(value: unknown): unknown { return value; }
            pick(true);
            declare function external(value: { a: number }): void;
            declare function external(value: { b: string }): void;
            external({ a: 1, b: 2 });
            function only(value: string): void;
            function only(value: any): void {}
            only(123);
            declare function nested(value: { data: { name: string } }): void;
            declare function nested(value: { data: { name: number } }): void;
            declare const data: { data: { name: boolean } };
            nested(data);
            function generic<T extends string>(value: T): T;
            function generic<T extends number>(value: T): T;
            function generic<T>(value: T): T { return value; }
            generic(true);
            declare function primitive(value: string): void;
            primitive(123);
            declare function receiver(this: { a: number }, value: string): void;
            declare function receiver(this: { a: string }, value: number): void;
            receiver(123);
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        // Pinned-reference argument errors, overload wrappers and related declarations.
        const string reference = """
            [{"arguments":[],"category":1,"chain":[{"arguments":[],"category":1,"chain":[{"arguments":["boolean","string"],"category":1,"chain":[],"code":2345,"file":"/project/main.ts","key":"Argument_of_type_0_is_not_assignable_to_parameter_of_type_1_2345","length":4,"related":[],"start":194}],"code":2770,"file":"/project/main.ts","key":"The_last_overload_gave_the_following_error_2770","length":4,"related":[],"start":194}],"code":2769,"file":"/project/main.ts","key":"No_overload_matches_this_call_2769","length":4,"related":[{"arguments":[],"category":1,"chain":[],"code":2771,"file":"/project/main.ts","key":"The_last_overload_is_declared_here_2771","length":4,"related":[],"start":103},{"arguments":[],"category":1,"chain":[],"code":2793,"file":"/project/main.ts","key":"The_call_would_have_succeeded_against_this_implementation_but_implementation_signatures_of_overloads_2793","length":4,"related":[],"start":141}],"start":194},{"arguments":[],"category":1,"chain":[{"arguments":[],"category":1,"chain":[{"arguments":["number","string"],"category":1,"chain":[],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":1,"related":[{"arguments":["b","{ b: string; }"],"category":3,"chain":[],"code":6500,"file":"/project/main.ts","key":"The_expected_type_comes_from_property_0_which_is_declared_here_on_type_1_6500","length":1,"related":[],"start":291}],"start":328}],"code":2770,"file":"/project/main.ts","key":"The_last_overload_gave_the_following_error_2770","length":1,"related":[{"arguments":["b","{ b: string; }"],"category":3,"chain":[],"code":6500,"file":"/project/main.ts","key":"The_expected_type_comes_from_property_0_which_is_declared_here_on_type_1_6500","length":1,"related":[],"start":291}],"start":328}],"code":2769,"file":"/project/main.ts","key":"No_overload_matches_this_call_2769","length":1,"related":[{"arguments":["b","{ b: string; }"],"category":3,"chain":[],"code":6500,"file":"/project/main.ts","key":"The_expected_type_comes_from_property_0_which_is_declared_here_on_type_1_6500","length":1,"related":[],"start":291},{"arguments":[],"category":1,"chain":[],"code":2771,"file":"/project/main.ts","key":"The_last_overload_is_declared_here_2771","length":8,"related":[],"start":273}],"start":328},{"arguments":["number","string"],"category":1,"chain":[],"code":2345,"file":"/project/main.ts","key":"Argument_of_type_0_is_not_assignable_to_parameter_of_type_1_2345","length":3,"related":[{"arguments":[],"category":1,"chain":[],"code":2793,"file":"/project/main.ts","key":"The_call_would_have_succeeded_against_this_implementation_but_implementation_signatures_of_overloads_2793","length":4,"related":[],"start":382}],"start":413},{"arguments":[],"category":1,"chain":[{"arguments":[],"category":1,"chain":[{"arguments":["{ data: { name: boolean; }; }","{ data: { name: number; }; }"],"category":1,"chain":[{"arguments":["data.name"],"category":1,"chain":[{"arguments":["boolean","number"],"category":1,"chain":[],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":4,"related":[],"start":607}],"code":2200,"file":"/project/main.ts","key":"The_types_of_0_are_incompatible_between_these_types_2200","length":4,"related":[],"start":607}],"code":2345,"file":"/project/main.ts","key":"Argument_of_type_0_is_not_assignable_to_parameter_of_type_1_2345","length":4,"related":[],"start":607}],"code":2770,"file":"/project/main.ts","key":"The_last_overload_gave_the_following_error_2770","length":4,"related":[],"start":607}],"code":2769,"file":"/project/main.ts","key":"No_overload_matches_this_call_2769","length":4,"related":[{"arguments":[],"category":1,"chain":[],"code":2771,"file":"/project/main.ts","key":"The_last_overload_is_declared_here_2771","length":6,"related":[],"start":502}],"start":607},{"arguments":[],"category":1,"chain":[{"arguments":[],"category":1,"chain":[{"arguments":["boolean","number"],"category":1,"chain":[],"code":2345,"file":"/project/main.ts","key":"Argument_of_type_0_is_not_assignable_to_parameter_of_type_1_2345","length":4,"related":[],"start":771}],"code":2770,"file":"/project/main.ts","key":"The_last_overload_gave_the_following_error_2770","length":4,"related":[],"start":771}],"code":2769,"file":"/project/main.ts","key":"No_overload_matches_this_call_2769","length":4,"related":[{"arguments":[],"category":1,"chain":[],"code":2771,"file":"/project/main.ts","key":"The_last_overload_is_declared_here_2771","length":7,"related":[],"start":672},{"arguments":[],"category":1,"chain":[],"code":2793,"file":"/project/main.ts","key":"The_call_would_have_succeeded_against_this_implementation_but_implementation_signatures_of_overloads_2793","length":7,"related":[],"start":721}],"start":771},{"arguments":["number","string"],"category":1,"chain":[],"code":2345,"file":"/project/main.ts","key":"Argument_of_type_0_is_not_assignable_to_parameter_of_type_1_2345","length":3,"related":[],"start":837},{"arguments":[],"category":1,"chain":[{"arguments":[],"category":1,"chain":[{"arguments":["void","{ a: string; }"],"category":1,"chain":[],"code":2684,"file":"/project/main.ts","key":"The_this_context_of_type_0_is_not_assignable_to_method_s_this_of_type_1_2684","length":13,"related":[],"start":981}],"code":2770,"file":"/project/main.ts","key":"The_last_overload_gave_the_following_error_2770","length":13,"related":[],"start":981}],"code":2769,"file":"/project/main.ts","key":"No_overload_matches_this_call_2769","length":13,"related":[{"arguments":[],"category":1,"chain":[],"code":2771,"file":"/project/main.ts","key":"The_last_overload_is_declared_here_2771","length":8,"related":[],"start":929}],"start":981}]
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
            throw new InvalidOperationException("Overload diagnostic count");
        for (int i = 0; i < actual.RootElement.GetArrayLength(); i++)
            if (!System.Text.Json.JsonElement.DeepEquals(actual.RootElement[i], expected.RootElement[i]))
                throw new InvalidOperationException($"Overload diagnostic {i}: {actual.RootElement[i].GetRawText()}");
        return actual.RootElement.GetArrayLength();
    }

    private static async Task<int> CallArityDiagnosticSafety()
    {
        string source = "\n" + """
            interface Array<T> { length: number; [n: number]: T; }
            declare function range(a: number, b?: string): void;
            range(); range(1, '', true, false);
            declare const object: { method(a: number): void };
            object.method();
            declare function receiver(this: {}, value: number): void;
            receiver();
            declare function binding({ x }: { x: number }): void;
            binding();
            declare function rest(...items: [number, string]): void;
            rest();
            declare function unbounded(a: number, ...items: string[]): void;
            unbounded();
            declare function overload(a: number): void;
            declare function overload(a: number, b: number, c: number): void;
            overload(1, 2);
            declare function generic<T, U = string>(): void;
            generic<number, string, boolean>();
            range<number>(1);
            declare function typeOverload<T>(): void;
            declare function typeOverload<T, U, V>(): void;
            typeOverload<number, string>();
            interface Promise<T> { then(cb: (value: T) => unknown): unknown; }
            interface PromiseConstructor { new <T>(executor: (resolve: (value: T) => void) => void): Promise<T>; }
            declare var Promise: PromiseConstructor;
            new Promise(resolve => { resolve(); });
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        // Counts, spans and related parameters are captured from the pinned checker.
        const string reference = """
            [{"arguments":["1-2","0"],"category":1,"chain":[],"code":2554,"file":"/project/main.ts","key":"Expected_0_arguments_but_got_1_2554","length":5,"related":[{"arguments":["a"],"category":3,"chain":[],"code":6210,"file":"/project/main.ts","key":"An_argument_for_0_was_not_provided_6210","length":9,"related":[],"start":79}],"start":109},{"arguments":["1-2","4"],"category":1,"chain":[],"code":2554,"file":"/project/main.ts","key":"Expected_0_arguments_but_got_1_2554","length":11,"related":[],"start":131},{"arguments":["1","0"],"category":1,"chain":[],"code":2554,"file":"/project/main.ts","key":"Expected_0_arguments_but_got_1_2554","length":6,"related":[{"arguments":["a"],"category":3,"chain":[],"code":6210,"file":"/project/main.ts","key":"An_argument_for_0_was_not_provided_6210","length":9,"related":[],"start":176}],"start":203},{"arguments":["1","0"],"category":1,"chain":[],"code":2554,"file":"/project/main.ts","key":"Expected_0_arguments_but_got_1_2554","length":8,"related":[{"arguments":["value"],"category":3,"chain":[],"code":6210,"file":"/project/main.ts","key":"An_argument_for_0_was_not_provided_6210","length":13,"related":[],"start":249}],"start":271},{"arguments":["1","0"],"category":1,"chain":[],"code":2554,"file":"/project/main.ts","key":"Expected_0_arguments_but_got_1_2554","length":7,"related":[{"arguments":[],"category":3,"chain":[],"code":6211,"file":"/project/main.ts","key":"An_argument_matching_this_binding_pattern_was_not_provided_6211","length":20,"related":[],"start":308}],"start":337},{"arguments":["2","0"],"category":1,"chain":[],"code":2554,"file":"/project/main.ts","key":"Expected_0_arguments_but_got_1_2554","length":4,"related":[{"arguments":["items"],"category":1,"chain":[],"code":6236,"file":"/project/main.ts","key":"Arguments_for_the_rest_parameter_0_were_not_provided_6236","length":26,"related":[],"start":370}],"start":405},{"arguments":["1","0"],"category":1,"chain":[],"code":2555,"file":"/project/main.ts","key":"Expected_at_least_0_arguments_but_got_1_2555","length":9,"related":[{"arguments":["a"],"category":3,"chain":[],"code":6210,"file":"/project/main.ts","key":"An_argument_for_0_was_not_provided_6210","length":9,"related":[],"start":440}],"start":478},{"arguments":["2","1","3"],"category":1,"chain":[],"code":2575,"file":"/project/main.ts","key":"No_overload_expects_0_arguments_but_overloads_do_exist_that_expect_either_1_or_2_arguments_2575","length":8,"related":[],"start":601},{"arguments":["1-2","3"],"category":1,"chain":[],"code":2558,"file":"/project/main.ts","key":"Expected_0_type_arguments_but_got_1_2558","length":23,"related":[],"start":674},{"arguments":["0","1"],"category":1,"chain":[],"code":2558,"file":"/project/main.ts","key":"Expected_0_type_arguments_but_got_1_2558","length":6,"related":[],"start":708},{"arguments":["2","1","3"],"category":1,"chain":[],"code":2743,"file":"/project/main.ts","key":"No_overload_expects_0_type_arguments_but_overloads_do_exist_that_expect_either_1_or_2_type_arguments_2743","length":14,"related":[],"start":823},{"arguments":["1","0"],"category":1,"chain":[],"code":2794,"file":"/project/main.ts","key":"Expected_0_arguments_but_got_1_Did_you_forget_to_include_void_in_your_type_argument_to_Promise_2794","length":7,"related":[{"arguments":["value"],"category":3,"chain":[],"code":6210,"file":"/project/main.ts","key":"An_argument_for_0_was_not_provided_6210","length":8,"related":[],"start":969}],"start":1078}]
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
            throw new InvalidOperationException("Call arity diagnostic count");
        for (int i = 0; i < actual.RootElement.GetArrayLength(); i++)
            if (!System.Text.Json.JsonElement.DeepEquals(actual.RootElement[i], expected.RootElement[i]))
                throw new InvalidOperationException($"Call arity diagnostic {i}: {actual.RootElement[i].GetRawText()}");
        return actual.RootElement.GetArrayLength();
    }

    private static async Task<int> SignatureDiagnosticSafety()
    {
        string source = "\n" + """
            interface Array<T> { length: number; [n: number]: T; }
            declare const parameterSource: (s: string) => void;
            const parameterTarget: (n: number) => void = parameterSource;
            declare const aritySource: (a: number, b: number) => void;
            const arityTarget: (a: number) => void = aritySource;
            declare const returnSource: () => number;
            const returnTarget: () => string = returnSource;
            declare const methodSource: { method(): number };
            const methodTarget: { method(): string } = methodSource;
            declare const callbackSource: (cb: (n: number) => void) => void;
            const callbackTarget: (cb: (s: string) => void) => void = callbackSource;
            declare const thisSource: (this: { x: number }) => void;
            const thisTarget: (this: { x: string }) => void = thisSource;
            declare const overloaded: { (s: string): void; (n: number): void };
            const overloadTarget: (b: boolean) => void = overloaded;
            const overloadSuccess: (n: number) => void = overloaded;
            declare const empty: {};
            const noCall: (x: number) => void = empty;
            declare const abstractSource: abstract new () => {};
            const concreteTarget: new () => {} = abstractSource;
            declare const guardSource: (x: unknown) => x is number;
            const guardTarget: (x: unknown) => x is string = guardSource;
            declare const booleanSource: (x: unknown) => boolean;
            const predicateTarget: (x: unknown) => x is string = booleanSource;
            declare const thisGuard: (this: any) => this is { value: number };
            const identifierGuard: (x: unknown) => x is { value: number } = thisGuard;
            declare const secondGuard: (x: unknown, y: unknown) => y is string;
            const firstGuard: (a: unknown, b: unknown) => a is string = secondGuard;
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        // Complete diagnostics from the pinned checker, including overload order and predicates.
        const string reference = """
            [{"arguments":["(s: string) => void","(n: number) => void"],"category":1,"chain":[{"arguments":["s","n"],"category":1,"chain":[{"arguments":["number","string"],"category":1,"chain":[],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":15,"related":[],"start":114}],"code":2328,"file":"/project/main.ts","key":"Types_of_parameters_0_and_1_are_incompatible_2328","length":15,"related":[],"start":114}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":15,"related":[],"start":114},{"arguments":["(a: number, b: number) => void","(a: number) => void"],"category":1,"chain":[{"arguments":["2","1"],"category":1,"chain":[],"code":2849,"file":"/project/main.ts","key":"Target_signature_provides_too_few_arguments_Expected_0_or_more_but_got_1_2849","length":11,"related":[],"start":235}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":11,"related":[],"start":235},{"arguments":["() => number","() => string"],"category":1,"chain":[{"arguments":["number","string"],"category":1,"chain":[],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":12,"related":[],"start":331}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":12,"related":[],"start":331},{"arguments":["{ method(): number; }","{ method(): string; }"],"category":1,"chain":[{"arguments":["method()"],"category":1,"chain":[{"arguments":["number","string"],"category":1,"chain":[],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":12,"related":[],"start":430}],"code":2201,"file":"/project/main.ts","key":"The_types_returned_by_0_are_incompatible_between_these_types_2201","length":12,"related":[],"start":430}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":12,"related":[],"start":430},{"arguments":["(cb: (n: number) => void) => void","(cb: (s: string) => void) => void"],"category":1,"chain":[{"arguments":["cb","cb"],"category":1,"chain":[{"arguments":["s","n"],"category":1,"chain":[{"arguments":["number","string"],"category":1,"chain":[],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":14,"related":[],"start":552}],"code":2328,"file":"/project/main.ts","key":"Types_of_parameters_0_and_1_are_incompatible_2328","length":14,"related":[],"start":552}],"code":2328,"file":"/project/main.ts","key":"Types_of_parameters_0_and_1_are_incompatible_2328","length":14,"related":[],"start":552}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":14,"related":[],"start":552},{"arguments":["(this: { x: number; }) => void","(this: { x: string; }) => void"],"category":1,"chain":[{"arguments":[],"category":1,"chain":[{"arguments":["{ x: string; }","{ x: number; }"],"category":1,"chain":[{"arguments":["x"],"category":1,"chain":[{"arguments":["string","number"],"category":1,"chain":[],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":10,"related":[],"start":683}],"code":2326,"file":"/project/main.ts","key":"Types_of_property_0_are_incompatible_2326","length":10,"related":[],"start":683}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":10,"related":[],"start":683}],"code":2685,"file":"/project/main.ts","key":"The_this_types_of_each_signature_are_incompatible_2685","length":10,"related":[],"start":683}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":10,"related":[],"start":683},{"arguments":["{ (s: string): void; (n: number): void; }","(b: boolean) => void"],"category":1,"chain":[{"arguments":["s","b"],"category":1,"chain":[{"arguments":["boolean","string"],"category":1,"chain":[],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":14,"related":[],"start":813}],"code":2328,"file":"/project/main.ts","key":"Types_of_parameters_0_and_1_are_incompatible_2328","length":14,"related":[],"start":813}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":14,"related":[],"start":813},{"arguments":["{}","(x: number) => void"],"category":1,"chain":[{"arguments":["{}","(x: number): void"],"category":1,"chain":[],"code":2658,"file":"/project/main.ts","key":"Type_0_provides_no_match_for_the_signature_1_2658","length":6,"related":[],"start":952}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":6,"related":[],"start":952},{"arguments":["abstract new () => {}","new () => {}"],"category":1,"chain":[{"arguments":[],"category":1,"chain":[],"code":2517,"file":"/project/main.ts","key":"Cannot_assign_an_abstract_constructor_type_to_a_non_abstract_constructor_type_2517","length":14,"related":[],"start":1048}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":14,"related":[],"start":1048},{"arguments":["(x: unknown) => x is number","(x: unknown) => x is string"],"category":1,"chain":[{"arguments":["x is number","x is string"],"category":1,"chain":[{"arguments":["number","string"],"category":1,"chain":[],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":11,"related":[],"start":1157}],"code":1226,"file":"/project/main.ts","key":"Type_predicate_0_is_not_assignable_to_1_1226","length":11,"related":[],"start":1157}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":11,"related":[],"start":1157},{"arguments":["(x: unknown) => boolean","(x: unknown) => x is string"],"category":1,"chain":[{"arguments":["(x: unknown): boolean"],"category":1,"chain":[],"code":1224,"file":"/project/main.ts","key":"Signature_0_must_be_a_type_predicate_1224","length":15,"related":[],"start":1273}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":15,"related":[],"start":1273},{"arguments":["(this: any) => this is { value: number; }","(x: unknown) => x is { value: number; }"],"category":1,"chain":[{"arguments":["this is { value: number; }","x is { value: number; }"],"category":1,"chain":[{"arguments":[],"category":1,"chain":[],"code":2518,"file":"/project/main.ts","key":"A_this_based_type_guard_is_not_compatible_with_a_parameter_based_type_guard_2518","length":15,"related":[],"start":1408}],"code":1226,"file":"/project/main.ts","key":"Type_predicate_0_is_not_assignable_to_1_1226","length":15,"related":[],"start":1408}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":15,"related":[],"start":1408},{"arguments":["(x: unknown, y: unknown) => y is string","(a: unknown, b: unknown) => a is string"],"category":1,"chain":[{"arguments":["y is string","a is string"],"category":1,"chain":[{"arguments":["y","a"],"category":1,"chain":[],"code":1227,"file":"/project/main.ts","key":"Parameter_0_is_not_in_the_same_position_as_parameter_1_1227","length":10,"related":[],"start":1551}],"code":1226,"file":"/project/main.ts","key":"Type_predicate_0_is_not_assignable_to_1_1226","length":10,"related":[],"start":1551}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":10,"related":[],"start":1551}]
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
            throw new InvalidOperationException("Signature diagnostic count");
        int checks = 0;
        for (int i = 0; i < actual.RootElement.GetArrayLength(); i++)
        {
            if (!System.Text.Json.JsonElement.DeepEquals(actual.RootElement[i], expected.RootElement[i]))
                throw new InvalidOperationException($"Signature diagnostic {i}: {actual.RootElement[i].GetRawText()}");
            checks++;
        }
        var successful = file.DescendantsAndSelf().OfType<VariableDeclarationNode>()
            .Single(d => d.Name is IdentifierNode { Text: { Span: "overloadSuccess" } });
        if (checker.DetailedDiagnosticsForFile(file).Any(d => d.Start >= successful.Pos && d.Start < successful.End))
            throw new InvalidOperationException("Successful overload retained trial failures");
        return checks + 1;
    }

    private static async Task<int> PredicateSafety()
    {
        const string source = """
            interface Array<T> { length: number; [n: number]: T; }
            type Invalid = asserts value;
            function wrong(value: number): value is string { return true; }
            function rest(...values: unknown[]): values is string[] { return true; }
            function binding({ value }: { value: unknown }): value is string { return true; }
            function missing(value: unknown): absent is string { return true; }
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        await checker.CheckProgramAsync();
        var codes = checker.DiagnosticCodesForFile(program.SourceFiles[0].Syntax);
        if (!codes.SequenceEqual(
            [
                    DiagnosticCode.CannotFindParameter0,
                    DiagnosticCode.ATypePredicateIsOnlyAllowedInReturnTypePositionForFunctionsAndMethods,
                    DiagnosticCode.ATypePredicateCannotReferenceARestParameter,
                    DiagnosticCode.ATypePredicateCannotReferenceElement0InABindingPattern,
                    DiagnosticCode.ATypePredicateSTypeMustBeAssignableToItsParameterSType
                ]))
            throw new InvalidOperationException($"Predicate diagnostics: {string.Join(',', codes)}");
        return 1;
    }

    private static async Task<int> VarianceSafety()
    {
        const string source = """
            interface Consumer<out T> { consume: (value: T) => void; }
            interface Producer<in T> { value: T; }
            interface Both<in out T> { value: T; consume: (value: T) => void; }
            type Union<out T> = T | undefined;
            function invalid<in T>(value: T) { return value; }
            interface Invalid<const T> { value: T; }
            type Reordered<out in T> = { value: T; };
            type Duplicate<in in T> = (value: T) => void;
            interface ValidConsumer<in T> { consume: (value: T) => void; }
            interface ValidProducer<out T> { value: T; }
            class InvalidMember { in value = 0; out other = 0; }
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts")!.Syntax;
        await checker.CheckSourceFileAsync(file);
        var codes = checker.DiagnosticCodesForFile(file);
        if (!codes.SequenceEqual(
            [
                    DiagnosticCode.X0ModifierMustPrecede1Modifier,
                    DiagnosticCode.X0ModifierAlreadySeen,
                    DiagnosticCode.X0ModifierCanOnlyAppearOnATypeParameterOfAClassInterfaceOrTypeAlias,
                    DiagnosticCode.X0ModifierCanOnlyAppearOnATypeParameterOfAClassInterfaceOrTypeAlias,
                    DiagnosticCode.X0ModifierCanOnlyAppearOnATypeParameterOfAClassInterfaceOrTypeAlias,
                    DiagnosticCode.X0ModifierCanOnlyAppearOnATypeParameterOfAFunctionMethodOrClass,
                    DiagnosticCode.Type0IsNotAssignableToType1AsImpliedByVarianceAnnotation,
                    DiagnosticCode.Type0IsNotAssignableToType1AsImpliedByVarianceAnnotation,
                    DiagnosticCode.VarianceAnnotationsAreOnlySupportedInTypeAliasesForObjectFunctionConstructorAndMappedTypes
                ]))
            throw new InvalidOperationException($"Variance diagnostics: {string.Join(',', codes)}");
        if (checker.VarianceTypeParameter is not null || checker.Variances.Measuring)
            throw new InvalidOperationException("Variance annotation checking retained active state");
        return 2;
    }

    private static async Task<int> DecoratorSafety()
    {
        const string library = """
            interface Array<T> { length: number; [n: number]: T; }
            interface ReadonlyArray<T> { readonly length: number; readonly [n: number]: T; }
            interface TypedPropertyDescriptor<T> { value?: T; get?: () => T; set?: (value: T) => void; }
            interface ClassDecoratorContext<T> { kind: 'class'; name: string | undefined; }
            interface ClassMethodDecoratorContext<This, Value> { kind: 'method'; name: string | symbol; static: boolean; private: boolean; }
            interface ClassFieldDecoratorContext<This, Value> { kind: 'field'; name: string | symbol; static: boolean; private: boolean; value: Value; }
            """;
        const string standard = """
            declare function classDecorator<T extends new (...args: any[]) => any>(value: T, context: ClassDecoratorContext<T>): T;
            declare const provider: { tag: true; decorate<T>(this: { tag: true }, value: T, context: any): T; };
            declare function staticOnly(value: undefined, context: ClassFieldDecoratorContext<any, number> & { name: 'count'; static: true; private: false }): void;
            declare function wrong(value: undefined, context: ClassFieldDecoratorContext<any, string>): void;
            declare function replacement(value: any, context: any): string;
            @classDecorator class C {
                @provider.decorate method() { return 1; }
                @staticOnly static count = 1;
                @wrong value = 1;
                @replacement replaced() { return 1; }
            }
            """;
        const string legacy = """
            declare function methodDecorator(target: object, key: string, descriptor: TypedPropertyDescriptor<() => number>): void;
            declare function parameterDecorator(target: object, key: string | undefined, index: 0): void;
            class C {
                constructor(@parameterDecorator value: number) { }
                @methodDecorator method() { return 1; }
            }
            """;
        const string metadata = """
            import { C, I } from './types';
            declare function decorator(...args: any[]): void;
            class D { @decorator classValue!: C; @decorator interfaceValue!: I; }
            """;
        int checks = 0;
        foreach (var (source, old, emit, expected) in new (string, bool, bool, DiagnosticCode[])[]
            {
                (standard, false, false,
                    [
                        DiagnosticCode.UnableToResolveSignatureOfPropertyDecoratorWhenCalledAsAnExpression,
                        DiagnosticCode.DecoratorFunctionReturnType0IsNotAssignableToType1
                    ]),
                (legacy, true, false, []),
                (metadata, true, true, [DiagnosticCode.ATypeReferencedInADecoratedSignatureMustBeImportedWithImportTypeOrANamespaceImportWhenIsolatedModulesAndEmitDecoratorMetadataAreEnabled])
            })
        {
            var options = new CompilerOptions();
            options.SetRaw("noLib", "true");
            options.SetRaw("strict", "true");
            options.SetRaw("target", "\"es2022\"");
            options.SetRaw("module", "\"esnext\"");
            options.SetRaw("isolatedModules", "true");
            options.SetRaw("experimentalDecorators", old ? "true" : "false");
            options.SetRaw("emitDecoratorMetadata", emit ? "true" : "false");
            var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
            {
                ["/project/main.ts"] = Wtf8.Encode(source),
                ["/project/globals.d.ts"] = Wtf8.Encode(library),
                ["/project/types.ts"] = Wtf8.Encode("export class C { value = 1; } export interface I { value: number; }")
            }), "/project", new("/project/tsconfig.json", options, ["/project/main.ts", "/project/globals.d.ts"], [], [], []));
            var checker = await program.CreateCheckerAsync();
            var file = program.GetFile("/project/main.ts")!.Syntax;
            var nodes = file.DescendantsAndSelf().ToArray();
            var parents = nodes.Select(n => n.Parent).ToArray();
            await checker.CheckSourceFileAsync(file);
            var codes = checker.DiagnosticCodesForFile(file);
            if (!codes.SequenceEqual(expected))
                throw new InvalidOperationException($"Decorator diagnostics: {string.Join(',', codes)}");
            if (!nodes.Select(n => n.Parent).SequenceEqual(parents))
                throw new InvalidOperationException("Decorator checking changed source parents");
            checks += 2;
            if (emit)
            {
                var import = nodes.OfType<ImportSpecifierNode>().Single(n => n.Name!.Text == "C");
                if (!checker.Links.Aliases.Get(checker.Symbols.Declaration(import)!).Referenced)
                    throw new InvalidOperationException("Decorator metadata did not retain the value import");
                checks++;
            }
        }
        return checks;
    }

    private static async Task<int> DocumentationSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Documentation signature assertion {checks + 1}");
            checks++;
        }
        const string source = """
            /** @type {(value:number, optional?:string)=>boolean} */
            function typed(value, optional) { return true; }
            /**
             * @template T
             * @param {T} value
             * @returns {T}
             */
            function identity(value) { return value; }
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("allowJs", "true");
        options.SetRaw("checkJs", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.js"] = Wtf8.Encode(source)
        }), "/project", new("/project/tsconfig.json", options, ["/project/main.js"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var functions = program.SourceFiles[0].Syntax.Statements!.OfType<FunctionDeclarationNode>().ToArray();
        var typed = (await checker.FullSignatureAsync(functions[0], default))!;
        Check(typed.Parameters.Count == 2 && typed.MinArgumentCount == 1);
        Check(await checker.Parameters.AtAsync(typed, 0) == checker.Context.NumberType);
        Check(await checker.Signatures.ReturnAsync(typed) == checker.Context.BooleanType);
        Check(await checker.FullSignatureAsync(functions[0], default) == typed);
        var generic = await checker.Signatures.FromDeclarationAsync(functions[1]);
        Check(generic.TypeParameters.Count == 1 && await checker.Signatures.ReturnAsync(generic) == generic.TypeParameters[0]);
        Check(functions[0].FullSignature is not null && ((ParameterDeclarationNode)functions[0].Parameters![0]).Type is null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await checker.FullSignatureAsync(functions[0], cancellation.Token);
            throw new InvalidOperationException("Cancelled documentation signature query completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        var independent = await program.CreateCheckerAsync();
        var independentSignature = (await independent.FullSignatureAsync(functions[0], default))!;
        Check(independentSignature != typed && independentSignature.Context != typed.Context);
        var inheritedDocumentationProgram = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/docs.js"] = Wtf8.Encode("/** @param {number} missing */\nconst f = /** prose */ function(value) {};")
        }), "/project", new("/project/tsconfig.json", options, ["/project/docs.js"], [], [], []));
        var inheritedDocumentationChecker = await inheritedDocumentationProgram.CreateCheckerAsync();
        await inheritedDocumentationChecker.CheckProgramAsync();
        Check(
            inheritedDocumentationChecker.DiagnosticCodesForProgramFile(
                inheritedDocumentationProgram.SourceFiles[0].Syntax).Contains(
                    DiagnosticCode.JSDocParamTagHasName0ButThereIsNoParameterWithThatName));
        return checks;
    }

    private static async Task<int> IterationSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Iteration assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T} interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T} interface SymbolConstructor{readonly iterator:unique symbol} declare const Symbol:SymbolConstructor; interface Iterable<T,R=any,N=any>{[Symbol.iterator]():Iterator<T,R,N>} interface Iterator<T,R=any,N=any>{next(value:N):{done:false;value:T}|{done:true;value:R}} interface Generator<T,R,N> extends Iterable<T,R,N>,Iterator<T,R,N>{} declare const values:Iterable<number,string,boolean>; interface Invalid{[Symbol.iterator]():{next?:()=>{value:number}}} type Yielded={done:false;value:number}; type Returned={done:true;value:string};";
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
        var values = await host.Values.GetAsync(symbols.Globals["values"]);
        var result = await host.Iterators.IterableAsync(values, IterationUse.Spread);
        Check(result == new IterationTypes(context.NumberType, context.StringType, context.BooleanType));
        int cached = host.Iterators.CacheCount;
        Check(await host.Iterators.IterableAsync(values, IterationUse.Destructuring) == result && host.Iterators.CacheCount == cached);
        var node = symbols.Globals["values"].ValueDeclaration!;
        Check(await host.Iteration.CheckAsync(IterationUse.Spread, values, context.UndefinedType, node) == context.NumberType);
        Check(
            host.Diagnostics.Contains(
                DiagnosticCode.CannotIterateValueBecauseTheNextMethodOfItsIteratorExpectsType1ButArraySpreadWillAlwaysSend0));
        var invalid = await host.Declared.GetAsync(symbols.Globals["Invalid"]);
        Check(!(await host.Iterators.IterableAsync(invalid, IterationUse.Spread)).HasTypes);
        cached = host.Iterators.CacheCount;
        Check(!(await host.Iterators.IterableAsync(invalid, IterationUse.Spread, node)).HasTypes);
        Check(
            host.Iterators.CacheCount == cached
                && host.DeferredIterationDiagnostics.Single().Related.Single().Code == DiagnosticCode.AnIteratorMustHaveANextMethod);
        var yielded = await host.Declared.GetAsync(symbols.Globals["Yielded"]);
        var returned = await host.Declared.GetAsync(symbols.Globals["Returned"]);
        Check(await host.Iterators.ResultAsync(yielded) == new IterationTypes(context.NumberType, context.VoidType, null));
        Check(await host.Iterators.ResultAsync(returned) == new IterationTypes(null, context.StringType, null));
        Check(
            await host.Iterators.ResultAsync(await host.Algebra.UnionAsync(
                [
                    yielded,
                    returned
                ])) == new IterationTypes(context.NumberType, context.StringType, null));
        Check(
            await host.Iterators.IterableAsync(
                context.AnyType,
                IterationUse.ForAwaitOf) == new IterationTypes(context.AnyType, context.AnyType, context.AnyType));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                await host.Iterators.IterableAsync(values, IterationUse.Spread, cancellation: cancelled.Token);
                throw new InvalidOperationException("Cancelled iterator cache query accepted");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        try
        {
            await host.Iterators.IterableAsync(new TypeContext().AnyType, IterationUse.Spread);
            throw new InvalidOperationException("Foreign iterator accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Check(await host.Iterators.IterableAsync(values, IterationUse.Spread) == result);
        var other = context.CreateTypeReference(
            (InterfaceType)((TypeReference)values).Target!,
            [context.BigIntType, context.StringType, context.BooleanType]);
        cached = host.Iterators.CacheCount;
        host.BeforeIterationGlobal = _ => throw new OperationCanceledException();
        try
        {
            await host.Iterators.IterableAsync(other, IterationUse.Spread);
            throw new InvalidOperationException("Interrupted iterator query completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        host.BeforeIterationGlobal = null;
        Check(host.Iterators.CacheCount == cached);
        Check((await host.Iterators.IterableAsync(other, IterationUse.Spread)).Yield == context.BigIntType);
        SyntaxNode body = new YieldExpressionNode { Expression = new NumericLiteralNode { Text = "1" } };
        var leaf = body;
        for (int i = 0; i < 20000; i++)
            body = new ParenthesizedExpressionNode { Expression = body };
        Check(FunctionSyntax.Yields(body).Single() == leaf);
        return checks;
    }

    private static async Task<int> CallSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Call assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T} interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T} declare function f(value:number):number; f(1); f('bad'); declare function map<T,U>(value:T,callback:(value:T)=>U):U; map(1,value=>value+1);";
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        var program = await CompilerProgram.CreateAsync(
            new MemoryFileSystem(new Dictionary<string, byte[]> { ["/project/main.ts"] = Wtf8.Encode(source) }),
            "/project", new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scope = new CheckerEnvironment(context, links);
        await CheckerSymbols.CreateAsync(program, links, scope);
        var host = new Checker(context, links, scope);
        var calls = program.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<CallExpressionNode>().ToArray();
        using (var cancellation = new CancellationTokenSource())
        {
            host.BeforeExpressionFinish = () =>
            {
                if (host.Expressions.CurrentNode is NumericLiteralNode)
                    cancellation.Cancel();
            };
            try
            {
                await host.Calls.CheckAsync(calls[0], cancellation: cancellation.Token);
                throw new InvalidOperationException("Call cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        host.BeforeExpressionFinish = null;
        Check(links.Signatures.Get(calls[0]).ResolvedSignature is null);
        Check(
            host.CallResolution.ActiveCount == 0
                && host.CallResolution.ResolutionDepth == 0
                && host.Instantiation.Resolutions.ResolutionStart == 0);
        Check(host.Contexts.ContextDepth == 0 && host.Contexts.InferenceDepth == 0 && host.Expressions.CurrentNode is null);
        Check(await host.Calls.CheckAsync(calls[0]) == context.NumberType);
        var cached = links.Signatures.Get(calls[0]).ResolvedSignature;
        Check(cached is not null && cached != host.CallSignatures.Resolving);
        Check(await host.CallResolution.GetAsync(calls[0]) == cached);
        var candidates = new List<Signature>();
        Check(await host.CallResolution.GetAsync(calls[0], candidates) == cached && candidates.Count == 1);
        using (var cancellation = new CancellationTokenSource())
        {
            host.BeforeCallDiagnostics = _ => cancellation.Cancel();
            try
            {
                await host.CallResolution.GetAsync(calls[1], cancellation: cancellation.Token);
                throw new InvalidOperationException("Failure diagnostic cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        host.BeforeCallDiagnostics = null;
        Check(links.Signatures.Get(calls[1]).ResolvedSignature is null);
        Check(
            host.CallResolution.ActiveCount == 0
                && host.CallResolution.ResolutionDepth == 0
                && host.Instantiation.Resolutions.ResolutionStart == 0);
        Check(
            await host.Calls.CheckAsync(calls[1]) == context.NumberType
                && host.Diagnostics.Contains(DiagnosticCode.ArgumentOfType0IsNotAssignableToParameterOfType1));
        var arrow = (ArrowFunctionNode)calls[2].Arguments![1];
        using (var cancellation = new CancellationTokenSource())
        {
            host.BeforeFunctionDeclaration = _ => cancellation.Cancel();
            try
            {
                await host.CallResolution.GetAsync(calls[2], cancellation: cancellation.Token);
                throw new InvalidOperationException("Callback inference cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        host.BeforeFunctionDeclaration = null;
        Check(
            links.Signatures.Get(calls[2]).ResolvedSignature is null
                && (links.Nodes.Get(arrow).Flags & NodeCheckFlags.ContextChecked) == 0);
        Check(host.Contexts.ContextDepth == 0 && host.Contexts.InferenceDepth == 0 && host.CallResolution.ResolutionDepth == 0);
        Check(await host.Calls.CheckAsync(calls[2]) == context.NumberType);
        var genericCandidates = new List<Signature> { host.CallSignatures.Any };
        var genericResult = await host.CallResolution.GetAsync(calls[2], genericCandidates);
        Check(genericCandidates.Count == 1 && genericCandidates[0] == genericResult && genericResult.TypeParameters.Count == 0);
        var original = host.Inference.Create([context.NewTypeParameter()]);
        var first = original.Inferences[0];
        first.Candidates.Add(context.NumberType);
        try
        {
            await original.RunAsync<Type>(() =>
            {
                original.Inferences[0] = new InferenceInfo(first.Parameter);
                original.InferredTypeParameters = [context.NewTypeParameter()];
                original.ReturnMapper = TypeMapper.Create([first.Parameter], [context.StringType]);
                throw new OperationCanceledException();
            }, default);
            throw new InvalidOperationException("Inference rollback failure ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(original.Inferences[0] == first && first.Candidates.SequenceEqual([context.NumberType]));
        Check(original.InferredTypeParameters is null && original.ReturnMapper is null);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await host.CallResolution.GetAsync(calls[0], cancellation: cancelled.Token);
            throw new InvalidOperationException("Cached call cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(
            host.CallResolution.ActiveCount == 0 && host.CallResolution.ResolutionDepth == 0 && host.Instantiation.Resolutions.Count == 0);
        return checks;
    }

    private static async Task<int> FunctionSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Function assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T} interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T} type NumberFunction=(value:number)=>number; type StringFunction=(value:string)=>string; type GenericFunction=<T>(value:T)=>T; const f=value=>value; const generic=value=>value; const predicate=(value:string|number)=>typeof value==='string';";
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
        var nodes = program.SourceFiles[0].Syntax.DescendantsAndSelf().ToArray();
        var arrows = nodes.OfType<ArrowFunctionNode>().ToArray();
        var number = await host.Declared.GetAsync(symbols.Globals["NumberFunction"]);
        var text = await host.Declared.GetAsync(symbols.Globals["StringFunction"]);
        var generic = await host.Declared.GetAsync(symbols.Globals["GenericFunction"]);
        var signature = await host.Signatures.FromDeclarationAsync(arrows[0]);
        using (var cancellation = new CancellationTokenSource())
        {
            host.BeforeFunctionDeclaration = _ => cancellation.Cancel();
            try
            {
                await host.Contexts.CheckWithAsync(arrows[0], number, cancellation: cancellation.Token);
                throw new InvalidOperationException("Function cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        host.BeforeFunctionDeclaration = null;
        Check((links.Nodes.Get(arrows[0]).Flags & NodeCheckFlags.ContextChecked) == 0);
        Check(signature.ResolvedReturnType is null && links.Values.Get(signature.Parameters[0]).ResolvedType is null);
        Check(host.Contexts.ContextDepth == 0 && host.Contexts.InferenceDepth == 0 && host.Expressions.CurrentNode is null);
        await host.Contexts.CheckWithAsync(arrows[0], text);
        Check(await host.Signatures.ReturnAsync(signature) == context.StringType);
        Check(await host.Values.GetAsync(signature.Parameters[0]) == context.StringType);
        var genericSignature = await host.Signatures.FromDeclarationAsync(arrows[1]);
        host.BeforeFunctionDeclaration = _ => throw new InvalidOperationException("generic-failure");
        try
        {
            await host.Contexts.CheckWithAsync(arrows[1], generic);
            throw new InvalidOperationException("Generic failure ignored");
        }
        catch (InvalidOperationException error) when (error.Message == "generic-failure")
        {
            checks++;
        }
        host.BeforeFunctionDeclaration = null;
        Check(genericSignature.TypeParameters.Count == 0 && genericSignature.ResolvedReturnType is null);
        Check(links.Values.Get(genericSignature.Parameters[0]).ResolvedType is null);
        await host.Contexts.CheckWithAsync(arrows[1], generic);
        Check(genericSignature.TypeParameters.Count == 1);
        Check(await host.Signatures.ReturnAsync(genericSignature) == genericSignature.TypeParameters[0]);
        await host.Functions.CheckAsync(arrows[2]);
        var predicateSignature = await host.Signatures.FromDeclarationAsync(arrows[2]);
        Check(await host.Signatures.ReturnAsync(predicateSignature) == context.BooleanType);
        Check(await host.Signatures.PredicateAsync(predicateSignature) is { Type: { } predicate } && predicate == context.StringType);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await host.Functions.ContextualAsync(arrows[0], cancellation: cancelled.Token);
            throw new InvalidOperationException("Cached contextual function cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        var returnStatement = new ReturnStatementNode { Expression = new NumericLiteralNode { Text = "1" } };
        SyntaxNode body = returnStatement;
        for (int i = 0; i < 20_000; i++)
            body = new BlockNode { Statements = new NodeList([body]) };
        Check(FunctionSyntax.Returns(body).Single() == returnStatement);
        Check(!FunctionSyntax.Sensitive(arrows[2], symbols));
        Check(host.Instantiation.Resolutions.Count == 0 && host.Contexts.ContextDepth == 0 && host.Contexts.InferenceDepth == 0);
        return checks;
    }
}

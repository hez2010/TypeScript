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
        Console.WriteLine(
            $"{checks} signature/function/call/iteration/inference/context/cancellation assertions; binding and return traversal depth 20000");
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
        if (!codes.SequenceEqual([1029, 1030, 1274, 1274, 1274, 1277, 2636, 2636, 2637]))
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
        foreach (var (source, old, emit, expected) in new (string, bool, bool, int[])[]
            { (standard, false, false, [1240, 1270]), (legacy, true, false, []), (metadata, true, true, [1272]) })
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
                inheritedDocumentationProgram.SourceFiles[0].Syntax).Contains(8024));
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
        Check(host.Diagnostics.Contains(2764));
        var invalid = await host.Declared.GetAsync(symbols.Globals["Invalid"]);
        Check(!(await host.Iterators.IterableAsync(invalid, IterationUse.Spread)).HasTypes);
        cached = host.Iterators.CacheCount;
        Check(!(await host.Iterators.IterableAsync(invalid, IterationUse.Spread, node)).HasTypes);
        Check(host.Iterators.CacheCount == cached && host.DeferredIterationDiagnostics.Single().Related.Single().Code == 2489);
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
        Check(await host.Calls.CheckAsync(calls[1]) == context.NumberType && host.Diagnostics.Contains(2345));
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

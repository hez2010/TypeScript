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

internal static class CheckerAssignabilityTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Assignability assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T;push(...items:T[]):number}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}interface Out<T>{value:T}interface In<T>{accept:(x:T)=>void}interface Both<T>{f:(x:T)=>T}interface Phantom<T>{}type S={first:{a:string};second:{b:number}};type T={first:{a:string};second:{b:string}};type U={kind:'a';value:number}|{kind:'b';value:number};type D={kind:'a'|'b';value:number};type F=(x:string)=>number;type G=(x:'x')=>number;";
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        var program = await CompilerProgram.CreateAsync(
            new MemoryFileSystem(new Dictionary<string, byte[]> { ["/project/main.ts"] = Wtf8.Encode(source) }),
            "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scope = new CheckerEnvironment(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scope);
        var host = new Checker(context, links, scope);
        async ValueTask<Type> Type(string name) => await host.Declared.GetAsync(symbols.Globals[name]);
        var output = (InterfaceType)await Type("Out");
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Variances.OfTypeAsync(output);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(
            host.Variances.Cache.Count == 0
                && !host.Variances.Measuring
                && host.Instantiation.Resolutions.Count == 0
                && host.Instantiation.Resolutions.ResolutionStart == 0);
        host.BeforeNode = null;
        Check((await host.Variances.OfTypeAsync(output)).SequenceEqual([VarianceFlags.Covariant]));
        Check((await host.Variances.OfTypeAsync((InterfaceType)await Type("In"))).SequenceEqual([VarianceFlags.Contravariant]));
        Check((await host.Variances.OfTypeAsync((InterfaceType)await Type("Both"))).SequenceEqual([VarianceFlags.Invariant]));
        Check((await host.Variances.OfTypeAsync((InterfaceType)await Type("Phantom"))).SequenceEqual([VarianceFlags.Independent]));
        var narrow = context.CreateTypeReference(output, [context.GetStringLiteralType("x")]);
        var wide = context.CreateTypeReference(output, [context.StringType]);
        Check(await host.Relations.RelatedAsync(narrow, wide, RelationKind.Assignable));
        Check(!await host.Relations.RelatedAsync(wide, narrow, RelationKind.Assignable));
        var array = context.CreateTypeReference((InterfaceType)host.ArrayTarget(false), [context.StringType]);
        var readonlyArray = context.CreateTypeReference((InterfaceType)host.ArrayTarget(true), [context.StringType]);
        Check(await host.Relations.RelatedAsync(array, readonlyArray, RelationKind.Assignable));
        Check(!await host.Relations.RelatedAsync(readonlyArray, array, RelationKind.Assignable));

        host.Relations.State.Reliability = 0;
        await host.Variances.ReportAsync(context.MarkerSub, false);
        Check(host.Relations.State.Reliability == RelationComparisonResult.ReportsUnreliable);
        await host.Variances.ReportAsync(context.MarkerOther, true);
        Check(host.Relations.State.Reliability == RelationComparisonResult.ReportsMask);
        host.Relations.State.Reliability = 0;

        var s = await Type("S");
        var t = await Type("T");
        int before = host.Relations.Cache(RelationKind.Assignable).Count;
        host.BeforeNode = node =>
        {
            if (node.Kind == SyntaxKind.StringKeyword
                && node.Parent is PropertySignatureDeclarationNode { Name: IdentifierNode { Text: "b" } })
                throw new OperationCanceledException();
        };
        try
        {
            await host.Relations.RelatedAsync(s, t, RelationKind.Assignable);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(host.Relations.Cache(RelationKind.Assignable).Count == before && host.Instantiation.Resolutions.Count == 0);
        host.BeforeNode = null;
        Check(!await host.Relations.RelatedAsync(s, t, RelationKind.Assignable));
        Check(host.Relations.Cache(RelationKind.Assignable).Count > before);

        var union = (UnionType)await Type("U");
        var discriminated = await Type("D");
        Check(await host.Discriminants.PropertyAsync(union, "kind"));
        var discriminant = await host.Properties.CachedPropertyAsync(union, "kind");
        Check(
            (discriminant!.CheckFlags & (CheckFlags.IsDiscriminant | CheckFlags.IsDiscriminantComputed)) == (CheckFlags.IsDiscriminant | CheckFlags.IsDiscriminantComputed));
        Check(await host.Relations.RelatedAsync(discriminated, union, RelationKind.Assignable));
        Check(
            await host.Discriminants.MatchAsync(union, discriminated) is null
                && union.KeyPropertyName == Symbol.InternalPrefix + "missing");
        Check((await host.Facts.GetAsync(context.UnknownType, TypeFacts.IsUndefinedOrNull)) == 0);
        Check(await host.Facts.NonNullableAsync(context.UnknownType) == context.UnknownEmptyObjectType);
        var nullable = await host.Algebra.UnionAsync([context.StringType, context.UndefinedType, context.NullType]);
        Check(await host.Facts.NonNullableAsync(nullable) == context.StringType);
        Check(
            await host.Facts.FilterAsync(nullable, TypeFacts.NEUndefined) is UnionType nonUndefined
                && nonUndefined.Types.Contains(context.NullType)
                && !nonUndefined.Types.Contains(context.UndefinedType));
        Check((await host.Facts.GetAsync(context.GetNumberLiteralType(0), TypeFacts.Falsy | TypeFacts.Truthy)) == TypeFacts.Falsy);
        Check(
            (await host.Facts.GetAsync(context.GetNumberLiteralType(double.NaN), TypeFacts.Falsy | TypeFacts.Truthy)) == TypeFacts.Truthy);

        var f = (ObjectType)await Type("F");
        var g = (ObjectType)await Type("G");
        await host.Members.ResolveAsync(f);
        await host.Members.ResolveAsync(g);
        Check(await host.Relations.RelatedAsync(f, g, RelationKind.Assignable));
        Check(!await host.Relations.RelatedAsync(g, f, RelationKind.Assignable));
        Check(await host.SignatureAssignability.ErasedAsync(f.CallSignatures[0]) == f.CallSignatures[0]);
        var generic = context.NewSignature(0, null, [context.NewTypeParameter()], null, [], context.VoidType, null, 0);
        Check((await host.SignatureAssignability.ErasedAsync(generic)).TypeParameters.Count == 0);
        Check(await host.SignatureAssignability.ErasedAsync(generic) == await host.SignatureAssignability.ErasedAsync(generic));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await host.Variances.OfTypeAsync(output, cancellation.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(!host.Variances.Measuring && host.Instantiation.Resolutions.Count == 0);
        checks += await MissingPropertySafety();
        checks += await DiagnosticChainSafety();
        Console.WriteLine($"{checks} structural relation/variance/facts/cancellation assertions");
    }

    private static async Task<int> DiagnosticChainSafety()
    {
        string source = """

            interface Box<T> { value: T; }
            declare const numberBox: Box<number>;
            const stringBox: Box<string> = numberBox;
            declare const nestedSource: { outer: { leaf: number } };
            const nestedTarget: { outer: { leaf: string } } = nestedSource;
            declare const numberIndex: { [key: string]: number };
            const stringIndex: { [key: string]: string } = numberIndex;
            declare const objectSource: { value: number };
            const objectIndex: { [key: string]: string } = objectSource;
            function unconstrained<T>() { const invalid: T = 123; }
            function constrained<T extends string>() { const invalid: T = 'x'; }

            """.Replace("\r\n", "\n", StringComparison.Ordinal);
        // Complete diagnostic records captured from the pinned checker.
        const string reference = """
            [{"arguments":["Box<number>","Box<string>"],"category":1,"chain":[{"arguments":["number","string"],"category":1,"chain":[],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":9,"related":[],"start":76}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":9,"related":[],"start":76},{"arguments":["{ outer: { leaf: number; }; }","{ outer: { leaf: string; }; }"],"category":1,"chain":[{"arguments":["outer.leaf"],"category":1,"chain":[{"arguments":["number","string"],"category":1,"chain":[],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":12,"related":[],"start":175}],"code":2200,"file":"/project/main.ts","key":"The_types_of_0_are_incompatible_between_these_types_2200","length":12,"related":[],"start":175}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":12,"related":[],"start":175},{"arguments":["{ [key: string]: number; }","{ [key: string]: string; }"],"category":1,"chain":[{"arguments":["string"],"category":1,"chain":[{"arguments":["number","string"],"category":1,"chain":[],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":11,"related":[],"start":293}],"code":2634,"file":"/project/main.ts","key":"_0_index_signatures_are_incompatible_2634","length":11,"related":[],"start":293}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":11,"related":[],"start":293},{"arguments":["{ value: number; }","{ [key: string]: string; }"],"category":1,"chain":[{"arguments":["value"],"category":1,"chain":[{"arguments":["number","string"],"category":1,"chain":[],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":11,"related":[],"start":400}],"code":2530,"file":"/project/main.ts","key":"Property_0_is_incompatible_with_index_signature_2530","length":11,"related":[],"start":400}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":11,"related":[],"start":400},{"arguments":["number","T"],"category":1,"chain":[{"arguments":["T","number"],"category":1,"chain":[],"code":5082,"file":"/project/main.ts","key":"_0_could_be_instantiated_with_an_arbitrary_type_which_could_be_unrelated_to_1_5082","length":7,"related":[],"start":491}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":7,"related":[],"start":491},{"arguments":["string","T"],"category":1,"chain":[{"arguments":["string","T","string"],"category":1,"chain":[],"code":5075,"file":"/project/main.ts","key":"_0_is_assignable_to_the_constraint_of_type_1_but_1_could_be_instantiated_with_a_different_subtype_of_5075","length":7,"related":[],"start":560}],"code":2322,"file":"/project/main.ts","key":"Type_0_is_not_assignable_to_type_1_2322","length":7,"related":[],"start":560}]
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
        if (!System.Text.Json.JsonElement.DeepEquals(actual.RootElement, expected.RootElement))
            throw new InvalidOperationException("Relation diagnostic records: " + actual.RootElement.GetRawText());
        var declarations = file.DescendantsAndSelf().OfType<VariableDeclarationNode>().Take(2).ToArray();
        var sourceType = await checker.GetTypeFromTypeNodeAsync(declarations[0].Type!);
        var targetType = await checker.GetTypeFromTypeNodeAsync(declarations[1].Type!);
        if (await checker.Relations.RelatedAsync(sourceType, targetType, RelationKind.Assignable))
            throw new InvalidOperationException("Diagnostic elaboration changed relation result");
        int cacheCount = checker.Relations.Cache(RelationKind.Assignable).Count;
        var reliability = checker.Relations.State.Reliability;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await checker.Relations.ExplainAsync(sourceType, targetType, RelationKind.Assignable, cancellation.Token);
            throw new InvalidOperationException("Cancelled relation elaboration succeeded");
        }
        catch (OperationCanceledException) { }
        if (checker.Relations.Cache(RelationKind.Assignable).Count != cacheCount || checker.Relations.State.Reliability != reliability)
            throw new InvalidOperationException("Cancelled relation elaboration changed cache state");
        var explanation = await checker.Relations.ExplainAsync(sourceType, targetType, RelationKind.Assignable, default);
        if (explanation?.Next is not { Code: 2322 } detail
            || detail.Source != checker.Context.NumberType
            || detail.Target != checker.Context.StringType)
            throw new InvalidOperationException("Cached negative relation lost its explanation");
        return 4;
    }

    private static async Task<int> MissingPropertySafety()
    {
        const string source = """
            interface One { value: number; }
            const one: One = {};
            const many: { a: number; b: number; optional?: number } = {};
            const large: { a: number; b: number; c: number; d: number; e: number; f: number } = {};
            declare function use(value: One): void;
            use({});
            type Box<T extends One> = T;
            type Invalid = Box<{}>;
            const fn: { value: number } = () => 0;
            const withCall: { (): void; value: number } = () => {};
            class Implements implements One {}
            class A { #p = 1; }
            class B { #p = 1; }
            const privateValue: A = new B();
            const optional: { value?: number } = {};
            const indexed: { [key: string]: number } = {};
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("target", "\"es2015\"");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        await checker.CheckProgramAsync();
        var codes = checker.DiagnosticCodesForFile(program.SourceFiles[0].Syntax);
        if (!codes.SequenceEqual([2322, 2322, 2420, 2739, 2740, 2741, 2741, 2741, 2741]))
            throw new InvalidOperationException($"Missing property diagnostics: {string.Join(',', codes)}");
        if (!checker.RequiredPropertyDeclarations.Values.Any(p => p.Count == 6)
            || !checker.RequiredPropertyDeclarations.Values.Any(p => p.Count == 1 && p[0].Name == "value"))
            throw new InvalidOperationException("Missing property diagnostics lost required declarations");
        return 2;
    }
}

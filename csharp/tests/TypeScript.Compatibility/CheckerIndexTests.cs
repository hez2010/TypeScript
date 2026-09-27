using System.Text.Json;
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

internal static class CheckerIndexTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Indexed type assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}interface I{a:string;b?:number}interface J{x:string;y:number}type M<T>={[P in keyof T]?:T[P]};type A<T>={a:string;b:number}[keyof T];type B<T>=[1,...T[]][number];";
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scope = new CheckerEnvironment(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scope);
        var host = new Checker(context, links, scope);
        var i = await host.Declared.GetAsync(symbols.Globals["I"]);
        var j = await host.Declared.GetAsync(symbols.Globals["J"]);
        host.BeforeMemberTable = _ => throw new OperationCanceledException();
        try
        {
            await host.Keys.GetAsync(i);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        host.BeforeMemberTable = null;
        var keys = await host.Keys.GetAsync(i);
        Check(keys is UnionType { Types.Count: 2, Origin: IndexType });
        Check(await host.Keys.GetAsync(i) == keys);
        Check(await host.Indexed.GetAsync(i, context.GetStringLiteralType("a")) == context.StringType);
        Check(await host.Indexed.TryGetAsync(i, context.GetStringLiteralType("absent")) is null);
        Check(await host.Indexed.GetAsync(i, context.GetStringLiteralType("absent")) == context.UnknownType);
        Check(await host.Indexed.GetAsync(context.WildcardType, context.NumberType) == context.WildcardType);
        Check(await host.Indexed.GetAsync(i, context.WildcardType) == context.WildcardType);
        var both = await host.Algebra.UnionAsync([context.GetStringLiteralType("x"), context.GetStringLiteralType("y")]);
        Check(await host.Indexed.GetAsync(j, both) == context.StringOrNumberType);
        Check(await host.Indexed.GetAsync(j, both, AccessFlags.Writing) == context.NeverType);
        var p = context.NewTypeParameter();
        var index = context.GetGenericIndexedAccess(p, both, 0);
        Check(await host.Indexed.SimplifyAsync(index, false) is UnionType { Types.Count: 2 });
        Check(await host.Indexed.SimplifyAsync(index, true) is IntersectionType { Types.Count: 2 });
        var generic = context.GetGenericIndexedAccess(p, context.StringType, AccessFlags.Writing);
        Check(generic.AccessFlags == 0 && generic != context.GetGenericIndexedAccess(p, context.StringType, 0));
        Check(context.GetGenericIndexedAccess(p, context.StringType, AccessFlags.Writing) == generic);
        var optional = context.GetGenericIndexedAccess(p, context.StringType, AccessFlags.Writing | AccessFlags.IncludeUndefined);
        Check(optional.AccessFlags == AccessFlags.IncludeUndefined && optional != generic);

        var mapping = (MappedType)await host.Declared.GetAsync(symbols.Globals["M"]);
        var mappedAccess = context.GetGenericIndexedAccess(mapping, context.StringType, 0);
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Indexed.SimplifyAsync(mappedAccess, false);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        host.BeforeNode = null;
        var mappedValue = await host.Indexed.SimplifyAsync(mappedAccess, false);
        Check(mappedValue != mappedAccess && await host.Indexed.SimplifyAsync(mappedAccess, false) == mappedValue);
        Check(host.Instantiation.Resolutions.Count == 0);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await host.Indexed.SimplifyAsync(mappedAccess, false, cancelled.Token);
            throw new InvalidOperationException("Cached simplification ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(await host.Indexed.SimplifyAsync(mappedAccess, false) == mappedValue);
        try
        {
            await host.Keys.GetAsync(new TypeContext(true, true).StringType);
            throw new InvalidOperationException("Foreign context accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Type deep = p;
        var q = context.NewTypeParameter();
        for (int n = 0; n < 20_000; n++)
            deep = context.NewIndexedAccessType(deep, q, 0);
        Check(await host.SimplifyAsync(deep, false, default) == deep);
        Type deepKeys = context.StringType;
        for (int n = 0; n < 20_000; n++)
            deepKeys = context.NewIntersectionType([deepKeys]);
        Check(TypeKeys.KeyIncluded(deepKeys, TypeFlags.String));
        Check(!TypeKeys.KeyIncluded(deepKeys, TypeFlags.Number));
        checks += await ComputedIndexSafety();
        checks += await IndexDiagnosticSafety();
        Console.WriteLine($"Checker indexed type safety: {checks} assertions; 20,000-level simplification and key traversal");
    }

    private static async Task<int> IndexDiagnosticSafety()
    {
        string source = "\n" + """
            interface Array<T> { length: number; [n: number]: T; }
            interface Shape { length: number; }
            declare const shape: Shape;
            shape['missing'];
            shape['lenght'];
            declare const key: string;
            shape[key];
            declare const numeric: number;
            shape[numeric];
            declare const tuple: [number, string];
            tuple[3]; tuple[-1];
            declare const objectKey: {};
            shape[objectKey];
            shape[true];
            declare const array: number[];
            array['missing'];
            class Static { static value = 1; }
            declare const instance: Static;
            instance['value'];
            declare const container: { api: { get(key: string): number; set(key: string, value: number): void } };
            container.api[key];
            container.api[key] = 1;
            declare const symbol: unique symbol;
            shape[symbol];
            enum E { A = 'a', B = 'b' }
            declare const member: E.A;
            shape[member];
            const literal = { value: 1 };
            literal['missing'];
            type Bad = Shape['missing'];
            type Invalid = Shape[{}];
            type Numeric = Shape[number];
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        const string reference = """
            [{"arguments":["\"missing\"","Shape"],"category":1,"chain":[{"arguments":["missing","Shape"],"category":1,"chain":[],"code":2339,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_2339","length":16,"related":[],"start":120}],"code":7053,"file":"/project/main.ts","key":"Element_implicitly_has_an_any_type_because_expression_of_type_0_can_t_be_used_to_index_type_1_7053","length":16,"related":[],"start":120},{"arguments":["lenght","Shape","length"],"category":1,"chain":[],"code":2551,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_Did_you_mean_2_2551","length":8,"related":[],"start":144},{"arguments":["string","Shape"],"category":1,"chain":[{"arguments":["string","Shape"],"category":1,"chain":[],"code":7054,"file":"/project/main.ts","key":"No_index_signature_with_a_parameter_of_type_0_was_found_on_type_1_7054","length":10,"related":[],"start":182}],"code":7053,"file":"/project/main.ts","key":"Element_implicitly_has_an_any_type_because_expression_of_type_0_can_t_be_used_to_index_type_1_7053","length":10,"related":[],"start":182},{"arguments":["number","Shape"],"category":1,"chain":[{"arguments":["number","Shape"],"category":1,"chain":[],"code":7054,"file":"/project/main.ts","key":"No_index_signature_with_a_parameter_of_type_0_was_found_on_type_1_7054","length":14,"related":[],"start":225}],"code":7053,"file":"/project/main.ts","key":"Element_implicitly_has_an_any_type_because_expression_of_type_0_can_t_be_used_to_index_type_1_7053","length":14,"related":[],"start":225},{"arguments":["[number, string]","2","3"],"category":1,"chain":[],"code":2493,"file":"/project/main.ts","key":"Tuple_type_0_of_length_1_has_no_element_at_index_2_2493","length":1,"related":[],"start":286},{"arguments":[],"category":1,"chain":[],"code":2514,"file":"/project/main.ts","key":"A_tuple_type_cannot_be_indexed_with_a_negative_value_2514","length":2,"related":[],"start":296},{"arguments":["{}"],"category":1,"chain":[],"code":2538,"file":"/project/main.ts","key":"Type_0_cannot_be_used_as_an_index_type_2538","length":9,"related":[],"start":336},{"arguments":["true"],"category":1,"chain":[],"code":2538,"file":"/project/main.ts","key":"Type_0_cannot_be_used_as_an_index_type_2538","length":4,"related":[],"start":354},{"arguments":[],"category":1,"chain":[],"code":7015,"file":"/project/main.ts","key":"Element_implicitly_has_an_any_type_because_index_expression_is_not_of_type_number_7015","length":9,"related":[],"start":398},{"arguments":["value","Static","Static['value']"],"category":1,"chain":[],"code":2576,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_Did_you_mean_to_access_the_static_member_2_instead_2576","length":17,"related":[],"start":477},{"arguments":["{ get(key: string): number; set(key: string, value: number): void; }","container.api.get"],"category":1,"chain":[],"code":7052,"file":"/project/main.ts","key":"Element_implicitly_has_an_any_type_because_type_0_has_no_index_signature_Did_you_mean_to_call_1_7052","length":18,"related":[],"start":599},{"arguments":["{ get(key: string): number; set(key: string, value: number): void; }","container.api.set"],"category":1,"chain":[],"code":7052,"file":"/project/main.ts","key":"Element_implicitly_has_an_any_type_because_type_0_has_no_index_signature_Did_you_mean_to_call_1_7052","length":18,"related":[],"start":619},{"arguments":["unique symbol","Shape"],"category":1,"chain":[{"arguments":["[symbol]","Shape"],"category":1,"chain":[],"code":2339,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_2339","length":13,"related":[],"start":680}],"code":7053,"file":"/project/main.ts","key":"Element_implicitly_has_an_any_type_because_expression_of_type_0_can_t_be_used_to_index_type_1_7053","length":13,"related":[],"start":680},{"arguments":["E.A","Shape"],"category":1,"chain":[{"arguments":["[E.A]","Shape"],"category":1,"chain":[],"code":2339,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_2339","length":13,"related":[],"start":750}],"code":7053,"file":"/project/main.ts","key":"Element_implicitly_has_an_any_type_because_expression_of_type_0_can_t_be_used_to_index_type_1_7053","length":13,"related":[],"start":750},{"arguments":["\"missing\"","{ value: number; }"],"category":1,"chain":[{"arguments":["missing","{ value: number; }"],"category":1,"chain":[],"code":2339,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_2339","length":18,"related":[],"start":795}],"code":7053,"file":"/project/main.ts","key":"Element_implicitly_has_an_any_type_because_expression_of_type_0_can_t_be_used_to_index_type_1_7053","length":18,"related":[],"start":795},{"arguments":["missing","Shape"],"category":1,"chain":[],"code":2339,"file":"/project/main.ts","key":"Property_0_does_not_exist_on_type_1_2339","length":9,"related":[],"start":832},{"arguments":["{}"],"category":1,"chain":[],"code":2538,"file":"/project/main.ts","key":"Type_0_cannot_be_used_as_an_index_type_2538","length":2,"related":[],"start":865},{"arguments":["Shape","number"],"category":1,"chain":[],"code":2537,"file":"/project/main.ts","key":"Type_0_has_no_matching_index_signature_for_type_1_2537","length":6,"related":[],"start":891}]
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        options.SetRaw("noErrorTruncation", "true");
        options.SetRaw("target", "\"esnext\"");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        await checker.CheckProgramAsync();
        var file = program.GetFile("/project/main.ts")!.Syntax;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            CheckerCorpusTests.WriteDiagnostics(writer, checker.DetailedDiagnosticsForFile(file).OrderBy(d => d.Start));
        using var actual = JsonDocument.Parse(stream.ToArray());
        using var expected = JsonDocument.Parse(reference);
        if (actual.RootElement.GetArrayLength() != expected.RootElement.GetArrayLength())
            throw new InvalidOperationException("Index diagnostic count");
        for (int i = 0; i < actual.RootElement.GetArrayLength(); i++)
            if (!JsonElement.DeepEquals(actual.RootElement[i], expected.RootElement[i]))
                throw new InvalidOperationException($"Index diagnostic {i}: {actual.RootElement[i].GetRawText()}");
        int count = checker.DetailedDiagnosticsForFile(file).Count;
        var node = file.DescendantsAndSelf().OfType<ElementAccessExpressionNode>().First();
        var type = await checker.Declared.GetAsync(checker.Symbols.Globals["Shape"]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await checker.InvalidIndexAsync(
                node,
                type,
                checker.Context.BooleanType,
                DiagnosticCode.Type0CannotBeUsedAsAnIndexType,
                cancellation.Token);
            throw new InvalidOperationException("Cancelled index diagnostic succeeded");
        }
        catch (OperationCanceledException) { }
        if (checker.DetailedDiagnosticsForFile(file).Count != count)
            throw new InvalidOperationException("Cancelled index diagnostic was published");
        await checker.InvalidIndexAsync(node, type, checker.Context.BooleanType, DiagnosticCode.Type0CannotBeUsedAsAnIndexType, default);
        if (checker.DetailedDiagnosticsForFile(file).Count != count + 1)
            throw new InvalidOperationException("Cancelled index diagnostic could not be retried");
        return actual.RootElement.GetArrayLength() + 2;
    }

    private static async Task<int> ComputedIndexSafety()
    {
        const string source = """
            interface Array<T> { length: number; [n: number]: T; }
            declare const textKey: string, numberKey: number, symbolKey: symbol;
            class Strings { [textKey] = 1; named = 'text'; }
            class Numbers { readonly [numberKey] = 1; 1 = 'text'; other = true; }
            class Symbols { [symbolKey] = true; other = 1; }
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        options.SetRaw("target", "\"esnext\"");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }),
            "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.SourceFiles[0].Syntax;
        var nodes = file.DescendantsAndSelf().ToArray();
        var parents = nodes.Select(n => n.Parent).ToArray();
        var stringType = await checker.Declared.GetAsync(checker.Symbols.Globals["Strings"]);
        int tables = checker.LateMembers.CachedTableCount;
        checker.BeforeExpressionFinish = () => throw new OperationCanceledException();
        try
        {
            await checker.IndexesAsync(stringType, default);
            throw new InvalidOperationException("Computed key cancellation ignored");
        }
        catch (OperationCanceledException) { }
        checker.BeforeExpressionFinish = null;
        if (checker.LateMembers.CachedTableCount != tables || checker.Instantiation.Resolutions.Count != 0)
            throw new InvalidOperationException("Computed index cancellation left partial state");
        var strings = (await checker.IndexesAsync(stringType, default)).Single();
        if (strings.KeyType != checker.Context.StringType || strings.IsReadonly
            || !checker.Predicates.Maybe(strings.ValueType, TypeFlags.NumberLike, default)
            || !checker.Predicates.Maybe(strings.ValueType, TypeFlags.StringLike, default))
            throw new InvalidOperationException("Computed string index lost sibling property types");
        var numbers = (await checker.IndexesAsync(await checker.Declared.GetAsync(checker.Symbols.Globals["Numbers"]), default)).Single();
        if (numbers.KeyType != checker.Context.NumberType || !numbers.IsReadonly
            || !checker.Predicates.Maybe(numbers.ValueType, TypeFlags.NumberLike, default)
            || !checker.Predicates.Maybe(numbers.ValueType, TypeFlags.StringLike, default)
            || checker.Predicates.Maybe(numbers.ValueType, TypeFlags.BooleanLike, default))
            throw new InvalidOperationException("Computed number index classification or readonly flag changed");
        var symbols = (await checker.IndexesAsync(await checker.Declared.GetAsync(checker.Symbols.Globals["Symbols"]), default)).Single();
        if (symbols.KeyType != checker.Context.ESSymbolType || symbols.IsReadonly
            || !checker.Predicates.Maybe(symbols.ValueType, TypeFlags.BooleanLike, default)
            || checker.Predicates.Maybe(symbols.ValueType, TypeFlags.NumberLike, default))
            throw new InvalidOperationException("Computed symbol index included a string-named property");
        await checker.CheckSourceFileAsync(file);
        if (checker.DiagnosticCodesForFile(file).Count != 0)
            throw new InvalidOperationException("Computed key grammar rejected late-bound names");
        if (!nodes.Select(n => n.Parent).SequenceEqual(parents))
            throw new InvalidOperationException("Computed index checking changed source parents");
        return 7;
    }

    internal static async Task WriteAsync(Utf8JsonWriter writer, IReadOnlyList<SyntaxNode> nodes, Checker host,
        Func<Type?, int> typeId, Func<SyntaxNode?, int> nodeId)
    {
        var keys = new List<int[]>();
        var indexes = new List<int[]>();
        foreach (var node in nodes)
        {
            if (node is TypeOperatorNode { Operator: SyntaxKind.KeyOfKeyword } operation)
            {
                var type = await host.Nodes.FromNodeAsync(operation.Type!);
                for (uint flags = 0; flags < 8; flags++)
                    keys.Add([nodeId(node), (int)flags, typeId(await host.Keys.GetAsync(type, (IndexFlags)flags))]);
            }
            if (node is IndexedAccessTypeNode access)
            {
                var objectType = await host.Nodes.FromNodeAsync(access.ObjectType!);
                var indexType = await host.Nodes.FromNodeAsync(access.IndexType!);
                foreach (uint flags in new uint[] { 0, 1, 2, 3, 4, 5, 16, 32, 64, 128 })
                {
                    var type = await host.Indexed.GetAsync(objectType, indexType, (AccessFlags)flags);
                    int id = typeId(type);
                    int read = typeId(await host.SimplifyAsync(type, false, default));
                    int write = typeId(await host.SimplifyAsync(type, true, default));
                    indexes.Add([nodeId(node), (int)flags, id, read, write]);
                }
            }
        }
        void Rows(string name, List<int[]> rows)
        {
            writer.WriteStartArray(name);
            foreach (var row in rows)
            {
                writer.WriteStartArray();
                foreach (int value in row)
                    writer.WriteNumberValue(value);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        Rows("keyQueries", keys);
        Rows("indexQueries", indexes);
    }
}

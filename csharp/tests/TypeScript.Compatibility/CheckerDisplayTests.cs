using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compatibility;

internal static class CheckerDisplayTests
{
    internal static async Task<int> FormatSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Display format assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        var files = new Dictionary<string, byte[]>
        {
            ["/project/main.ts"] = Wtf8.Encode(
                "interface Object{}interface Function{}type A={p:{q:number}};declare let value:A;class C{}class D{}function f(value:C):D{return new D()}function guard(value:unknown):value is A{return true}")
        };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project",
            new("/project/tsconfig.json", options, files.Keys.ToArray(), [], [], []));
        var source = program.SourceFiles[0].Syntax;
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var checker = await program.CreateCheckerAsync();
        var declaration = source.Statements!.OfType<VariableStatementNode>().Single().DeclarationList!.Declarations![0] as VariableDeclarationNode;
        var type = await checker.GetTypeFromTypeNodeAsync(declaration!.Type!);
        const string expanded = "{\n    p: {\n        q: number;\n    };\n}";
        var flags = TypeFormatFlags.InTypeAlias | TypeFormatFlags.MultilineObjectLiterals;
        Check(await checker.GetTypeDisplayAsync(type) == "A");
        Check(await checker.GetTypeDisplayAsync(type, source, flags) == expanded);
        Check(await checker.GetTypeDisplayAsync(type, source, (TypeFormatFlags)(1u << 31)) == "A");
        var functions = source.Statements!.OfType<FunctionDeclarationNode>().ToDictionary(n => n.Name!.Text);
        var signature = await checker.Signatures.FromDeclarationAsync(functions["f"]);
        Check(await checker.GetSignatureDisplayAsync(signature, flags: TypeFormatFlags.WriteArrowStyleSignature) == "(value: C) => D");
        var guard = await checker.Signatures.FromDeclarationAsync(functions["guard"]);
        var predicate = (await checker.Signatures.PredicateAsync(guard))!;
        Check(await checker.GetPredicateDisplayAsync(predicate, source, flags) == "value is " + expanded);
        var originalCaches = (checker.AccessibleChainCacheCount, checker.SymbolTableAliasCacheCount, checker.SymbolContainerCacheCount);
        using var stop = new CancellationTokenSource();
        checker.BeforeSymbolChainTable = _ =>
        {
            stop.Cancel();
            stop.Token.ThrowIfCancellationRequested();
        };
        try
        {
            await checker.GetSignatureDisplayAsync(signature, source, cancellation: stop.Token);
            throw new InvalidOperationException("Expected render cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        finally
        {
            checker.BeforeSymbolChainTable = null;
        }
        Check(originalCaches == (checker.AccessibleChainCacheCount, checker.SymbolTableAliasCacheCount, checker.SymbolContainerCacheCount));
        Check(await checker.GetSignatureDisplayAsync(signature, source) == "(value: C): D");
        try
        {
            await checker.GetTypeDisplayAsync(type, cancellation: stop.Token);
            throw new InvalidOperationException("Expected cancelled entry");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        var other = await program.CreateCheckerAsync();
        try
        {
            await checker.GetTypeDisplayAsync(other.Context.NumberType);
            throw new InvalidOperationException("Expected foreign type rejection");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        var otherSignature = await other.Signatures.FromDeclarationAsync(functions["guard"]);
        try
        {
            await checker.GetSignatureDisplayAsync(otherSignature);
            throw new InvalidOperationException("Expected foreign signature rejection");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        try
        {
            await checker.GetPredicateDisplayAsync((await other.Signatures.PredicateAsync(otherSignature))!);
            throw new InvalidOperationException("Expected foreign predicate rejection");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        var foreignSource = Parser.ParseSourceFile(new("/foreign.ts"), new SourceText("const x=0"));
        try
        {
            await checker.GetTypeDisplayAsync(type, foreignSource);
            throw new InvalidOperationException("Expected foreign scope rejection");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        var repeated = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => checker.GetTypeDisplayAsync(type, source, flags).AsTask()));
        Check(repeated.All(text => text == expanded));
        Check(snapshot.All(n => n.Node.Parent == n.Parent && n.Node.Pos == n.Pos && n.Node.End == n.End && n.Node.Flags == n.Flags));
        return checks;
    }

    internal static async Task FormatsAsync(Checker checker, SourceFileNode source, TypeFormatFlags[] formats, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("formats");
        foreach (var declaration in source.DescendantsAndSelf().OfType<VariableDeclarationNode>())
            if (declaration.Name is IdentifierNode name
                && name.Text.Span.StartsWith("show", StringComparison.Ordinal)
                && declaration.Type is not null)
            {
                var type = await checker.GetTypeFromTypeNodeAsync(declaration.Type);
                var enclosings = new SyntaxNode?[] { null, source, declaration };
                for (int scope = 0; scope < enclosings.Length; scope++)
                    foreach (var format in formats)
                    {
                        Write("type", await checker.GetTypeDisplayAsync(type, enclosings[scope], format));
                        for (int kind = 0; kind < 2; kind++)
                        {
                            var signatures = await checker.SignaturesAsync(type, kind != 0, default);
                            for (int i = 0; i < signatures.Count; i++)
                            {
                                string key = kind + ":" + i;
                                Write("signature:" + key, await checker.GetSignatureDisplayAsync(signatures[i], enclosings[scope], format));
                                if (await checker.Signatures.PredicateAsync(signatures[i]) is { } predicate)
                                    Write("predicate:" + key, await checker.GetPredicateDisplayAsync(predicate, enclosings[scope], format));
                            }
                        }
                        void Write(string kind, TextSlice text)
                        {
                            writer.WriteStartArray();
                            writer.WriteStringValue(name.Text.Span);
                            writer.WriteStringValue(kind);
                            writer.WriteNumberValue(scope);
                            writer.WriteNumberValue((uint)format);
                            writer.WriteStringValue(text.Span);
                            writer.WriteEndArray();
                        }
                    }
            }
        writer.WriteEndArray();
        await checker.CheckSourceFileAsync(source);
        writer.WriteStartArray("diagnostics");
        foreach (int code in checker.DiagnosticCodesForFile(source))
            writer.WriteNumberValue(code);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    internal static async Task<int> Safety()
    {
        const string source = """
            type NoInfer<T> = intrinsic; type Uppercase<S extends string> = intrinsic; type Lowercase<S extends string> = intrinsic;
            interface Array<T>{length:number;[n:number]:T;} interface ReadonlyArray<T>{readonly length:number;readonly [n:number]:T;}
            interface Object{} interface Function{} interface CallableFunction{} interface NewableFunction{} interface IArguments{} interface String{} interface Number{} interface Boolean{} interface RegExp{}
            interface Model { x:number; y:string } class Klass { x=1; }
            type Alias<T> = T | {value:T};
            function scope<T extends string, U extends Model, V>() {
            let showTemplate: `prefix${T}tail`;
            let showEscaped: `line\n${T}\`\${end}`;
            let showMapping: Uppercase<T>;
            let showNoInfer: NoInfer<T>;
            let showConditional: T extends string ? U : V;
            let showInferred: T extends `first${infer R extends string}` ? R : never;
            let showMapped: { readonly [P in keyof U]?: U[P] };
            let showMappedMinus: { -readonly [P in keyof U]-?: U[P] };
            let showRemapped: { [P in keyof U as `x${P & string}`]: U[P] };
            let showIndex: (U & {z:boolean})["x"];
            let showKeys: keyof (U | {z:boolean});
            let showAlias: Alias<T>;
            let showModel: Model;
            let showClass: Klass;
            let showTuple: readonly [name:T, value?:U, ...rest:V[]];
            let showFunction: <X extends "foo" | "bar" = "foo">(x:X,...rest:U[])=>X;
            }
            """;
        // Captured from the pinned reference's TypeToString, with strict mode off and on.
        const string reference = """
            [{"typeDisplays":[["showTemplate","`prefix${T}tail`"],["showEscaped","`line\n${T}\\`\\${end}`"],["showMapping","Uppercase\u003cT\u003e"],["showNoInfer","NoInfer\u003cT\u003e"],["showConditional","T extends string ? U : V"],["showInferred","T extends `first${infer R}` ? R : never"],["showMapped","{ readonly [P in keyof U]?: U[P]; }"],["showMappedMinus","{ -readonly [P in keyof U]-?: U[P]; }"],["showRemapped","{ [P in keyof U as `x${P \u0026 string}`]: U[P]; }"],["showIndex","(U \u0026 { z: boolean; })[\"x\"]"],["showKeys","keyof U \u0026 \"z\""],["showAlias","Alias\u003cT\u003e"],["showModel","Model"],["showClass","Klass"],["showTuple","readonly [name: T, value?: U, ...rest: V[]]"],["showFunction","\u003cX extends \"foo\" | \"bar\" = \"foo\"\u003e(x: X, ...rest: U[]) =\u003e X"]]},{"typeDisplays":[["showTemplate","`prefix${T}tail`"],["showEscaped","`line\n${T}\\`\\${end}`"],["showMapping","Uppercase\u003cT\u003e"],["showNoInfer","NoInfer\u003cT\u003e"],["showConditional","T extends string ? U : V"],["showInferred","T extends `first${infer R}` ? R : never"],["showMapped","{ readonly [P in keyof U]?: U[P] | undefined; }"],["showMappedMinus","{ -readonly [P in keyof U]-?: U[P]; }"],["showRemapped","{ [P in keyof U as `x${P \u0026 string}`]: U[P]; }"],["showIndex","(U \u0026 { z: boolean; })[\"x\"]"],["showKeys","keyof U \u0026 \"z\""],["showAlias","Alias\u003cT\u003e"],["showModel","Model"],["showClass","Klass"],["showTuple","readonly [name: T, value?: U | undefined, ...rest: V[]]"],["showFunction","\u003cX extends \"foo\" | \"bar\" = \"foo\"\u003e(x: X, ...rest: U[]) =\u003e X"]]}]
            """;
        using var expected = JsonDocument.Parse(reference);
        int checks = 0;
        for (int mode = 0; mode < 2; mode++)
        {
            var options = new CompilerOptions();
            options.SetRaw("noLib", "true");
            options.SetRaw("strict", mode == 0 ? "false" : "true");
            var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
            { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
                new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
            var checker = await program.CreateCheckerAsync();
            var declarations = program.GetFile("/project/main.ts")!.Syntax.DescendantsAndSelf().OfType<VariableDeclarationNode>()
                .Where(d => d.Name is IdentifierNode name && name.Text.Span.StartsWith("show", StringComparison.Ordinal)).ToArray();
            var rows = expected.RootElement[mode].GetProperty("typeDisplays");
            if (rows.GetArrayLength() != declarations.Length)
                throw new InvalidOperationException("Type display fixture coverage changed");
            for (int i = 0; i < declarations.Length; i++)
            {
                var type = await checker.GetTypeFromTypeNodeAsync(declarations[i].Type!);
                TextSlice text = await checker.TypeDisplay.GetAsync(type);
                if (((IdentifierNode)declarations[i].Name!).Text != rows[i][0].GetString()! || text != rows[i][1].GetString()!)
                    throw new InvalidOperationException($"Type display {mode}/{i}: {text}");
                checks++;
            }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await checker.TypeDisplay.GetAsync(checker.Context.StringType, cancellation.Token);
                throw new InvalidOperationException("Cancelled type display succeeded");
            }
            catch (OperationCanceledException) { }
            if (await checker.TypeDisplay.GetAsync(checker.Context.StringType) != "string")
                throw new InvalidOperationException("Type display did not recover from cancellation");
            checks++;
        }
        foreach (bool noTruncation in new[] { false, true })
        {
            var options = new CompilerOptions();
            options.SetRaw("noLib", "true");
            options.SetRaw("noErrorTruncation", noTruncation ? "true" : "false");
            var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
            { ["/project/main.ts"] = [] }), "/project", new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
            var checker = await program.CreateCheckerAsync();
            int limit = noTruncation ? 2_000_000 : 320;
            TextSlice text = await checker.TypeDisplay.GetAsync(checker.Context.GetStringLiteralType(new string('a', limit + 10)));
            if (text != "\"" + new string('a', limit - 4) + "...")
                throw new InvalidOperationException("Diagnostic display byte limit");
            checks++;
            if (!noTruncation)
            {
                text = await checker.TypeDisplay.GetAsync(checker.Context.GetStringLiteralType(string.Concat(Enumerable.Repeat("日", 150))));
                if (text != "\"" + string.Concat(Enumerable.Repeat("日", 105)) + "\ufffd...")
                    throw new InvalidOperationException("Diagnostic display UTF-8 truncation");
                checks++;
            }
        }
        return checks;
    }
}

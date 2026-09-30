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
        options.SetRaw("noLib"u8, "true"u8);
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode(
                "interface Object{}interface Function{}type A={p:{q:number}};declare let value:A;class C{}class D{}function f(value:C):D{return new D()}function guard(value:unknown):value is A{return true}")
        };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
            new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        var source = program.SourceFiles[0].Syntax;
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var checker = await program.CreateCheckerAsync();
        var declaration = source.Statements!.OfType<VariableStatementNode>().Single().DeclarationList!.Declarations![0] as VariableDeclarationNode;
        var type = await checker.GetTypeFromTypeNodeAsync(declaration!.Type!);
        Utf8String expanded = "{\n    p: {\n        q: number;\n    };\n}"u8;
        var flags = TypeFormatFlags.InTypeAlias | TypeFormatFlags.MultilineObjectLiterals;
        Check(await checker.GetTypeDisplayAsync(type) == "A"u8);
        Check(await checker.GetTypeDisplayAsync(type, source, flags) == expanded);
        Check(await checker.GetTypeDisplayAsync(type, source, (TypeFormatFlags)(1u << 31)) == "A"u8);
        var functions = source.Statements!.OfType<FunctionDeclarationNode>().ToDictionary(n => n.Name!.Text);
        var signature = await checker.Signatures.FromDeclarationAsync(functions["f"u8]);
        Check(await checker.GetSignatureDisplayAsync(signature, flags: TypeFormatFlags.WriteArrowStyleSignature) == "(value: C) => D"u8);
        var guard = await checker.Signatures.FromDeclarationAsync(functions["guard"u8]);
        var predicate = (await checker.Signatures.PredicateAsync(guard))!;
        Check(await checker.GetPredicateDisplayAsync(predicate, source, flags) == Utf8String.Copy("value is "u8) + expanded);
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
        Check(await checker.GetSignatureDisplayAsync(signature, source) == "(value: C): D"u8);
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
        var otherSignature = await other.Signatures.FromDeclarationAsync(functions["guard"u8]);
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
        var foreignSource = Parser.ParseSourceFile(new("/foreign.ts"u8), new SourceText("const x=0"u8));
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
        writer.WriteStartArray("formats"u8);
        foreach (var declaration in source.DescendantsAndSelf().OfType<VariableDeclarationNode>())
            if (declaration.Name is IdentifierNode name
                && name.Text.Span.StartsWith("show"u8, StringComparison.Ordinal)
                && declaration.Type is not null)
            {
                var type = await checker.GetTypeFromTypeNodeAsync(declaration.Type);
                var enclosings = new SyntaxNode?[] { null, source, declaration };
                for (int scope = 0; scope < enclosings.Length; scope++)
                    foreach (var format in formats)
                    {
                        Write("type"u8, await checker.GetTypeDisplayAsync(type, enclosings[scope], format));
                        for (int kind = 0; kind < 2; kind++)
                        {
                            var signatures = await checker.SignaturesAsync(type, kind != 0, default);
                            for (int i = 0; i < signatures.Count; i++)
                            {
                                Utf8String key = Utf8String.Concat(Utf8String.Format(kind), ":"u8, Utf8String.Format(i));
                                Write(Utf8String.Copy("signature:"u8) + key, await checker.GetSignatureDisplayAsync(signatures[i], enclosings[scope], format));
                                if (await checker.Signatures.PredicateAsync(signatures[i]) is { } predicate)
                                    Write(Utf8String.Copy("predicate:"u8) + key, await checker.GetPredicateDisplayAsync(predicate, enclosings[scope], format));
                            }
                        }
                        void Write(Utf8String kind, Utf8String text)
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
        writer.WriteStartArray("diagnostics"u8);
        foreach (int code in checker.DiagnosticCodesForFile(source))
            writer.WriteNumberValue(code);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    internal static async Task<int> Safety()
    {
        Utf8String source = """
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
            """u8;
        Utf8String reference = """
            [{"typeDisplays":[["showTemplate","`prefix${T}tail`"],["showEscaped","`line\n${T}\\`\\${end}`"],["showMapping","Uppercase\u003cT\u003e"],["showNoInfer","NoInfer\u003cT\u003e"],["showConditional","T extends string ? U : V"],["showInferred","T extends `first${infer R}` ? R : never"],["showMapped","{ readonly [P in keyof U]?: U[P]; }"],["showMappedMinus","{ -readonly [P in keyof U]-?: U[P]; }"],["showRemapped","{ [P in keyof U as `x${P \u0026 string}`]: U[P]; }"],["showIndex","(U \u0026 { z: boolean; })[\"x\"]"],["showKeys","keyof U \u0026 \"z\""],["showAlias","Alias\u003cT\u003e"],["showModel","Model"],["showClass","Klass"],["showTuple","readonly [name: T, value?: U, ...rest: V[]]"],["showFunction","\u003cX extends \"foo\" | \"bar\" = \"foo\"\u003e(x: X, ...rest: U[]) =\u003e X"]]},{"typeDisplays":[["showTemplate","`prefix${T}tail`"],["showEscaped","`line\n${T}\\`\\${end}`"],["showMapping","Uppercase\u003cT\u003e"],["showNoInfer","NoInfer\u003cT\u003e"],["showConditional","T extends string ? U : V"],["showInferred","T extends `first${infer R}` ? R : never"],["showMapped","{ readonly [P in keyof U]?: U[P] | undefined; }"],["showMappedMinus","{ -readonly [P in keyof U]-?: U[P]; }"],["showRemapped","{ [P in keyof U as `x${P \u0026 string}`]: U[P]; }"],["showIndex","(U \u0026 { z: boolean; })[\"x\"]"],["showKeys","keyof U \u0026 \"z\""],["showAlias","Alias\u003cT\u003e"],["showModel","Model"],["showClass","Klass"],["showTuple","readonly [name: T, value?: U | undefined, ...rest: V[]]"],["showFunction","\u003cX extends \"foo\" | \"bar\" = \"foo\"\u003e(x: X, ...rest: U[]) =\u003e X"]]}]
            """u8;
        using var expected = JsonDocument.Parse(reference.Memory);
        int checks = 0;
        for (int mode = 0; mode < 2; mode++)
        {
            var options = new CompilerOptions();
            options.SetRaw("noLib"u8, "true"u8);
            options.SetRaw("strict"u8, mode == 0 ? Utf8String.Copy("false"u8) : Utf8String.Copy("true"u8));
            var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
            { ["/project/main.ts"u8] = source.Span.ToArray() }), "/project"u8,
                new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
            var checker = await program.CreateCheckerAsync();
            var declarations = program.GetFile("/project/main.ts"u8)!.Syntax.DescendantsAndSelf().OfType<VariableDeclarationNode>()
                .Where(d => d.Name is IdentifierNode name && name.Text.Span.StartsWith("show"u8, StringComparison.Ordinal)).ToArray();
            var rows = expected.RootElement[mode].GetProperty("typeDisplays"u8);
            if (rows.GetArrayLength() != declarations.Length)
                throw new InvalidOperationException("Type display fixture coverage changed");
            for (int i = 0; i < declarations.Length; i++)
            {
                var type = await checker.GetTypeFromTypeNodeAsync(declarations[i].Type!);
                Utf8String text = await checker.TypeDisplay.GetAsync(type);
                if (((IdentifierNode)declarations[i].Name!).Text != JsonStrings.GetString(rows[i][0])! || text != JsonStrings.GetString(rows[i][1])!)
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
            if (await checker.TypeDisplay.GetAsync(checker.Context.StringType) != "string"u8)
                throw new InvalidOperationException("Type display did not recover from cancellation");
            checks++;
        }
        foreach (bool noTruncation in new[] { false, true })
        {
            var options = new CompilerOptions();
            options.SetRaw("noLib"u8, "true"u8);
            options.SetRaw("noErrorTruncation"u8, noTruncation ? Utf8String.Copy("true"u8) : Utf8String.Copy("false"u8));
            var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
            { ["/project/main.ts"u8] = [] }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
            var checker = await program.CreateCheckerAsync();
            int limit = noTruncation ? 2_000_000 : 320;
            Utf8String text = await checker.TypeDisplay.GetAsync(checker.Context.GetStringLiteralType(new Utf8String('a', limit + 10)));
            if (text != Utf8String.Concat("\""u8, new Utf8String('a', limit - 4), "..."u8))
                throw new InvalidOperationException("Diagnostic display byte limit");
            checks++;
            if (!noTruncation)
            {
                text = await checker.TypeDisplay.GetAsync(checker.Context.GetStringLiteralType(Utf8String.Concat(Enumerable.Repeat(Utf8String.Copy("日"u8), 150))));
                if (text != Utf8String.Concat("\""u8, Utf8String.Concat(Enumerable.Repeat(Utf8String.Copy("日"u8), 105)), [0xE6, (byte)'.', (byte)'.', (byte)'.']))
                    throw new InvalidOperationException("Diagnostic display UTF-8 truncation");
                checks++;
            }
        }
        return checks;
    }
}

using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class CheckerDisplayTests
{
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
                .Where(d => d.Name is IdentifierNode name && name.Text.StartsWith("show", StringComparison.Ordinal)).ToArray();
            var rows = expected.RootElement[mode].GetProperty("typeDisplays");
            if (rows.GetArrayLength() != declarations.Length)
                throw new InvalidOperationException("Type display fixture coverage changed");
            for (int i = 0; i < declarations.Length; i++)
            {
                var type = await checker.GetTypeFromTypeNodeAsync(declarations[i].Type!);
                string text = await checker.TypeDisplay.GetAsync(type);
                if (((IdentifierNode)declarations[i].Name!).Text != rows[i][0].GetString() || text != rows[i][1].GetString())
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
        return checks;
    }
}

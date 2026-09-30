using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class CheckerTypeSyntaxTests
{
    internal static async Task<int> DiagnosticSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Diagnostic renderer assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/project/globals.d.ts"u8] = Wtf8.Encode(
                "interface Object{}interface Function{}interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}"),
            ["/project/main.ts"u8] = Wtf8.Encode(
                "class C{value=1}class D{text=''}namespace N{export const value=1}enum E{A}let ctor:typeof C;let module:typeof N;let enumeration:E;function f(value:C):D{return new D()}function g(value:D):D{return value}function guard(value:unknown):value is number{return true}function assertS(値:unknown):asserts 値 is string{}")
        };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
            new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        var before = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var checker = await program.CreateCheckerAsync();
        foreach (var (name, expected) in new[] { (Utf8String.Copy("ctor"u8), Utf8String.Copy("typeof C"u8)), (Utf8String.Copy("module"u8), Utf8String.Copy("typeof N"u8)), (Utf8String.Copy("enumeration"u8), Utf8String.Copy("E"u8)) })
        {
            var declaration = source.DescendantsAndSelf().OfType<VariableDeclarationNode>().Single(n => n.Name is IdentifierNode i
                && i.Text == Utf8String.Copy(name));
            var type = await checker.GetTypeFromTypeNodeAsync(declaration.Type!);
            Check(await checker.TypeDisplay.GetAsync(type) == expected);
        }
        var functions = source.Statements!.OfType<FunctionDeclarationNode>().ToDictionary(n => n.Name!.Text);
        var guard = await checker.Signatures.FromDeclarationAsync(functions["guard"u8]);
        Check(await checker.TypeDisplay.GetSignatureAsync(guard) == "(value: unknown): value is number"u8);
        Check(await checker.TypeDisplay.GetPredicateAsync((await checker.Signatures.PredicateAsync(guard))!) == "value is number"u8);
        var assertion = await checker.Signatures.FromDeclarationAsync(functions["assertS"u8]);
        Check(await checker.TypeDisplay.GetSignatureAsync(assertion) == "(値: unknown): asserts 値 is string"u8);
        Check(await checker.TypeDisplay.GetPredicateAsync((await checker.Signatures.PredicateAsync(assertion))!) == "asserts 値 is string"u8);
        var f = await checker.Signatures.FromDeclarationAsync(functions["f"u8]);
        var g = await checker.Signatures.FromDeclarationAsync(functions["g"u8]);
        var caches = (checker.AccessibleChainCacheCount, checker.SymbolTableAliasCacheCount, checker.SymbolContainerCacheCount,
            checker.DeclarationVisibilityCount, checker.TypeSyntaxScopeCount);
        bool nested = false;
        using var stop = new CancellationTokenSource();
        checker.BeforeSymbolChainTable = _ =>
        {
            if (!nested)
            {
                nested = true;
                Check(checker.TypeDisplay.GetSignatureAsync(g).GetAwaiter().GetResult() == "(value: D): D"u8);
                return;
            }
            stop.Cancel();
            stop.Token.ThrowIfCancellationRequested();
        };
        try
        {
            await checker.SerializeSignatureSyntaxAsync(f, SyntaxKind.CallSignature, source, NodeBuilderFlags.IgnoreErrors, stop.Token);
            throw new InvalidOperationException("Expected nested-render cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        finally
        {
            checker.BeforeSymbolChainTable = null;
        }
        Check(nested);
        Check(caches == (checker.AccessibleChainCacheCount, checker.SymbolTableAliasCacheCount, checker.SymbolContainerCacheCount,
            checker.DeclarationVisibilityCount, checker.TypeSyntaxScopeCount));
        Check(await checker.TypeDisplay.GetSignatureAsync(f) == "(value: C): D"u8);
        TypeScript.Compiler.Checking.Type deep = checker.Context.NumberType;
        var array = (InterfaceType)checker.Environment.Globals.Types["Array"u8];
        for (int i = 0; i < 5000; i++)
            deep = checker.Context.CreateTypeReference(array, [deep]);
        Utf8String shortened = await checker.TypeDisplay.GetAsync(deep);
        Check(shortened.Length == 320 && shortened.Span.EndsWith("..."u8, StringComparison.Ordinal));
        Check(
            await checker.TypeDisplay.GetAsync(
                deep,
                NodeBuilderFlags.NoTruncation) == Utf8String.Copy("number"u8) + Utf8String.Concat(Enumerable.Repeat(Utf8String.Copy("[]"u8), 5000)));
        Check(before.All(n => n.Node.Parent == n.Parent && n.Node.Pos == n.Pos && n.Node.End == n.End && n.Node.Flags == n.Flags));
        return checks + await CheckerDisplayTests.Safety();
    }

    internal static async Task<int> DeclarationSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Signature declaration assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode(
                "function f(callback:()=>any):any{return callback()}class C{constructor(public value:number){}}type F=typeof f;")
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        var function = source.Statements!.OfType<FunctionDeclarationNode>().Single();
        var constructor = source.DescendantsAndSelf().OfType<ConstructorDeclarationNode>().Single();
        var signature = await checker.Signatures.FromDeclarationAsync(function);
        var ctor = await checker.Signatures.FromDeclarationAsync(constructor);
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        const NodeBuilderFlags flags = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation;
        Check(
            await checker.SerializeSignatureSyntaxAsync(
                signature,
                SyntaxKind.CallSignature,
                source,
                flags | NodeBuilderFlags.SuppressAnyReturnType)
            == "(callback: () => any)"u8);
        Check(await checker.SerializeSignatureSyntaxAsync(signature, SyntaxKind.CallSignature, source, flags)
            == "(callback: () => any): any"u8);
        Check(
            await checker.SerializeSignatureSyntaxAsync(
                ctor,
                SyntaxKind.Constructor,
                source,
                flags) == "constructor(public value: number)"u8);
        Check(
            await checker.SerializeSignatureSyntaxAsync(
                ctor,
                SyntaxKind.Constructor,
                source,
                flags | NodeBuilderFlags.OmitParameterModifiers)
            == "constructor(value: number)"u8);
        Check(await checker.SerializeSignatureSyntaxAsync(signature, SyntaxKind.ArrowFunction, source, flags)
            == "(callback: () => any): any  { }"u8);
        var type = await checker.Nodes.FromNodeAsync(source.Statements!.OfType<TypeAliasDeclarationNode>().Single().Type!);
        Check(await checker.SerializeTypeSyntaxAsync(type, source, flags | NodeBuilderFlags.UseTypeOfFunction) == "typeof f"u8);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            await checker.SerializeSignatureSyntaxAsync(signature, SyntaxKind.FunctionType, source, flags, stop.Token);
            throw new InvalidOperationException("Canceled signature construction completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        var other = await program.CreateCheckerAsync();
        var foreign = await other.Signatures.FromDeclarationAsync(function);
        try
        {
            await checker.SerializeSignatureSyntaxAsync(foreign, SyntaxKind.FunctionType, source, flags);
            throw new InvalidOperationException("Foreign signature accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Check(await checker.SerializeSignatureSyntaxAsync(signature, SyntaxKind.CallSignature, source, flags)
            == "(callback: () => any): any"u8);
        Check(
            checker.TypeSyntaxScopeCount == 0
                && snapshot.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        return checks;
    }

    internal static async Task<int> NamesSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Type syntax names assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        Utf8String deep = Utf8String.Concat(Enumerable.Repeat(Utf8String.Copy("<T>(x:T)=>"u8), 120)) + "typeof globalThis.globalValue"u8;
        Utf8String text = Utf8String.Concat(Utf8String.Concat(Utf8String.Concat("namespace Left{export interface Value{a:number}export interface Other{x:number}}"u8, "namespace Right{export interface Value{b:string}}type Collision=[[Left.Value,Right.Value],Left.Other];"u8, "type A=<T>(x:T)=><T>(y:T)=>[T,typeof x];type Siblings={a:<T>()=>T;b:<T>()=>T};"u8), "declare var globalValue:number;type Deep="u8, deep), ";class Scope<T>{}"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = text.Span.ToArray()
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        var declarations = source.Statements!.OfType<TypeAliasDeclarationNode>().ToArray();
        var collision = await checker.Nodes.FromNodeAsync(declarations[0].Type!);
        var a = await checker.Nodes.FromNodeAsync(declarations[1].Type!);
        var siblings = await checker.Nodes.FromNodeAsync(declarations[2].Type!);
        var nested = await checker.Nodes.FromNodeAsync(declarations[3].Type!);
        var scope = source.Statements!.OfType<ClassDeclarationNode>().Single();
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        const NodeBuilderFlags flags = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation | NodeBuilderFlags.InTypeAlias;
        const NodeBuilderFlags generated = flags | NodeBuilderFlags.GenerateNamesForShadowedTypeParams;
        Check(await checker.SerializeTypeSyntaxAsync(collision, null, flags) == "[[Left.Value, Right.Value], Other]"u8);
        Utf8String renamed = await checker.SerializeTypeSyntaxAsync(a, scope, generated);
        Check(renamed == "<T_1>(x: T_1) => <T_2>(y: T_2) => [T_2, typeof x]"u8);
        Check(await checker.SerializeTypeSyntaxAsync(siblings, scope, generated) == "{ a: <T_1>() => T_1; b: <T_1>() => T_1; }"u8);
        Check(await checker.SerializeTypeSyntaxAsync(a, scope, flags) == "<T>(x: T) => <T>(y: T) => [T, typeof x]"u8);
        using var stop = new CancellationTokenSource();
        checker.BeforeSymbolChainTable = _ =>
        {
            if (checker.TypeSyntaxScopeCount != 0)
            {
                stop.Cancel();
                stop.Token.ThrowIfCancellationRequested();
            }
        };
        try
        {
            await checker.SerializeTypeSyntaxAsync(nested, scope, generated, stop.Token);
            throw new InvalidOperationException("Generated-scope cancellation was not observed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        finally
        {
            checker.BeforeSymbolChainTable = null;
        }
        Check(checker.TypeSyntaxScopeCount == 0);
        int maximumScopes = 0;
        checker.BeforeSymbolChainTable = _ => maximumScopes = Math.Max(maximumScopes, checker.TypeSyntaxScopeCount);
        Utf8String nestedResult;
        try
        {
            nestedResult = await checker.SerializeTypeSyntaxAsync(nested, scope, generated);
        }
        finally
        {
            checker.BeforeSymbolChainTable = null;
        }
        Check(nestedResult.Span.Contains("T_120"u8, StringComparison.Ordinal)
            && nestedResult.Span.EndsWith("typeof globalThis.globalValue"u8, StringComparison.Ordinal));
        Check(maximumScopes == 2 && checker.TypeSyntaxScopeCount == 0);
        Check(await checker.SerializeTypeSyntaxAsync(a, scope, generated) == renamed);
        Check(snapshot.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        return checks;
    }

    internal static async Task<int> OptionsSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Type syntax options assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        Utf8String entries = Utf8String.Join(","u8, Enumerable.Range(0, 40).Select(i => Utf8String.Concat("\"value"u8, Utf8String.Format(i), "\""u8)));
        Utf8String prefix = new('x', 180);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode(
                $"interface Name{{value:number}}type A=[{entries}];type B=[\"{prefix}\",Name];type R=readonly number[];declare const key:unique symbol;type U=typeof key;"),
            ["/project/globals.d.ts"u8] = Wtf8.Encode(
                "interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly [n:number]:T}")
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8, "/project/globals.d.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        var declarations = source.Statements!.OfType<TypeAliasDeclarationNode>().ToArray();
        var a = await checker.Nodes.FromNodeAsync(declarations[0].Type!);
        var b = await checker.Nodes.FromNodeAsync(declarations[1].Type!);
        var array = await checker.Nodes.FromNodeAsync(declarations[2].Type!);
        var unique = await checker.Nodes.FromNodeAsync(declarations[3].Type!);
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        const NodeBuilderFlags flags = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.InTypeAlias;
        Utf8String shortened = await checker.SerializeTypeSyntaxAsync(a, source, flags);
        Check(shortened.Span.Contains(" more ..."u8, StringComparison.Ordinal) && shortened.Span.EndsWith("\"value39\"]"u8, StringComparison.Ordinal));
        Utf8String full = await checker.SerializeTypeSyntaxAsync(a, source, flags | NodeBuilderFlags.NoTruncation);
        Check(!full.Span.Contains("..."u8, StringComparison.Ordinal) && full.Span.Contains("\"value20\""u8, StringComparison.Ordinal));
        Check(await checker.SerializeTypeSyntaxAsync(a, source, flags) == shortened);
        Check(
            await checker.SerializeTypeSyntaxAsync(
                array,
                source,
                flags | NodeBuilderFlags.WriteArrayAsGenericType) == "ReadonlyArray<number>"u8);
        Check(await checker.SerializeTypeSyntaxAsync(unique, source, flags | NodeBuilderFlags.AllowUniqueESSymbolType) == "unique symbol"u8);
        using var stop = new CancellationTokenSource();
        checker.BeforeSymbolChainTable = _ =>
        {
            stop.Cancel();
            stop.Token.ThrowIfCancellationRequested();
        };
        try
        {
            await checker.SerializeTypeSyntaxAsync(b, source, flags, stop.Token);
            throw new InvalidOperationException("Cancellation after crossing the length threshold was not observed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        finally
        {
            checker.BeforeSymbolChainTable = null;
        }
        Check(await checker.SerializeTypeSyntaxAsync(a, source, flags | NodeBuilderFlags.NoTruncation) == full);
        Check(checker.TypeSyntaxScopeCount == 0);
        try
        {
            await checker.SerializeTypeSyntaxAsync(a, source, flags | (NodeBuilderFlags)(1u << 31));
            throw new InvalidOperationException("An unknown node-builder option was accepted");
        }
        catch (NotSupportedException)
        {
            checks++;
        }
        Check(snapshot.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        return checks;
    }

    internal static async Task<int> SignatureSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Signature syntax assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("strict"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode(
                "declare var x:number;type A=(x:number)=>typeof globalThis.x;type M<T>={readonly [K in keyof T]?:T[K]};")
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        var declarations = source.Statements!.OfType<TypeAliasDeclarationNode>().ToArray();
        var type = await checker.Nodes.FromNodeAsync(declarations[0].Type!);
        var mapped = await checker.Nodes.FromNodeAsync(declarations[1].Type!);
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        using var stop = new CancellationTokenSource();
        bool canceledInScope = false;
        checker.BeforeSymbolChainTable = _ =>
        {
            if (checker.TypeSyntaxScopeCount != 0)
            {
                canceledInScope = true;
                stop.Cancel();
                stop.Token.ThrowIfCancellationRequested();
            }
        };
        try
        {
            await checker.SerializeTypeSyntaxAsync(type, source, true, cancellation: stop.Token);
            throw new InvalidOperationException("Mid-signature cancellation was not observed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        finally
        {
            checker.BeforeSymbolChainTable = null;
        }
        Check(canceledInScope);
        Check(checker.TypeSyntaxScopeCount == 0);
        Check(snapshot.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        Check(await checker.SerializeTypeSyntaxAsync(type, source, true) == "(x: number) => typeof globalThis.x"u8);
        int cached = checker.AccessibleChainCacheCount;
        Check(await checker.SerializeTypeSyntaxAsync(type, source, true) == "(x: number) => typeof globalThis.x"u8);
        Check(checker.AccessibleChainCacheCount == cached && checker.TypeSyntaxScopeCount == 0);
        Check(await checker.SerializeTypeSyntaxAsync(mapped, source, true) == "{ readonly [K in keyof T]?: T[K] | undefined; }"u8);
        Check(snapshot.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        return checks;
    }

    internal static async Task<int> ConditionalSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Conditional syntax assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("strict"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode(
                "type A<T>=T extends (infer U)[] ? U : never;type B<T>=T extends {value:infer V extends string} ? V : never;type C={x?:'a'|'b'};"),
            ["/project/globals.d.ts"u8] = Wtf8.Encode(
                "interface Array<T>{length:number;[n:number]:T;}interface ReadonlyArray<T>{readonly length:number;readonly [n:number]:T;}")
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8, "/project/globals.d.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        var declarations = source.Statements!.OfType<TypeAliasDeclarationNode>().ToArray();
        var a = await checker.Nodes.FromNodeAsync(declarations[0].Type!);
        var b = await checker.Nodes.FromNodeAsync(declarations[1].Type!);
        var c = await checker.Nodes.FromNodeAsync(declarations[2].Type!);
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        Check(await checker.SerializeTypeSyntaxAsync(a, source, true) == "T extends (infer U)[] ? U : never"u8);
        Check(await checker.SerializeTypeSyntaxAsync(b, source, true) == "T extends { value: infer V extends string; } ? V : never"u8);
        Check(await checker.SerializeTypeSyntaxAsync(c, source, true) == "{ x?: 'a' | 'b'; }"u8);
        Check(await checker.SerializeTypeSyntaxAsync(c, null, true) == "{ x?: \"a\" | \"b\" | undefined; }"u8);
        Check(snapshot.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        var inferredNode = source.DescendantsAndSelf().OfType<InferTypeNode>().First().TypeParameter!;
        var inferred = await checker.Declared.GetAsync(checker.Symbols.Declaration(inferredNode)!);
        Check(await checker.SerializeTypeSyntaxAsync(inferred, source) == "U"u8);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            await checker.SerializeTypeSyntaxAsync(b, source, true, cancellation: stop.Token);
            throw new InvalidOperationException("Canceled conditional syntax completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(await checker.SerializeTypeSyntaxAsync(a, source, true) == "T extends (infer U)[] ? U : never"u8);
        Check(snapshot.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        return checks;
    }

    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Type syntax assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode("type A='é';type B=[number,string?];type C=number[];"),
            ["/project/globals.d.ts"u8] = Wtf8.Encode(
                "interface Array<T>{length:number;[n:number]:T;}interface ReadonlyArray<T>{readonly length:number;readonly [n:number]:T;}")
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8, "/project/globals.d.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        var nodes = source.Statements!.OfType<TypeAliasDeclarationNode>().Select(n => n.Type!).ToArray();
        var a = await checker.Nodes.FromNodeAsync(nodes[0]);
        var b = await checker.Nodes.FromNodeAsync(nodes[1]);
        var c = await checker.Nodes.FromNodeAsync(nodes[2]);
        Check(await checker.SerializeTypeSyntaxAsync(a) == "\"é\""u8);
        Check(await checker.SerializeTypeSyntaxAsync(b, source, expandAlias: true) == "[number, (string | undefined)?]"u8);
        Check(await checker.SerializeTypeSyntaxAsync(c, source, expandAlias: true) == "number[]"u8);
        Check(await checker.SerializeTypeSyntaxAsync(b, source) == "B"u8);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            await checker.SerializeTypeSyntaxAsync(b, source, cancellation: stop.Token);
            throw new InvalidOperationException("Canceled type syntax completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(await checker.SerializeTypeSyntaxAsync(b, source, expandAlias: true) == "[number, (string | undefined)?]"u8);
        try
        {
            await checker.SerializeTypeSyntaxAsync(new TypeContext().StringType, source);
            throw new InvalidOperationException("Foreign type accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        try
        {
            await checker.SerializeTypeSyntaxAsync(a, Parser.ParseSourceFile(new("/foreign.ts"u8), new SourceText(""u8)));
            throw new InvalidOperationException("Foreign syntax accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Check(await checker.SerializeTypeSyntaxAsync(a) == "\"é\""u8);
        return checks;
    }

    internal static async Task WriteSignaturesAsync(
        Utf8JsonWriter writer,
        SyntaxNode[] nodes,
        Checker checker,
        Func<SyntaxNode?, int> nodeId)
    {
        var main = nodes.Where(
            n => SemanticSyntax.Source(n)?.FileName.StartsWith("/project/main."u8, StringComparison.Ordinal) == true).ToArray();
        SyntaxNode?[] locations = [null, .. main.Where(n => n is SourceFileNode or ClassDeclarationNode or FunctionDeclarationNode)];
        var targets = main.Where(n => Signatures.FunctionLike(n) && (SemanticSyntax.Name(n) is IdentifierNode name
            && name.Text.Span.StartsWith("serialize"u8, StringComparison.Ordinal) || n.Parent is ClassDeclarationNode { Name: { } parentName }
            && parentName.Text.Span.StartsWith("Serialize"u8, StringComparison.Ordinal)));
        SyntaxKind[] kinds = [SyntaxKind.CallSignature,SyntaxKind.ConstructSignature,SyntaxKind.FunctionType,SyntaxKind.ConstructorType,
            SyntaxKind.MethodSignature,SyntaxKind.MethodDeclaration,SyntaxKind.Constructor,SyntaxKind.GetAccessor,SyntaxKind.SetAccessor,
            SyntaxKind.IndexSignature,SyntaxKind.FunctionDeclaration,SyntaxKind.FunctionExpression,SyntaxKind.ArrowFunction];
        NodeBuilderFlags[] flags = [NodeBuilderFlags.NoTruncation,NodeBuilderFlags.NoTruncation|NodeBuilderFlags.OmitParameterModifiers,
            NodeBuilderFlags.NoTruncation|NodeBuilderFlags.SuppressAnyReturnType,NodeBuilderFlags.NoTruncation|NodeBuilderFlags.OmitThisParameter,
            NodeBuilderFlags.NoTruncation|NodeBuilderFlags.GenerateNamesForShadowedTypeParams];
        writer.WriteStartArray("typeSyntaxQueries"u8);
        foreach (var target in targets)
        {
            var signature = await checker.Signatures.FromDeclarationAsync(target);
            foreach (var location in locations)
                foreach (var kind in kinds)
                    foreach (var flag in flags)
                    {
                        Utf8String value = await checker.SerializeSignatureSyntaxAsync(
                            signature,
                            kind,
                            location,
                            flag | NodeBuilderFlags.IgnoreErrors);
                        writer.WriteStartArray();
                        writer.WriteNumberValue(nodeId(target));
                        writer.WriteNumberValue(nodeId(location));
                        writer.WriteNumberValue((int)kind);
                        writer.WriteNumberValue((uint)flag);
                        writer.WriteStringValue(value.Span);
                        writer.WriteEndArray();
                    }
        }
        writer.WriteEndArray();
    }

    internal static async Task WriteAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, Checker checker, Func<SyntaxNode?, int> nodeId,
        IReadOnlyList<NodeBuilderFlags>? configuredFlags = null)
    {
        var main = nodes.Where(
            n => SemanticSyntax.Source(n)?.FileName.StartsWith("/project/main."u8, StringComparison.Ordinal) == true).ToArray();
        SyntaxNode?[] locations = [null, .. main.Where(n => n is SourceFileNode or ClassDeclarationNode or FunctionDeclarationNode)];
        writer.WriteStartArray("typeSyntaxQueries"u8);
        foreach (var target in main.OfType<TypeAliasDeclarationNode>().Where(
            n => n.Name?.Text.Span.StartsWith("Serialize"u8, StringComparison.Ordinal) == true).Select(n => n.Type!))
        {
            var type = await checker.Nodes.FromNodeAsync(target);
            foreach (var location in locations)
                foreach (bool expand in new[] { false, true })
                    foreach (bool outside in new[] { false, true })
                        foreach (var configured in configuredFlags ?? [NodeBuilderFlags.NoTruncation])
                        {
                            Utf8String value = await checker.SerializeTypeSyntaxAsync(type, location, configured | NodeBuilderFlags.IgnoreErrors
                                | (expand
                                    ? NodeBuilderFlags.InTypeAlias
                                    : 0) | (outside ? NodeBuilderFlags.UseAliasDefinedOutsideCurrentScope : 0));
                            writer.WriteStartArray();
                            writer.WriteNumberValue(nodeId(target));
                            writer.WriteNumberValue(nodeId(location));
                            writer.WriteBooleanValue(expand);
                            writer.WriteBooleanValue(outside);
                            if (configuredFlags is not null)
                                writer.WriteNumberValue((uint)configured);
                            writer.WriteStringValue(value.Span);
                            writer.WriteEndArray();
                        }
        }
        writer.WriteEndArray();
    }
}

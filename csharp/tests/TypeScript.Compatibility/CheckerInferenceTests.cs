using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerInferenceTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Inference assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}type Bound<U extends string>=U;type Box<T>={[P in keyof T]:{value:T[P]}};type Unbox<T>=T extends Box<infer U>?U:never;type Result=Unbox<{a:{value:1};b:{value:string}}>";
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
        var p = context.NewTypeParameter();
        var inference = host.Inference.Create([p]);
        await host.Inference.InferAsync(inference, context.StringType, p, InferencePriority.ReturnType);
        Check(await host.Inference.GetAsync(inference, 0) == context.StringType);
        await host.Inference.InferAsync(inference, context.NumberType, p);
        Check(inference.Inferences[0].Priority == 0 && inference.Inferences[0].Candidates.SequenceEqual([context.NumberType]));
        Check(inference.Inferences[0].InferredType is null && await host.Inference.GetAsync(inference, 0) == context.NumberType);
        await host.Inference.InferAsync(inference, context.StringType, p, InferencePriority.NakedTypeVariable);
        Check(inference.Inferences[0].Candidates.SequenceEqual([context.NumberType]));
        var clone = host.Inference.Clone(inference);
        await host.Inference.InferAsync(clone, context.StringType, p);
        Check(clone.Inferences[0].Candidates.Count == 2 && inference.Inferences[0].Candidates.Count == 1);
        Check(await host.Inference.GetAsync(clone, 0) == context.StringOrNumberType);
        Check(await inference.Mapper.MapAsync(p) == context.NumberType && inference.Inferences[0].IsFixed);
        await host.Inference.InferAsync(inference, context.BooleanType, p);
        Check(inference.Inferences[0].Candidates.Count == 1 && await host.Inference.GetAsync(inference, 0) == context.NumberType);
        Check(host.Inference.CloneInferred(host.Inference.Create([p])) is null);
        Check(host.Inference.CloneInferred(inference)?.Inferences.Length == 1);
        var contra = host.Inference.Create([p]);
        await host.Inference.InferAsync(contra, context.StringType, p, contravariant: true);
        await host.Inference.InferAsync(contra, context.NumberType, p, contravariant: true);
        Check(contra.Inferences[0].ContraCandidates.Count == 2 && contra.Inferences[0].Candidates.Count == 0);
        Check(await host.Inference.GetAsync(contra, 0) == context.NeverType);
        var blocked = host.Inference.Create([p]);
        await host.Inference.InferAsync(blocked, context.NonInferrableAnyType, p);
        await host.Inference.InferAsync(blocked, context.SilentNeverType, p);
        await host.Inference.InferAsync(blocked, context.BlockedStringType, p);
        Check(!blocked.Inferences[0].HasCandidates && await host.Inference.GetAsync(blocked, 0) == context.UnknownType);
        var wildcard = host.Inference.Create([p]);
        await host.Inference.InferAsync(wildcard, context.WildcardType, p);
        Check(await host.Inference.GetAsync(wildcard, 0) == context.WildcardType);
        var bound = (TypeParameter)await host.Declared.GetAsync(symbols.Globals["Bound"]);
        var cancellable = host.Inference.Create([bound]);
        await host.Inference.InferAsync(cancellable, context.StringType, bound);
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await cancellable.Mapper.MapAsync(bound);
            throw new InvalidOperationException("Inference cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(!cancellable.Inferences[0].IsFixed && cancellable.Inferences[0].InferredType is null);
        Check(cancellable.Inferences[0].Candidates.SequenceEqual([context.StringType]));
        host.BeforeNode = null;
        Check(await cancellable.Mapper.MapAsync(bound) == context.StringType);
        Check(cancellable.Inferences[0].IsFixed && host.Instantiation.Resolutions.Count == 0);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await host.Inference.GetAsync(cancellable, 0, cancelled.Token);
            throw new InvalidOperationException("Cached inference ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(await host.Inference.GetAsync(cancellable, 0) == context.StringType);
        int asyncCalls = 0;
        var asynchronous = TypeMapper.FunctionAsync(async (type, token) =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            asyncCalls++;
            return type == p ? context.StringType : type;
        });
        Check(
            await TypeMapper.Merge(
                asynchronous,
                TypeMapper.Create([context.StringType], [context.NumberType])).MapAsync(p) == context.NumberType
                && asyncCalls == 1);

        var fresh = context.GetFreshLiteralType(context.GetStringLiteralType("value"));
        Check(await host.Widening.LiteralAsync(fresh) == context.StringType);
        Check(await host.Widening.LiteralAsync(fresh.RegularType) == fresh.RegularType);
        Check(await host.Widening.GetAsync(context.UndefinedType) == context.UndefinedType);
        var literal = context.NewObjectType(
            ObjectFlags.Anonymous | ObjectFlags.ObjectLiteral | ObjectFlags.ContainsObjectOrArrayLiteral | ObjectFlags.ContainsWideningType);
        var property = new Symbol(SymbolFlags.Property | SymbolFlags.Transient, "value");
        links.Values.Get(property).ResolvedType = context.NonInferrableAnyType;
        literal.Members = new Dictionary<TextSlice, Symbol> { ["value"] = property }.AsReadOnly();
        literal.Properties = [property];
        literal.ObjectFlags |= ObjectFlags.MembersResolved;
        var widened = await host.Widening.GetAsync(literal);
        Check(widened != literal && (widened.ObjectFlags & ObjectFlags.ObjectLiteral) == 0);
        Check(await host.Widening.GetAsync(literal) == widened);
        var widenedProperty = await host.Properties.PropertyAsync(widened, "value");
        Check(widenedProperty is not null && await host.Values.GetAsync(widenedProperty) == context.AnyType);
        Check(await host.Values.GetAsync(property) == context.NonInferrableAnyType);
        var reversed = await host.Declared.GetAsync(symbols.Globals["Result"]);
        Check(reversed is ReverseMappedType);
        Check(await host.Indexed.GetAsync(reversed, context.GetStringLiteralType("a")) == context.GetNumberLiteralType(1));
        Check(await host.Indexed.GetAsync(reversed, context.GetStringLiteralType("b")) == context.StringType);
        try
        {
            host.Inference.Create([new TypeContext().StringType]);
            throw new InvalidOperationException("Foreign inference parameter accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Type deep = p;
        for (int i = 0; i < 20_000; i++)
            deep = context.NewIntersectionType([deep]);
        Check(await host.Inference.TopLevelAsync(deep, p));
        Check(!await host.Inference.ConstVariableAsync(deep));
        checks += await ConditionalCallSafety();
        Console.WriteLine($"{checks} inference/priority/fixing/cancellation/widening/reverse-map assertions; 20000-level traversal");
    }

    private static async Task<int> ConditionalCallSafety()
    {
        const string source = """
            declare const f: <T>(f: (x: T) => unknown) => (x: T) => unknown;
            declare const g: <T extends unknown>(x: { foo: T }) => unknown;
            const h = f(g);
            type FirstParameter<T> = T extends (x: infer P) => unknown ? P : unknown;
            type X = FirstParameter<typeof h>["foo"];
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.SourceFiles[0].Syntax;
        await checker.CheckSourceFileAsync(file);
        if (checker.DiagnosticCodesForFile(file).Count != 0)
            throw new InvalidOperationException("Conditional call inference reported an error");
        var alias = file.DescendantsAndSelf().OfType<TypeAliasDeclarationNode>().Single(n => n.Name!.Text == "X");
        if (await checker.Nodes.FromNodeAsync(alias.Type!) != checker.Context.UnknownType)
            throw new InvalidOperationException("Conditional inference lost its unknown result");
        return 2;
    }
}

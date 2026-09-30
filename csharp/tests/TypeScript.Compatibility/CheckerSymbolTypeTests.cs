using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerSymbolTypeTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Symbol type assertion {checks + 1}");
            checks++;
        }
        Utf8String text = "interface I<T> { get value():T; set value(v:T|undefined); p?:T } type S=I<string>; declare const number:number; declare function f(x:number):void; class C<T> { value:T } namespace N { export const name:string; export const other:number; } import A=N.name; import Retry=N.other;"u8;
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("strict"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(
            new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/project/main.ts"u8] = text.Span.ToArray() }),
            "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scope = new CheckerEnvironment(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scope);
        var host = new Checker(context, links, scope);
        var instance = (ObjectType)await host.Declared.GetAsync(symbols.Globals["S"u8]);
        await host.Members.ResolveAsync(instance);
        var accessor = instance.Properties!.Single(p => p.Name == "value"u8);
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Values.GetAsync(accessor);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(links.Values.Get(accessor).ResolvedType is null && host.Instantiation.Resolutions.Count == 0);
        host.BeforeNode = null;
        Check(await host.Values.GetAsync(accessor) == context.StringType);
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Values.WriteAsync(accessor);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(links.Values.Get(accessor).WriteType is null && host.Instantiation.Resolutions.Count == 0);
        host.BeforeNode = null;
        var write = await host.Values.WriteAsync(accessor);
        Check(write is UnionType union && union.Types.Contains(context.StringType) && union.Types.Contains(context.UndefinedType));
        Check(await host.Values.WriteAsync(accessor) == write);
        var optional = instance.Properties!.Single(p => p.Name == "p"u8);
        Check(await host.Values.GetAsync(optional) is UnionType missing && missing.Types.Contains(context.MissingType));
        Check(await host.Values.WriteAsync(optional) == context.StringType);
        Check(await host.Values.GetAsync(symbols.Globals["A"u8]) == context.StringType);
        Check(links.Values.Get(symbols.Globals["A"u8]).ResolvedType == context.StringType);
        var retry = symbols.Globals["Retry"u8];
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Values.GetAsync(retry);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(links.Values.Get(retry).ResolvedType is null && host.Instantiation.Resolutions.Count == 0);
        host.BeforeNode = null;
        Check(await host.Values.GetAsync(retry) == context.NumberType);
        var prototype = symbols.Globals["C"u8].Exports["prototype"u8];
        var prototypeType = (TypeReference)await host.Values.GetAsync(prototype);
        Check(prototypeType.ResolvedTypeArguments!.SequenceEqual([context.AnyType]));
        Check(await host.Values.GetAsync(symbols.RequireSymbol) == context.AnyType);

        var deferred = new Symbol(
            SymbolFlags.Property | SymbolFlags.Transient,
            "deferred"u8)
        { CheckFlags = CheckFlags.SyntheticProperty | CheckFlags.DeferredType };
        var deferredLinks = links.DeferredSymbols.Get(deferred);
        deferredLinks.Parent = context.NewUnionType([context.StringType, context.NumberType]);
        deferredLinks.Constituents = [context.StringType, context.NumberType];
        deferredLinks.WriteConstituents = [context.BooleanType];
        Check(await host.Values.GetAsync(deferred) is UnionType read && read.Types.Count == 2);
        Check(await host.Values.WriteAsync(deferred) == context.BooleanType);
        var intersection = new Symbol(
            SymbolFlags.Property | SymbolFlags.Transient,
            "intersection"u8)
        { CheckFlags = CheckFlags.SyntheticProperty | CheckFlags.DeferredType };
        var intersectionLinks = links.DeferredSymbols.Get(intersection);
        intersectionLinks.Parent = context.NewIntersectionType([context.StringType, context.NumberType]);
        intersectionLinks.Constituents = [context.StringType, context.NumberType];
        Check(await host.Values.GetAsync(intersection) == context.NeverType);
        Check(await host.Values.WriteAsync(intersection) == context.NeverType);

        var variable = symbols.Globals["number"u8];
        host.VariableBody = (symbol, _, token) => host.Values.GetAsync(symbol, token);
        Check(
            await host.Values.GetAsync(variable) == context.ErrorType
                && host.Diagnostics.Contains(DiagnosticCode.X0IsReferencedDirectlyOrIndirectlyInItsOwnTypeAnnotation));
        Check(host.Instantiation.Resolutions.Count == 0);
        links.Values.Get(variable).ResolvedType = null;
        using var cancellation = new CancellationTokenSource();
        host.VariableBody = (_, _, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult<Type>(context.NumberType);
        };
        try
        {
            await host.Values.GetAsync(variable, cancellation.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(links.Values.Get(variable).ResolvedType is null && host.Instantiation.Resolutions.Count == 0);
        host.VariableBody = null;
        Check(await host.Values.GetAsync(variable) == context.NumberType);

        var parameter = symbols.Globals["f"u8].Declarations.OfType<FunctionDeclarationNode>().Single().Parameters![0];
        var parameterSymbol = symbols.Binding(parameter)!.Get(parameter)!.Value.Symbol!;
        host.SensitiveParameter = symbol => symbol == parameterSymbol;
        host.VariableBody = (_, reportErrors, _) =>
        {
            Check(!reportErrors);
            return ValueTask.FromResult<Type>(context.NumberType);
        };
        Check(await host.Values.GetAsync(parameterSymbol) == context.NumberType && links.Values.Get(parameterSymbol).ResolvedType is null);
        host.VariableBody = (_, reportErrors, _) =>
        {
            links.Values.Get(parameterSymbol).ResolvedType = context.StringType;
            return ValueTask.FromResult<Type>(context.NumberType);
        };
        Check(await host.Values.GetAsync(parameterSymbol) == context.NumberType);
        Check(await host.Values.GetAsync(parameterSymbol) == context.StringType);
        host.VariableBody = null;
        host.SensitiveParameter = null;

        Symbol deepest = variable;
        for (int i = 0; i < 20_000; i++)
        {
            var next = new Symbol(SymbolFlags.Property | SymbolFlags.Transient, "instantiated"u8) { CheckFlags = CheckFlags.Instantiated };
            links.Values.Get(next).Target = deepest;
            deepest = next;
        }
        Check(await host.Values.GetAsync(deepest) == context.NumberType);
        Check(await host.Values.WriteAsync(deepest) == context.NumberType);
        Check(host.Instantiation.Resolutions.Count == 0);
        Console.WriteLine($"{checks} symbol read/write/cache/cancellation assertions; instantiated symbol chain depth 20000");
    }
}

using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerConditionalTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Conditional assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}type A<T>=T extends string?{value:T}:number;type B<T extends string>=T extends 'a'?1:2;type Loop<T>=T extends string?Loop<T>:number;";
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
        var a = (ConditionalType)await host.Declared.GetAsync(symbols.Globals["A"]);
        var parameter = a.Root.OuterTypeParameters![0];
        var mapper = TypeMapper.Create([parameter], [context.StringType]);
        int before = a.Root.Instantiations!.Count;
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Conditionals.InstantiateAsync(a, mapper);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(
            a.Root.Instantiations.Count == before && host.Instantiation.Engine.Depth == 0 && host.Instantiation.Engine.ActiveMappers == 0);
        host.BeforeNode = null;
        var value = await host.Conditionals.InstantiateAsync(a, mapper);
        Check(value is ObjectType);
        Check(await host.Conditionals.InstantiateAsync(a, mapper) == value);
        Check(a.Root.Instantiations.Count == before + 1);
        var numeric = TypeMapper.Create([parameter], [context.NumberType]);
        Check(await host.Conditionals.InstantiateAsync(a, numeric) == context.NumberType);
        Check(await host.Conditionals.InstantiateAsync(a, TypeMapper.Create([parameter], [context.NeverType])) == context.NeverType);
        Check(await host.Instantiation.Constraints.ConditionalFalseAsync(a) == context.NumberType);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await host.Conditionals.InstantiateAsync(a, mapper, cancellation: cancellation.Token);
            throw new InvalidOperationException("Cached instantiation ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        try
        {
            await host.Instantiation.Constraints.ConditionalFalseAsync(a, cancellation.Token);
            throw new InvalidOperationException("Cached branch ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(await host.Conditionals.InstantiateAsync(a, mapper) == value);
        var b = (ConditionalType)await host.Declared.GetAsync(symbols.Globals["B"]);
        var restrictive = await host.Instantiation.Engine.RestrictiveAsync(b);
        Check(host.Instantiation.Engine.IsRestrictive(restrictive));
        Check(b.Root.OuterTypeParameters![0].Constraint != context.NoConstraintType);
        Check(await host.Instantiation.Engine.PermissiveAsync(b) == context.WildcardType);
        var loop = (ConditionalType)await host.Declared.GetAsync(symbols.Globals["Loop"]);
        int tailIterations = 0;
        host.BeforeNode = node =>
        {
            if (node == loop.Root.Node.TrueType)
                tailIterations++;
        };
        var looping = TypeMapper.Create([loop.Root.OuterTypeParameters![0]], [context.StringType]);
        Check(await host.Conditionals.InstantiateAsync(loop, looping) == context.ErrorType);
        Check(
            tailIterations == 1000
                && host.Diagnostics.Count(c => c == DiagnosticCode.TypeInstantiationIsExcessivelyDeepAndPossiblyInfinite) == 1);
        Check(await host.Conditionals.InstantiateAsync(loop, looping) == context.ErrorType && tailIterations == 1000);
        host.BeforeNode = null;
        Check(
            host.Instantiation.Engine.Depth == 0
                && host.Instantiation.Engine.ActiveMappers == 0
                && host.Instantiation.Resolutions.Count == 0);

        var nested = Enumerable.Range(0, 10).Select(_ => new ConditionalType(context, a.Root, a.CheckType, a.ExtendsType)).ToArray();
        var recursion = new TypeRecursion(async (type, token) => await host.Instantiation.Members.ModifiersTypeAsync(type, token));
        Check(!await recursion.IsDeeplyNestedAsync(nested[^1], nested.Take(9).Cast<Type>().ToArray(), 10));
        Check(await recursion.IsDeeplyNestedAsync(nested[^1], nested, 10));
        foreach (bool target in new[] { false, true })
        {
            var session = new RelationSession(context, new(RelationKind.Assignable), host.RelationKeys, recursion, host.Relations.State);
            var operation = new RelationOperation(context, host.Relations, session, host.Normalization, host, RelationKind.Assignable);
            async ValueTask<Ternary> Nest(int depth)
                => await session.RecursiveAsync(target ? context.NumberType : nested[depth], target ? nested[depth] : context.NumberType,
                    0, target ? RecursionFlags.Target : RecursionFlags.Source, false,
                    async () => depth < 9 ? await Nest(depth + 1) : target
                        ? (await host.ConditionalRelations.TargetAsync(operation, context.NumberType, nested[depth], 0))!.Value
                        : await host.ConditionalRelations.SourceAsync(operation, nested[depth], context.NumberType),
                    (_, _, _) => throw new InvalidOperationException("Conditional nesting overflowed"));
            Check(await Nest(0) == Ternary.Maybe);
            Check(session.SourceStack.Count == 0 && session.TargetStack.Count == 0 && session.PendingCount == 0);
        }
        Console.WriteLine($"{checks} conditional/cache/cancellation assertions; exact 1000 tail and 10 relation nesting limits");
    }
}

using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerGenericRelationTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Generic relation assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}type A<K extends string>={[P in K]:number};type B<K extends string>={[Q in K]:number};type C<K extends string>={[P in K]?:number};type D<K extends string>={[P in K]:string};interface I{a:string}";
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
        var parameter = context.NewTypeParameter();
        parameter.Constraint = context.StringType;
        async ValueTask<Type> Instantiate(string name) => await host.References.AliasInstantiationAsync(symbols.Globals[name], [parameter]);
        var a = await Instantiate("A");
        var b = await Instantiate("B");
        var optional = await Instantiate("C");
        var different = await Instantiate("D");
        int before = host.Relations.Cache(RelationKind.Assignable).Count;
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Relations.RelatedAsync(a, b, RelationKind.Assignable);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(host.Relations.Cache(RelationKind.Assignable).Count == before);
        Check(host.Instantiation.Resolutions.Count == 0 && !host.Variances.Measuring);
        host.BeforeNode = null;
        foreach (var kind in Enum.GetValues<RelationKind>())
            Check(await host.Relations.RelatedAsync(a, b, kind));
        Check(await host.Relations.RelatedAsync(a, optional, RelationKind.Assignable));
        Check(!await host.Relations.RelatedAsync(optional, a, RelationKind.Assignable));
        Check(!await host.Relations.RelatedAsync(a, optional, RelationKind.Identity));
        Check(await host.Relations.RelatedAsync(optional, a, RelationKind.Comparable));
        Check(!await host.Relations.RelatedAsync(a, different, RelationKind.Assignable));
        int cached = host.Relations.Cache(RelationKind.Assignable).Count;
        Check(await host.Relations.RelatedAsync(a, b, RelationKind.Assignable));
        Check(host.Relations.Cache(RelationKind.Assignable).Count == cached);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await host.Relations.RelatedAsync(a, b, RelationKind.Assignable, cancellation.Token);
            throw new InvalidOperationException("Cached relation ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(host.Relations.Cache(RelationKind.Assignable).Count == cached);
        var p = context.NewTypeParameter();
        p.Constraint = await host.Declared.GetAsync(symbols.Globals["I"]);
        var q = context.NewTypeParameter();
        q.Constraint = p;
        var pKeys = await host.Keys.GetAsync(p);
        var qKeys = await host.Keys.GetAsync(q);
        Check(await host.Relations.RelatedAsync(pKeys, qKeys, RelationKind.Assignable));
        Check(!await host.Relations.RelatedAsync(qKeys, pKeys, RelationKind.Assignable));
        var access = await host.Indexed.GetAsync(p, context.GetStringLiteralType("a"));
        Check(await host.Relations.RelatedAsync(access, context.StringType, RelationKind.Assignable));
        Check(await host.Relations.RelatedAsync(context.StringType, access, RelationKind.Assignable));
        Check(!await host.Relations.RelatedAsync(context.NumberType, access, RelationKind.Assignable));
        Console.WriteLine($"{checks} generic relation/optionality/key variance/cancellation assertions");
    }
}

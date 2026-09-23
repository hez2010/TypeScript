using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;
using TypeScript.Compiler.Syntax;
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
        var scope = new ProgramScopeHost(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scope);
        var host = new ProgramTypeHost(context, links, scope);
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
        Console.WriteLine($"Checker indexed type safety: {checks} assertions; 20,000-level simplification and key traversal");
    }

    internal static async Task WriteAsync(Utf8JsonWriter writer, IReadOnlyList<SyntaxNode> nodes, ProgramTypeHost host,
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

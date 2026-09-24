using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerAssignabilityTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Assignability assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T;push(...items:T[]):number}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}interface Out<T>{value:T}interface In<T>{accept:(x:T)=>void}interface Both<T>{f:(x:T)=>T}interface Phantom<T>{}type S={first:{a:string};second:{b:number}};type T={first:{a:string};second:{b:string}};type U={kind:'a';value:number}|{kind:'b';value:number};type D={kind:'a'|'b';value:number};type F=(x:string)=>number;type G=(x:'x')=>number;";
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        var program = await CompilerProgram.CreateAsync(
            new MemoryFileSystem(new Dictionary<string, byte[]> { ["/project/main.ts"] = Wtf8.Encode(source) }),
            "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scope = new CheckerEnvironment(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scope);
        var host = new Checker(context, links, scope);
        async ValueTask<Type> Type(string name) => await host.Declared.GetAsync(symbols.Globals[name]);
        var output = (InterfaceType)await Type("Out");
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Variances.OfTypeAsync(output);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(
            host.Variances.Cache.Count == 0
                && !host.Variances.Measuring
                && host.Instantiation.Resolutions.Count == 0
                && host.Instantiation.Resolutions.ResolutionStart == 0);
        host.BeforeNode = null;
        Check((await host.Variances.OfTypeAsync(output)).SequenceEqual([VarianceFlags.Covariant]));
        Check((await host.Variances.OfTypeAsync((InterfaceType)await Type("In"))).SequenceEqual([VarianceFlags.Contravariant]));
        Check((await host.Variances.OfTypeAsync((InterfaceType)await Type("Both"))).SequenceEqual([VarianceFlags.Invariant]));
        Check((await host.Variances.OfTypeAsync((InterfaceType)await Type("Phantom"))).SequenceEqual([VarianceFlags.Independent]));
        var narrow = context.CreateTypeReference(output, [context.GetStringLiteralType("x")]);
        var wide = context.CreateTypeReference(output, [context.StringType]);
        Check(await host.Relations.RelatedAsync(narrow, wide, RelationKind.Assignable));
        Check(!await host.Relations.RelatedAsync(wide, narrow, RelationKind.Assignable));
        var array = context.CreateTypeReference((InterfaceType)host.ArrayTarget(false), [context.StringType]);
        var readonlyArray = context.CreateTypeReference((InterfaceType)host.ArrayTarget(true), [context.StringType]);
        Check(await host.Relations.RelatedAsync(array, readonlyArray, RelationKind.Assignable));
        Check(!await host.Relations.RelatedAsync(readonlyArray, array, RelationKind.Assignable));

        host.Relations.State.Reliability = 0;
        await host.Variances.ReportAsync(context.MarkerSub, false);
        Check(host.Relations.State.Reliability == RelationComparisonResult.ReportsUnreliable);
        await host.Variances.ReportAsync(context.MarkerOther, true);
        Check(host.Relations.State.Reliability == RelationComparisonResult.ReportsMask);
        host.Relations.State.Reliability = 0;

        var s = await Type("S");
        var t = await Type("T");
        int before = host.Relations.Cache(RelationKind.Assignable).Count;
        host.BeforeNode = node =>
        {
            if (node.Kind == SyntaxKind.StringKeyword
                && node.Parent is PropertySignatureDeclarationNode { Name: IdentifierNode { Text: "b" } })
                throw new OperationCanceledException();
        };
        try
        {
            await host.Relations.RelatedAsync(s, t, RelationKind.Assignable);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(host.Relations.Cache(RelationKind.Assignable).Count == before && host.Instantiation.Resolutions.Count == 0);
        host.BeforeNode = null;
        Check(!await host.Relations.RelatedAsync(s, t, RelationKind.Assignable));
        Check(host.Relations.Cache(RelationKind.Assignable).Count > before);

        var union = (UnionType)await Type("U");
        var discriminated = await Type("D");
        Check(await host.Discriminants.PropertyAsync(union, "kind"));
        var discriminant = await host.Properties.CachedPropertyAsync(union, "kind");
        Check(
            (discriminant!.CheckFlags & (CheckFlags.IsDiscriminant | CheckFlags.IsDiscriminantComputed)) == (CheckFlags.IsDiscriminant | CheckFlags.IsDiscriminantComputed));
        Check(await host.Relations.RelatedAsync(discriminated, union, RelationKind.Assignable));
        Check(
            await host.Discriminants.MatchAsync(union, discriminated) is null
                && union.KeyPropertyName == Symbol.InternalPrefix + "missing");
        Check((await host.Facts.GetAsync(context.UnknownType, TypeFacts.IsUndefinedOrNull)) == 0);
        Check(await host.Facts.NonNullableAsync(context.UnknownType) == context.UnknownEmptyObjectType);
        var nullable = await host.Algebra.UnionAsync([context.StringType, context.UndefinedType, context.NullType]);
        Check(await host.Facts.NonNullableAsync(nullable) == context.StringType);
        Check(
            await host.Facts.FilterAsync(nullable, TypeFacts.NEUndefined) is UnionType nonUndefined
                && nonUndefined.Types.Contains(context.NullType)
                && !nonUndefined.Types.Contains(context.UndefinedType));
        Check((await host.Facts.GetAsync(context.GetNumberLiteralType(0), TypeFacts.Falsy | TypeFacts.Truthy)) == TypeFacts.Falsy);
        Check(
            (await host.Facts.GetAsync(context.GetNumberLiteralType(double.NaN), TypeFacts.Falsy | TypeFacts.Truthy)) == TypeFacts.Truthy);

        var f = (ObjectType)await Type("F");
        var g = (ObjectType)await Type("G");
        await host.Members.ResolveAsync(f);
        await host.Members.ResolveAsync(g);
        Check(await host.Relations.RelatedAsync(f, g, RelationKind.Assignable));
        Check(!await host.Relations.RelatedAsync(g, f, RelationKind.Assignable));
        Check(await host.SignatureAssignability.ErasedAsync(f.CallSignatures[0]) == f.CallSignatures[0]);
        var generic = context.NewSignature(0, null, [context.NewTypeParameter()], null, [], context.VoidType, null, 0);
        Check((await host.SignatureAssignability.ErasedAsync(generic)).TypeParameters.Count == 0);
        Check(await host.SignatureAssignability.ErasedAsync(generic) == await host.SignatureAssignability.ErasedAsync(generic));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await host.Variances.OfTypeAsync(output, cancellation.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(!host.Variances.Measuring && host.Instantiation.Resolutions.Count == 0);
        Console.WriteLine($"{checks} structural relation/variance/facts/cancellation assertions");
    }
}

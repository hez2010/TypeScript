using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerPropertyTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Property resolution assertion {checks + 1}");
            checks++;
        }
        Utf8String text = "interface Array<T> { length:number; [n:number]:T } interface ReadonlyArray<T> {} type U={value:string; optional?:number}|{value:number; optional?:string}; type P={value:string}|{}; type I={kind:'a'}&{kind:'b'}; type D={value:string}|{value:number}|{value:boolean}; interface Box<T>{value:T;length:number} type B=Box<string>|Box<number>; type Read={readonly [s:string]:number};"u8;
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        Check(options.StrictOption("strictNullChecks"u8));
        options.SetRaw("strict"u8, "false"u8);
        Check(!options.StrictOption("strictNullChecks"u8));
        options.SetRaw("strictNullChecks"u8, "true"u8);
        Check(options.StrictOption("strictNullChecks"u8));
        var program = await CompilerProgram.CreateAsync(
            new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/project/main.ts"u8] = text.Span.ToArray() }),
            "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scope = new CheckerEnvironment(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scope);
        var host = new Checker(context, links, scope);
        var union = (UnionType)await host.Declared.GetAsync(symbols.Globals["U"u8]);
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Properties.CachedPropertyAsync(union, "value"u8);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(union.PropertyCache is null && union.ResolvedProperties is null && host.Instantiation.Resolutions.Count == 0);
        host.BeforeNode = null;
        var value = await host.Properties.CachedPropertyAsync(union, "value"u8, true);
        Check(value is not null && union.PropertyCacheWithoutFunctionAugment!["value"u8] == value && union.PropertyCache!["value"u8] == value);
        Check(await host.Properties.PropertyAsync(union, "value"u8) == value);
        Check(
            await host.Values.GetAsync(value!) is UnionType valueType
                && valueType.Types.SequenceEqual([context.StringType, context.NumberType]));
        var optional = (await host.Properties.GetAsync(union)).Single(p => p.Name == "optional"u8);
        Check(await host.Values.GetAsync(optional) is UnionType optionalType && optionalType.Types.Contains(context.MissingType));
        Check(await host.Values.WriteAsync(optional) is UnionType writeType && !writeType.Types.Contains(context.MissingType));
        var properties = union.ResolvedProperties;
        Check(ReferenceEquals(await host.Properties.GetAsync(union), properties));

        var partial = (UnionType)await host.Declared.GetAsync(symbols.Globals["P"u8]);
        var raw = await host.Properties.CachedPropertyAsync(partial, "value"u8, true);
        Check(raw is not null && (raw.CheckFlags & CheckFlags.ReadPartial) != 0 && partial.PropertyCache is null);
        Check(await host.Properties.PropertyAsync(partial, "value"u8) is null);
        Check(await host.Properties.CachedPropertyAsync(partial, "value"u8) != raw);
        Check((await host.Properties.GetAsync(partial)).Count == 0);

        var intersection = (IntersectionType)await host.Declared.GetAsync(symbols.Globals["I"u8]);
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Views.ReducedAsync(intersection);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check((intersection.ObjectFlags & ObjectFlags.IsNeverIntersectionComputed) == 0 && intersection.ResolvedProperties is null);
        host.BeforeNode = null;
        Check(await host.Views.ReducedAsync(intersection) == context.NeverType);
        Check((intersection.ObjectFlags & (ObjectFlags.IsNeverIntersectionComputed | ObjectFlags.IsNeverIntersection))
            == (ObjectFlags.IsNeverIntersectionComputed | ObjectFlags.IsNeverIntersection));
        Check((await host.Properties.GetAsync(intersection)).Count == 0);

        var deferred = (UnionType)await host.Declared.GetAsync(symbols.Globals["D"u8]);
        var deferredValue = await host.Properties.CachedPropertyAsync(deferred, "value"u8);
        Check((deferredValue!.CheckFlags & CheckFlags.DeferredType) != 0 && links.Values.Get(deferredValue).ResolvedType is null);
        Check(links.DeferredSymbols.Get(deferredValue).Constituents.Count == 3);
        Check(await host.Values.GetAsync(deferredValue) is UnionType normalized && normalized.Types.Count == 4);
        var generic = (UnionType)await host.Declared.GetAsync(symbols.Globals["B"u8]);
        var length = await host.Properties.PropertyAsync(generic, "length"u8);
        Check(length is not null && links.Values.Get(length).ContainingType == generic && links.Values.Get(length).Target is not null);
        Check(await host.Values.GetAsync(length!) == context.NumberType);

        var prefix = context.NewTemplateLiteralType(["p"u8, ""u8], [context.StringType]);
        var suffix = context.NewTemplateLiteralType([""u8, "x"u8], [context.StringType]);
        var applicable = await host.IndexSignatures.ApplicableAsync([
            context.NewIndexInfo(context.StringType, context.AnyType), context.NewIndexInfo(prefix, context.StringType, true),
            context.NewIndexInfo(suffix, context.NumberType, true)], context.GetStringLiteralType("px"u8));
        Check(applicable is { IsReadonly: true } && applicable.KeyType == context.UnknownType && applicable.ValueType == context.NeverType);
        foreach (var name in new Utf8String[] { "0"u8, "1"u8, "-1"u8, "Infinity"u8, "-Infinity"u8, "NaN"u8, "1e+21"u8 })
            Check(IndexSignatures.NumericName(name));
        foreach (var name in new Utf8String[] { "01"u8, "-0"u8, " 1"u8, "0x10"u8, "1e21"u8, ""u8 })
            Check(!IndexSignatures.NumericName(name));
        Check(await host.Views.EmptyAnonymousAsync(context.EmptyObjectType));
        Check(!await host.Views.EmptyObjectAsync(context.AnyFunctionType));
        Check(await host.Views.EmptyObjectAsync(context.NonPrimitiveType));

        var empty = context.NewObjectType(ObjectFlags.Anonymous, new(SymbolFlags.TypeLiteral, "empty"u8));
        var unknownLike = context.NewUnionType([context.UndefinedType, context.NullType, empty]);
        host.BeforeMemberTable = _ =>
        {
            Check((unknownLike.ObjectFlags & ObjectFlags.IsUnknownLikeUnionComputed) != 0);
            throw new OperationCanceledException();
        };
        try
        {
            await host.Views.UnknownLikeUnionAsync(unknownLike);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check((unknownLike.ObjectFlags & ObjectFlags.IsUnknownLikeUnionComputed) == 0);
        host.BeforeMemberTable = null;
        Check(await host.Views.UnknownLikeUnionAsync(unknownLike));

        Type deep = intersection;
        for (int i = 0; i < 20_000; i++)
            deep = context.NewUnionType([deep, context.StringType], ObjectFlags.ContainsIntersections);
        Check(await host.Views.ReducedAsync(deep) == context.StringType);
        Check(await host.Views.ReducedAsync(deep) == context.StringType);
        try
        {
            await host.Properties.GetAsync(new TypeContext().StringType);
            throw new InvalidOperationException("Foreign type accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Console.WriteLine($"{checks} property/view/index/cache/cancellation assertions; reduction chain depth 20000");
    }

    internal static async Task WriteAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, CheckerSymbols symbols, Checker host,
        Func<Type?, int> typeId, Func<Symbol?, int> symbolId, Func<SyntaxNode?, int> nodeId)
    {
        writer.WriteStartArray("propertyQueries"u8);
        foreach (var node in nodes)
        {
            if (node is not (TypeAliasDeclarationNode or InterfaceDeclarationNode or ClassDeclarationNode or TypeParameterDeclarationNode))
                continue;
            var type = await host.Declared.GetAsync(symbols.Declaration(node)!);
            writer.WriteStartArray();
            writer.WriteNumberValue(nodeId(node));
            writer.WriteNumberValue(typeId(type));
            writer.WriteNumberValue(typeId(await host.Views.ApparentAsync(type)));
            writer.WriteNumberValue(typeId(await host.Views.ReducedAsync(type)));
            writer.WriteStartArray();
            foreach (var property in await host.Properties.GetAsync(type))
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(symbolId(property));
                writer.WriteNumberValue(typeId(await host.Values.GetAsync(property)));
                writer.WriteNumberValue(typeId(await host.Values.WriteAsync(property)));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStartArray();
            foreach (var name in new Utf8String[] { "value"u8, "kind"u8, "optional"u8, "missing"u8, "toString"u8, "apply"u8, "0"u8, "1"u8, "length"u8 })
            {
                var property = await host.Properties.PropertyAsync(type, name);
                var raw = type is UnionOrIntersectionType composite ? await host.Properties.CachedPropertyAsync(composite, name) : property;
                writer.WriteStartArray();
                writer.WriteStringValue(name);
                writer.WriteNumberValue(symbolId(property));
                writer.WriteNumberValue(symbolId(raw));
                writer.WriteNumberValue(typeId(raw is null ? null : await host.Values.GetAsync(raw)));
                writer.WriteNumberValue(typeId(raw is null ? null : await host.Values.WriteAsync(raw)));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStartArray();
            foreach (var index in await host.IndexesAsync(type, default))
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(typeId(index.KeyType));
                writer.WriteNumberValue(typeId(index.ValueType));
                writer.WriteBooleanValue(index.IsReadonly);
                writer.WriteNumberValue(nodeId(index.Declaration));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}

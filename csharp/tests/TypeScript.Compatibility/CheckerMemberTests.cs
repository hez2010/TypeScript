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

internal static class CheckerMemberTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Structured member assertion {checks + 1}");
            checks++;
        }
        const string text = "interface Array<T> { [n:number]:T; length:number } interface ReadonlyArray<T> {} interface Base<T> { value:T; self:this } interface Child extends Base<string> { own:number; [key: string]:unknown } type Fn<T> = (x:T) => T; type S = Fn<string>; function body(x:unknown) { return x; } type Pred = (x:unknown) => x is string;";
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        var program = await CompilerProgram.CreateAsync(
            new MemoryFileSystem(new Dictionary<string, byte[]> { ["/project/main.ts"] = Wtf8.Encode(text) }),
            "/project", new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scopes = new ProgramScopeHost(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scopes);
        var host = new ProgramTypeHost(context, links, scopes);
        var child = (InterfaceType)await host.Declared.GetAsync(symbols.Globals["Child"]);
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Members.ResolveAsync(child);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check((child.ObjectFlags & ObjectFlags.MembersResolved) == 0 && child.Members is null);
        Check(!child.DeclaredMembersResolved && host.Instantiation.Resolutions.Count == 0);
        host.BeforeNode = null;
        await host.Members.ResolveAsync(child);
        Check(child.Properties!.Select(p => p.Name).SequenceEqual(["own", "value", "self"]));
        var value = child.Properties!.Single(p => p.Name == "value");
        Check(await host.SymbolTypeAsync(value, default) == context.StringType);
        Check(await host.SymbolTypeAsync(child.Properties!.Single(p => p.Name == "self"), default) == child);
        Check(child.IndexInfos!.Count == 1 && child.IndexInfos[0].ValueType == context.UnknownType);
        var originalMembers = child.Members;
        Check(await host.Members.ResolveAsync(child) == child && child.Members == originalMembers);
        Check(symbols.Globals["Base"].Members["value"] != value);

        var function = (ObjectType)await host.Declared.GetAsync(symbols.Globals["S"]);
        await host.Members.ResolveAsync(function);
        var signature = function.CallSignatures!.Single();
        Check(signature.Target is not null && signature.ResolvedReturnType is null);
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Signatures.ReturnAsync(signature);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(
            signature.ResolvedReturnType is null
                && signature.Target!.ResolvedReturnType is null
                && host.Instantiation.Resolutions.Count == 0);
        host.BeforeNode = null;
        Check(await host.Signatures.ReturnAsync(signature) == context.StringType);
        Check(await host.Signatures.ReturnAsync(signature) == context.StringType);
        var predicateType = (ObjectType)await host.Declared.GetAsync(symbols.Globals["Pred"]);
        await host.Members.ResolveAsync(predicateType);
        var predicateSignature = predicateType.CallSignatures!.Single();
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Signatures.PredicateAsync(predicateSignature);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(predicateSignature.ResolvedTypePredicate is null);
        host.BeforeNode = null;
        var predicate = await host.Signatures.PredicateAsync(predicateSignature);
        Check(predicate is { ParameterIndex: 0, ParameterName: "x" } && predicate.Type == context.StringType);
        Check(await host.Signatures.PredicateAsync(predicateSignature) == predicate);

        var body = (await host.Signatures.OfSymbolAsync(symbols.Globals["body"])).Single();
        host.ReturnBody = (_, token) => host.Signatures.ReturnAsync(body, token);
        Check(await host.Signatures.ReturnAsync(body) == context.AnyType && host.Diagnostics.Contains(2577));
        Check(host.Instantiation.Resolutions.Count == 0);
        body.ResolvedReturnType = null;
        using var cancellation = new CancellationTokenSource();
        host.PredicateBody = (_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult<TypePredicate?>(predicate);
        };
        try
        {
            await host.Signatures.PredicateAsync(body, cancellation.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(body.ResolvedTypePredicate is null);
        host.PredicateBody = (_, _) => ValueTask.FromResult<TypePredicate?>(predicate);
        Check(await host.Signatures.PredicateAsync(body) == predicate);

        var falseReturn = context.NewSignature(0, null, [], null, [], context.RegularFalseType, null, 0);
        var composite = context.NewSignature(0, null, [], null, [], null, null, 0);
        composite.Composite = new(true, [predicateSignature, falseReturn]);
        Check((await host.Signatures.PredicateAsync(composite))?.Type == context.StringType);
        var incompatible = context.NewSignature(0, null, [], null, [], context.BooleanType,
            new(TypePredicateKind.Identifier, 1, "other", context.NumberType), 0);
        var mismatch = context.NewSignature(0, null, [], null, [], null, null, 0);
        mismatch.Composite = new(true, [predicateSignature, incompatible]);
        Check(await host.Signatures.PredicateAsync(mismatch) is null);
        var inner = context.NewSignature(SignatureFlags.IsInnerCallChain, null, [], null, [], null, null, 0);
        inner.Target = signature;
        Check(await host.Signatures.ReturnAsync(inner) is UnionType innerUnion && innerUnion.Types.Contains(context.OptionalType));
        var outer = context.NewSignature(SignatureFlags.IsOuterCallChain, null, [], null, [], null, null, 0);
        outer.Target = signature;
        Check(await host.Signatures.ReturnAsync(outer) is UnionType outerUnion && outerUnion.Types.Contains(context.UndefinedType));

        Type root = child;
        for (int i = 0; i < 20_000; i++)
        {
            var next = (InterfaceType)context.NewObjectType(ObjectFlags.Interface);
            next.BaseTypesResolved = true;
            next.ResolvedBaseTypes = [root];
            root = next;
        }
        Check(await host.Bases.HasBaseAsync(root, child));
        var deepest = signature;
        for (int i = 0; i < 20_000; i++)
        {
            var next = context.NewSignature(0, null, [], null, [], null, null, 0);
            next.Target = deepest;
            deepest = next;
        }
        Check(await host.Signatures.ReturnAsync(deepest) == context.StringType);
        Check(host.Instantiation.Resolutions.Count == 0);
        try
        {
            await host.Signatures.ReturnAsync(new TypeContext().NewSignature(0, null, [], null, [], null, null, 0));
            throw new InvalidOperationException("Foreign signature accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Console.WriteLine($"{checks} structured member/signature assertions; base and signature chains depth 20000");
    }

    internal static async Task WriteAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, CheckerSymbols symbols,
        ProgramTypeHost host, Func<Type?, int> typeId, Func<Symbol?, int> symbolId, Func<SyntaxNode?, int> nodeId)
    {
        var pending = new List<ObjectType>();
        var seen = new HashSet<Type>();
        int Type(Type? type)
        {
            int id = typeId(type);
            if (type is ObjectType obj && seen.Add(type))
                pending.Add(obj);
            return id;
        }
        var queries = new List<object[]>();
        foreach (var node in nodes)
        {
            Type? type = node switch
            {
                InterfaceDeclarationNode or TypeAliasDeclarationNode => await host.Declared.GetAsync(symbols.Declaration(node)!),
                FunctionDeclarationNode => await host.SymbolTypeAsync(symbols.Declaration(node)!, default),
                _ => null
            };
            if (type is not null)
                queries.Add([nodeId(node), Type(type)]);
        }
        async Task<object[]> Signature(Signature signature)
        {
            var parameters = new List<object[]>();
            var generic = signature.TypeParameters.Select(p => Type(p)).ToArray();
            object[]? receiver = signature.ThisParameter is { } thisParameter
                ? [symbolId(thisParameter), Type(await host.SymbolTypeAsync(thisParameter, default))] : null;
            foreach (var parameter in signature.Parameters)
                parameters.Add([symbolId(parameter), Type(await host.SymbolTypeAsync(parameter, default))]);
            int result = Type(await host.Signatures.ReturnAsync(signature));
            var predicate = await host.Signatures.PredicateAsync(signature);
            object[]? predicateRow = predicate is null
                ? null
                : [(uint)predicate.Kind, predicate.ParameterIndex, predicate.ParameterName, Type(predicate.Type)];
            return
                [
                    (uint)signature.Flags,
                    nodeId(signature.Declaration),
                    signature.MinArgumentCount,
                    generic,
                    receiver!,
                    parameters,
                    result,
                    predicateRow!
                ];
        }
        var members = new List<object[]>();
        for (int i = 0; i < pending.Count; i++)
        {
            var type = pending[i];
            await host.Members.ResolveAsync(type);
            var properties = new List<object[]>();
            foreach (var property in type.Properties ?? [])
                properties.Add([symbolId(property), Type(await host.SymbolTypeAsync(property, default))]);
            var calls = new List<object[]>();
            foreach (var signature in type.CallSignatures ?? [])
                calls.Add(await Signature(signature));
            var constructors = new List<object[]>();
            foreach (var signature in type.ConstructSignatures ?? [])
                constructors.Add(await Signature(signature));
            var indexes = new List<object[]>();
            foreach (var index in type.IndexInfos ?? [])
                indexes.Add([Type(index.KeyType), Type(index.ValueType), index.IsReadonly, nodeId(index.Declaration)]);
            members.Add([typeId(type), properties, calls, constructors, indexes]);
        }
        writer.WritePropertyName("memberQueries");
        Write(queries);
        writer.WritePropertyName("members");
        Write(members);
        void Write(object? value)
        {
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case int number:
                    writer.WriteNumberValue(number);
                    break;
                case uint number:
                    writer.WriteNumberValue(number);
                    break;
                case bool boolean:
                    writer.WriteBooleanValue(boolean);
                    break;
                case string text:
                    writer.WriteStringValue(text);
                    break;
                case System.Collections.IEnumerable values:
                    writer.WriteStartArray();
                    foreach (var item in values)
                        Write(item);
                    writer.WriteEndArray();
                    break;
                default:
                    throw new InvalidOperationException("Unexpected member probe value");
            }
        }
    }
}

using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerInstantiationTests
{
    internal static void Safety()
    {
        int checks = 0;
        void Check(bool value)
        {
            if (!value)
                throw new InvalidOperationException($"Instantiation assertion {checks + 1}");
            checks++;
        }
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var relations = new AlgebraFixtureHost(context);
        var algebra = new TypeAlgebra(context, new([]), relations);
        var host = new InstantiationFixtureHost(context, algebra, links, relations);
        var engine = host.Engine;
        var t = context.NewTypeParameter(new(SymbolFlags.TypeParameter, "T"u8));
        var u = context.NewTypeParameter(new(SymbolFlags.TypeParameter, "U"u8));
        var mapping = TypeMapper.Create([t], [context.NumberType]);
        var target = (InterfaceType)context.NewObjectType(ObjectFlags.Interface | ObjectFlags.Reference, new(SymbolFlags.Interface, "I"u8));
        Type chain = t;
        for (int i = 0; i < 100; i++)
            chain = context.CreateTypeReference(target, [chain]);
        var instantiated = engine.InstantiateAsync(chain, mapping).GetAwaiter().GetResult();
        Check(
            instantiated is TypeReference
                && host.Diagnostics.Contains(DiagnosticCode.TypeInstantiationIsExcessivelyDeepAndPossiblyInfinite));
        Check(engine.Count == 100 && engine.Depth == 0 && engine.ActiveMappers == 0);
        engine.ResetStatementCount();
        host.Diagnostics.Clear();
        Check(engine.InstantiateAsync(t, mapping).GetAwaiter().GetResult() == context.NumberType);
        Check(engine.Count == 1 && host.Diagnostics.Count == 0);

        engine.ResetStatementCount();
        TypeMapper? reentrant = null;
        int mappings = 0;
        reentrant = TypeMapper.Function(type =>
        {
            if (type == u)
            {
                var first = engine.InstantiateAsync(t, reentrant!).GetAwaiter().GetResult();
                var second = engine.InstantiateAsync(t, reentrant!).GetAwaiter().GetResult();
                Check(first == second);
                return second!;
            }
            mappings++;
            return context.StringType;
        });
        Check(engine.InstantiateAsync(u, reentrant).GetAwaiter().GetResult() == context.StringType);
        Check(mappings == 1 && engine.Count == 2 && engine.ActiveMappers == 0);

        var index = context.GetIndexTypeForGenericType(t);
        host.OnIndex = _ => throw new OperationCanceledException();
        try
        {
            engine.InstantiateAsync(index, mapping).GetAwaiter().GetResult();
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(engine.Depth == 0 && engine.ActiveMappers == 0);
        host.OnIndex = null;
        Check(engine.InstantiateAsync(index, mapping).GetAwaiter().GetResult() == context.NeverType);

        var genericParameter = new Symbol(
            SymbolFlags.FunctionScopedVariable,
            "value"u8)
        { CheckFlags = CheckFlags.Readonly | CheckFlags.Mapped };
        var primitiveParameter = new Symbol(SymbolFlags.FunctionScopedVariable, "count"u8);
        var setter = new Symbol(SymbolFlags.SetAccessor, "setter"u8);
        links.Values.Get(genericParameter).ResolvedType = t;
        links.Values.Get(primitiveParameter).ResolvedType = context.NumberType;
        links.Values.Get(setter).ResolvedType = context.NumberType;
        links.Values.Get(setter).WriteType = t;
        var signature = context.NewSignature(SignatureFlags.HasRestParameter | SignatureFlags.IsOuterCallChain, null, [t], null,
            [genericParameter, primitiveParameter, setter], t, new(TypePredicateKind.Identifier, 0, "value"u8, t), 1);
        var fresh = engine.SignatureAsync(signature, mapping, false).GetAwaiter().GetResult();
        Check(fresh.Target == signature && fresh.Flags == SignatureFlags.HasRestParameter && fresh.MinArgumentCount == 1);
        Check(fresh.TypeParameters.Count == 1 && fresh.TypeParameters[0] != t && fresh.TypeParameters[0].Target == t);
        Check(fresh.TypeParameters[0].Mapper!.MapType(t) == fresh.TypeParameters[0]);
        Check(fresh.ResolvedReturnType is null && fresh.ResolvedTypePredicate is null);
        Check(fresh.Parameters[0] != genericParameter && fresh.Parameters[0].CheckFlags == (CheckFlags.Instantiated | CheckFlags.Readonly));
        Check(fresh.Parameters[1] == primitiveParameter && fresh.Parameters[2] != setter);
        var parameterLinks = links.Values.Get(fresh.Parameters[0]);
        Check(parameterLinks.Target == genericParameter && parameterLinks.ResolvedType is null);
        var reInstantiated = engine.SymbolAsync(fresh.Parameters[0], mapping).GetAwaiter().GetResult()!;
        Check(links.Values.Get(reInstantiated).Target == genericParameter);
        var erased = engine.SignatureAsync(signature, mapping, true).GetAwaiter().GetResult();
        Check(erased.TypeParameters.Count == 0 && erased.ResolvedReturnType is null);
        var indexInfo = context.NewIndexInfo(context.StringType, t, true);
        var mappedIndex = engine.IndexInfoAsync(indexInfo, mapping).GetAwaiter().GetResult();
        Check(
            mappedIndex != indexInfo
                && mappedIndex.KeyType == context.StringType
                && mappedIndex.ValueType == context.NumberType
                && mappedIndex.IsReadonly);

        var many = Enumerable.Repeat<Type>(context.StringType, 9_999).ToArray();
        var infos = Enumerable.Repeat(new TupleElementInfo(ElementFlags.Required), many.Length).ToArray();
        var tuple = host.Tuples.CreateAsync(many, infos).GetAwaiter().GetResult();
        Check(host.Tuples.CreateAsync([tuple], [new(ElementFlags.Variadic)]).GetAwaiter().GetResult() != context.ErrorType);
        Check(
            host.Tuples.CreateAsync(
                [context.NumberType, tuple],
                [new(ElementFlags.Required), new(ElementFlags.Variadic)]).GetAwaiter().GetResult() == context.ErrorType);
        Check(host.Diagnostics.Contains(DiagnosticCode.ExpressionProducesATupleTypeThatIsTooLargeToRepresent));

        engine.ResetStatementCount();
        host.Diagnostics.Clear();
        for (int i = 0; i < 5_000_000; i++)
            if (engine.InstantiateAsync(t, mapping).GetAwaiter().GetResult() != context.NumberType)
                throw new InvalidOperationException("Instantiation budget changed early");
        Check(engine.Count == 5_000_000 && host.Diagnostics.Count == 0);
        Check(
            engine.InstantiateAsync(t, mapping).GetAwaiter().GetResult() == context.ErrorType
                && host.Diagnostics.SequenceEqual([DiagnosticCode.TypeInstantiationIsExcessivelyDeepAndPossiblyInfinite]));
        Check(engine.Depth == 0 && engine.ActiveMappers == 0);
        Console.WriteLine($"{checks} instantiation/signature/tuple assertions; exact 100-depth, 5,000,000-work and 10,000-tuple limits");
    }
}

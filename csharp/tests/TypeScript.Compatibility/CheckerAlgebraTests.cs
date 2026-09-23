using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerAlgebraTests
{
    internal static void Safety()
    {
        int assertions = 0;
        void Check(bool value)
        {
            if (!value)
                throw new InvalidOperationException($"Algebra assertion {assertions + 1}");
            assertions++;
        }
        var context = new TypeContext(true, true);
        var host = new AlgebraFixtureHost(context);
        var algebra = new TypeAlgebra(context, new([]), host);
        var other = new TypeContext(true);
        try
        {
            algebra.UnionAsync([context.StringType, other.NumberType]).GetAwaiter().GetResult();
            throw new InvalidOperationException("Foreign union input accepted");
        }
        catch (ArgumentException)
        {
            assertions++;
        }
        try
        {
            algebra.IntersectionAsync([context.StringType, other.NumberType]).GetAwaiter().GetResult();
            throw new InvalidOperationException("Foreign intersection input accepted");
        }
        catch (ArgumentException)
        {
            assertions++;
        }
        Type template = context.StringType;
        const int depth = 20_000;
        for (int i = 0; i < depth; i++)
            template = context.NewTemplateLiteralType(["", ""], [template]);
        Check(TypeAlgebra.IsPatternLiteral(template));
        Check(algebra.TemplateAsync(["", ""], [template]).GetAwaiter().GetResult() == context.StringType);
        var uppercase = new Symbol(SymbolFlags.TypeAlias, "Uppercase");
        var mapped = algebra.StringMappingAsync(uppercase, template).GetAwaiter().GetResult();
        Check(mapped is StringMappingType { Target: { } target } && target == context.StringType);

        var parameter = context.NewTypeParameter(new(SymbolFlags.TypeParameter, "T"));
        Type intersection = parameter;
        for (int i = 0; i < depth; i++)
            intersection = context.NewIntersectionType([intersection, context.StringType]);
        var flattened = algebra.IntersectionAsync([intersection], IntersectionFlags.NoConstraintReduction).GetAwaiter().GetResult();
        Check(
            flattened is IntersectionType { Types.Count: 2 } result
                && result.Types[0] == parameter
                && result.Types[1] == context.StringType);

        var aliasSymbol = new Symbol(SymbolFlags.TypeAlias, "Alias");
        Type union = algebra.UnionAsync(
            [context.StringType, context.NumberType],
            alias: context.CreateAlias(aliasSymbol, [])).GetAwaiter().GetResult();
        for (int i = 0; i < depth; i++)
        {
            var origin = context.NewUnionType([union]);
            union = context.GetUnionFromSortedTypes([context.StringType, context.NumberType], ObjectFlags.PrimitiveUnion, origin: origin);
        }
        Check(algebra.UnionAsync([union, context.NeverType]).GetAwaiter().GetResult().Alias?.Symbol == aliasSymbol);

        Check(TypeAlgebra.CrossProductSize(Enumerable.Repeat(context.BooleanType, 63).ToArray()) == long.MaxValue);
        Check(TypeAlgebra.CrossProductSize([context.NeverType, .. Enumerable.Repeat(context.BooleanType, 64)]) == 0);
        Check(TypeAlgebra.CrossProductSize([.. Enumerable.Repeat(context.BooleanType, 64), context.NeverType]) == long.MaxValue);
        var letters = Enumerable.Range(0, 2_000).Select(i => context.GetStringLiteralType("v" + i)).ToArray();
        var duplicates = letters.SelectMany(t => new Type[] { t, t, t }).ToArray();
        Check(algebra.UnionAsync(duplicates).GetAwaiter().GetResult() is UnionType { Types.Count: 2_000 });

        using var cancellation = new CancellationTokenSource();
        host.BeforeGenericIndex = cancellation.Cancel;
        try
        {
            algebra.TemplateAsync(["x", "y"], [parameter], cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            assertions++;
        }
        host.BeforeGenericIndex = null;
        var recovered = algebra.TemplateAsync(["x", "y"], [parameter]).GetAwaiter().GetResult();
        Check(recovered is TemplateLiteralType { Texts: ["x", "y"] });
        Check(recovered == algebra.TemplateAsync(["x", "y"], [parameter]).GetAwaiter().GetResult());
        Check(
            context.StringType == algebra.UnionAsync([context.StringType, context.GetStringLiteralType("text")]).GetAwaiter().GetResult());
        Console.WriteLine($"{assertions} algebra ownership/limit/cancellation assertions; template, intersection and origin depth {depth}");
    }
}

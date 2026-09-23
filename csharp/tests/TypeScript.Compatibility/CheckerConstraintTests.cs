using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerConstraintTests
{
    internal static void Safety()
    {
        int assertions = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Constraint assertion {assertions + 1}");
            assertions++;
        }
        var context = new TypeContext(true);
        var algebraHost = new AlgebraFixtureHost(context);
        var algebra = new TypeAlgebra(context, new([]), algebraHost);
        var host = new ConstraintFixtureHost(context, algebraHost);
        var stack = new TypeResolutionStack(new());
        var recursion = new TypeRecursion((type, _) => ValueTask.FromResult(type.ModifiersType));
        var constraints = new TypeConstraints(context, algebra, stack, recursion, host);
        algebraHost.ResolveBaseConstraint = constraints.BaseConstraintAsync;
        var cycle = context.NewTypeParameter();
        cycle.Constraint = cycle;
        Check(constraints.BaseConstraintAsync(cycle).GetAwaiter().GetResult() is null);
        Check(cycle.ResolvedBaseConstraint == context.CircularConstraintType && stack.Count == 0);
        Check(constraints.ConstraintAsync(cycle).GetAwaiter().GetResult() is null);
        var unconstrained = context.NewTypeParameter();
        Check(constraints.BaseConstraintAsync(unconstrained).GetAwaiter().GetResult() is null);
        Check(unconstrained.ResolvedBaseConstraint == context.NoConstraintType && unconstrained.Constraint == context.NoConstraintType);

        var defaultRoot = context.NewTypeParameter();
        defaultRoot.ResolvedDefaultType = context.StringType;
        var current = defaultRoot;
        var identity = TypeMapper.Create([], []);
        const int depth = 20_000;
        for (int i = 0; i < depth; i++)
        {
            var next = context.NewTypeParameter();
            next.Target = current;
            next.Mapper = identity;
            current = next;
        }
        Check(constraints.DefaultAsync(current).GetAwaiter().GetResult() == context.StringType);
        Check(current.ResolvedDefaultType == context.StringType);

        var defaultNode = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "Default" } };
        var declaration = new TypeParameterDeclarationNode { Name = new IdentifierNode { Text = "T" }, DefaultType = defaultNode };
        var symbol = new Symbol(SymbolFlags.TypeParameter, "T");
        symbol.DeclarationList.Add(declaration);
        var parameter = context.NewTypeParameter(symbol);
        host.Nodes[defaultNode] = _ => constraints.ResolvedDefaultAsync(parameter);
        Check(constraints.DefaultAsync(parameter).GetAwaiter().GetResult() is null);
        Check(parameter.ResolvedDefaultType == context.CircularConstraintType);

        var cancelNode = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "Canceled" } };
        var canceledDeclaration = new TypeParameterDeclarationNode
        {
            Name = new IdentifierNode { Text = "Canceled" },
            DefaultType = cancelNode
        };
        var canceledSymbol = new Symbol(SymbolFlags.TypeParameter, "Canceled");
        canceledSymbol.DeclarationList.Add(canceledDeclaration);
        var canceled = context.NewTypeParameter(canceledSymbol);
        host.Nodes[cancelNode] = _ => throw new OperationCanceledException();
        try
        {
            constraints.DefaultAsync(canceled).GetAwaiter().GetResult();
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            assertions++;
        }
        Check(canceled.ResolvedDefaultType is null);
        host.Nodes[cancelNode] = _ => ValueTask.FromResult<Type>(context.NumberType);
        Check(constraints.DefaultAsync(canceled).GetAwaiter().GetResult() == context.NumberType);
        var fresh = context.NewTypeParameter();
        fresh.Constraint = context.StringType;
        host.Simplifier = (_, _, _) => throw new OperationCanceledException();
        try
        {
            constraints.BaseConstraintAsync(fresh).GetAwaiter().GetResult();
            throw new InvalidOperationException("Constraint cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            assertions++;
        }
        Check(stack.Count == 0 && fresh.ResolvedBaseConstraint is null);
        host.Simplifier = null;
        Check(constraints.BaseConstraintAsync(fresh).GetAwaiter().GetResult() == context.StringType);

        Type access = context.StringType;
        for (int i = 0; i < depth; i++)
            access = context.NewIndexedAccessType(access, context.NumberType, 0);
        Check(
            recursion.IdentityAsync(access).GetAwaiter().GetResult() == recursion.IdentityAsync(context.StringType).GetAwaiter().GetResult());
        var sameSymbol = new Symbol(SymbolFlags.Interface, "I");
        var older = context.NewObjectType(ObjectFlags.Interface, sameSymbol);
        var newer = context.NewObjectType(ObjectFlags.Interface, sameSymbol);
        Check(recursion.IsDeeplyNestedAsync(older, [older, newer, older, newer], 4).GetAwaiter().GetResult() == false);
        Check(recursion.IsDeeplyNestedAsync(older, [older, newer, newer, newer], 4).GetAwaiter().GetResult());

        var noDefault = context.NewTypeParameter();
        var filled = constraints.FillMissingArgumentsAsync(
            [],
            [noDefault],
            false,
            static (a, b, _) => ValueTask.FromResult(a == b)).GetAwaiter().GetResult();
        Check(filled[0] == context.UnknownType);
        var js = constraints.FillMissingArgumentsAsync(
            [],
            [noDefault],
            true,
            static (a, b, _) => ValueTask.FromResult(a == b)).GetAwaiter().GetResult();
        Check(js[0] == context.AnyType);
        var foreign = new TypeContext();
        try
        {
            constraints.BaseConstraintAsync(foreign.UnknownUnionType).GetAwaiter().GetResult();
            throw new InvalidOperationException("Foreign type accepted");
        }
        catch (ArgumentException)
        {
            assertions++;
        }
        Console.WriteLine($"{assertions} constraint/default/recursion state assertions; default and indexed identity depth {depth}");
    }
}

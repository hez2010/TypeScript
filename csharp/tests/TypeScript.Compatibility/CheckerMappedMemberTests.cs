using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerMappedMemberTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Mapped member assertion {checks + 1}");
            checks++;
        }
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var relations = new AlgebraFixtureHost(context);
        var algebra = new TypeAlgebra(context, new([]), relations);
        var host = new InstantiationFixtureHost(context, algebra, links, relations);
        var a = context.GetStringLiteralType("a");
        var b = context.GetStringLiteralType("b");
        var keys = await algebra.UnionAsync([a, b]);
        MappedType NewMapped(bool indexTemplate = false, bool optional = false)
        {
            var node = new MappedTypeNode
            {
                TypeParameter = new TypeParameterDeclarationNode { Name = new IdentifierNode { Text = "P" } },
                Type = new TokenNode(SyntaxKind.UnknownKeyword),
                QuestionToken = optional ? new TokenNode(SyntaxKind.QuestionToken) : null
            };
            node.SetParents();
            var symbol = new Symbol(SymbolFlags.TypeLiteral, "Mapped");
            symbol.DeclarationList.Add(node);
            var parameter = context.NewTypeParameter(new(SymbolFlags.TypeParameter, "P"));
            parameter.Symbol!.DeclarationList.Add(node.TypeParameter);
            parameter.Constraint = keys;
            var result = (MappedType)context.NewObjectType(ObjectFlags.Mapped, symbol);
            result.Declaration = node;
            result.TypeParameter = parameter;
            host.Parameters[node.TypeParameter] = parameter;
            host.MappedNodes[node] = result;
            host.ConstraintDependencies.Nodes[node.Type!] = _ => ValueTask.FromResult<Type>(
                indexTemplate ? context.GetIndexTypeForGenericType(parameter) : parameter);
            return result;
        }

        var cancelled = NewMapped();
        int valuesBefore = links.Values.Count, mappingsBefore = links.MappedSymbols.Count;
        host.BeforeProperty = name =>
        {
            Check(
                (cancelled.ObjectFlags & ObjectFlags.MembersResolved) != 0
                    && cancelled.Members is null
                    && cancelled.Properties!.Count == 0);
            host.Members.ResolveAsync(cancelled).GetAwaiter().GetResult();
            if (name == "b")
                throw new OperationCanceledException();
        };
        try
        {
            await host.Members.ResolveAsync(cancelled);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check((cancelled.ObjectFlags & ObjectFlags.MembersResolved) == 0 && cancelled.Members is null && cancelled.Properties is null);
        Check(links.Values.Count == valuesBefore && links.MappedSymbols.Count == mappingsBefore);
        host.BeforeProperty = null;
        await host.Members.ResolveAsync(cancelled);
        Check(cancelled.Properties!.Select(p => p.Name).SequenceEqual(["a", "b"]));
        var property = cancelled.Members!["a"];
        Check(links.Values.Get(property).ResolvedType is null);
        Check(links.MappedSymbols.Get(property).KeyType == a && links.Values.Get(property).ContainingType == cancelled);
        Check(await host.Members.SymbolTypeAsync(property) == a);
        var previousMembers = cancelled.Members;
        await host.Members.ResolveAsync(cancelled);
        Check(cancelled.Members == previousMembers && await host.Members.SymbolTypeAsync(property) == a);

        var optional = NewMapped(optional: true);
        await host.Members.ResolveAsync(optional);
        var optionalProperty = optional.Members!["a"];
        var optionalType = await host.Members.SymbolTypeAsync(optionalProperty);
        Check((optionalProperty.Flags & SymbolFlags.Optional) != 0 && optionalType is UnionType union
            && union.Types.Contains(a) && union.Types.Contains(context.MissingType));

        var cycle = NewMapped(indexTemplate: true);
        await host.Members.ResolveAsync(cycle);
        var recursive = cycle.Members!["a"];
        host.OnIndex = _ => Check(host.Members.SymbolTypeAsync(recursive).GetAwaiter().GetResult() == context.ErrorType);
        Check(await host.Members.SymbolTypeAsync(recursive) == context.ErrorType);
        Check(
            cycle.ContainsError
                && host.Diagnostics.SequenceEqual([DiagnosticCode.TypeOfProperty0CircularlyReferencesItselfInMappedType1])
                && host.Resolutions.Count == 0);
        host.OnIndex = null;
        Check(await host.Members.SymbolTypeAsync(recursive) == context.ErrorType && host.Diagnostics.Count == 1);

        var retry = NewMapped(indexTemplate: true);
        await host.Members.ResolveAsync(retry);
        var retryProperty = retry.Members!["a"];
        host.OnIndex = _ => throw new OperationCanceledException();
        try
        {
            await host.Members.SymbolTypeAsync(retryProperty);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(links.Values.Get(retryProperty).ResolvedType is null && host.Resolutions.Count == 0 && !retry.ContainsError);
        host.OnIndex = null;
        Check(await host.Members.SymbolTypeAsync(retryProperty) == context.NeverType);

        var indexes = new List<IndexInfo> { context.NewIndexInfo(context.StringType, context.StringType, true) };
        await host.Members.AppendIndexAsync(indexes, context.NewIndexInfo(context.StringType, context.NumberType), true);
        Check(indexes.Count == 1 && indexes[0].IsReadonly && indexes[0].ValueType is UnionType);
        await host.Members.AppendIndexAsync(indexes, context.NewIndexInfo(context.StringType, context.StringType), false);
        Check(!indexes[0].IsReadonly && indexes[0].ValueType == context.StringType);
        Check(
            MappedMembers.PropertyName(
                context.GetStringLiteralType(Symbol.InternalPrefix + "source")) == Symbol.InternalPrefix + Symbol.InternalPrefix + "source");
        var unique = context.GetUniqueSymbolType(new(SymbolFlags.BlockScopedVariable, "unique"));
        Check(MappedMembers.PropertyName(unique) == unique.Name);
        Check(MappedMembers.PropertyName(context.GetNumberLiteralType(-0.0)) == "0");

        Type deep = context.StringType;
        for (int i = 0; i < 20_000; i++)
            deep = context.NewIntersectionType([deep]);
        Check(await host.Members.ValidIndexKeyAsync(deep));
        Check(await host.Members.LowerBoundAsync(deep) == context.StringType);
        Type modifierChain = optional;
        for (int i = 0; i < 20_000; i++)
        {
            var next = (MappedType)context.NewObjectType(ObjectFlags.Mapped);
            next.Declaration = new MappedTypeNode();
            next.ModifiersType = modifierChain;
            modifierChain = next;
        }
        Check(await host.Members.CombinedOptionalityAsync(modifierChain) == 1);
        Check(await host.Members.CombinedOptionalityAsync(context.NewIntersectionType([modifierChain, context.StringType])) == 0);
        var preserved = context.NewIntersectionType([context.StringType, context.EmptyTypeLiteralType]);
        Check(await host.Members.LowerBoundAsync(preserved) == preserved);
        using var token = new CancellationTokenSource();
        token.Cancel();
        try
        {
            await host.Members.ResolveAsync(cancelled, token.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        try
        {
            await host.Members.LowerBoundAsync(new TypeContext(true, true).StringType);
            throw new InvalidOperationException("Foreign type accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Console.WriteLine($"{checks} mapped member assertions; key validity, lower-bound and modifier chains at depth 20000");
    }
}

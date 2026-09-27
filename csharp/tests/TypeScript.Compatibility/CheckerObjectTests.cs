using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerObjectTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Object instantiation assertion {checks + 1}");
            checks++;
        }
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var relations = new AlgebraFixtureHost(context);
        var algebra = new TypeAlgebra(context, new([]), relations);
        var host = new InstantiationFixtureHost(context, algebra, links, relations);
        var scope = new FunctionDeclarationNode();
        var declaration = new TypeParameterDeclarationNode { Name = new IdentifierNode { Text = "T" }, Parent = scope };
        var symbol = new Symbol(SymbolFlags.TypeParameter, "T");
        symbol.DeclarationList = symbol.DeclarationList.Add(declaration);
        var t = context.NewTypeParameter(symbol);
        var u = context.NewTypeParameter(new(SymbolFlags.TypeParameter, "U"));
        var reference = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "T" }, Parent = scope };
        host.ReferenceSymbols[reference] = symbol;
        Check(await host.Objects.PossiblyReferencedAsync(t, reference));
        var unrelated = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "Other" }, Parent = scope };
        host.ReferenceSymbols[unrelated] = null;
        Check(!await host.Objects.PossiblyReferencedAsync(t, unrelated));
        Check(await host.Objects.PossiblyReferencedAsync(u, unrelated));
        var number = new TokenNode(SyntaxKind.NumberKeyword) { Parent = scope };
        Check(!await host.Objects.PossiblyReferencedAsync(t, number));
        var block = new BlockNode { Parent = scope };
        Check(await host.Objects.PossiblyReferencedAsync(t, new TokenNode(SyntaxKind.NumberKeyword) { Parent = block }));
        var conditional = new ConditionalTypeNode { ExtendsType = reference, Parent = scope };
        Check(await host.Objects.PossiblyReferencedAsync(t, new TokenNode(SyntaxKind.NumberKeyword) { Parent = conditional }));
        conditional.ExtendsType = number;
        Check(!await host.Objects.PossiblyReferencedAsync(t, new TokenNode(SyntaxKind.NumberKeyword) { Parent = conditional }));

        var classDeclaration = new ClassDeclarationNode { Parent = scope };
        var classSymbol = new Symbol(SymbolFlags.Class, "C");
        classSymbol.DeclarationList = classSymbol.DeclarationList.Add(classDeclaration);
        var thisType = context.NewTypeParameter(classSymbol);
        thisType.IsThisType = true;
        var thisNode = new TokenNode(SyntaxKind.ThisType) { Parent = classDeclaration };
        Check(await host.Objects.PossiblyReferencedAsync(thisType, thisNode));
        Check(!await host.Objects.PossiblyReferencedAsync(t, thisNode));

        var method = new MethodDeclarationNode { Parent = scope, Body = new BlockNode() };
        Check(await host.Objects.PossiblyReferencedAsync(t, method));
        method.Type = number;
        method.Body = reference;
        Check(!await host.Objects.PossiblyReferencedAsync(t, method));
        method.Parameters = new([new ParameterDeclarationNode { Type = reference }], -1, -1);
        Check(await host.Objects.PossiblyReferencedAsync(t, method));

        var identifier = new IdentifierNode { Text = "value" };
        var query = new TypeQueryNode { ExprName = identifier, Parent = scope };
        var value = new Symbol(SymbolFlags.BlockScopedVariable, "value");
        value.DeclarationList = value.DeclarationList.Add(new VariableDeclarationNode { Parent = scope });
        host.ValueSymbols[identifier] = value;
        Check(await host.Objects.PossiblyReferencedAsync(t, query));
        value.DeclarationList[0].Parent = new SourceFileNode();
        Check(!await host.Objects.PossiblyReferencedAsync(t, query));
        query.TypeArguments = new([reference], -1, -1);
        Check(await host.Objects.PossiblyReferencedAsync(t, query));
        query.TypeArguments = null;
        query.ExprName = new IdentifierNode { Text = "this" };
        Check(await host.Objects.PossiblyReferencedAsync(t, query));

        SyntaxNode deep = reference;
        for (int i = 0; i < 20_000; i++)
        {
            var parent = new ParenthesizedTypeNode { Type = deep, Parent = scope };
            deep.Parent = parent;
            deep = parent;
        }
        Check(await host.Objects.PossiblyReferencedAsync(t, deep));
        Check(await host.Objects.PossiblyReferencedAsync(t, reference));
        SyntaxNode name = identifier;
        for (int i = 0; i < 20_000; i++)
            name = new QualifiedNameNode { Left = name, Right = new IdentifierNode { Text = "member" } };
        query.ExprName = name;
        Check(!await host.Objects.PossiblyReferencedAsync(t, query));

        var objectDeclaration = new TypeLiteralNode { Members = new([reference], -1, -1), Parent = scope };
        var objectSymbol = new Symbol(SymbolFlags.TypeLiteral, "Object");
        objectSymbol.DeclarationList = objectSymbol.DeclarationList.Add(objectDeclaration);
        var target = context.NewObjectType(ObjectFlags.Anonymous, objectSymbol);
        host.OuterParameters[objectDeclaration] = new Type[] { t, u };
        var map = TypeMapper.Create([t], [context.StringType]);
        var instance = (ObjectType)await host.Objects.InstantiateAsync(target, map);
        Check(instance.Target == target && instance.Mapper!.MapType(t) == context.StringType);
        Check(instance.Members is null && instance.Properties is null && instance.CallSignatures.Count == 0);
        Check(links.TypeNodes.Get(objectDeclaration).OuterTypeParameters!.SequenceEqual([t, u]));
        Check(await host.Objects.InstantiateAsync(target, map) == instance);
        Check(await host.Objects.InstantiateAsync(target, TypeMapper.Create([t, u], [t, u])) == target);
        var alias = context.CreateAlias(new(SymbolFlags.TypeAlias, "Alias"), [u]);
        var aliased = (ObjectType)await host.Objects.InstantiateAsync(target, map, alias);
        Check(aliased != instance && aliased.Alias == alias);
        var second = (ObjectType)await host.Objects.InstantiateAsync(aliased, TypeMapper.Create([u], [context.NumberType]));
        Check(second.Target == target && second.Mapper!.MapType(t) == context.StringType && second.Mapper.MapType(u) == context.NumberType);
        Check(second.Alias!.TypeArguments[0] == context.NumberType);

        var unusedNode = new TypeLiteralNode { Members = new([number], -1, -1), Parent = scope };
        var unusedSymbol = new Symbol(SymbolFlags.TypeLiteral, "Unused");
        unusedSymbol.DeclarationList = unusedSymbol.DeclarationList.Add(unusedNode);
        var unused = context.NewObjectType(ObjectFlags.Anonymous, unusedSymbol);
        var parameters = new Type[] { t };
        host.OuterParameters[unusedNode] = parameters;
        Check(await host.Objects.InstantiateAsync(unused, map) == unused);
        Check(links.TypeNodes.Get(unusedNode).OuterTypeParameters!.Count == 0);

        var snapshotNode = new TypeLiteralNode { Parent = scope };
        var snapshotSymbol = new Symbol(SymbolFlags.TypeLiteral, "Snapshot");
        snapshotSymbol.DeclarationList = snapshotSymbol.DeclarationList.Add(snapshotNode);
        var snapshot = context.NewObjectType(ObjectFlags.Anonymous, snapshotSymbol);
        snapshot.Alias = context.CreateAlias(new(SymbolFlags.TypeAlias, "Captured"), [t]);
        host.OuterParameters[snapshotNode] = parameters;
        await host.Objects.InstantiateAsync(snapshot, map);
        parameters[0] = u;
        Check(links.TypeNodes.Get(snapshotNode).OuterTypeParameters!.SequenceEqual([t]));

        var mapped = (MappedType)context.NewObjectType(ObjectFlags.Mapped, new(SymbolFlags.TypeLiteral, "Mapped"));
        mapped.Declaration = new MappedTypeNode();
        mapped.TypeParameter = t;
        mapped.Alias = alias;
        var mappedInstance = (MappedType)await host.Objects.AnonymousAsync(mapped, TypeMapper.Create([u], [context.NumberType]));
        Check(mappedInstance.TypeParameter != t && mappedInstance.TypeParameter!.Target == t);
        Check(
            mappedInstance.Mapper!.MapType(t) == mappedInstance.TypeParameter
                && mappedInstance.TypeParameter.Mapper == mappedInstance.Mapper);
        Check(
            mappedInstance.Declaration == mapped.Declaration
                && mappedInstance.ConstraintType is null
                && mappedInstance.TemplateType is null);
        Check(mappedInstance.Alias!.TypeArguments[0] == context.NumberType && alias.TypeArguments[0] == u);

        var interfaceType = (InterfaceType)context.NewObjectType(ObjectFlags.Interface | ObjectFlags.Reference);
        var nested = context.CreateTypeReference(interfaceType, [u]);
        host.OnTypeArguments = _ => throw new OperationCanceledException();
        var nestedMapper = TypeMapper.Create([t], [nested]);
        int cacheCount = target.Instantiations!.Count;
        try
        {
            await host.Objects.InstantiateAsync(target, nestedMapper);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(target.Instantiations.Count == cacheCount);
        host.OnTypeArguments = null;
        var retried = await host.Objects.InstantiateAsync(target, nestedMapper);
        Check(retried != target && await host.Objects.InstantiateAsync(target, nestedMapper) == retried);
        Check((retried.ObjectFlags & ObjectFlags.CouldContainTypeVariables) != 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await host.Objects.PossiblyReferencedAsync(t, deep, cancellation.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        try
        {
            await host.Objects.InstantiateAsync(new TypeContext(true, true).EmptyObjectType, map);
            throw new InvalidOperationException("Foreign object accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Console.WriteLine($"{checks} object instantiation assertions; reference, parent and entity-name depth 20000");
    }
}

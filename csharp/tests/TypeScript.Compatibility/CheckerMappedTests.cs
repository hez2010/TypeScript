using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerMappedTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Mapped type assertion {checks + 1}");
            checks++;
        }
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var relations = new AlgebraFixtureHost(context);
        var algebra = new TypeAlgebra(context, new([]), relations);
        var host = new InstantiationFixtureHost(context, algebra, links, relations);
        var t = context.NewTypeParameter(new(SymbolFlags.TypeParameter, "T"u8));
        var p = context.NewTypeParameter(new(SymbolFlags.TypeParameter, "P"u8));
        var map = TypeMapper.Create([t], [context.NumberType]);
        var emptyMapper = TypeMapper.Create([], []);
        MappedType NewMapped(Type constraint, Type template, SyntaxKind? optional = null)
        {
            var node = new MappedTypeNode
            {
                TypeParameter = new TypeParameterDeclarationNode { Name = new IdentifierNode { Text = "P"u8 } },
                Type = new TokenNode(SyntaxKind.UnknownKeyword),
                QuestionToken = optional is { } kind ? new TokenNode(kind) : null
            };
            node.SetParents();
            var parameter = context.NewTypeParameter(new(SymbolFlags.TypeParameter, "P"u8));
            parameter.Symbol!.DeclarationList = parameter.Symbol!.DeclarationList.Add(node.TypeParameter);
            parameter.Constraint = constraint;
            host.Parameters[node.TypeParameter] = parameter;
            host.ConstraintDependencies.Nodes[node.Type!] = _ => ValueTask.FromResult(template);
            var symbol = new Symbol(SymbolFlags.TypeLiteral, "Mapped"u8);
            symbol.DeclarationList = symbol.DeclarationList.Add(node);
            var type = (MappedType)context.NewObjectType(ObjectFlags.Mapped, symbol);
            type.Declaration = node;
            host.MappedNodes[node] = type;
            links.TypeNodes.Get(node).OuterTypeParameters = [t];
            return type;
        }
        var source = NewMapped(context.GetIndexTypeForGenericType(t), t, SyntaxKind.QuestionToken);
        source.Mapper = map;
        Check(source.TypeParameter is null && source.ConstraintType is null && source.TemplateType is null);
        var parameter = await host.Mapped.ParameterAsync(source);
        Check(source.TypeParameter == parameter && await host.Mapped.ParameterAsync(source) == parameter);
        source.ObjectFlags |= ObjectFlags.MembersResolved;
        Check(((ObjectType)await host.Objects.AnonymousAsync(source, map)).Members is null);
        Check((((ObjectType)await host.Objects.AnonymousAsync(source, map)).ObjectFlags & ObjectFlags.MembersResolved) == 0);
        Check(await host.Mapped.ConstraintAsync(source) is IndexType { Target: var constraintTarget } && constraintTarget == t);
        Check(await host.Mapped.HomomorphicVariableAsync(source) == t);
        Check(await host.Mapped.IsGenericAsync(source));
        int calls = 0;
        host.ConstraintDependencies.Nodes[source.Declaration!.Type!] = _ =>
        {
            calls++;
            return ValueTask.FromResult<Type>(t);
        };
        var template = await host.Mapped.TemplateAsync(source);
        Check(template is UnionType union && union.Types.Contains(context.NumberType) && union.Types.Contains(context.MissingType));
        Check(await host.Mapped.TemplateAsync(source) == template && calls == 1);
        source.Declaration.NameType = new TokenNode(SyntaxKind.UnknownKeyword);
        host.ConstraintDependencies.Nodes[source.Declaration.NameType] = _ => throw new OperationCanceledException();
        try
        {
            await host.Mapped.NameAsync(source);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(source.NameType is null);
        host.ConstraintDependencies.Nodes[source.Declaration.NameType] = _ => ValueTask.FromResult<Type>(t);
        Check(await host.Mapped.NameAsync(source) == context.NumberType);
        host.ConstraintDependencies.Nodes[source.Declaration.NameType] = _ => throw new InvalidOperationException("Resolved name recomputed");
        Check(await host.Mapped.NameAsync(source) == context.NumberType);

        var strip = NewMapped(context.StringType, template, SyntaxKind.MinusToken);
        var stripped = await host.Mapped.InstantiateTemplateAsync(strip, context.StringType, true, emptyMapper);
        Check(stripped == context.NumberType);
        Check(await host.Mapped.InstantiateTemplateAsync(strip, context.StringType, false, emptyMapper) == template);
        var wildcard = NewMapped(context.WildcardType, context.NumberType);
        Check(await host.Mapped.InstantiateAsync(wildcard, emptyMapper) == context.WildcardType);
        var array = await host.Tuples.ArrayAsync(context.StringType);
        var constrained = context.NewTypeParameter();
        constrained.Constraint = array;
        var constrainedMapped = NewMapped(context.GetIndexTypeForGenericType(constrained), context.NumberType);
        var anyMapper = TypeMapper.Create([constrained], [context.AnyType]);
        Check(host.Resolutions.Push(constrained, TypeSystemPropertyName.ResolvedBaseConstraint));
        Check(await host.Mapped.InstantiateAsync(constrainedMapped, anyMapper) is MappedType);
        Check(host.Resolutions.Pop());
        Check(host.IsArrayType(await host.Mapped.InstantiateAsync(constrainedMapped, anyMapper)));
        var aliasError = new IntrinsicType(
            context,
            TypeFlags.Any,
            "error"u8)
        { Alias = context.CreateAlias(new(SymbolFlags.TypeAlias, "Unresolved"u8), []) };
        Check(await host.Mapped.InstantiateAsync(constrainedMapped, TypeMapper.Create([constrained], [aliasError])) == aliasError);

        var labels = new TupleElementInfo[]
        {
            new(ElementFlags.Required, new ParameterDeclarationNode()),
            new(ElementFlags.Optional, new ParameterDeclarationNode())
        };
        var tuple = await host.Tuples.CreateAsync([context.StringType, context.NumberType], labels, true);
        var tupleMapped = NewMapped(context.GetIndexTypeForGenericType(t), context.NumberType, SyntaxKind.MinusToken);
        tupleMapped.Declaration!.ReadonlyToken = new TokenNode(SyntaxKind.MinusToken);
        var result = (TypeReference)await host.Mapped.InstantiateAsync(tupleMapped, TypeMapper.Create([t], [tuple]));
        var target = (TupleType)result.ReferencedType;
        Check(!target.IsReadonly && target.MinLength == 2 && target.ElementInfos.All(e => e.Flags == ElementFlags.Required));
        Check(target.ElementInfos.Select(e => e.LabeledDeclaration).SequenceEqual(labels.Select(e => e.LabeledDeclaration)));
        Check(result.ResolvedTypeArguments!.All(type => type == context.NumberType));

        Type deep = context.GetOrCreateSubstitutionType(t, context.UnknownType);
        for (int i = 0; i < 20_000; i++)
            deep = context.NewIndexedAccessType(deep, p, 0);
        var actual = await host.Mapped.ActualVariableAsync(deep);
        Check(actual != deep);
        for (int i = 0; i < 20_000; i++)
            actual = ((IndexedAccessType)actual).ObjectType;
        Check(actual == t);
        Type generic = t;
        for (int i = 0; i < 20_000; i++)
            generic = context.NewIntersectionType([generic, context.StringType]);
        Check((await host.Mapped.GenericFlagsAsync(generic) & ObjectFlags.IsGenericObjectType) != 0);
        Check((generic.ObjectFlags & ObjectFlags.IsGenericTypeComputed) != 0);
        Check(MappedTypes.MaybeKind(generic, TypeFlags.TypeParameter));
        Check(!MappedTypes.MaybeKind(generic, TypeFlags.Void));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await host.Mapped.GenericFlagsAsync(generic, cancellation.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        try
        {
            await host.Mapped.ActualVariableAsync(new TypeContext(true, true).StringType);
            throw new InvalidOperationException("Foreign type accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        var retry = NewMapped(context.NumberType, context.NumberType);
        host.ConstraintDependencies.Nodes[retry.Declaration!.Type!] = _ => throw new OperationCanceledException();
        try
        {
            await host.Mapped.TemplateAsync(retry);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(retry.TemplateType is null);
        host.ConstraintDependencies.Nodes[retry.Declaration.Type!] = _ => ValueTask.FromResult<Type>(context.StringType);
        Check(await host.Mapped.TemplateAsync(retry) == context.StringType);
        var checkLeaf = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "Check"u8 } };
        var extendsLeaf = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "Extends"u8 } };
        host.ConstraintDependencies.Nodes[checkLeaf] = _ => ValueTask.FromResult<Type>(t);
        host.ConstraintDependencies.Nodes[extendsLeaf] = _ => ValueTask.FromResult<Type>(context.NumberType);
        SyntaxNode checkNode = checkLeaf, extendsNode = extendsLeaf;
        for (int i = 0; i < 20_000; i++)
        {
            var nextCheck = new TupleTypeNode { Elements = new([checkNode]) };
            var nextExtends = new TupleTypeNode { Elements = new([extendsNode]) };
            checkNode.Parent = nextCheck;
            extendsNode.Parent = nextExtends;
            checkNode = nextCheck;
            extendsNode = nextExtends;
        }
        Check(await host.TypeNodeFlow.ImpliedAsync(t, checkNode, extendsNode) == context.NumberType);
        Check(await host.TypeNodeFlow.ImpliedAsync(context.StringType, checkNode, extendsNode) is null);
        Console.WriteLine($"{checks} mapped type assertions; actual-variable, generic-flag, kind and unary-tuple analysis depth 20000");
    }
}

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerTypeTests
{
    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                Process(document.RootElement, writer);
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    private static Dictionary<string, Type> Builtins(TypeContext c) => new(StringComparer.Ordinal)
    {
        ["any"] = c.AnyType,
        ["auto"] = c.AutoType,
        ["wildcard"] = c.WildcardType,
        ["blockedString"] = c.BlockedStringType,
        ["error"] = c.ErrorType,
        ["unresolved"] = c.UnresolvedType,
        ["nonInferrableAny"] = c.NonInferrableAnyType,
        ["intrinsic"] = c.IntrinsicMarkerType,
        ["unknown"] = c.UnknownType,
        ["undefined"] = c.UndefinedType,
        ["undefinedWidening"] = c.UndefinedWideningType,
        ["missing"] = c.MissingType,
        ["undefinedOrMissing"] = c.UndefinedOrMissingType,
        ["optional"] = c.OptionalType,
        ["null"] = c.NullType,
        ["nullWidening"] = c.NullWideningType,
        ["string"] = c.StringType,
        ["number"] = c.NumberType,
        ["bigint"] = c.BigIntType,
        ["false"] = c.FalseType,
        ["true"] = c.TrueType,
        ["regularFalse"] = c.RegularFalseType,
        ["regularTrue"] = c.RegularTrueType,
        ["boolean"] = c.BooleanType,
        ["symbol"] = c.ESSymbolType,
        ["void"] = c.VoidType,
        ["never"] = c.NeverType,
        ["silentNever"] = c.SilentNeverType,
        ["implicitNever"] = c.ImplicitNeverType,
        ["unreachableNever"] = c.UnreachableNeverType,
        ["object"] = c.NonPrimitiveType,
        ["uniqueLiteral"] = c.UniqueLiteralType,
        ["empty"] = c.EmptyObjectType,
        ["emptyTypeLiteral"] = c.EmptyTypeLiteralType,
        ["unknownEmpty"] = c.UnknownEmptyObjectType,
        ["anyFunction"] = c.AnyFunctionType,
        ["unknownUnion"] = c.UnknownUnionType,
        ["numericString"] = c.NumericStringType,
        ["templateConstraint"] = c.TemplateConstraintType,
        ["noConstraint"] = c.NoConstraintType,
        ["circularConstraint"] = c.CircularConstraintType,
        ["resolvingDefault"] = c.ResolvingDefaultType
    };

    private static void Process(JsonElement input, Utf8JsonWriter writer)
    {
        if (input.TryGetProperty("resolutions", out var resolutions))
        {
            CheckerStateTests.Resolutions(resolutions, writer);
            return;
        }
        var c = new TypeContext(Bool(input, "strict"), Bool(input, "exact"));
        var builtins = Builtins(c);
        var host = new AlgebraFixtureHost(c);
        var algebra = new TypeAlgebra(c, new([]), host);
        bool memberMode = input.GetProperty("steps").EnumerateArray().Any(step => Text(step, "op")
            is "resolveMembers" or "mappedProperty" or "mappedModifiers" or "keyLowerBound" or "mappedOptionality" or "apparentKeys");
        var memberSource = memberMode ? Parser.ParseSourceFile(new("/input.ts"), new SourceText("")) : null;
        bool mappedMode = memberMode
            || input.GetProperty("steps").EnumerateArray().Any(step => Text(step, "op") is "mapped" or "typeNodeFlow");
        bool objectMode = mappedMode || input.GetProperty("steps").EnumerateArray().Any(
            step => Text(step, "op") is "capturedObject" or "deferredObject" or "anonymousInstance" or "possiblyReferenced");
        bool instantiationMode = objectMode || input.GetProperty("steps").EnumerateArray().Any(
            step => Text(step, "op") is "instantiate" or "tuple" or "array" or "permissive" or "restrictive");
        var links = new CheckerLinks();
        var instantiationHost = instantiationMode ? new InstantiationFixtureHost(c, algebra, links, host) : null;
        bool constraintMode = input.GetProperty("steps").EnumerateArray().Any(
            step => Text(
                step,
                "op") is "baseConstraint" or "resolvedConstraint" or "constraint" or "default" or "resolvedDefault" or "fillArgument");
        var constraintHost = new ConstraintFixtureHost(c, host);
        var recursion = new TypeRecursion((type, _) => ValueTask.FromResult(type.ModifiersType));
        var constraints = new TypeConstraints(c, algebra, new(new()), recursion, constraintHost);
        if (constraintMode)
            host.ResolveBaseConstraint = constraints.BaseConstraintAsync;
        bool algebraUsed = constraintMode || instantiationMode;
        var symbols = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        var symbolNames = new Dictionary<Symbol, string>();
        Symbol? SymbolFor(string name)
        {
            if (name.Length == 0)
                return null;
            if (!symbols.TryGetValue(name, out var symbol))
            {
                string stored = name.StartsWith("@internal:", StringComparison.Ordinal) ? Symbol.InternalPrefix + name[10..]
                    : name.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal) ? Symbol.InternalPrefix + name : name;
                symbols.Add(name, symbol = new(SymbolFlags.TypeAlias, stored));
                symbolNames.Add(symbol, name);
            }
            return symbol;
        }
        var values = new List<Type?> { null };
        Type[] Arguments(JsonElement step, string key = "args") => step.TryGetProperty(key, out var indices)
            ? indices.EnumerateArray().Select(i => values[i.GetInt32()]!).ToArray() : [];
        foreach (var step in input.GetProperty("steps").EnumerateArray())
        {
            var args = Arguments(step);
            string op = Text(step, "op"), text = Text(step, "text"), symbol = Text(step, "symbol"), member = Text(step, "member");
            uint flags = step.TryGetProperty("flags", out var f) ? f.GetUInt32() : 0;
            algebraUsed |= op is "unionReduced" or "intersection" or "templateNormalized" or "caseMap" or "regularAll";
            TypeAlias? Alias() => symbol.Length == 0 ? null : c.CreateAlias(SymbolFor(symbol)!, Arguments(step, "aliasArgs"));
            Type? type = op switch
            {
                "builtin" => builtins[text],
                "string" => c.GetStringLiteralType(Wtf8.DecodeString(Convert.FromBase64String(text))),
                "number" => c.GetNumberLiteralType(Number(text)),
                "bigint" => c.GetBigIntLiteralType(BigInteger.Parse(text, CultureInfo.InvariantCulture)),
                "enumNumber" => c.GetEnumLiteralType(Number(text), SymbolFor(symbol)!, SymbolFor(member)!),
                "enumString" => c.GetEnumLiteralType(text, SymbolFor(symbol)!, SymbolFor(member)!),
                "computedEnum" => c.NewComputedEnumType(SymbolFor(symbol)!),
                "errorAlias" => new IntrinsicType(c, TypeFlags.Any, "error") { Alias = Alias() },
                "fresh" => c.GetFreshLiteralType((LiteralType)args[0]),
                "regular" => ((LiteralType)args[0]).RegularType,
                "parameter" => NewParameter(),
                "declaredParameter" => DeclaredParameter(),
                "cloneParameter" => CloneParameter(),
                "setConstraint" => SetParameter(false),
                "setDefault" => SetParameter(true),
                "baseConstraint" => constraints.BaseConstraintAsync(args[0]).GetAwaiter().GetResult(),
                "resolvedConstraint" => constraints.ResolvedBaseConstraintAsync(args[0]).GetAwaiter().GetResult(),
                "constraint" => constraints.ConstraintAsync(args[0]).GetAwaiter().GetResult(),
                "default" => constraints.DefaultAsync((TypeParameter)args[0]).GetAwaiter().GetResult(),
                "resolvedDefault" => constraints.ResolvedDefaultAsync((TypeParameter)args[0]).GetAwaiter().GetResult(),
                "fillArgument" => constraints.FillMissingArgumentsAsync(
                    args,
                    Arguments(step, "aliasArgs").Cast<TypeParameter>().ToArray(),
                    Bool(step, "this"),
                    static (a, b, _) => ValueTask.FromResult(a == b)).GetAwaiter().GetResult()[(int)flags],
                "conditional" => Conditional(),
                "noInfer" => c.GetOrCreateSubstitutionType(args[0], c.UnknownType),
                "distributed" => NewDistributed(),
                "object" => c.NewObjectType((ObjectFlags)flags, SymbolFor(symbol)),
                "capturedObject" => CapturedObject(),
                "deferredObject" => DeferredObject(),
                "anonymousInstance" => instantiationHost!.Objects.AnonymousAsync((ObjectType)args[0],
                    TypeMapper.Create(args.Skip(1).ToArray(), Arguments(step, "aliasArgs")),
                    symbol.Length == 0 ? null : c.CreateAlias(SymbolFor(symbol)!, [])).GetAwaiter().GetResult(),
                "objectMap" => ((ObjectType)args[0]).Mapper!.MapType(args[1]),
                "possiblyReferenced" => PossiblyReferenced() ? c.RegularTrueType : c.RegularFalseType,
                "mappedIdentity" => MappedIdentity(),
                "mapped" => Mapped(),
                "mappedInstantiate" => Instantiate(true),
                "mappedParameter" => instantiationHost!.Mapped.ParameterAsync((MappedType)args[0]).GetAwaiter().GetResult(),
                "mappedConstraint" => instantiationHost!.Mapped.ConstraintAsync((MappedType)args[0]).GetAwaiter().GetResult(),
                "mappedName" => instantiationHost!.Mapped.NameAsync((MappedType)args[0]).GetAwaiter().GetResult(),
                "mappedTemplate" => instantiationHost!.Mapped.TemplateAsync((MappedType)args[0]).GetAwaiter().GetResult(),
                "homomorphic" => instantiationHost!.Mapped.HomomorphicVariableAsync((MappedType)args[0]).GetAwaiter().GetResult(),
                "actualVariable" => instantiationHost!.Mapped.ActualVariableAsync(args[0]).GetAwaiter().GetResult(),
                "genericMapped" => instantiationHost!.Mapped.IsGenericAsync((MappedType)args[0]).GetAwaiter().GetResult()
                    ? c.RegularTrueType : c.RegularFalseType,
                "genericType" => instantiationHost!.Mapped.GenericFlagsAsync(args[0]).GetAwaiter().GetResult() != 0
                    ? c.RegularTrueType : c.RegularFalseType,
                "typeNodeFlow" => TypeNodeFlow(),
                "resolveEmpty" => ResolveEmpty(),
                "resolveMembers" => ResolveMembers(),
                "mappedProperty" => MappedProperty(),
                "mappedProperty64" => MappedProperty(),
                "mappedModifiers" => instantiationHost!.Members.ModifiersTypeAsync((MappedType)args[0]).GetAwaiter().GetResult(),
                "keyLowerBound" => instantiationHost!.Members.LowerBoundAsync(args[0]).GetAwaiter().GetResult(),
                "mappedOptionality" => c.GetNumberLiteralType(
                    instantiationHost!.Members.CombinedOptionalityAsync(args[0]).GetAwaiter().GetResult()),
                "apparentKeys" => instantiationHost!.Members.ApparentKeysAsync(args[1], (MappedType)args[0]).GetAwaiter().GetResult(),
                "memberShape" => MemberShape(),
                "propertyMetadata" => PropertyMetadata(),
                "addIndex" => AddIndex(),
                "identityEquals" => recursion.IdentityAsync(args[0]).GetAwaiter().GetResult() == recursion.IdentityAsync(args[1]).GetAwaiter().GetResult()
                    ? c.RegularTrueType
                    : c.RegularFalseType,
                "identityMatches" => recursion.MatchesAsync(
                    args[0],
                    recursion.IdentityAsync(args[1]).GetAwaiter().GetResult()).GetAwaiter().GetResult()
                    ? c.RegularTrueType
                    : c.RegularFalseType,
                "deeplyNested" => recursion.IsDeeplyNestedAsync(args[0], args.Skip(1).ToArray(), (int)flags).GetAwaiter().GetResult()
                    ? c.RegularTrueType
                    : c.RegularFalseType,
                "shape" => host.Shape(
                    step.GetProperty("properties").EnumerateArray().Select(p => p.GetString()!).ToArray(),
                    args,
                    SymbolFor(symbol)),
                "reference" => c.CreateTypeReference((InterfaceType)args[0], args.AsSpan(1), (ObjectFlags)flags),
                "instantiate" => Instantiate(false),
                "restrictive" => instantiationHost!.Engine.RestrictiveAsync(args[0]).GetAwaiter().GetResult(),
                "permissive" => instantiationHost!.Engine.PermissiveAsync(args[0]).GetAwaiter().GetResult(),
                "array" => instantiationHost!.Tuples.ArrayAsync(args[0], Bool(step, "this")).GetAwaiter().GetResult(),
                "tuple" => instantiationHost!.Tuples.CreateAsync(args, step.GetProperty("elements").EnumerateArray()
                    .Select(e => new TupleElementInfo((ElementFlags)e.GetUInt32())).ToArray(), Bool(step, "this")).GetAwaiter().GetResult(),
                "clone" => c.CloneTypeReference((TypeReference)args[0]),
                "union" => c.GetUnionFromSortedTypes(args, (ObjectFlags)flags,
                    symbol.Length == 0 ? null : c.CreateAlias(SymbolFor(symbol)!, Arguments(step, "aliasArgs")),
                    step.TryGetProperty("origin", out var origin) ? values[origin.GetInt32()] : null),
                "rawUnion" => c.NewUnionType(args, (ObjectFlags)flags),
                "rawIntersection" => c.NewIntersectionType(args, (ObjectFlags)flags),
                "index" => c.GetIndexTypeForGenericType(args[0], (IndexFlags)flags),
                "indexed" => c.NewIndexedAccessType(args[0], args[1], (AccessFlags)flags),
                "substitution" => c.GetSubstitutionType(args[0], args[1]),
                "template" => c.NewTemplateLiteralType(Enumerable.Repeat(text, args.Length + 1).ToArray(), args),
                "stringMapping" => c.NewStringMappingType(SymbolFor(symbol)!, args[0]),
                "unionReduced" => algebra.UnionAsync(args, (UnionReduction)flags, Alias(),
                    step.TryGetProperty("origin", out var o) ? values[o.GetInt32()] : null).GetAwaiter().GetResult(),
                "intersection" => algebra.IntersectionAsync(args, (IntersectionFlags)flags, Alias()).GetAwaiter().GetResult(),
                "regularAll" => algebra.RegularTypeAsync(args[0]).GetAwaiter().GetResult(),
                "filter" => algebra.Filter(args[0], t => ((uint)t.Flags & flags) == 0),
                "templateNormalized" => algebra.TemplateAsync(step.GetProperty("texts").EnumerateArray()
                    .Select(t => Wtf8.DecodeString(t.GetBytesFromBase64())).ToArray(), args).GetAwaiter().GetResult(),
                "caseMap" => algebra.StringMappingAsync(SymbolFor(symbol)!, args[0]).GetAwaiter().GetResult(),
                _ => throw new InvalidOperationException(op)
            };
            values.Add(type);
            Type MemberShape()
            {
                var names = step.GetProperty("properties").EnumerateArray().Select(p => p.GetString()!).ToArray();
                var result = (ObjectType)host.Shape(names, args, SymbolFor(symbol));
                for (int i = 0; i < names.Length; i++)
                    links.Values.Get(result.Members![names[i]]).ResolvedType = args[i];
                return result;
            }
            Type PropertyMetadata()
            {
                var result = (ObjectType)args[0];
                var property = result.Members![text];
                property.Flags |= (SymbolFlags)flags;
                property.CheckFlags |= (CheckFlags)step.GetProperty("origin").GetUInt32();
                var declaration = new PropertySignatureDeclarationNode
                {
                    Name = new IdentifierNode { Text = text },
                    Pos = values.Count,
                    End = values.Count + 1,
                    Parent = memberSource!
                };
                property.DeclarationList.Add(declaration);
                property.ValueDeclaration = declaration;
                if (args.Length > 1)
                    links.Values.Get(property).NameType = args[1];
                return result;
            }
            Type AddIndex()
            {
                var result = (ObjectType)args[0];
                result.IndexInfos = [.. result.IndexInfos, c.NewIndexInfo(args[1], args[2], Bool(step, "this"))];
                return result;
            }
            Type ResolveMembers()
            {
                instantiationHost!.Members.ResolveAsync((MappedType)args[0]).GetAwaiter().GetResult();
                return args[0];
            }
            Type? MappedProperty()
            {
                var structure = (MappedType)args[0];
                instantiationHost!.Members.ResolveAsync(structure).GetAwaiter().GetResult();
                string decoded = op == "mappedProperty64" ? Wtf8.DecodeString(Convert.FromBase64String(text)) : text;
                string name = decoded.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal)
                    ? Symbol.InternalPrefix + decoded
                    : decoded;
                return structure.Members!.TryGetValue(name, out var property)
                    ? instantiationHost.Members.SymbolTypeAsync(property).GetAwaiter().GetResult() : null;
            }
            Type ResolveEmpty()
            {
                var result = (ObjectType)args[0];
                result.ObjectFlags |= ObjectFlags.MembersResolved;
                result.Members = new Dictionary<string, Symbol>().AsReadOnly();
                result.Properties = [];
                result.CallSignatures = [];
                result.ConstructSignatures = [];
                result.IndexInfos = [];
                return result;
            }
            Type TypeNodeFlow()
            {
                var file = Parser.ParseSourceFile(new("/type-flow/fixture.ts"), new SourceText(text));
                TypeReferenceNode? selected = null;
                foreach (var reference in file.DescendantsAndSelf().OfType<TypeReferenceNode>())
                {
                    var value = ((IdentifierNode)reference.TypeName!).Text switch
                    {
                        "Value" => args[0],
                        "Check" => args[1],
                        "Extends" => args[2],
                        _ => throw new InvalidOperationException("Unseeded type-flow reference")
                    };
                    instantiationHost!.ConstraintDependencies.Nodes[reference] = _ => ValueTask.FromResult(value);
                    if (((IdentifierNode)reference.TypeName!).Text == "Value")
                        selected = reference;
                }
                return instantiationHost!.TypeNodeFlow.ApplyAsync(args[0], selected!).GetAwaiter().GetResult();
            }
            bool PossiblyReferenced()
            {
                var file = Parser.ParseSourceFile(new("/references/fixture.ts"), new SourceText(text));
                var nodes = file.DescendantsAndSelf().ToArray();
                var declaration = nodes.Single(n => n is INamedNode { Name: IdentifierNode id } && id.Text == symbol
                    && (Bool(step, "this") ? n is ClassDeclarationNode : n is TypeParameterDeclarationNode));
                var parameterSymbol = new Symbol(SymbolFlags.TypeParameter, symbol);
                parameterSymbol.DeclarationList.Add(declaration);
                var parameter = c.NewTypeParameter(parameterSymbol);
                parameter.IsThisType = Bool(step, "this");
                var unknownSymbol = new Symbol(SymbolFlags.None, "unknown");
                foreach (var node in nodes)
                {
                    if (node is TypeReferenceNode reference)
                        instantiationHost!.ReferenceSymbols[reference] = reference.TypeName is IdentifierNode id && id.Text == symbol
                            ? parameterSymbol : unknownSymbol;
                    if (node is IdentifierNode identifier)
                    {
                        var value = new Symbol(SymbolFlags.BlockScopedVariable, identifier.Text);
                        value.DeclarationList.AddRange(nodes.OfType<VariableDeclarationNode>()
                            .Where(d => d.Name is IdentifierNode name && name.Text == identifier.Text));
                        instantiationHost!.ValueSymbols[identifier] = value;
                    }
                }
                var selected = ((ITypedNode)nodes.Single(n => n is INamedNode { Name: IdentifierNode id } && id.Text == "Result")).Type!;
                if (member == "true")
                    selected = ((ConditionalTypeNode)selected).TrueType!;
                return instantiationHost!.Objects.PossiblyReferencedAsync(parameter, selected).GetAwaiter().GetResult();
            }
            ObjectType CapturedObject()
            {
                var name = SymbolFor(text)!;
                name.Flags = SymbolFlags.TypeLiteral;
                SyntaxNode node = (flags & (uint)ObjectFlags.InstantiationExpressionType) != 0
                    ? new ExpressionWithTypeArgumentsNode() : new TypeLiteralNode();
                name.DeclarationList.Add(node);
                var result = c.NewObjectType((ObjectFlags)flags, name);
                if (result is InstantiationExpressionType expression)
                    expression.Node = node;
                result.Alias = Alias();
                links.TypeNodes.Get(node).OuterTypeParameters = Array.AsReadOnly(args);
                return result;
            }
            TypeReference DeferredObject()
            {
                var node = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "Fixture" }, Parent = new TypeLiteralNode() };
                instantiationHost!.NodeAliases[node] = Alias();
                var result = instantiationHost.Objects.DeferredReferenceAsync(args[0], node, null).GetAwaiter().GetResult();
                var data = links.TypeNodes.Get(node);
                data.OuterTypeParameters = Array.AsReadOnly(args.Skip(1).ToArray());
                data.ResolvedType = result;
                return result;
            }
            MappedType Mapped()
            {
                var parameter = (TypeParameter)args[0];
                var declaration = new MappedTypeNode
                {
                    TypeParameter = new TypeParameterDeclarationNode
                    {
                        Name = new IdentifierNode { Text = parameter.Symbol!.Name },
                        Constraint = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "SeedConstraint" } }
                    },
                    Type = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "SeedTemplate" } },
                    ReadonlyToken = (flags & 3) == 0
                        ? null
                        : new TokenNode((flags & 1) != 0 ? SyntaxKind.ReadonlyKeyword : SyntaxKind.MinusToken),
                    QuestionToken = (flags & 12) == 0
                        ? null
                        : new TokenNode((flags & 4) != 0 ? SyntaxKind.QuestionToken : SyntaxKind.MinusToken)
                };
                if (step.TryGetProperty("origin", out var origin) && origin.GetInt32() != 0)
                {
                    declaration.NameType = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "SeedName" } };
                    instantiationHost!.ConstraintDependencies.Nodes[declaration.NameType] = _ => ValueTask.FromResult(values[origin.GetInt32()]!);
                }
                if (step.TryGetProperty("keyof", out var keyof) && keyof.GetInt32() != 0)
                {
                    var operand = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "SeedModifiers" } };
                    declaration.TypeParameter.Constraint = new TypeOperatorNode { Operator = SyntaxKind.KeyOfKeyword, Type = operand };
                    instantiationHost!.ConstraintDependencies.Nodes[operand] = _ => ValueTask.FromResult(values[keyof.GetInt32()]!);
                }
                declaration.SetParents();
                declaration.Parent = new TypeAliasDeclarationNode(SyntaxKind.TypeAliasDeclaration)
                {
                    Name = new IdentifierNode { Text = "Fixture" },
                    Type = declaration
                };
                var name = SymbolFor(text)!;
                name.Flags = SymbolFlags.TypeLiteral;
                name.DeclarationList.Add(declaration);
                parameter.Symbol!.DeclarationList.Add(declaration.TypeParameter);
                parameter.Constraint = args[1];
                instantiationHost!.Parameters[declaration.TypeParameter] = parameter;
                instantiationHost.ConstraintDependencies.Nodes[declaration.Type!] = _ => ValueTask.FromResult(args[2]);
                var result = (MappedType)c.NewObjectType(ObjectFlags.Mapped, name);
                result.Declaration = declaration;
                result.TypeParameter = parameter;
                result.Alias = Alias();
                instantiationHost.MappedNodes[declaration] = result;
                links.TypeNodes.Get(declaration).OuterTypeParameters = Array.AsReadOnly(args.Skip(3).ToArray());
                return result;
            }
            Type? Instantiate(bool mapped)
            {
                var sources = new List<Type>();
                var targets = new List<Type>();
                for (int i = 1; i < args.Length; i += 2)
                {
                    sources.Add(args[i]);
                    targets.Add(args[i + 1]);
                }
                if (mapped)
                    return instantiationHost!.Mapped.InstantiateAsync((MappedType)args[0],
                        TypeMapper.Create(sources.ToArray(), targets.ToArray()), Alias()).GetAwaiter().GetResult();
                return instantiationHost!.Engine.InstantiateAsync(
                    args[0],
                    TypeMapper.Create(sources.ToArray(), targets.ToArray()),
                    Alias()).GetAwaiter().GetResult();
            }
            MappedType MappedIdentity()
            {
                var mapped = (MappedType)c.NewObjectType(ObjectFlags.Mapped | ObjectFlags.Instantiated, SymbolFor(symbol));
                mapped.ModifiersType = args[0];
                return mapped;
            }
            TypeParameter SetParameter(bool isDefault)
            {
                var parameter = (TypeParameter)args[0];
                if (isDefault)
                    parameter.ResolvedDefaultType = args[1];
                else
                    parameter.Constraint = args[1];
                return parameter;
            }
            TypeParameter CloneParameter()
            {
                var parameter = c.NewTypeParameter(args[0].Symbol);
                parameter.Target = (TypeParameter)args[0];
                parameter.Mapper = TypeMapper.Create([args[1]], [args[2]]);
                return parameter;
            }
            TypeParameter DeclaredParameter()
            {
                string source = (flags & 1) == 0 ? "type Host<" + symbol + " extends SeedConstraint = SeedDefault> = unknown;"
                    : "type Host = { [" + symbol + " in SeedConstraint]: unknown };";
                var file = Parser.ParseSourceFile(new("/constraints/" + values.Count + ".ts"), new SourceText(source));
                var declaration = file.DescendantsAndSelf().OfType<TypeParameterDeclarationNode>().Single();
                if (args.Length > 0 && args[0] is { } constraint)
                    constraintHost.Nodes[declaration.Constraint!] = _ => ValueTask.FromResult(constraint);
                else
                    declaration.Constraint = null;
                if (args.Length > 1 && args[1] is { } defaultType)
                    constraintHost.Nodes[declaration.DefaultType!] = _ => ValueTask.FromResult(defaultType);
                else
                    declaration.DefaultType = null;
                var name = SymbolFor(symbol)!;
                name.DeclarationList.Add(declaration);
                var parameter = c.NewTypeParameter(name);
                parameter.IsThisType = Bool(step, "this");
                return parameter;
            }
            ConditionalType Conditional()
            {
                var file = Parser.ParseSourceFile(
                    new("/conditional/" + values.Count + ".ts"),
                    new SourceText("type Host = SeedCheck extends SeedExtends ? SeedTrue : SeedFalse;"));
                var node = file.DescendantsAndSelf().OfType<ConditionalTypeNode>().Single();
                constraintHost.Nodes[node.CheckType!] = _ => ValueTask.FromResult(args[0]);
                constraintHost.Nodes[node.ExtendsType!] = _ => ValueTask.FromResult(args[1]);
                constraintHost.Nodes[node.TrueType!] = _ => ValueTask.FromResult(args[2]);
                constraintHost.Nodes[node.FalseType!] = _ => ValueTask.FromResult(args[3]);
                return new(c, new(node, args[0], args[1], false), args[0], args[1]);
            }
            TypeParameter NewParameter()
            {
                var parameter = c.NewTypeParameter(SymbolFor(symbol));
                parameter.IsThisType = Bool(step, "this");
                if (args.Length != 0)
                    parameter.Constraint = args[0];
                return parameter;
            }
            TypeParameter NewDistributed()
            {
                var parameter = c.NewTypeParameter();
                parameter.IsDistributed = true;
                parameter.Constraint = args[0];
                return parameter;
            }
        }

        var mappers = new List<TypeMapper?> { null };
        int calls = 0;
        if (input.TryGetProperty("mappers", out var mapperSteps))
            foreach (var step in mapperSteps.EnumerateArray())
            {
                var sources = Arguments(step, "sources");
                var targets = Arguments(step, "targets");
                var parts = step.TryGetProperty("parts", out var p) ? p.EnumerateArray().Select(i => i.GetInt32()).ToArray() : [];
                TypeMapper mapper = Text(step, "op") switch
                {
                    "direct" => TypeMapper.Create(sources, targets),
                    "single" => TypeMapper.ToSingle(sources, targets[0]),
                    "deferred" => TypeMapper.Deferred(sources, targets.Select(t => (Func<Type>)(() =>
                    {
                        calls++;
                        return t;
                    })).ToArray()),
                    "function" => TypeMapper.Function(t =>
                    {
                        calls++;
                        int i = Array.IndexOf(sources, t);
                        return i < 0 ? t : targets[i];
                    }),
                    "merged" => TypeMapper.Merge(mappers[parts[0]], mappers[parts[1]]!),
                    "composite" => TypeMapper.Combine(mappers[parts[0]], mappers[parts[1]]!, InstantiateAtom),
                    "prepend" => TypeMapper.Prepend(sources[0], targets[0], mappers[parts[0]]),
                    "append" => TypeMapper.Append(mappers[parts[0]], sources[0], targets[0]),
                    _ => throw new InvalidOperationException("Invalid mapper operation")
                };
                mappers.Add(mapper);
            }
        var queryTypes = new List<Type>();
        if (input.TryGetProperty("queries", out var queries))
            foreach (var query in queries.EnumerateArray())
            {
                var type = values[query.GetProperty("type").GetInt32()]!;
                var mapper = mappers[query.GetProperty("mapper").GetInt32()]!;
                queryTypes.Add(Bool(query, "normalize") ? mapper.MapType(type) : mapper.Map(type));
            }

        var ids = new Dictionary<Type, int>();
        var queue = new List<Type>();
        int Ref(Type? type)
        {
            if (type is null)
                return 0;
            if (!ids.TryGetValue(type, out int id))
            {
                ids.Add(type, id = queue.Count + 1);
                queue.Add(type);
            }
            return id;
        }
        void Refs(IEnumerable<Type?>? types)
        {
            if (types is null)
            {
                writer.WriteNullValue();
                return;
            }
            writer.WriteStartArray();
            foreach (var type in types)
                writer.WriteNumberValue(Ref(type));
            writer.WriteEndArray();
        }
        writer.WriteStartObject();
        writer.WritePropertyName("results");
        Refs(values.Skip(1));
        writer.WriteStartArray("types");
        for (int i = 0; i < queue.Count; i++)
        {
            var type = queue[i];
            writer.WriteStartObject();
            writer.WriteNumber("flags", (uint)type.Flags);
            writer.WriteNumber("objectFlags", (uint)type.ObjectFlags);
            writer.WriteString(
                "symbol",
                type.Symbol is null
                    ? ""
                    : symbolNames.GetValueOrDefault(type.Symbol, TypeScript.Compiler.Binding.Symbol.EscapeName(type.Symbol.Name)));
            writer.WriteBoolean("literal", type.IsLiteral);
            writer.WriteBoolean("unit", type.IsUnit);
            if (objectMode && type is ObjectType objectType)
            {
                writer.WriteNumber("objectTarget", Ref(objectType.Target));
                writer.WriteBoolean("hasMapper", objectType.Mapper is not null);
                writer.WriteBoolean("deferred", objectType is TypeReference { Node: not null });
            }
            if (instantiationMode && type is TupleType tuple)
            {
                writer.WriteStartArray("tuple");
                writer.WriteStartArray();
                foreach (var info in tuple.ElementInfos)
                    writer.WriteNumberValue((uint)info.Flags);
                writer.WriteEndArray();
                writer.WriteNumberValue(tuple.MinLength);
                writer.WriteNumberValue(tuple.FixedLength);
                writer.WriteNumberValue((uint)tuple.CombinedFlags);
                writer.WriteBooleanValue(tuple.IsReadonly);
                writer.WriteEndArray();
                writer.WriteNumber("thisType", Ref(tuple.ThisType));
            }
            if (constraintMode || mappedMode)
            {
                if (type is ConstrainedType constrained)
                    writer.WriteNumber("baseConstraint", Ref(constrained.ResolvedBaseConstraint));
                if (type is TypeParameter parameter)
                {
                    writer.WriteNumber("constraint", Ref(parameter.Constraint));
                    writer.WriteNumber("default", Ref(parameter.ResolvedDefaultType));
                    writer.WriteNumber("parameterTarget", Ref(parameter.Target));
                }
                if (type is ConditionalType conditional)
                {
                    writer.WriteNumber("check", Ref(conditional.CheckType));
                    writer.WriteNumber("extends", Ref(conditional.ExtendsType));
                    writer.WriteNumber("true", Ref(conditional.ResolvedTrueType));
                    writer.WriteNumber("false", Ref(conditional.ResolvedFalseType));
                    writer.WriteNumber("inferredTrue", Ref(conditional.ResolvedInferredTrueType));
                    writer.WriteNumber("defaultConstraint", Ref(conditional.ResolvedDefaultConstraint));
                    writer.WriteNumber("distributiveConstraint", Ref(conditional.ResolvedConstraintOfDistributive));
                }
            }
            if (mappedMode && type is MappedType mappedType)
            {
                writer.WriteNumber("mappedParameter", Ref(mappedType.TypeParameter));
                writer.WriteNumber("mappedConstraint", Ref(mappedType.ConstraintType));
                writer.WriteNumber("mappedName", Ref(mappedType.NameType));
                writer.WriteNumber("mappedTemplate", Ref(mappedType.TemplateType));
                if (memberMode)
                {
                    writer.WriteNumber("modifiersType", Ref(mappedType.ModifiersType));
                    writer.WriteBoolean("containsError", mappedType.ContainsError);
                }
            }
            if (memberMode && type is StructuredType structure)
            {
                writer.WritePropertyName("members");
                if (structure.Members is null)
                    writer.WriteNullValue();
                else
                {
                    writer.WriteStartArray();
                    foreach (var pair in structure.Members.OrderBy(p => p.Key, Comparer<string>.Create(TypeOrder.CompareSymbolNames)))
                    {
                        var property = pair.Value;
                        var data = links.Values.Get(property);
                        var mapping = links.MappedSymbols.TryGet(property);
                        writer.WriteStartArray();
                        writer.WriteBase64StringValue(Wtf8.Encode(Symbol.EscapeName(pair.Key)));
                        writer.WriteNumberValue((uint)property.Flags);
                        writer.WriteNumberValue((uint)property.CheckFlags);
                        writer.WriteNumberValue(Ref(data.ResolvedType));
                        writer.WriteNumberValue(Ref(data.NameType));
                        writer.WriteNumberValue(Ref(mapping?.KeyType));
                        writer.WriteNumberValue(Ref(data.ContainingType));
                        writer.WriteBase64StringValue(
                            Wtf8.Encode(mapping?.SyntheticOrigin is { } original ? Symbol.EscapeName(original.Name) : ""));
                        writer.WriteNumberValue(property.Declarations.Count);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteStartArray("properties");
                foreach (var property in structure.Properties ?? [])
                    writer.WriteBase64StringValue(Wtf8.Encode(Symbol.EscapeName(property.Name)));
                writer.WriteEndArray();
                writer.WriteStartArray("indexes");
                foreach (var index in structure.IndexInfos)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(Ref(index.KeyType));
                    writer.WriteNumberValue(Ref(index.ValueType));
                    writer.WriteBooleanValue(index.IsReadonly);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
            if (type.Alias is { } alias)
            {
                writer.WriteStartArray("alias");
                writer.WriteStringValue(symbolNames[alias.Symbol]);
                Refs(alias.TypeArguments);
                writer.WriteEndArray();
            }
            switch (type)
            {
                case IntrinsicType intrinsic:
                    writer.WriteString("name", intrinsic.IntrinsicName);
                    break;
                case LiteralType literal:
                    writer.WriteNumber("fresh", Ref(literal.FreshType));
                    writer.WriteNumber("regular", Ref(literal.RegularType));
                    writer.WriteBoolean("isFresh", literal.IsFreshLiteral);
                    writer.WritePropertyName("value");
                    switch (literal.Value)
                    {
                        case string value:
                            writer.WriteBase64StringValue(Wtf8.Encode(value));
                            break;
                        case double value:
                            writer.WriteStringValue(BitConverter.DoubleToUInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture));
                            break;
                        case BigInteger value:
                            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
                            break;
                        case bool value:
                            writer.WriteBooleanValue(value);
                            break;
                        case null:
                            writer.WriteNullValue();
                            break;
                        default:
                            throw new InvalidOperationException("Invalid literal value");
                    }
                    break;
                case UnionOrIntersectionType composite:
                    writer.WritePropertyName("types");
                    Refs(composite.Types);
                    if (composite is UnionType union)
                        writer.WriteNumber("origin", Ref(union.Origin));
                    break;
                case TypeParameter parameter:
                    writer.WriteBoolean("this", parameter.IsThisType);
                    break;
                case TypeReference reference when (reference.ObjectFlags & ObjectFlags.Reference) != 0:
                    writer.WriteNumber("target", Ref(reference.Target));
                    writer.WritePropertyName("arguments");
                    Refs(reference.ResolvedTypeArguments);
                    break;
                case IndexType index:
                    writer.WriteNumber("target", Ref(index.Target));
                    writer.WriteNumber("indexFlags", (uint)index.IndexFlags);
                    break;
                case IndexedAccessType indexed:
                    writer.WriteNumber("object", Ref(indexed.ObjectType));
                    writer.WriteNumber("index", Ref(indexed.IndexType));
                    writer.WriteNumber("accessFlags", (uint)indexed.AccessFlags);
                    break;
                case SubstitutionType substitution:
                    writer.WriteNumber("base", Ref(substitution.BaseType));
                    writer.WriteNumber("constraint", Ref(substitution.Constraint));
                    break;
                case TemplateLiteralType template:
                    writer.WriteStartArray("texts");
                    foreach (string value in template.Texts)
                        writer.WriteStringValue(value);
                    writer.WriteEndArray();
                    writer.WritePropertyName("types");
                    Refs(template.Types);
                    break;
                case StringMappingType mapping:
                    writer.WriteNumber("target", Ref(mapping.Target));
                    break;
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("mappers");
        foreach (var mapper in mappers.Skip(1))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue((int)mapper!.Kind);
            writer.WriteBooleanValue(mapper.MapsThisOnly);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("queries");
        Refs(queryTypes);
        writer.WriteNumber("calls", calls);
        writer.WriteStartArray("comparisons");
        if (input.TryGetProperty("comparisons", out var comparisons))
        {
            var order = new TypeOrder([]);
            foreach (var pair in comparisons.EnumerateArray())
                writer.WriteNumberValue(Math.Sign(order.Compare(values[pair[0].GetInt32()], values[pair[1].GetInt32()])));
        }
        writer.WriteEndArray();
        if (algebraUsed)
        {
            writer.WriteStartArray("diagnostics");
            foreach (int code in host.Diagnostics.Distinct().Order())
                writer.WriteNumberValue(code);
            foreach (int code in constraintHost.Diagnostics.Order())
                writer.WriteNumberValue(code);
            if (instantiationHost is not null)
                foreach (int code in instantiationHost.Diagnostics.Distinct().Order())
                    writer.WriteNumberValue(code);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    // The isolated mapper probe deliberately supplies only atomic replacements.
    // Full structural instantiation is a separate checker algorithm, not supplied
    // by this test callback or represented as implemented by the production mapper.
    private static Type InstantiateAtom(Type type, TypeMapper mapper)
    {
        if (type is TypeParameter)
        {
            // instantiateType asks couldContainTypeVariables before mapping.
            type.ObjectFlags |= ObjectFlags.CouldContainTypeVariablesComputed | ObjectFlags.CouldContainTypeVariables;
            return mapper.MapType(type);
        }
        return type is IntrinsicType or LiteralType ? type
            : throw new InvalidOperationException("Mapper fixture requires structural instantiation");
    }

    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var property) ? property.GetString()! : "";

    private static bool Bool(JsonElement value, string key) => value.TryGetProperty(key, out var property) && property.GetBoolean();

    private static double Number(string bits) =>
        BitConverter.UInt64BitsToDouble(ulong.Parse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}

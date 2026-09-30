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

    private static Dictionary<Utf8String, Type> Builtins(TypeContext c) => new(Utf8StringComparer.Ordinal)
    {
        ["any"u8] = c.AnyType,
        ["auto"u8] = c.AutoType,
        ["wildcard"u8] = c.WildcardType,
        ["blockedString"u8] = c.BlockedStringType,
        ["error"u8] = c.ErrorType,
        ["unresolved"u8] = c.UnresolvedType,
        ["nonInferrableAny"u8] = c.NonInferrableAnyType,
        ["intrinsic"u8] = c.IntrinsicMarkerType,
        ["unknown"u8] = c.UnknownType,
        ["undefined"u8] = c.UndefinedType,
        ["undefinedWidening"u8] = c.UndefinedWideningType,
        ["missing"u8] = c.MissingType,
        ["undefinedOrMissing"u8] = c.UndefinedOrMissingType,
        ["optional"u8] = c.OptionalType,
        ["null"u8] = c.NullType,
        ["nullWidening"u8] = c.NullWideningType,
        ["string"u8] = c.StringType,
        ["number"u8] = c.NumberType,
        ["bigint"u8] = c.BigIntType,
        ["false"u8] = c.FalseType,
        ["true"u8] = c.TrueType,
        ["regularFalse"u8] = c.RegularFalseType,
        ["regularTrue"u8] = c.RegularTrueType,
        ["boolean"u8] = c.BooleanType,
        ["symbol"u8] = c.ESSymbolType,
        ["void"u8] = c.VoidType,
        ["never"u8] = c.NeverType,
        ["silentNever"u8] = c.SilentNeverType,
        ["implicitNever"u8] = c.ImplicitNeverType,
        ["unreachableNever"u8] = c.UnreachableNeverType,
        ["object"u8] = c.NonPrimitiveType,
        ["uniqueLiteral"u8] = c.UniqueLiteralType,
        ["empty"u8] = c.EmptyObjectType,
        ["emptyTypeLiteral"u8] = c.EmptyTypeLiteralType,
        ["unknownEmpty"u8] = c.UnknownEmptyObjectType,
        ["anyFunction"u8] = c.AnyFunctionType,
        ["unknownUnion"u8] = c.UnknownUnionType,
        ["numericString"u8] = c.NumericStringType,
        ["templateConstraint"u8] = c.TemplateConstraintType,
        ["noConstraint"u8] = c.NoConstraintType,
        ["circularConstraint"u8] = c.CircularConstraintType,
        ["resolvingDefault"u8] = c.ResolvingDefaultType
    };

    private static void Process(JsonElement input, Utf8JsonWriter writer)
    {
        if (input.TryGetProperty("resolutions"u8, out var resolutions))
        {
            CheckerStateTests.Resolutions(resolutions, writer);
            return;
        }
        var c = new TypeContext(Bool(input, "strict"u8), Bool(input, "exact"u8));
        var builtins = Builtins(c);
        var host = new AlgebraFixtureHost(c);
        var algebra = new TypeAlgebra(c, new([]), host);
        bool memberMode = input.GetProperty("steps"u8).EnumerateArray().Any(step => (Text(step, "op"u8) is var matchedText && (matchedText == "resolveMembers"u8 || matchedText == "mappedProperty"u8 || matchedText == "mappedModifiers"u8 || matchedText == "keyLowerBound"u8 || matchedText == "mappedOptionality"u8 || matchedText == "apparentKeys"u8)));
        var memberSource = memberMode ? Parser.ParseSourceFile(new("/input.ts"u8), new SourceText(""u8)) : null;
        bool mappedMode = memberMode
            || input.GetProperty("steps"u8).EnumerateArray().Any(step => (Text(step, "op"u8) is var matchedText2 && (matchedText2 == "mapped"u8 || matchedText2 == "typeNodeFlow"u8)));
        bool objectMode = mappedMode || input.GetProperty("steps"u8).EnumerateArray().Any(
            step => (Text(step, "op"u8) is var matchedText3 && (matchedText3 == "capturedObject"u8 || matchedText3 == "deferredObject"u8 || matchedText3 == "anonymousInstance"u8 || matchedText3 == "possiblyReferenced"u8)));
        bool instantiationMode = objectMode || input.GetProperty("steps"u8).EnumerateArray().Any(
            step => (Text(step, "op"u8) is var matchedText4 && (matchedText4 == "instantiate"u8 || matchedText4 == "tuple"u8 || matchedText4 == "array"u8 || matchedText4 == "permissive"u8 || matchedText4 == "restrictive"u8)));
        var links = new CheckerLinks();
        var instantiationHost = instantiationMode ? new InstantiationFixtureHost(c, algebra, links, host) : null;
        bool constraintMode = input.GetProperty("steps"u8).EnumerateArray().Any(
            step => (Text(step, "op"u8) is var matchedText5 && (matchedText5 == "baseConstraint"u8 || matchedText5 == "resolvedConstraint"u8 || matchedText5 == "constraint"u8 || matchedText5 == "default"u8 || matchedText5 == "resolvedDefault"u8 || matchedText5 == "fillArgument"u8)));
        var constraintHost = new ConstraintFixtureHost(c, host);
        var recursion = new TypeRecursion((type, _) => ValueTask.FromResult(type.ModifiersType));
        var constraints = new TypeConstraints(c, algebra, new(new()), recursion, constraintHost);
        if (constraintMode)
            host.ResolveBaseConstraint = constraints.BaseConstraintAsync;
        bool algebraUsed = constraintMode || instantiationMode;
        var symbols = new Dictionary<Utf8String, Symbol>();
        var symbolNames = new Dictionary<Symbol, Utf8String>();
        Symbol? SymbolFor(Utf8String name)
        {
            if (name.Length == 0)
                return null;
            if (!symbols.TryGetValue(name, out var symbol))
            {
                Utf8String stored = name.StartsWith("@internal:"u8, StringComparison.Ordinal) ? Symbol.InternalPrefix + name[10..] : name;
                symbols.Add(name, symbol = new(SymbolFlags.TypeAlias, stored));
                symbolNames.Add(symbol, name);
            }
            return symbol;
        }
        var values = new List<Type?> { null };
        Type[] Arguments(JsonElement step, Utf8String? key = null) => step.TryGetProperty((key ?? Utf8String.Copy("args"u8)).Span, out var indices)
            ? indices.EnumerateArray().Select(i => values[i.GetInt32()]!).ToArray() : [];
        foreach (var step in input.GetProperty("steps"u8).EnumerateArray())
        {
            var args = Arguments(step);
            Utf8String op = Text(step, "op"u8), text = Text(step, "text"u8), symbol = Text(step, "symbol"u8), member = Text(step, "member"u8);
            uint flags = step.TryGetProperty("flags"u8, out var f) ? f.GetUInt32() : 0;
            algebraUsed |= (op == "unionReduced"u8 || op == "intersection"u8 || op == "templateNormalized"u8 || op == "caseMap"u8 || op == "regularAll"u8);
            TypeAlias? Alias() => symbol.Length == 0 ? null : c.CreateAlias(SymbolFor(symbol)!, Arguments(step, "aliasArgs"u8));
            Type? type = op switch
            {
                _ when op == "builtin"u8 => builtins[text],
                _ when op == "string"u8 => c.GetStringLiteralType(new Utf8String(System.Buffers.Text.Base64.DecodeFromUtf8(text.Span))),
                _ when op == "number"u8 => c.GetNumberLiteralType(Number(text)),
                _ when op == "bigint"u8 => c.GetBigIntLiteralType(BigInteger.Parse(text, CultureInfo.InvariantCulture)),
                _ when op == "enumNumber"u8 => c.GetEnumLiteralType(Number(text), SymbolFor(symbol)!, SymbolFor(member)!),
                _ when op == "enumString"u8 => c.GetEnumLiteralType(text, SymbolFor(symbol)!, SymbolFor(member)!),
                _ when op == "computedEnum"u8 => c.NewComputedEnumType(SymbolFor(symbol)!),
                _ when op == "errorAlias"u8 => new IntrinsicType(c, TypeFlags.Any, "error"u8) { Alias = Alias() },
                _ when op == "fresh"u8 => c.GetFreshLiteralType((LiteralType)args[0]),
                _ when op == "regular"u8 => ((LiteralType)args[0]).RegularType,
                _ when op == "parameter"u8 => NewParameter(),
                _ when op == "declaredParameter"u8 => DeclaredParameter(),
                _ when op == "cloneParameter"u8 => CloneParameter(),
                _ when op == "setConstraint"u8 => SetParameter(false),
                _ when op == "setDefault"u8 => SetParameter(true),
                _ when op == "baseConstraint"u8 => constraints.BaseConstraintAsync(args[0]).GetAwaiter().GetResult(),
                _ when op == "resolvedConstraint"u8 => constraints.ResolvedBaseConstraintAsync(args[0]).GetAwaiter().GetResult(),
                _ when op == "constraint"u8 => constraints.ConstraintAsync(args[0]).GetAwaiter().GetResult(),
                _ when op == "default"u8 => constraints.DefaultAsync((TypeParameter)args[0]).GetAwaiter().GetResult(),
                _ when op == "resolvedDefault"u8 => constraints.ResolvedDefaultAsync((TypeParameter)args[0]).GetAwaiter().GetResult(),
                _ when op == "fillArgument"u8 => constraints.FillMissingArgumentsAsync(
                    args,
                    Arguments(step, "aliasArgs"u8).Cast<TypeParameter>().ToArray(),
                    Bool(step, "this"u8),
                    static (a, b, _) => ValueTask.FromResult(a == b)).GetAwaiter().GetResult()[(int)flags],
                _ when op == "conditional"u8 => Conditional(),
                _ when op == "noInfer"u8 => c.GetOrCreateSubstitutionType(args[0], c.UnknownType),
                _ when op == "distributed"u8 => NewDistributed(),
                _ when op == "object"u8 => c.NewObjectType((ObjectFlags)flags, SymbolFor(symbol)),
                _ when op == "capturedObject"u8 => CapturedObject(),
                _ when op == "deferredObject"u8 => DeferredObject(),
                _ when op == "anonymousInstance"u8 => instantiationHost!.Objects.AnonymousAsync((ObjectType)args[0],
                    TypeMapper.Create(args.Skip(1).ToArray(), Arguments(step, "aliasArgs"u8)),
                    symbol.Length == 0 ? null : c.CreateAlias(SymbolFor(symbol)!, [])).GetAwaiter().GetResult(),
                _ when op == "objectMap"u8 => ((ObjectType)args[0]).Mapper!.MapType(args[1]),
                _ when op == "possiblyReferenced"u8 => PossiblyReferenced() ? c.RegularTrueType : c.RegularFalseType,
                _ when op == "mappedIdentity"u8 => MappedIdentity(),
                _ when op == "mapped"u8 => Mapped(),
                _ when op == "mappedInstantiate"u8 => Instantiate(true),
                _ when op == "mappedParameter"u8 => instantiationHost!.Mapped.ParameterAsync((MappedType)args[0]).GetAwaiter().GetResult(),
                _ when op == "mappedConstraint"u8 => instantiationHost!.Mapped.ConstraintAsync((MappedType)args[0]).GetAwaiter().GetResult(),
                _ when op == "mappedName"u8 => instantiationHost!.Mapped.NameAsync((MappedType)args[0]).GetAwaiter().GetResult(),
                _ when op == "mappedTemplate"u8 => instantiationHost!.Mapped.TemplateAsync((MappedType)args[0]).GetAwaiter().GetResult(),
                _ when op == "homomorphic"u8 => instantiationHost!.Mapped.HomomorphicVariableAsync((MappedType)args[0]).GetAwaiter().GetResult(),
                _ when op == "actualVariable"u8 => instantiationHost!.Mapped.ActualVariableAsync(args[0]).GetAwaiter().GetResult(),
                _ when op == "genericMapped"u8 => instantiationHost!.Mapped.IsGenericAsync((MappedType)args[0]).GetAwaiter().GetResult()
                    ? c.RegularTrueType : c.RegularFalseType,
                _ when op == "genericType"u8 => instantiationHost!.Mapped.GenericFlagsAsync(args[0]).GetAwaiter().GetResult() != 0
                    ? c.RegularTrueType : c.RegularFalseType,
                _ when op == "typeNodeFlow"u8 => TypeNodeFlow(),
                _ when op == "resolveEmpty"u8 => ResolveEmpty(),
                _ when op == "resolveMembers"u8 => ResolveMembers(),
                _ when op == "mappedProperty"u8 => MappedProperty(),
                _ when op == "mappedProperty64"u8 => MappedProperty(),
                _ when op == "mappedModifiers"u8 => instantiationHost!.Members.ModifiersTypeAsync((MappedType)args[0]).GetAwaiter().GetResult(),
                _ when op == "keyLowerBound"u8 => instantiationHost!.Members.LowerBoundAsync(args[0]).GetAwaiter().GetResult(),
                _ when op == "mappedOptionality"u8 => c.GetNumberLiteralType(
                    instantiationHost!.Members.CombinedOptionalityAsync(args[0]).GetAwaiter().GetResult()),
                _ when op == "apparentKeys"u8 => instantiationHost!.Members.ApparentKeysAsync(args[1], (MappedType)args[0]).GetAwaiter().GetResult(),
                _ when op == "memberShape"u8 => MemberShape(),
                _ when op == "propertyMetadata"u8 => PropertyMetadata(),
                _ when op == "addIndex"u8 => AddIndex(),
                _ when op == "identityEquals"u8 => recursion.IdentityAsync(args[0]).GetAwaiter().GetResult() == recursion.IdentityAsync(args[1]).GetAwaiter().GetResult()
                    ? c.RegularTrueType
                    : c.RegularFalseType,
                _ when op == "identityMatches"u8 => recursion.MatchesAsync(
                    args[0],
                    recursion.IdentityAsync(args[1]).GetAwaiter().GetResult()).GetAwaiter().GetResult()
                    ? c.RegularTrueType
                    : c.RegularFalseType,
                _ when op == "deeplyNested"u8 => recursion.IsDeeplyNestedAsync(args[0], args.Skip(1).ToArray(), (int)flags).GetAwaiter().GetResult()
                    ? c.RegularTrueType
                    : c.RegularFalseType,
                _ when op == "shape"u8 => host.Shape(
                    step.GetProperty("properties"u8).EnumerateArray().Select(p => JsonStrings.GetString(p)!).ToArray(),
                    args,
                    SymbolFor(symbol)),
                _ when op == "reference"u8 => c.CreateTypeReference((InterfaceType)args[0], args.AsSpan(1), (ObjectFlags)flags),
                _ when op == "instantiate"u8 => Instantiate(false),
                _ when op == "restrictive"u8 => instantiationHost!.Engine.RestrictiveAsync(args[0]).GetAwaiter().GetResult(),
                _ when op == "permissive"u8 => instantiationHost!.Engine.PermissiveAsync(args[0]).GetAwaiter().GetResult(),
                _ when op == "array"u8 => instantiationHost!.Tuples.ArrayAsync(args[0], Bool(step, "this"u8)).GetAwaiter().GetResult(),
                _ when op == "tuple"u8 => instantiationHost!.Tuples.CreateAsync(args, step.GetProperty("elements"u8).EnumerateArray()
                    .Select(e => new TupleElementInfo((ElementFlags)e.GetUInt32())).ToArray(), Bool(step, "this"u8)).GetAwaiter().GetResult(),
                _ when op == "clone"u8 => c.CloneTypeReference((TypeReference)args[0]),
                _ when op == "union"u8 => c.GetUnionFromSortedTypes(args, (ObjectFlags)flags,
                    symbol.Length == 0 ? null : c.CreateAlias(SymbolFor(symbol)!, Arguments(step, "aliasArgs"u8)),
                    step.TryGetProperty("origin"u8, out var origin) ? values[origin.GetInt32()] : null),
                _ when op == "rawUnion"u8 => c.NewUnionType(args, (ObjectFlags)flags),
                _ when op == "rawIntersection"u8 => c.NewIntersectionType(args, (ObjectFlags)flags),
                _ when op == "index"u8 => c.GetIndexTypeForGenericType(args[0], (IndexFlags)flags),
                _ when op == "indexed"u8 => c.NewIndexedAccessType(args[0], args[1], (AccessFlags)flags),
                _ when op == "substitution"u8 => c.GetSubstitutionType(args[0], args[1]),
                _ when op == "template"u8 => c.NewTemplateLiteralType(Enumerable.Repeat((Utf8String)text, args.Length + 1).ToArray(), args),
                _ when op == "stringMapping"u8 => c.NewStringMappingType(SymbolFor(symbol)!, args[0]),
                _ when op == "unionReduced"u8 => algebra.UnionAsync(args, (UnionReduction)flags, Alias(),
                    step.TryGetProperty("origin"u8, out var o) ? values[o.GetInt32()] : null).GetAwaiter().GetResult(),
                _ when op == "intersection"u8 => algebra.IntersectionAsync(args, (IntersectionFlags)flags, Alias()).GetAwaiter().GetResult(),
                _ when op == "regularAll"u8 => algebra.RegularTypeAsync(args[0]).GetAwaiter().GetResult(),
                _ when op == "filter"u8 => algebra.Filter(args[0], t => ((uint)t.Flags & flags) == 0),
                _ when op == "templateNormalized"u8 => algebra.TemplateAsync(step.GetProperty("texts"u8).EnumerateArray()
                    .Select(t => new Utf8String(t.GetBytesFromBase64())).ToArray(), args).GetAwaiter().GetResult(),
                _ when op == "caseMap"u8 => algebra.StringMappingAsync(SymbolFor(symbol)!, args[0]).GetAwaiter().GetResult(),
                _ => throw new InvalidOperationException(op.ToString())
            };
            values.Add(type);
            Type MemberShape()
            {
                var names = step.GetProperty("properties"u8).EnumerateArray().Select(p => JsonStrings.GetString(p)!).ToArray();
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
                property.CheckFlags |= (CheckFlags)step.GetProperty("origin"u8).GetUInt32();
                var declaration = new PropertySignatureDeclarationNode
                {
                    Name = new IdentifierNode { Text = text },
                    Pos = values.Count,
                    End = values.Count + 1,
                    Parent = memberSource!
                };
                property.DeclarationList = property.DeclarationList.Add(declaration);
                property.ValueDeclaration = declaration;
                if (args.Length > 1)
                    links.Values.Get(property).NameType = args[1];
                return result;
            }
            Type AddIndex()
            {
                var result = (ObjectType)args[0];
                result.IndexInfos = [.. result.IndexInfos, c.NewIndexInfo(args[1], args[2], Bool(step, "this"u8))];
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
                Utf8String decoded = op == "mappedProperty64"u8 ? new Utf8String(System.Buffers.Text.Base64.DecodeFromUtf8(text.Span)) : text;
                return structure.Members!.TryGetValue(decoded, out var property)
                    ? instantiationHost.Members.SymbolTypeAsync(property).GetAwaiter().GetResult() : null;
            }
            Type ResolveEmpty()
            {
                var result = (ObjectType)args[0];
                result.ObjectFlags |= ObjectFlags.MembersResolved;
                result.Members = new Dictionary<Utf8String, Symbol>().AsReadOnly();
                result.Properties = [];
                result.CallSignatures = [];
                result.ConstructSignatures = [];
                result.IndexInfos = [];
                return result;
            }
            Type TypeNodeFlow()
            {
                var file = Parser.ParseSourceFile(new("/type-flow/fixture.ts"u8), new SourceText(text));
                TypeReferenceNode? selected = null;
                foreach (var reference in file.DescendantsAndSelf().OfType<TypeReferenceNode>())
                {
                    var value = ((IdentifierNode)reference.TypeName!).Text.Span switch
                    {
                        var matchedText6 when matchedText6.SequenceEqual("Value"u8) => args[0],
                        var matchedText7 when matchedText7.SequenceEqual("Check"u8) => args[1],
                        var matchedText8 when matchedText8.SequenceEqual("Extends"u8) => args[2],
                        _ => throw new InvalidOperationException("Unseeded type-flow reference")
                    };
                    instantiationHost!.ConstraintDependencies.Nodes[reference] = _ => ValueTask.FromResult(value);
                    if (((IdentifierNode)reference.TypeName!).Text == "Value"u8)
                        selected = reference;
                }
                return instantiationHost!.TypeNodeFlow.ApplyAsync(args[0], selected!).GetAwaiter().GetResult();
            }
            bool PossiblyReferenced()
            {
                var file = Parser.ParseSourceFile(new("/references/fixture.ts"u8), new SourceText(text));
                var nodes = file.DescendantsAndSelf().ToArray();
                var declaration = nodes.Single(n => n is INamedNode { Name: IdentifierNode id } && id.Text == symbol
                    && (Bool(step, "this"u8) ? n is ClassDeclarationNode : n is TypeParameterDeclarationNode));
                var parameterSymbol = new Symbol(SymbolFlags.TypeParameter, symbol);
                parameterSymbol.DeclarationList = parameterSymbol.DeclarationList.Add(declaration);
                var parameter = c.NewTypeParameter(parameterSymbol);
                parameter.IsThisType = Bool(step, "this"u8);
                var unknownSymbol = new Symbol(SymbolFlags.None, "unknown"u8);
                foreach (var node in nodes)
                {
                    if (node is TypeReferenceNode reference)
                        instantiationHost!.ReferenceSymbols[reference] = reference.TypeName is IdentifierNode id && id.Text == symbol
                            ? parameterSymbol : unknownSymbol;
                    if (node is IdentifierNode identifier)
                    {
                        var value = new Symbol(SymbolFlags.BlockScopedVariable, identifier.Text);
                        value.DeclarationList = value.DeclarationList.AddRange(nodes.OfType<VariableDeclarationNode>()
                            .Where(d => d.Name is IdentifierNode name && name.Text == identifier.Text));
                        instantiationHost!.ValueSymbols[identifier] = value;
                    }
                }
                var selected = ((ITypedNode)nodes.Single(n => n is INamedNode { Name: IdentifierNode id } && id.Text == "Result"u8)).Type!;
                if (member == "true"u8)
                    selected = ((ConditionalTypeNode)selected).TrueType!;
                return instantiationHost!.Objects.PossiblyReferencedAsync(parameter, selected).GetAwaiter().GetResult();
            }
            ObjectType CapturedObject()
            {
                var name = SymbolFor(text)!;
                name.Flags = SymbolFlags.TypeLiteral;
                SyntaxNode node = (flags & (uint)ObjectFlags.InstantiationExpressionType) != 0
                    ? new ExpressionWithTypeArgumentsNode() : new TypeLiteralNode();
                name.DeclarationList = name.DeclarationList.Add(node);
                var result = c.NewObjectType((ObjectFlags)flags, name);
                if (result is InstantiationExpressionType expression)
                    expression.Node = node;
                result.Alias = Alias();
                links.TypeNodes.Get(node).OuterTypeParameters = Array.AsReadOnly(args);
                return result;
            }
            TypeReference DeferredObject()
            {
                var node = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "Fixture"u8 }, Parent = new TypeLiteralNode() };
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
                        Constraint = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "SeedConstraint"u8 } }
                    },
                    Type = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "SeedTemplate"u8 } },
                    ReadonlyToken = (flags & 3) == 0
                        ? null
                        : new TokenNode((flags & 1) != 0 ? SyntaxKind.ReadonlyKeyword : SyntaxKind.MinusToken),
                    QuestionToken = (flags & 12) == 0
                        ? null
                        : new TokenNode((flags & 4) != 0 ? SyntaxKind.QuestionToken : SyntaxKind.MinusToken)
                };
                if (step.TryGetProperty("origin"u8, out var origin) && origin.GetInt32() != 0)
                {
                    declaration.NameType = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "SeedName"u8 } };
                    instantiationHost!.ConstraintDependencies.Nodes[declaration.NameType] = _ => ValueTask.FromResult(values[origin.GetInt32()]!);
                }
                if (step.TryGetProperty("keyof"u8, out var keyof) && keyof.GetInt32() != 0)
                {
                    var operand = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "SeedModifiers"u8 } };
                    declaration.TypeParameter.Constraint = new TypeOperatorNode { Operator = SyntaxKind.KeyOfKeyword, Type = operand };
                    instantiationHost!.ConstraintDependencies.Nodes[operand] = _ => ValueTask.FromResult(values[keyof.GetInt32()]!);
                }
                declaration.SetParents();
                declaration.Parent = new TypeAliasDeclarationNode(SyntaxKind.TypeAliasDeclaration)
                {
                    Name = new IdentifierNode { Text = "Fixture"u8 },
                    Type = declaration
                };
                var name = SymbolFor(text)!;
                name.Flags = SymbolFlags.TypeLiteral;
                name.DeclarationList = name.DeclarationList.Add(declaration);
                parameter.Symbol!.DeclarationList = parameter.Symbol!.DeclarationList.Add(declaration.TypeParameter);
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
                Utf8String source = (flags & 1) == 0 ? Utf8String.Concat("type Host<"u8, symbol, " extends SeedConstraint = SeedDefault> = unknown;"u8)
                    : Utf8String.Concat("type Host = { ["u8, symbol, " in SeedConstraint]: unknown };"u8);
                var file = Parser.ParseSourceFile(new(Utf8String.Copy("/constraints/"u8) + values.Count + ".ts"u8), new SourceText(source));
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
                name.DeclarationList = name.DeclarationList.Add(declaration);
                var parameter = c.NewTypeParameter(name);
                parameter.IsThisType = Bool(step, "this"u8);
                return parameter;
            }
            ConditionalType Conditional()
            {
                var file = Parser.ParseSourceFile(
                    new(Utf8String.Copy("/conditional/"u8) + values.Count + ".ts"u8),
                    new SourceText("type Host = SeedCheck extends SeedExtends ? SeedTrue : SeedFalse;"u8));
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
                parameter.IsThisType = Bool(step, "this"u8);
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
        if (input.TryGetProperty("mappers"u8, out var mapperSteps))
            foreach (var step in mapperSteps.EnumerateArray())
            {
                var sources = Arguments(step, "sources"u8);
                var targets = Arguments(step, "targets"u8);
                var parts = step.TryGetProperty("parts"u8, out var p) ? p.EnumerateArray().Select(i => i.GetInt32()).ToArray() : [];
                TypeMapper mapper = Text(step, "op"u8) switch
                {
                    var matchedText9 when matchedText9 == "direct"u8 => TypeMapper.Create(sources, targets),
                    var matchedText10 when matchedText10 == "single"u8 => TypeMapper.ToSingle(sources, targets[0]),
                    var matchedText11 when matchedText11 == "deferred"u8 => TypeMapper.Deferred(sources, targets.Select(t => (Func<Type>)(() =>
                    {
                        calls++;
                        return t;
                    })).ToArray()),
                    var matchedText12 when matchedText12 == "function"u8 => TypeMapper.Function(t =>
                    {
                        calls++;
                        int i = Array.IndexOf(sources, t);
                        return i < 0 ? t : targets[i];
                    }),
                    var matchedText13 when matchedText13 == "merged"u8 => TypeMapper.Merge(mappers[parts[0]], mappers[parts[1]]!),
                    var matchedText14 when matchedText14 == "composite"u8 => TypeMapper.Combine(mappers[parts[0]], mappers[parts[1]]!, InstantiateAtom),
                    var matchedText15 when matchedText15 == "prepend"u8 => TypeMapper.Prepend(sources[0], targets[0], mappers[parts[0]]),
                    var matchedText16 when matchedText16 == "append"u8 => TypeMapper.Append(mappers[parts[0]], sources[0], targets[0]),
                    _ => throw new InvalidOperationException("Invalid mapper operation")
                };
                mappers.Add(mapper);
            }
        var queryTypes = new List<Type>();
        if (input.TryGetProperty("queries"u8, out var queries))
            foreach (var query in queries.EnumerateArray())
            {
                var type = values[query.GetProperty("type"u8).GetInt32()]!;
                var mapper = mappers[query.GetProperty("mapper"u8).GetInt32()]!;
                queryTypes.Add(Bool(query, "normalize"u8) ? mapper.MapType(type) : mapper.Map(type));
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
        writer.WritePropertyName("results"u8);
        Refs(values.Skip(1));
        writer.WriteStartArray("types"u8);
        for (int i = 0; i < queue.Count; i++)
        {
            var type = queue[i];
            writer.WriteStartObject();
            writer.WriteNumber("flags"u8, (uint)type.Flags);
            writer.WriteNumber("objectFlags"u8, (uint)type.ObjectFlags);
            writer.WriteString(
                "symbol"u8,
                type.Symbol is null
                    ? Utf8String.Empty
                    : symbolNames.GetValueOrDefault(type.Symbol, TypeScript.Compiler.Binding.Symbol.EscapeName(type.Symbol.Name)));
            writer.WriteBoolean("literal"u8, type.IsLiteral);
            writer.WriteBoolean("unit"u8, type.IsUnit);
            if (objectMode && type is ObjectType objectType)
            {
                writer.WriteNumber("objectTarget"u8, Ref(objectType.Target));
                writer.WriteBoolean("hasMapper"u8, objectType.Mapper is not null);
                writer.WriteBoolean("deferred"u8, objectType is TypeReference { Node: not null });
            }
            if (instantiationMode && type is TupleType tuple)
            {
                writer.WriteStartArray("tuple"u8);
                writer.WriteStartArray();
                foreach (var info in tuple.ElementInfos)
                    writer.WriteNumberValue((uint)info.Flags);
                writer.WriteEndArray();
                writer.WriteNumberValue(tuple.MinLength);
                writer.WriteNumberValue(tuple.FixedLength);
                writer.WriteNumberValue((uint)tuple.CombinedFlags);
                writer.WriteBooleanValue(tuple.IsReadonly);
                writer.WriteEndArray();
                writer.WriteNumber("thisType"u8, Ref(tuple.ThisType));
            }
            if (constraintMode || mappedMode)
            {
                if (type is ConstrainedType constrained)
                    writer.WriteNumber("baseConstraint"u8, Ref(constrained.ResolvedBaseConstraint));
                if (type is TypeParameter parameter)
                {
                    writer.WriteNumber("constraint"u8, Ref(parameter.Constraint));
                    writer.WriteNumber("default"u8, Ref(parameter.ResolvedDefaultType));
                    writer.WriteNumber("parameterTarget"u8, Ref(parameter.Target));
                }
                if (type is ConditionalType conditional)
                {
                    writer.WriteNumber("check"u8, Ref(conditional.CheckType));
                    writer.WriteNumber("extends"u8, Ref(conditional.ExtendsType));
                    writer.WriteNumber("true"u8, Ref(conditional.ResolvedTrueType));
                    writer.WriteNumber("false"u8, Ref(conditional.ResolvedFalseType));
                    writer.WriteNumber("inferredTrue"u8, Ref(conditional.ResolvedInferredTrueType));
                    writer.WriteNumber("defaultConstraint"u8, Ref(conditional.ResolvedDefaultConstraint));
                    writer.WriteNumber("distributiveConstraint"u8, Ref(conditional.ResolvedConstraintOfDistributive));
                }
            }
            if (mappedMode && type is MappedType mappedType)
            {
                writer.WriteNumber("mappedParameter"u8, Ref(mappedType.TypeParameter));
                writer.WriteNumber("mappedConstraint"u8, Ref(mappedType.ConstraintType));
                writer.WriteNumber("mappedName"u8, Ref(mappedType.NameType));
                writer.WriteNumber("mappedTemplate"u8, Ref(mappedType.TemplateType));
                if (memberMode)
                {
                    writer.WriteNumber("modifiersType"u8, Ref(mappedType.ModifiersType));
                    writer.WriteBoolean("containsError"u8, mappedType.ContainsError);
                }
            }
            if (memberMode && type is StructuredType structure)
            {
                writer.WritePropertyName("members"u8);
                if (structure.Members is null)
                    writer.WriteNullValue();
                else
                {
                    writer.WriteStartArray();
                    foreach (var pair in structure.Members.OrderBy(p => p.Key, Comparer<Utf8String>.Create(TypeOrder.CompareSymbolNames)))
                    {
                        var property = pair.Value;
                        var data = links.Values.Get(property);
                        var mapping = links.MappedSymbols.TryGet(property);
                        writer.WriteStartArray();
                        writer.WriteBase64StringValue(Symbol.EscapeName(pair.Key).Span.ToArray());
                        writer.WriteNumberValue((uint)property.Flags);
                        writer.WriteNumberValue((uint)property.CheckFlags);
                        writer.WriteNumberValue(Ref(data.ResolvedType));
                        writer.WriteNumberValue(Ref(data.NameType));
                        writer.WriteNumberValue(Ref(mapping?.KeyType));
                        writer.WriteNumberValue(Ref(data.ContainingType));
                        writer.WriteBase64StringValue(
                            (mapping?.SyntheticOrigin is { } original ? Symbol.EscapeName(original.Name) : Utf8String.Empty).Span.ToArray());
                        writer.WriteNumberValue(property.Declarations.Length);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteStartArray("properties"u8);
                foreach (var property in structure.Properties ?? [])
                    writer.WriteBase64StringValue(Symbol.EscapeName(property.Name).Span.ToArray());
                writer.WriteEndArray();
                writer.WriteStartArray("indexes"u8);
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
                writer.WriteStartArray("alias"u8);
                writer.WriteStringValue(symbolNames[alias.Symbol]);
                Refs(alias.TypeArguments);
                writer.WriteEndArray();
            }
            switch (type)
            {
                case IntrinsicType intrinsic:
                    writer.WriteString("name"u8, intrinsic.IntrinsicName.Span);
                    break;
                case LiteralType literal:
                    writer.WriteNumber("fresh"u8, Ref(literal.FreshType));
                    writer.WriteNumber("regular"u8, Ref(literal.RegularType));
                    writer.WriteBoolean("isFresh"u8, literal.IsFreshLiteral);
                    writer.WritePropertyName("value"u8);
                    switch (literal.Value)
                    {
                        case Utf8String value:
                            writer.WriteBase64StringValue(value.Span.ToArray());
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
                    writer.WritePropertyName("types"u8);
                    Refs(composite.Types);
                    if (composite is UnionType union)
                        writer.WriteNumber("origin"u8, Ref(union.Origin));
                    break;
                case TypeParameter parameter:
                    writer.WriteBoolean("this"u8, parameter.IsThisType);
                    break;
                case TypeReference reference when (reference.ObjectFlags & ObjectFlags.Reference) != 0:
                    writer.WriteNumber("target"u8, Ref(reference.Target));
                    writer.WritePropertyName("arguments"u8);
                    Refs(reference.ResolvedTypeArguments);
                    break;
                case IndexType index:
                    writer.WriteNumber("target"u8, Ref(index.Target));
                    writer.WriteNumber("indexFlags"u8, (uint)index.IndexFlags);
                    break;
                case IndexedAccessType indexed:
                    writer.WriteNumber("object"u8, Ref(indexed.ObjectType));
                    writer.WriteNumber("index"u8, Ref(indexed.IndexType));
                    writer.WriteNumber("accessFlags"u8, (uint)indexed.AccessFlags);
                    break;
                case SubstitutionType substitution:
                    writer.WriteNumber("base"u8, Ref(substitution.BaseType));
                    writer.WriteNumber("constraint"u8, Ref(substitution.Constraint));
                    break;
                case TemplateLiteralType template:
                    writer.WriteStartArray("texts"u8);
                    foreach (Utf8String value in template.Texts)
                        writer.WriteStringValue(value.Span);
                    writer.WriteEndArray();
                    writer.WritePropertyName("types"u8);
                    Refs(template.Types);
                    break;
                case StringMappingType mapping:
                    writer.WriteNumber("target"u8, Ref(mapping.Target));
                    break;
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("mappers"u8);
        foreach (var mapper in mappers.Skip(1))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue((int)mapper!.Kind);
            writer.WriteBooleanValue(mapper.MapsThisOnly);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("queries"u8);
        Refs(queryTypes);
        writer.WriteNumber("calls"u8, calls);
        writer.WriteStartArray("comparisons"u8);
        if (input.TryGetProperty("comparisons"u8, out var comparisons))
        {
            var order = new TypeOrder([]);
            foreach (var pair in comparisons.EnumerateArray())
                writer.WriteNumberValue(Math.Sign(order.Compare(values[pair[0].GetInt32()], values[pair[1].GetInt32()])));
        }
        writer.WriteEndArray();
        if (algebraUsed)
        {
            writer.WriteStartArray("diagnostics"u8);
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

    private static Utf8String Text(JsonElement value, Utf8String key) => value.TryGetProperty(key, out var property) ? JsonStrings.GetString(property)! : ""u8;

    private static bool Bool(JsonElement value, Utf8String key) => value.TryGetProperty(key, out var property) && property.GetBoolean();

    private static double Number(Utf8String bits) =>
        BitConverter.UInt64BitsToDouble(ulong.Parse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}

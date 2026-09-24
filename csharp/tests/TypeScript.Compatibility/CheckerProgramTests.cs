using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Numerics;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerProgramTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Checker program assertion {checks + 1}");
            checks++;
        }
        static async ValueTask<CompilerProgram> Build(Dictionary<string, string> sources, CompilerProgram? previous = null)
        {
            var files = sources.ToDictionary(p => p.Key, p => Wtf8.Encode(p.Value));
            var options = new CompilerOptions();
            options.SetRaw("noLib", "true");
            return await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project",
                new("/project/tsconfig.json", options, sources.Keys.ToArray(), [], [], []), previous, concurrency: 4);
        }
        var sources = new Dictionary<string, string>
        {
            ["/project/a.ts"] = "interface I<T> { a: T } namespace N { export interface A {} }",
            ["/project/b.ts"] = "interface I<T> { b: T; self: this } namespace N { export interface B {} }"
        };
        var program = await Build(sources);
        var original = program.SourceFiles[0].Binding.Locals["I"];
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var host = new ProgramScopeHost(context, links);
        var environment = await CheckerSymbols.CreateAsync(program, links, host);
        var merged = environment.Globals["I"];
        Check(merged != original && merged.Declarations.Count == 2 && original.Declarations.Count == 1);
        Check(merged.Members.ContainsKey("a") && merged.Members.ContainsKey("b") && !original.Members.ContainsKey("b"));
        Check(ReferenceEquals(environment.Globals["globalThis"].Exports["I"], merged));
        var declaration = program.SourceFiles[1].Syntax.DescendantsAndSelf().OfType<InterfaceDeclarationNode>().First();
        Check(environment.Declaration(declaration) == merged);
        var type = await host.Scopes.ClassOrInterfaceAsync(merged);
        Check(type.ThisType is { IsThisType: true } && type.ThisType.Constraint == type && type.Target == type);
        Check(type.AllTypeParameters.Count == 2 && type.ResolvedTypeArguments!.Count == 1);
        Check(await host.Scopes.ClassOrInterfaceAsync(merged) == type);
        var updated = await Build(sources, program);
        Check(updated.ReusedSourceFiles == 2 && ReferenceEquals(updated.SourceFiles[0].Binding, program.SourceFiles[0].Binding));
        var otherContext = new TypeContext(true, true);
        var otherLinks = new CheckerLinks();
        var otherHost = new ProgramScopeHost(otherContext, otherLinks);
        var otherEnvironment = await CheckerSymbols.CreateAsync(updated, otherLinks, otherHost);
        Check(otherEnvironment.Globals["I"] != merged && original.Declarations.Count == 1);
        Check((await otherHost.Scopes.ClassOrInterfaceAsync(otherEnvironment.Globals["I"])).Context == otherContext);
        Check(links.Values.Get(environment.UndefinedSymbol).ResolvedType == context.UndefinedWideningType);
        Check(host.Globals.AnyArrayType == context.EmptyObjectType && host.Globals.AutoArrayType != context.EmptyObjectType);

        var cancelledLinks = new CheckerLinks();
        var cancelledHost = new ProgramScopeHost(new(true, true), cancelledLinks);
        using var cancellation = new CancellationTokenSource();
        cancelledHost.BeforeGlobalTypes = cancellation.Cancel;
        try
        {
            await CheckerSymbols.CreateAsync(program, cancelledLinks, cancelledHost, cancellation.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(original.Declarations.Count == 1 && !original.Members.ContainsKey("b"));
        var recoveredLinks = new CheckerLinks();
        var recoveredHost = new ProgramScopeHost(new(true, true), recoveredLinks);
        var recovered = await CheckerSymbols.CreateAsync(program, recoveredLinks, recoveredHost);
        Check(recovered.Globals["I"].Declarations.Count == 2);

        var retryProgram = await Build(
            new() { ["/project/rollback.ts"] = "interface Finished {} interface Stop {} interface Root extends Finished, Stop {}" });
        var retryContext = new TypeContext();
        var retryLinks = new CheckerLinks();
        var retryHost = new ProgramScopeHost(retryContext, retryLinks);
        var retryEnvironment = await CheckerSymbols.CreateAsync(retryProgram, retryLinks, retryHost);
        int resolutions = 0;
        retryHost.BeforeResolveType = () =>
        {
            if (++resolutions == 2)
                throw new OperationCanceledException();
        };
        try
        {
            await retryHost.Scopes.ClassOrInterfaceAsync(retryEnvironment.Globals["Root"]);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(retryLinks.DeclaredTypes.Get(retryEnvironment.Globals["Root"]).DeclaredType is null);
        Check(retryLinks.DeclaredTypes.Get(retryEnvironment.Globals["Finished"]).DeclaredType is null);
        retryHost.BeforeResolveType = null;
        Check((await retryHost.Scopes.ClassOrInterfaceAsync(retryEnvironment.Globals["Root"])).ThisType is null);

        var contextualProgram = await Build(new() { ["/project/contextual.ts"] = "function outer<T>() { const f = value => value; }" });
        var contextualContext = new TypeContext();
        var contextualLinks = new CheckerLinks();
        var contextualHost = new ProgramScopeHost(contextualContext, contextualLinks);
        var contextualEnvironment = await CheckerSymbols.CreateAsync(contextualProgram, contextualLinks, contextualHost);
        var arrow = contextualProgram.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<ArrowFunctionNode>().Single();
        var parameter = contextualContext.NewTypeParameter(new(SymbolFlags.TypeParameter, "Contextual"));
        var signature = contextualContext.NewSignature(0, arrow, [parameter], null, [], contextualContext.UnknownType, null, 0);
        contextualHost.ContextualSignatures[arrow] = signature;
        var scope = await contextualHost.Scopes.OuterAsync(arrow.Body!);
        Check(scope.Count == 2 && scope[1] == parameter && scope[0].Symbol?.Name == "T");

        const int depth = 20_000;
        var source = new StringBuilder();
        for (int i = 0; i < depth - 1; i++)
            source.Append("interface I").Append(i).Append(" extends I").Append(i + 1).Append(" {}\n");
        source.Append("interface I").Append(depth - 1).Append(" { self: this }");
        var deepProgram = await Build(new() { ["/project/deep.ts"] = source.ToString() });
        var deepContext = new TypeContext();
        var deepLinks = new CheckerLinks();
        var deepHost = new ProgramScopeHost(deepContext, deepLinks);
        var deepEnvironment = await CheckerSymbols.CreateAsync(deepProgram, deepLinks, deepHost);
        Check((await deepHost.Scopes.ClassOrInterfaceAsync(deepEnvironment.Globals["I0"])).ThisType is not null);
        Check(deepLinks.DeclaredTypes.Count == depth);
        SyntaxNode nested = deepProgram.SourceFiles[0].Syntax;
        for (int i = 0; i < depth; i++)
            nested = new BlockNode { Parent = nested };
        Check(deepEnvironment.Binding(nested) == deepProgram.SourceFiles[0].Binding);
        Check((await deepHost.Scopes.OuterAsync(nested)).Count == 0);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            await deepHost.Scopes.OuterAsync(nested, cancellation: stop.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Console.WriteLine($"{checks} program/checker ownership assertions; interface and scope depth 20000");
    }

    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                Process(input.RootElement, writer).GetAwaiter().GetResult();
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    private static async Task Process(JsonElement input, Utf8JsonWriter writer)
    {
        var files = input.GetProperty("files").EnumerateObject().ToDictionary(
            p => p.Name,
            p => Convert.FromBase64String(p.Value.GetString()!));
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        if (input.TryGetProperty("options", out var supplied))
            foreach (var property in supplied.EnumerateObject())
                options.Set(property.Name, property.Value);
        var roots = input.GetProperty("roots").EnumerateArray().Select(p => p.GetString()!).ToArray();
        int concurrency = input.GetProperty("concurrency").GetInt32();
        var config = new ParsedConfig("/project/tsconfig.json", options, roots, [], [], []);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project", config, concurrency: concurrency);
        var context = new TypeContext(options.StrictOption("strictNullChecks"),
            options.Boolean("exactOptionalPropertyTypes") ?? false);
        var links = new CheckerLinks();
        var host = new ProgramScopeHost(context, links);
        var environment = await CheckerSymbols.CreateAsync(program, links, host);
        var nodes = program.SourceFiles.SelectMany(file => file.Syntax.DescendantsAndSelf()).ToArray();
        var nodeIds = nodes.Select((node, i) => (node, i)).ToDictionary(p => p.node, p => p.i + 1);
        int Node(SyntaxNode? node) => node is null ? 0 : nodeIds.GetValueOrDefault(node);
        var symbolIds = new Dictionary<Symbol, int>(ReferenceEqualityComparer.Instance);
        var symbols = new List<Symbol>();
        int SymbolId(Symbol? symbol)
        {
            if (symbol is null)
                return 0;
            if (!symbolIds.TryGetValue(symbol, out int id))
            {
                symbolIds.Add(symbol, id = symbols.Count + 1);
                symbols.Add(symbol);
            }
            return id;
        }
        var typeIds = new Dictionary<Type, int>();
        var types = new List<Type>();
        int TypeId(Type? type)
        {
            if (type is null)
                return 0;
            if (!typeIds.TryGetValue(type, out int id))
            {
                typeIds.Add(type, id = types.Count + 1);
                types.Add(type);
            }
            return id;
        }
        void Name(string text) => writer.WriteBase64StringValue(Wtf8.Encode(Symbol.EscapeName(text)));
        void Table(IReadOnlyDictionary<string, Symbol> table)
        {
            writer.WriteStartArray();
            foreach (var (name, symbol) in table.OrderBy(p => p.Key, Comparer<string>.Create(TypeOrder.CompareSymbolNames)))
            {
                writer.WriteStartArray();
                Name(name);
                writer.WriteNumberValue(SymbolId(symbol));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        void TypeIds(IEnumerable<Type>? values)
        {
            if (values is null)
            {
                writer.WriteNullValue();
                return;
            }
            writer.WriteStartArray();
            foreach (var type in values)
                writer.WriteNumberValue(TypeId(type));
            writer.WriteEndArray();
        }
        IReadOnlyList<Type>? OrderedInferences(IReadOnlyList<Type>? values)
        {
            if (values is null)
                return null;
            var result = values.ToArray();
            var groups = new Dictionary<SyntaxNode, List<int>>();
            for (int i = 0; i < values.Count; i++)
            {
                var declaration = values[i].Symbol?.Declarations.FirstOrDefault();
                if (declaration?.Parent is not InferTypeNode infer)
                    continue;
                for (var owner = infer.Parent; owner is not null; owner = owner.Parent)
                    if (owner is ConditionalTypeNode)
                    {
                        if (!groups.TryGetValue(owner, out var positions))
                            groups[owner] = positions = [];
                        positions.Add(i);
                        break;
                    }
            }
            foreach (var positions in groups.Values)
            {
                var parameters = positions.Select(i => values[i]).OrderBy(t => Node(t.Symbol!.Declarations[0])).ToArray();
                for (int i = 0; i < positions.Count; i++)
                    result[positions[i]] = parameters[i];
            }
            return result;
        }
        writer.WriteStartObject();
        writer.WriteStartArray("files");
        foreach (var file in program.SourceFiles)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(file.Syntax.FileName);
            writer.WriteBooleanValue(file.Binding.IsModule);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("globals");
        Table(environment.Globals);
        writer.WriteStartArray("patterns");
        foreach (var pattern in environment.PatternModules)
        {
            writer.WriteStartArray();
            Name(pattern.Pattern);
            writer.WriteNumberValue(SymbolId(pattern.Symbol));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("augmentations");
        Table(environment.PatternAugmentations);
        writer.WritePropertyName("augmentationTargets");
        Table(environment.PatternTargets);
        writer.WriteStartArray("globalTypes");
        foreach (var (name, type) in host.Globals.Types.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(name);
            writer.WriteNumberValue(TypeId(type));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("specialTypes");
        foreach (var symbol in new[]
        {
            environment.UndefinedSymbol,
            environment.ArgumentsSymbol,
            environment.UnknownSymbol,
            environment.GlobalThisSymbol
        })
            writer.WriteNumberValue(TypeId(links.Values.Get(symbol).ResolvedType));
        writer.WriteNumberValue(TypeId(host.Globals.AnyArrayType));
        writer.WriteNumberValue(TypeId(host.Globals.AutoArrayType));
        writer.WriteNumberValue(TypeId(host.Globals.AnyReadonlyArrayType));
        writer.WriteEndArray();
        writer.WriteStartArray("declarations");
        foreach (var node in nodes)
            if (environment.Binding(node)?.Get(node)?.Symbol is not null)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(Node(node));
                writer.WriteNumberValue(SymbolId(environment.Declaration(node)));
                writer.WriteEndArray();
            }
        writer.WriteEndArray();
        writer.WriteStartArray("classes");
        foreach (var node in nodes.Where(
            n => n.Kind is SyntaxKind.ClassDeclaration or SyntaxKind.ClassExpression or SyntaxKind.InterfaceDeclaration))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Node(node));
            writer.WriteNumberValue(TypeId(await host.Scopes.ClassOrInterfaceAsync(environment.Declaration(node)!)));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("scopes");
        foreach (var node in nodes.Where(n => n.Kind is SyntaxKind.TypeReference or SyntaxKind.ThisType or SyntaxKind.TypeParameter))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Node(node));
            TypeIds(OrderedInferences(await host.Scopes.OuterAsync(node)));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        if (input.TryGetProperty("aliases", out var aliasOption) && aliasOption.GetBoolean())
        {
            writer.WriteStartArray("aliases");
            var seenAliases = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
            foreach (var node in nodes)
            {
                var symbol = environment.Declaration(node);
                if (symbol is null || (symbol.Flags & SymbolFlags.Alias) == 0 || !seenAliases.Add(symbol))
                    continue;
                writer.WriteStartArray();
                writer.WriteNumberValue(SymbolId(symbol));
                writer.WriteNumberValue(SymbolId(await host.Aliases.ResolveAsync(symbol)));
                writer.WriteNumberValue(SymbolId(await host.Aliases.ImmediateAsync(symbol)));
                writer.WriteNumberValue((uint)await host.Aliases.FlagsAsync(symbol));
                writer.WriteNumberValue((uint)await host.Aliases.FlagsAsync(symbol, excludeTypeOnly: true));
                writer.WriteNumberValue((uint)await host.Aliases.FlagsAsync(symbol, excludeLocal: true));
                writer.WriteNumberValue(Node(await host.Aliases.TypeOnlyAsync(symbol)));
                writer.WriteNumberValue(Node(await host.Aliases.TypeOnlyAsync(symbol, SymbolFlags.Value)));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        ProgramTypeHost? typeHost = null;
        if (input.TryGetProperty("typeNodes", out var typeOption) && typeOption.GetBoolean())
        {
            typeHost = new(context, links, host);
            writer.WriteStartArray("typeQueries");
            foreach (var node in nodes)
            {
                Type? result = null;
                if (node is TypeAliasDeclarationNode or EnumDeclarationNode)
                    result = await typeHost.Declared.GetAsync(environment.Declaration(node)!);
                else if (node is ITypedNode { Type: { } annotation }
                    && (node is IFunctionSignature
                        || node.Kind is SyntaxKind.VariableDeclaration or SyntaxKind.PropertyDeclaration or SyntaxKind.PropertySignature
                            or SyntaxKind.Parameter or SyntaxKind.IndexSignature))
                    result = await typeHost.Nodes.FromNodeAsync(annotation);
                if (result is null)
                    continue;
                writer.WriteStartArray();
                writer.WriteNumberValue(Node(node));
                writer.WriteNumberValue(TypeId(result));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("awaited", out var awaitedOption) && awaitedOption.GetBoolean())
        {
            writer.WriteStartArray("awaitedQueries");
            foreach (var node in nodes)
                if (node is TypeAliasDeclarationNode or InterfaceDeclarationNode && node is INamedNode { Name: IdentifierNode name }
                    && name.Text.Length > 1 && name.Text[0] == 'A' && char.IsAsciiDigit(name.Text[1]))
                {
                    var type = await typeHost!.Declared.GetAsync(environment.Declaration(node)!);
                    writer.WriteStartArray();
                    writer.WriteNumberValue(Node(node));
                    writer.WriteNumberValue(TypeId(type));
                    writer.WriteNumberValue(TypeId((await typeHost.Awaited.PromisedAsync(type)).Type));
                    writer.WriteNumberValue(TypeId(await typeHost.Awaited.NoAliasAsync(type)));
                    writer.WriteNumberValue(TypeId(await typeHost.Awaited.GetAsync(type)));
                    writer.WriteBooleanValue(await typeHost.Awaited.NeededAsync(type));
                    writer.WriteBooleanValue(await typeHost.Awaited.ThenableAsync(type));
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("expressions", out var expressionOption) && expressionOption.GetBoolean())
        {
            writer.WriteStartArray("expressionQueries");
            foreach (var declaration in nodes.OfType<VariableDeclarationNode>())
                if (declaration.Initializer is { } expression)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(Node(expression));
                    writer.WriteNumberValue(TypeId(await typeHost!.Expressions.CheckAsync(expression)));
                    writer.WriteEndArray();
                }
            writer.WriteEndArray();
            writer.WriteStartArray("suggestions");
            foreach (int code in typeHost!.Suggestions.Order())
                writer.WriteNumberValue(code);
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("constants", out var constantOption) && constantOption.GetBoolean())
        {
            writer.WriteStartArray("constantQueries");
            foreach (var member in nodes.OfType<EnumMemberNode>())
            {
                var result = await typeHost!.EnumValues.GetAsync(member);
                writer.WriteStartArray();
                writer.WriteNumberValue(Node(member));
                if (result.Value is null)
                    writer.WriteNullValue();
                else
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(result.Value is string ? "string" : "number");
                    if (result.Value is string text)
                        writer.WriteBase64StringValue(Wtf8.Encode(text));
                    else
                        writer.WriteStringValue(
                            BitConverter.DoubleToUInt64Bits((double)result.Value).ToString("x16", CultureInfo.InvariantCulture));
                    writer.WriteEndArray();
                }
                writer.WriteBooleanValue(result.IsSyntacticallyString);
                writer.WriteBooleanValue(result.ResolvedOtherFiles);
                writer.WriteBooleanValue(result.HasExternalReferences);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        if (input.TryGetProperty("indexing", out var indexingOption) && indexingOption.GetBoolean())
            await CheckerIndexTests.WriteAsync(writer, nodes, typeHost!, TypeId, Node);
        if (input.TryGetProperty("members", out var memberOption) && memberOption.GetBoolean())
            await CheckerMemberTests.WriteAsync(writer, nodes, environment, typeHost!, TypeId, SymbolId, Node,
                input.TryGetProperty("values", out var valueOption) && valueOption.GetBoolean(),
                input.TryGetProperty("signatures", out var signatureOption) && signatureOption.GetBoolean());
        if (input.TryGetProperty("properties", out var propertyOption) && propertyOption.GetBoolean())
            await CheckerPropertyTests.WriteAsync(writer, nodes, environment, typeHost!, TypeId, SymbolId, Node);
        if (input.TryGetProperty("identity", out var identityOption) && identityOption.GetBoolean())
            await CheckerRelationTests.WriteAsync(writer, nodes, environment, typeHost!, TypeId, Node,
                input.TryGetProperty("assignability", out var assignabilityOption) && assignabilityOption.GetBoolean());
        var variableQueries = typeHost is not null
            && input.TryGetProperty("awaited", out var classifyVariables)
            && classifyVariables.GetBoolean()
            ? new TypeVariables(typeHost.References.TypeArgumentsAsync) : null;
        writer.WriteStartArray("types");
        for (int i = 0; i < types.Count; i++)
        {
            var type = types[i];
            var parameter = type as TypeParameter;
            var intf = type as InterfaceType;
            if (typeHost is not null && type is TypeReference lazy && (type.ObjectFlags & ObjectFlags.Reference) != 0)
                await typeHost.References.TypeArgumentsAsync(lazy);
            bool containsVariables = variableQueries is not null && await variableQueries.CouldContainAsync(type);
            writer.WriteStartArray();
            writer.WriteNumberValue((uint)type.Flags);
            writer.WriteNumberValue((uint)type.ObjectFlags);
            writer.WriteNumberValue(SymbolId(type.Symbol));
            writer.WriteNumberValue(TypeId(type is ObjectType obj ? obj.Target : parameter?.Target));
            TypeIds(type is TypeReference reference ? reference.ResolvedTypeArguments : null);
            TypeIds(intf?.AllTypeParameters);
            writer.WriteNumberValue(intf?.OuterTypeParameterCount ?? 0);
            writer.WriteNumberValue(TypeId(intf?.ThisType));
            writer.WriteBooleanValue(parameter?.IsThisType ?? false);
            writer.WriteNumberValue(TypeId(parameter?.Constraint));
            writer.WriteStringValue(type is IntrinsicType intrinsic ? intrinsic.IntrinsicName : "");
            if (typeHost is not null)
            {
                writer.WriteStartObject();
                if (variableQueries is not null)
                    writer.WriteBoolean("couldContainTypeVariables", containsVariables);
                if (type.Alias is { } typeAlias)
                {
                    writer.WriteStartArray("alias");
                    writer.WriteNumberValue(SymbolId(typeAlias.Symbol));
                    TypeIds(typeAlias.TypeArguments);
                    writer.WriteEndArray();
                }
                if (type is ConstrainedType constrained)
                    writer.WriteNumber("baseConstraint", TypeId(constrained.ResolvedBaseConstraint));
                switch (type)
                {
                    case LiteralType literal:
                        writer.WriteNumber("fresh", TypeId(literal.FreshType));
                        writer.WriteNumber("regular", TypeId(literal.RegularType));
                        writer.WritePropertyName("value");
                        switch (literal.Value)
                        {
                            case string value:
                                writer.WriteBase64StringValue(Wtf8.Encode(value));
                                break;
                            case double value:
                                writer.WriteStringValue(
                                    BitConverter.DoubleToUInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture));
                                break;
                            case BigInteger value:
                                writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
                                break;
                            case bool value:
                                writer.WriteBooleanValue(value);
                                break;
                            default:
                                writer.WriteNullValue();
                                break;
                        }
                        break;
                    case UnionOrIntersectionType composite:
                        writer.WritePropertyName("parts");
                        TypeIds(composite.Types);
                        if (type is UnionType union)
                            writer.WriteNumber("origin", TypeId(union.Origin));
                        break;
                    case TypeParameter:
                        writer.WriteBoolean("distributed", parameter!.IsDistributed);
                        writer.WriteNumber("default", TypeId(parameter.ResolvedDefaultType));
                        writer.WriteNumber("distributedType", TypeId(parameter.DistributedType));
                        break;
                    case IndexType index:
                        writer.WriteNumber("target", TypeId(index.Target));
                        writer.WriteNumber("indexFlags", (uint)index.IndexFlags);
                        break;
                    case IndexedAccessType indexed:
                        writer.WriteNumber("object", TypeId(indexed.ObjectType));
                        writer.WriteNumber("index", TypeId(indexed.IndexType));
                        writer.WriteNumber("accessFlags", (uint)indexed.AccessFlags);
                        break;
                    case TemplateLiteralType template:
                        writer.WriteStartArray("texts");
                        foreach (string text in template.Texts)
                            writer.WriteBase64StringValue(Wtf8.Encode(text));
                        writer.WriteEndArray();
                        writer.WritePropertyName("parts");
                        TypeIds(template.Types);
                        break;
                    case StringMappingType mapping:
                        writer.WriteNumber("target", TypeId(mapping.Target));
                        break;
                    case SubstitutionType substitution:
                        writer.WriteNumber("base", TypeId(substitution.BaseType));
                        writer.WriteNumber("constraint", TypeId(substitution.Constraint));
                        break;
                    case ConditionalType conditional:
                        writer.WriteStartArray("root");
                        writer.WriteNumberValue(Node(conditional.Root.Node));
                        writer.WriteNumberValue(TypeId(conditional.Root.CheckType));
                        writer.WriteNumberValue(TypeId(conditional.Root.ExtendsType));
                        writer.WriteBooleanValue(conditional.Root.IsDistributive);
                        TypeIds(OrderedInferences(conditional.Root.OuterTypeParameters));
                        TypeIds(OrderedInferences(conditional.Root.InferTypeParameters));
                        writer.WriteEndArray();
                        writer.WriteNumber("check", TypeId(conditional.CheckType));
                        writer.WriteNumber("extends", TypeId(conditional.ExtendsType));
                        writer.WriteNumber("true", TypeId(conditional.ResolvedTrueType));
                        writer.WriteNumber("false", TypeId(conditional.ResolvedFalseType));
                        writer.WriteNumber("inferredTrue", TypeId(conditional.ResolvedInferredTrueType));
                        writer.WriteNumber("defaultConstraint", TypeId(conditional.ResolvedDefaultConstraint));
                        writer.WriteNumber("distributiveConstraint", TypeId(conditional.ResolvedConstraintOfDistributive));
                        break;
                }
                if (type is TypeReference referenceType)
                    writer.WriteNumber("node", Node(referenceType.Node));
                if (type is TupleType tuple)
                {
                    writer.WriteStartArray("tuple");
                    writer.WriteStartArray();
                    foreach (var info in tuple.ElementInfos)
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue((uint)info.Flags);
                        writer.WriteNumberValue(Node(info.LabeledDeclaration));
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                    writer.WriteNumberValue(tuple.MinLength);
                    writer.WriteNumberValue(tuple.FixedLength);
                    writer.WriteNumberValue((uint)tuple.CombinedFlags);
                    writer.WriteBooleanValue(tuple.IsReadonly);
                    writer.WriteEndArray();
                }
                if (type is MappedType mapped)
                {
                    writer.WriteNumber("parameter", TypeId(mapped.TypeParameter));
                    writer.WriteNumber("constraint", TypeId(mapped.ConstraintType));
                    writer.WriteNumber("template", TypeId(mapped.TemplateType));
                    writer.WriteNumber("name", TypeId(mapped.NameType));
                }
                if (type is ReverseMappedType reverse)
                {
                    writer.WriteStartArray("reverse");
                    writer.WriteNumberValue(TypeId(reverse.Source));
                    writer.WriteNumberValue(TypeId(reverse.MappedType));
                    writer.WriteNumberValue(TypeId(reverse.ConstraintType));
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("symbols");
        for (int i = 0; i < symbols.Count; i++)
        {
            var symbol = symbols[i];
            writer.WriteStartArray();
            Name(symbol.Name);
            writer.WriteNumberValue((uint)symbol.Flags);
            writer.WriteNumberValue((uint)symbol.CheckFlags);
            writer.WriteNumberValue(SymbolId(symbol.Parent));
            writer.WriteStartArray();
            foreach (var declaration in symbol.Declarations)
                writer.WriteNumberValue(Node(declaration));
            writer.WriteEndArray();
            writer.WriteNumberValue(Node(symbol.ValueDeclaration));
            Table(symbol.Members);
            Table(symbol.Exports);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("diagnostics");
        IEnumerable<int> diagnostics = host.Diagnostics;
        if (typeHost is not null)
            diagnostics = diagnostics.Concat(typeHost.Diagnostics).Concat(typeHost.Instantiation.Diagnostics)
                .Concat(typeHost.Instantiation.ConstraintDependencies.Diagnostics).Concat(typeHost.AlgebraDiagnostics);
        foreach (int code in diagnostics.Order())
            writer.WriteNumberValue(code);
        writer.WriteEndArray();
        if (input.TryGetProperty("numberStrings", out var numberStrings))
        {
            writer.WriteStartArray("numberStrings");
            foreach (var item in numberStrings.EnumerateArray())
            {
                double number = TypeScript.Compiler.Semantics.JsNumber.FromString(Wtf8.DecodeString(item.GetBytesFromBase64()));
                writer.WriteStringValue(
                    double.IsNaN(number) ? "nan" : BitConverter.DoubleToUInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture));
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}

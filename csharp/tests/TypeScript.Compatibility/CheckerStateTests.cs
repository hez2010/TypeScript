using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerStateTests
{
    internal static void Safety()
    {
        int assertions = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Checker state assertion {assertions + 1}");
            assertions++;
        }
        void Reject(Action operation)
        {
            try
            {
                operation();
            }
            catch (ArgumentException)
            {
                assertions++;
                return;
            }
            throw new InvalidOperationException("Cross-context types were accepted");
        }
        var c = new TypeContext(true);
        var other = new TypeContext(true);
        var parameter = c.NewTypeParameter();
        var target = (InterfaceType)c.NewObjectType(ObjectFlags.Interface | ObjectFlags.Reference);
        var aliasSymbol = new Symbol(SymbolFlags.TypeAlias, "A");
        Reject(() => c.CreateTypeReference(target, [other.StringType]));
        Reject(() => c.CreateTypeReference((InterfaceType)other.NewObjectType(ObjectFlags.Interface), []));
        Reject(() => c.GetFreshLiteralType(other.GetStringLiteralType("x")));
        Reject(() => c.NewIntersectionType([c.StringType, other.NumberType]));
        Reject(() => c.GetSubstitutionType(c.StringType, other.NumberType));
        Reject(() => c.CreateAlias(aliasSymbol, [other.NumberType]));
        Reject(() => c.GetUnionFromSortedTypes([c.StringType, c.NumberType], 0, other.CreateAlias(aliasSymbol, [other.StringType])));
        Reject(() => TypeMapper.Create([parameter], [other.StringType]));
        Reject(() => TypeMapper.Function(_ => other.StringType).Map(parameter));
        Reject(() => new TypeOrder([]).Compare(c.StringType, other.StringType));

        Type[] arguments = [c.StringType];
        var reference = c.CreateTypeReference(target, arguments);
        arguments[0] = c.NumberType;
        Check(reference.ResolvedTypeArguments![0] == c.StringType);
        Check(reference == c.CreateTypeReference(target, [c.StringType]));
        Check(reference != c.CreateTypeReference(target, [c.NumberType]));
        Check(c.GetIndexTypeForGenericType(parameter) == c.GetIndexTypeForGenericType(parameter, IndexFlags.NoIndexSignatures));
        Check(c.GetIndexTypeForGenericType(parameter) != c.GetIndexTypeForGenericType(parameter, IndexFlags.StringsOnly));
        var regular = c.GetStringLiteralType("x");
        var fresh = c.GetFreshLiteralType(regular);
        Check(fresh != regular && fresh.RegularType == regular && fresh.FreshType == fresh && regular.FreshType == fresh);
        Check(c.GetFreshLiteralType(fresh) == fresh);
        Check(c.GetNumberLiteralType(double.NaN) == c.GetNumberLiteralType(BitConverter.UInt64BitsToDouble(0x7ff0000000000001)));
        Check(c.GetNumberLiteralType(-0.0) == c.GetNumberLiteralType(0.0));

        var links = new CheckerLinks();
        var symbol = new Symbol(SymbolFlags.Property, "x");
        Check(!links.Values.Has(symbol) && links.Values.TryGet(symbol) is null && links.Values.Count == 0);
        var value = links.Values.Get(symbol);
        value.ResolvedType = c.NumberType;
        Check(links.Values.Get(symbol) == value && links.Values.Count == 1);
        var secondLinks = new CheckerLinks();
        Check(secondLinks.Values.Get(symbol).ResolvedType is null);

        var signature = c.NewSignature(SignatureFlags.HasRestParameter | SignatureFlags.IsOuterCallChain | SignatureFlags.IsNonInferrable,
            null, [parameter], symbol, [symbol], c.StringType, new(TypePredicateKind.Identifier, 0, "x", c.StringType), 1);
        var cloned = c.CloneSignature(signature);
        Check(cloned.Flags == SignatureFlags.HasRestParameter && cloned.ResolvedReturnType is null && cloned.ResolvedTypePredicate is null);
        Check(cloned.Id != signature.Id && cloned.MinArgumentCount == 1 && cloned.ResolvedMinArgumentCount == -1);
        Check(cloned.Parameters[0] == symbol && cloned.TypeParameters[0] == parameter && cloned.ThisParameter == symbol);
        Reject(() => other.CloneSignature(signature));
        var indexInfo = c.NewIndexInfo(c.StringType, c.NumberType, true);
        Check(indexInfo.IsReadonly && indexInfo.KeyType == c.StringType && indexInfo.ValueType == c.NumberType);

        const int depth = 50_000;
        var identity = TypeMapper.Create([], []);
        TypeMapper merged = TypeMapper.Create([parameter], [c.StringType]);
        TypeMapper composed = identity;
        for (int i = 0; i < depth; i++)
        {
            merged = TypeMapper.Merge(merged, identity);
            composed = TypeMapper.Combine(composed, identity, static (type, mapper) => mapper.MapType(type));
        }
        Check(merged.Map(parameter) == c.StringType);
        Check(composed.Map(c.NumberType) == c.NumberType);
        TypeMapper instantiating = identity;
        var otherParameter = c.NewTypeParameter();
        var flip = TypeMapper.Create([parameter, otherParameter], [otherParameter, parameter]);
        for (int i = 0; i < depth; i++)
            instantiating = TypeMapper.CombineAsync(flip, instantiating,
                static (type, mapper, cancellation) => mapper.MapTypeAsync(type, cancellation));
        Check(instantiating.MapAsync(parameter).GetAwaiter().GetResult() == parameter);
        using var cancellation = new CancellationTokenSource();
        var canceling = TypeMapper.Function(type =>
        {
            cancellation.Cancel();
            return type;
        });
        var canceled = TypeMapper.Merge(canceling, merged);
        try
        {
            canceled.Map(parameter, cancellation.Token);
            throw new InvalidOperationException("Cancellation was ignored");
        }
        catch (OperationCanceledException)
        {
            assertions++;
        }
        Check(merged.Map(parameter) == c.StringType);
        int calls = 0;
        var deferred = TypeMapper.Deferred([parameter], [() =>
        {
            calls++;
            return calls == 1 ? c.StringType : c.NumberType;
        }]);
        Check(deferred.Map(parameter) == c.StringType && deferred.Map(parameter) == c.NumberType && calls == 2);
        Check(deferred.Map(c.AnyType) == c.AnyType && calls == 2);

        Type left = c.StringType, right = c.NumberType;
        for (int i = 0; i < depth; i++)
        {
            left = c.GetIndexTypeForGenericType(left);
            right = c.GetIndexTypeForGenericType(right);
        }
        var order = new TypeOrder([]);
        Check(order.Compare(left, right) < 0 && order.Compare(right, left) > 0);
        Check(TypeOrder.CompareText("\ue000", "😀") < 0 && TypeOrder.CompareText("\ud800", "\ue000") < 0);
        string[] names = ["", "a", "aa", "ab", "\0", "\u007f", "\u0080", "\u07ff", "\u0800", "\ud7ff",
            "\ud800", "\ud800a", "\ud800\ud800", "\ud800\udc00", "\ud800\udc01", "\ud800\ue000",
            "\udbff\udfff", "\udc00", "\udfff", "\ue000", "\uffff", "😀"];
        foreach (string prefix in new[] { "", new string('x', 128), "😀\ud800" })
            foreach (string leftName in names)
                foreach (string rightName in names)
                    Check(Math.Sign(TypeOrder.CompareText(prefix + leftName, prefix + rightName))
                        == Math.Sign(Wtf8.Encode(prefix + leftName).AsSpan().SequenceCompareTo(Wtf8.Encode(prefix + rightName))));
        Check(TypeOrder.CompareSymbolNames(Symbol.InternalPrefix + "type", "😀") > 0
            && TypeOrder.CompareSymbolNames(Symbol.InternalPrefix + Symbol.InternalPrefix + "x", "😀") < 0);

        // Symbols in earlier program files sort before source positions in later files.
        var firstFile = new SourceFileNode();
        var lastFile = new SourceFileNode();
        var firstNode = new IdentifierNode { Pos = 100, Parent = firstFile };
        var lastNode = new IdentifierNode { Pos = 1, Parent = lastFile };
        var firstSymbol = new Symbol(SymbolFlags.Interface, "I");
        var lastSymbol = new Symbol(SymbolFlags.Interface, "I");
        firstSymbol.DeclarationList = firstSymbol.DeclarationList.Add(firstNode);
        lastSymbol.DeclarationList = lastSymbol.DeclarationList.Add(lastNode);
        order = new([firstFile, lastFile]);
        Check(order.CompareSymbols(firstSymbol, lastSymbol) < 0);
        Check(order.CompareNodes(firstNode, lastNode) < 0);

        var contexts = new TypeContext[16];
        Parallel.For(0, contexts.Length, i =>
        {
            var context = contexts[i] = new TypeContext(i % 2 == 0);
            for (int j = 0; j < 1000; j++)
                context.GetNumberLiteralType(j);
        });
        Check(contexts.Distinct().Count() == contexts.Length);
        Check(contexts.Select(context => context.GetNumberLiteralType(1)).Distinct().Count() == contexts.Length);
        Console.WriteLine($"{assertions} checker ownership/state assertions; mapper and type-order depth {depth}; 16 independent contexts");
    }

    internal static void Resolutions(JsonElement operations, Utf8JsonWriter writer)
    {
        var context = new TypeContext();
        var links = new CheckerLinks();
        var stack = new TypeResolutionStack(links);
        var entities = new Dictionary<(int Group, int Id), object>();
        object Entity(int id, TypeSystemPropertyName property)
        {
            int group = property switch
            {
                TypeSystemPropertyName.Type or TypeSystemPropertyName.DeclaredType or TypeSystemPropertyName.WriteType
                    or TypeSystemPropertyName.AliasTarget => 0,
                TypeSystemPropertyName.ResolvedReturnType => 1,
                TypeSystemPropertyName.InitializerIsUndefined => 2,
                _ => 3
            };
            if (!entities.TryGetValue((group, id), out var entity))
            {
                entity = group switch
                {
                    0 => new Symbol(SymbolFlags.Property, id.ToString()),
                    1 => context.NewSignature(0, null, [], null, [], null, null, 0),
                    2 => new IdentifierNode(),
                    _ => context.NewObjectType(ObjectFlags.Interface | ObjectFlags.Reference)
                };
                entities.Add((group, id), entity);
            }
            return entity;
        }
        writer.WriteStartArray();
        foreach (var operation in operations.EnumerateArray())
        {
            int Read(string key) => operation.TryGetProperty(key, out var value) ? value.GetInt32() : 0;
            var property = (TypeSystemPropertyName)Read("property");
            object target = Entity(Read("target"), property);
            int result;
            switch (operation.GetProperty("op").GetString())
            {
                case "push":
                    result = stack.Push(target, property) ? 1 : 0;
                    break;
                case "pop":
                    result = stack.Pop() ? 1 : 0;
                    break;
                case "find":
                    result = stack.FindCycleStart(target, property);
                    break;
                case "start":
                    result = stack.ResolutionStart = Read("value");
                    break;
                case "set":
                    Set(target, property, Read("value") != 0);
                    result = links.HasResolvedProperty(target, property) ? 1 : 0;
                    break;
                default:
                    throw new InvalidOperationException("Unknown resolution operation");
            }
            writer.WriteStartArray();
            writer.WriteNumberValue(result);
            writer.WriteNumberValue(stack.Count);
            writer.WriteNumberValue(stack.ResolutionStart);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();

        void Set(object target, TypeSystemPropertyName property, bool resolved)
        {
            Type? type = resolved ? context.StringType : null;
            switch (property)
            {
                case TypeSystemPropertyName.Type:
                    links.Values.Get((Symbol)target).ResolvedType = type;
                    break;
                case TypeSystemPropertyName.DeclaredType:
                    links.TypeAliases.Get((Symbol)target).DeclaredType = type;
                    break;
                case TypeSystemPropertyName.WriteType:
                    links.Values.Get((Symbol)target).WriteType = type;
                    break;
                case TypeSystemPropertyName.AliasTarget:
                    links.Aliases.Get((Symbol)target).AliasTarget = resolved ? new(SymbolFlags.None, "target") : null;
                    break;
                case TypeSystemPropertyName.ResolvedTypeArguments:
                    ((TypeReference)target).ResolvedTypeArguments = resolved ? [] : null;
                    break;
                case TypeSystemPropertyName.ResolvedBaseTypes:
                    ((InterfaceType)target).BaseTypesResolved = resolved;
                    break;
                case TypeSystemPropertyName.ResolvedBaseConstructorType:
                    ((InterfaceType)target).ResolvedBaseConstructorType = type;
                    break;
                case TypeSystemPropertyName.ResolvedReturnType:
                    ((Signature)target).ResolvedReturnType = type;
                    break;
                case TypeSystemPropertyName.ResolvedBaseConstraint:
                    ((ConstrainedType)target).ResolvedBaseConstraint = type;
                    break;
                case TypeSystemPropertyName.InitializerIsUndefined:
                    links.Nodes.Get((SyntaxNode)target).Flags = resolved ? NodeCheckFlags.InitializerIsUndefinedComputed : 0;
                    break;
                default:
                    throw new InvalidOperationException("Unknown resolution property");
            }
        }
    }
}

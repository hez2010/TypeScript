using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
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
        ["uniqueLiteral"] = c.UniqueLiteralType
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
            Type type = op switch
            {
                "builtin" => builtins[text],
                "string" => c.GetStringLiteralType(Wtf8.DecodeString(Convert.FromBase64String(text))),
                "number" => c.GetNumberLiteralType(Number(text)),
                "bigint" => c.GetBigIntLiteralType(BigInteger.Parse(text, CultureInfo.InvariantCulture)),
                "enumNumber" => c.GetEnumLiteralType(Number(text), SymbolFor(symbol)!, SymbolFor(member)!),
                "enumString" => c.GetEnumLiteralType(text, SymbolFor(symbol)!, SymbolFor(member)!),
                "computedEnum" => c.NewComputedEnumType(SymbolFor(symbol)!),
                "fresh" => c.GetFreshLiteralType((LiteralType)args[0]),
                "regular" => ((LiteralType)args[0]).RegularType,
                "parameter" => NewParameter(),
                "distributed" => NewDistributed(),
                "object" => c.NewObjectType((ObjectFlags)flags, SymbolFor(symbol)),
                "reference" => c.CreateTypeReference((InterfaceType)args[0], args.AsSpan(1), (ObjectFlags)flags),
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
                _ => throw new InvalidOperationException(op)
            };
            values.Add(type);
            TypeParameter NewParameter()
            {
                var parameter = c.NewTypeParameter(SymbolFor(symbol));
                parameter.IsThisType = Bool(step, "this");
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
            writer.WriteString("symbol", type.Symbol is null ? "" : symbolNames[type.Symbol]);
            writer.WriteBoolean("literal", type.IsLiteral);
            writer.WriteBoolean("unit", type.IsUnit);
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

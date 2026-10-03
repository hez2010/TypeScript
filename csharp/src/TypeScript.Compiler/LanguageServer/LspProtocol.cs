using System.Text.Json;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compiler.LanguageServer;

/// <summary>Validates the generated LSP wire contracts without reflection or recursive object traversal.</summary>
internal static partial class LspProtocol
{
    private enum Kind { Any, String, Bool, Uint32, Int32, Float64, Null, NoParams, EmptyObject, Pointer, Array, Tuple, Map, Ref, Object, Literal, Kind, Keys, Tag, AnyOf, Invalid }
    private sealed record Field(string Name, string Type, bool Required, bool RejectNull);
    private sealed record Schema(Kind Kind, string? Type = null, Field[]? Fields = null, Dictionary<string, Schema>? Cases = null,
        string[]? Keys = null, string? Field = null, string? Value = null, string[]? Types = null);
    private static readonly Dictionary<string, Schema> Schemas = CreateSchemas();

    internal static void ValidateParams(Utf8String method, JsonElement value)
    {
        if (!Requests.TryGetValue(method.ToString(), out var type)) return;
        if (type == "NoParams")
        {
            if (value.ValueKind != JsonValueKind.Undefined) Fail("expected no params, got " + value.GetRawText());
            return;
        }
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) Fail("params must be an object or array");
        ValidateType(type, value);
    }

    internal static void ValidateResponse(Utf8String method, JsonElement value)
    {
        if (Responses.TryGetValue(method.ToString(), out var type)) ValidateType(type, value);
    }

    internal static void ValidateType(string type, JsonElement value)
    {
        CheckDuplicateNames(value);
        Validate(Schemas[type], value);
    }

    private static void Validate(Schema initial, JsonElement value)
    {
        Stack<(Schema Schema, JsonElement Value)> pending = new();
        pending.Push((initial, value));
        while (pending.TryPop(out var item))
        {
            var (schema, current) = item;
            var kind = current.ValueKind;
            switch (schema.Kind)
            {
                case Kind.Any: break;
                case Kind.Ref: pending.Push((Schemas[schema.Type!], current)); break;
                case Kind.Pointer:
                    if (kind != JsonValueKind.Null) pending.Push((Schemas[schema.Type!], current));
                    break;
                case Kind.String:
                    if (kind is not (JsonValueKind.String or JsonValueKind.Null)) Mismatch("string", current);
                    break;
                case Kind.Bool:
                    if (kind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) Mismatch("boolean", current);
                    break;
                case Kind.Int32:
                    if (kind != JsonValueKind.Null && (kind != JsonValueKind.Number || !current.TryGetInt32(out _))) Mismatch("int32", current);
                    break;
                case Kind.Uint32:
                    if (kind != JsonValueKind.Null && (kind != JsonValueKind.Number || !current.TryGetUInt32(out _))) Mismatch("uint32", current);
                    break;
                case Kind.Float64:
                    if (kind != JsonValueKind.Null && (kind != JsonValueKind.Number || !current.TryGetDouble(out double number) || !double.IsFinite(number))) Mismatch("float64", current);
                    break;
                case Kind.Null:
                    if (kind != JsonValueKind.Null) Fail("expected null, got " + current.GetRawText());
                    break;
                case Kind.Literal:
                    if (current.GetRawText() != schema.Value) Fail("expected literal " + schema.Value + ", got " + current.GetRawText());
                    break;
                case Kind.EmptyObject:
                    if (kind is not (JsonValueKind.Object or JsonValueKind.Null)) Mismatch("object", current);
                    break;
                case Kind.Object:
                    if (kind != JsonValueKind.Object) Mismatch("object", current);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in current.EnumerateObject())
                    {
                        string name = RawName(property);
                        var field = System.Array.Find(schema.Fields!, field => field.Name == name);
                        if (field is null) continue;
                        seen.Add(name);
                        if (field.RejectNull && property.Value.ValueKind == JsonValueKind.Null) Fail($"null value is not allowed for field \"{name}\"");
                        pending.Push((Schemas[field.Type], property.Value));
                    }
                    var missing = schema.Fields!.Where(field => field.Required && !seen.Contains(field.Name)).Select(field => field.Name).ToArray();
                    if (missing.Length != 0) Fail("missing required properties: " + string.Join(", ", missing));
                    break;
                case Kind.Array:
                case Kind.Tuple:
                    if (kind == JsonValueKind.Null) break;
                    if (kind != JsonValueKind.Array) Mismatch("array", current);
                    if (schema.Kind == Kind.Tuple && current.GetArrayLength() != 2) Fail("expected array of length 2");
                    foreach (var element in current.EnumerateArray()) pending.Push((Schemas[schema.Type!], element));
                    break;
                case Kind.Map:
                    if (kind == JsonValueKind.Null) break;
                    if (kind != JsonValueKind.Object) Mismatch("object", current);
                    foreach (var property in current.EnumerateObject()) pending.Push((Schemas[schema.Type!], property.Value));
                    break;
                case Kind.Kind:
                case Kind.Keys:
                case Kind.Tag:
                    string selector = "default";
                    if (schema.Kind == Kind.Kind) selector = kind switch
                    {
                        JsonValueKind.Object => "{", JsonValueKind.Array => "[", JsonValueKind.String => "\"", JsonValueKind.Number => "0",
                        JsonValueKind.Null => "n", JsonValueKind.True => "t", JsonValueKind.False => "f", _ => "default",
                    };
                    else if (kind == JsonValueKind.Object)
                    {
                        foreach (var property in current.EnumerateObject())
                        {
                            string name = RawName(property);
                            if (schema.Kind == Kind.Tag && name == schema.Field) { selector = property.Value.GetRawText(); break; }
                            if (schema.Kind == Kind.Keys && System.Array.IndexOf(schema.Keys!, name) is >= 0 and var index)
                            { selector = index.ToString(System.Globalization.CultureInfo.InvariantCulture); break; }
                        }
                    }
                    if (!schema.Cases!.TryGetValue(selector, out var branch) && !schema.Cases.TryGetValue("default", out branch)) Mismatch("union", current);
                    pending.Push((branch!, current));
                    break;
                case Kind.AnyOf:
                    bool matched = false;
                    foreach (var candidate in schema.Types!)
                    {
                        try { Validate(Schemas[candidate], current); matched = true; break; }
                        catch (RpcException) { }
                    }
                    if (!matched) Mismatch("union", current);
                    break;
                default: Mismatch("union", current); break;
            }
        }
    }

    // The reference struct codec and discriminator lookup compare encoded property names.
    private static string RawName(JsonProperty property)
    {
        string raw = property.ToString();
        for (int i = 1; i < raw.Length; i++)
        {
            if (raw[i] == '\\') { i++; continue; }
            if (raw[i] == '"') return raw[1..i];
        }
        return property.Name;
    }

    private static void CheckDuplicateNames(JsonElement value)
    {
        Stack<JsonElement> pending = new(); pending.Push(value);
        while (pending.TryPop(out var current))
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> seen = new(StringComparer.Ordinal);
                foreach (var property in current.EnumerateObject())
                {
                    if (!seen.Add(property.Name)) Fail("duplicate object member name " + property.Name);
                    pending.Push(property.Value);
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
                foreach (var element in current.EnumerateArray()) pending.Push(element);
        }
    }

    private static void Mismatch(string expected, JsonElement value) => Fail("expected " + expected + ", got " + value.ValueKind);
    private static void Fail(string message) => throw new RpcException(-32602, Utf8String.FromString("InvalidParams: " + message));
}

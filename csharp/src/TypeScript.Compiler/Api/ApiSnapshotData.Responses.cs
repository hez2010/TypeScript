using System.Globalization;
using System.Numerics;
using System.Text.Json;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Protocol;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.Api;

internal sealed partial class ApiSnapshotData
{
    internal async ValueTask<RpcResponse> SymbolResponseAsync(Symbol? symbol, Utf8String project, CancellationToken cancellation)
    {
        if (symbol is null) return RpcResponse.Null;
        var registered = Register(symbol, project);
        var declarations = new List<Utf8String>(symbol.Declarations.Count);
        foreach (var declaration in symbol.Declarations)
            declarations.Add(await NodeHandleAsync(declaration, cancellation).ConfigureAwait(false));
        var value = symbol.ValueDeclaration is { } node ? await NodeHandleAsync(node, cancellation).ConfigureAwait(false) : default;
        return RpcResponse.Json(writer =>
        {
            writer.WriteStartObject(); writer.WriteNumber("id"u8, registered.Id);
            ApiJson.String(writer, "project"u8, registered.Project);
            ApiJson.String(writer, "name"u8, Binding.Symbol.EscapeName(symbol.Name));
            writer.WriteNumber("flags"u8, (uint)symbol.Flags); writer.WriteNumber("checkFlags"u8, (uint)symbol.CheckFlags);
            if (declarations.Count > 0) ApiJson.Strings(writer, "declarations"u8, declarations);
            if (!value.IsEmpty) ApiJson.String(writer, "valueDeclaration"u8, value);
            if (symbol.Parent is { } parent) writer.WriteNumber("parent"u8, parent.Id);
            if (symbol.ExportSymbol is { } export) writer.WriteNumber("exportSymbol"u8, export.Id);
            writer.WriteEndObject();
        });
    }

    internal async ValueTask<RpcResponse> TypeResponseAsync(Type? type, Utf8String project, CancellationToken cancellation)
    {
        if (type is null) return RpcResponse.Null;
        uint id = Register(type, project);
        Utf8String[]? labels = null;
        if (type is TupleType tuple && tuple.Target == type && tuple.ElementInfos.Any(element => element.LabeledDeclaration is not null))
        {
            labels = new Utf8String[tuple.ElementInfos.Count];
            for (int i = 0; i < labels.Length; i++)
                if (tuple.ElementInfos[i].LabeledDeclaration is { } declaration)
                    labels[i] = await NodeHandleAsync(declaration, cancellation).ConfigureAwait(false);
        }
        return RpcResponse.Json(writer =>
        {
            writer.WriteStartObject(); writer.WriteNumber("id"u8, id); writer.WriteNumber("flags"u8, (uint)type.Flags);
            writer.WriteNumber("objectFlags"u8, type is ObjectType ? (uint)type.ObjectFlags : 0);
            writer.WriteBoolean("isTupleType"u8, type is TypeReference { Target: TupleType });
            writer.WriteBoolean("isThisType"u8, type is TypeParameter { IsThisType: true });
            writer.WritePropertyName("value"u8);
            WriteLiteral(writer, type is LiteralType valueType && (type.Flags & TypeFlags.Literal) != 0 ? valueType.Value : null);
            if (type.Symbol is { } symbol) writer.WriteNumber("symbol"u8, symbol.Id);
            if (type.Alias is { } alias)
            {
                WriteTypes(writer, "aliasTypeArguments"u8, alias.TypeArguments);
                writer.WriteNumber("aliasSymbol"u8, alias.Symbol.Id);
            }
            switch (type)
            {
                case LiteralType literal:
                    WriteType(writer, "freshType"u8, literal.FreshType);
                    WriteType(writer, "regularType"u8, literal.RegularType);
                    break;
                case ObjectType obj:
                    if ((obj.ObjectFlags & ObjectFlags.Reference) != 0)
                    {
                        WriteType(writer, "target"u8, obj.Target);
                        if (obj is TupleType target && target.Target == target)
                        {
                            if (target.ElementInfos.Count > 0)
                            {
                                writer.WriteStartArray("elementFlags"u8);
                                foreach (var element in target.ElementInfos) writer.WriteNumberValue((uint)element.Flags);
                                writer.WriteEndArray();
                            }
                            writer.WriteNumber("fixedLength"u8, target.FixedLength);
                            writer.WriteBoolean("readonly"u8, target.IsReadonly);
                            if (labels is not null) ApiJson.Strings(writer, "labeledElementDeclarations"u8, labels);
                        }
                    }
                    if ((obj.ObjectFlags & ObjectFlags.ClassOrInterface) != 0 && obj is InterfaceType iface)
                    {
                        WriteTypes(writer, "typeParameters"u8, iface.AllTypeParameters.SkipLast(1));
                        WriteTypes(writer, "outerTypeParameters"u8, iface.AllTypeParameters.Take(iface.OuterTypeParameterCount));
                        WriteTypes(writer, "localTypeParameters"u8, iface.AllTypeParameters.SkipLast(1).Skip(iface.OuterTypeParameterCount));
                        WriteType(writer, "thisType"u8, iface.ThisType);
                    }
                    break;
                case IndexType index: WriteType(writer, "target"u8, index.Target); break;
                case IndexedAccessType indexed:
                    WriteType(writer, "objectType"u8, indexed.ObjectType); WriteType(writer, "indexType"u8, indexed.IndexType); break;
                case ConditionalType conditional:
                    WriteType(writer, "checkType"u8, conditional.CheckType); WriteType(writer, "extendsType"u8, conditional.ExtendsType); break;
                case SubstitutionType substitution:
                    WriteType(writer, "baseType"u8, substitution.BaseType); WriteType(writer, "substConstraint"u8, substitution.Constraint); break;
                case TemplateLiteralType template:
                    if (template.Texts.Count > 0) ApiJson.Strings(writer, "texts"u8, template.Texts);
                    break;
                case StringMappingType mapping: WriteType(writer, "target"u8, mapping.Target); break;
                case IntrinsicType intrinsic:
                    if (!intrinsic.IntrinsicName.IsEmpty) ApiJson.String(writer, "intrinsicName"u8, intrinsic.IntrinsicName);
                    break;
            }
            writer.WriteEndObject();
        });
    }

    internal async ValueTask<RpcResponse> SignatureResponseAsync(Signature? signature, Utf8String project, CancellationToken cancellation)
    {
        if (signature is null) return RpcResponse.Null;
        uint id = Register(signature, project);
        var declaration = signature.Declaration is { } node ? await NodeHandleAsync(node, cancellation).ConfigureAwait(false) : default;
        return RpcResponse.Json(writer =>
        {
            writer.WriteStartObject(); writer.WriteNumber("id"u8, id); writer.WriteNumber("flags"u8, (uint)signature.Flags);
            if (!declaration.IsEmpty) ApiJson.String(writer, "declaration"u8, declaration);
            WriteTypes(writer, "typeParameters"u8, signature.TypeParameters);
            if (signature.Parameters.Count > 0)
            {
                writer.WriteStartArray("parameters"u8);
                foreach (var parameter in signature.Parameters) writer.WriteNumberValue(parameter.Id);
                writer.WriteEndArray();
            }
            if (signature.ThisParameter is { } thisParameter) writer.WriteNumber("thisParameter"u8, thisParameter.Id);
            if (signature.Target is { } target) writer.WriteNumber("target"u8, target.Id);
            writer.WriteEndObject();
        });
    }

    internal async ValueTask<RpcResponse> IndexResponseAsync(IndexInfo? info, Utf8String project, CancellationToken cancellation)
    {
        if (info is null) return RpcResponse.Null;
        var key = await TypeResponseAsync(info.KeyType, project, cancellation).ConfigureAwait(false);
        var value = await TypeResponseAsync(info.ValueType, project, cancellation).ConfigureAwait(false);
        var declaration = info.Declaration is { } node ? await NodeHandleAsync(node, cancellation).ConfigureAwait(false) : default;
        return RpcResponse.Json(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("keyType"u8); writer.WriteRawValue(key.Data.Span, skipInputValidation: true);
            writer.WritePropertyName("valueType"u8); writer.WriteRawValue(value.Data.Span, skipInputValidation: true);
            writer.WriteBoolean("isReadonly"u8, info.IsReadonly);
            if (!declaration.IsEmpty) ApiJson.String(writer, "declaration"u8, declaration);
            writer.WriteEndObject();
        });
    }

    internal async ValueTask<RpcResponse> PredicateResponseAsync(TypePredicate? predicate, Utf8String project, CancellationToken cancellation)
    {
        if (predicate is null) return RpcResponse.Null;
        var type = await TypeResponseAsync(predicate.Type, project, cancellation).ConfigureAwait(false);
        return RpcResponse.Json(writer =>
        {
            writer.WriteStartObject(); writer.WriteNumber("kind"u8, (int)predicate.Kind);
            writer.WriteNumber("parameterIndex"u8, predicate.ParameterIndex);
            if (!predicate.ParameterName.IsEmpty) ApiJson.String(writer, "parameterName"u8, predicate.ParameterName);
            if (predicate.Type is not null)
            { writer.WritePropertyName("type"u8); writer.WriteRawValue(type.Data.Span, skipInputValidation: true); }
            writer.WriteEndObject();
        });
    }

    private static void WriteType(Utf8JsonWriter writer, ReadOnlySpan<byte> name, Type? type)
    { if (type is not null) writer.WriteNumber(name, type.Id); }

    private static void WriteTypes(Utf8JsonWriter writer, ReadOnlySpan<byte> name, IEnumerable<Type> types)
    {
        using var enumerator = types.GetEnumerator();
        if (!enumerator.MoveNext()) return;
        writer.WriteStartArray(name);
        do { writer.WriteNumberValue(enumerator.Current.Id); } while (enumerator.MoveNext());
        writer.WriteEndArray();
    }

    internal static void WriteLiteral(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case Utf8String text: JsonStrings.WriteString(writer, text.Span); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case double number when double.IsFinite(number): writer.WriteNumberValue(number); break;
            case double number: writer.WriteStringValue(double.IsNaN(number) ? "NaN" : number > 0 ? "+Infinity" : "-Infinity"); break;
            case BigInteger bigint: writer.WriteStringValue(bigint.ToString(CultureInfo.InvariantCulture)); break;
            default: writer.WriteNullValue(); break;
        }
    }
}

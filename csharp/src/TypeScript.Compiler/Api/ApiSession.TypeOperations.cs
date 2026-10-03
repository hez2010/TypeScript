using System.Text.Json;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Protocol;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.Api;

public sealed partial class ApiSession
{
    private static readonly HashSet<Utf8String> typeOperationMethods =
    [
        "getAnyType"u8, "getStringType"u8, "getNumberType"u8, "getBooleanType"u8, "getVoidType"u8,
        "getUndefinedType"u8, "getNullType"u8, "getNeverType"u8, "getUnknownType"u8, "getBigIntType"u8,
        "getESSymbolType"u8, "getNonPrimitiveType"u8, "getWellKnownSymbols"u8, "getWellKnownSignatures"u8,
        "getAwaitedType"u8, "getBaseTypeOfLiteralType"u8, "getNonNullableType"u8, "getWidenedType"u8,
        "getParameterType"u8, "getTypeParameterAtPosition"u8, "getRestTypeOfSignature"u8, "getTypePredicateOfSignature"u8,
        "isArrayType"u8, "isArrayLikeType"u8, "isTypeAssignableTo"u8, "isReadonlySymbol"u8,
        "getBaseTypes"u8, "getPropertiesOfType"u8, "getApparentPropertiesOfType"u8, "getApparentType"u8, "getReducedType"u8,
        "getIndexInfosOfType"u8, "getIndexInfoOfType"u8, "getIndexTypeOfTypeByKind"u8,
        "getConstraintOfTypeParameter"u8, "getDefaultFromTypeParameter"u8, "getBaseConstraintOfType"u8,
        "getPropertyOfType"u8, "getTypeOfPropertyOfType"u8, "getTypeArguments"u8,
        "getTrueTypeOfConditionalType"u8, "getFalseTypeOfConditionalType"u8, "getFullyQualifiedName"u8,
        "getImmediateAliasedSymbol"u8, "getTargetSymbol"u8, "getExportSymbolOfSymbolForChecker"u8, "getMemberInModuleExports"u8,
    ];

    private static async ValueTask<RpcResponse> TypeOperationAsync(Utf8String method, JsonElement parameters,
        ApiSnapshotData data, Utf8String project, Checker checker, CancellationToken cancellation)
    {
        var context = checker.Context;
        ValueTask<RpcResponse> TypeResult(Type? type) => data.TypeResponseAsync(type, project, cancellation);
        ValueTask<RpcResponse> SymbolResult(Symbol? symbol) => data.SymbolResponseAsync(symbol, project, cancellation);
        ValueTask<RpcResponse> IndexResult(IndexInfo? info) => data.IndexResponseAsync(info, project, cancellation);
        Type ResolveType() => data.Type(project, ApiJson.UInt32(parameters, "type"u8));
        Type ResolveObject() => data.Type(project, ApiJson.UInt32(parameters, "objectId"u8));
        Symbol ResolveSymbol() => data.Symbol(ApiJson.UInt64(parameters, "symbol"u8)).Symbol;
        Signature ResolveSignature() => data.Signature(project, ApiJson.UInt64(parameters, "signature"u8));
        var intrinsic = method == "getAnyType"u8 ? context.AnyType : method == "getStringType"u8 ? context.StringType
            : method == "getNumberType"u8 ? context.NumberType : method == "getBooleanType"u8 ? context.BooleanType
            : method == "getVoidType"u8 ? context.VoidType : method == "getUndefinedType"u8 ? context.UndefinedType
            : method == "getNullType"u8 ? context.NullType : method == "getNeverType"u8 ? context.NeverType
            : method == "getUnknownType"u8 ? context.UnknownType : method == "getBigIntType"u8 ? context.BigIntType
            : method == "getESSymbolType"u8 ? context.ESSymbolType : method == "getNonPrimitiveType"u8 ? context.NonPrimitiveType : null;
        if (intrinsic is not null) return await TypeResult(intrinsic);
        if (method == "getWellKnownSymbols"u8)
        {
            var unknown = data.Register(checker.Symbols.UnknownSymbol, project);
            var undefined = data.Register(checker.Symbols.UndefinedSymbol, project);
            var arguments = data.Register(checker.Symbols.ArgumentsSymbol, project);
            return RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); writer.WriteNumber("unknown"u8, unknown.Id);
                writer.WriteNumber("undefined"u8, undefined.Id); writer.WriteNumber("arguments"u8, arguments.Id); writer.WriteEndObject();
            });
        }
        if (method == "getWellKnownSignatures"u8)
        {
            uint unknown = data.Register(checker.CallSignatures.Unknown, project);
            return RpcResponse.Json(writer => { writer.WriteStartObject(); writer.WriteNumber("unknown"u8, unknown); writer.WriteEndObject(); });
        }
        if (method == "getAwaitedType"u8) return await TypeResult(await checker.Awaited.GetAsync(ResolveType(), cancellation: cancellation));
        if (method == "getBaseTypeOfLiteralType"u8) return await TypeResult(await checker.Widening.LiteralBaseAsync(ResolveType(), cancellation));
        if (method == "getNonNullableType"u8) return await TypeResult(await checker.Facts.NonNullableAsync(ResolveObject(), cancellation));
        if (method == "getWidenedType"u8) return await TypeResult(await checker.Widening.GetAsync(ResolveType(), cancellation));
        if (method == "getParameterType"u8 || method == "getTypeParameterAtPosition"u8)
        {
            var signature = ResolveSignature(); int index = ApiJson.Int32(parameters, "index"u8);
            if (index < 0) throw new ApiException("invalid parameter index");
            var type = await checker.Parameters.AtAsync(signature, index, cancellation);
            if (method == "getTypeParameterAtPosition"u8 && type is IndexType { Target: TypeParameter { IsThisType: true } parameter }
                && await checker.Instantiation.Constraints.BaseConstraintAsync(parameter, cancellation) is { } constraint)
                type = await checker.Keys.GetAsync(constraint, cancellation: cancellation);
            return await TypeResult(type);
        }
        if (method == "getRestTypeOfSignature"u8)
        {
            var signature = ResolveSignature(); Type? rest = null;
            if (signature.HasRestParameter)
            {
                rest = await checker.Values.GetAsync(signature.Parameters[^1], cancellation);
                if (rest is TypeReference { Target: TupleType } tuple) rest = await checker.TupleRestAsync(tuple, cancellation);
                if (rest is not null) rest = await checker.NumberIndexAsync(rest, cancellation);
            }
            return await TypeResult(rest ?? context.AnyType);
        }
        if (method == "getTypePredicateOfSignature"u8)
            return await data.PredicateResponseAsync(await checker.Signatures.PredicateAsync(ResolveSignature(), cancellation), project, cancellation);
        if (method == "isArrayType"u8) return RpcResponse.Boolean(checker.IsArray(ResolveType()));
        if (method == "isArrayLikeType"u8) return RpcResponse.Boolean(await checker.ArrayLikeAsync(ResolveType(), cancellation));
        if (method == "isReadonlySymbol"u8) return RpcResponse.Boolean(checker.IsReadonly(ResolveSymbol()));
        if (method == "isTypeAssignableTo"u8) return RpcResponse.Boolean(await checker.Relations.RelatedAsync(
            data.Type(project, ApiJson.UInt32(parameters, "source"u8)), data.Type(project, ApiJson.UInt32(parameters, "target"u8)),
            RelationKind.Assignable, cancellation));
        if (method == "getBaseTypes"u8) return await ResponseArrayAsync(await checker.Bases.GetAsync((InterfaceType)ResolveType(), cancellation), TypeResult);
        if (method == "getPropertiesOfType"u8) return await ResponseArrayAsync(await checker.Properties.GetAsync(ResolveType(), cancellation), SymbolResult);
        if (method == "getApparentPropertiesOfType"u8) return await ResponseArrayAsync(await checker.GetApparentPropertiesAsync(ResolveObject(), cancellation), SymbolResult);
        if (method == "getApparentType"u8) return await TypeResult(await checker.Views.ApparentAsync(ResolveObject(), cancellation));
        if (method == "getReducedType"u8) return await TypeResult(await checker.Views.ReducedAsync(ResolveObject(), cancellation));
        if (method == "getIndexInfosOfType"u8) return await ResponseArrayAsync(await checker.IndexesAsync(ResolveType(), cancellation), IndexResult);
        if (method == "getIndexInfoOfType"u8 || method == "getIndexTypeOfTypeByKind"u8)
        {
            var type = ResolveType(); int kind = ApiJson.Int32(parameters, "kind"u8);
            var key = kind == 0 ? context.StringType : kind == 1 ? context.NumberType : throw new ApiException($"invalid index kind {kind}");
            var info = (await checker.IndexesAsync(type, cancellation)).FirstOrDefault(index => index.KeyType == key);
            return method == "getIndexInfoOfType"u8 ? await IndexResult(info) : await TypeResult(info?.ValueType);
        }
        if (method == "getConstraintOfTypeParameter"u8) return await TypeResult(await checker.Instantiation.Constraints.ParameterConstraintAsync((TypeParameter)ResolveObject(), cancellation));
        if (method == "getDefaultFromTypeParameter"u8) return await TypeResult(await checker.Instantiation.Constraints.DefaultAsync((TypeParameter)ResolveObject(), cancellation));
        if (method == "getBaseConstraintOfType"u8) return await TypeResult(await checker.Instantiation.Constraints.BaseConstraintAsync(ResolveType(), cancellation));
        if (method == "getPropertyOfType"u8 || method == "getTypeOfPropertyOfType"u8)
        {
            var property = await checker.Properties.PropertyAsync(ResolveType(), ApiJson.String(parameters, "name"u8), cancellation: cancellation);
            return method == "getPropertyOfType"u8 ? await SymbolResult(property)
                : await TypeResult(property is null ? null : await checker.Values.GetAsync(property, cancellation));
        }
        if (method == "getTypeArguments"u8) return await ResponseArrayAsync(await checker.References.TypeArgumentsAsync((TypeReference)ResolveType(), cancellation), TypeResult);
        if (method == "getTrueTypeOfConditionalType"u8) return await TypeResult(await checker.Instantiation.Constraints.ConditionalTrueAsync((ConditionalType)ResolveObject(), cancellation: cancellation));
        if (method == "getFalseTypeOfConditionalType"u8) return await TypeResult(await checker.Instantiation.Constraints.ConditionalFalseAsync((ConditionalType)ResolveObject(), cancellation));
        if (method == "getFullyQualifiedName"u8) return RpcResponse.String(await checker.FullyQualifiedNameAsync(ResolveSymbol(), null, cancellation));
        if (method == "getImmediateAliasedSymbol"u8) return await SymbolResult(await checker.GetImmediateAliasedSymbolAsync(ResolveSymbol(), cancellation));
        if (method == "getTargetSymbol"u8)
        {
            var symbol = ResolveSymbol();
            return await SymbolResult((symbol.CheckFlags & CheckFlags.Instantiated) != 0 ? checker.Links.Values.Get(symbol).Target : symbol);
        }
        if (method == "getExportSymbolOfSymbolForChecker"u8)
        {
            var symbol = ResolveSymbol(); return await SymbolResult(checker.Symbols.Merger.GetMergedSymbol(symbol.ExportSymbol ?? symbol));
        }
        if (method == "getMemberInModuleExports"u8)
            return await SymbolResult(await checker.GetMemberInModuleExportsAsync(ResolveSymbol(), ApiJson.String(parameters, "name"u8), cancellation));
        throw new InvalidOperationException($"No type operation for {method}");
    }
}

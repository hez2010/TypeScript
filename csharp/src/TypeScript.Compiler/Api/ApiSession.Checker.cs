using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.Api;

public sealed partial class ApiSession
{
    private static readonly HashSet<Utf8String> checkerMethods =
    [
        "getSymbolAtLocation"u8, "getSymbolsAtLocations"u8, "getSymbolOfSourceFile"u8, "getSymbolsOfSourceFiles"u8,
        "getTypeAtLocation"u8, "getTypeAtLocations"u8, "getTypeOfSymbol"u8, "getTypesOfSymbols"u8,
        "getDeclaredTypeOfSymbol"u8, "getNonMissingTypeOfSymbol"u8, "getSignaturesOfType"u8, "getResolvedSignature"u8,
        "getDocumentationComment"u8, "getJsDocTags"u8,
        "getReferencesToSymbolInFile"u8,
        "getContextualType"u8, "getContextualTypeForArgument"u8, "getTypeFromTypeNode"u8, "getTypeOfSymbolAtLocation"u8,
        "getReturnTypeOfSignature"u8, "getExportsOfModule"u8, "getAliasedSymbol"u8, "getShorthandAssignmentValueSymbol"u8,
        "getExportSpecifierLocalTargetSymbol"u8,
        "getSymbolAtPosition"u8, "getSymbolsAtPositions"u8, "getTypeAtPosition"u8, "getTypesAtPositions"u8,
        "resolveName"u8, "getSymbolsInScope"u8, "getMembersOfSymbol"u8, "getExportsOfSymbol"u8,
        "getConstantValue"u8, "getSignatureFromDeclaration"u8, "isContextSensitive"u8,
        "typeToString"u8, "typeToTypeNode"u8, "signatureToSignatureDeclaration"u8,
    ];
    private static readonly HashSet<Utf8String> typePropertyMethods =
    [
        "getSymbolOfType"u8, "getTargetOfType"u8, "getFreshTypeOfType"u8, "getRegularTypeOfType"u8,
        "getTypesOfType"u8, "getTypeParametersOfType"u8, "getOuterTypeParametersOfType"u8, "getLocalTypeParametersOfType"u8,
        "getThisTypeOfType"u8, "getAliasTypeArgumentsOfType"u8, "getAliasSymbolOfType"u8, "getObjectTypeOfType"u8,
        "getIndexTypeOfType"u8, "getCheckTypeOfType"u8, "getExtendsTypeOfType"u8, "getBaseTypeOfType"u8, "getConstraintOfType"u8,
    ];
    private static readonly HashSet<Utf8String> signaturePropertyMethods =
    [
        "getTypeParametersOfSignature"u8, "getParametersOfSignature"u8, "getThisParameterOfSignature"u8, "getTargetOfSignature"u8,
    ];

    private async ValueTask<RpcResponse?> CheckerRequestAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation)
    {
        bool typeProperty = typePropertyMethods.Contains(method), signatureProperty = signaturePropertyMethods.Contains(method);
        bool symbolProperty = method == "getParentOfSymbol"u8 || method == "getExportSymbolOfSymbol"u8;
        if (!typeProperty && !signatureProperty && !symbolProperty && !checkerMethods.Contains(method) && !typeOperationMethods.Contains(method)) return null;
        await using var pin = Pin(ApiJson.UInt64(parameters, "snapshot"u8));
        var data = pin.Data;
        var project = ApiJson.String(parameters, "project"u8);
        ValueTask<RpcResponse> TypeResult(Type? type) => data.TypeResponseAsync(type, project, cancellation);
        ValueTask<RpcResponse> SymbolResult(Symbol? symbol) => data.SymbolResponseAsync(symbol, project, cancellation);
        ValueTask<RpcResponse> SignatureResult(Signature? signature) => data.SignatureResponseAsync(signature, project, cancellation);
        Type ResolveType() => data.Type(project, ApiJson.UInt32(parameters, "type"u8));
        Symbol ResolveSymbol() => data.Symbol(ApiJson.UInt64(parameters, "symbol"u8)).Symbol;
        if (typeProperty)
        {
            var type = data.Type(project, ApiJson.UInt32(parameters, "objectId"u8));
            if (method == "getSymbolOfType"u8) return await SymbolResult(type.Symbol);
            if (method == "getAliasSymbolOfType"u8) return await SymbolResult(type.Alias?.Symbol);
            if (method == "getTargetOfType"u8) return await TypeResult(type switch
            { ObjectType obj => obj.Target, TypeParameter parameter => parameter.Target, IndexType index => index.Target,
                StringMappingType mapping => mapping.Target, _ => throw new InvalidOperationException("Unhandled case in Type.Target") });
            if (method == "getFreshTypeOfType"u8) return await TypeResult(((LiteralType)type).FreshType);
            if (method == "getRegularTypeOfType"u8) return await TypeResult(((LiteralType)type).RegularType);
            if (method == "getThisTypeOfType"u8) return await TypeResult(((InterfaceType)type).ThisType);
            if (method == "getObjectTypeOfType"u8) return await TypeResult(((IndexedAccessType)type).ObjectType);
            if (method == "getIndexTypeOfType"u8) return await TypeResult(((IndexedAccessType)type).IndexType);
            if (method == "getCheckTypeOfType"u8) return await TypeResult(((ConditionalType)type).CheckType);
            if (method == "getExtendsTypeOfType"u8) return await TypeResult(((ConditionalType)type).ExtendsType);
            if (method == "getBaseTypeOfType"u8) return await TypeResult(((SubstitutionType)type).BaseType);
            if (method == "getConstraintOfType"u8) return await TypeResult(((SubstitutionType)type).Constraint);
            IEnumerable<Type> types = method == "getTypesOfType"u8
                ? type switch { UnionOrIntersectionType union => union.Types, TemplateLiteralType template => template.Types,
                    _ => throw new InvalidOperationException("Unhandled case in Type.Types") }
                : method == "getAliasTypeArgumentsOfType"u8 ? type.Alias?.TypeArguments ?? []
                : method == "getOuterTypeParametersOfType"u8 ? ((InterfaceType)type).AllTypeParameters.Take(((InterfaceType)type).OuterTypeParameterCount)
                : method == "getLocalTypeParametersOfType"u8 ? ((InterfaceType)type).AllTypeParameters.SkipLast(1).Skip(((InterfaceType)type).OuterTypeParameterCount)
                : ((InterfaceType)type).AllTypeParameters.SkipLast(1);
            return await ResponseArrayAsync(types, TypeResult);
        }
        if (signatureProperty)
        {
            var signature = data.Signature(project, ApiJson.UInt64(parameters, "objectId"u8));
            if (method == "getTargetOfSignature"u8) return await SignatureResult(signature.Target);
            if (method == "getThisParameterOfSignature"u8) return await SymbolResult(signature.ThisParameter);
            if (method == "getParametersOfSignature"u8)
                return await ResponseArrayAsync(signature.Parameters, SymbolResult);
            return await ResponseArrayAsync<Type>(signature.TypeParameters, TypeResult);
        }
        if (symbolProperty)
        {
            var symbol = data.Symbol(ApiJson.UInt64(parameters, "objectId"u8)).Symbol;
            return await SymbolResult(method == "getParentOfSymbol"u8 ? symbol.Parent : symbol.ExportSymbol);
        }

        Symbol[]? symbolTable = null;
        if (method == "getMembersOfSymbol"u8 || method == "getExportsOfSymbol"u8)
        {
            var symbol = data.Symbol(ApiJson.UInt64(parameters, "objectId"u8)).Symbol;
            symbolTable = (method == "getMembersOfSymbol"u8 ? symbol.Members : symbol.Exports).Values.ToArray();
            if (symbolTable.Length <= 1) return await ResponseArrayAsync(symbolTable, SymbolResult);
        }

        var program = data.Program(project);
        using var request = new ProjectRequest(cancellation);
        using var lease = await data.Project(project).Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Api, request);
        var checker = lease.Checker;
        if (method == "getDocumentationComment"u8) return RpcResponse.String(await new SymbolDocumentation(checker).CommentAsync(ResolveSymbol(), cancellation));
        if (method == "getJsDocTags"u8)
        {
            var tags = await SymbolDocumentation.TagsAsync(ResolveSymbol(), cancellation);
            return RpcResponse.Json(writer =>
            {
                writer.WriteStartArray();
                foreach (var tag in tags)
                {
                    writer.WriteStartObject(); ApiJson.String(writer, "name"u8, tag.Name);
                    if (!tag.Text.IsEmpty) ApiJson.String(writer, "text"u8, tag.Text); writer.WriteEndObject();
                }
                writer.WriteEndArray();
            });
        }
        if (typeOperationMethods.Contains(method)) return await TypeOperationAsync(method, parameters, data, project, checker, cancellation);
        if (symbolTable is not null)
        {
            Array.Sort(symbolTable, checker.Algebra.Order.CompareSymbols);
            return await ResponseArrayAsync(symbolTable, SymbolResult);
        }
        ValueTask<SyntaxNode> ResolveNode(JsonElement value) => data.ResolveNodeAsync(program, ApiJson.String(value), cancellation);
        ValueTask<SyntaxNode> Location() => ResolveNode(ApiJson.Get(parameters, "location"u8));
        SourceFileNode Source(JsonElement value) => program.GetFile(Document(value))?.Syntax
            ?? throw new ApiException($"source file not found: {(value.ValueKind == JsonValueKind.Object ? ApiJson.String(value, "uri"u8) : ApiJson.String(value))}");
        if (method == "getReferencesToSymbolInFile"u8)
        {
            var symbol = ResolveSymbol();
            var nodes = await checker.GetReferencesToSymbolInFileAsync(Source(ApiJson.Get(parameters, "file"u8)), symbol, cancellation);
            List<Utf8String> handles = [];
            foreach (var node in nodes) handles.Add(await data.NodeHandleAsync(node, cancellation));
            return RpcResponse.Json(writer => ApiJson.Strings(writer, handles));
        }
        async ValueTask<RpcResponse> SymbolAt(JsonElement value) => await SymbolResult(await checker.GetSymbolAtLocationAsync(await ResolveNode(value), cancellation));
        async ValueTask<RpcResponse> TypeAt(JsonElement value) => await TypeResult(await checker.GetTypeAtLocationAsync(await ResolveNode(value), cancellation));
        async ValueTask<RpcResponse> SourceSymbol(JsonElement value) => await SymbolResult(await checker.GetSymbolAtLocationAsync(Source(value), cancellation));
        ValueTask<SyntaxNode> AtPosition(SourceFileNode source, JsonElement position) => SyntaxNavigation.GetTouchingPropertyNameAsync(
            source, (int)Math.Min(ApiJson.UInt32(position), int.MaxValue), cancellation);
        async ValueTask<SyntaxNode?> OptionalLocation()
        {
            if (!ApiJson.String(parameters, "location"u8).IsEmpty) return await Location();
            var file = ApiJson.Get(parameters, "file"u8); var position = ApiJson.Get(parameters, "position"u8);
            return file.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && position.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? await AtPosition(Source(file), position) : null;
        }
        if (method == "typeToString"u8 || method == "typeToTypeNode"u8 || method == "signatureToSignatureDeclaration"u8)
        {
            var location = ApiJson.String(parameters, "location"u8).IsEmpty ? null : await Location();
            int flags = ApiJson.Int32(parameters, "flags"u8);
            if (method == "typeToString"u8) return RpcResponse.String(await checker.GetTypeDisplayAsync(ResolveType(), location,
                flags == 0 ? TypeFormatFlags.AllowUniqueESSymbolType | TypeFormatFlags.UseAliasDefinedOutsideCurrentScope : (TypeFormatFlags)flags, cancellation));
            var node = method == "typeToTypeNode"u8 ? await checker.TypeToTypeNodeAsync(ResolveType(), location, (NodeBuilderFlags)flags, cancellation)
                : await checker.SignatureToDeclarationAsync(data.Signature(project, ApiJson.UInt64(parameters, "signature"u8)),
                    (SyntaxKind)ApiJson.Int32(parameters, "kind"u8), location, (NodeBuilderFlags)flags, cancellation);
            return await EncodeAsync(node, new(), cancellation);
        }
        if (method == "getSymbolAtPosition"u8 || method == "getSymbolsAtPositions"u8 || method == "getTypeAtPosition"u8 || method == "getTypesAtPositions"u8)
        {
            var source = Source(ApiJson.Get(parameters, "file"u8));
            bool symbols = method == "getSymbolAtPosition"u8 || method == "getSymbolsAtPositions"u8;
            async ValueTask<RpcResponse> QueryPosition(JsonElement position)
            {
                var node = await AtPosition(source, position);
                return symbols ? await SymbolResult(await checker.GetSymbolAtLocationAsync(node, cancellation))
                    : await TypeResult(await checker.GetTypeAtLocationAsync(node, cancellation));
            }
            return method == "getSymbolsAtPositions"u8 || method == "getTypesAtPositions"u8
                ? await ResponseArrayAsync(ApiJson.Array(parameters, "positions"u8), QueryPosition) : await QueryPosition(ApiJson.Get(parameters, "position"u8));
        }
        if (method == "resolveName"u8) return await SymbolResult(checker.Symbols.NameResolver(cancellation).Resolve(
            await OptionalLocation(), ApiJson.String(parameters, "name"u8), (SymbolFlags)ApiJson.UInt32(parameters, "meaning"u8),
            excludeGlobals: ApiJson.Boolean(parameters, "excludeGlobals"u8)));
        if (method == "getSymbolsInScope"u8) return await ResponseArrayAsync(await checker.GetSymbolsInScopeAsync(
            await OptionalLocation() ?? throw new ApiException("getSymbolsInScope requires a location"),
            (SymbolFlags)ApiJson.UInt32(parameters, "meaning"u8), cancellation), SymbolResult);
        if (method == "getSymbolAtLocation"u8) return await SymbolAt(ApiJson.Get(parameters, "location"u8));
        if (method == "getSymbolsAtLocations"u8) return await ResponseArrayAsync(ApiJson.Array(parameters, "locations"u8), SymbolAt);
        if (method == "getTypeAtLocation"u8) return await TypeAt(ApiJson.Get(parameters, "location"u8));
        if (method == "getTypeAtLocations"u8) return await ResponseArrayAsync(ApiJson.Array(parameters, "locations"u8), TypeAt);
        if (method == "getSymbolOfSourceFile"u8) return await SourceSymbol(ApiJson.Get(parameters, "file"u8));
        if (method == "getSymbolsOfSourceFiles"u8) return await ResponseArrayAsync(ApiJson.Array(parameters, "files"u8), SourceSymbol);
        if (method == "getTypeOfSymbol"u8) return await TypeResult(await checker.Values.GetAsync(ResolveSymbol(), cancellation));
        if (method == "getTypesOfSymbols"u8) return await ResponseArrayAsync(ApiJson.Array(parameters, "symbols"u8),
            async value => await TypeResult(await checker.Values.GetAsync(data.Symbol(ApiJson.UInt64(value)).Symbol, cancellation)));
        if (method == "getDeclaredTypeOfSymbol"u8) return await TypeResult(await checker.Declared.GetAsync(ResolveSymbol(), cancellation));
        if (method == "getNonMissingTypeOfSymbol"u8)
        {
            var symbol = ResolveSymbol();
            return await TypeResult(checker.Values.NonMissing(await checker.Values.GetAsync(symbol, cancellation), (symbol.Flags & SymbolFlags.Optional) != 0));
        }
        if (method == "getSignaturesOfType"u8) return await ResponseArrayAsync(
            await checker.SignaturesAsync(ResolveType(), ApiJson.Int32(parameters, "kind"u8) != 0, cancellation), SignatureResult);
        if (method == "getResolvedSignature"u8) return await SignatureResult(await checker.GetResolvedSignatureAsync(await Location(), cancellation));
        if (method == "getReturnTypeOfSignature"u8) return await TypeResult(await checker.GetReturnTypeOfSignatureAsync(
            data.Signature(project, ApiJson.UInt64(parameters, "objectId"u8)), cancellation));
        if (method == "getContextualType"u8) return await TypeResult(await checker.GetContextualTypeAsync(await Location(), cancellation: cancellation));
        if (method == "getContextualTypeForArgument"u8) return await TypeResult(await checker.GetContextualTypeForArgumentAtIndexAsync(
            await Location(), ApiJson.Int32(parameters, "index"u8), cancellation));
        if (method == "getTypeFromTypeNode"u8) return await TypeResult(await checker.GetTypeFromTypeNodeAsync(await Location(), cancellation));
        if (method == "getTypeOfSymbolAtLocation"u8) return await TypeResult(await checker.GetTypeOfSymbolAtLocationAsync(ResolveSymbol(), await Location(), cancellation));
        if (method == "isContextSensitive"u8) return RpcResponse.Boolean(checker.ContextSensitive(await Location()));
        if (method == "getSignatureFromDeclaration"u8) return await SignatureResult(await checker.Signatures.FromDeclarationAsync(await Location(), cancellation));
        if (method == "getConstantValue"u8)
        {
            var value = await checker.GetConstantValueForEmitAsync(await Location(), cancellation);
            return RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); writer.WriteBoolean("isNumber"u8, value is double);
                writer.WritePropertyName("value"u8); ApiSnapshotData.WriteLiteral(writer, value); writer.WriteEndObject();
            });
        }
        if (method == "getExportsOfModule"u8)
        {
            var exports = (await checker.GetExportsOfModuleAsync(ResolveSymbol(), cancellation)).ToArray();
            Array.Sort(exports, checker.Algebra.Order.CompareSymbols);
            return await ResponseArrayAsync(exports, SymbolResult);
        }
        if (method == "getAliasedSymbol"u8) return await SymbolResult(await checker.GetAliasedSymbolAsync(ResolveSymbol(), cancellation));
        if (method == "getShorthandAssignmentValueSymbol"u8) return await SymbolResult(await checker.GetShorthandAssignmentValueSymbolAsync(await Location(), cancellation));
        if (method == "getExportSpecifierLocalTargetSymbol"u8) return await SymbolResult(await checker.GetExportSpecifierLocalTargetSymbolAsync(await Location(), cancellation));
        throw new InvalidOperationException($"No checker handler for {method}");
    }

    private static async ValueTask<RpcResponse> ResponseArrayAsync<T>(IEnumerable<T> values, Func<T, ValueTask<RpcResponse>> convert)
    {
        var responses = new List<RpcResponse>();
        foreach (var value in values) responses.Add(await convert(value).ConfigureAwait(false));
        return RpcResponse.Json(writer =>
        {
            writer.WriteStartArray();
            foreach (var response in responses) writer.WriteRawValue(response.Data.Span, skipInputValidation: true);
            writer.WriteEndArray();
        });
    }
}

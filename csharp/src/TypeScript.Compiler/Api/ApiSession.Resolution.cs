using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Api;

public sealed partial class ApiSession
{
    private static readonly HashSet<Utf8String> resolutionMethods =
    [
        "getConfigFileNames"u8, "getConfigSourceFile"u8, "getSourceFileMetadata"u8,
        "getModeForUsageLocation"u8, "getModeForResolutionAtIndex"u8, "getResolvedModule"u8,
        "getResolvedModuleFromModuleSpecifier"u8, "getResolvedTypeReferenceDirective"u8,
        "getResolvedTypeReferenceDirectiveFromTypeReferenceDirective"u8,
    ];

    private async ValueTask<RpcResponse?> ResolutionRequestAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation)
    {
        if (!resolutionMethods.Contains(method)) return null;
        await using var pin = Pin(ApiJson.UInt64(parameters, "snapshot"u8));
        var data = pin.Data;
        var program = data.Program(ApiJson.String(parameters, "project"u8));
        var config = program.Configuration;
        if (method == "getConfigFileNames"u8) return RpcResponse.Json(writer => ApiJson.Strings(writer,
            config.SourceFile is null ? [] : new[] { config.SourceFile.FileName }.Concat(config.ExtendedConfigFiles.Order(Utf8StringComparer.Ordinal))));
        if (method == "getConfigSourceFile"u8)
        {
            SourceFileNode? source = null;
            var path = host.Path(Document(ApiJson.Get(parameters, "file"u8)));
            if (config.SourceFile is { } root)
            {
                if (host.Path(root.FileName) == path) source = root;
                else if (config.ExtendedConfigFiles.FirstOrDefault(name => host.Path(name) == path) is { IsEmpty: false } name
                    && data.Snapshot.FileSystem.GetDocument(name) is { } document)
                    source = await Parser.ParseSourceFileAsync(new(name, ScriptKind.JSON), document.Source, cancellation);
            }
            return await EncodeAsync(source, new() { Path = path, ParseOptions = new(source?.FileName ?? default, ScriptKind.JSON) }, cancellation);
        }
        if (method == "getSourceFileMetadata"u8)
        {
            var file = program.GetFile(Document(ApiJson.Get(parameters, "file"u8)));
            return file is null ? RpcResponse.Null : RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); writer.WriteBoolean("isDefaultLibrary"u8, file.Library);
                writer.WriteBoolean("isFromExternalLibrary"u8, program.IsFromExternalLibrary(file.Syntax));
                ApiJson.String(writer, "packageJsonType"u8, file.PackageType);
                ApiJson.String(writer, "packageJsonDirectory"u8, file.PackageDirectory);
                writer.WriteNumber("impliedNodeFormat"u8, (int)file.ImpliedFormat); writer.WriteEndObject();
            });
        }
        SourceFileNode RequiredSource(ReadOnlySpan<byte> property)
        {
            var value = ApiJson.Get(parameters, property);
            return program.GetFile(Document(value))?.Syntax ?? throw new ApiException(
                $"source file not found: {(value.ValueKind == JsonValueKind.Object ? ApiJson.String(value, "uri"u8) : ApiJson.String(value))}");
        }
        SourceFileNode sourceFile;
        Utf8String specifier;
        ReferenceResolutionMode mode;
        bool types = method == "getResolvedTypeReferenceDirective"u8 || method == "getResolvedTypeReferenceDirectiveFromTypeReferenceDirective"u8;
        if (method == "getResolvedModuleFromModuleSpecifier"u8)
        {
            var node = await data.ResolveNodeAsync(program, ApiJson.String(parameters, "moduleSpecifier"u8), cancellation);
            if (node is not (StringLiteralNode or NoSubstitutionTemplateLiteralNode)) throw new ApiException("moduleSpecifier must be a StringLiteralLike node");
            var ancestor = node;
            while (ancestor.Parent is not null) ancestor = ancestor.Parent;
            sourceFile = ApiJson.Get(parameters, "sourceFile"u8).ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? RequiredSource("sourceFile"u8) : ancestor as SourceFileNode
                    ?? throw new ApiException("moduleSpecifier must have a SourceFile ancestor or sourceFile must be provided");
            specifier = LiteralText(node);
            mode = program.ResolutionModeForUsage(sourceFile, node);
        }
        else
        {
            sourceFile = RequiredSource(method == "getResolvedTypeReferenceDirectiveFromTypeReferenceDirective"u8 ? "sourceFile"u8 : "file"u8);
            if (method == "getModeForUsageLocation"u8)
            {
                var usage = await data.ResolveNodeAsync(program, ApiJson.String(parameters, "usage"u8), cancellation);
                if (usage is not (StringLiteralNode or NoSubstitutionTemplateLiteralNode)) throw new ApiException("usage must be a StringLiteralLike node");
                return RpcResponse.Json(writer => writer.WriteNumberValue((int)program.ResolutionModeForUsage(sourceFile, usage)));
            }
            if (method == "getModeForResolutionAtIndex"u8)
            {
                int index = ApiJson.Int32(parameters, "index"u8);
                var usage = index < 0 ? null : sourceFile.Imports.Concat(sourceFile.ModuleAugmentations.Where(node => node is StringLiteralNode)).ElementAtOrDefault(index);
                if (usage is null) throw new ApiException("invalid resolution index");
                return RpcResponse.Json(writer => writer.WriteNumberValue((int)program.ResolutionModeForUsage(sourceFile, usage)));
            }
            specifier = ApiJson.String(parameters, types ? "typeDirectiveName"u8 : "moduleName"u8);
            if (method == "getResolvedTypeReferenceDirectiveFromTypeReferenceDirective"u8)
            {
                mode = (ReferenceResolutionMode)ApiJson.Int32(parameters, "resolutionMode"u8);
                if (mode == ReferenceResolutionMode.Unspecified) mode = program.ResolutionModeForUsage(sourceFile, null);
            }
            else mode = (ReferenceResolutionMode)ApiJson.Int32(parameters, "mode"u8);
        }
        var resolved = program.GetFile(sourceFile.FileName)!.Resolutions.FirstOrDefault(reference =>
            reference.TypeReference == types && reference.Specifier == specifier && reference.Mode == mode)?.Resolution;
        return resolved is not { IsResolved: true } ? RpcResponse.Null : RpcResponse.Json(writer => WriteResolution(writer, resolved, types));
    }

    private static Utf8String LiteralText(SyntaxNode node) => node switch
    { StringLiteralNode literal => literal.Text, NoSubstitutionTemplateLiteralNode literal => literal.Text, _ => default };

    private static void WriteResolution(Utf8JsonWriter writer, ResolvedModule resolution, bool typeReference)
    {
        writer.WriteStartObject(); ApiJson.String(writer, "resolvedFileName"u8, resolution.FileName);
        if (!resolution.OriginalPath.IsEmpty) ApiJson.String(writer, "originalPath"u8, resolution.OriginalPath);
        if (typeReference) writer.WriteBoolean("primary"u8, resolution.Primary);
        else
        {
            ApiJson.String(writer, "extension"u8, resolution.Extension);
            writer.WriteBoolean("resolvedUsingTsExtension"u8, resolution.UsingTsExtension);
            writer.WriteBoolean("resolvedUsingExtraExtensions"u8, resolution.UsingExtraExtension);
            if (!resolution.AlternateResult.IsEmpty) ApiJson.String(writer, "alternateResult"u8, resolution.AlternateResult);
        }
        writer.WriteBoolean("isExternalLibraryImport"u8, resolution.External);
        if (resolution.PackageId is { Name.IsEmpty: false } package)
        {
            writer.WritePropertyName("packageId"u8); writer.WriteStartObject(); ApiJson.String(writer, "name"u8, package.Name);
            ApiJson.String(writer, "subModuleName"u8, package.SubModuleName); ApiJson.String(writer, "version"u8, package.Version);
            ApiJson.String(writer, "peerDependencies"u8, package.PeerDependencies); writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
}

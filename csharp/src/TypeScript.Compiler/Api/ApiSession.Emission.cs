using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compiler.Api;

public sealed partial class ApiSession
{
    private Utf8String? ReadText(Utf8String fileName) => fileSystem is ICompilerSourceProvider provider
        ? provider.ReadSource(fileName)?.Text.Text
        : fileSystem.ReadFile(fileName) is { } bytes ? new Utf8String(SourceEncoding.DecodeBytes(bytes)) : null;

    private async ValueTask<RpcResponse?> EmissionRequestAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation)
    {
        if (method == "transpileModule"u8 || method == "transpileDeclaration"u8
            || method == "transpileModuleFromFile"u8 || method == "transpileDeclarationFromFile"u8)
        {
            var settings = ApiJson.Get(parameters, "options"u8);
            bool fromFile = method == "transpileModuleFromFile"u8 || method == "transpileDeclarationFromFile"u8;
            var fileName = fromFile ? host.FileName(ApiJson.String(parameters, "fileName"u8)) : ApiJson.String(settings, "fileName"u8);
            var input = fromFile ? ReadText(fileName) ?? throw new ApiException($"could not read file \"{fileName}\"") : ApiJson.String(parameters, "input"u8);
            var compilerOptions = ApiJson.CompilerOptions(ApiJson.Get(settings, "compilerOptions"u8));
            var options = new TranspileOptions { CompilerOptions = compilerOptions, FileName = fileName,
                ReportDiagnostics = ApiJson.Boolean(settings, "reportDiagnostics"u8) };
            var output = method == "transpileDeclaration"u8 || method == "transpileDeclarationFromFile"u8
                ? await Transpiler.TranspileDeclarationAsync(input, options, cancellation)
                : await Transpiler.TranspileModuleAsync(input, options, cancellation);
            var source = new SourceText(input);
            var sourceName = CompilerPath.Resolve("/"u8, fileName.IsEmpty
                ? compilerOptions.Jsx != JsxEmit.None ? "module.tsx"u8 : "module.ts"u8 : fileName);
            return RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); ApiJson.String(writer, "outputText"u8, output.OutputText);
                if (output.Diagnostics.Count != 0)
                {
                    writer.WritePropertyName("diagnostics"u8); writer.WriteStartArray();
                    foreach (var diagnostic in output.Diagnostics) WriteDiagnostic(writer, diagnostic, name => name == sourceName ? source : null);
                    writer.WriteEndArray();
                }
                if (!output.SourceMapText.IsEmpty) ApiJson.String(writer, "sourceMapText"u8, output.SourceMapText);
                writer.WriteEndObject();
            });
        }
        bool selected = method == "getJavaScriptEmit"u8 || method == "getDeclarationEmit"u8;
        if (!selected && method != "emit"u8 && method != "emitToString"u8) return null;
        await using var pin = Pin(ApiJson.UInt64(parameters, "snapshot"u8));
        var data = pin.Data;
        var program = data.Program(ApiJson.String(parameters, "project"u8));
        IReadOnlyList<SourceFileNode>? sources = null;
        var only = selected ? method == "getJavaScriptEmit"u8 ? EmitOnly.JavaScript : EmitOnly.Declarations
            : (EmitOnly)ApiJson.UInt32(parameters, "emitOnly"u8);
        if (only > EmitOnly.Declarations) throw new ApiException($"invalid emitOnly value: {(uint)only}");
        if (selected)
        {
            if (ApiJson.Get(parameters, "files"u8).ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                throw new ApiException("files is required");
            sources = ApiJson.Array(parameters, "files"u8).Select(file => program.GetFile(Document(file, program))?.Syntax
                ?? throw new ApiException($"source file not found: {(file.ValueKind == JsonValueKind.Object ? ApiJson.String(file, "uri"u8) : ApiJson.String(file))}")).ToArray();
        }
        bool full = false;
        for (var fs = data.FileSystem; fs is RequestFileSystem request; fs = request.BaseFileSystem)
            if (request.Kind == RequestFileSystemKind.Full) { full = true; break; }
        bool capture = full || method != "emit"u8;
        var files = new List<(Utf8String Name, Utf8String Text, Utf8String Source)>();
        using var checkerRequest = new ProjectRequest(cancellation);
        using var checkerLease = await data.Project(ApiJson.String(parameters, "project"u8)).Resource!.Checkers
            .AcquireAsync(ProjectCheckerLifetime.Diagnostics, checkerRequest);
        var result = await program.EmitWithCheckerAsync(new()
        {
            Only = only, SourceFiles = sources, Force = selected,
            WriteFile = (name, text, information, _) =>
            {
                if (capture) files.Add((name, text, information.SourceFile.FileName));
                else fileSystem.WriteFile(name, text.Span);
                return ValueTask.CompletedTask;
            }
        }, checkerLease.Checker, cancellation);
        return RpcResponse.Json(writer =>
        {
            writer.WriteStartObject(); writer.WriteBoolean("emitSkipped"u8, result.EmitSkipped);
            writer.WritePropertyName("diagnostics"u8); writer.WriteStartArray();
            foreach (var diagnostic in result.Diagnostics) WriteDiagnostic(writer, diagnostic, name => program.GetFile(name)?.Syntax.Source
                ?? program.ProjectReferences.Projects.Values.FirstOrDefault(config => config.SourceFile?.FileName == name)?.SourceFile?.Source, program);
            writer.WriteEndArray();
            if (method == "emit"u8)
            {
                ApiJson.Strings(writer, "emittedFiles"u8, result.EmittedFiles);
                ApiJson.Strings(writer, "emittedFilesContents"u8, full
                    ? result.EmittedFiles.Select(name => files.Last(file => file.Name == name).Text) : []);
            }
            else
            {
                writer.WritePropertyName("outputFiles"u8); writer.WriteStartArray();
                foreach (var file in files.OrderBy(file => file.Name, Utf8StringComparer.Ordinal))
                {
                    writer.WriteStartObject(); ApiJson.String(writer, "fileName"u8, file.Name); ApiJson.String(writer, "text"u8, file.Text);
                    ApiJson.String(writer, "sourceFileName"u8, file.Source); writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        });
    }
}

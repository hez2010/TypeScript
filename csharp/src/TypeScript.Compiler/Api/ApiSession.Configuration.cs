using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compiler.Api;

public sealed partial class ApiSession
{
    private RpcResponse? ConfigurationRequest(Utf8String method, JsonElement parameters, CancellationToken cancellation)
    {
        if (method == "parseCommandLine"u8)
        {
            var parsed = new CommandLineParser(fileSystem, CurrentDirectory).Parse(ApiJson.Strings(parameters, "commandLine"u8), resolvePaths: false);
            var options = new CompilerOptions();
            foreach (var (name, value) in parsed.Options.Values)
            {
                var definition = OptionDefinitions.Find(name);
                options.Set(name, value.ValueKind == JsonValueKind.String && definition?.IsFilePath == true
                    ? OptionValues.String(CompilerPath.Resolve(CurrentDirectory, ApiJson.String(value)))
                    : value.ValueKind == JsonValueKind.Array && definition?.ElementIsFilePath == true
                        ? OptionValues.Array(value.EnumerateArray().Select(element => element.ValueKind == JsonValueKind.String
                            ? OptionValues.String(CompilerPath.Resolve(CurrentDirectory, ApiJson.String(element))) : element)) : value);
            }
            JsonElement raw = default;
            if (parsed.Options.Values.Count != 0)
            {
                var response = RpcResponse.Json(writer => WriteOptions(writer, parsed.Options, includeNull: true));
                using var document = JsonDocument.Parse(response.Data); raw = document.RootElement.Clone();
            }
            return RpcResponse.Json(writer => WriteConfiguration(writer,
                new(default, options, parsed.FileNames, [], parsed.Diagnostics, []) { Raw = raw }, commandLine: true));
        }
        if (method == "parseJsonConfigFileContent"u8)
        {
            var directoryValue = ApiJson.Get(parameters, "configDirectory"u8);
            var fileValue = ApiJson.Get(parameters, "configFileName"u8);
            bool hasDirectory = directoryValue.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
            bool hasFile = fileValue.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
            if (hasDirectory == hasFile) throw new ApiException("exactly one of configDirectory or configFileName is required");
            var name = hasFile ? Document(fileValue) : default;
            var directory = hasDirectory ? host.FileName(ApiJson.String(directoryValue)) : CompilerPath.DirectoryName(name);
            var parsed = new ConfigParser(fileSystem, CurrentDirectory).ParseJson(ApiJson.Get(parameters, "json"u8), directory, name, cancellation);
            return RpcResponse.Json(writer => WriteConfiguration(writer, parsed, jsonContent: true));
        }
        if (method != "readConfigFile"u8 && method != "parseConfigFile"u8) return null;
        var fileName = Document(ApiJson.Get(parameters, "file"u8));
        var text = ReadText(fileName);
        if (method == "parseConfigFile"u8)
        {
            if (text is null) throw new ApiException($"could not read file \"{fileName}\"");
            var config = new ConfigParser(fileSystem, CurrentDirectory).ParseForApi(fileName, cancellation);
            return RpcResponse.Json(writer => WriteConfiguration(writer, config));
        }
        var errors = new List<Diagnostic>();
        ConfigSyntax? syntax = null;
        if (text is { } source) syntax = new(fileName, new SourceText(source), errors, cancellation);
        else errors.Add(new(Messages.Cannot_read_file_0, -1, 0, [fileName]));
        return RpcResponse.Json(writer =>
        {
            writer.WriteStartObject(); writer.WritePropertyName("config"u8);
            if (syntax is null) { writer.WriteStartObject(); writer.WriteEndObject(); }
            else ApiJson.Write(writer, syntax.Root);
            if (errors.Count != 0)
            {
                writer.WritePropertyName("error"u8); WriteDiagnostic(writer, errors[0],
                    name => syntax?.Source.FileName == name ? syntax.Source.Source : null);
            }
            writer.WriteEndObject();
        });
    }
}

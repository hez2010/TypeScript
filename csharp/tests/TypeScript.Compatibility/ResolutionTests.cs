using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class ResolutionTests
{
    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = int.MaxValue });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                Process(document.RootElement, writer);
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    private static void Process(JsonElement input, Utf8JsonWriter writer)
    {
        string Text(string key, string fallback = "") => input.TryGetProperty(key, out var item) ? JsonStrings.GetString(item) : fallback;
        string operation = Text("operation"), path = Text("path"), other = Text("other");
        if (operation is "version" or "range")
        {
            writer.WriteStartArray();
            if (operation == "version")
            {
                var version = SemanticVersion.Parse(path);
                writer.WriteBooleanValue(version is not null);
                if (version is not null)
                {
                    writer.WriteStringValue(version.ToString());
                    if (SemanticVersion.Parse(other) is { } second)
                        writer.WriteNumberValue(version.CompareTo(second));
                    else
                        writer.WriteNullValue();
                }
            }
            else
            {
                var range = VersionRange.Parse(path);
                writer.WriteBooleanValue(range is not null);
                if (range is not null)
                {
                    writer.WriteStringValue(range.ToString());
                    if (SemanticVersion.Parse(other) is { } version)
                        writer.WriteBooleanValue(range.Test(version));
                    else
                        writer.WriteNullValue();
                }
            }
            writer.WriteEndArray();
            return;
        }
        var files = new Dictionary<string, byte[]>();
        if (input.TryGetProperty("files", out var fileMap))
            foreach (var file in fileMap.EnumerateObject())
                files[JsonStrings.GetName(file)] = Wtf8.Encode(JsonStrings.GetString(file.Value));
        var links = new Dictionary<string, string>();
        if (input.TryGetProperty("symlinks", out var symlinks))
            foreach (var link in symlinks.EnumerateObject())
                links[link.Name] = JsonStrings.GetString(link.Value);
        string directory = Text("directory", "/project"), containing = Text("containingFile", directory + "/main.ts");
        string config = directory + "/tsconfig.json";
        using (var configStream = new MemoryStream())
        {
            using (var configWriter = new Utf8JsonWriter(configStream))
            {
                configWriter.WriteStartObject();
                configWriter.WritePropertyName("compilerOptions");
                if (input.TryGetProperty("options", out var opts))
                    opts.WriteTo(configWriter);
                else
                {
                    configWriter.WriteStartObject();
                    configWriter.WriteEndObject();
                }
                configWriter.WriteStartArray("files");
                configWriter.WriteStringValue(containing);
                configWriter.WriteEndArray();
                configWriter.WriteEndObject();
            }
            files[config] = configStream.ToArray();
        }
        var fs = new MemoryFileSystem(
            files,
            !input.TryGetProperty("sensitive", out var sensitive) || sensitive.GetBoolean(),
            directory,
            links);
        var options = new ConfigParser(fs, directory).Parse(config).Options;
        var resolver = new ModuleResolver(fs, options, directory, config,
            extraExtensions: input.TryGetProperty("extraExtensions", out var extra)
                ? extra.EnumerateArray().Select(JsonStrings.GetString)
                : null);
        if (operation == "automatic")
        {
            writer.WriteStartArray();
            foreach (string type in resolver.AutomaticTypeDirectives())
                writer.WriteStringValue(type);
            writer.WriteEndArray();
            return;
        }
        var result = operation == "config" ? ModuleResolver.ResolveConfig(
            fs,
            directory,
            path,
            containing) : resolver.Resolve(
            path,
            containing,
            input.TryGetProperty("mode", out var mode) ? (ReferenceResolutionMode)mode.GetInt32() : default, operation == "types");
        if (operation == "resolveTrace")
            writer.WriteStartArray();
        writer.WriteStartArray();
        writer.WriteStringValue(result.FileName);
        if (operation != "types")
            writer.WriteStringValue(result.Extension);
        writer.WriteStringValue(result.OriginalPath);
        writer.WriteBooleanValue(result.External);
        if (operation == "types")
            writer.WriteBooleanValue(result.Primary);
        else
        {
            writer.WriteBooleanValue(result.UsingTsExtension);
            writer.WriteBooleanValue(result.UsingExtraExtension);
        }
        writer.WriteStartObject();
        writer.WriteString("Name", result.PackageId?.Name ?? "");
        writer.WriteString("SubModuleName", result.PackageId?.SubModuleName ?? "");
        writer.WriteString("Version", result.PackageId?.Version ?? "");
        writer.WriteString("PeerDependencies", result.PackageId?.PeerDependencies ?? "");
        writer.WriteEndObject();
        if (operation != "types")
            writer.WriteStringValue(result.AlternateResult);
        writer.WriteStartArray();
        foreach (var diagnostic in result.Diagnostics)
            writer.WriteNumberValue((int)diagnostic.Code);
        writer.WriteEndArray();
        writer.WriteEndArray();
        if (operation == "resolveTrace")
        {
            writer.WriteStartArray();
            foreach (var trace in result.Trace)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(trace.Operation);
                writer.WriteStringValue(trace.Path);
                writer.WriteStringValue(trace.Detail);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
    }
}

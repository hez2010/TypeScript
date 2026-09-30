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
        Utf8String Text(Utf8String key, Utf8String fallback = default) => input.TryGetProperty(key, out var item) ? JsonStrings.GetString(item) : fallback;
        Utf8String operation = Text("operation"u8), path = Text("path"u8), other = Text("other"u8);
        if (operation == "version"u8 || operation == "range"u8)
        {
            writer.WriteStartArray();
            if (operation == "version"u8)
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
        var files = new Dictionary<Utf8String, byte[]>();
        if (input.TryGetProperty("files"u8, out var fileMap))
            foreach (var file in fileMap.EnumerateObject())
                files[JsonStrings.GetName(file)] = JsonStrings.GetString(file.Value).Span.ToArray();
        var links = new Dictionary<Utf8String, Utf8String>();
        if (input.TryGetProperty("symlinks"u8, out var symlinks))
            foreach (var link in symlinks.EnumerateObject())
                links[JsonStrings.GetName(link)] = JsonStrings.GetString(link.Value);
        Utf8String directory = Text("directory"u8, "/project"u8), containing = Text("containingFile"u8, directory + "/main.ts"u8);
        Utf8String config = Utf8String.Concat(directory, "/tsconfig.json"u8);
        using (var configStream = new MemoryStream())
        {
            using (var configWriter = new Utf8JsonWriter(configStream))
            {
                configWriter.WriteStartObject();
                configWriter.WritePropertyName("compilerOptions"u8);
                if (input.TryGetProperty("options"u8, out var opts))
                    opts.WriteTo(configWriter);
                else
                {
                    configWriter.WriteStartObject();
                    configWriter.WriteEndObject();
                }
                configWriter.WriteStartArray("files"u8);
                configWriter.WriteStringValue(containing);
                configWriter.WriteEndArray();
                configWriter.WriteEndObject();
            }
            files[config] = configStream.ToArray();
        }
        var fs = new MemoryFileSystem(
            files,
            !input.TryGetProperty("sensitive"u8, out var sensitive) || sensitive.GetBoolean(),
            directory,
            links);
        var options = new ConfigParser(fs, directory).Parse(config).Options;
        var resolver = new ModuleResolver(fs, options, directory, config,
            extraExtensions: input.TryGetProperty("extraExtensions"u8, out var extra)
                ? extra.EnumerateArray().Select(JsonStrings.GetString)
                : null);
        if (operation == "automatic"u8)
        {
            writer.WriteStartArray();
            foreach (Utf8String type in resolver.AutomaticTypeDirectives())
                writer.WriteStringValue(type);
            writer.WriteEndArray();
            return;
        }
        var result = operation == "config"u8 ? ModuleResolver.ResolveConfig(
            fs,
            directory,
            path,
            containing) : resolver.Resolve(
            path,
            containing,
            input.TryGetProperty("mode"u8, out var mode) ? (ReferenceResolutionMode)mode.GetInt32() : default, operation == "types"u8);
        if (operation == "resolveTrace"u8)
            writer.WriteStartArray();
        writer.WriteStartArray();
        writer.WriteStringValue(result.FileName);
        if (operation != "types"u8)
            writer.WriteStringValue(result.Extension);
        writer.WriteStringValue(result.OriginalPath);
        writer.WriteBooleanValue(result.External);
        if (operation == "types"u8)
            writer.WriteBooleanValue(result.Primary);
        else
        {
            writer.WriteBooleanValue(result.UsingTsExtension);
            writer.WriteBooleanValue(result.UsingExtraExtension);
        }
        writer.WriteStartObject();
        writer.WriteString("Name"u8, result.PackageId?.Name ?? ""u8);
        writer.WriteString("SubModuleName"u8, result.PackageId?.SubModuleName ?? ""u8);
        writer.WriteString("Version"u8, result.PackageId?.Version ?? ""u8);
        writer.WriteString("PeerDependencies"u8, result.PackageId?.PeerDependencies ?? ""u8);
        writer.WriteEndObject();
        if (operation != "types"u8)
            writer.WriteStringValue(result.AlternateResult);
        writer.WriteStartArray();
        foreach (var diagnostic in result.Diagnostics)
            writer.WriteNumberValue((int)diagnostic.Code);
        writer.WriteEndArray();
        writer.WriteEndArray();
        if (operation == "resolveTrace"u8)
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

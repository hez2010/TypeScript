using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class MapperCodecTests
{
    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var input = document.RootElement;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                if (JsonStrings.GetString(input.GetProperty("operation"u8)) == "identity"u8)
                {
                    var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
                    {
                        ["/project/tsconfig.json"u8] = Utf8String.Concat(
                            "{\"files\":[\"a.ts\"],\"compilerOptions\":"u8, JsonStrings.Raw(input.GetProperty("options"u8)), "}"u8).Span.ToArray()
                    });
                    var options = new ConfigParser(fs, "/project"u8).Parse("/project/tsconfig.json"u8).Options;
                    var mapper = new ContentMapper("p"u8, [".view"u8], input.TryGetProperty("mapperOptions"u8, out var config) ? config : null,
                        "/project"u8,
                        JsonStrings.GetString(input.GetProperty("name"u8))!,
                        JsonStrings.GetString(input.GetProperty("version"u8))!,
                        [],
                        input.GetProperty("declared"u8),
                        input.GetProperty("dynamic"u8).GetBoolean());
                    var entry = new ContentMapperHost.ProjectEntry(mapper, options, "/project/tsconfig.json"u8, "handle"u8, new(mapper))
                    { ConfigIdentity = TypeScript.Compiler.Configuration.JsonStrings.GetString(input.GetProperty("configIdentity"u8))! };
                    writer.WriteStartArray();
                    writer.WriteStringValue(ContentMapperHost.TransformIdentity(entry));
                    byte[] declared = ContentMapperHost.DeclaredOptions(mapper, options);
                    writer.WriteRawValue(declared);
                    writer.WriteStringValue(Encoding.UTF8.GetString(declared));
                    writer.WriteEndArray();
                }
                else
                {
                    MapperResult? result = null;
                    try
                    {
                        result = MapperOutputDecoder.Decode(
                            input.GetProperty("result"u8),
                            new SourceText(JsonStrings.GetString(input.GetProperty("original"u8))!),
                            JsonStrings.GetString(input.GetProperty("source"u8))!);
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or OverflowException
                        or KeyNotFoundException or FormatException)
                    { }
                    writer.WriteStartArray();
                    writer.WriteBooleanValue(result is not null);
                    if (result is not null)
                    {
                        void Output(MapperOutput mapped)
                        {
                            writer.WriteStartArray();
                            writer.WriteStringValue(mapped.Text.Text.Span);
                            writer.WriteStringValue(mapped.Extension);
                            mapped.Mappings.Write(writer);
                            writer.WriteStartArray();
                            foreach (var d in mapped.Directives)
                            {
                                writer.WriteStartArray();
                                writer.WriteNumberValue(d.OriginalStart);
                                writer.WriteNumberValue(d.OriginalEnd);
                                writer.WriteNumberValue(d.VirtualStart);
                                writer.WriteNumberValue(d.VirtualEnd);
                                writer.WriteNumberValue(d.Expect ? 1 : 0);
                                writer.WriteStringValue(d.Source);
                                writer.WriteNumberValue((int)d.UnusedCode);
                                writer.WriteStringValue(d.UnusedMessage);
                                writer.WriteEndArray();
                            }
                            writer.WriteEndArray();
                            writer.WriteEndArray();
                        }
                        Output(result.Canonical);
                        writer.WriteStartArray();
                        foreach (var output in result.Supplemental)
                            Output(output);
                        writer.WriteEndArray();
                        writer.WriteStartArray();
                        foreach (var diagnostic in result.Diagnostics)
                        {
                            writer.WriteStartArray();
                            writer.WriteNumberValue(diagnostic.Start);
                            writer.WriteNumberValue(diagnostic.Length);
                            writer.WriteNumberValue((int)diagnostic.Code);
                            if (diagnostic.Source is { } source)
                                writer.WriteStringValue(source.Span);
                            else
                                writer.WriteNullValue();
                            writer.WriteStringValue(diagnostic.Format());
                            writer.WriteEndArray();
                        }
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                }
            }
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }
}

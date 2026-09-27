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
                if (input.GetProperty("operation").GetString() == "identity")
                {
                    var fs = new MemoryFileSystem(new Dictionary<string, byte[]>
                    {
                        ["/project/tsconfig.json"] = Encoding.UTF8.GetBytes(
                        "{\"files\":[\"a.ts\"],\"compilerOptions\":" + input.GetProperty("options").GetRawText() + "}")
                    });
                    var options = new ConfigParser(fs, "/project").Parse("/project/tsconfig.json").Options;
                    var mapper = new ContentMapper("p", [".view"], input.TryGetProperty("mapperOptions", out var config) ? config : null,
                        "/project",
                        input.GetProperty("name").GetString()!,
                        input.GetProperty("version").GetString()!,
                        [],
                        input.GetProperty("declared"),
                        input.GetProperty("dynamic").GetBoolean());
                    var entry = new ContentMapperHost.ProjectEntry(mapper, options, "/project/tsconfig.json", "handle", new(mapper))
                    { ConfigIdentity = input.GetProperty("configIdentity").GetString()! };
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
                            input.GetProperty("result"),
                            new SourceText(input.GetProperty("original").GetString()!),
                            input.GetProperty("encoding").GetString()!,
                            input.GetProperty("source").GetString()!);
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
                            writer.WriteStringValue(diagnostic.Source);
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

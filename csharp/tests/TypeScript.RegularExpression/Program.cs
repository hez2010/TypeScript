using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

if (args is ["--native-check"])
{
    if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        throw new InvalidOperationException("Expected NativeAOT");
    Console.WriteLine(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
    return;
}

while (Console.ReadLine() is { } line)
{
    using JsonDocument document = JsonDocument.Parse(line);
    JsonElement request = document.RootElement;
    var scanner = new Scanner(new SourceText(request.GetProperty("text").GetBytesFromBase64()));
    scanner.TargetYear = request.TryGetProperty("target", out var target) ? target.GetInt32() : int.MaxValue;
    scanner.Scan();
    scanner.RescanSlashToken(true);
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer))
    {
        writer.WriteStartArray();
        writer.WriteNumberValue((int)scanner.Kind);
        writer.WriteNumberValue(scanner.Position);
        writer.WriteNumberValue((int)scanner.Flags);
        writer.WriteBase64StringValue(Wtf8.Encode(scanner.Value));
        writer.WriteStartArray();
        foreach (var diagnostic in scanner.Diagnostics)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue((int)diagnostic.Code);
            writer.WriteNumberValue(diagnostic.Start);
            writer.WriteNumberValue(diagnostic.Length);
            writer.WriteStartArray();
            foreach (TextSlice argument in diagnostic.Arguments)
                writer.WriteStringValue(argument.Span);
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteEndArray();
    }
    Console.WriteLine(Encoding.UTF8.GetString(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
}

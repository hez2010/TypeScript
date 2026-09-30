using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class MappingTests
{
    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var input = document.RootElement;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                Process(input, writer);
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    private static void Process(JsonElement input, Utf8JsonWriter writer)
    {
        SpanMap map;
        try
        {
            map = SpanMap.Read(input.GetProperty("mappings"));
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or FormatException or OverflowException)
        {
            writer.WriteStartArray();
            writer.WriteStringValue("decode");
            writer.WriteEndArray();
            return;
        }
        try
        {
            map.Validate((JsonStrings.GetString(input.GetProperty("virtual"))!).Span.ToArray(), (JsonStrings.GetString(input.GetProperty("original"))!).Span.ToArray());
        }
        catch (MappingException e)
        {
            writer.WriteStartArray();
            writer.WriteStringValue("validation");
            writer.WriteNumberValue((int)e.Kind);
            writer.WriteNumberValue(e.VirtualPosition);
            writer.WriteNumberValue(e.OriginalPosition);
            writer.WriteEndArray();
            return;
        }
        int start = input.GetProperty("start").GetInt32(), end = input.GetProperty("end").GetInt32();
        var feature = (MappingFeature)input.GetProperty("feature").GetInt32();
        void Position(MappedPosition position)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(position.Position);
            writer.WriteNumberValue((int)position.Fidelity);
            writer.WriteEndArray();
        }
        void Span(MappedSpan span)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(span.Start);
            writer.WriteNumberValue(span.End);
            writer.WriteNumberValue((int)span.Fidelity);
            writer.WriteEndArray();
        }
        writer.WriteStartArray();
        map.Write(writer);
        Position(map.VirtualToOriginalPosition(start));
        bool exact = map.TryMapExactPosition(start, out int mapped);
        writer.WriteStartArray();
        writer.WriteNumberValue(mapped);
        writer.WriteBooleanValue(exact);
        writer.WriteEndArray();
        Position(map.VirtualToOriginalPosition(start, feature));
        Span(map.VirtualToOriginalSpan(start, end));
        Span(map.VirtualToOriginalSpan(start, end, feature));
        writer.WriteStartArray();
        foreach (var position in map.OriginalToVirtualPositions(start, feature))
            Position(position);
        writer.WriteEndArray();
        writer.WriteStartArray();
        foreach (var span in map.OriginalToVirtualSpans(start, end, feature))
            Span(span);
        writer.WriteEndArray();
        writer.WriteStartArray();
        foreach (var span in map.OriginalToVirtualIntersectingSpans(start, end, feature))
            Span(span);
        writer.WriteEndArray();
        writer.WriteBooleanValue(map.AliasForVirtualSpan(start, end) is not null);
        writer.WriteEndArray();
    }
}

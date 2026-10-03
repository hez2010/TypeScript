using System.Text;
using System.Text.Json;
using TypeScript.Compiler;
using TypeScript.Compiler.Semantics;

namespace TypeScript.Compatibility;

internal static class SpecifierRegexTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var pattern = GoRegex.Parse("^(a+)+b$"u8)!;
        var missing = Utf8String.FromString(new string('a', 50_000));
        Check(!pattern.IsMatch(missing, deadline.Token), "Nested repetitions complete without exponential backtracking");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => pattern.IsMatch(i % 2 == 0 ? "aab"u8 : "aac"u8, deadline.Token))));
        Check(concurrent.SequenceEqual(Enumerable.Range(0, 8).Select(i => i % 2 == 0)), "Concurrent regular-expression queries preserve their own matching state");
        foreach (Action operation in new Action[]
        {
            () => GoRegex.Parse("a"u8, canceled.Token),
            () => GoRegex.FromSpecifierPattern("/a/i"u8, canceled.Token),
            () => pattern.IsMatch(missing, canceled.Token)
        })
        {
            try { operation(); Check(false, "Canceled regular-expression work must throw"); }
            catch (OperationCanceledException) { checks++; }
        }
        var large = Utf8String.FromString(new string('a', 1_000_000));
        using var interrupted = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
        try { GoRegex.Parse(large, interrupted.Token); Check(false, "Parsing observes cancellation during a large expression"); }
        catch (OperationCanceledException) { checks++; }
        return checks;
    }

    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            Utf8String Decode(JsonElement value) => Utf8String.Copy(Convert.FromBase64String(value.GetString()!));
            var pattern = Decode(input.RootElement.GetProperty("pattern"));
            var texts = input.RootElement.GetProperty("texts").EnumerateArray().Select(Decode).ToArray();
            var parsed = GoRegex.Parse(pattern);
            var exclusion = GoRegex.FromSpecifierPattern(pattern);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject(); writer.WriteBoolean("valid", parsed is not null);
                foreach (var (name, regex) in new[] { ("matches", parsed), ("excludes", exclusion) })
                {
                    writer.WritePropertyName(name); writer.WriteStartArray();
                    foreach (var text in texts) writer.WriteBooleanValue(regex?.IsMatch(text) == true);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
        }
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Semantics;

namespace TypeScript.Compatibility;

internal static class AutoImportIndexTests
{
    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            var root = input.RootElement;
            if (root.TryGetProperty("mode", out var mode) && mode.GetString() == "unicode")
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] data = new byte[16];
                for (int point = 0; point <= 0x10ffff; point++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(data, GoUnicode.Upper(point));
                    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), GoUnicode.Lower(point));
                    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), GoUnicode.Fold(point));
                    data[12] = (byte)(GoUnicode.IsUpper(point) ? 1 : 0);
                    data[13] = (byte)(GoUnicode.IsLower(point) ? 1 : 0);
                    hash.AppendData(data);
                }
                Console.WriteLine('"' + Convert.ToHexStringLower(hash.GetHashAndReset()) + '"');
                continue;
            }
            Utf8String Decode(JsonElement value) => Utf8String.Copy(Convert.FromBase64String(value.GetString()!));
            var names = root.GetProperty("names").EnumerateArray().Select(Decode).ToArray();
            var queries = root.GetProperty("queries").EnumerateArray().Select(Decode).ToArray();
            AutoImportIndex? index = new();
            for (int i = 0; i < names.Length; i++)
                if (!names[i].IsEmpty) index.Add(new(new(default, Utf8String.FromString(i.ToString())), default, 0, 0, names[i], default, false, null, default));
            if (root.TryGetProperty("nil", out var nil) && nil.GetBoolean()) index = null;
            var keep = root.TryGetProperty("keep", out var kept) ? kept.EnumerateArray().Select(item => item.GetInt32()).ToHashSet() : null;
            var clone = AutoImportIndex.Clone(index, entry => keep is null || keep.Contains(int.Parse(entry.Id.Name.ToString())));
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("words"); writer.WriteStartArray();
                foreach (var name in names)
                {
                    writer.WriteStartArray();
                    foreach (int offset in AutoImportIndex.WordIndices(name)) writer.WriteNumberValue(offset);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteBoolean("cloneNil", clone is null);
                writer.WritePropertyName("results"); writer.WriteStartArray();
                foreach (var current in new[] { index, clone })
                {
                    writer.WriteStartArray();
                    foreach (var query in queries)
                    {
                        writer.WriteStartArray();
                        if (current is not null)
                            foreach (var matches in new[] { current.Find(query, true), current.Find(query, false), current.Search(query) })
                            {
                                writer.WriteStartArray();
                                foreach (var match in matches) writer.WriteNumberValue(int.Parse(match.Id.Name.ToString()));
                                writer.WriteEndArray();
                            }
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
        }
    }
}

using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Incremental;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class BuildInfoTests
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
                writer.WriteStartObject();
                if (input.TryGetProperty("info", out var value))
                {
                    var info = BuildInfo.TryRead(JsonStrings.Raw(value).Memory) ?? throw new InvalidDataException("Invalid build info");
                    writer.WritePropertyName("info"); writer.WriteRawValue(info.ToJson());
                    writer.WriteBoolean("validVersion", info.IsValidVersion);
                    writer.WriteBoolean("incremental", info.IsIncremental);
                }
                else
                {
                    Utf8String text = input.TryGetProperty("textBase64", out var encoded)
                        ? new(Convert.FromBase64String(encoded.GetString()!)) : JsonStrings.GetString(input.GetProperty("text"));
                    writer.WritePropertyName("hash");
                    JsonStrings.WriteString(writer, BuildInfo.ComputeHash(text,
                        input.TryGetProperty("includeText", out var include) && include.GetBoolean()));
                }
                writer.WriteEndObject();
            }
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    internal static int Safety()
    {
        int count = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException($"Build info assertion {count + 1}"); count++; }
        foreach (string json in new[]
        {
            "", "{", "null", "[]", "{\"fileNames\":{}}", "{\"errors\":1}", "{\"options\":[]}",
            "{\"fileNames\":[\"a.ts\"],\"root\":[[1]]}", "{\"fileNames\":[\"a.ts\"],\"root\":[[2,1]]}",
            "{\"fileNames\":[\"a.ts\"],\"root\":[2]}", "{\"fileNames\":[\"a.ts\"],\"root\":[\"a.ts\"]}",
            "{\"fileNames\":[\"a.ts\"],\"fileInfos\":[\"x\",\"y\"]}",
            "{\"fileNames\":[\"a.ts\"],\"referencedMap\":[[1,1]]}",
            "{\"fileNames\":[\"a.ts\"],\"fileIdsList\":[[0]]}",
            "{\"fileNames\":[\"a.ts\"],\"affectedFilesPendingEmit\":[[1,64]]}",
            "{\"fileNames\":[\"a.ts\"],\"affectedFilesPendingEmit\":[[]]}",
            "{\"fileNames\":[\"a.ts\"],\"emitSignatures\":[[1,[] ,0]]}",
            "{\"fileNames\":[\"a.ts\"],\"semanticDiagnosticsPerFile\":[[1,{}]]}",
            "{\"fileNames\":[\"a.ts\"],\"changeFileSet\":[2147483648]}",
            "{\"fileNames\":[\"a.ts\"],\"resolvedRoot\":[[1,0]]}"
        }) Check(BuildInfo.TryRead(Encoding.UTF8.GetBytes(json)) is null);
        var original = new BuildInfo
        {
            FileNames = ["./a.ts"u8], FileInfos = [new("v"u8, null)], Root = [new(1)],
            AffectedFilesPendingEmit = [new(1, FileEmitKind.Declarations)],
            EmitSignatures = [new(1, "s"u8, EmitSignatureKind.DifferentOptions)]
        };
        var bytes = original.ToJson();
        var restored = BuildInfo.TryRead(bytes)!;
        Check(restored.IsValidVersion && restored.IsIncremental);
        Check(restored.FileInfos![0].Signature is null);
        Check(restored.EmitSignatures![0] == original.EmitSignatures[0]);
        Check(restored.AffectedFilesPendingEmit![0] == original.AffectedFilesPendingEmit[0]);
        Array.Fill(bytes, (byte)0);
        Check(restored.FileNames![0] == "./a.ts"u8);
        Check(BuildInfo.GetPendingEmitKind(FileEmitKind.JavaScript | FileEmitKind.JavaScriptMap, FileEmitKind.JavaScript)
            == (FileEmitKind.JavaScript | FileEmitKind.JavaScriptMap));
        Check(BuildInfo.GetPendingEmitKind(FileEmitKind.AllDeclarations, FileEmitKind.Declarations)
            == FileEmitKind.AllDeclarationOutput);
        Check(BuildInfo.GetPendingEmitKind(FileEmitKind.All, FileEmitKind.All) == FileEmitKind.None);
        return count;
    }
}

using System.Globalization;
using System.Text.Json;
using TypeScript.Compiler.Experiments;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Storage;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args is ["--checker-generic-relations-safety"])
            {
                CheckerGenericRelationTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--checker-indexing-safety"])
            {
                CheckerIndexTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--checker-assignability-safety"])
            {
                CheckerAssignabilityTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--checker-relations-safety"])
            {
                CheckerRelationTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--checker-signatures-safety"])
            {
                CheckerSignatureTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--checker-properties-safety"])
            {
                CheckerPropertyTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--checker-values-safety"])
            {
                CheckerSymbolTypeTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--checker-members-safety"])
            {
                CheckerMemberTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--checker-type-nodes-safety"])
            {
                CheckerTypeNodeTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--checker-alias-safety"])
            {
                CheckerAliasTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--checker-program-safety"])
            {
                CheckerProgramTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--checker-program-lines"])
            {
                CheckerProgramTests.Lines();
                return 0;
            }
            if (args is ["--checker-mapped-members-safety"])
            {
                CheckerMappedMemberTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--checker-mapped-safety"])
            {
                CheckerMappedTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--checker-objects-safety"])
            {
                CheckerObjectTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--checker-instantiation-safety"])
            {
                CheckerInstantiationTests.Safety();
                return 0;
            }
            if (args is ["--checker-constraints-safety"])
            {
                CheckerConstraintTests.Safety();
                return 0;
            }
            if (args is ["--checker-algebra-safety"])
            {
                CheckerAlgebraTests.Safety();
                return 0;
            }
            if (args is ["--checker-names-safety"])
            {
                CheckerNameTests.Safety();
                return 0;
            }
            if (args is ["--checker-symbols-lines"])
            {
                CheckerSymbolTests.Lines();
                return 0;
            }
            if (args is ["--checker-names-lines"])
            {
                CheckerNameTests.Lines();
                return 0;
            }
            if (args is ["--checker-state"])
            {
                CheckerStateTests.Safety();
                return 0;
            }
            if (args is ["--checker-types-lines"])
            {
                CheckerTypeTests.Lines();
                return 0;
            }
            if (args is ["--mapper-codec-lines"])
            {
                MapperCodecTests.Lines();
                return 0;
            }
            if (args is ["--program-safety"])
            {
                ProgramGraphTests.Safety().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--content-mappers", var mapperRepository])
            {
                ContentMapperTests.Run(mapperRepository).GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--mapping-lines"])
            {
                MappingTests.Lines();
                return 0;
            }
            if (args is ["--program-lines"])
            {
                ProgramGraphTests.Lines().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--binding-lines"])
            {
                BindingTests.Lines();
                return 0;
            }
            if (args is ["--resolution-lines"])
            {
                ResolutionTests.Lines();
                return 0;
            }
            if (args is ["--javascript-syntax", var syntaxRepository])
            {
                JavaScriptSyntaxTests.Run(syntaxRepository);
                return 0;
            }
            if (args is ["--parser-single-worker"])
            {
                ParserSafetyTests.RunSingleWorkerDocumentation();
                return 0;
            }
            if (args is ["--modules"])
            {
                ModuleTests.Run();
                return 0;
            }
            if (args is ["--hosts", var hostRepository])
            {
                HostTests.Run(hostRepository);
                return 0;
            }
            if (args is ["--host-lines"])
            {
                HostTests.RunLines();
                return 0;
            }
            if (args is ["--parser-safety"])
            {
                ParserSafetyTests.Run();
                return 0;
            }
            if (args is ["--foundations", var repository])
            {
                FoundationTests.Run(repository);
                return 0;
            }
            if (args is ["--scan-lines"])
            {
                SyntaxTests.ScanLines();
                return 0;
            }
            if (args is ["--profile-pipeline", var profileInputs, var profileDirectory])
            {
                ProfileExperiments.Run(profileInputs, profileDirectory);
                return 0;
            }
            if (args is ["--pipeline" or "--pipeline-benchmark", var fixturePath, var resultsPath])
            {
                PipelineExperiments.Run(fixturePath, resultsPath, args[0] == "--pipeline-benchmark");
                return 0;
            }
            if (args is ["--startup"])
            {
                Console.WriteLine("TypeScript C# NativeAOT experiment; compiler not implemented");
                return 0;
            }
            if (args is ["--native-check"])
            {
                if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
                    throw new InvalidOperationException("Expected the published NativeAOT runtime");
                Console.WriteLine(
                    $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; Vector128={System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated}; Vector256={System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated}; Vector512={System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated}");
                return 0;
            }
            if (args is ["--profile-workload", var seconds])
            {
                Experiments.ProfileWorkload(int.Parse(seconds, CultureInfo.InvariantCulture));
                return 0;
            }
            if (args is not ["--verify" or "--benchmark", _, _])
                throw new ArgumentException(
                    "Usage: --verify|--benchmark <input.json> <Go-output.json>, --startup, --profile-workload <seconds>");
            using JsonDocument input = JsonDocument.Parse(File.ReadAllBytes(args[1]));
            using JsonDocument expected = JsonDocument.Parse(File.ReadAllBytes(args[2]));
            Verify(
                input.RootElement,
                expected.RootElement,
                Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2]))!, "semantic-differences.json"));
            if (args[0] == "--benchmark")
                Experiments.Benchmark(input.RootElement, expected.RootElement);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Verify(JsonElement input, JsonElement expected, string differencesPath)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition)
                throw new InvalidDataException(message);
        }
        var expectedTexts = expected.GetProperty("texts");
        int textIndex = 0;
        foreach (JsonElement textElement in input.GetProperty("texts").EnumerateArray())
        {
            byte[] bytes = textElement.GetBytesFromBase64();
            JsonElement wanted = expectedTexts[textIndex];
            string decoded = Wtf8.DecodeString(bytes);
            int scalarLines = 0;
            foreach (byte value in bytes)
                if (value == (byte)'\n')
                    scalarLines++;
            Check(bytes.AsSpan().Count((byte)'\n') == scalarLines, $"BCL SIMD/scalar count {textIndex}");
            int[] units = wanted.GetProperty("units").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            Check(decoded.Select(ch => (int)ch).SequenceEqual(units), $"WTF-8 text {textIndex}");
            Check(
                Wtf8.CombineSurrogatePairs(bytes).AsSpan().SequenceEqual(wanted.GetProperty("combined").GetBytesFromBase64()),
                $"Concatenation {textIndex}");
            Check(Wtf8.DecodeString(Wtf8.Encode(decoded)) == decoded, $"WTF-8 UTF-16 round trip {textIndex}");
            var map = new PositionMap(bytes);
            int offset = -1;
            foreach (JsonElement value in wanted.GetProperty("toUtf16").EnumerateArray())
                Check(map.Utf8ToUtf16(offset++) == value.GetInt32(), $"UTF-8 position {textIndex}:{offset - 1}");
            offset = -1;
            foreach (JsonElement value in wanted.GetProperty("toUtf8").EnumerateArray())
                Check(map.Utf16ToUtf8(offset++) == value.GetInt32(), $"UTF-16 position {textIndex}:{offset - 1}");
            textIndex++;
        }
        int numberIndex = 0;
        int permittedPowerDifferences = 0;
        var differences = new List<(int Index, string Go, string Candidate, string Node)>();
        foreach (JsonElement pair in input.GetProperty("numbers").EnumerateArray())
        {
            double x = BitConverter.UInt64BitsToDouble(
                ulong.Parse(pair[0].GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            double y = BitConverter.UInt64BitsToDouble(
                ulong.Parse(pair[1].GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            double[] results = NumberOperations.Evaluate(x, y);
            JsonElement wanted = expected.GetProperty("numbers")[numberIndex];
            for (int i = 0; i < results.Length; i++)
            {
                string bits = double.IsNaN(results[i])
                    ? "nan"
                    : BitConverter.DoubleToUInt64Bits(results[i]).ToString("x16", CultureInfo.InvariantCulture);
                bool equal = bits == wanted[i].GetString();
                if (!equal && i == 8)
                {
                    // Named policy: ECMAScript implementation-approximated power.
                    // Compare against the independent Node/V8 result. Keep zero,
                    // infinities, NaN, and sign exact; allow <=4 ULP only for
                    // finite nonzero same-sign powers. This is a regression guard,
                    // not a proof or a spec-mandated error bound.
                    string jsBits = input.GetProperty("powers")[numberIndex].GetString()!;
                    equal = bits == jsBits || NumberOperations.ApproximatePower(results[i], jsBits);
                    if (equal)
                    {
                        permittedPowerDifferences++;
                        differences.Add((numberIndex, wanted[i].GetString()!, bits, jsBits));
                    }
                }
                Check(equal, $"Numeric pair {numberIndex}, operation {i}: {x:R}, {y:R}; {bits} != {wanted[i]}");
            }
            numberIndex++;
        }
        var types = new List<TypeAtom[]>();
        foreach (JsonElement expression in input.GetProperty("types").EnumerateArray())
            types.Add(TypeRelations.Parse(expression.GetString(), types));
        int relationIndex = 0;
        foreach (TypeAtom[] source in types)
            foreach (TypeAtom[] target in types)
            {
                bool actual = TypeRelations.Assignable<StrictAssignment>(source, target);
                Check(
                    actual == expected.GetProperty("relations")[relationIndex].GetBoolean(),
                    $"Type relation {relationIndex / types.Count} -> {relationIndex % types.Count}");
                Check(actual == TypeRelations.AssignableConcrete(source, target), $"Generic/concrete relation {relationIndex}");
                relationIndex++;
            }
        int nodeCount = 0;
        int fileIndex = 0;
        foreach (JsonElement file in expected.GetProperty("files").EnumerateArray())
        {
            byte[] wire = file.GetProperty("wire").GetBytesFromBase64();
            var packet = new AstPacket(wire);
            Check(packet.Reencode().AsSpan().SequenceEqual(wire), $"AST packet {file.GetProperty("name")}");
            Check(
                packet.HasContentHash(input.GetProperty("files")[fileIndex++].GetProperty("text").GetBytesFromBase64()),
                "BCL xxh3-128 content hash / byte order");
            nodeCount += packet.NodeCount;
            var arena = new Arena<NodeRecord>(31);
            for (int i = 0; i < packet.NodeCount; i++)
            {
                Handle<NodeRecord> handle = arena.Add(packet.GetNode(i));
                Check(arena[handle] == packet.GetNode(i), $"Arena node {i}");
            }
        }
        Check(Experiments.CheckOwnershipAndStack(), "Ownership, identity, and stack safety");
        // Repeated decoding must remain stable across collections.
        for (int repetition = 0; repetition < 5; repetition++)
        {
            GC.Collect();
            foreach (JsonElement file in expected.GetProperty("files").EnumerateArray())
            {
                byte[] bytes = file.GetProperty("wire").GetBytesFromBase64();
                Check(new AstPacket(bytes).Reencode().AsSpan().SequenceEqual(bytes), $"Packet reload {repetition}");
            }
        }
        Check(Experiments.CheckNativeInterop(), "Native process interop");
        Check(Experiments.CheckFileSystem(), "Filesystem byte and UTF-16 path roundtrip");
        byte[] firstPacket = expected.GetProperty("files")[0].GetProperty("wire").GetBytesFromBase64();
        foreach (int length in new[] { 0, 1, 43, firstPacket.Length - 1 })
        {
            bool rejected = false;
            try
            {
                _ = new AstPacket(firstPacket.AsSpan(0, length));
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            Check(rejected, $"Truncated packet accepted at {length}");
        }
        byte[] damaged = (byte[])firstPacket.Clone();
        damaged.AsSpan(24, 4).Fill(255);
        bool invalidOffsetRejected = false;
        try
        {
            _ = new AstPacket(damaged);
        }
        catch (InvalidDataException)
        {
            invalidOffsetRejected = true;
        }
        Check(invalidOffsetRejected, "Invalid section accepted");
        using (var stream = File.Create(differencesPath))
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("policy", "ecmascript-power-approximation");
            writer.WriteStartArray("differences");
            foreach (var difference in differences)
            {
                writer.WriteStartObject();
                writer.WriteNumber("pairIndex", difference.Index);
                writer.WriteString("goBits", difference.Go);
                writer.WriteString("candidateBits", difference.Candidate);
                writer.WriteString("nodeBits", difference.Node);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        Console.WriteLine(
            $"Verified {checks} assertions: {textIndex} texts, {numberIndex} number pairs, {relationIndex} type relations, {nodeCount} AST records; {permittedPowerDifferences} permitted power rounding differences.");
    }
}

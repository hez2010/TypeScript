using System.Diagnostics;
using System.Text.Json;
using TypeScript.Compiler.Experiments;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Storage;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compatibility;

internal static class PipelineExperiments
{
    internal readonly record struct InputFile(Utf8String Name, byte[] Text);

    internal static SliceProject<TStore> Compile<TStore, TSource>(
        InputFile[] inputs,
        Func<TStore> createStore,
        Func<byte[], TSource> createSource,
        CancellationToken cancellation = default)
        where TStore : INodeStore where TSource : struct, ISourceView
    {
        var files = new List<SliceFile<TStore>>(inputs.Length);
        foreach (InputFile input in inputs)
        {
            var file = new SliceFile<TStore>(input.Name, input.Text, createStore());
            files.Add(new SliceParser<TStore, TSource>(file, createSource(input.Text), cancellation).Parse());
        }
        var project = new SliceProject<TStore>(files, cancellation);
        project.Check();
        return project;
    }

    public static void Run(Utf8String inputPath, Utf8String outputPath, bool benchmark)
    {
        using var input = JsonDocument.Parse(File.ReadAllBytes(inputPath.ToString()));
        using var stream = File.Create(outputPath.ToString());
        using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
        writer.WriteStartObject();
        writer.WriteStartArray("cases"u8);
        foreach (JsonElement fixture in input.RootElement.GetProperty("cases"u8).EnumerateArray())
        {
            InputFile[] files = fixture.GetProperty("files"u8).EnumerateArray().Select(
                file => new InputFile(JsonStrings.GetString(file.GetProperty("name"u8))!, file.GetProperty("text"u8).GetBytesFromBase64())).ToArray();
            var classes = Compile(files, static () => new ClassNodeStore(), static bytes => new Utf8Source(bytes));
            var arenas = Compile(files, static () => new ArenaNodeStore(), static bytes => new Utf8Source(bytes));
            if (!classes.Diagnostics.SequenceEqual(arenas.Diagnostics))
                throw new InvalidDataException("Storage/encoding diagnostics differ");
            writer.WriteStartObject();
            writer.WriteString("name"u8, JsonStrings.GetString(fixture.GetProperty("name"u8)));
            writer.WriteStartArray("diagnostics"u8);
            foreach (ProjectDiagnostic diagnostic in arenas.Diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteString("file"u8, diagnostic.File);
                writer.WriteNumber("code"u8, (int)diagnostic.Code);
                writer.WriteNumber("pos"u8, diagnostic.Pos);
                writer.WriteNumber("length"u8, diagnostic.Length);
                writer.WriteString("message"u8, diagnostic.Message);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("files"u8);
            foreach (InputFile file in files)
            {
                byte[] packet = SliceEncoder.Encode(arenas.Files[file.Name]);
                var clone = SliceTrees.Clone(arenas.Files[file.Name], new ClassNodeStore());
                if (!packet.AsSpan().SequenceEqual(SliceEncoder.Encode(clone)))
                    throw new InvalidDataException("Generated clone changed the tree/metadata");
                if (!packet.AsSpan().SequenceEqual(SliceEncoder.Encode(classes.Files[file.Name])))
                    throw new InvalidDataException("Independent packets differ across representations");
                if (!new AstPacket(packet).HasContentHash(file.Text))
                    throw new InvalidDataException("Invalid independent source hash");
                writer.WriteStartObject();
                writer.WriteString("name"u8, file.Name);
                writer.WriteBase64String("wire"u8, packet);
                writer.WriteStartArray("locals"u8);
                foreach (Utf8String name in arenas.Locals[file.Name].Keys.Order(Utf8StringComparer.Ordinal))
                    writer.WriteStringValue(name);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            var symbols = arenas.Files.Keys.Order(Utf8StringComparer.Ordinal).SelectMany(
                file => arenas.Locals[file].OrderBy(
                    pair => pair.Key,
                    Utf8StringComparer.Ordinal).Select(pair => pair.Value).Where(symbol => symbol.File.Name == file)).Distinct().ToArray();
            if (arenas.Diagnostics.Count == 0)
            {
                var printed = files.Select(
                    file => new InputFile(
                        file.Name,
                        SlicePrinter.Print(arenas.Files[file.Name]).Span.ToArray())).ToArray();
                var recompiled = Compile(printed, static () => new ArenaNodeStore(), static bytes => new Utf8Source(bytes));
                if (recompiled.Diagnostics.Count != 0)
                    throw new InvalidDataException("Printed source no longer checks");
                foreach (var symbol in symbols)
                {
                    var before = arenas.TypeOf(symbol);
                    var after = recompiled.TypeOf(recompiled.Locals[symbol.File.Name][symbol.Name]);
                    if (!TypeRelations.Assignable<StrictAssignment>(before, after)
                        || !TypeRelations.Assignable<StrictAssignment>(after, before))
                        throw new InvalidDataException("Printer changed a type");
                }
            }
            writer.WriteStartArray("symbolNames"u8);
            foreach (var symbol in symbols)
                writer.WriteStringValue(Utf8String.Concat(symbol.File.Name, ":"u8) + symbol.Name);
            writer.WriteEndArray();
            writer.WriteStartArray("relations"u8);
            if (arenas.Diagnostics.Count == 0)
                foreach (var source in symbols)
                    foreach (var target in symbols)
                        writer.WriteBooleanValue(TypeRelations.Assignable<StrictAssignment>(arenas.TypeOf(source), arenas.TypeOf(target)));
            writer.WriteEndArray();
            if (benchmark && JsonStrings.GetString(fixture.GetProperty("name"u8)) == "large-graph"u8)
                Measure(writer, files);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        CheckDeepAndCancellation();
        writer.WriteBoolean("deepAndCancellation"u8, true);
        writer.WriteEndObject();
    }

    private static void Measure(Utf8JsonWriter writer, InputFile[] files)
    {
        var workloads = new (Utf8String Name, Func<long> Run)[]
        {
            (Utf8String.Copy("classes-utf8"u8), () => Consume(Compile(files, static () => new ClassNodeStore(), static bytes => new Utf8Source(bytes)))),
            (Utf8String.Copy("arena-utf8"u8), () => Consume(Compile(files, static () => new ArenaNodeStore(), static bytes => new Utf8Source(bytes)))),
        };
        long expected = workloads[0].Run();
        foreach (var workload in workloads)
            if (workload.Run() != expected)
                throw new InvalidDataException("Pipeline results differ");
        writer.WriteStartArray("measurements"u8);
        for (int sample = 0; sample < 15; sample++)
            for (int ordinal = 0; ordinal < workloads.Length; ordinal++)
            {
                var workload = workloads[(ordinal + sample) % workloads.Length];
                GC.Collect();
                GC.WaitForPendingFinalizers();
                long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
                const int iterations = 20;
                long checksum = 0;
                for (int iteration = 0; iteration < iterations; iteration++)
                    checksum += workload.Run();
                double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
                long allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
                if (checksum != expected * iterations)
                    throw new InvalidDataException("Pipeline checksum changed");
                writer.WriteStartObject();
                writer.WriteString("name"u8, workload.Name);
                writer.WriteNumber("milliseconds"u8, milliseconds);
                writer.WriteNumber("allocatedBytes"u8, allocated);
                writer.WriteNumber("checksum"u8, checksum);
                writer.WriteEndObject();
            }
        writer.WriteEndArray();
        static long Consume<TStore>(SliceProject<TStore> project) where TStore : INodeStore
        {
            if (project.Diagnostics.Count != 0)
                throw new InvalidDataException("Benchmark has diagnostics");
            long sum = 0;
            foreach (var file in project.Files.Values)
                sum += SliceEncoder.Encode(file).Length;
            return sum;
        }
    }

    private static void CheckDeepAndCancellation()
    {
        byte[] text = (Utf8String.Concat("export type Deep = "u8, new Utf8String('(', 20_000), "string"u8) + new Utf8String(')', 20_000) + ";"u8).Span.ToArray();
        InputFile[] files = [new("/deep.ts"u8, text)];
        var deep = Compile(files, static () => new ArenaNodeStore(), static bytes => new Utf8Source(bytes));
        if (deep.Diagnostics.Count != 0 || deep.TypeOf(deep.Locals["/deep.ts"u8]["Deep"u8])[0].Kind != AtomKind.String)
            throw new InvalidDataException("Deep parse/check failed");
        _ = SliceEncoder.Encode(deep.Files["/deep.ts"u8]);
        var cloned = SliceTrees.Clone(deep.Files["/deep.ts"u8], new ArenaNodeStore());
        if (cloned.Store.Count != deep.Files["/deep.ts"u8].Store.Count)
            throw new InvalidDataException("Deep clone lost nodes");
        Utf8String printed = SlicePrinter.Print(deep.Files["/deep.ts"u8]);
        if (printed.Span.Count((byte)'(') != 20_000)
            throw new InvalidDataException("Deep printer lost nesting");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            _ = Compile(files, static () => new ArenaNodeStore(), static bytes => new Utf8Source(bytes), cancellation.Token);
            throw new InvalidDataException("Cancellation was ignored");
        }
        catch (OperationCanceledException) { }
        Utf8String chain = Utf8String.Join(
            (byte)'\n',
            Enumerable.Range(0, 10_000).Select(i => Utf8String.ConcatMany("type T"u8, Utf8String.Format(i), " = "u8, i == 9_999 ? Utf8String.Copy("string"u8) : Utf8String.Concat("T"u8, Utf8String.Format(i + 1)), ";"u8)));
        var aliases = Compile(
            [new("/chain.ts"u8, chain.Span.ToArray())],
            static () => new ArenaNodeStore(),
            static bytes => new Utf8Source(bytes));
        if (aliases.Diagnostics.Count != 0 || aliases.TypeOf(aliases.Locals["/chain.ts"u8]["T0"u8])[0].Kind != AtomKind.String)
            throw new InvalidDataException("Deep checker dependency traversal failed");
        CheckSnapshots();
    }

    private static void CheckSnapshots()
    {
        using var workspace = new SliceWorkspace<ArenaNodeStore>(static () => new());
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/a.ts"u8] = "export type A = string;"u8.ToArray(),
            ["/b.ts"u8] = "export type B = number;"u8.ToArray()
        };
        workspace.Update(files);
        using var old = workspace.Acquire();
        var unchanged = old.Root("/a.ts"u8);
        var replaced = old.Root("/b.ts"u8);
        var symbol = old.Project.Locals["/a.ts"u8]["A"u8];
        files["/b.ts"u8] = "export type B = boolean;"u8.ToArray();
        workspace.Update(files);
        using var current = workspace.Acquire();
        if (!ReferenceEquals(current.Root("/a.ts"u8).Owner, unchanged.Owner))
            throw new InvalidDataException("Unchanged source owner not shared");
        _ = old.Read(replaced);
        _ = current.Read(unchanged);
        try
        {
            _ = current.Read(replaced);
            throw new InvalidDataException("Stale node owner accepted");
        }
        catch (ArgumentException) { }
        Parallel.For(0, 32, _ =>
        {
            if (!ReferenceEquals(old.Project.Locals["/a.ts"u8]["A"u8], symbol) || old.Project.TypeOf(symbol)[0].Kind != AtomKind.String)
                throw new InvalidDataException("Snapshot query identity changed");
        });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            workspace.Update(files, cancelled.Token);
            throw new InvalidDataException("Cancelled update published");
        }
        catch (OperationCanceledException) { }
        using var retained = workspace.Acquire();
        if (!ReferenceEquals(retained.Project, current.Project))
            throw new InvalidDataException("Cancellation replaced snapshot");
        workspace.Dispose();
        GC.Collect();
        _ = old.Read(replaced);
        old.Dispose();
        try
        {
            _ = old.Read(replaced);
            throw new InvalidDataException("Disposed lease accepted");
        }
        catch (ObjectDisposedException) { }
    }
}

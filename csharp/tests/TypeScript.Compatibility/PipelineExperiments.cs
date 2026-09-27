using System.Diagnostics;
using System.Text.Json;
using TypeScript.Compiler.Experiments;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Storage;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compatibility;

internal static class PipelineExperiments
{
    internal readonly record struct InputFile(string Name, byte[] Text);

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

    public static void Run(string inputPath, string outputPath, bool benchmark)
    {
        using var input = JsonDocument.Parse(File.ReadAllBytes(inputPath));
        using var stream = File.Create(outputPath);
        using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
        writer.WriteStartObject();
        writer.WriteStartArray("cases");
        foreach (JsonElement fixture in input.RootElement.GetProperty("cases").EnumerateArray())
        {
            InputFile[] files = fixture.GetProperty("files").EnumerateArray().Select(
                file => new InputFile(file.GetProperty("name").GetString()!, file.GetProperty("text").GetBytesFromBase64())).ToArray();
            var classes = Compile(files, static () => new ClassNodeStore(), static bytes => new Utf8Source(bytes));
            var classes16 = Compile(files, static () => new ClassNodeStore(), static bytes => new Utf16Source(bytes));
            var arenas = Compile(files, static () => new ArenaNodeStore(), static bytes => new Utf8Source(bytes));
            var utf16 = Compile(files, static () => new ArenaNodeStore(), static bytes => new Utf16Source(bytes));
            if (!classes.Diagnostics.SequenceEqual(arenas.Diagnostics)
                || !arenas.Diagnostics.SequenceEqual(utf16.Diagnostics)
                || !classes.Diagnostics.SequenceEqual(classes16.Diagnostics))
                throw new InvalidDataException("Storage/encoding diagnostics differ");
            writer.WriteStartObject();
            writer.WriteString("name", fixture.GetProperty("name").GetString());
            writer.WriteStartArray("diagnostics");
            foreach (ProjectDiagnostic diagnostic in arenas.Diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteString("file", diagnostic.File);
                writer.WriteNumber("code", (int)diagnostic.Code);
                writer.WriteNumber("pos", diagnostic.Pos);
                writer.WriteNumber("length", diagnostic.Length);
                writer.WriteString("message", diagnostic.Message);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("files");
            foreach (InputFile file in files)
            {
                byte[] packet = SliceEncoder.Encode(arenas.Files[file.Name]);
                var clone = SliceTrees.Clone(arenas.Files[file.Name], new ClassNodeStore());
                if (!packet.AsSpan().SequenceEqual(SliceEncoder.Encode(clone)))
                    throw new InvalidDataException("Generated clone changed the tree/metadata");
                if (!packet.AsSpan().SequenceEqual(SliceEncoder.Encode(classes.Files[file.Name]))
                    || !packet.AsSpan().SequenceEqual(SliceEncoder.Encode(utf16.Files[file.Name]))
                    || !packet.AsSpan().SequenceEqual(SliceEncoder.Encode(classes16.Files[file.Name])))
                    throw new InvalidDataException("Independent packets differ across representations");
                if (!new AstPacket(packet).HasContentHash(file.Text))
                    throw new InvalidDataException("Invalid independent source hash");
                writer.WriteStartObject();
                writer.WriteString("name", file.Name);
                writer.WriteBase64String("wire", packet);
                writer.WriteStartArray("locals");
                foreach (string name in arenas.Locals[file.Name].Keys.Order(StringComparer.Ordinal))
                    writer.WriteStringValue(name);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            var symbols = arenas.Files.Keys.Order(StringComparer.Ordinal).SelectMany(
                file => arenas.Locals[file].OrderBy(
                    pair => pair.Key,
                    StringComparer.Ordinal).Select(pair => pair.Value).Where(symbol => symbol.File.Name == file)).Distinct().ToArray();
            if (arenas.Diagnostics.Count == 0)
            {
                var printed = files.Select(
                    file => new InputFile(
                        file.Name,
                        System.Text.Encoding.UTF8.GetBytes(SlicePrinter.Print(arenas.Files[file.Name])))).ToArray();
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
            writer.WriteStartArray("symbolNames");
            foreach (var symbol in symbols)
                writer.WriteStringValue(symbol.File.Name + ":" + symbol.Name);
            writer.WriteEndArray();
            writer.WriteStartArray("relations");
            if (arenas.Diagnostics.Count == 0)
                foreach (var source in symbols)
                    foreach (var target in symbols)
                        writer.WriteBooleanValue(TypeRelations.Assignable<StrictAssignment>(arenas.TypeOf(source), arenas.TypeOf(target)));
            writer.WriteEndArray();
            if (benchmark && fixture.GetProperty("name").GetString() == "large-graph")
                Measure(writer, files);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        CheckDeepAndCancellation();
        writer.WriteBoolean("deepAndCancellation", true);
        writer.WriteEndObject();
    }

    private static void Measure(Utf8JsonWriter writer, InputFile[] files)
    {
        var workloads = new (string Name, Func<long> Run)[]
        {
            ("classes-utf8", () => Consume(Compile(files, static () => new ClassNodeStore(), static bytes => new Utf8Source(bytes)))),
            ("classes-utf16", () => Consume(Compile(files, static () => new ClassNodeStore(), static bytes => new Utf16Source(bytes)))),
            ("arena-utf8", () => Consume(Compile(files, static () => new ArenaNodeStore(), static bytes => new Utf8Source(bytes)))),
            ("arena-utf16", () => Consume(Compile(files, static () => new ArenaNodeStore(), static bytes => new Utf16Source(bytes)))),
        };
        long expected = workloads[0].Run();
        foreach (var workload in workloads)
            if (workload.Run() != expected)
                throw new InvalidDataException("Pipeline results differ");
        writer.WriteStartArray("measurements");
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
                writer.WriteString("name", workload.Name);
                writer.WriteNumber("milliseconds", milliseconds);
                writer.WriteNumber("allocatedBytes", allocated);
                writer.WriteNumber("checksum", checksum);
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
        byte[] text = System.Text.Encoding.UTF8.GetBytes(
            "export type Deep = " + new string('(', 20_000) + "string" + new string(')', 20_000) + ";");
        InputFile[] files = [new("/deep.ts", text)];
        var deep = Compile(files, static () => new ArenaNodeStore(), static bytes => new Utf8Source(bytes));
        if (deep.Diagnostics.Count != 0 || deep.TypeOf(deep.Locals["/deep.ts"]["Deep"])[0].Kind != AtomKind.String)
            throw new InvalidDataException("Deep parse/check failed");
        _ = SliceEncoder.Encode(deep.Files["/deep.ts"]);
        var cloned = SliceTrees.Clone(deep.Files["/deep.ts"], new ArenaNodeStore());
        if (cloned.Store.Count != deep.Files["/deep.ts"].Store.Count)
            throw new InvalidDataException("Deep clone lost nodes");
        string printed = SlicePrinter.Print(deep.Files["/deep.ts"]);
        if (printed.Count(ch => ch == '(') != 20_000)
            throw new InvalidDataException("Deep printer lost nesting");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            _ = Compile(files, static () => new ArenaNodeStore(), static bytes => new Utf8Source(bytes), cancellation.Token);
            throw new InvalidDataException("Cancellation was ignored");
        }
        catch (OperationCanceledException) { }
        string chain = string.Join(
            '\n',
            Enumerable.Range(0, 10_000).Select(i => $"type T{i} = {(i == 9_999 ? "string" : "T" + (i + 1))};"));
        var aliases = Compile(
            [new("/chain.ts", System.Text.Encoding.UTF8.GetBytes(chain))],
            static () => new ArenaNodeStore(),
            static bytes => new Utf8Source(bytes));
        if (aliases.Diagnostics.Count != 0 || aliases.TypeOf(aliases.Locals["/chain.ts"]["T0"])[0].Kind != AtomKind.String)
            throw new InvalidDataException("Deep checker dependency traversal failed");
        CheckSnapshots();
    }

    private static void CheckSnapshots()
    {
        using var workspace = new SliceWorkspace<ArenaNodeStore>(static () => new());
        var files = new Dictionary<string, byte[]>
        {
            ["/a.ts"] = "export type A = string;"u8.ToArray(),
            ["/b.ts"] = "export type B = number;"u8.ToArray()
        };
        workspace.Update(files);
        using var old = workspace.Acquire();
        var unchanged = old.Root("/a.ts");
        var replaced = old.Root("/b.ts");
        var symbol = old.Project.Locals["/a.ts"]["A"];
        files["/b.ts"] = "export type B = boolean;"u8.ToArray();
        workspace.Update(files);
        using var current = workspace.Acquire();
        if (!ReferenceEquals(current.Root("/a.ts").Owner, unchanged.Owner))
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
            if (!ReferenceEquals(old.Project.Locals["/a.ts"]["A"], symbol) || old.Project.TypeOf(symbol)[0].Kind != AtomKind.String)
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

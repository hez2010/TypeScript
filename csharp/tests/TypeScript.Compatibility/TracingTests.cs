using System.Runtime.CompilerServices;
using System.Text.Json;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Execution;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;

namespace TypeScript.Compatibility;

internal static class TracingTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }

        async Task<Dictionary<string, int>> Threads(Utf8String[] paths)
        {
            var memory = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>(), true);
            await using (var trace = new CompilationTrace(memory, "/trace"u8, default, deterministic: true))
            {
                var first = trace.Begin("parse"u8, "createSourceFile"u8, paths[0], null, 0, 0, false);
                var second = trace.Begin("parse"u8, "createSourceFile"u8, paths[1], null, 0, 0, false);
                first!.Dispose(); second!.Dispose();
                using (trace.Begin("check"u8, "checkSourceFile"u8, paths[0], 0, 0, 0, false))
                    using (trace.Begin("checkTypes"u8, "structuredTypeRelatedTo"u8, default, 0, 1, 2, false)) { }
                Check(trace.Begin("checkTypes"u8, "sampled"u8, default, 0, 1, 2, true) is null, "Deterministic traces omit sampled operations");
            }
            var events = Read(memory, "/trace/trace.json"u8);
            ValidateEvents(events, Check);
            var parse = events.Where(item => item.GetProperty("name").GetString() == "createSourceFile" && item.GetProperty("ph").GetString() == "B").ToArray();
            Check(parse[0].GetProperty("tid").GetInt32() != parse[1].GetProperty("tid").GetInt32(), "Concurrent file scopes have separate lanes");
            var checkerEvents = events.Where(item => item.TryGetProperty("args", out var args) && args.TryGetProperty("checkerId", out _)).ToArray();
            Check(checkerEvents.Select(item => item.GetProperty("tid").GetInt32()).Distinct().Count() == 1, "Nested checker operations share one lane");
            return parse.ToDictionary(item => item.GetProperty("args").GetProperty("path").GetString()!, item => item.GetProperty("tid").GetInt32());
        }
        var forward = await Threads(["/a.ts"u8, "/b.ts"u8]);
        var reverse = await Threads(["/b.ts"u8, "/a.ts"u8]);
        Check(forward.All(pair => reverse[pair.Key] == pair.Value), "File lane identities do not depend on first-seen order");

        var stressMemory = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>(), true);
        var stress = new TraceFileSystem(stressMemory);
        await using (var trace = new CompilationTrace(stress, "/stress"u8, default, deterministic: true))
            for (int index = 0; index < 2000; index++)
                using (trace.Begin("check"u8, "checkSourceFile"u8, "/large.ts"u8, 0, 0, 0, false)) { }
        Check(stress.Appends > 2, "Long traces flush multiple bounded chunks");
        Check(stress.LargestAppend <= 256 * 1024 + 1024, "Normal event records do not accumulate an unbounded trace buffer");
        var stressEvents = Read(stressMemory, "/stress/trace.json"u8);
        Check(stressEvents.Count(item => item.GetProperty("ph").GetString() is "B" or "E") == 4000, "Flushing preserves every duration event");
        ValidateEvents(stressEvents, Check);

        Utf8String source = "export interface Box<T> { value: T; }\nexport type Picked<T> = T extends Box<infer U> ? U : never;\nexport const café: Box<number> = { value: 1 };\n"u8;
        MemoryFileSystem Files() => new(new Dictionary<Utf8String, byte[]>
        {
            ["/p/tsconfig.json"u8] = "{\"compilerOptions\":{\"strict\":true,\"skipLibCheck\":true},\"files\":[\"source.ts\"]}"u8.ToArray(),
            ["/p/source.ts"u8] = source.Span.ToArray(),
        }, true, "/p"u8);

        var memory = Files();
        using var output = new StringWriter();
        await using (var command = new CompilerCommand(new LibraryFileSystem(memory), "/p"u8, output))
        {
            Check(await command.ExecuteAsync(["--generateTrace"u8, "/capture"u8, "--extendedDiagnostics"u8]) == CompilerExitStatus.Success, "Tracing preserves compilation success");
            Check(CompilationCapture.Current is null, "A completed command restores the ambient capture");
            Check(output.ToString().Contains("CLR managed bytes:") && output.ToString().Contains("CLR allocated bytes:"), "Runtime allocation units are explicit");
            Check(!output.ToString().Contains("Memory allocs:"), "CLR allocation bytes are not reported as Go allocation counts");
            ValidateTrace(memory, "/capture"u8, Check);
        }
        Check(memory.ReadFile("/p/source.js"u8) is { Length: > 0 }, "Tracing retains emitted JavaScript");

        foreach (bool failAtStart in new[] { true, false })
        {
            var backing = Files();
            var failure = new TraceFileSystem(backing) { FailWrite = failAtStart ? "/failure/trace.json"u8 : default, FailAppend = !failAtStart };
            using var errors = new StringWriter();
            await using var command = new CompilerCommand(new LibraryFileSystem(failure), "/p"u8, errors);
            Check(await command.ExecuteAsync(["--generateTrace"u8, "/failure"u8]) == CompilerExitStatus.Success, "Trace I/O failures do not change compilation status");
            Check(errors.ToString().Contains(failAtStart ? "Failed to start tracing" : "Failed to stop tracing"), "Trace I/O failure is reported");
            Check(backing.ReadFile("/p/source.js"u8)!.AsSpan().SequenceEqual(memory.ReadFile("/p/source.js"u8)), "Trace I/O failures preserve ordinary outputs");
            Check(CompilationCapture.Current is null, "A failed trace restores the ambient capture");
        }

        var parallel = await Task.WhenAll(Enumerable.Range(0, 2).Select(async index =>
        {
            var files = Files(); using var text = new StringWriter();
            await using var command = new CompilerCommand(new LibraryFileSystem(files), "/p"u8, text);
            await command.ExecuteAsync(["--generateTrace"u8, Utf8String.FromString($"/parallel{index}"), "--noEmit"u8]);
            return files;
        }));
        for (int index = 0; index < parallel.Length; index++)
        {
            ValidateTrace(parallel[index], Utf8String.FromString($"/parallel{index}"), Check);
            Check(!parallel[index].FileExists(Utf8String.FromString($"/parallel{1 - index}/legend.json")), "Concurrent captures do not share output paths");
        }

        var control = await CompileWeakAsync(Files(), trace: false);
        var retained = await CompileWeakAsync(Files(), trace: true);
        for (int index = 0; index < 8; index++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10); }
        Check(!IsAlive(control), "The untraced control releases its program");
        Check(!IsAlive(retained), "Completed traces do not retain a released program");
        return checks;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<CompilerProgram>> CompileWeakAsync(MemoryFileSystem files, bool trace)
    {
        await using var command = new CompilerCommand(new LibraryFileSystem(files), "/p"u8, TextWriter.Null);
        await command.ExecuteAsync(trace ? ["--generateTrace"u8, "/weak"u8, "--noEmit"u8] : ["--noEmit"u8]);
        return new(command.Program!.Program);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<CompilerProgram> value) => value.TryGetTarget(out _);

    private static JsonElement[] Read(IFileSystem fileSystem, Utf8String path)
    {
        using var document = JsonDocument.Parse(fileSystem.ReadFile(path) ?? throw new InvalidDataException($"Missing trace output: {path}"));
        return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static void ValidateTrace(IFileSystem fileSystem, Utf8String directory, Action<bool, string> check)
    {
        var legend = Read(fileSystem, CompilerPath.Combine(directory, "legend.json"u8));
        check(legend.Length > 0, "A checked program has a type-file legend");
        ValidateEvents(Read(fileSystem, CompilerPath.Combine(directory, "trace.json"u8)), check);
        foreach (var entry in legend)
        {
            var types = Read(fileSystem, JsonStrings.GetString(entry.GetProperty("typesPath")));
            check(types.Length > 0 && types.Select((type, index) => type.GetProperty("id").GetUInt32() == index + 1).All(value => value), "Type IDs match complete ordered records");
            var ids = types.Select(type => type.GetProperty("id").GetUInt32()).ToHashSet();
            foreach (var type in types)
            {
                check(type.GetProperty("flags").GetArrayLength() != 0, "Every type has flags");
                foreach (var property in type.EnumerateObject())
                {
                    bool list = property.Name is "unionTypes" or "intersectionTypes" or "aliasTypeArguments" or "typeArguments";
                    bool scalar = property.Name is "keyofType" or "indexedAccessObjectType" or "indexedAccessIndexType" or "conditionalCheckType" or "conditionalExtendsType"
                        or "conditionalTrueType" or "conditionalFalseType" or "substitutionBaseType" or "constraintType" or "instantiatedType"
                        or "reverseMappedSourceType" or "reverseMappedMappedType" or "reverseMappedConstraintType" or "evolvingArrayElementType" or "evolvingArrayFinalType";
                    if (list) foreach (var id in property.Value.EnumerateArray()) check(ids.Contains(id.GetUInt32()), "Type list references resolve within their checker");
                    else if (scalar && property.Value.GetInt64() > 0) check(ids.Contains(property.Value.GetUInt32()), "Type references resolve within their checker");
                }
            }
        }
    }

    private static void ValidateEvents(JsonElement[] events, Action<bool, string> check)
    {
        var stacks = new Dictionary<int, Stack<string>>();
        check(events.Any(item => item.GetProperty("name").GetString() == "process_name"), "Trace metadata identifies the process");
        foreach (var item in events)
        {
            double time = item.GetProperty("ts").GetDouble();
            check(double.IsFinite(time) && time >= 0, "Event timestamps are nonnegative microseconds");
            int thread = item.GetProperty("tid").GetInt32(); string name = item.GetProperty("name").GetString()!;
            if (!stacks.TryGetValue(thread, out var stack)) stacks.Add(thread, stack = new());
            switch (item.GetProperty("ph").GetString())
            {
                case "B": stack.Push(name); break;
                case "E": check(stack.TryPop(out string? prior) && prior == name, "Duration events are nested within their logical lane"); break;
                case "X": check(item.GetProperty("dur").GetDouble() >= 0, "Sampled durations are nonnegative"); break;
            }
        }
        check(stacks.Values.All(stack => stack.Count == 0), "Every duration event is closed");
    }

    private sealed class TraceFileSystem(IFileSystem inner) : IFileSystem
    {
        internal Utf8String FailWrite;
        internal bool FailAppend;
        internal int Appends, LargestAppend;
        public bool CaseSensitive => inner.CaseSensitive;
        public bool FileExists(Utf8String path) => inner.FileExists(path);
        public bool DirectoryExists(Utf8String path) => inner.DirectoryExists(path);
        public byte[]? ReadFile(Utf8String path) => inner.ReadFile(path);
        public DirectoryEntries GetAccessibleEntries(Utf8String path) => inner.GetAccessibleEntries(path);
        public FileEntry? Stat(Utf8String path) => inner.Stat(path);
        public Utf8String RealPath(Utf8String path) => inner.RealPath(path);
        public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents)
        { if (path == FailWrite) throw new IOException("trace write failure"); inner.WriteFile(path, contents); }
        public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents)
        { if (FailAppend) throw new IOException("trace append failure"); Appends++; LargestAppend = Math.Max(LargestAppend, contents.Length); inner.AppendFile(path, contents); }
        public void Remove(Utf8String path) => inner.Remove(path);
        public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc) => inner.SetTimes(path, accessTimeUtc, writeTimeUtc);
    }
}

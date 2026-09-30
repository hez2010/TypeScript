using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;
using System.Text.Json;

namespace TypeScript.Compatibility;

internal static class CheckerPoolTests
{
    internal static async Task<int> CancellationSafety()
    {
        int checks = 0;
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        var files = new Dictionary<Utf8String, byte[]> { ["/project/main.ts"u8] = Wtf8.Encode("const x=1;const y=2;") };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
            new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        using var cancellation = new CancellationTokenSource();
        var pool = await program.CreateCheckerPoolAsync();
        var file = program.SourceFiles[0].Syntax;
        using (var lease = await pool.AcquireAsync(file))
            lease.Checker.BeforeSourceElement = _ => cancellation.Cancel();
        try
        {
            await pool.GetDiagnosticsAsync(cancellation.Token);
            throw new InvalidOperationException("Expected source-check cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        using (var lease = await pool.AcquireAsync(file))
        {
            lease.Checker.BeforeSourceElement = null;
            checks++;
        }
        try
        {
            await pool.GetDiagnosticsAsync();
            throw new InvalidOperationException("Expected unusable checker rejection");
        }
        catch (InvalidOperationException error) when (error.Message == "A cancelled source check requires a new checker")
        {
            checks++;
        }
        try
        {
            await program.CreateCheckerPoolAsync(cancellation: cancellation.Token);
            throw new InvalidOperationException("Expected cancelled pool creation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        var fresh = await program.CreateCheckerPoolAsync();
        var recovered = await fresh.GetDiagnosticsAsync();
        if (recovered.Semantic.Count != 0)
            throw new InvalidOperationException("Fresh checker did not recover after cancellation");
        checks++;
        var empty = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>()), "/project"u8,
            new("/project/tsconfig.json"u8, options, [], [], [], []));
        var emptyPool = await empty.CreateCheckerPoolAsync();
        var emptyResult = await emptyPool.GetDiagnosticsAsync();
        if (emptyPool.Count != 1 || emptyResult.Semantic.Count != 0)
            throw new InvalidOperationException("Empty program did not keep its global checker");
        return checks + 1;
    }

    internal static void Partitions(Utf8String input, Utf8String output)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(input.ToString()));
        var results = new List<int[]>();
        foreach (var row in document.RootElement.EnumerateArray())
            results.Add(CheckerPartitions.Assign(
                row.GetProperty("weights"u8).EnumerateArray().Select(x => x.GetInt64()).ToArray(),
                row.GetProperty("imports"u8).EnumerateArray().Select(x => x.GetInt32()).ToArray(),
                row.GetProperty("declarations"u8).EnumerateArray().Select(x => x.GetBoolean()).ToArray(),
                row.GetProperty("adjacency"u8).EnumerateArray().Select(
                    x => (IReadOnlyList<int>)x.EnumerateArray().Select(n => n.GetInt32()).ToArray()).ToArray(),
                row.GetProperty("count"u8).GetInt32()));
        using var stream = File.Create(output.ToString());
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartArray();
        foreach (var row in results)
        {
            writer.WriteStartArray();
            foreach (int value in row)
                writer.WriteNumberValue(value);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Checker pool assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("strict"u8, "true"u8);
        var files = new Dictionary<Utf8String, byte[]>();
        for (int i = 0; i < 12; i++)
            files[Utf8String.ConcatMany("/project/file"u8, Utf8String.Format(i), ".ts"u8)] = Wtf8.Encode($"export const value{i}:number='error';");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
            new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        var nodes = program.SourceFiles.SelectMany(f => f.Syntax.DescendantsAndSelf())
            .Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var pool = await program.CreateCheckerPoolAsync();
        Check(pool.Count == 4);
        var unique = new HashSet<Checker>();
        using var barrier = new CountdownEvent(pool.Count);
        foreach (var file in program.SourceFiles)
        {
            using var lease = await pool.AcquireAsync(file.Syntax);
            if (!unique.Add(lease.Checker))
                continue;
            int entered = 0;
            lease.Checker.BeforeSourceElement = _ =>
            {
                if (Interlocked.Exchange(ref entered, 1) == 0)
                {
                    barrier.Signal();
                    if (!barrier.Wait(TimeSpan.FromSeconds(15)))
                        throw new InvalidOperationException("Checker workers did not execute concurrently");
                }
            };
        }
        Check(unique.Count == pool.Count);
        var parallel = await pool.GetDiagnosticsAsync();
        Check(barrier.IsSet);
        Check(parallel.Semantic.Count(d => d.Code == DiagnosticCode.Type0IsNotAssignableToType1) == files.Count);
        Check(parallel.Global.Count == parallel.Global.Distinct(DiagnosticEqualityComparer.Instance).Count());
        var serialPool = await program.CreateCheckerPoolAsync(singleThreaded: true);
        Check(serialPool.Count == 1);
        var serial = await serialPool.GetDiagnosticsAsync();
        Check(new HashSet<Diagnostic>(serial.Semantic, DiagnosticEqualityComparer.Instance)
            .SetEquals(parallel.Semantic));
        Check(new HashSet<Diagnostic>(serial.Global, DiagnosticEqualityComparer.Instance).SetEquals(parallel.Global));
        var repeats = await Task.WhenAll(pool.GetDiagnosticsAsync().AsTask(), pool.GetDiagnosticsAsync().AsTask());
        Check(repeats.All(r => new HashSet<Diagnostic>(parallel.Semantic, DiagnosticEqualityComparer.Instance).SetEquals(r.Semantic)));
        var held = await pool.AcquireAsync();
        using var cancellation = new CancellationTokenSource();
        var waiting = pool.AcquireAsync(cancellation: cancellation.Token).AsTask();
        Check(!waiting.IsCompleted);
        cancellation.Cancel();
        try
        {
            await waiting;
            throw new InvalidOperationException("Expected cancelled pool acquisition");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        held.Dispose();
        held.Dispose();
        using (var available = await pool.AcquireAsync())
            Check(unique.Contains(available.Checker));
        try
        {
            _ = held.Checker;
            throw new InvalidOperationException("Expected disposed lease");
        }
        catch (ObjectDisposedException)
        {
            checks++;
        }
        try
        {
            await pool.AcquireAsync(new SourceFileNode());
            throw new InvalidOperationException("Expected source ownership rejection");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Check(nodes.All(n => n.Node.Parent == n.Parent && n.Node.Pos == n.Pos && n.Node.End == n.End && n.Node.Flags == n.Flags));
        Check(program.SourceFiles.All(f => f.Syntax.NodeCount > 0));
        options.SetRaw("checkers"u8, "2"u8);
        var configured = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
            new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        Check((await configured.CreateCheckerPoolAsync()).Count == 2);
        Check((await configured.CreateCheckerPoolAsync(singleThreaded: true)).Count == 1);
        options.SetRaw("checkers"u8, "999"u8);
        var clamped = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project"u8,
            new("/project/tsconfig.json"u8, options, files.Keys.ToArray(), [], [], []));
        Check((await clamped.CreateCheckerPoolAsync()).Count == files.Count);
        return checks;
    }
}

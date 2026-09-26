using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Programs;

namespace TypeScript.Compiler.Checking;

internal sealed class CheckerPool
{
    private readonly CompilerProgram program;
    private readonly Checker[] checkers;
    private readonly SemaphoreSlim[] gates;
    private readonly Dictionary<SourceFileNode, int> associations;

    private CheckerPool(CompilerProgram program, Checker[] checkers, int[] assignments)
    {
        this.program = program;
        this.checkers = checkers;
        gates = checkers.Select(_ => new SemaphoreSlim(1, 1)).ToArray();
        associations = program.SourceFiles.Select((file, i) => (file.Syntax, Index: assignments[i]))
            .ToDictionary(p => p.Syntax, p => p.Index);
    }

    internal int Count => checkers.Length;

    internal static async ValueTask<CheckerPool> CreateAsync(CompilerProgram program, bool singleThreaded,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        int count = singleThreaded ? 1 : (int?)program.Configuration.Options.Number("checkers") ?? 4;
        count = Math.Clamp(count, 1, Math.Max(1, Math.Min(program.SourceFiles.Count, 256)));
        var assignments = CheckerPartitions.ForProgram(program, count, cancellation);
        var checkers = new Checker[count];
        if (count == 1)
            checkers[0] = await program.CreateCheckerAsync(cancellation);
        else
            await Task.WhenAll(Enumerable.Range(0, count).Select(index => Task.Run(async () =>
                checkers[index] = await program.CreateCheckerAsync(cancellation), cancellation)));
        cancellation.ThrowIfCancellationRequested();
        return new(program, checkers, assignments);
    }

    internal async ValueTask<Lease> AcquireAsync(SourceFileNode? file = null, CancellationToken cancellation = default)
    {
        int index = file is null ? 0 : associations.TryGetValue(file, out int owner)
            ? owner : throw new ArgumentException("Source file does not belong to this checker pool", nameof(file));
        await gates[index].WaitAsync(cancellation);
        return new(checkers[index], gates[index]);
    }

    internal async ValueTask<(IReadOnlyList<Diagnostic> Semantic, IReadOnlyList<Diagnostic> Global)> GetDiagnosticsAsync(
        CancellationToken cancellation = default)
    {
        var semantic = new IReadOnlyList<Diagnostic>[program.SourceFiles.Count];
        var globals = new IReadOnlyList<Diagnostic>[Count];
        async Task CheckGroupAsync(int index)
        {
            await gates[index].WaitAsync(cancellation);
            using var lease = new Lease(checkers[index], gates[index]);
            // A partition may be empty. Its checker still contributes global diagnostics.
            var checker = checkers[index];
            foreach (var file in program.SourceFiles)
                if (associations[file.Syntax] == index && !checker.SkipProgramFile(file.Syntax))
                    await checker.CheckSourceFileAsync(file.Syntax, cancellation);
            for (int i = 0; i < program.SourceFiles.Count; i++)
                if (associations[program.SourceFiles[i].Syntax] == index)
                    semantic[i] = checker.DetailedDiagnosticsForProgramFile(program.SourceFiles[i].Syntax);
            globals[index] = checker.DetailedDiagnosticsForFile(null);
        }
        if (Count == 1)
            await CheckGroupAsync(0);
        else
            await Task.WhenAll(Enumerable.Range(0, Count).Select(index => Task.Run(() => CheckGroupAsync(index), cancellation)));
        cancellation.ThrowIfCancellationRequested();
        return (semantic.SelectMany(d => d).ToArray(), globals.SelectMany(d => d).Distinct(DiagnosticEqualityComparer.Instance).ToArray());
    }

    internal sealed class Lease(Checker checker, SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? gate = gate;
        internal Checker Checker => gate is null ? throw new ObjectDisposedException(nameof(Lease)) : checker;

        public void Dispose() => Interlocked.Exchange(ref gate, null)?.Release();
    }
}

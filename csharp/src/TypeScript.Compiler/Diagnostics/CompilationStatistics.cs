using System.Globalization;

namespace TypeScript.Compiler.Diagnostics;

internal sealed record CompilationStatistics
{
    internal long Files { get; init; }
    internal long Lines { get; init; }
    internal long Identifiers { get; init; }
    internal long Symbols { get; init; }
    internal long Types { get; init; }
    internal long Instantiations { get; init; }
    internal long ManagedBytes { get; init; }
    internal long AllocatedBytes { get; init; }
    internal double ConfigTime { get; init; }
    internal double ProgramTime { get; init; }
    internal double ParseTime { get; init; }
    internal double BindTime { get; init; }
    internal double CheckTime { get; init; }
    internal double EmitTime { get; init; }
    internal double TransformTime { get; init; }
    internal double PrintTime { get; init; }
    /// <summary>Per-pass wall time for the transform and checker probes, summed across files (diagnostics only).</summary>
    internal Dictionary<string, double> PassDurations { get; init; } = [];
    internal double BuildInfoTime { get; init; }
    internal double ChangesTime { get; init; }
    internal double TotalTime { get; init; }
    // Per-phase allocation attribution, summed across parallel work like the phase times above.
    // Only collected when diagnostics are enabled; used to target allocation-reduction work.
    internal long AllocatedProgram { get; init; }
    internal long AllocatedParse { get; init; }
    internal long AllocatedBind { get; init; }
    internal long AllocatedCheck { get; init; }
    internal long AllocatedEmit { get; init; }
    internal long AllocatedTransform { get; init; }
    internal long AllocatedPrint { get; init; }
    internal long[] CheckKindAllocations { get; init; } = [];
    internal long[] TransformKindAllocations { get; init; } = [];
    internal long[] ProbeAllocations { get; init; } = [];
    internal long[] ProbeDurations { get; init; } = [];
    internal long[] ProbeOwn { get; init; } = [];
    internal long[] ProbeChildren { get; init; } = [];
    internal long[] ProbeCounts { get; init; } = [];
    internal long CommentAdds { get; init; }
    internal long CommentSets { get; init; }
    internal long NodeDataCalls { get; init; }
    internal long LinkCreates { get; init; }
    internal int MaxRelationDepth { get; init; }
    internal long MaxRelationStackBytes { get; init; }
    internal long StackGuardCalls { get; init; }
    internal long StackGuardYields { get; init; }

    internal void Report(TextWriter output, int? projects = null, int built = 0, int timestamps = 0)
    {
        var rows = new List<(string Name, string Value)>();
        string prefix = projects is null ? "" : "Aggregate ";
        void Count(string name, long value) => rows.Add((prefix + name, value.ToString(CultureInfo.InvariantCulture)));
        void Time(string name, double value) => rows.Add((prefix + name, value.ToString("F3", CultureInfo.InvariantCulture) + "s"));
        if (projects is { } total)
        {
            rows.Add(("Projects in scope", total.ToString(CultureInfo.InvariantCulture)));
            rows.Add(("Projects built", built.ToString(CultureInfo.InvariantCulture)));
            rows.Add(("Timestamps only updates", timestamps.ToString(CultureInfo.InvariantCulture)));
        }
        Count("Files", Files); Count("Lines", Lines); Count("Identifiers", Identifiers); Count("Symbols", Symbols);
        Count("Types", Types); Count("Instantiations", Instantiations);
        Count("CLR managed bytes", ManagedBytes); Count("CLR allocated bytes", AllocatedBytes);
        Time("Config time", ConfigTime); Time("Program time", ProgramTime);
        Time("Parse time (summed)", ParseTime); Time("Bind time (summed)", BindTime); Time("Check time (summed)", CheckTime);
        Time("Emit time (summed)", EmitTime); Time("Transform time (summed)", TransformTime); Time("Print time (summed)", PrintTime); Time("BuildInfo read time", BuildInfoTime); Time("Changes compute time", ChangesTime); Time("Total time", TotalTime);
        Count("Alloc program (summed)", AllocatedProgram); Count("Alloc parse (summed)", AllocatedParse); Count("Alloc bind (summed)", AllocatedBind);
        Count("Alloc check (summed)", AllocatedCheck); Count("Alloc emit (summed)", AllocatedEmit);
        Count("Alloc transform (summed)", AllocatedTransform); Count("Alloc print (summed)", AllocatedPrint);
        if (CheckKindAllocations.Length != 0)
        {
            var top = CheckKindAllocations.Select((bytes, kind) => (bytes, kind)).Where(entry => entry.bytes > 0)
                .OrderByDescending(entry => entry.bytes).Take(12);
            foreach (var (bytes, kind) in top)
                Count($"Check alloc kind {Enum.GetName(typeof(Syntax.SyntaxKind), kind) ?? kind.ToString(CultureInfo.InvariantCulture)}", bytes);
        }
        if (TransformKindAllocations.Length != 0)
        {
            var top = TransformKindAllocations.Select((bytes, kind) => (bytes, kind)).Where(entry => entry.bytes > 0)
                .OrderByDescending(entry => entry.bytes).Take(12);
            foreach (var (bytes, kind) in top)
                Count($"Transform alloc kind {Enum.GetName(typeof(Syntax.SyntaxKind), kind) ?? kind.ToString(CultureInfo.InvariantCulture)}", bytes);
        }
        if (ProbeAllocations.Length != 0)
        {
            var top = ProbeAllocations.Select((bytes, id) => (bytes, id)).Where(entry => entry.bytes > 0)
                .OrderByDescending(entry => entry.bytes).Take(16);
            foreach (var (bytes, id) in top)
            {
                string name = (uint)id < AllocationProbes.Names.Length
                    ? AllocationProbes.Names[id] : id.ToString(CultureInfo.InvariantCulture);
                Count("Probe alloc " + name, bytes);
            }
        }
        if (ProbeOwn.Length != 0)
        {
            var top = ProbeOwn.Select((bytes, id) => (bytes, id)).Where(entry => entry.bytes > 0)
                .OrderByDescending(entry => entry.bytes).Take(32);
            foreach (var (bytes, id) in top)
            {
                string name = (uint)id < AllocationProbes.Names.Length
                    ? AllocationProbes.Names[id] : id.ToString(CultureInfo.InvariantCulture);
                Count("Own alloc " + name, bytes);
                Count("Own calls " + name, ProbeCounts[id]);
            }
        }
        if (ProbeDurations.Length != 0)
        {
            var top = ProbeDurations.Select((ticks, id) => (ticks, id)).Where(entry => entry.ticks > 0)
                .OrderByDescending(entry => entry.ticks).Take(16);
            foreach (var (ticks, id) in top)
            {
                string name = (uint)id < AllocationProbes.Names.Length
                    ? AllocationProbes.Names[id] : id.ToString(CultureInfo.InvariantCulture);
                Time("Probe time " + name, ticks / (double)System.Diagnostics.Stopwatch.Frequency);
            }
        }
        if (NodeDataCalls != 0)
        {
            Count("EmitContext.NodeData calls", NodeDataCalls);
            Count("EmitContext comment adds", CommentAdds);
            Count("EmitContext comment sets", CommentSets);
            Count("LinkStore creates", LinkCreates);
            Count("Max relation depth", MaxRelationDepth);
            Count("Max relation stack bytes", MaxRelationStackBytes);
            Count("Stack guard calls", StackGuardCalls);
            Count("Stack guard yields", StackGuardYields);
        }
        if (PassDurations.Count != 0)
            foreach (var (name, seconds) in PassDurations.OrderByDescending(pass => pass.Value).Take(20))
                Time(name, seconds);
        int nameWidth = rows.Max(row => row.Name.Length) + 1, valueWidth = rows.Max(row => row.Value.Length);
        foreach (var row in rows) output.WriteLine((row.Name + ":").PadRight(nameWidth) + " " + row.Value.PadLeft(valueWidth));
    }

    internal static CompilationStatistics Aggregate(IEnumerable<CompilationStatistics> values, double elapsed)
    {
        var entries = values.ToArray();
        return new()
        {
            Files = entries.Sum(value => value.Files), Lines = entries.Sum(value => value.Lines), Identifiers = entries.Sum(value => value.Identifiers),
            Symbols = entries.Sum(value => value.Symbols), Types = entries.Sum(value => value.Types), Instantiations = entries.Sum(value => value.Instantiations),
            ManagedBytes = entries.Sum(value => value.ManagedBytes), AllocatedBytes = entries.Sum(value => value.AllocatedBytes),
            ConfigTime = entries.Sum(value => value.ConfigTime), ProgramTime = entries.Sum(value => value.ProgramTime),
            ParseTime = entries.Sum(value => value.ParseTime), BindTime = entries.Sum(value => value.BindTime), CheckTime = entries.Sum(value => value.CheckTime),
            EmitTime = entries.Sum(value => value.EmitTime), BuildInfoTime = entries.Sum(value => value.BuildInfoTime), ChangesTime = entries.Sum(value => value.ChangesTime),
            TotalTime = elapsed,
            AllocatedProgram = entries.Sum(value => value.AllocatedProgram), AllocatedParse = entries.Sum(value => value.AllocatedParse),
            AllocatedBind = entries.Sum(value => value.AllocatedBind), AllocatedCheck = entries.Sum(value => value.AllocatedCheck),
            AllocatedEmit = entries.Sum(value => value.AllocatedEmit),
        };
    }
}

using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Programs;

namespace TypeScript.Compiler.Checking;

internal static class CheckerPartitions
{
    internal static int[] ForProgram(CompilerProgram program, int count, CancellationToken cancellation)
    {
        var files = program.SourceFiles;
        if (count == 1)
            return new int[files.Count];
        var indices = files.Select((file, i) => (file.Syntax, Index: i)).ToDictionary(p => p.Syntax, p => p.Index);
        var weights = new long[files.Count];
        var imports = new int[files.Count];
        var declarations = new bool[files.Count];
        var adjacency = Enumerable.Range(0, files.Count).Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < files.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var file = files[i];
            weights[i] = Math.Max((long)file.Syntax.NodeCount + file.Syntax.Source.Bytes.Length / 100, 1);
            imports[i] = file.Syntax.Imports.Count;
            declarations[i] = file.Syntax.IsDeclarationFile;
            foreach (var resolution in file.Resolutions.Where(r => !r.TypeReference)
                .DistinctBy(r => (r.Specifier, r.Mode)))
                if (resolution.Resolution.IsResolved && program.GetFile(resolution.Resolution.FileName) is { } target
                    && indices.TryGetValue(target.Syntax, out int adjacent) && adjacent != i)
                {
                    adjacency[i].Add(adjacent);
                    adjacency[adjacent].Add(i);
                }
        }
        return Assign(weights, imports, declarations, adjacency, count, cancellation);
    }

    // Retain the reference's calibrated FENNEL policy: syntax plus normalized import work,
    // source-first balancing for source-heavy programs, and stable program order otherwise.
    internal static int[] Assign(long[] weights, int[] imports, bool[] declarations, IReadOnlyList<int>[] adjacency,
        int count, CancellationToken cancellation = default)
    {
        long total = weights.Sum(), declarationWeight = 0;
        for (int i = 0; i < weights.Length; i++)
            if (declarations[i])
                declarationWeight += weights[i];
        bool sourceFirst = declarationWeight * count * 2 <= total;
        int sourceMultiplier = sourceFirst || count < 4 ? 1 : 4;
        int penaltyMultiplier = sourceFirst ? 12 : count >= 4 ? 16 : 1;
        weights = weights.Select((weight, i) => declarations[i] ? weight : weight * sourceMultiplier).ToArray();
        total = weights.Sum();
        long totalImports = imports.Sum(i => (long)i);
        long importWeight = totalImports == 0 ? 0 : Math.Max(total / totalImports, 1);
        for (int i = 0; i < weights.Length; i++)
            weights[i] += imports[i] * importWeight;
        total = weights.Sum();
        int[] order = Enumerable.Range(0, weights.Length).ToArray();
        if (sourceFirst)
            order = order.OrderBy(i => declarations[i]).ThenByDescending(i => weights[i]).ThenBy(i => i).ToArray();
        var assignments = Enumerable.Repeat(-1, weights.Length).ToArray();
        if (weights.Length == 0)
            return assignments;
        var loads = new long[count];
        long average = (total + count - 1) / count;
        long limit = Math.Max(weights.Max(), average + average / 100);
        long edges = adjacency.Sum(a => (long)a.Count);
        double alpha = penaltyMultiplier * (double)(edges / 2) * Math.Sqrt(count) / (total * Math.Sqrt(total));
        var neighbors = new int[count];
        foreach (int file in order)
        {
            cancellation.ThrowIfCancellationRequested();
            Array.Clear(neighbors);
            foreach (int adjacent in adjacency[file])
                if (assignments[adjacent] >= 0)
                    neighbors[assignments[adjacent]]++;
            int best = -1;
            double bestScore = double.NegativeInfinity;
            for (int checker = 0; checker < count; checker++)
            {
                if (loads[checker] + weights[file] > limit)
                    continue;
                double oldWeight = loads[checker], newWeight = loads[checker] + weights[file];
                double score = neighbors[checker] - alpha * (newWeight * Math.Sqrt(newWeight) - oldWeight * Math.Sqrt(oldWeight));
                if (score > bestScore || score == bestScore && (best < 0 || loads[checker] < loads[best]))
                {
                    best = checker;
                    bestScore = score;
                }
            }
            if (best < 0)
            {
                best = 0;
                for (int checker = 1; checker < count; checker++)
                    if (loads[checker] < loads[best])
                        best = checker;
            }
            assignments[file] = best;
            loads[best] += weights[file];
        }
        return assignments;
    }
}

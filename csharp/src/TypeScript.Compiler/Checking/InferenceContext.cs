using TypeScript.Compiler.Ast;

namespace TypeScript.Compiler.Checking;

internal sealed class InferenceInfo(Type parameter)
{
    internal Type Parameter { get; } = parameter;
    internal List<Type> Candidates { get; set; } = [];
    internal List<Type> ContraCandidates { get; set; } = [];
    internal Type? InferredType { get; set; }
    internal InferencePriority Priority { get; set; } = InferencePriority.MaxValue;
    internal bool TopLevel { get; set; } = true;
    internal bool IsFixed { get; set; }
    internal int ImpliedArity { get; set; } = -1;
    internal bool HasCandidates => Candidates.Count != 0 || ContraCandidates.Count != 0;

    internal InferenceInfo Clone() => new(Parameter)
    {
        Candidates = [.. Candidates],
        ContraCandidates = [.. ContraCandidates],
        InferredType = InferredType,
        Priority = Priority,
        TopLevel = TopLevel,
        IsFixed = IsFixed,
        ImpliedArity = ImpliedArity
    };

    internal void Restore(InferenceInfo previous)
    {
        Candidates = previous.Candidates;
        ContraCandidates = previous.ContraCandidates;
        InferredType = previous.InferredType;
        Priority = previous.Priority;
        TopLevel = previous.TopLevel;
        IsFixed = previous.IsFixed;
        ImpliedArity = previous.ImpliedArity;
    }
}

internal sealed class InferenceContext(InferenceInfo[] inferences, Signature? signature, InferenceFlags flags,
    Func<Type, Type, CancellationToken, ValueTask<Ternary>> compareTypes)
{
    internal InferenceInfo[] Inferences { get; } = inferences;
    internal Signature? Signature { get; } = signature;
    internal InferenceFlags Flags { get; set; } = flags;
    internal Func<Type, Type, CancellationToken, ValueTask<Ternary>> CompareTypes { get; } = compareTypes;
    internal TypeMapper Mapper { get; set; } = null!;
    internal TypeMapper NonFixingMapper { get; set; } = null!;
    internal TypeMapper? ReturnMapper { get; set; }
    internal TypeMapper? OuterReturnMapper { get; set; }
    internal IReadOnlyList<Type>? InferredTypeParameters { get; set; }
    internal List<(SyntaxNode Node, Type Type)> IntraExpressionSites { get; set; } = [];
    private int active;

    internal void ClearCached()
    {
        foreach (var inference in Inferences)
            if (!inference.IsFixed)
                inference.InferredType = null;
    }

    internal async ValueTask<T> RunAsync<T>(Func<ValueTask<T>> action, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        bool outer = active++ == 0;
        var originals = outer ? Inferences.ToArray() : null;
        var previous = outer ? Inferences.Select(i => i.Clone()).ToArray() : null;
        var sites = outer ? IntraExpressionSites.ToArray() : null;
        var oldFlags = Flags;
        var oldParameters = InferredTypeParameters;
        var oldReturnMapper = ReturnMapper;
        var oldOuterReturnMapper = OuterReturnMapper;
        try
        {
            var result = await action().ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            if (outer)
            {
                for (int i = 0; i < Inferences.Length; i++)
                {
                    Inferences[i] = originals![i];
                    Inferences[i].Restore(previous![i]);
                }
                IntraExpressionSites = [.. sites!];
                Flags = oldFlags;
                InferredTypeParameters = oldParameters;
                ReturnMapper = oldReturnMapper;
                OuterReturnMapper = oldOuterReturnMapper;
            }
            throw;
        }
        finally
        {
            active--;
        }
    }
}

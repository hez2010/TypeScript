namespace TypeScript.Compiler.Projects;

/// <summary>One request's cancellation and checker affinity, released when the request ends.</summary>
internal sealed class ProjectRequest : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;
    internal CancellationToken Cancellation { get; }
    internal CancellationToken Lifetime { get; }
    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    internal ProjectRequest(CancellationToken cancellation = default)
    {
        Cancellation = cancellation;
        Lifetime = lifetime.Token;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}

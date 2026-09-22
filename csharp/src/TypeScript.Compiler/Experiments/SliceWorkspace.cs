using TypeScript.Compiler.Storage;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Experiments;

public readonly record struct SliceNodeHandle<TStore>(SliceFile<TStore> Owner, NodeId Node) where TStore : INodeStore;

// Managed snapshot ownership: unchanged source owners are shared; a lease keeps
// its complete checked snapshot alive. No slot recycling or raw interior pointer
// crosses publication. Checking completes before concurrent queries begin.
public sealed class SliceWorkspace<TStore>(Func<TStore> createStore) : IDisposable where TStore : INodeStore
{
    private readonly object gate = new();
    private SliceProject<TStore>? current;
    private bool disposed;

    public void Update(IReadOnlyDictionary<string, byte[]> inputs, CancellationToken cancellation = default)
    {
        SliceProject<TStore>? previous = Volatile.Read(ref current);
        List<SliceFile<TStore>> files = [];
        foreach (var input in inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            if (previous?.Files.TryGetValue(input.Key, out var shared) == true && shared.Text.AsSpan().SequenceEqual(input.Value))
                files.Add(shared);
            else
            {
                byte[] owned = (byte[])input.Value.Clone();
                var file = new SliceFile<TStore>(input.Key, owned, createStore());
                files.Add(new SliceParser<TStore, Utf8Source>(file, new(owned), cancellation).Parse());
            }
        }
        var next = new SliceProject<TStore>(files, cancellation);
        next.Check();
        cancellation.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Volatile.Write(ref current, next);
        }
    }

    public SnapshotLease Acquire() => new(Volatile.Read(ref current) ?? throw new InvalidOperationException("No active snapshot"));

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Volatile.Write(ref current, null);
        }
    }

    public sealed class SnapshotLease(SliceProject<TStore> project) : IDisposable
    {
        private SliceProject<TStore>? owner = project;
        public SliceProject<TStore> Project => Volatile.Read(ref owner) ?? throw new ObjectDisposedException(nameof(SnapshotLease));

        public SliceNodeHandle<TStore> Root(string name) => new(Project.Files[name], Project.Files[name].Root);

        public NodeHeader Read(SliceNodeHandle<TStore> handle)
        {
            var snapshot = Project;
            if (!snapshot.Files.TryGetValue(handle.Owner.Name, out var file) || !ReferenceEquals(file, handle.Owner))
                throw new ArgumentException("Node owner is not in this snapshot", nameof(handle));
            return file.Store.Header(handle.Node);
        }

        public void Dispose() => Interlocked.Exchange(ref owner, null);
    }
}

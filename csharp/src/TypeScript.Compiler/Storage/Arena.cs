namespace TypeScript.Compiler.Storage;

// Owner identity stays with the handle; slots are never recycled. Chunk size is
// an experiment parameter, not a compiler tuning constant or a wire identity.
public readonly record struct Handle<T>(Arena<T>? Owner, int Index) where T : struct
{
    public bool IsNull => Owner is null;
}

public sealed class Arena<T>(int chunkSize = 256) where T : struct
{
    private readonly List<T[]> chunks = [];
    private readonly int chunkSize = chunkSize > 0 ? chunkSize : throw new ArgumentOutOfRangeException(nameof(chunkSize));
    public int Count { get; private set; }

    public Handle<T> Add(T value)
    {
        int index = Count;
        int next = checked(index + 1);
        if (index % chunkSize == 0)
            chunks.Add(new T[chunkSize]);
        chunks[index / chunkSize][index % chunkSize] = value;
        Count = next;
        return new(this, index);
    }

    public ref T this[Handle<T> handle]
    {
        get
        {
            if (!ReferenceEquals(handle.Owner, this) || (uint)handle.Index >= (uint)Count)
                throw new ArgumentException("Handle does not belong to this arena", nameof(handle));
            return ref chunks[handle.Index / chunkSize][handle.Index % chunkSize];
        }
    }
}

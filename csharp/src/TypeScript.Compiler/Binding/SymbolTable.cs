using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Binding;

// Published tables expose only read operations. Like the pinned Go runtime's
// small maps, scopes with at most eight names fit in one group. Larger scopes
// use the ordinary dictionary instead of extending the linear search.
internal sealed class SymbolTable : IReadOnlyDictionary<Utf8String, Symbol>, ICollection<KeyValuePair<Utf8String, Symbol>>
{
    private const int SmallCapacity = 8;

    private struct Entry
    {
        internal Utf8String Key;
        internal Symbol? Value;
    }

    private object? entries;
    private int count;

    internal SymbolTable(int capacity = 0)
    {
        if (capacity > SmallCapacity)
            entries = new Dictionary<Utf8String, Symbol>(capacity);
        else if (capacity != 0)
            entries = new Entry[capacity];
    }

    public int Count => entries is Dictionary<Utf8String, Symbol> table ? table.Count : count;

    public Symbol this[Utf8String key]
    {
        get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();
        internal set => GetValueRefOrAddDefault(key, out _) = value;
    }

    public bool TryGetValue(Utf8String key, [MaybeNullWhen(false)] out Symbol value)
    {
        if (entries is Dictionary<Utf8String, Symbol> table)
            return table.TryGetValue(key, out value);
        if (entries is Entry[] group)
            for (int i = 0; i < count; i++)
                if (group[i].Key == key)
                {
                    value = group[i].Value!;
                    return true;
                }
        value = null;
        return false;
    }

    public bool ContainsKey(Utf8String key) => TryGetValue(key, out _);

    internal ref Symbol? GetValueRefOrAddDefault(Utf8String key, out bool exists)
    {
        if (entries is Dictionary<Utf8String, Symbol> table)
            return ref CollectionsMarshal.GetValueRefOrAddDefault(table, key, out exists);
        var group = (Entry[]?)entries ?? [];
        for (int i = 0; i < count; i++)
            if (group[i].Key == key)
            {
                exists = true;
                return ref group[i].Value;
            }
        if (count == SmallCapacity)
        {
            table = new(count * 2);
            foreach (var entry in group)
                table.Add(entry.Key, entry.Value!);
            entries = table;
            return ref CollectionsMarshal.GetValueRefOrAddDefault(table, key, out exists);
        }
        if (count == group.Length)
        {
            Array.Resize(ref group, Math.Min(Math.Max(count * 2, 1), SmallCapacity));
            entries = group;
        }
        int index = count++;
        group[index].Key = key;
        exists = false;
        return ref group[index].Value;
    }

    internal bool TryAdd(Utf8String key, Symbol value)
    {
        ref var entry = ref GetValueRefOrAddDefault(key, out bool exists);
        if (exists)
            return false;
        entry = value;
        return true;
    }

    internal void Add(Utf8String key, Symbol value)
    {
        if (!TryAdd(key, value))
            throw new ArgumentException("An item with the same key has already been added.", nameof(key));
    }

    internal void Clear()
    {
        if (entries is Dictionary<Utf8String, Symbol> table)
            table.Clear();
        else if (entries is Entry[] group)
        {
            Array.Clear(group, 0, count);
            count = 0;
        }
    }

    internal IReadOnlyDictionary<Utf8String, Symbol> AsReadOnly() => this;

    public IEnumerable<Utf8String> Keys => entries is Dictionary<Utf8String, Symbol> table ? table.Keys : ReadKeys();
    public IEnumerable<Symbol> Values => entries is Dictionary<Utf8String, Symbol> table ? table.Values : ReadValues();

    private IEnumerable<Utf8String> ReadKeys()
    {
        if (entries is Entry[] group)
            for (int i = 0; i < count; i++)
                yield return group[i].Key;
        else if (entries is Dictionary<Utf8String, Symbol> table)
            foreach (var key in table.Keys)
                yield return key;
    }

    private IEnumerable<Symbol> ReadValues()
    {
        if (entries is Entry[] group)
            for (int i = 0; i < count; i++)
                yield return group[i].Value!;
        else if (entries is Dictionary<Utf8String, Symbol> table)
            foreach (var value in table.Values)
                yield return value;
    }

    public IEnumerator<KeyValuePair<Utf8String, Symbol>> GetEnumerator() => entries is Dictionary<Utf8String, Symbol> table
        ? table.GetEnumerator() : ReadSmall().GetEnumerator();

    private IEnumerable<KeyValuePair<Utf8String, Symbol>> ReadSmall()
    {
        if (entries is Entry[] group)
            for (int i = 0; i < count; i++)
                yield return new(group[i].Key, group[i].Value!);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    bool ICollection<KeyValuePair<Utf8String, Symbol>>.IsReadOnly => true;
    void ICollection<KeyValuePair<Utf8String, Symbol>>.Add(KeyValuePair<Utf8String, Symbol> item) => throw new NotSupportedException();
    bool ICollection<KeyValuePair<Utf8String, Symbol>>.Remove(KeyValuePair<Utf8String, Symbol> item) => throw new NotSupportedException();
    void ICollection<KeyValuePair<Utf8String, Symbol>>.Clear() => throw new NotSupportedException();
    bool ICollection<KeyValuePair<Utf8String, Symbol>>.Contains(KeyValuePair<Utf8String, Symbol> item) =>
        TryGetValue(item.Key, out var value) && value == item.Value;

    void ICollection<KeyValuePair<Utf8String, Symbol>>.CopyTo(KeyValuePair<Utf8String, Symbol>[] array, int arrayIndex)
    {
        if (entries is Dictionary<Utf8String, Symbol> table)
        {
            ((ICollection<KeyValuePair<Utf8String, Symbol>>)table).CopyTo(array, arrayIndex);
            return;
        }
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        if (arrayIndex > array.Length || Count > array.Length - arrayIndex)
            throw new ArgumentException("The destination array is too small.", nameof(array));
        if (entries is Entry[] group)
            for (int i = 0; i < count; i++)
                array[arrayIndex++] = new(group[i].Key, group[i].Value!);
    }
}

using System.Buffers;
using System.Text;
using TypeScript.Compiler.Semantics;

namespace TypeScript.Compiler.LanguageServices;

internal sealed class AutoImportIndex
{
    private readonly List<AutoImportExport> entries = [];
    private readonly Dictionary<int, List<int>> index = [];

    internal void Add(AutoImportExport export)
    {
        if (export.Name.IsEmpty) throw new ArgumentException("Cannot index entry with empty name", nameof(export));
        int entry = entries.Count;
        entries.Add(export);
        var name = export.Name;
        HashSet<int> seen = [];
        foreach (int offset in WordIndices(name))
        {
            int point = Decode(name[offset..], out _);
            if (point == 0xfffd) continue;
            int key = offset == 0 ? GoUnicode.Upper(point) : GoUnicode.Lower(point);
            if (!seen.Add(key)) continue;
            if (!index.TryGetValue(key, out var bucket)) index[key] = bucket = [];
            bucket.Add(entry);
        }
    }

    internal static IEnumerable<int> WordIndices(Utf8String name)
    {
        int previous = 0;
        for (int offset = 0; offset < name.Length;)
        {
            int point = Decode(name[offset..], out int width);
            if (offset == 0) yield return 0;
            else
            {
                if (point == '_')
                {
                    if (offset + 1 < name.Length && name[offset + 1] != '_') yield return offset + 1;
                }
                else if (GoUnicode.IsUpper(point) && (GoUnicode.IsLower(previous)
                    || offset + 1 < name.Length && GoUnicode.IsLower(Decode(name[(offset + 1)..], out _)))) yield return offset;
            }
            previous = point;
            offset += width;
        }
    }

    internal IEnumerable<AutoImportExport> Find(Utf8String name, bool caseSensitive)
    {
        if (name.IsEmpty || Decode(name, out _) is 0xfffd) return [];
        var candidates = index.GetValueOrDefault(GoUnicode.Upper(Decode(name, out _))) ?? [];
        int[] runes = caseSensitive ? [] : GoUnicode.Runes(name);
        return candidates.Select(i => entries[i]).Where(export => caseSensitive ? export.Name == name
            : GoUnicode.EqualFold(GoUnicode.Runes(export.Name), runes));
    }

    internal static AutoImportIndex? Clone(AutoImportIndex? source, Func<AutoImportExport, bool> filter)
    {
        if (source is null) return null;
        var clone = new AutoImportIndex();
        foreach (var entry in source.entries) if (filter(entry)) clone.Add(entry);
        return clone;
    }

    internal IEnumerable<AutoImportExport> Search(Utf8String prefix)
    {
        if (prefix.IsEmpty) return entries;
        prefix = GoUnicode.LowerText(prefix);
        int first = Decode(prefix, out _);
        if (first == 0xfffd) return [];
        int upper = GoUnicode.Upper(first), lower = GoUnicode.Lower(first);
        IEnumerable<int> candidates = index.GetValueOrDefault(upper) ?? [];
        if (upper != lower) candidates = candidates.Concat(index.GetValueOrDefault(lower) ?? []);
        return candidates.Select(i => entries[i]).Where(export => Contains(export.Name, prefix));
    }

    private static bool Contains(Utf8String name, Utf8String prefix)
    {
        int offset = 0;
        foreach (int point in GoUnicode.Runes(GoUnicode.LowerText(name)))
            if (offset < prefix.Length && point == Decode(prefix[offset..], out int width)) offset += width;
        return offset == prefix.Length;
    }

    private static int Decode(Utf8String text, out int width)
    {
        if (Rune.DecodeFromUtf8(text.Span, out var rune, out width) == OperationStatus.Done) return rune.Value;
        width = 1;
        return 0xfffd;
    }
}

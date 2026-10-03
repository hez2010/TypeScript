using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Projects;

public enum FileChangeKind { Open, Close, Change, Save, WatchCreate, WatchChange, WatchDelete }

public sealed record FileChange(FileChangeKind Kind, Utf8String FileName, int Version = 0,
    Utf8String Content = default, ScriptKind ScriptKind = ScriptKind.Unknown, IReadOnlyList<DocumentEdit>? Edits = null);

public sealed class FileChangeSummary
{
    public Utf8String Opened { get; internal set; }
    public Utf8String Reopened { get; internal set; }
    public HashSet<Utf8String> Closed { get; }
    public HashSet<Utf8String> Changed { get; }
    public HashSet<Utf8String> Created { get; }
    public HashSet<Utf8String> Deleted { get; }
    public bool IncludesWatchChangeOutsideNodeModules { get; internal set; }
    public bool InvalidateAll { get; set; }
    internal bool InvalidateFileCache { get; set; }
    internal bool InvalidateNodeModules { get; set; }
    public bool IsEmpty => !InvalidateAll && Opened.IsEmpty && Reopened.IsEmpty
        && Closed.Count == 0 && Changed.Count == 0 && Created.Count == 0 && Deleted.Count == 0;
    public bool HasExcessiveWatchEvents => InvalidateAll || Changed.Count + Created.Count + Deleted.Count > 1000;

    public FileChangeSummary(bool caseSensitive = true)
    {
        var comparer = caseSensitive ? Utf8StringComparer.Ordinal : Utf8StringComparer.OrdinalIgnoreCase;
        Closed = new(comparer); Changed = new(comparer); Created = new(comparer); Deleted = new(comparer);
    }

    public void Merge(FileChangeSummary other)
    {
        InvalidateAll |= other.InvalidateAll;
        Changed.UnionWith(other.Changed); Created.UnionWith(other.Created); Deleted.UnionWith(other.Deleted);
        IncludesWatchChangeOutsideNodeModules |= other.IncludesWatchChangeOutsideNodeModules;
    }
}

using System.Collections.Concurrent;
using System.IO.Hashing;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Projects;

/// <summary>Shares syntax across programs without making the cache an owner of retired source graphs.</summary>
internal sealed class ProjectParseCache
{
    private readonly record struct Key(ParseOptions Options, UInt128 Hash, int Length);
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal WeakReference<SourceFileNode>? Source;
        internal bool Retired;
    }
    private readonly ConcurrentDictionary<Key, Entry> entries = new();
    private int requests;

    internal async ValueTask<SourceFileNode> ParseAsync(ParseOptions options, SourceText source, CancellationToken cancellation)
    {
        if (options.ScriptKind == ScriptKind.Unknown)
        {
            var kind = DocumentSnapshot.InferKind(options.FileName);
            options = options with { ScriptKind = kind == ScriptKind.Unknown ? ScriptKind.TS : kind };
        }
        var key = new Key(options, XxHash128.HashToUInt128(source.Bytes.Span), source.Length);
        if ((Interlocked.Increment(ref requests) & 63) == 0) Trim();
        while (true)
        {
            var entry = entries.GetOrAdd(key, static _ => new());
            await entry.Gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (entry.Retired) continue;
                if (entry.Source?.TryGetTarget(out var cached) == true && cached.Source.Bytes.Span.SequenceEqual(source.Bytes.Span)) return cached;
                var parsed = await Parser.ParseSourceFileAsync(options, source, cancellation).ConfigureAwait(false);
                await Binder.BindAsync(parsed, cancellation).ConfigureAwait(false);
                entry.Source = new(parsed);
                return parsed;
            }
            finally { entry.Gate.Release(); }
        }
    }

    internal void Trim()
    {
        foreach (var (key, entry) in entries)
        {
            if (!entry.Gate.Wait(0)) continue;
            try
            {
                if (entry.Source?.TryGetTarget(out _) == true) continue;
                entry.Retired = true;
                entries.TryRemove(new KeyValuePair<Key, Entry>(key, entry));
            }
            finally { entry.Gate.Release(); }
        }
    }
}

namespace TypeScript.Compiler.LanguageServer;

// The registration worker serializes updates; shared globs retain their first client's registration ID.
internal sealed class LspWatchRegistry(Func<Utf8String, LspWatchPattern, CancellationToken, ValueTask> register,
    Func<Utf8String, CancellationToken, ValueTask> unregister, Action<Exception>? onError = null)
{
    private sealed class Registration(Utf8String id) { internal readonly Utf8String Id = id; internal int References = 1; }
    private sealed record Group(Utf8String Id, LspWatchPattern[] Patterns, bool Pending);
    private readonly Dictionary<(Utf8String Pattern, int Kind), Registration> registrations = [];
    private readonly Dictionary<Utf8String, Group> groups = [];
    private ulong nextId;

    internal async ValueTask UpdateAsync(IReadOnlyList<ProjectWatchGroup> plan, CancellationToken cancellation)
    {
        var retained = plan.Select(group => group.Name).ToHashSet();
        foreach (var update in plan)
        {
            cancellation.ThrowIfCancellationRequested();
            var previous = groups.GetValueOrDefault(update.Name);
            bool same = previous is not null && previous.Patterns.SequenceEqual(update.Patterns);
            if (same && !previous!.Pending) continue;
            var id = same ? previous!.Id : update.Name + " watcher "u8 + Utf8String.Format(++nextId);
            List<(LspWatchPattern Pattern, Utf8String Id)> added = [];
            List<LspWatchPattern> acquired = [];
            bool failed = false;
            for (int index = 0; index < update.Patterns.Length; index++)
            {
                var pattern = update.Patterns[index];
                var key = (pattern.Key, pattern.Kind);
                acquired.Add(pattern);
                if (registrations.TryGetValue(key, out var existing)) existing.References++;
                else
                {
                    var registrationId = id + "."u8 + Utf8String.Format(index);
                    registrations.Add(key, new(registrationId));
                    added.Add((pattern, registrationId));
                }
            }
            List<Utf8String> installed = [];
            foreach (var addition in added)
                if (await CallAsync(token => register(addition.Id, addition.Pattern, token), cancellation)) installed.Add(addition.Id);
                else failed = true;
            if (failed)
            {
                // Roll back shared acquisitions too, and close successful partial registrations.
                // Otherwise a retry leaks references or collides with a still-live native watcher.
                foreach (var pattern in acquired) Release(pattern);
                foreach (var installedId in installed) await CallAsync(token => unregister(installedId, token), cancellation);
            }
            if (previous is { Pending: false }) await ReleaseAsync(previous, cancellation);
            groups[update.Name] = new(id, update.Patterns, failed);
        }
        foreach (var name in groups.Keys.Where(name => !retained.Contains(name)).ToArray())
        {
            if (groups.Remove(name, out var group) && !group.Pending) await ReleaseAsync(group, cancellation);
        }
    }

    private Utf8String Release(LspWatchPattern pattern)
    {
        var key = (pattern.Key, pattern.Kind);
        if (!registrations.TryGetValue(key, out var registration) || --registration.References != 0) return default;
        registrations.Remove(key); return registration.Id;
    }

    private async ValueTask ReleaseAsync(Group group, CancellationToken cancellation)
    {
        foreach (var pattern in group.Patterns)
            if (Release(pattern) is { IsEmpty: false } id) await CallAsync(token => unregister(id, token), cancellation);
    }

    private async ValueTask<bool> CallAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try { await action(timeout.Token).ConfigureAwait(false); return true; }
        catch (Exception error) when (error is IOException or Protocol.RpcException or OperationCanceledException or InvalidOperationException or UnauthorizedAccessException)
        { onError?.Invoke(error); return false; }
    }
}

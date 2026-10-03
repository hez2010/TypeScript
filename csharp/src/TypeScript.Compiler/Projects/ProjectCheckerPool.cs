using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Programs;

namespace TypeScript.Compiler.Projects;

internal enum ProjectCheckerLifetime { Diagnostics, Query, Api }

/// <summary>Editor queries may expire; API checkers retain identity for their program's lifetime.</summary>
internal sealed class ProjectCheckerPool : IDisposable
{
    private sealed class Slot
    {
        internal Checker? Checker;
        internal bool Held;
        internal ProjectRequest? Request;
        internal DateTimeOffset LastReleased;
    }

    private readonly object sync = new();
    private readonly CompilerProgram program;
    private readonly Slot[] slots;
    private readonly Slot persistent = new();
    private readonly SemaphoreSlim diagnostics = new(1, 1), api = new(1, 1), queries;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Dictionary<SourceFileNode, int> fileAssociations = [];
    private readonly Dictionary<ProjectRequest, (int Index, CancellationTokenRegistration Registration)> requests = [];
    private readonly TimeProvider time;
    private readonly TimeSpan idleTimeout;
    private readonly ITimer cleanup;
    private IReadOnlyList<Diagnostic> globals = [];
    private bool globalsChanged, discarded, disposed;

    internal ProjectCheckerPool(CompilerProgram program, int maxCheckers = 4, TimeSpan idleTimeout = default,
        TimeProvider? time = null)
    {
        this.program = program;
        this.time = time ?? TimeProvider.System;
        this.idleTimeout = idleTimeout > TimeSpan.Zero ? idleTimeout : TimeSpan.FromSeconds(30);
        slots = Enumerable.Range(0, maxCheckers <= 0 ? 4 : Math.Max(2, maxCheckers)).Select(_ => new Slot()).ToArray();
        queries = new(slots.Length - 1, slots.Length - 1);
        cleanup = this.time.CreateTimer(_ => ExpireIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal async ValueTask<Lease> AcquireAsync(ProjectCheckerLifetime lifetime, ProjectRequest request,
        SourceFileNode? file = null)
    {
        ObjectDisposedException.ThrowIf(request.IsDisposed, request);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation, shutdown.Token);
        var cancellation = linked.Token;
        cancellation.ThrowIfCancellationRequested();
        bool persistentRequest = lifetime == ProjectCheckerLifetime.Api;
        var gate = persistentRequest ? api : lifetime == ProjectCheckerLifetime.Diagnostics ? diagnostics : queries;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!persistentRequest && requests.TryGetValue(request, out var association))
            {
                var slot = slots[association.Index];
                if ((lifetime == ProjectCheckerLifetime.Diagnostics ? association.Index != 0 : association.Index == 0) || slot.Checker is null)
                {
                    association.Registration.Unregister();
                    requests.Remove(request);
                }
                else if (slot is { Held: true, Checker: { } held } && slot.Request == request) return new(held, null);
            }
        }
        await gate.WaitAsync(cancellation);
        Slot? selected = null;
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                ObjectDisposedException.ThrowIf(request.IsDisposed, request);
                int index = 0;
                if (persistentRequest) selected = persistent;
                else
                {
                    if (lifetime == ProjectCheckerLifetime.Query)
                    {
                        if (requests.TryGetValue(request, out var association) && association.Index > 0
                            && !slots[association.Index].Held && slots[association.Index].Checker is not null)
                            index = association.Index;
                        else if (file is not null && fileAssociations.TryGetValue(file, out int preferred)
                            && !slots[preferred].Held && slots[preferred].Checker is not null)
                            index = preferred;
                        else
                        {
                            index = Array.FindIndex(slots, 1, slot => !slot.Held && slot.Checker is not null);
                            if (index < 0) index = Array.FindIndex(slots, 1, slot => !slot.Held);
                        }
                    }
                    selected = slots[index];
                    if (!requests.ContainsKey(request))
                    {
                        var registration = request.Lifetime.Register(() => ForgetRequest(request));
                        if (request.IsDisposed) registration.Unregister();
                        else requests.Add(request, (index, registration));
                    }
                    if (file is not null && index > 0) fileAssociations[file] = index;
                }
                selected.Held = true;
                selected.Request = request;
            }
            Checker checker = selected.Checker ?? await program.CreateCheckerAsync(cancellation);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                ObjectDisposedException.ThrowIf(request.IsDisposed, request);
                selected.Checker = checker;
            }
            return new(checker, () => Release(selected, gate, persistentRequest));
        }
        catch
        {
            lock (sync)
            {
                if (selected is not null)
                {
                    selected.Held = false;
                    selected.Request = null;
                    if (selected.Checker is null) ClearAssociations(selected);
                }
            }
            gate.Release();
            throw;
        }
    }

    private void ForgetRequest(ProjectRequest request)
    {
        lock (sync) requests.Remove(request);
    }

    private void Release(Slot slot, SemaphoreSlim gate, bool persistentRequest)
    {
        lock (sync)
        {
            slot.Held = false;
            slot.Request = null;
            if (disposed || slot.Checker!.WasCanceled)
            {
                slot.Checker = null;
                ClearAssociations(slot);
            }
            else if (!persistentRequest)
            {
                var merged = DiagnosticCollection.SortAndDeduplicate(globals.Concat(slot.Checker.DetailedDiagnosticsForFile(null)));
                globalsChanged |= merged.Length != globals.Count;
                globals = merged;
                slot.LastReleased = time.GetUtcNow();
                ScheduleCleanup();
            }
        }
        gate.Release();
    }

    private void ClearAssociations(Slot slot)
    {
        int index = Array.IndexOf(slots, slot);
        foreach (var file in fileAssociations.Where(pair => pair.Value == index).Select(pair => pair.Key).ToArray())
            fileAssociations.Remove(file);
        foreach (var request in requests.Where(pair => pair.Value.Index == index).Select(pair => pair.Key).ToArray())
        {
            // Unregister does not wait for a callback holding this pool's lock.
            requests[request].Registration.Unregister();
            requests.Remove(request);
        }
    }

    private void ScheduleCleanup()
    {
        if (discarded || disposed) return;
        DateTimeOffset deadline = DateTimeOffset.MaxValue;
        foreach (var slot in slots)
            if (slot.Checker is not null && !slot.Held && slot.LastReleased != default)
                if (slot.LastReleased + idleTimeout < deadline) deadline = slot.LastReleased + idleTimeout;
        cleanup.Change(deadline == DateTimeOffset.MaxValue ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromTicks(Math.Max(1, (deadline - time.GetUtcNow()).Ticks)), Timeout.InfiniteTimeSpan);
    }

    internal void ExpireIdle()
    {
        lock (sync)
        {
            if (discarded || disposed) return;
            var now = time.GetUtcNow();
            foreach (var slot in slots)
                if (!slot.Held && slot.Checker is not null && slot.LastReleased != default
                    && now - slot.LastReleased >= idleTimeout)
                {
                    slot.Checker = null;
                    ClearAssociations(slot);
                }
            ScheduleCleanup();
        }
    }

    internal IReadOnlyList<Diagnostic> GlobalDiagnostics { get { lock (sync) return globals.ToArray(); } }
    internal bool TakeNewGlobalDiagnostics()
    {
        lock (sync) { bool changed = globalsChanged; globalsChanged = false; return changed; }
    }

    internal void Discard()
    {
        lock (sync)
        {
            discarded = true;
            cleanup.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            cleanup.Dispose();
            foreach (var registration in requests.Values) registration.Registration.Unregister();
            requests.Clear();
            fileAssociations.Clear();
            foreach (var slot in slots.Append(persistent)) if (!slot.Held) slot.Checker = null;
        }
        shutdown.Cancel();
    }

    internal sealed class Lease(Checker checker, Action? release) : IDisposable
    {
        private Action? release = release;
        private int disposed;
        internal Checker Checker => Volatile.Read(ref disposed) == 0 ? checker : throw new ObjectDisposedException(nameof(Lease));
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Exchange(ref release, null)?.Invoke();
        }
    }
}

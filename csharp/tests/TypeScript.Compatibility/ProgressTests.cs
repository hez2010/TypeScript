using System.Threading.Channels;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.LanguageServer;

namespace TypeScript.Compatibility;

internal static class ProgressTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var calls = Channel.CreateUnbounded<LoadingProgressNotification>();
        ValueTask Send(LoadingProgressNotification value) { calls.Writer.TryWrite(value); return default; }
        async Task Expect(Utf8String kind, Utf8String token, Utf8String message = default)
        {
            var actual = await calls.Reader.ReadAsync(timeout.Token);
            Check(actual.Kind == kind && actual.Token == token && actual.Message == message, $"Progress {kind}/{token}/{message}: {actual}");
            if (kind == "begin"u8) Check(actual.Title == "Loading"u8, "A new indicator has the localized loading title");
        }
        await using (var progress = new ProjectLoadingProgress(Send, default, TimeSpan.Zero, TimeProvider.System, timeout.Token))
        {
            await progress.EnqueueAsync(Messages.Project_0, "absent"u8, true);
            await progress.EnqueueAsync(Messages.Project_0, "A"u8, false);
            await Expect("create"u8, "tsgo-loading-1"u8);
            await Expect("begin"u8, "tsgo-loading-1"u8, "Project 'A'"u8);
            await progress.EnqueueAsync(Messages.Project_0, "B"u8, false);
            await progress.EnqueueAsync(Messages.Project_0, "A"u8, false);
            await progress.EnqueueAsync(Messages.Project_0, "A"u8, true);
            await progress.EnqueueAsync(Messages.Project_0, "absent"u8, true);
            await progress.EnqueueAsync(Messages.Project_0, "A"u8, true);
            await progress.EnqueueAsync(Messages.Project_0, "B"u8, true);
            foreach (Utf8String message in new Utf8String[] { "Project 'B'"u8, "Project 'A'"u8, "Project 'A'"u8, "Project 'A'"u8, "Project 'B'"u8 })
                await Expect("report"u8, "tsgo-loading-1"u8, message);
            await Expect("end"u8, "tsgo-loading-1"u8);
            await progress.EnqueueAsync(Messages.Installing_types_for_0, "A"u8, false);
            await progress.EnqueueAsync(Messages.Installing_types_for_0, "A"u8, true);
            await Expect("create"u8, "tsgo-loading-2"u8);
            await Expect("begin"u8, "tsgo-loading-2"u8, "Installing types for 'A'"u8);
            await Expect("end"u8, "tsgo-loading-2"u8);
        }

        var clock = new ManualTime();
        await using (var progress = new ProjectLoadingProgress(Send, default, TimeSpan.FromMilliseconds(500), clock, timeout.Token))
        {
            await progress.EnqueueAsync(Messages.Project_0, "fast"u8, false);
            var timer = await clock.Timers.Reader.ReadAsync(timeout.Token);
            Check(timer.Due == TimeSpan.FromMilliseconds(500) && !calls.Reader.TryRead(out _), "Delay is armed without displaying progress");
            await progress.EnqueueAsync(Messages.Project_0, "fast"u8, true);
            await timer.Stopped.Task.WaitAsync(timeout.Token);
            timer.Fire();
            Check(!calls.Reader.TryRead(out _), "Finishing before the delay suppresses all notifications");
            await progress.EnqueueAsync(Messages.Project_0, "slow"u8, false);
            timer = await clock.Timers.Reader.ReadAsync(timeout.Token); timer.Fire();
            await Expect("create"u8, "tsgo-loading-2"u8);
            await Expect("begin"u8, "tsgo-loading-2"u8, "Project 'slow'"u8);
            await progress.EnqueueAsync(Messages.Project_0, "later"u8, false);
            await Expect("report"u8, "tsgo-loading-2"u8, "Project 'later'"u8);
            await progress.EnqueueAsync(Messages.Project_0, "slow"u8, true);
            await Expect("report"u8, "tsgo-loading-2"u8, "Project 'later'"u8);
            await progress.EnqueueAsync(Messages.Project_0, "later"u8, true);
            await Expect("end"u8, "tsgo-loading-2"u8);
            await progress.EnqueueAsync(Messages.Project_0, "shutdown"u8, false);
            timer = await clock.Timers.Reader.ReadAsync(timeout.Token);
            await progress.DisposeAsync(); await progress.DisposeAsync(); timer.Fire();
            await progress.EnqueueAsync(Messages.Project_0, "after shutdown"u8, false);
            Check(timer.Stopped.Task.IsCompleted && !calls.Reader.TryRead(out _), "Shutdown cancels the timer and drops subsequent events");
        }

        using (var cancelled = new CancellationTokenSource())
        {
            var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var progress = new ProjectLoadingProgress(async _ =>
            {
                sending.TrySetResult(); await Task.Delay(Timeout.Infinite, cancelled.Token);
            }, default, TimeSpan.Zero, TimeProvider.System, cancelled.Token);
            await progress.EnqueueAsync(Messages.Project_0, "blocked transport"u8, false);
            await sending.Task.WaitAsync(timeout.Token);
            var writers = Enumerable.Range(0, 128).Select(i => progress.EnqueueAsync(Messages.Project_0, "queued"u8, i % 2 == 0).AsTask()).ToArray();
            Check(writers.Any(task => !task.IsCompleted), "The progress queue applies bounded backpressure");
            cancelled.Cancel(); await Task.WhenAll(writers).WaitAsync(timeout.Token); await progress.DisposeAsync();
            Check(writers.All(task => task.IsCompletedSuccessfully), "Cancellation releases writers blocked by a full queue");
        }
        Check(!calls.Reader.TryRead(out _), "No surplus progress notifications remain");
        return checks;
    }

    private sealed class ManualTime : TimeProvider
    {
        internal readonly Channel<Timer> Timers = Channel.CreateUnbounded<Timer>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(callback, state, dueTime); Timers.Writer.TryWrite(timer); return timer;
        }
        internal sealed class Timer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            internal TimeSpan Due => due;
            internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal void Fire() { if (!Stopped.Task.IsCompleted) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Stopped.TrySetResult();
            public ValueTask DisposeAsync() { Dispose(); return default; }
        }
    }
}

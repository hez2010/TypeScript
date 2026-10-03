using System.Collections.Generic;
using System.Threading.Channels;
using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.LanguageServer;

internal readonly record struct LoadingProgressNotification(Utf8String Token, Utf8String Kind, Utf8String Title = default, Utf8String Message = default);

/// <summary>Serializes overlapping project and typings work into one delayed editor progress indicator.</summary>
internal sealed class ProjectLoadingProgress : IAsyncDisposable
{
    private readonly record struct Event(DiagnosticMessage Message, Utf8String Argument, bool Finish);
    private readonly Channel<Event> events = Channel.CreateBounded<Event>(new BoundedChannelOptions(64) { SingleReader = true });
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken cancellation;
    private readonly Task worker;
    private readonly object sync = new();
    private Task? stopping;

    internal ProjectLoadingProgress(Func<LoadingProgressNotification, ValueTask> send, Utf8String locale, TimeSpan delay,
        TimeProvider time, CancellationToken cancellation = default)
    {
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        this.cancellation = lifetime.Token;
        worker = RunAsync(send, locale, delay, time, lifetime.Token);
    }

    internal async ValueTask EnqueueAsync(DiagnosticMessage message, Utf8String argument, bool finish)
    {
        try { await events.Writer.WriteAsync(new(message, argument, finish), cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (ChannelClosedException) { }
    }

    private async Task RunAsync(Func<LoadingProgressNotification, ValueTask> send, Utf8String locale, TimeSpan delay,
        TimeProvider time, CancellationToken cancellation)
    {
        var loading = new OrderedDictionary<Utf8String, int>();
        Utf8String token = default;
        int sequence = 0;
        bool begun = false;
        Task? timer = null;
        Task<bool>? readable = null;
        CancellationTokenSource? delayLifetime = null;
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                readable ??= events.Reader.WaitToReadAsync(cancellation).AsTask();
                if (timer is not null && await Task.WhenAny(readable, timer).ConfigureAwait(false) == timer)
                {
                    await timer.ConfigureAwait(false); StopDelay();
                    if (!token.IsEmpty && loading.Count != 0)
                    {
                        await send(new(token, "create"u8)).ConfigureAwait(false);
                        await BeginOrReport(loading.GetAt(0).Key).ConfigureAwait(false);
                    }
                    continue;
                }
                if (!await readable.ConfigureAwait(false)) return;
                readable = null;
                if (!events.Reader.TryRead(out var next)) continue;
                var text = next.Message.Format(locale, next.Argument);
                if (!next.Finish)
                {
                    loading[text] = loading.GetValueOrDefault(text) + 1;
                    if (token.IsEmpty)
                    {
                        token = (Utf8String)"tsgo-loading-"u8 + ++sequence;
                        begun = false;
                        if (delay <= TimeSpan.Zero) await send(new(token, "create"u8)).ConfigureAwait(false);
                        else
                        {
                            delayLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                            timer = Task.Delay(delay, time, delayLifetime.Token);
                        }
                    }
                    if (timer is null) await BeginOrReport(text).ConfigureAwait(false);
                }
                else
                {
                    int count = loading.GetValueOrDefault(text);
                    if (count <= 1) loading.Remove(text); else loading[text] = count - 1;
                    if (token.IsEmpty) continue;
                    if (loading.Count == 0)
                    {
                        if (begun) await send(new(token, "end"u8)).ConfigureAwait(false);
                        StopDelay(); token = default;
                    }
                    else if (timer is null) await send(new(token, "report"u8, Message: loading.GetAt(0).Key)).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (IOException) { }
        finally { StopDelay(); events.Writer.TryComplete(); }

        async ValueTask BeginOrReport(Utf8String text)
        {
            await send(new(token, begun ? "report"u8 : "begin"u8, begun ? default : Messages.Loading.Format(locale), text)).ConfigureAwait(false);
            begun = true;
        }
        void StopDelay()
        {
            delayLifetime?.Cancel(); delayLifetime?.Dispose(); delayLifetime = null; timer = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync) return new(stopping ??= StopAsync());
    }

    private async Task StopAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        events.Writer.TryComplete();
        try { await worker.ConfigureAwait(false); }
        finally { lifetime.Dispose(); }
    }
}

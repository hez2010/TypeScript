using System.Diagnostics;
using System.Text.Json;
using TypeScript.Compiler.Configuration;

namespace TypeScript.Compiler.Protocol;

public sealed record RpcConnectionOptions
{
    public bool UseMessagePack { get; init; }
    public bool CollectTiming { get; init; }
    public bool LeaveOpen { get; init; }
    public bool PreserveMessageOrder { get; init; }
    internal Action<JsonElement>? ValidateCancellation { get; init; }
    public int MaximumPayloadLength { get; init; } = Array.MaxLength;
}

/// <summary>Bidirectional framed RPC. MessagePack calls are serialized; JSON-RPC calls have independent identities.</summary>
public sealed class RpcConnection : IAsyncDisposable
{
    private readonly Stream input, output;
    private readonly IRpcHandler handler;
    private readonly RpcConnectionOptions options;
    private readonly RpcWire wire;
    private readonly object sync = new();
    private readonly SemaphoreSlim writes = new(1), calls = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationTokenSource handlerLifetime;
    private readonly Dictionary<RpcId, TaskCompletionSource<ReadOnlyMemory<byte>>> pending = [];
    private readonly Dictionary<RpcId, CancellationTokenSource> requests = [];
    private readonly HashSet<Task> handlers = [];
    private readonly TaskCompletionSource outgoingDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RpcTiming timing;
    private static readonly AsyncLocal<CancellationToken> requestCancellation = new();
    internal static CancellationToken RequestCancellation => requestCancellation.Value;
    private long sequence;
    private int outgoingCount;
    private Task? run, disposal, stopping;
    private Task orderedDispatch = Task.CompletedTask;
    private Exception? failure;
    private bool terminal, disposed;

    public RpcConnection(Stream input, Stream output, IRpcHandler handler, RpcConnectionOptions? options = null)
    {
        this.input = input; this.output = output; this.handler = handler; this.options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.options.MaximumPayloadLength);
        wire = new(input, output, this.options.UseMessagePack, this.options.MaximumPayloadLength);
        timing = new(this.options.CollectTiming);
        handlerLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    }
    public Task RunAsync(CancellationToken cancellation = default)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (run is not null) throw new InvalidOperationException("RPC connection has already been started");
            return run = Task.Run(() => RunCoreAsync(cancellation));
        }
    }
    private async Task RunCoreAsync(CancellationToken cancellation)
    {
        using var registration = cancellation.Register(Stop);
        try
        {
            while (await ReadAsync(lifetime.Token).ConfigureAwait(false) is { } message)
            {
                if (message.IsResponse)
                {
                    if (options.UseMessagePack) throw new InvalidDataException("ipc: unexpected response message in sync connection");
                    TaskCompletionSource<ReadOnlyMemory<byte>>? completion;
                    lock (sync) pending.Remove(message.Id!.Value, out completion);
                    if (message.Error is { } error) completion?.TrySetException(RemoteError(error));
                    else completion?.TrySetResult(message.Data);
                }
                else if ((message.Id is null || options.ValidateCancellation is not null) && message.Method == "$/cancelRequest"u8)
                {
                    try
                    {
                        using var document = Parse(message.Data);
                        var parameters = document?.RootElement ?? default;
                        options.ValidateCancellation?.Invoke(parameters);
                        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("id"u8, out var value) && RpcId.Read(value) is { } id)
                        {
                            // CancelAsync schedules callbacks outside the dictionary lock. Completion is observed by shutdown.
                            lock (sync) if (requests.TryGetValue(id, out var request)) TrackLocked(request.CancelAsync());
                        }
                    }
                    catch (Exception error) when (error is RpcException or JsonException or InvalidDataException) { }
                }
                else if (options.UseMessagePack) await DispatchAsync(message, handlerLifetime.Token).ConfigureAwait(false);
                else StartDispatch(message);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
        finally
        {
            ClosePending();
            await ObserveCancellationAsync(handlerLifetime.CancelAsync()).ConfigureAwait(false);
            Task[] work;
            lock (sync) work = handlers.ToArray();
            try { await Task.WhenAll(work).ConfigureAwait(false); }
            catch (Exception error) { lock (sync) failure ??= error; }
            Stop();
            await ObserveCancellationAsync(stopping!).ConfigureAwait(false);
        }
        if (failure is { } cause) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw();
        cancellation.ThrowIfCancellationRequested();
    }
    private void StartDispatch(RpcMessage message)
    {
        lock (sync)
        {
            if (terminal) return;
            var source = CancellationTokenSource.CreateLinkedTokenSource(handlerLifetime.Token);
            if (message.Id is { } id) requests[id] = source;
            var predecessor = options.PreserveMessageOrder ? orderedDispatch : Task.CompletedTask;
            var dispatch = Task.Run(async () =>
            {
                try
                {
                    await predecessor.ConfigureAwait(false);
                    await DispatchAsync(message, source.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception error) { Fail(error); }
                finally
                {
                    lock (sync)
                    {
                        if (message.Id is { } id && requests.TryGetValue(id, out var current) && ReferenceEquals(current, source)) requests.Remove(id);
                        source.Dispose();
                    }
                }
            });
            if (options.PreserveMessageOrder) orderedDispatch = dispatch;
            TrackLocked(dispatch);
        }
    }
    private void TrackLocked(Task task)
    {
        handlers.Add(task);
        _ = task.ContinueWith(completed =>
        {
            if (completed.Exception is { } error) Fail(error);
            lock (sync) handlers.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private async ValueTask DispatchAsync(RpcMessage message, CancellationToken cancellation)
    {
        var previous = requestCancellation.Value; requestCancellation.Value = cancellation;
        try
        {
            if (message.Id is null)
            {
                try
                {
                    using var document = Parse(message.Data);
                    await handler.HandleNotificationAsync(message.Method, document?.RootElement ?? default, cancellation).ConfigureAwait(false);
                }
                catch (Exception) { /* Notification errors have no response, matching the API connection contract. */ }
                return;
            }
            RpcResponse response = RpcResponse.Null;
            RpcException? error = null;
            Exception? handlerFault = null;
            if (message.Method == "getServerTiming"u8) response = timing.Snapshot();
            else if (message.Method == "resetServerTiming"u8) timing.Reset();
            else
            {
                long start = Stopwatch.GetTimestamp();
                bool record = true;
                try
                {
                    response = await handler.HandleRequestAsync(message.Method, message.Data, cancellation).ConfigureAwait(false);
                }
                catch (Exception cause)
                {
                    record = cause is RpcException or OperationCanceledException;
                    if (!record) handlerFault = cause;
                    error = cause as RpcException ?? new(-32603, Utf8String.FromString(cause.Message));
                }
                if (record) timing.Record(message.Method, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            await WriteAsync(message.Id, default, response, error, lifetime.Token, handlerFault).ConfigureAwait(false);
        }
        finally { requestCancellation.Value = previous; }
    }
    private static JsonDocument? Parse(ReadOnlyMemory<byte> bytes) => bytes.IsEmpty ? null : JsonDocument.Parse(bytes, new() { MaxDepth = int.MaxValue });
    private static IOException RemoteError(RpcException error) => new($"ipc: remote error [{error.Code}]: {error.Message}", error);

    private async ValueTask<RpcMessage?> ReadAsync(CancellationToken cancellation)
    {
        var reading = wire.ReadAsync(cancellation);
        if (reading.IsCompleted) return await reading.ConfigureAwait(false);
        var task = reading.AsTask();
        try { return await task.WaitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Console input can ignore read cancellation. Session shutdown must finish so its owned
            // stream can be disposed; observe a delayed I/O failure without keeping the session alive.
            _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> CallAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation = default)
    {
        using var outgoing = EnterOutgoing();
        var callerCancellation = cancellation;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        cancellation = linked.Token;
        if (options.UseMessagePack)
        {
            await calls.WaitAsync(cancellation).ConfigureAwait(false);
            bool sent = false;
            try
            {
                CheckOpen();
                await WriteAsync(new(method), method, new(parameters), null, cancellation).ConfigureAwait(false);
                sent = true;
                var message = await ReadAsync(cancellation).ConfigureAwait(false);
                if (message is null) throw new EndOfStreamException("ipc: connection closed");
                if (!message.IsResponse || message.Id != new RpcId(method))
                    throw new InvalidDataException($"ipc: unexpected message while waiting for \"{method}\" response");
                sent = false;
                if (message.Error is { } error) throw RemoteError(error);
                return message.Data;
            }
            catch (Exception error) { if (sent) Fail(error); throw; }
            finally { calls.Release(); }
        }
        var id = new RpcId((Utf8String)"api"u8 + Interlocked.Increment(ref sequence));
        var completion = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync) { CheckOpen(); pending.Add(id, completion); }
        try
        {
            await WriteAsync(id, method, new(parameters), null, cancellation).ConfigureAwait(false);
            return await completion.Task.WaitAsync(callerCancellation).ConfigureAwait(false);
        }
        finally { lock (sync) pending.Remove(id); }
    }
    public async ValueTask NotifyAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation = default)
    {
        using var outgoing = EnterOutgoing();
        await WriteAsync(null, method, new(parameters), null, cancellation).ConfigureAwait(false);
    }
    internal async ValueTask CallWithoutResponseAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation)
    {
        if (options.UseMessagePack) throw new InvalidOperationException("Unobserved requests require JSON-RPC");
        using var outgoing = EnterOutgoing();
        var id = new RpcId((Utf8String)"api"u8 + Interlocked.Increment(ref sequence));
        await WriteAsync(id, method, new(parameters), null, cancellation).ConfigureAwait(false);
    }
    private async ValueTask WriteAsync(RpcId? id, Utf8String method, RpcResponse response, RpcException? error, CancellationToken cancellation, Exception? handlerFault = null)
    {
        await writes.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (!method.IsEmpty) CheckOpen();
            // A request cancellation must not interrupt a partially written frame.
            await wire.WriteAsync(id, method, response, error, lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception cause) when (cause is not OperationCanceledException)
        {
            if (handlerFault is not null && !lifetime.IsCancellationRequested)
            {
                var combined = new IOException($"ipc: failed to write error response: {cause.Message} (original handler failure: {handlerFault.Message})", cause);
                Fail(combined); throw combined;
            }
            if (!lifetime.IsCancellationRequested) Fail(cause);
            throw;
        }
        finally { writes.Release(); }
    }
    private void CheckOpen() { lock (sync) if (terminal || disposed) throw new IOException("ipc: connection closed", failure); }
    private Outgoing EnterOutgoing()
    {
        lock (sync) { CheckOpen(); outgoingCount++; return new(this); }
    }
    private sealed class Outgoing(RpcConnection connection) : IDisposable
    {
        public void Dispose()
        {
            lock (connection.sync) if (--connection.outgoingCount == 0 && connection.disposed) connection.outgoingDrained.TrySetResult();
        }
    }
    private void Fail(Exception error)
    {
        lock (sync) failure ??= error;
        ClosePending(); Stop();
    }
    private void ClosePending()
    {
        lock (sync)
        {
            terminal = true;
            foreach (var completion in pending.Values) completion.TrySetException(new IOException("ipc: connection closed", failure));
            pending.Clear();
        }
    }
    public void Stop()
    {
        // CancelAsync marks the token immediately and runs callbacks outside the caller's lock/stack.
        lock (sync) stopping ??= lifetime.CancelAsync();
    }
    private async Task ObserveCancellationAsync(Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception error) { lock (sync) failure ??= error; }
    }
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposal is not null) return new(disposal);
            disposed = true;
            if (outgoingCount == 0) outgoingDrained.TrySetResult();
            return new(disposal = Task.Run(async () =>
            {
                Stop(); await ObserveCancellationAsync(stopping!).ConfigureAwait(false); ClosePending();
                Exception? disposeFailure = null;
                if (!options.LeaveOpen)
                {
                    try { await input.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { disposeFailure = error; }
                    if (!ReferenceEquals(input, output))
                        try { await output.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { disposeFailure ??= error; }
                }
                if (run is not null) { try { await run.ConfigureAwait(false); } catch (Exception) { /* RunAsync owns the terminal error. */ } }
                await outgoingDrained.Task.ConfigureAwait(false);
                handlerLifetime.Dispose(); lifetime.Dispose(); writes.Dispose(); calls.Dispose();
                if (disposeFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposeFailure).Throw();
            }));
        }
    }
}

internal sealed class RpcTiming(bool enabled)
{
    private readonly object sync = new();
    private readonly Queue<(Utf8String Method, double Milliseconds, long Timestamp)> recent = new(5);
    private ulong count;
    private double total;
    internal void Record(Utf8String method, double milliseconds)
    {
        if (!enabled) return;
        lock (sync)
        {
            count++; total += Math.Max(milliseconds, 0);
            if (recent.Count == 5) recent.Dequeue();
            recent.Enqueue((method, Math.Max(milliseconds, 0), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
    }
    internal void Reset() { lock (sync) { count = 0; total = 0; recent.Clear(); } }
    internal RpcResponse Snapshot()
    {
        lock (sync) return RpcResponse.Json(writer =>
        {
            writer.WriteStartObject(); writer.WriteBoolean("enabled"u8, enabled);
            writer.WriteStartObject("totals"u8); writer.WriteNumber("requestCount"u8, count);
            writer.WriteNumber("totalProcessingTimeMs"u8, total); writer.WriteEndObject();
            writer.WriteStartArray("recentRequests"u8);
            foreach (var entry in recent)
            {
                writer.WriteStartObject(); writer.WritePropertyName("method"u8); JsonStrings.WriteString(writer, entry.Method.Span);
                writer.WriteNumber("processingTimeMs"u8, entry.Milliseconds); writer.WriteNumber("timestamp"u8, entry.Timestamp); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        });
    }
}

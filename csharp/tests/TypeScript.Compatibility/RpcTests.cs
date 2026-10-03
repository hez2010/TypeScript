using System.Buffers.Binary;
using System.Text.Json;
using System.Threading.Channels;
using TypeScript.Compiler.Api;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class RpcTests
{
    internal static async Task<int> SafetyAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        async Task<Exception> Error(Task task)
        {
            try { await task.WaitAsync(token); throw new InvalidOperationException("Expected a failure"); }
            catch (Exception e) when (e is not InvalidOperationException) { checks++; return e; }
        }
        static JsonElement Json(ReadOnlyMemory<byte> value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }

        foreach (int length in new[] { 0, 1, 255, 256, 65535, 65536 })
        {
            byte[] payload = new byte[length]; new Random(length).NextBytes(payload);
            byte[] frame = Tuple(1, "m😀"u8.ToArray(), payload);
            using var fragmented = new FragmentedStream(frame, 1);
            using var output = new MemoryStream();
            var wire = new RpcWire(fragmented, output, true, 100_000);
            var message = await wire.ReadAsync(token);
            Check(message?.Method == "m😀"u8 && message.Data.Span.SequenceEqual(payload), $"MessagePack {length} one-byte fragments");
            Check(await wire.ReadAsync(token) is null, "EOF between frames");
            await wire.WriteAsync(new("m😀"u8), default, new(payload, true), null, token);
            Check(output.ToArray().AsSpan().SequenceEqual(Tuple(4, "m😀"u8.ToArray(), payload)), $"MessagePack {length} exact output");
        }
        byte[] uintType = [0x93, 0xCC, 1, 0xC4, 1, (byte)'x', 0xC4, 0];
        using (var stream = new MemoryStream(uintType)) Check((await new RpcWire(stream, Stream.Null, true, 100).ReadAsync(token))?.Method == "x"u8, "uint8 message type");
        foreach (byte[] malformed in new byte[][] { [0x92], [0x93, 0], [0x93, 0xCD], [0x93, 1, 0xA0], [0x93, 1, 0xC6, 255, 255, 255, 255] })
        {
            using var stream = new MemoryStream(malformed);
            Check(await Error(new RpcWire(stream, Stream.Null, true, 1024).ReadAsync(token).AsTask()) is InvalidDataException, "Malformed MessagePack rejected");
        }
        var valid = Tuple(1, "method"u8.ToArray(), "{\"x\":true}"u8.ToArray());
        for (int length = 1; length < valid.Length; length++)
        {
            using var stream = new MemoryStream(valid[..length]);
            Check(await Error(new RpcWire(stream, Stream.Null, true, 1024).ReadAsync(token).AsTask()) is EndOfStreamException, "Every truncated MessagePack prefix rejected");
        }
        var jsonFrame = Frame("{\"jsonrpc\":\"2.0\",\"id\":\"日本語\",\"method\":\"echo\",\"params\":\"😀\"}"u8.ToArray());
        using (var stream = new FragmentedStream(jsonFrame, 1))
        {
            var message = await new RpcWire(stream, Stream.Null, false, 1024).ReadAsync(token);
            Check(message?.Id?.Text == "日本語"u8 && Json(message.Data).ValueEquals("😀"u8), "UTF-8 Content-Length and string ID");
        }
        foreach (var header in new[] { "Oops\r\n", "Content-Length: -1\r\n\r\n", "Content-Length: 99999999999999999999\r\n\r\n", "Content-Length: 0\r\n\r\n", "Content-Length: 1025\r\n\r\n", "Other: 2\r\n\r\n" })
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(header));
            Check(await Error(new RpcWire(stream, Stream.Null, false, 1024).ReadAsync(token).AsTask()) is InvalidDataException, "Invalid JSON-RPC header rejected");
        }

        // Original ipc timing_test.go: all accumulator, ordering, precision, and reset assertions.
        var timing = new RpcTiming(true); timing.Record("getSourceFile"u8, 2); timing.Record("getSymbolAtPosition"u8, 0.5);
        var snap = Json(timing.Snapshot().Data);
        Check(snap.GetProperty("enabled").GetBoolean() && snap.GetProperty("totals").GetProperty("requestCount").GetUInt64() == 2, "Timing count");
        Check(snap.GetProperty("totals").GetProperty("totalProcessingTimeMs").GetDouble() == 2.5, "Timing total");
        Check(snap.GetProperty("recentRequests")[0].GetProperty("method").GetString() == "getSourceFile"
            && snap.GetProperty("recentRequests")[0].GetProperty("processingTimeMs").GetDouble() == 2
            && snap.GetProperty("recentRequests")[1].GetProperty("method").GetString() == "getSymbolAtPosition"
            && snap.GetProperty("recentRequests")[1].GetProperty("processingTimeMs").GetDouble() == 0.5
            && snap.GetProperty("recentRequests").GetArrayLength() == 2, "Timing order and fractional duration");
        timing.Reset();
        foreach (var name in new[] { "a", "b", "c", "d", "e", "f", "g" }) timing.Record(Utf8String.FromString(name), 1);
        snap = Json(timing.Snapshot().Data);
        Check(snap.GetProperty("totals").GetProperty("requestCount").GetUInt64() == 7
            && snap.GetProperty("recentRequests").EnumerateArray().Select(x => x.GetProperty("method").GetString()).SequenceEqual(["c", "d", "e", "f", "g"]), "Timing ring");
        timing.Reset(); timing.Record("x"u8, -5000); snap = Json(timing.Snapshot().Data);
        Check(snap.GetProperty("totals").GetProperty("totalProcessingTimeMs").GetDouble() == 0, "Negative duration clamp");
        timing.Reset(); timing.Record("tiny"u8, 0.001234); snap = Json(timing.Snapshot().Data);
        Check(snap.GetProperty("recentRequests")[0].GetProperty("processingTimeMs").GetDouble() == 0.001234, "Sub-microsecond timing precision");
        timing.Reset(); snap = Json(timing.Snapshot().Data);
        Check(snap.GetProperty("totals").GetProperty("requestCount").GetUInt64() == 0 && snap.GetProperty("recentRequests").GetArrayLength() == 0, "Timing reset");
        timing.Record("c"u8, 2); Check(Json(timing.Snapshot().Data).GetProperty("recentRequests")[0].GetProperty("method").GetString() == "c", "Timing reusable after reset");
        snap = Json(new RpcTiming(false).Snapshot().Data);
        Check(!snap.GetProperty("enabled").GetBoolean() && snap.GetProperty("recentRequests").GetArrayLength() == 0
            && snap.GetProperty("totals").GetProperty("requestCount").GetUInt64() == 0, "Disabled timing");
        foreach (double value in new[] { 1.5, 0, -5000, 0.0005, 0.001234 })
        {
            timing.Reset(); timing.Record("duration"u8, value);
            Check(Json(timing.Snapshot().Data).GetProperty("totals").GetProperty("totalProcessingTimeMs").GetDouble() == Math.Max(0, value), "Original duration conversion case");
        }

        // An async filesystem callback can be outstanding while another client request finishes.
        {
            var (server, client) = DuplexStream.Pair(); await using var clientOwner = client;
            RpcConnection? connection = null;
            var handler = new Handler(async (method, _, cancel) => method == "callback"u8
                ? new(await connection!.CallAsync("readFile"u8, "\"/a.ts\""u8.ToArray(), cancel)) : RpcResponse.String(method));
            await using var owner = connection = new(server, server, handler, new() { CollectTiming = true });
            Task run = connection.RunAsync(token); var peer = new RpcWire(client, client, false, 100_000);
            await peer.WriteAsync(new(default, 1), "callback"u8, RpcResponse.Null, null, token);
            var callback = await peer.ReadAsync(token); Check(callback?.Method == "readFile"u8, "Callback request sent");
            await peer.WriteAsync(new("two"u8), "nested"u8, RpcResponse.Null, null, token);
            var nested = await peer.ReadAsync(token); Check(nested?.Id?.Text == "two"u8 && Json(nested.Data).ValueEquals("nested"u8), "Nested async request progresses during callback");
            await peer.WriteAsync(callback!.Id, default, RpcResponse.String("text"u8), null, token);
            var response = await peer.ReadAsync(token); Check(response?.Id?.Number == 1 && Json(response.Data).ValueEquals("text"u8), "Callback resumes original request");
            await peer.WriteAsync(new(default, 3), "getServerTiming"u8, RpcResponse.Null, null, token);
            response = await peer.ReadAsync(token); Check(Json(response!.Data).GetProperty("totals").GetProperty("requestCount").GetUInt64() == 2, "Timing excludes meta requests");
            client.CompleteWrites(); await run.WaitAsync(token);
            Check(await Error(connection.CallAsync("late"u8, default, token).AsTask()) is IOException, "Call after EOF fails immediately");
        }
        // Original async lifecycle cases: join handlers, cancel on EOF, unblock pending callbacks, preserve write failure.
        foreach (bool notification in new[] { false, true })
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (server, client) = DuplexStream.Pair(); await using var clientOwner = client;
            var handler = new Handler(async (_, _, _) => { started.SetResult(); await release.Task; return RpcResponse.Null; });
            await using var connection = new RpcConnection(server, server, handler);
            Task run = connection.RunAsync(token); var peer = new RpcWire(client, client, false, 10000);
            await peer.WriteAsync(notification ? null : new RpcId(default, 1), "wait"u8, RpcResponse.Null, null, token);
            await started.Task.WaitAsync(token); client.CompleteWrites();
            Check(!run.IsCompleted, "EOF joins active handlers"); release.SetResult(); await run.WaitAsync(token);
        }
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (server, client) = DuplexStream.Pair(); await using var clientOwner = client;
            var handler = new Handler(async (_, _, cancel) => { started.SetResult(); await Task.Delay(Timeout.Infinite, cancel); return RpcResponse.Null; });
            await using var connection = new RpcConnection(server, server, handler);
            Task run = connection.RunAsync(token); var peer = new RpcWire(client, client, false, 10000);
            await peer.WriteAsync(new(default, 1), "wait"u8, RpcResponse.Null, null, token);
            await started.Task.WaitAsync(token); client.CompleteWrites(); await run.WaitAsync(token); Check(true, "EOF cancels request context");
        }
        {
            var (server, client) = DuplexStream.Pair(); await using var clientOwner = client;
            await using var connection = new RpcConnection(server, server, new Handler());
            Task run = connection.RunAsync(token); Task<ReadOnlyMemory<byte>> call = connection.CallAsync("transform"u8, default, token).AsTask();
            await new RpcWire(client, client, false, 10000).ReadAsync(token); client.CompleteWrites();
            Check(await Error(call) is IOException, "Peer EOF unblocks a callback"); await run.WaitAsync(token);
        }
        foreach (bool binary in new[] { false, true }) foreach (bool panic in new[] { false, true })
        {
            var (server, client) = DuplexStream.Pair(); await using var clientOwner = client;
            var failure = new IOException("response write failed"); server.WriteFailure = failure;
            var handler = new Handler((_, _, _) => panic ? throw new ApplicationException("handler panic") : ValueTask.FromResult(RpcResponse.Null));
            await using var connection = new RpcConnection(server, server, handler, new() { UseMessagePack = binary });
            Task run = connection.RunAsync(token);
            if (binary) await client.WriteAsync(Tuple(1, "transform"u8.ToArray(), "null"u8.ToArray()), token);
            else await new RpcWire(client, client, false, 10000).WriteAsync(new(default, 1), "transform"u8, RpcResponse.Null, null, token);
            var reported = await Error(run);
            Check(ReferenceEquals(panic ? reported.InnerException : reported, failure), "Response write failure reaches RunAsync");
            if (panic) Check(reported.Message.Contains("handler panic"), "An error-response write failure retains the original handler fault");
            Check(ReferenceEquals((await Error(connection.CallAsync("late"u8, default, token).AsTask())).InnerException, reported), "Terminal callback error retains original write failure");
        }
        {
            var (_, output) = DuplexStream.Pair(); output.WriteFailure = new IOException("response write failed after EOF");
            using var input = new MemoryStream(Frame("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"request\"}"u8.ToArray()));
            await using var connection = new RpcConnection(input, output, new Handler());
            Check(ReferenceEquals(await Error(connection.RunAsync(token)), output.WriteFailure), "Response write failure is reported even when the reader already reached EOF");
        }
        {
            var (server, client) = DuplexStream.Pair(); await using var clientOwner = client;
            await using var connection = new RpcConnection(server, server, new Handler()); Task run = connection.RunAsync(token);
            await client.WriteAsync("oops\n"u8.ToArray(), token);
            Check(await Error(run) is InvalidDataException, "Read loop framing failure");
            Check(await Error(connection.NotifyAsync("late"u8, default, token).AsTask()) is IOException, "Notification after read failure fails immediately");
        }
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (server, client) = DuplexStream.Pair();
            var handler = new Handler(async (_, _, _) => { started.SetResult(); await release.Task; return RpcResponse.Null; });
            await using var connection = new RpcConnection(server, server, handler); Task run = connection.RunAsync(token);
            var peer = new RpcWire(client, client, false, 10000);
            await peer.WriteAsync(new(default, 1), "wait"u8, RpcResponse.Null, null, token); await started.Task.WaitAsync(token);
            await client.DisposeAsync(); Check(!run.IsCompleted, "Full peer close still joins the handler"); release.SetResult();
            var error = await Error(run); Check(error is IOException && error.Message.Contains("Peer closed"), "A joined handler's failed response is retained");
            Check(ReferenceEquals((await Error(connection.CallAsync("late"u8, default, token).AsTask())).InnerException, error), "Peer-close write failure remains the callback cause");
        }
        {
            // Cancel an outgoing call while its header write is in progress, then send another call.
            var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (server, client) = DuplexStream.Pair(); await using var clientOwner = client;
            int writes = 0;
            server.BeforeWrite = async cancel => { if (Interlocked.Increment(ref writes) == 1) { writing.SetResult(); await release.Task.WaitAsync(cancel); } };
            await using var connection = new RpcConnection(server, server, new Handler()); Task run = connection.RunAsync(token);
            using var canceled = new CancellationTokenSource();
            Task<ReadOnlyMemory<byte>> first = connection.CallAsync("first"u8, "{\"text\":\"😀\"}"u8.ToArray(), canceled.Token).AsTask();
            await writing.Task.WaitAsync(token); canceled.Cancel();
            Task<ReadOnlyMemory<byte>> second = connection.CallAsync("second"u8, "true"u8.ToArray(), token).AsTask(); release.SetResult();
            var peer = new RpcWire(client, client, false, 10000);
            var firstMessage = await peer.ReadAsync(token); var secondMessage = await peer.ReadAsync(token);
            Check(firstMessage?.Method == "first"u8 && secondMessage?.Method == "second"u8, "Canceled call leaves two complete, separate frames");
            Check(await Error(first) is OperationCanceledException, "Only the canceled callback fails");
            await peer.WriteAsync(firstMessage!.Id, default, RpcResponse.Null, null, token);
            await peer.WriteAsync(secondMessage!.Id, default, RpcResponse.Boolean(true), null, token);
            Check((await second.WaitAsync(token)).Span.SequenceEqual("true"u8), "Late canceled callback response cannot consume another call's response");
            client.CompleteWrites(); await run.WaitAsync(token);
        }
        {
            var (server, client) = DuplexStream.Pair(); await using var clientOwner = client;
            var connection = new RpcConnection(server, server, new Handler()); Task run = connection.RunAsync(token);
            var callback = connection.CallAsync("pending"u8, default, token).AsTask();
            await new RpcWire(client, client, false, 10000).ReadAsync(token);
            await connection.DisposeAsync().AsTask().WaitAsync(token);
            Check(callback.IsCompleted && run.IsCompleted, "Disposal joins outgoing calls and the reader");
            Check(await Error(callback) is IOException or OperationCanceledException, "Disposal releases callback waiters");
            await connection.DisposeAsync(); connection.Stop(); Check(true, "Repeated disposal and stop are safe");
        }
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (server, client) = DuplexStream.Pair(); await using var clientOwner = client;
            var handler = new Handler(async (_, _, cancel) =>
            {
                using var registration = cancel.Register(() => throw new ApplicationException("cancellation callback failed"));
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, cancel); return RpcResponse.Null; }
                finally { finished.SetResult(); }
            });
            await using var connection = new RpcConnection(server, server, handler); Task run = connection.RunAsync(token);
            await new RpcWire(client, client, false, 10000).WriteAsync(new(default, 1), "wait"u8, RpcResponse.Null, null, token);
            await started.Task.WaitAsync(token); connection.Stop();
            var error = await Error(run);
            Check(error.ToString().Contains("cancellation callback failed") && finished.Task.IsCompleted, "Cancellation callback faults do not skip joining active handlers");
        }
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (server, client) = DuplexStream.Pair(); await using var clientOwner = client;
            var handler = new Handler(async (method, _, cancel) =>
            { if (method == "wait"u8) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancel); } return RpcResponse.String("ok"u8); });
            await using var connection = new RpcConnection(server, server, handler); Task run = connection.RunAsync(token);
            var peer = new RpcWire(client, client, false, 10000);
            await peer.WriteAsync(new("cancel"u8), "wait"u8, RpcResponse.Null, null, token); await started.Task.WaitAsync(token);
            await peer.WriteAsync(null, "$/cancelRequest"u8, new("{\"id\":\"cancel\"}"u8.ToArray()), null, token);
            Check((await peer.ReadAsync(token))?.Error is not null, "Cancellation produces one request error");
            await peer.WriteAsync(new(default, 2), "ping"u8, RpcResponse.Null, null, token);
            Check((await peer.ReadAsync(token))?.Error is null, "Connection survives one request cancellation");
            client.CompleteWrites(); await run.WaitAsync(token);
        }
        checks += await CallbackSafetyAsync(token);
        foreach (int scenario in Enumerable.Range(0, 4))
        {
            using var canceled = new CancellationTokenSource();
            if (scenario == 1) canceled.Cancel();
            using var input = new FailingReadStream(scenario == 3 ? canceled.Cancel : null,
                scenario == 2 ? new OperationCanceledException() : scenario == 3 ? new IOException("server failed") : null);
            var fs = new MemoryFileSystem([], true, "/"u8);
            Task server = ApiServer.RunAsync(fs, input, Stream.Null, new() { CurrentDirectory = "/"u8, Async = true, LeaveOpen = true }, canceled.Token);
            if (scenario == 2) Check(await Error(server) is OperationCanceledException, "Unrelated server cancellation remains an error");
            else { await server.WaitAsync(token); Check(true, "API server handles EOF and caller cancellation"); }
        }
        return checks;
    }
    private static async Task<int> CallbackSafetyAsync(CancellationToken cancellation)
    {
        int count = 0;
        void Check(bool value, string message) { count++; if (!value) throw new InvalidOperationException(message); }
        var files = new MemoryFileSystem([KeyValuePair.Create((Utf8String)"/a.ts"u8, "disk"u8.ToArray())], true, "/"u8);
        ReadOnlyMemory<byte> reply = "null"u8.ToArray(); Utf8String called = default; ReadOnlyMemory<byte> parameters = default;
        var fs = new CallbackFileSystem(files, ["readFile"u8, "fileExists"u8, "directoryExists"u8, "getAccessibleEntries"u8, "realpath"u8, "writeFile"u8],
            (method, input, _) => { called = method; parameters = input; return ValueTask.FromResult(reply); });
        Check(fs.ReadFile("/a.ts"u8)!.AsSpan().SequenceEqual("disk"u8), "Null read callback falls back to disk");
        reply = "{\"content\":null}"u8.ToArray(); Check(fs.ReadFile("/a.ts"u8) is null, "Explicit missing read does not fall back");
        reply = "{\"content\":\"😀\\ud800\"}"u8.ToArray(); Check(new Utf8String(fs.ReadFile("/a.ts"u8)!) == Utf8String.FromString("😀\ud800"), "Callback source preserves WTF-8");
        reply = "false"u8.ToArray(); Check(!fs.FileExists("/a.ts"u8) && !fs.DirectoryExists("/"u8), "Callback absence overrides disk");
        reply = default; Check(fs.FileExists("/a.ts"u8) && fs.DirectoryExists("/"u8), "Empty callback falls back");
        reply = "{\"files\":[\"b\",\"a\"],\"directories\":[\"z\"]}"u8.ToArray();
        Check(fs.GetAccessibleEntries("/"u8).Files.Select(x => x.ToString()).SequenceEqual(["b", "a"]), "Callback entries retain order");
        reply = "\"/real.ts\""u8.ToArray(); Check(fs.RealPath("/a.ts"u8) == "/real.ts"u8, "Callback realpath");
        reply = "null"u8.ToArray(); fs.WriteFile("/a.ts"u8, "virtual"u8);
        Check(called == "writeFile"u8 && files.ReadFile("/a.ts"u8)!.AsSpan().SequenceEqual("disk"u8), "Write callback owns the write even on null result");
        using (var payload = JsonDocument.Parse(parameters)) Check(payload.RootElement.GetProperty("data").ValueEquals("virtual"u8), "Write callback payload");
        fs.AppendFile("/a.ts"u8, "!"u8); Check(files.ReadFile("/a.ts"u8)!.AsSpan().SequenceEqual("disk!"u8), "Append delegates to host");
        Check(fs.Stat("/a.ts"u8)?.Length == 5, "Stat delegates to host");
        var now = DateTime.UtcNow; fs.SetTimes("/a.ts"u8, now, now); Check(fs.Stat("/a.ts"u8)?.LastWriteTimeUtc == now, "Timestamps delegate to host");
        fs.Remove("/a.ts"u8); Check(!files.FileExists("/a.ts"u8), "Removal delegates to host");
        // Sync callbacks are serialized even when compiler workers invoke the same operation concurrently.
        var (server, client) = DuplexStream.Pair(); await using var clientOwner = client;
        RpcConnection? connection = null;
        var handler = new Handler(async (_, _, cancel) =>
        {
            var work = Enumerable.Range(0, 12).Select(_ => connection!.CallAsync("readFile"u8, "null"u8.ToArray(), cancel).AsTask()).ToArray();
            await Task.WhenAll(work); return RpcResponse.Boolean(true);
        });
        await using var owner = connection = new(server, server, handler, new() { UseMessagePack = true });
        Task run = connection.RunAsync(cancellation);
        await client.WriteAsync(Tuple(1, "workers"u8.ToArray(), "null"u8.ToArray()), cancellation);
        for (int i = 0; i < 12; i++)
        {
            var frame = await ReadTupleAsync(client, cancellation); Check(frame.Type == 6 && new Utf8String(frame.Method) == "readFile"u8, "Serialized callback frame");
            await client.WriteAsync(Tuple(2, frame.Method, "null"u8.ToArray()), cancellation);
        }
        var final = await ReadTupleAsync(client, cancellation); Check(final.Type == 4 && final.Data.AsSpan().SequenceEqual("true"u8), "Sync workers resume after callbacks");
        client.CompleteWrites(); await run.WaitAsync(cancellation);
        return count;
    }

    private sealed class Handler(Func<Utf8String, JsonElement, CancellationToken, ValueTask<RpcResponse>>? invoke = null) : IRpcHandler
    {
        public async ValueTask<RpcResponse> HandleRequestAsync(Utf8String method, ReadOnlyMemory<byte> parameters, CancellationToken cancellation)
        {
            using var document = parameters.IsEmpty ? null : JsonDocument.Parse(parameters);
            return invoke is null ? RpcResponse.Null : await invoke(method, document?.RootElement ?? default, cancellation);
        }
        public async ValueTask HandleNotificationAsync(Utf8String method, JsonElement parameters, CancellationToken cancellation)
        { if (invoke is not null) await invoke(method, parameters, cancellation); }
    }
    private static byte[] Frame(byte[] payload) => System.Text.Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n").Concat(payload).ToArray();
    internal static byte[] Tuple(byte type, byte[] method, byte[] payload)
    {
        using var output = new MemoryStream(); output.WriteByte(0x93); output.WriteByte(type);
        Span<byte> header = stackalloc byte[5];
        foreach (byte[] value in new[] { method, payload })
        {
            int size;
            if (value.Length <= byte.MaxValue) { header[0] = 0xC4; header[1] = (byte)value.Length; size = 2; }
            else if (value.Length <= ushort.MaxValue) { header[0] = 0xC5; BinaryPrimitives.WriteUInt16BigEndian(header[1..], (ushort)value.Length); size = 3; }
            else { header[0] = 0xC6; BinaryPrimitives.WriteUInt32BigEndian(header[1..], (uint)value.Length); size = 5; }
            output.Write(header[..size]); output.Write(value);
        }
        return output.ToArray();
    }
    private static async Task<(byte Type, byte[] Method, byte[] Data)> ReadTupleAsync(Stream input, CancellationToken cancellation)
    {
        byte[] prefix = new byte[2]; await input.ReadExactlyAsync(prefix, cancellation);
        if (prefix[0] != 0x93) throw new InvalidDataException("Invalid test tuple");
        async Task<byte[]> ReadBin()
        {
            byte[] header = new byte[1]; await input.ReadExactlyAsync(header, cancellation);
            int width = header[0] switch { 0xC4 => 1, 0xC5 => 2, 0xC6 => 4, _ => throw new InvalidDataException() };
            byte[] size = new byte[width]; await input.ReadExactlyAsync(size, cancellation); int length = 0;
            foreach (byte part in size) length = checked(length * 256 + part);
            byte[] data = new byte[length]; await input.ReadExactlyAsync(data, cancellation); return data;
        }
        return (prefix[1], await ReadBin(), await ReadBin());
    }
    private sealed class FragmentedStream(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }
    private sealed class FailingReadStream(Action? beforeRead, Exception? error) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            beforeRead?.Invoke();
            return error is null ? base.ReadAsync(buffer, cancellationToken) : ValueTask.FromException<int>(error);
        }
    }
    internal sealed class DuplexStream(Channel<byte[]> incoming, Channel<byte[]> outgoing) : Stream
    {
        private ReadOnlyMemory<byte> current;
        internal Exception? WriteFailure;
        internal Func<CancellationToken, Task>? BeforeWrite;
        internal static (DuplexStream, DuplexStream) Pair()
        {
            var first = Channel.CreateUnbounded<byte[]>(); var second = Channel.CreateUnbounded<byte[]>();
            return (new(first, second), new(second, first));
        }
        internal void CompleteWrites() => outgoing.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            while (current.IsEmpty)
            {
                if (!await incoming.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (incoming.Reader.TryRead(out var bytes)) current = bytes;
            }
            int count = Math.Min(buffer.Length, current.Length); current[..count].CopyTo(buffer); current = current[count..]; return count;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (BeforeWrite is { } before) await before(cancellationToken);
            if (WriteFailure is { } error) throw error;
            if (!outgoing.Writer.TryWrite(buffer.ToArray())) throw new IOException("Peer closed the connection");
        }
        protected override void Dispose(bool disposing) { incoming.Writer.TryComplete(); outgoing.Writer.TryComplete(); base.Dispose(disposing); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

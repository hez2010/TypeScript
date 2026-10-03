using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compatibility;

internal static class TelemetryTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/private/project/a.ts"u8] = "export const secret = '😀';"u8.ToArray(),
            ["/private/project/b.d.ts"u8] = "declare const privateSymbol: number;"u8.ToArray(),
            ["/private/project/c.js"u8] = "export const x = 1;"u8.ToArray(),
        });
        await using var host = new ProjectSnapshotHost(fs);
        CompilerOptions options = new();
        options.SetRaw("noLib"u8, "true"u8); options.SetRaw("allowJs"u8, "true"u8); options.SetRaw("strict"u8, "false"u8);
        options.SetRaw("baseUrl"u8, "\"/private/project\""u8); options.SetRaw("target"u8, "\"esnext\""u8); options.SetRaw("jsx"u8, "\"react-jsx\""u8);
        await using var snapshot = await host.CreateAsync(new() { CreatePrograms = [new(["/private/project/a.ts"u8, "/private/project/b.d.ts"u8, "/private/project/c.js"u8], options)] });
        var project = snapshot.CreatedPrograms.Single();
        var payload = SessionTelemetry.ProjectInfo(project);
        string text = Encoding.UTF8.GetString(payload.Data.Span);
        using var document = JsonDocument.Parse(payload.Data);
        var root = document.RootElement;
        Check(root.GetProperty("eventName").GetString() == "languageServer.projectInfo" && root.GetProperty("telemetryPurpose").GetString() == "usage", "Project telemetry envelope");
        Check(!text.Contains("private", StringComparison.Ordinal) && !text.Contains("secret", StringComparison.Ordinal) && !text.Contains("baseUrl", StringComparison.Ordinal), "Telemetry excludes paths, contents, symbols, and non-whitelisted options");
        Check(root.GetProperty("properties").GetProperty("compilerOptions").GetString() == "{\"allowJs\":true,\"jsx\":\"react-jsx\",\"strict\":false,\"target\":\"ESNext\"}", "Boolean values, enum names, and lexical option order");
        var measurements = root.GetProperty("measurements");
        Check(measurements.GetProperty("tsFileSize").GetInt32() == "export const secret = '😀';"u8.Length, "File sizes use UTF-8 bytes");
        Check(measurements.GetProperty("dtsFileCount").GetInt32() == 1 && measurements.GetProperty("jsFileCount").GetInt32() == 1 && !measurements.TryGetProperty("tsxFileCount", out _), "Script kinds and omitted zero counters");

        DocumentDiagnostic diagnostic = new(new(new(1, 2), new(3, 4)), 2322, 1, "private message"u8, "ts"u8, [], []);
        Check(FlakyDiagnostics.Compare([diagnostic], [diagnostic with { Severity = 2, Source = "other"u8, Tags = [1] }]).Full.IsEmpty,
            "Diagnostic comparison uses code, range, and message, matching the reference");
        var diff = FlakyDiagnostics.Compare([diagnostic], [diagnostic with { Code = 2323 }]);
        Check(diff.Full == "Diagnostic 2323 (1:2-3:4): private message was present after emit but not before emit\nDiagnostic 2322 (1:2-3:4): private message was present before emit but not after emit\n"u8,
            "Flake logs list additions before removals");
        Check(diff.Sanitized == "Diagnostic Code(2323) was present after emit but not before emit\nDiagnostic Code(2322) was present before emit but not after emit\n"u8,
            "Flake telemetry includes codes only");
        Check(!FlakyDiagnostics.Compare([diagnostic], [diagnostic with { Message = "changed"u8 }]).Full.IsEmpty
            && !FlakyDiagnostics.Compare([diagnostic], [diagnostic with { Range = new(new(1, 3), new(3, 4)) }]).Full.IsEmpty, "Message and range changes are detected");

        var clock = new ManualTime();
        var delivered = Channel.CreateUnbounded<string>();
        int samples = 0;
        await using (var telemetry = new SessionTelemetry((data, _) => { delivered.Writer.TryWrite(Encoding.UTF8.GetString(data.Span)); return default; }, () =>
        {
            samples++; return ValueTask.FromResult<RpcResponse?>(SessionTelemetry.Performance(new Dictionary<string, double> { ["projectCount"] = 1, ["openFileCount"] = 0 }));
        }, clock, timeout.Token))
        {
            Check(clock.Timer!.Due == TimeSpan.FromMinutes(5) && clock.Timer.Period == TimeSpan.FromMinutes(5) && samples == 0, "Telemetry starts a five-minute timer without sampling immediately");
            telemetry.ProjectAdded(project); telemetry.ProjectAdded(project);
            Check(await delivered.Reader.ReadAsync(timeout.Token) == text, "Project payload is queued intact");
            clock.Timer.Fire();
            var sample = await delivered.Reader.ReadAsync(timeout.Token);
            Check(sample.Contains("languageServer.performanceStats", StringComparison.Ordinal) && !sample.Contains("openFileCount", StringComparison.Ordinal), "Periodic resource sample and omitted zero counters");
            telemetry.ProjectAdded(project);
            telemetry.RequestFailure("textDocument/hover"u8, "ExceptionType"u8);
            using var error = JsonDocument.Parse(await delivered.Reader.ReadAsync(timeout.Token));
            Check(error.RootElement.GetProperty("properties").GetProperty("requestMethod").GetString() == "textDocument.hover", "A project is sent once and request methods use telemetry notation");
            await telemetry.DisposeAsync(); await telemetry.DisposeAsync(); clock.Timer.Fire(); telemetry.ProjectAdded(project);
            Check(clock.Timer.Closed && samples == 1 && !delivered.Reader.TryRead(out _), "Shutdown joins the timer, drops events, and supports repeated disposal");
        }

        foreach (bool disposedTransport in new[] { false, true })
        {
            var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int attempts = 0;
            await using var telemetry = new SessionTelemetry((data, _) =>
            {
                if (++attempts == 1) { attempted.SetResult(); if (disposedTransport) throw new ObjectDisposedException("transport"); throw new IOException("disconnected transport"); }
                delivered.Writer.TryWrite(Encoding.UTF8.GetString(data.Span)); return default;
            }, () => ValueTask.FromResult<RpcResponse?>(null), new ManualTime(), timeout.Token);
            telemetry.ProjectAdded(project); await attempted.Task.WaitAsync(timeout.Token);
            // An error event is a queue barrier: the failed project reservation has been released by then.
            telemetry.RequestFailure("custom/projectInfo"u8, "barrier"u8); await delivered.Reader.ReadAsync(timeout.Token);
            telemetry.ProjectAdded(project);
            Check(await delivered.Reader.ReadAsync(timeout.Token) == text && attempts == 3, "A failed send does not mark the project as seen");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var telemetry = new SessionTelemetry(async (_, token) => { sending.TrySetResult(); await Task.Delay(Timeout.Infinite, token); },
                () => ValueTask.FromResult<RpcResponse?>(null), new ManualTime(), cancelled.Token);
            telemetry.ProjectAdded(project); await sending.Task.WaitAsync(timeout.Token); cancelled.Cancel();
            await telemetry.DisposeAsync().AsTask().WaitAsync(timeout.Token);
            Check(true, "Cancellation releases a blocked telemetry write");
        }
        return checks + await LiveAsync();
    }

    private static async Task<int> LiveAsync()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/p/a.ts"u8] = "export const a = 1;"u8.ToArray(),
            ["/p/tsconfig.json"u8] = "{\"compilerOptions\":{\"noLib\":true},\"files\":[\"a.ts\"]}"u8.ToArray() });
        foreach (bool enabled in new[] { false, true })
        {
            var clock = new ManualTime();
            var (server, client) = RpcTests.DuplexStream.Pair(); await using var clientOwner = client;
            var running = LanguageServer.RunAsync(fs, server, server, new() { CurrentDirectory = "/p"u8, TimeProvider = clock }, timeout.Token);
            var wire = new RpcWire(client, client, false, 10_000_000);
            int nextId = 0;
            var events = new List<JsonElement>();
            async Task<RpcMessage> ReadAsync()
            {
                var message = await wire.ReadAsync(timeout.Token) ?? throw new EndOfStreamException();
                if (message.Method == "telemetry/event"u8)
                { using var value = JsonDocument.Parse(message.Data); events.Add(value.RootElement.Clone()); }
                else if (!message.Method.IsEmpty && message.Id is not null)
                    await wire.WriteAsync(message.Id, default, RpcResponse.Null, null, timeout.Token);
                return message;
            }
            async Task RequestAsync(Utf8String method, RpcResponse parameters)
            {
                var id = new RpcId(default, ++nextId);
                await wire.WriteAsync(id, method, parameters, null, timeout.Token);
                for (;;) { var response = await ReadAsync(); if (response.Id == id && response.Method.IsEmpty) { Check(response.Error is null, "Live telemetry request succeeds"); return; } }
            }
            await RequestAsync("initialize"u8, RpcResponse.Json(writer =>
            {
                writer.WriteStartObject(); writer.WriteNull("processId"); writer.WriteString("rootUri", "file:///p");
                writer.WriteStartObject("capabilities"); writer.WriteEndObject(); writer.WriteStartObject("initializationOptions");
                writer.WriteBoolean("enableTelemetry", enabled); writer.WriteBoolean("disablePushDiagnostics", true); writer.WriteEndObject(); writer.WriteEndObject();
            }));
            await wire.WriteAsync(null, "initialized"u8, new("{}"u8.ToArray()), null, timeout.Token);
            await wire.WriteAsync(null, "textDocument/didOpen"u8,
                new("{\"textDocument\":{\"uri\":\"file:///p/a.ts\",\"languageId\":\"typescript\",\"version\":1,\"text\":\"export const a = 1;\"}}"u8.ToArray()), null, timeout.Token);
            await RequestAsync("custom/projectInfo"u8, new("{\"textDocument\":{\"uri\":\"file:///p/a.ts\"}}"u8.ToArray()));
            if (enabled)
            {
                while (events.Count == 0) await ReadAsync();
                clock.Timer!.Fire();
                while (!events.Any(value => value.GetProperty("eventName").GetString() == "languageServer.performanceStats")) await ReadAsync();
                var measurements = events.Last().GetProperty("measurements");
                Check(measurements.GetProperty("openFileCount").GetInt32() == 1 && measurements.GetProperty("projectCount").GetInt32() == 1
                    && measurements.GetProperty("configCount").GetInt32() == 1, "The live periodic collector reports current project counters");
                Check(measurements.GetProperty("clrAllocatedBytes").GetDouble() > 0 && !measurements.TryGetProperty("goGCPercent", out _)
                    && !measurements.TryGetProperty("memoryUsedBytes", out _), "CLR measurements are labelled explicitly and Go-specific counters are absent");
            }
            else Check(clock.Timer is null && events.Count == 0, "Disabled telemetry creates no timer and sends no project events");
            await RequestAsync("shutdown"u8, default);
            await wire.WriteAsync(null, "exit"u8, default, null, timeout.Token); await running.WaitAsync(timeout.Token);
            Check(clock.Timer is null || clock.Timer.Closed, "Live shutdown disposes the telemetry timer");
        }
        return checks;
    }

    private sealed class ManualTime : TimeProvider
    {
        internal ManualTimer? Timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => Timer = new(callback, state, dueTime, period);
        internal sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
        {
            internal TimeSpan Due => due;
            internal TimeSpan Period => period;
            internal bool Closed;
            internal void Fire() { if (!Closed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Closed;
            public void Dispose() => Closed = true;
            public ValueTask DisposeAsync() { Dispose(); return default; }
        }
    }
}

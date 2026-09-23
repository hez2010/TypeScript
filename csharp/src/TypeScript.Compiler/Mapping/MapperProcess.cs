using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;

namespace TypeScript.Compiler.Mapping;

/// <summary>A parent-driven JSON-RPC connection with byte-counted LSP framing and independent request cancellation.</summary>
internal sealed class MapperProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Stream input, output;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly Task reader, errors;
    private long nextId;
    private int disposed;
    internal string PositionEncoding { get; private set; } = "";
    internal string DiagnosticSource { get; private set; } = "";
    internal bool IsAlive => Volatile.Read(ref disposed) == 0 && !lifetime.IsCancellationRequested && !process.HasExited;

    private MapperProcess(Process process, Action<string>? log)
    {
        this.process = process;
        input = process.StandardInput.BaseStream;
        output = new BufferedStream(process.StandardOutput.BaseStream);
        reader = ReadMessages();
        errors = ReadErrors(log);
    }

    internal static async Task<MapperProcess> Start(
        ContentMapper mapper,
        string locale,
        Action<string>? log,
        CancellationToken cancellation)
    {
        if (mapper.Exec.Length == 0)
            throw new MapperException(MapperFailure.Initialize, "Mapper declares no executable");
        var start = new ProcessStartInfo(mapper.Exec[0])
        {
            WorkingDirectory = mapper.PackageDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in mapper.Exec.Skip(1))
            start.ArgumentList.Add(argument);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new IOException("Mapper process did not start");
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new MapperException(MapperFailure.Initialize, "Mapper process could not start", e);
        }
        var connection = new MapperProcess(process, log);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await connection.Call("initialize", writer =>
            {
                if (locale.Length != 0)
                    writer.WriteString("locale", locale);
                writer.WriteStartArray("positionEncodings");
                writer.WriteStringValue("utf-8");
                writer.WriteStringValue("utf-16");
                writer.WriteEndArray();
            }, timeout.Token).ConfigureAwait(false);
            string encoding = result.GetProperty("positionEncoding").GetString() ?? "";
            string source = result.GetProperty("diagnosticSource").GetString() ?? "";
            if (encoding is not ("utf-8" or "utf-16"))
                throw new InvalidDataException("Unsupported mapper position encoding: " + encoding);
            if (string.IsNullOrWhiteSpace(source))
                throw new InvalidDataException("Mapper diagnostic source must not be empty");
            string[] reserved =
                [
                    "typescript",
                    "tsc",
                    "ts",
                    "tsx",
                    "d.ts",
                    "mts",
                    "cts",
                    "d.mts",
                    "d.cts",
                    "js",
                    "jsx",
                    "mjs",
                    "cjs",
                    "json"
                ];
            if (reserved.Contains(source, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Reserved mapper diagnostic source: " + source);
            connection.PositionEncoding = encoding;
            connection.DiagnosticSource = source;
            return connection;
        }
        catch (Exception e)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException(cancellation);
            throw new MapperException(MapperFailure.Initialize, "Mapper initialize failed", e);
        }
    }

    internal async ValueTask<JsonElement> Call(string method, Action<Utf8JsonWriter> parameters, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellation.ThrowIfCancellationRequested();
        long id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion))
            throw new InvalidOperationException("Duplicate mapper request identity");
        using var registration = cancellation.Register(() =>
        {
            if (pending.TryRemove(id, out var waiting))
                waiting.TrySetCanceled(cancellation);
        });
        try
        {
            using var payload = new MemoryStream();
            using (var writer = new Utf8JsonWriter(payload))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteNumber("id", id);
                writer.WriteString("method", method);
                writer.WriteStartObject("params");
                parameters(writer);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            await Write(payload.ToArray(), cancellation).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    private async ValueTask Write(byte[] payload, CancellationToken cancellation)
    {
        await writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            // Once a frame begins, only connection disposal may interrupt it. A canceled request cannot corrupt the next frame.
            byte[] header = Encoding.ASCII.GetBytes(
                "Content-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
            await input.WriteAsync(header, lifetime.Token).ConfigureAwait(false);
            await input.WriteAsync(payload, lifetime.Token).ConfigureAwait(false);
            await input.FlushAsync(lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task ReadMessages()
    {
        Exception failure = new EndOfStreamException("Mapper process closed the connection");
        try
        {
            byte[] single = new byte[1];
            while (true)
            {
                int length = 0;
                while (true)
                {
                    using var line = new MemoryStream();
                    while (true)
                    {
                        await output.ReadExactlyAsync(single, lifetime.Token).ConfigureAwait(false);
                        line.WriteByte(single[0]);
                        if (single[0] == (byte)'\n')
                            break;
                    }
                    string text = Encoding.ASCII.GetString(line.GetBuffer(), 0, (int)line.Length);
                    if (text == "\r\n")
                        break;
                    int colon = text.IndexOf(':');
                    if (colon < 0)
                        throw new InvalidDataException("Malformed mapper frame header");
                    if (text[..colon] == "Content-Length"
                        && (!int.TryParse(text.AsSpan(colon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out length)
                            || length < 0))
                        throw new InvalidDataException("Invalid mapper content length");
                }
                if (length <= 0)
                    throw new InvalidDataException("Missing mapper content length");
                byte[] payload = GC.AllocateUninitializedArray<byte>(length);
                await output.ReadExactlyAsync(payload, lifetime.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = int.MaxValue });
                JsonElement message = document.RootElement;
                if (message.TryGetProperty("method", out var method))
                {
                    if (message.TryGetProperty("id", out var requestId))
                    {
                        using var response = new MemoryStream();
                        using (var writer = new Utf8JsonWriter(response))
                        {
                            writer.WriteStartObject();
                            writer.WriteString("jsonrpc", "2.0");
                            writer.WritePropertyName("id");
                            requestId.WriteTo(writer);
                            writer.WriteStartObject("error");
                            writer.WriteNumber("code", -32601);
                            writer.WriteString("message", "Unexpected content mapper request: " + method.GetString());
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                        }
                        await Write(response.ToArray(), lifetime.Token).ConfigureAwait(false);
                    }
                    continue;
                }
                if (!message.TryGetProperty("id", out var responseId) || !responseId.TryGetInt64(out long id))
                    throw new InvalidDataException("Mapper response has no numeric request identity");
                if (!pending.TryRemove(id, out var completion))
                    continue;
                if (message.TryGetProperty("error", out var error))
                    completion.TrySetException(new IOException("Mapper request failed: " + error.GetRawText()));
                else if (message.TryGetProperty("result", out var result))
                    completion.TrySetResult(result.Clone());
                else
                    completion.TrySetException(new InvalidDataException("Mapper response has neither result nor error"));
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or InvalidOperationException
            or OperationCanceledException or ObjectDisposedException)
        {
            failure = e;
        }
        finally
        {
            lifetime.Cancel();
            foreach (var entry in pending)
                if (pending.TryRemove(entry.Key, out var completion))
                    completion.TrySetException(failure);
        }
    }

    private async Task ReadErrors(Action<string>? log)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line)
                log?.Invoke(line);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        lifetime.Cancel();
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        input.Dispose();
        output.Dispose();
        await Task.WhenAll(reader, errors).ConfigureAwait(false);
        process.Dispose();
        lifetime.Dispose();
    }
}

public enum MapperFailure
{
    Initialize,
    Project,
    Request,
    Response,
    Mappings
}
public sealed class MapperException(MapperFailure stage, string message, Exception? inner = null) : IOException(message, inner)
{
    public MapperFailure Stage { get; } = stage;
}

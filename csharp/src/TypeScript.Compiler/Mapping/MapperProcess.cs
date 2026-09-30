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
    internal Utf8String DiagnosticSource { get; private set; } = Utf8String.Empty;
    internal bool IsAlive => Volatile.Read(ref disposed) == 0 && !lifetime.IsCancellationRequested && !process.HasExited;

    private MapperProcess(Process process, Action<Utf8String>? log)
    {
        this.process = process;
        input = process.StandardInput.BaseStream;
        output = new BufferedStream(process.StandardOutput.BaseStream);
        reader = ReadMessages();
        errors = ReadErrors(log);
    }

    internal static async Task<MapperProcess> Start(
        ContentMapper mapper,
        Utf8String locale,
        Action<Utf8String>? log,
        CancellationToken cancellation)
    {
        if (mapper.Exec.Length == 0)
            throw new MapperException(MapperFailure.Initialize, Utf8Literals.MapperDeclaresNoExecutable);
        var start = new ProcessStartInfo(mapper.Exec[0].ToString())
        {
            WorkingDirectory = mapper.PackageDirectory.ToString(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (Utf8String argument in mapper.Exec.Skip(1))
            start.ArgumentList.Add(argument.ToString());
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new IOException("Mapper process did not start");
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new MapperException(MapperFailure.Initialize, Utf8Literals.MapperProcessCouldNotStart, e);
        }
        var connection = new MapperProcess(process, log);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await connection.Call(Utf8Literals.Initialize, writer =>
            {
                if (locale.Length != 0)
                    writer.WriteString("locale"u8, locale);
                writer.WriteStartArray("positionEncodings"u8);
                writer.WriteStringValue("utf-8"u8);
                writer.WriteEndArray();
            }, timeout.Token).ConfigureAwait(false);
            Utf8String encoding = JsonStrings.GetString(result.GetProperty("positionEncoding"u8));
            Utf8String source = JsonStrings.GetString(result.GetProperty("diagnosticSource"u8));
            if (encoding != "utf-8"u8)
                throw new InvalidDataException($"Unsupported mapper position encoding: {encoding}");
            if (Utf8String.IsNullOrWhiteSpace(source))
                throw new InvalidDataException("Mapper diagnostic source must not be empty");
            Utf8String[] reserved =
                [
                    Utf8Literals.Typescript,
                    Utf8Literals.Tsc,
                    Utf8Literals.TsFormat,
                    Utf8Literals.TsxFormat,
                    Utf8Literals.DTsSuffix,
                    Utf8Literals.MtsFormat,
                    Utf8Literals.CtsFormat,
                    Utf8Literals.DMtsSuffix,
                    Utf8Literals.DCtsSuffix,
                    Utf8Literals.JsFormat,
                    Utf8Literals.JsxKeyword,
                    Utf8Literals.MjsFormat,
                    Utf8Literals.CjsFormat,
                    Utf8Literals.JsonFormat
                ];
            if (reserved.Contains(source, Utf8StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Reserved mapper diagnostic source: {source}");
            connection.DiagnosticSource = source;
            return connection;
        }
        catch (Exception e)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException(cancellation);
            throw new MapperException(MapperFailure.Initialize, Utf8Literals.MapperInitializeFailed, e);
        }
    }

    internal async ValueTask<JsonElement> Call(Utf8String method, Action<Utf8JsonWriter> parameters, CancellationToken cancellation)
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
                writer.WriteString("jsonrpc"u8, "2.0"u8);
                writer.WriteNumber("id"u8, id);
                writer.WriteString("method"u8, method);
                writer.WriteStartObject("params"u8);
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
            Utf8String header = Utf8String.Concat("Content-Length: "u8, Utf8String.Format(payload.Length), "\r\n\r\n"u8);
            await input.WriteAsync(header.Memory, lifetime.Token).ConfigureAwait(false);
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
                    Utf8String text = new(line.GetBuffer().AsMemory(0, (int)line.Length));
                    if (text == Utf8Literals.CrLf)
                        break;
                    int colon = text.IndexOf((byte)':');
                    if (colon < 0)
                        throw new InvalidDataException("Malformed mapper frame header");
                    if (text[..colon] == Utf8Literals.ContentLength
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
                if (message.TryGetProperty("method"u8, out var method))
                {
                    if (message.TryGetProperty("id"u8, out var requestId))
                    {
                        using var response = new MemoryStream();
                        using (var writer = new Utf8JsonWriter(response))
                        {
                            writer.WriteStartObject();
                            writer.WriteString("jsonrpc"u8, "2.0"u8);
                            writer.WritePropertyName("id"u8);
                            requestId.WriteTo(writer);
                            writer.WriteStartObject("error"u8);
                            writer.WriteNumber("code"u8, -32601);
                            writer.WriteString("message"u8, Utf8Literals.UnexpectedContentMapperRequest + JsonStrings.GetString(method));
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                        }
                        await Write(response.ToArray(), lifetime.Token).ConfigureAwait(false);
                    }
                    continue;
                }
                if (!message.TryGetProperty("id"u8, out var responseId) || !responseId.TryGetInt64(out long id))
                    throw new InvalidDataException("Mapper response has no numeric request identity");
                if (!pending.TryRemove(id, out var completion))
                    continue;
                if (message.TryGetProperty("error"u8, out var error))
                    completion.TrySetException(new IOException($"Mapper request failed: {JsonStrings.Raw(error)}"));
                else if (message.TryGetProperty("result"u8, out var result))
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

    private async Task ReadErrors(Action<Utf8String>? log)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line)
                log?.Invoke(Utf8String.FromString(line));
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
public sealed class MapperException(MapperFailure stage, Utf8String message, Exception? inner = null) : IOException(message.ToString(), inner)
{
    public MapperFailure Stage { get; } = stage;
}

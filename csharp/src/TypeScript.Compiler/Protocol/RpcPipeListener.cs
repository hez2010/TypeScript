using System.IO.Pipes;
using System.Net.Sockets;

namespace TypeScript.Compiler.Protocol;

/// <summary>Binds an endpoint before announcing it and transfers one accepted stream to its caller.</summary>
internal sealed class RpcPipeListener : IDisposable
{
    private NamedPipeServerStream? pipe;
    private Socket? socket;
    private string? socketPath;

    internal RpcPipeListener(Utf8String path)
    {
        if (OperatingSystem.IsWindows())
        {
            var name = path.StartsWith("\\\\.\\pipe\\"u8) ? path[9..] : path;
            pipe = new(name.ToString(), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
        else
        {
            socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                // Never unlink another listener's endpoint when binding fails.
                socket.Bind(new UnixDomainSocketEndPoint(path.ToString())); socketPath = path.ToString(); socket.Listen(1);
            }
            catch { Dispose(); throw; }
        }
    }

    internal async ValueTask<Stream> AcceptAsync(CancellationToken cancellation)
    {
        if (pipe is { } pending)
        {
            await pending.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
            pipe = null; return pending;
        }
        return new NetworkStream(await socket!.AcceptAsync(cancellation).ConfigureAwait(false), ownsSocket: true);
    }

    public void Dispose()
    {
        pipe?.Dispose(); pipe = null; socket?.Dispose(); socket = null;
        if (socketPath is { } path) { socketPath = null; File.Delete(path); }
    }
}
